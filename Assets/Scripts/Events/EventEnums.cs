/// <summary>
/// What kind of thing hosts an event site.
/// </summary>
public enum EventCategory
{
    Asteroid,
    Planet,
    Derelict
}


/// <summary>
/// Lifecycle of one event site. How much the player KNOWS about a site is a
/// separate axis and reuses the existing PlanetKnowledgeState ratchet.
/// </summary>
public enum EventSiteState
{
    Active,
    Triggered,
    Resolved,
    Expired
}
