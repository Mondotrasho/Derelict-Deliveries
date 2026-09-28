using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Quest timing and travel gates.
///
/// Event outcomes can schedule a follow-up for N turns later. Start Events can
/// fire once from a configured turn. Approach Events are a safety net for story
/// beats that must happen before the ship reaches a particular planet: when the
/// player enters the configured radius, movement is paused and the event is
/// shown before the route resumes.
///
/// Delayed state writes are used for quiet unlocks which should become available
/// after a number of turns without opening a modal event immediately.
/// </summary>
[DisallowMultipleComponent]
public sealed class QuestScheduler : MonoBehaviour
{
    [Serializable]
    public sealed class StartEvent
    {
        public EventDefinition definition;
        [Tooltip("Earliest turn it can fire. It fires once, when its Conditions hold.")]
        [Min(0)] public int fromTurn = 1;
    }

    [Serializable]
    public sealed class ApproachEvent
    {
        public EventDefinition definition;
        [Tooltip("Planet id used as the approach target.")]
        public string planetId = "";
        [Tooltip("Chebyshev grid distance from the planet at which the event is forced. 2 means within two movement cells, including diagonals.")]
        [Min(0)] public int triggerDistance = 2;
    }

    [Header("References (found automatically if empty)")]
    [Tooltip("IEventPresenter used to show quest events (EventPanel).")]
    [SerializeField] private MonoBehaviour presenterBehaviour;
    [SerializeField] private PlayerShipState player;
    [SerializeField] private TurnManager turnManager;
    [SerializeField] private PlanetManager planetManager;

    [Header("Start Events")]
    [SerializeField] private List<StartEvent> startEvents = new List<StartEvent>();

    [Header("Approach Events")]
    [Tooltip("Story events that must happen before reaching a target planet. Their normal EventDefinition Conditions still decide whether they are currently relevant.")]
    [SerializeField] private List<ApproachEvent> approachEvents = new List<ApproachEvent>();

    [Header("Debug")]
    [SerializeField] private bool logSchedule = false;

    public bool IsBusy { get; private set; }

    private struct Pending
    {
        public EventDefinition definition;
        public int dueTurn;
    }

    private struct PendingWrites
    {
        public EventStateWrites writes;
        public int dueTurn;
    }

    private readonly List<Pending> pending = new List<Pending>();
    private readonly List<PendingWrites> pendingWrites = new List<PendingWrites>();
    private readonly HashSet<StartEvent> startedFired = new HashSet<StartEvent>();
    private IEventPresenter presenter;
    private EventTriggerHandler trigger;
    private HazardEventController hazards;
    private PointOfInterestController pointsOfInterest;
    private CombatEncounterController combat;
    private WarpExitController warp;
    private DialoguePanelController dialogue;
    private EventDefinition forcedApproachEvent;
    private MovementInterruptionHandle forcedApproachHold;
    private int serial;


    private void Awake()
    {
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();
        if (planetManager == null) planetManager = FindFirstObjectByType<PlanetManager>();
        presenter = presenterBehaviour as IEventPresenter;
        if (presenter == null) presenter = FindFirstObjectByType<EventPanel>();

        trigger = FindFirstObjectByType<EventTriggerHandler>();
        hazards = FindFirstObjectByType<HazardEventController>();
        pointsOfInterest = FindFirstObjectByType<PointOfInterestController>();
        combat = FindFirstObjectByType<CombatEncounterController>();
        warp = FindFirstObjectByType<WarpExitController>();
        dialogue = FindFirstObjectByType<DialoguePanelController>(FindObjectsInactive.Include);
    }


    private void OnEnable()
    {
        if (player != null) player.CellEntered += HandleCellEntered;
    }


    private void OnDisable()
    {
        if (player != null) player.CellEntered -= HandleCellEntered;

        forcedApproachHold?.Release();
        forcedApproachHold = null;
        forcedApproachEvent = null;
    }


    /// <summary>Show this event in N turns (at least 1).</summary>
    public void Schedule(EventDefinition definition, int turns)
    {
        if (definition == null) return;
        int due = CurrentTurn + Mathf.Max(1, turns);
        pending.Add(new Pending { definition = definition, dueTurn = due });
        if (logSchedule) Debug.Log($"QuestScheduler: {definition.Id} due on turn {due}.", this);
    }


    /// <summary>
    /// Apply these writes after N turns without opening an event window.
    /// Player-scope writes are the intended use; Planet/Site writes have no
    /// target in a delayed global context and are ignored by EventStateWrites.
    /// </summary>
    public void ScheduleWrites(EventStateWrites writes, int turns)
    {
        if (writes == null || writes.IsEmpty) return;
        int due = CurrentTurn + Mathf.Max(1, turns);
        pendingWrites.Add(new PendingWrites { writes = writes, dueTurn = due });
        if (logSchedule) Debug.Log($"QuestScheduler: delayed state write due on turn {due}.", this);
    }


    private int CurrentTurn => turnManager != null ? turnManager.CurrentTurn : 0;


