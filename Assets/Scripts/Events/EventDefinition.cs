using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One kind of event. Identity, which families it belongs to (tags), when it may
/// appear (conditions against existing IEventState), what it writes back when
/// resolved, how it looks on the map, and what the event UI offers (description
/// and choices with weighted outcomes).
/// </summary>
[CreateAssetMenu(
    fileName = "EventDefinition",
    menuName = "Derelict Deliveries/Events/Event Definition")]
public sealed class EventDefinition : ScriptableObject
{
    [Header("Identity")]
    [SerializeField] private string id = "mining.basic";
    [SerializeField] private string displayName = "Ore signature";
    [SerializeField] private EventCategory category = EventCategory.Asteroid;

    [Tooltip("Event families this definition belongs to (matched against a source's Event Tags). Also copied onto the site's own state.")]
    [SerializeField] private List<string> tags = new List<string> { "mining" };

    [Header("When it may appear")]
    [SerializeField] private EventConditions conditions = new EventConditions();

    [Tooltip("Sets planet flag event:<id>:done on resolve and never rolls again on that planet.")]
    [SerializeField] private bool oncePerPlanet = false;

    [Header("What resolving writes")]
    [SerializeField] private EventStateWrites onResolve = new EventStateWrites();

    [Header("Presentation")]
    [Tooltip("If off, the marker uses the presenter's category colour instead.")]
    [SerializeField] private bool useMarkerColour = true;
    [SerializeField] private Color markerColour = new Color(1f, 0.8f, 0.2f, 1f);

    [Tooltip("Derelicts: wreck / pod / station sprite shown once identified. Empty = generated placeholder.")]
    [SerializeField] private Sprite identifiedSprite;

    [Tooltip("Derelicts: colour filter multiplied into the identified sprite. White = unchanged.")]
    [SerializeField] private Color identifiedTint = Color.white;

    [Tooltip("Derelicts: slow spin in degrees per second. 0 = none.")]
    [SerializeField] private float identifiedSpinDegreesPerSecond = 6f;

    [Tooltip("Derelicts: the sprite is scaled to fit this many grid tiles (largest side), whatever its pixels-per-unit.")]
    [Min(0.05f)]
    [SerializeField] private float identifiedSizeInTiles = 1f;

    [Header("Lifetime")]
    [Tooltip("Turns before an untriggered site expires. 0 = never.")]
    [Min(0)]
    [SerializeField] private int lifetimeTurns = 0;

    [Header("Event UI")]
    [Tooltip("Banner shown at the top of the event window (~3:1). Empty = the Banner Library picks one (category default, or the planet's banner for planet events).")]
    [SerializeField] private Sprite banner;

    [Tooltip("Player-facing text in the event window (and under the name once identified). Empty = Debug Text.")]
    [TextArea(2, 6)]
    [SerializeField] private string shortDescription = "";

    [Tooltip("Offer 'Leave for later'. Hazards never offer it.")]
    [SerializeField] private bool allowLeave = true;

    [Tooltip("Buttons in the event UI. Empty = the placeholder Resolve / Leave buttons.")]
    [SerializeField] private List<EventChoice> choices = new List<EventChoice>();

    [Header("Debug")]
    [TextArea(2, 6)]
    [SerializeField] private string debugText = "Placeholder event.";
    [Tooltip("Not used by the event UI: dialogue lives on the choice that opens it.")]
    [SerializeField] private TextAsset dialogueJson;


    public string Id => string.IsNullOrWhiteSpace(id) ? name : id.Trim();
    public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? Id : displayName.Trim();
    public EventCategory Category => category;
    public IReadOnlyList<string> Tags => tags;
    public EventConditions Conditions => conditions;
    public bool OncePerPlanet => oncePerPlanet;
    public EventStateWrites OnResolve => onResolve;
    public bool UseMarkerColour => useMarkerColour;
    public Color MarkerColour => markerColour;
    public Sprite IdentifiedSprite => identifiedSprite;
    public Color IdentifiedTint => identifiedTint;
    public float IdentifiedSpinDegreesPerSecond => identifiedSpinDegreesPerSecond;
    public float IdentifiedSizeInTiles => identifiedSizeInTiles;
    public int LifetimeTurns => lifetimeTurns;
    public string DebugText => debugText;
    public TextAsset DialogueJson => dialogueJson;
    public Sprite Banner => banner;
    public string ShortDescription => shortDescription;
    public bool AllowLeave => allowLeave;
    public IReadOnlyList<EventChoice> Choices => choices;

    /// <summary>Text for the event UI: the short description, else the debug text.</summary>
    public string BodyText => string.IsNullOrWhiteSpace(shortDescription) ? debugText : shortDescription;


    public EventChoice FindChoice(string choiceId)
    {
        if (choices == null || string.IsNullOrEmpty(choiceId)) return null;
        foreach (EventChoice c in choices)
        {
            if (c != null && c.id == choiceId) return c;
        }
        return null;
    }


    /// <summary>Case-insensitive, like EventStateData.</summary>
    public bool HasTag(string tag)
    {
        if (tags == null || string.IsNullOrWhiteSpace(tag)) return false;
        string wanted = tag.Trim();
        foreach (string t in tags)
        {
            if (!string.IsNullOrWhiteSpace(t) &&
                string.Equals(t.Trim(), wanted, System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }


    public bool HasAnyTag(IReadOnlyList<string> anyOf)
    {
        if (anyOf == null || anyOf.Count == 0) return true;
        foreach (string t in anyOf)
        {
            if (HasTag(t)) return true;
        }
        return false;
    }


    /// <summary>
    /// Full eligibility: tag family, conditions and the once-per-planet flag.
    /// </summary>
    public bool IsEligible(IReadOnlyList<string> sourceTags, EventContext context)
    {
        if (!HasAnyTag(sourceTags)) return false;
        if (conditions != null && !conditions.IsMet(context)) return false;
        if (oncePerPlanet && context.Planet != null && context.Planet.GetFlag(EventKeys.Done(Id))) return false;
        return true;
    }
}
