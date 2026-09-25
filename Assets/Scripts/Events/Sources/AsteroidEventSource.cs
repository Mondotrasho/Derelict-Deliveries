using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Arrival-driven source: asteroid cells in the (vision-limited) scan square.
/// Sites start Detected - they were rolled because the ship could see them.
/// A site becomes invalid once its asteroid is gone (mined elsewhere, etc.).
/// </summary>
public class AsteroidEventSource : EventSiteSourceBase
{
    [Header("Asteroids")]
    [SerializeField] private AsteroidFieldPainter asteroidField;

    public override EventCategory Category => EventCategory.Asteroid;
    public AsteroidFieldPainter AsteroidField => asteroidField;


    private void Reset()
    {
        sourceId = "asteroid";
        rollChance = 0.08f;
        rerollCooldownTurns = 5;
        maxNewSitesPerScan = 1;
        eventTags = new List<string> { "mining" };
    }


    public override void Bind(EventDirector director)
    {
        base.Bind(director);
        if (asteroidField == null) asteroidField = FindFirstObjectByType<AsteroidFieldPainter>();
        if (sourceEnabled && asteroidField == null)
            Debug.LogWarning($"{name}: AsteroidEventSource has no AsteroidFieldPainter.", this);
    }


    public override void CollectCandidates(Vector3Int centre, int radius, List<Vector3Int> results)
    {
        if (asteroidField == null) return;

        BoundsInt area = new BoundsInt(
            centre.x - radius, centre.y - radius, 0,
            radius * 2 + 1, radius * 2 + 1, 1);

        results.AddRange(asteroidField.GetTilesInArea(area));
    }


    public override EventSite CreateSite(Vector3Int cell, EventDefinition definition, int turn)
    {
        EventSite site = new EventSite(MakeSiteId(cell, turn), cell, definition, this, turn);
        site.Knowledge = PlanetKnowledgeState.Detected;
        return site;
    }


    public override bool IsSiteStillValid(EventSite site)
    {
        return asteroidField != null && asteroidField.HasAsteroidAtCell(site.Cell);
    }
}
