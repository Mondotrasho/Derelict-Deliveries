using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The officers aboard (N4). Sits on the Player next to PlayerShipState.
///
/// The roster mirrors itself into the player's event state after every change,
/// so events use the existing conditions with no new condition types:
///   tag     officer:&lt;id&gt;       - this officer is aboard
///   counter bonus:&lt;stat&gt;       - total of that stat across everyone aboard
/// Event choices read those (Bonus Stat / Min Bonus), outcomes recruit or lose
/// officers through Recruit / Lose. A scanning bonus also widens the event
/// scan radius.
/// </summary>
[DisallowMultipleComponent]
public sealed class OfficerRoster : MonoBehaviour
{
    [SerializeField] private PlayerShipState player;
    [Tooltip("Officers aboard at the start of a run.")]
    [SerializeField] private List<OfficerDefinition> startingOfficers = new List<OfficerDefinition>();

    [Header("Scanning Bonus")]
    [Tooltip("Optional. Its Scan Radius Cap grows with the scanning bonus.")]
    [SerializeField] private EventDirector director;
    [SerializeField] private string scanningStat = "scanning";
    [Min(0)] [SerializeField] private int scanRadiusPerPoint = 1;

    public event Action RosterChanged;
    public IReadOnlyList<OfficerDefinition> Aboard => aboard;

    private readonly List<OfficerDefinition> aboard = new List<OfficerDefinition>();
    private readonly HashSet<string> mirroredStats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private int baseScanCap = -1;


    private void Awake()
    {
        if (player == null) player = GetComponent<PlayerShipState>();
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (director == null) director = FindFirstObjectByType<EventDirector>();
    }


    private void Start()
    {
        if (director != null) baseScanCap = director.ScanRadiusCap;
        foreach (OfficerDefinition o in startingOfficers)
        {
            if (o != null && !aboard.Contains(o)) aboard.Add(o);
        }
        Mirror();
    }


    public bool Has(OfficerDefinition officer) => officer != null && aboard.Contains(officer);


    public int GetBonus(string stat)
    {
        int total = 0;
        foreach (OfficerDefinition o in aboard) total += o.GetBonus(stat);
        return total;
    }


    /// <summary>Adds an officer (once). Returns false if they were already aboard.</summary>
    public bool Recruit(OfficerDefinition officer)
    {
        if (officer == null || aboard.Contains(officer)) return false;
        aboard.Add(officer);
        Mirror();
        return true;
    }


    public bool Lose(OfficerDefinition officer)
    {
        if (officer == null || !aboard.Remove(officer)) return false;
        Mirror();
        return true;
    }


    /// <summary>Loses the officer with the highest bonus in a stat (e.g. the engineer who tried). Null if nobody has it.</summary>
    public OfficerDefinition LoseBestAt(string stat)
    {
        OfficerDefinition best = null;
        int bestValue = 0;
        foreach (OfficerDefinition o in aboard)
        {
            int v = o.GetBonus(stat);
            if (v > bestValue) { best = o; bestValue = v; }
        }
        if (best != null) Lose(best);
        return best;
    }


    /// <summary>Writes officer tags and bonus counters into the player's state.</summary>
    private void Mirror()
    {
        IEventState state = player != null ? player.EventState : null;
        if (state != null)
        {
            // officer:<id> tags
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (OfficerDefinition o in aboard) wanted.Add(EventKeys.Officer(o.Id));
            foreach (string tag in new List<string>(state.Tags))
            {
                if (tag.StartsWith("officer:", StringComparison.OrdinalIgnoreCase) && !wanted.Contains(tag)) state.RemoveTag(tag);
            }
            foreach (string tag in wanted) state.AddTag(tag);

            // bonus:<stat> counters (stats no longer present go back to 0)
            var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (OfficerDefinition o in aboard)
            {
                foreach (OfficerDefinition.Bonus b in o.Bonuses)
                {
                    if (string.IsNullOrWhiteSpace(b.stat)) continue;
                    string stat = b.stat.Trim().ToLowerInvariant();
                    totals[stat] = (totals.TryGetValue(stat, out int t) ? t : 0) + b.amount;
                }
            }
            foreach (string stat in mirroredStats) if (!totals.ContainsKey(stat)) state.SetCounter(EventKeys.Bonus(stat), 0);
            foreach (var kv in totals) state.SetCounter(EventKeys.Bonus(kv.Key), kv.Value);
            mirroredStats.Clear();
            foreach (string stat in totals.Keys) mirroredStats.Add(stat);
        }

        if (director != null && baseScanCap >= 0)
        {
            director.ScanRadiusCap = baseScanCap + GetBonus(scanningStat) * scanRadiusPerPoint;
        }

        RosterChanged?.Invoke();
    }
}