    private void Update()
    {
        int turn = CurrentTurn;
        ApplyDueWrites(turn);

        if (forcedApproachEvent != null)
        {
            if (IsBusy || presenter == null || !IdleForForcedApproach()) return;

            EventDefinition def = forcedApproachEvent;
            MovementInterruptionHandle hold = forcedApproachHold;
            forcedApproachEvent = null;
            forcedApproachHold = null;

            if (Eligible(def)) StartCoroutine(Run(def, hold));
            else hold?.Release();
            return;
        }

        if (IsBusy || presenter == null || !Idle()) return;

        for (int i = 0; i < pending.Count; i++)
        {
            if (pending[i].dueTurn > turn) continue;
            EventDefinition def = pending[i].definition;
            pending.RemoveAt(i);
            if (Eligible(def)) StartCoroutine(Run(def));
            else if (logSchedule) Debug.Log($"QuestScheduler: {def.Id} dropped (conditions no longer met).", this);
            return;
        }

        foreach (StartEvent start in startEvents)
        {
            if (start == null || start.definition == null || startedFired.Contains(start)) continue;
            if (turn < start.fromTurn || !Eligible(start.definition)) continue;
            startedFired.Add(start);
            StartCoroutine(Run(start.definition));
            return;
        }
    }


    private void ApplyDueWrites(int turn)
    {
        for (int i = pendingWrites.Count - 1; i >= 0; i--)
        {
            if (pendingWrites[i].dueTurn > turn) continue;

            EventStateWrites writes = pendingWrites[i].writes;
            pendingWrites.RemoveAt(i);

            EventContext context = new EventContext(
                player != null ? player.EventState : null,
                null,
                null,
                player != null ? player.CurrentCell : Vector3Int.zero,
                turn);

            writes?.Apply(context);
            if (logSchedule) Debug.Log("QuestScheduler: applied delayed state write.", this);
        }
    }


    private void HandleCellEntered(Vector3Int cell)
    {
        if (forcedApproachEvent != null || IsBusy || player == null || planetManager == null) return;
        if (turnManager != null &&
            turnManager.CurrentPhase != TurnPhase.Player &&
            turnManager.CurrentPhase != TurnPhase.WaitingForPlayerMovement) return;

        foreach (ApproachEvent approach in approachEvents)
        {
            if (approach == null || approach.definition == null || string.IsNullOrWhiteSpace(approach.planetId)) continue;
            if (!Eligible(approach.definition)) continue;

            Planet target = planetManager.FindPlanet(approach.planetId);
            if (target == null) continue;

            int dx = Mathf.Abs(cell.x - target.cell.x);
            int dy = Mathf.Abs(cell.y - target.cell.y);
            int distance = Mathf.Max(dx, dy);
            if (distance > Mathf.Max(0, approach.triggerDistance)) continue;

            forcedApproachEvent = approach.definition;
            forcedApproachHold = player.AcquireMovementInterruption("Quest approach: " + approach.definition.Id);

            if (logSchedule)
                Debug.Log($"QuestScheduler: forcing {approach.definition.Id} {distance} cell(s) from {approach.planetId}.", this);
            return;
        }
    }


    private bool Idle()
    {
        if (player == null || player.IsMoving) return false;
        if (turnManager != null && turnManager.CurrentPhase != TurnPhase.Player) return false;
        return OtherSystemsIdle();
    }


    private bool IdleForForcedApproach()
    {
        if (player == null || player.IsMoving) return false;
        if (turnManager != null &&
            turnManager.CurrentPhase != TurnPhase.Player &&
            turnManager.CurrentPhase != TurnPhase.WaitingForPlayerMovement) return false;
        return OtherSystemsIdle();
    }


    private bool OtherSystemsIdle()
    {
        if (trigger != null && trigger.IsBusy) return false;
        if (hazards != null && hazards.IsBusy) return false;
        if (pointsOfInterest != null && pointsOfInterest.IsBusy) return false;
        if (combat != null && combat.IsEncounterActive) return false;
        if (warp != null && (warp.IsBusy || warp.RunOver)) return false;
        if (dialogue != null && dialogue.IsDialogueOpen) return false;
        return true;
    }


    private bool Eligible(EventDefinition def)
    {
        if (def == null) return false;

        EventContext context = new EventContext(player != null ? player.EventState : null, null, null,
                                                player != null ? player.CurrentCell : Vector3Int.zero, CurrentTurn);
        return def.Conditions == null || def.Conditions.IsMet(context);
    }


    private IEnumerator Run(EventDefinition definition, MovementInterruptionHandle existingHold = null)
    {
        IsBusy = true;
        MovementInterruptionHandle hold = existingHold ??
            (player != null ? player.AcquireMovementInterruption("Quest: " + definition.Id) : null);

        int turn = CurrentTurn;
        Vector3Int cell = player != null ? player.CurrentCell : Vector3Int.zero;
        serial++;
        EventSite site = new EventSite($"quest:{definition.Id}:t{turn}:{serial}", cell, definition, null, turn);
        site.Knowledge = PlanetKnowledgeState.Identified;
        EventContext context = new EventContext(player != null ? player.EventState : null, null, null, cell, turn)
            .WithSite(site.State);

        EventOutcome outcome = EventOutcome.LeaveForLater;
        yield return presenter.Present(site, context, o => outcome = o);

        if (outcome.resolved) definition.OnResolve?.Apply(context);

        hold?.Release();
        IsBusy = false;
    }
}
