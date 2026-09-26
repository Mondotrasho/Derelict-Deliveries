using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The event UI. Presents any triggered event - asteroid and derelict marks,
/// hazards (random events) and planet event options - in a BannerChoiceView:
/// banner, name, description and the definition's authored choices.
///
/// A choice rolls one of its weighted outcomes, which is applied straight away
/// (EventChoiceResolver) and shown as result text. Then, depending on the
/// choice: the dialogue UI opens, a follow-up event is shown in the same
/// window, the event ends, or the choices come back re-checked against the
/// new state. Definitions with no choices get placeholder Resolve / Leave.
///
/// Presentation plus choice outcomes only: the controllers still own movement
/// interruption, the definition's On Resolve writes, site removal and route
/// resumption.
/// </summary>
public sealed class EventPanel : MonoBehaviour, IEventPresenter
{
    private const string ResolveId = "__resolve";
    private const string LeaveId = "__leave";
    private const string ContinueId = "__continue";

    [Header("References")]
    [Tooltip("The window this panel drives. Empty = a BannerChoiceView on this object or its children.")]
    [SerializeField] private BannerChoiceView view;
    [Tooltip("Picks the banner. Empty = no banner (the banner area collapses).")]
    [SerializeField] private BannerLibrary banners;

    [Header("Outcome Targets (found automatically if empty)")]
    [SerializeField] private PlayerShipState player;
    [SerializeField] private AsteroidFieldPainter asteroidField;
    [Tooltip("Opened by choices that have a Dialogue. Assign it: the panel starts inactive, so it cannot always be found.")]
    [SerializeField] private DialoguePanelController dialoguePanel;
    [Tooltip("Used by outcomes with Start Combat With.")]
    [SerializeField] private EnemySpawnController enemySpawner;
    [SerializeField] private EnemyTurnController enemyTurnController;

    [Header("Header Text")]
    [SerializeField] private string titlePrefix = "SYS://";
    [SerializeField] private bool upperCaseTitle = true;
    [Tooltip("Show the planet's name as the status for events on a planet.")]
    [SerializeField] private bool planetNameAsStatus = true;
    [SerializeField] private string asteroidStatus = "SIGNAL";
    [SerializeField] private string derelictStatus = "WRECK SIGNAL";
    [SerializeField] private string planetStatus = "LOCAL SIGNAL";
    [SerializeField] private string hazardStatus = "WARNING";

    [Header("Button Labels")]
    [SerializeField] private string resolveLabel = "RESOLVE";
    [SerializeField] private string leaveLabel = "LEAVE FOR LATER";
    [SerializeField] private string continueLabel = "CONTINUE";

    public bool IsOpen => current != null;

    private readonly List<BannerChoiceView.Choice> buttons = new List<BannerChoiceView.Choice>();
    private readonly HashSet<string> usedChoices = new HashSet<string>();
    private readonly System.Random rng = new System.Random();
    private EventSite current;
    private bool cancelled;


    private void Awake()
    {
        if (view == null) view = GetComponentInChildren<BannerChoiceView>(true);
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (asteroidField == null) asteroidField = FindFirstObjectByType<AsteroidFieldPainter>();
        if (dialoguePanel == null) dialoguePanel = FindFirstObjectByType<DialoguePanelController>(FindObjectsInactive.Include);
        if (enemySpawner == null) enemySpawner = FindFirstObjectByType<EnemySpawnController>();
        if (enemyTurnController == null) enemyTurnController = FindFirstObjectByType<EnemyTurnController>();
        if (view == null) Debug.LogWarning("EventPanel has no BannerChoiceView; events will be left for later.", this);
    }


