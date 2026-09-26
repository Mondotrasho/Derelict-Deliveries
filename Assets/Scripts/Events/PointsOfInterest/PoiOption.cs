using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>What picking a Point of Interest option launches.</summary>
public enum PoiActionKind
{
    /// <summary>Plays Dialogue Json in the existing DialoguePanelController.</summary>
    Dialogue,

    /// <summary>Spawns an enemy next to the player and starts the existing combat encounter.</summary>
    Combat,

    /// <summary>Shows Event Definition in the event UI (EventPanel) - mining, market events etc.</summary>
    Event
}


/// <summary>
/// One thing the player can choose to do at a planet. Fixed options are
/// authored per planet; shared options apply to any planet whose state passes
/// the conditions (planet tags, player tags/flags/counters - quest state lives
/// in the player's IEventState).
/// </summary>
[Serializable]
public sealed class PoiOption
{
    [Tooltip("Stable id. Used for the once-per-planet flag poi:<id>:done.")]
    public string id = "option";
    public string title = "Option";
    [TextArea(1, 4)] public string description = "";

    public PoiActionKind kind = PoiActionKind.Dialogue;

    [Tooltip("When this option is offered. Planet scope reads the planet's tags/flags; Player scope the player's.")]
    public EventConditions conditions = new EventConditions();

    [Tooltip("Hide the option on this planet once it has been completed there.")]
    public bool oncePerPlanet = false;

    [Tooltip("Written through IEventState when the option completes (dialogue closed, combat won, event resolved).")]
    public EventStateWrites onComplete = new EventStateWrites();

    [Tooltip("Come back to the planet's option list afterwards. Combat options always end the visit.")]
    public bool returnToPicker = true;

    [Header("Dialogue")]
    public TextAsset dialogueJson;
    public bool runBootSequence = false;

    [Header("Event (mining etc.)")]
    public EventDefinition eventDefinition;

    public string DoneFlag => "poi:" + (string.IsNullOrWhiteSpace(id) ? title : id.Trim()) + ":done";

    public bool ReturnsToPicker => returnToPicker && kind != PoiActionKind.Combat;


    public bool IsAvailable(EventContext context)
    {
        if (oncePerPlanet && context.Planet != null && context.Planet.GetFlag(DoneFlag)) return false;
        if (conditions != null && !conditions.IsMet(context)) return false;

        switch (kind)
        {
            case PoiActionKind.Dialogue: return dialogueJson != null;
            case PoiActionKind.Event: return eventDefinition != null;
            default: return true;
        }
    }
}


/// <summary>Fixed options for one planet, matched by Planet.id (case-insensitive).</summary>
[Serializable]
public sealed class PlanetPoiEntry
{
    public string planetId = "";
    public List<PoiOption> options = new List<PoiOption>();
}


/// <summary>An entry shown in the picker: an authored option or the planet's live event site.</summary>
public sealed class PoiChoice
{
    public string Title { get; }
    public string Description { get; }
    public PoiOption Option { get; }
    public EventSite Site { get; }

    public PoiChoice(PoiOption option)
    {
        Option = option;
        Title = option.title;
        Description = option.description;
    }

    public PoiChoice(EventSite site)
    {
        Site = site;
        string name = site.Definition != null ? site.Definition.DisplayName : "Unknown signal";
        Title = "Signal: " + name;
        Description = site.Definition != null ? site.Definition.BodyText : "";
    }
}
