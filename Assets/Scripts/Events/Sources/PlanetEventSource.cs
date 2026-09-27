using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Push-style source: rolls a planet when VisionManager says it came into live
/// view (and optionally on first discovery), not inside the arrival scan.
///
/// Cooldown is stored on the planet itself (counter event:nextRollTurn), so it
/// persists with authored planet data. Planet requirements are expressed as
/// Planet-scope conditions on the definitions (e.g. tag Industrial).
///
/// Keep Source Enabled OFF while DialogueTriggerManager/PlanetDialogueTestTrigger
/// is active: that component already reacts to every arrival at its planets.
/// </summary>
public class PlanetEventSource : EventSiteSourceBase
{
    [Header("Planet References")]
    [SerializeField] private PlanetManager planetManager;
    [SerializeField] private VisionManager visionManager;
    [SerializeField] private TurnManager turnManager;

    [Tooltip("Also roll once when a planet is first discovered.")]
    [SerializeField] private bool rollWhenDiscovered = true;

    public override EventCategory Category => EventCategory.Planet;
    public override bool RollsOnArrival => false;

    private VisionManager subscribedVision;


    private void Reset()
    {
        sourceId = "planet";
        sourceEnabled = false;
        rollChance = 0.2f;
        rerollCooldownTurns = 5;
        maxNewSitesPerScan = 1;
        eventTags = new List<string> { "planet" };
    }


    public override void Bind(EventDirector director)
    {
        base.Bind(director);

        if (planetManager == null) planetManager = FindFirstObjectByType<PlanetManager>();
        if (visionManager == null) visionManager = director != null ? director.VisionManager : null;
        if (turnManager == null) turnManager = director != null ? director.TurnManager : null;

        Subscribe();
    }


    private void OnEnable()
    {
        Subscribe();
    }


    private void OnDisable()
    {
        Unsubscribe();
    }


    private void Subscribe()
    {
        if (visionManager == null || subscribedVision == visionManager) return;
        Unsubscribe();
        subscribedVision = visionManager;
        subscribedVision.PlanetCurrentVisibilityChanged += HandlePlanetVisibilityChanged;
        subscribedVision.PlanetDiscovered += HandlePlanetDiscovered;
    }


    private void Unsubscribe()
    {
        if (subscribedVision == null) return;
        subscribedVision.PlanetCurrentVisibilityChanged -= HandlePlanetVisibilityChanged;
        subscribedVision.PlanetDiscovered -= HandlePlanetDiscovered;
        subscribedVision = null;
    }


    private void HandlePlanetVisibilityChanged(Planet planet, bool visible)
    {
        if (visible) TryRollPlanet(planet);
    }


    private void HandlePlanetDiscovered(Planet planet, FogOfWar.VisibilityTier tier)
    {
        if (rollWhenDiscovered) TryRollPlanet(planet);
    }


    private void TryRollPlanet(Planet planet)
    {
        if (planet == null || !IsEnabled || Director == null) return;
        if (turnManager != null && !turnManager.IsPlayerPhase) return;
        Director.TryRollAt(this, planet.cell);
    }


    /// <summary>Planets are never part of the arrival scan.</summary>
    public override void CollectCandidates(Vector3Int centre, int radius, List<Vector3Int> results) { }


    public override bool CanRollCell(Vector3Int cell, int turn, EventRollMemory memory)
    {
        Planet planet = FindPlanet(cell);
        return planet != null && turn >= planet.eventState.GetCounter(EventKeys.NextRollTurn, int.MinValue);
    }


    public override void RecordRoll(Vector3Int cell, int turn, EventRollMemory memory)
    {
        Planet planet = FindPlanet(cell);
        if (planet != null) planet.eventState.SetCounter(EventKeys.NextRollTurn, turn + rerollCooldownTurns);
    }


    public override EventContext BuildContext(Vector3Int cell, int turn)
    {
        IEventState playerState = player != null ? player.EventState : null;
        return new EventContext(playerState, FindPlanet(cell), null, cell, turn);
    }


    public override EventSite CreateSite(Vector3Int cell, EventDefinition definition, int turn)
    {
        Planet planet = FindPlanet(cell);
        if (planet == null) return null;

        EventSite site = new EventSite(MakeSiteId(cell, turn), cell, definition, this, turn, planet);
        site.Knowledge = planet.visibility.knowledgeState;
        planet.eventState.AddTag(EventKeys.Available);
        return site;
    }


    public override bool IsSiteStillValid(EventSite site)
    {
        return site.Planet != null && FindPlanet(site.Cell) == site.Planet;
    }


    public override void OnSiteRemoved(EventSite site)
    {
        if (site.Planet != null) site.Planet.eventState.RemoveTag(EventKeys.Available);
    }


    private Planet FindPlanet(Vector3Int cell)
    {
        if (planetManager != null && planetManager.TryGetPlanetAtCell(cell, out Planet planet)) return planet;
        return null;
    }
}