    public IEnumerator Present(EventSite site, EventContext context, Action<EventOutcome> done)
    {
        if (view == null || site == null || site.Definition == null)
        {
            done?.Invoke(EventOutcome.LeaveForLater);
            yield break;
        }

        current = site;
        cancelled = false;

        bool hazard = site.Category == EventCategory.Hazard;
        Planet planet = context.PlanetData != null ? context.PlanetData : site.Planet;
        EventDefinition shown = site.Definition;
        bool somethingHappened = false;   // an outcome was applied: leaving now still resolves the site
        bool resolved = false;
        EnemyShipDefinition combatAfter = null;

        ShowDefinition(shown, site, planet, hazard);

        while (true)
        {
            bool canLeave = !hazard && shown.AllowLeave;
            bool hasChoices = shown.Choices != null && shown.Choices.Count > 0;

            buttons.Clear();
            if (hasChoices) AddChoiceButtons(shown, context);
            else buttons.Add(new BannerChoiceView.Choice(ResolveId, resolveLabel));
            if (canLeave || buttons.Count == 0) buttons.Add(new BannerChoiceView.Choice(LeaveId, leaveLabel));
            view.SetChoices(buttons);

            string picked = null;
            yield return view.WaitForChoice(id => picked = id);
            if (cancelled || picked == null) break;

            if (picked == LeaveId)
            {
                resolved = somethingHappened;   // e.g. walked away from a capsule already cut out of the rock
                break;
            }

            if (picked == ResolveId)
            {
                resolved = true;
                break;
            }

            EventChoice choice = shown.FindChoice(picked);
            if (choice == null) continue;

            // Roll and apply one outcome now, so later choices see its writes.
            int index = EventChoiceResolver.RollOutcome(choice, rng);
            ChoiceOutcome outcome = index >= 0 ? choice.outcomes[index] : null;
            string summary = EventChoiceResolver.Apply(outcome, site, context, player, asteroidField);
            if (outcome != null) summary = EventChoiceResolver.Join(summary, EventChoiceResolver.ApplyDetection(outcome.detectionTurns, enemySpawner));
            if (outcome != null && !string.IsNullOrWhiteSpace(outcome.returnCrewFromSiteCounter) && context.Site != null)
            {
                int back = Mathf.Max(0, context.Site.GetCounter(outcome.returnCrewFromSiteCounter));
                context.Site.SetCounter(outcome.returnCrewFromSiteCounter, 0);
                if (back > 0 && player != null && player.Resources != null) player.Resources.AddCrew(back);
                summary = EventChoiceResolver.Join(summary, $"CREW +{back} BACK ABOARD ({(player != null && player.Resources != null ? player.Resources.Crew : 0)})");
            }
            somethingHappened = true;

            if (outcome != null && outcome.spawnOnMapEdge != null)
            {
                if (enemySpawner == null || !enemySpawner.TrySpawnAtMapEdge(outcome.spawnOnMapEdge, out _))
                {
                    Debug.LogWarning($"EventPanel: could not spawn {outcome.spawnOnMapEdge.DisplayName} at the map edge (no free entry cell?).", this);
                }
            }

            view.ShowResult(JoinResult(outcome != null ? outcome.resultText : "", summary));
            view.SetChoices(new[] { new BannerChoiceView.Choice(ContinueId, continueLabel) });
            yield return view.WaitForChoice(id => picked = id);
            if (cancelled || picked == null) break;

            if (choice.dialogue != null && dialoguePanel != null)
            {
                view.Hide();
                yield return dialoguePanel.OpenDialogueAndWait(choice.dialogue);
                if (cancelled) break;
                view.ShowAgain();
            }

            if (outcome != null && outcome.startCombatWith != null)
            {
                combatAfter = outcome.startCombatWith;
                resolved = true;
                break;
            }

            if (outcome != null && outcome.followUp != null)
            {
                shown = outcome.followUp;
                usedChoices.Clear();
                ShowDefinition(shown, site, planet, hazard);
                continue;
            }

            if (choice.endsEvent)
            {
                resolved = true;
                break;
            }

            if (choice.hideAfterUse) usedChoices.Add(choice.id);
            view.ShowResult(null);
        }

        view.Hide();
        current = null;
        usedChoices.Clear();

        done?.Invoke(resolved && !cancelled
            ? new EventOutcome { resolved = true, stopJourney = false }
            : EventOutcome.LeaveForLater);

        // Started here, straight after reporting, so the controller that ran this
        // event already sees the Combat phase (a planet visit then ends instead of
        // reopening its list).
        if (combatAfter != null && !cancelled) StartCombat(combatAfter);
    }


