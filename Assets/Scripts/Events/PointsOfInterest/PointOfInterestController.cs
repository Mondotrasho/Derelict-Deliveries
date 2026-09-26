using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Planet Points of Interest. Arriving at a planet no longer plays one fixed
/// dialogue: this opens a picker listing everything the player can do there,
/// then launches the chosen interaction with the existing systems:
///
///   Dialogue -> DialoguePanelController (the terminal window)
///   Combat   -> spawns an enemy next to the player and starts the existing encounter
///   Event    -> the event presenter (currently DebugEventWindow) - mining etc.
///   Signal   -> a live planet event site from Event Discovery, via EventTriggerHandler
///
/// Options are fixed per planet (Planet Options) or shared across planets
/// (Shared Options); both are filtered with EventConditions against the
/// planet's and player's IEventState (planet tags, player tags/flags/counters).
///
/// A visit loops: after an option finishes, the list is rebuilt (finished
/// once-per-planet options drop out, newly unlocked ones appear) and the picker
/// opens again until the player picks Leave. A fight, an option with Return To
/// Picker off, or an empty list ends the visit instead.
/// Travel is held with a movement interruption for the whole interaction and
/// resumes on its own afterwards. EventDirector calls TryOpen first on every
/// arrival, so this replaces PlanetDialogueTestTrigger (disable that one).
/// </summary>
[DisallowMultipleComponent]
public class PointOfInterestController : MonoBehaviour
{
    [Header("Core References")]
    [SerializeField] private PlayerShipState player;
    [SerializeField] private TurnManager turnManager;
    [SerializeField] private PlanetManager planetManager;

    [Header("UI")]
    [Tooltip("IPoiPicker that lists the options (PlanetPicker; DebugPoiPicker is the fallback).")]
    [SerializeField] private MonoBehaviour pickerBehaviour;
    [SerializeField] private DialoguePanelController dialoguePanel;

    [Tooltip("IEventPresenter for Event options (EventPanel; DebugEventWindow is the fallback).")]
    [SerializeField] private MonoBehaviour eventPresenterBehaviour;

    [Header("Combat")]
    [SerializeField] private EnemySpawnController enemySpawner;
    [SerializeField] private EnemyTurnController enemyTurnController;
    [SerializeField] private CombatEncounterController combatController;

    [Header("Event Discovery")]
    [SerializeField] private EventSiteRegistry registry;
    [SerializeField] private EventTriggerHandler trigger;
    [Tooltip("List a live planet event site (from PlanetEventSource) as a 'Signal' choice.")]
    [SerializeField] private bool includeLiveEventSites = true;

    [Header("Options")]
    [Tooltip("Fixed options for specific planets.")]
    [SerializeField] private List<PlanetPoiEntry> planetOptions = new List<PlanetPoiEntry>();

    [Tooltip("Options offered at any planet whose conditions pass.")]
    [SerializeField] private List<PoiOption> sharedOptions = new List<PoiOption>();

    public bool IsBusy { get; private set; }

    private IPoiPicker picker;
    private IEventPresenter eventPresenter;
    private readonly List<PoiChoice> choices = new List<PoiChoice>();
    private int serial;


    private void Awake()
    {
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();
        if (planetManager == null) planetManager = FindFirstObjectByType<PlanetManager>();
        if (enemySpawner == null) enemySpawner = FindFirstObjectByType<EnemySpawnController>();
        if (enemyTurnController == null) enemyTurnController = FindFirstObjectByType<EnemyTurnController>();
        if (combatController == null) combatController = FindFirstObjectByType<CombatEncounterController>();
        if (registry == null) registry = FindFirstObjectByType<EventSiteRegistry>();
        if (trigger == null) trigger = FindFirstObjectByType<EventTriggerHandler>();

        picker = pickerBehaviour as IPoiPicker;
        if (picker == null) picker = FindFirstObjectByType<DebugPoiPicker>();

        eventPresenter = eventPresenterBehaviour as IEventPresenter;
        if (eventPresenter == null) eventPresenter = FindFirstObjectByType<DebugEventWindow>();
    }


    /// <summary>
    /// If the entered cell is a planet with at least one available choice,
    /// opens the picker and returns true (the arrival is claimed).
    /// </summary>
    public bool TryOpen(Vector3Int cell)
    {
        if (IsBusy || picker == null || planetManager == null) return false;
        if (!planetManager.TryGetPlanetAtCell(cell, out Planet planet)) return false;

        EventContext context = BuildContext(planet, cell);
        BuildChoices(planet, context);
        if (choices.Count == 0) return false;

        StartCoroutine(Run(planet, cell, new List<PoiChoice>(choices)));
        return true;
    }


    private void BuildChoices(Planet planet, EventContext context)
    {
        choices.Clear();

        foreach (PlanetPoiEntry entry in planetOptions)
        {
            if (entry == null || !string.Equals(entry.planetId?.Trim(), planet.id, System.StringComparison.OrdinalIgnoreCase)) continue;
            foreach (PoiOption option in entry.options)
            {
                if (option != null && option.IsAvailable(context)) choices.Add(new PoiChoice(option));
            }
        }

        foreach (PoiOption option in sharedOptions)
        {
            if (option != null && option.IsAvailable(context)) choices.Add(new PoiChoice(option));
        }

        if (includeLiveEventSites && registry != null &&
            registry.TryGetSiteAtCell(planet.cell, out EventSite site) && site.IsLive)
        {
            choices.Add(new PoiChoice(site));
        }
    }


