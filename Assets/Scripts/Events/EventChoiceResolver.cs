using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Rolls and applies event choices. The event UI calls this as each choice is
/// made, so a choice that returns to the event can change which choices show next.
/// Controllers still own the definition's On Resolve writes, site removal and
/// route resumption.
/// </summary>
public static class EventChoiceResolver
{
    /// <summary>Index of the rolled outcome, or -1 if the choice has none.</summary>
    public static int RollOutcome(EventChoice choice, System.Random rng)
    {
        if (choice == null || choice.outcomes == null || choice.outcomes.Count == 0) return -1;

        float total = TotalWeight(choice);
        if (total <= 0f) return 0;

        double roll = rng.NextDouble() * total;
        for (int i = 0; i < choice.outcomes.Count; i++)
        {
            ChoiceOutcome o = choice.outcomes[i];
            roll -= o != null ? Mathf.Max(0f, o.weight) : 0f;
            if (roll < 0.0) return i;
        }
        return choice.outcomes.Count - 1;
    }


    /// <summary>The first outcome's share of the total weight, 0-1.</summary>
    public static float FirstOutcomeChance(EventChoice choice)
    {
        if (choice == null || choice.outcomes == null || choice.outcomes.Count == 0) return 1f;
        float total = TotalWeight(choice);
        if (total <= 0f) return 1f;
        ChoiceOutcome first = choice.outcomes[0];
        return first != null ? Mathf.Max(0f, first.weight) / total : 0f;
    }


    /// <summary>
    /// Applies one outcome and returns a short summary of what changed,
    /// e.g. "SUPPLIES +5 (12)   HULL -10".
    /// </summary>
    public static string Apply(ChoiceOutcome outcome, EventSite site, EventContext context,
                               PlayerShipState player, AsteroidFieldPainter asteroidField)
    {
        if (outcome == null) return "";

        outcome.writes?.Apply(context);

        if (outcome.consumeAsteroid && asteroidField != null && site != null)
        {
            asteroidField.TryConsumeCell(site.Cell);
        }

        return ApplyResources(outcome.resources, context, player);
    }


    public static string ApplyResources(IReadOnlyList<ResourceDelta> deltas, EventContext context, PlayerShipState player)
    {
        if (deltas == null || deltas.Count == 0) return "";

        ShipResources resources = player != null ? player.Resources : null;
        StringBuilder summary = new StringBuilder();

        foreach (ResourceDelta d in deltas)
        {
            if (Mathf.Approximately(d.amount, 0f)) continue;
            string sign = d.amount > 0f ? "+" : "-";
            float size = Mathf.Abs(d.amount);

            switch (d.kind)
            {
                case ResourceKind.Fuel:
                    if (resources == null) continue;
                    if (d.amount > 0f) resources.AddFuel(size); else resources.ConsumeFuel(size);
                    Append(summary, $"FUEL {sign}{size:0.#}");
                    break;

                case ResourceKind.Hull:
                    if (resources == null) continue;
                    if (d.amount > 0f) resources.RepairHull(size); else resources.ApplyHullDamage(size);
                    Append(summary, $"HULL {sign}{size:0.#}");
                    break;

                case ResourceKind.Shields:
                    if (resources == null) continue;
                    if (d.amount > 0f) resources.AddShields(size); else resources.ApplyShieldDamage(size);
                    Append(summary, $"SHIELDS {sign}{size:0.#}");
                    break;

                case ResourceKind.Damage:
                    if (resources == null) continue;
                    float onShields = resources.ApplyShieldDamage(size);
                    float onHull = resources.ApplyHullDamage(size - onShields);
                    if (onShields > 0f) Append(summary, $"SHIELDS -{onShields:0.#}");
                    if (onHull > 0f) Append(summary, $"HULL -{onHull:0.#}");
                    if (onShields <= 0f && onHull <= 0f) Append(summary, "NO DAMAGE");
                    break;

                case ResourceKind.Crew:
                    if (resources == null) continue;
                    int crew = Mathf.RoundToInt(size);
                    if (d.amount > 0f) resources.AddCrew(crew); else resources.RemoveCrew(crew);
                    Append(summary, $"CREW {sign}{crew} ({resources.Crew})");
                    break;

                case ResourceKind.Supplies:
                    IEventState state = context.Player;
                    if (state == null) continue;
                    int now = Mathf.Max(0, state.GetCounter(EventKeys.Supplies) + Mathf.RoundToInt(d.amount));
                    state.SetCounter(EventKeys.Supplies, now);
                    Append(summary, $"SUPPLIES {sign}{Mathf.RoundToInt(size)} ({now})");
                    break;
            }
        }

        return summary.ToString();
    }


    private static float TotalWeight(EventChoice choice)
    {
        float total = 0f;
        foreach (ChoiceOutcome o in choice.outcomes)
        {
            if (o != null) total += Mathf.Max(0f, o.weight);
        }
        return total;
    }


    /// <summary>Applies a detection shift and returns its summary part ("" if none).</summary>
    public static string ApplyDetection(int turns, EnemySpawnController spawner)
    {
        if (turns == 0 || spawner == null) return "";
        spawner.AddDetectionTurns(turns);
        return turns > 0 ? $"HUNT DELAYED +{turns} TURNS" : $"HUNT SOONER {turns} TURNS";
    }


    public static string Join(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b ?? "";
        if (string.IsNullOrEmpty(b)) return a;
        return a + "   " + b;
    }


    private static void Append(StringBuilder sb, string part)
    {
        if (sb.Length > 0) sb.Append("   ");
        sb.Append(part);
    }
}
