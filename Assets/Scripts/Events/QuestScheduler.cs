using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "N turns later" for quests. Event outcomes schedule follow-up events
/// (ChoiceOutcome.scheduleEvent / scheduleInTurns); Start Events fire once from
/// a given turn. When one is due it is shown through the event UI at the start
/// of a player turn, when the ship is still and nothing else is open, holding a
/// movement interruption like hazards do.
///
/// A due event's own Conditions are checked again first; if they no longer hold
/// (the egg was flushed, the passengers were spaced) it is dropped quietly.
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

    [Header("References (found automatically if empty)")]
    [Tooltip("IEventPresenter used to show quest events (EventPanel).")]
    [SerializeField] private MonoBehaviour presenterBehaviour;
    [SerializeField] private PlayerShipState player;
    [SerializeField] private TurnManager turnManager;

    [Header("Start Events")]
    [SerializeField] private List<StartEvent> startEvents = new List<StartEvent>();

    [Header("Debug")]
    [SerializeField] private bool logSchedule = false;

    public bool IsBusy { get; private set; }

    private struct Pending
    {
        public EventDefinition definition;
        public int dueTurn;
    }

    private readonly List<Pending> pending = new List<Pending>();
    private readonly HashSet<StartEvent> startedFired = new HashSet<StartEvent>();
    private IEventPresenter presenter;
    private EventTriggerHandler trigger;
    private HazardEventController hazards;
    private PointOfInterestController pointsOfInterest;
    private CombatEncounterController combat;
    private WarpExitController warp;
    private DialoguePanelController dialogue;
    private int serial;


    private void Awake()
    {
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();
        presenter = presenterBehaviour as IEventPresenter;
        if (presenter == null) presenter = FindFirstObjectByType<EventPanel>();

        trigger = FindFirstObjectByType<EventTriggerHandler>();
        hazards = FindFirstObjectByType<HazardEventController>();
        pointsOfInterest = FindFirstObjectByType<PointOfInterestController>();
        combat = FindFirstObjectByType<CombatEncounterController>();
        warp = FindFirstObjectByType<WarpExitController>();
        dialogue = FindFirstObjectByType<DialoguePanelController>(FindObjectsInactive.Include);
    }


    /// <summary>Show this event in N turns (at least 1).</summary>
    public void Schedule(EventDefinition definition, int turns)
    {
        if (definition == null) return;
        int due = CurrentTurn + Mathf.Max(1, turns);
        pending.Add(new Pending { definition = definition, dueTurn = due });
        if (logSchedule) Debug.Log($"QuestScheduler: {definition.Id} due on turn {due}.", this);
    }


    private int CurrentTurn => turnManager != null ? turnManager.CurrentTurn : 0;


    private void Update()
    {
        if (IsBusy || presenter == null || !Idle()) return;

        int turn = CurrentTurn;

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


    private bool Idle()
    {
        if (player == null || player.IsMoving) return false;
        if (turnManager != null && turnManager.CurrentPhase != TurnPhase.Player) return false;
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
        EventContext context = new EventContext(player != null ? player.EventState : null, null, null,
                                                player != null ? player.CurrentCell : Vector3Int.zero, CurrentTurn);
        return def.Conditions == null || def.Conditions.IsMet(context);
    }


    private IEnumerator Run(EventDefinition definition)
    {
        IsBusy = true;
        MovementInterruptionHandle hold = player != null ? player.AcquireMovementInterruption("Quest: " + definition.Id) : null;

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
