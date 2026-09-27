using UnityEngine;

/// <summary>
/// Runtime record for one tagged cell. Plain C# - not serialized, not a component.
/// State and knowledge are changed through EventSiteRegistry so its events fire.
/// </summary>
public sealed class EventSite
{
    public string Id { get; }
    public Vector3Int Cell { get; }
    public EventCategory Category { get; }
    public EventDefinition Definition { get; }
    public IEventSiteSource Source { get; }
    public int CreatedTurn { get; }

    /// <summary>The site's own tags/flags/counters, seeded with the definition's tags.</summary>
    public EventStateData State { get; } = new EventStateData();

    /// <summary>Planet sites only; null otherwise.</summary>
    public Planet Planet { get; }

    /// <summary>Derelict sites only: the spawned wreck/pod object.</summary>
    public GameObject SpawnedObject { get; set; }

    public EventSiteState LifecycleState { get; internal set; } = EventSiteState.Active;
    public PlanetKnowledgeState Knowledge { get; internal set; } = PlanetKnowledgeState.Unknown;

    public bool IsLive => LifecycleState == EventSiteState.Active;


    public EventSite(
        string id,
        Vector3Int cell,
        EventDefinition definition,
        IEventSiteSource source,
        int createdTurn,
        Planet planet = null)
    {
        Id = id;
        Cell = cell;
        Definition = definition;
        Category = definition != null ? definition.Category : EventCategory.Asteroid;
        Source = source;
        CreatedTurn = createdTurn;
        Planet = planet;

        if (definition != null && definition.Tags != null)
        {
            foreach (string tag in definition.Tags)
            {
                State.AddTag(tag);
            }
        }
    }


    public override string ToString()
    {
        string def = Definition != null ? Definition.Id : "<none>";
        return $"{Id} [{def}] {LifecycleState}/{Knowledge} @ {Cell}";
    }
}
