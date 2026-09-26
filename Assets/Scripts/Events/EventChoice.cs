using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>What a resource delta changes. Supplies are a player counter (EventKeys.Supplies).</summary>
public enum ResourceKind
{
    Fuel,
    Hull,
    Crew,
    Supplies
}


/// <summary>One resource change. Positive adds, negative removes (hull: negative = damage).</summary>
[Serializable]
public struct ResourceDelta
{
    public ResourceKind kind;
    public float amount;
}


/// <summary>
/// One possible result of a choice. A choice rolls exactly one of its outcomes,
/// weighted by <see cref="weight"/> (weights are relative, so 60 / 30 / 10 reads
/// as percentages).
/// </summary>
[Serializable]
public sealed class ChoiceOutcome
{
    [Tooltip("Inspector name only, e.g. Survivor / Dead crew / Parasite.")]
    public string label = "Result";

    [Tooltip("Relative chance. The odds shown on the button are the FIRST outcome's share of the total.")]
    [Min(0f)] public float weight = 1f;

    [TextArea(2, 4)] public string resultText = "";

    public List<ResourceDelta> resources = new List<ResourceDelta>();

    [Tooltip("State written when this outcome happens (tags, flags, counters on player / planet / site).")]
    public EventStateWrites writes = new EventStateWrites();

    [Tooltip("Remove the asteroid on the event's cell (mining).")]
    public bool consumeAsteroid;

    [Tooltip("Show this event next, in the same window (e.g. mining uncovers a life capsule). Its own On Resolve is not applied; its choices' outcomes are.")]
    public EventDefinition followUp;

    [Tooltip("End the event and start a fight with this enemy type, spawned next to the player (e.g. shooting the life capsule).")]
    public EnemyShipDefinition startCombatWith;

    [Tooltip("Spawn this enemy type at a map-edge entry cell (it then hunts the player like any enemy). E.g. defiling the shrine brings the eldritch monster.")]
    public EnemyShipDefinition spawnOnMapEdge;
}


/// <summary>One button in the event UI.</summary>
[Serializable]
public sealed class EventChoice
{
    public string id = "choice";
    public string text = "Do something";

    [Tooltip("Hidden (or greyed, see below) unless these are met.")]
    public EventConditions availability = new EventConditions();

    [Tooltip("Unmet: show greyed out with the reason instead of hiding it.")]
    public bool showWhenLocked;
    public string lockedReason = "";

    [Tooltip("Show the first outcome's chance on the button, e.g. (60%). Only shown when there is more than one outcome.")]
    public bool showOdds = true;

    public List<ChoiceOutcome> outcomes = new List<ChoiceOutcome> { new ChoiceOutcome() };

    [Tooltip("Optional: opens the dialogue UI with this JSON after the result.")]
    public TextAsset dialogue;

    [Tooltip("Off: return to this event's choices afterwards (re-checked against the new state).")]
    public bool endsEvent = true;

    [Tooltip("For choices that return to the event: hide this one afterwards.")]
    public bool hideAfterUse = true;
}
