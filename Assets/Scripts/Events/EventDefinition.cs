using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One kind of event. Deliberately thin: identity, which families it belongs to
/// (tags), when it may appear (conditions against existing IEventState), what it
/// writes back when resolved, and how it looks on the map.
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

    [Header("Lifetime")]
    [Tooltip("Turns before an untriggered site expires. 0 = never.")]
    [Min(0)]
    [SerializeField] private int lifetimeTurns = 0;

    [Header("Debug / future presenter")]
    [TextArea(2, 6)]
    [SerializeField] private string debugText = "Placeholder event.";
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
    public int LifetimeTurns => lifetimeTurns;
    public string DebugText => debugText;
    public TextAsset DialogueJson => dialogueJson;


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
