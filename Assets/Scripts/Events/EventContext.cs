using UnityEngine;

/// <summary>
/// Everything a condition or resolve-write needs: the player, planet (optional)
/// and site (optional) IEventState, plus where and when.
/// </summary>
public readonly struct EventContext
{
    public IEventState Player { get; }
    public IEventState Planet { get; }
    public IEventState Site { get; }
    public Planet PlanetData { get; }
    public Vector3Int Cell { get; }
    public int Turn { get; }

    public EventContext(
        IEventState player,
        Planet planetData,
        IEventState site,
        Vector3Int cell,
        int turn)
    {
        Player = player;
        PlanetData = planetData;
        Planet = planetData != null ? planetData.eventState : null;
        Site = site;
        Cell = cell;
        Turn = turn;
    }

    public EventContext WithSite(IEventState site)
    {
        return new EventContext(Player, PlanetData, site, Cell, Turn);
    }

    public IEventState Get(StateScope scope)
    {
        switch (scope)
        {
            case StateScope.Player: return Player;
            case StateScope.Planet: return Planet;
            case StateScope.Site: return Site;
            default: return null;
        }
    }
}
