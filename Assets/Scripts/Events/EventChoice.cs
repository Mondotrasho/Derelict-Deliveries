using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>What a resource delta changes. Supplies are a player counter (EventKeys.Supplies).</summary>
public enum ResourceKind
{
    Fuel,
    Hull,
    Crew,
    Supplies,
    Shields,    // appended so existing assets keep their values

    /// <summary>Incoming damage: shields absorb it first, the rest hits the hull. The sign is ignored.</summary>
    Damage
}


/// <summary>One resource change. Positive adds, negative removes (hull: negative = damage). Damage: shields first, then hull.</summary>
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

    [Tooltip("Turns added to the hunt: before detection it delays detection, after it delays the next wave. Negative = sooner.")]
    public int detectionTurns;

    [Tooltip("Remove the asteroid on the event's cell (mining).")]
    public bool consumeAsteroid;

    [Tooltip("Show this event next, in the same window (e.g. mining uncovers a life capsule). Its own On Resolve is not applied; its choices' outcomes are.")]
    public EventDefinition followUp;

    [Tooltip("Adds this SITE counter's value back to the ship's crew and zeroes it - e.g. 'party' when a boarding party returns. Empty = none.")]
    public string returnCrewFromSiteCounter = "";

    [Tooltip("End the event and start a fight with this enemy type, spawned next to the player (e.g. shooting the life capsule).")]
    public EnemyShipDefinition startCombatWith;

    [Tooltip("Spawn this enemy type at a map-edge entry cell (it then hunts the player like any enemy). E.g. defiling the shrine brings the eldritch monster.")]
    public EnemyShipDefinition spawnOnMapEdge;

    [Tooltip("Turns to wait before Spawn On Map Edge happens. 0 = straight away (while the window is still open). 1+ = at the start of that later player turn, with the map in view, so the player sees it arrive.")]
    [Min(0)] public int spawnDelayTurns;

    [Tooltip("When this outcome happens, swap the window's banner straight away to this event's banner (e.g. Defile -> the defiled shrine), so the result screen already shows the new state.")]
    public EventDefinition showBannerOf;

    [Tooltip("Show this event later, through the QuestScheduler (quests: '2 turns later...'). Its Conditions are checked again when it comes due.")]
    public EventDefinition scheduleEvent;

    [Tooltip("How many turns later Schedule Event appears (at least 1).")]
    [Min(1)] public int scheduleInTurns = 1;

    [Tooltip("An officer joins (N4).")]
    public OfficerDefinition recruitOfficer;

    [Tooltip("This officer leaves or dies, if aboard.")]
    public OfficerDefinition loseOfficer;

    [Tooltip("Lose whichever officer has the most of this stat (e.g. 'engineering': the engineer who tried). Empty = none.")]
    public string loseOfficerBestAt = "";
}


/// <summary>One button in the event UI.</summary>
[Serializable]
public sealed class EventChoice
{
    public string id = "choice";
    public string text = "Do something";

    [Tooltip("Hidden (or greyed, see below) unless these are met.")]
    public EventConditions availability = new EventConditions();

    [Tooltip("Needs at least this much crew aboard (e.g. boarding: 4). 0 = no requirement.")]
    [Min(0)] public int minCrew;

    [Header("Officer Bonus (N4)")]
    [Tooltip("Officer stat that helps, e.g. engineering. The button shows [Engineering] and the first outcome's chance rises with it.")]
    public string bonusStat = "";

    [Tooltip("Added to the first outcome's chance per point of the stat aboard (0.05 = +5% per point, capped at 95%).")]
    [Range(0f, 0.5f)] public float chancePerPoint = 0.05f;

    [Tooltip("Needs at least this much of the stat aboard to be picked at all (0 = anyone can try).")]
    [Min(0)] public int minBonus;

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