    private IEnumerator Run(Planet planet, Vector3Int cell, List<PoiChoice> offered)
    {
        IsBusy = true;
        MovementInterruptionHandle hold = player != null
            ? player.AcquireMovementInterruption("Point of interest: " + planet.id)
            : null;

        yield return null;   // let contact/combat resolve first

        if (turnManager != null && turnManager.CurrentPhase == TurnPhase.Combat)
        {
            hold?.Release();
            IsBusy = false;
            yield break;
        }

        List<PoiChoice> list = offered;
        while (true)
        {
            PoiChoice picked = null;
            yield return picker.Pick(planet, list, c => picked = c);
            if (picked == null) break;   // Leave

            bool returnToPicker = true;

            if (picked.Site != null)
            {
                yield return RunSite(picked.Site);
            }
            else
            {
                EventContext context = BuildContext(planet, cell);
                bool completed = false;
                returnToPicker = picked.Option.ReturnsToPicker;

                switch (picked.Option.kind)
                {
                    case PoiActionKind.Dialogue:
                        yield return RunDialogue(picked.Option, r => completed = r);
                        break;
                    case PoiActionKind.Event:
                        yield return RunEvent(picked.Option, cell, context, r => completed = r);
                        break;
                    case PoiActionKind.Combat:
                        yield return RunCombat(r => completed = r);
                        break;
                }

                if (completed)
                {
                    picked.Option.onComplete?.Apply(context);
                    EventChoiceResolver.ApplyResources(picked.Option.completeResources, context, player);
                    EventChoiceResolver.ApplyDetection(picked.Option.completeDetectionTurns, enemySpawner);
                    if (picked.Option.oncePerPlanet) planet.eventState.SetFlag(picked.Option.DoneFlag, true);
                }
            }

            // Exceptions end the visit instead of returning to the list.
            if (!returnToPicker) break;
            if (turnManager != null &&
                (turnManager.CurrentPhase == TurnPhase.Combat || turnManager.CurrentPhase == TurnPhase.Defeat)) break;

            BuildChoices(planet, BuildContext(planet, cell));
            if (choices.Count == 0) break;
            list = new List<PoiChoice>(choices);
        }

        hold?.Release();   // paused route (if any) resumes by itself
        IsBusy = false;
    }


    // ==================== Launchers ====================

    private IEnumerator RunDialogue(PoiOption option, System.Action<bool> completed)
    {
        if (dialoguePanel == null)
        {
            Debug.LogWarning($"{name}: no DialoguePanelController assigned for dialogue option '{option.id}'.", this);
            completed(false);
            yield break;
        }

        yield return dialoguePanel.OpenDialogueAndWait(option.dialogueJson, option.runBootSequence);
        completed(true);
    }


    private IEnumerator RunEvent(PoiOption option, Vector3Int cell, EventContext context, System.Action<bool> completed)
    {
        if (eventPresenter == null)
        {
            completed(false);
            yield break;
        }

        serial++;
        EventDefinition def = option.eventDefinition;
        EventSite site = new EventSite($"poi:{option.id}:{cell.x},{cell.y}:{serial}", cell, def, null, context.Turn);
        site.Knowledge = PlanetKnowledgeState.Identified;
        EventContext siteContext = context.WithSite(site.State);

        EventOutcome outcome = EventOutcome.LeaveForLater;
        yield return eventPresenter.Present(site, siteContext, o => outcome = o);

        if (outcome.resolved)
        {
            def.OnResolve?.Apply(siteContext);
            siteContext.Player?.IncrementCounter(EventKeys.Resolved(def.Category));
        }
        if (outcome.stopJourney && player != null) player.CancelCurrentJourney();
        completed(outcome.resolved);
    }


    private IEnumerator RunCombat(System.Action<bool> completed)
    {
        if (enemySpawner == null || enemyTurnController == null || combatController == null || player == null)
        {
            Debug.LogWarning($"{name}: combat option needs EnemySpawnController, EnemyTurnController and CombatEncounterController.", this);
            completed(false);
            yield break;
        }

        EnemyShip enemy = SpawnAdjacentEnemy(player.CurrentCell);
        if (enemy == null)
        {
            Debug.LogWarning($"{name}: no free cell (or enemy cap reached) to spawn a combat opponent.", this);
            completed(false);
            yield break;
        }

        CombatEncounterOutcome result = CombatEncounterOutcome.Interrupted;
        System.Action<EnemyShip, CombatEncounterOutcome, int> onEnded = (e, o, r) => result = o;
        combatController.EncounterEnded += onEnded;

        if (!enemyTurnController.TryStartEncounter(enemy))
        {
            combatController.EncounterEnded -= onEnded;
            Debug.LogWarning($"{name}: could not start the encounter.", this);
            completed(false);
            yield break;
        }

        yield return null;
        while (combatController.IsEncounterActive ||
               (turnManager != null && turnManager.CurrentPhase == TurnPhase.Combat))
        {
            yield return null;
        }

        combatController.EncounterEnded -= onEnded;
        completed(result == CombatEncounterOutcome.Victory);
    }


    private IEnumerator RunSite(EventSite site)
    {
        if (trigger == null || !trigger.TryTrigger(site.Cell, 0)) yield break;
        while (trigger.IsBusy) yield return null;
    }


    private EnemyShip SpawnAdjacentEnemy(Vector3Int centre)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Vector3Int cell = new Vector3Int(centre.x + dx, centre.y + dy, centre.z);
                if (enemySpawner.TrySpawnEnemyAtCell(cell, out EnemyShip enemy)) return enemy;
            }
        }
        return null;
    }


    private EventContext BuildContext(Planet planet, Vector3Int cell)
    {
        int turn = turnManager != null ? turnManager.CurrentTurn : 0;
        return new EventContext(player != null ? player.EventState : null, planet, null, cell, turn);
    }
}