    private void StartCombat(EnemyShipDefinition definition)
    {
        if (enemySpawner == null || enemyTurnController == null || player == null)
        {
            Debug.LogWarning("EventPanel: Start Combat With needs EnemySpawnController, EnemyTurnController and PlayerShipState.", this);
            return;
        }

        Vector3Int centre = player.CurrentCell;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector3Int cell = new Vector3Int(centre.x + dx, centre.y + dy, centre.z);
                if (!enemySpawner.TrySpawnEnemyAtCell(cell, out EnemyShip enemy)) continue;

                enemy.SetDefinition(definition);
                if (!enemyTurnController.TryStartEncounter(enemy))
                {
                    Debug.LogWarning($"EventPanel: could not start combat with {definition.DisplayName}.", this);
                }
                return;
            }
        }

        Debug.LogWarning("EventPanel: no free cell (or enemy cap reached) to spawn the combat opponent.", this);
    }


    /// <summary>Close without resolving (e.g. combat started). Present then finishes with LeaveForLater.</summary>
    public void Cancel()
    {
        if (current == null) return;
        cancelled = true;
        if (view != null) view.Hide();
    }


    private void ShowDefinition(EventDefinition definition, EventSite site, Planet planet, bool hazard)
    {
        view.Show(
            banners != null ? banners.Resolve(definition, planet) : null,
            Title(definition),
            Status(definition, planet),
            definition.BodyText,
            hazard);
    }


    private void AddChoiceButtons(EventDefinition definition, EventContext context)
    {
        foreach (EventChoice choice in definition.Choices)
        {
            if (choice == null || string.IsNullOrEmpty(choice.id)) continue;
            if (usedChoices.Contains(choice.id)) continue;

            bool crewOk = choice.minCrew <= 0 || (player != null && player.Resources != null && player.Resources.Crew >= choice.minCrew);
            bool met = crewOk && (choice.availability == null || choice.availability.IsMet(context));
            if (!met && !choice.showWhenLocked) continue;

            string label = choice.text;
            if (met && choice.showOdds && choice.outcomes != null && choice.outcomes.Count > 1)
            {
                int percent = Mathf.RoundToInt(EventChoiceResolver.FirstOutcomeChance(choice) * 100f);
                label += $" ({percent}%)";
            }
            if (!met)
            {
                string reason = !crewOk && string.IsNullOrWhiteSpace(choice.lockedReason)
                    ? $"needs {choice.minCrew} crew"
                    : choice.lockedReason;
                if (!string.IsNullOrWhiteSpace(reason)) label += $" [{reason}]";
            }

            buttons.Add(new BannerChoiceView.Choice(choice.id, label, met));
        }
    }


    private static string JoinResult(string text, string summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return text;
        if (string.IsNullOrWhiteSpace(text)) return summary;
        return text + "\n" + summary;
    }


    private string Title(EventDefinition definition)
    {
        string name = definition.DisplayName;
        if (upperCaseTitle) name = name.ToUpperInvariant();
        return titlePrefix + name;
    }


    private string Status(EventDefinition definition, Planet planet)
    {
        if (planetNameAsStatus && planet != null && !string.IsNullOrWhiteSpace(planet.displayName))
        {
            return planet.displayName.ToUpperInvariant();
        }

        switch (definition.Category)
        {
            case EventCategory.Hazard: return hazardStatus;
            case EventCategory.Derelict: return derelictStatus;
            case EventCategory.Planet: return planetStatus;
            default: return asteroidStatus;
        }
    }
}
