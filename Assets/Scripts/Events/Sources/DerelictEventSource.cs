using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Arrival-driven source: empty, walkable cells near an asteroid field.
///
/// Each site spawns a hidden wreck/pod/station object. Knowledge follows live
/// vision (Partial = Detected "?", Full = Identified), driven by EventDirector.
/// On Identified the object is shown and a named fog lock (the site id) keeps it
/// visible on the map, exactly like EnemyShip's fog signature.
/// </summary>
public class DerelictEventSource : EventSiteSourceBase
{
    [Header("World References")]
    [SerializeField] private GridMap gridMap;
    [SerializeField] private AsteroidFieldPainter asteroidField;
    [SerializeField] private PlanetManager planetManager;
    [SerializeField] private EnemyShipRegistry enemyRegistry;
    [SerializeField] private VisionManager visionManager;
    [SerializeField] private FogOfWar fogOfWar;

    [Header("Spawned Objects")]
    [Tooltip("Parent for spawned derelict objects. Empty = scene root.")]
    [SerializeField] private Transform spawnedObjectRoot;

    [SortingLayerName]
    [SerializeField] private string identifiedSortingLayer = "Default";
    [SerializeField] private int identifiedSortingOrder = 9;

    [Tooltip("Fog tier the named lock keeps around an identified derelict.")]
    [SerializeField] private FogOfWar.VisibilityTier identifiedFogLockTier = FogOfWar.VisibilityTier.Partial;

    public override EventCategory Category => EventCategory.Derelict;

    private EventSiteRegistry boundRegistry;


    private void Reset()
    {
        sourceId = "derelict";
        rollChance = 0.04f;
        rerollCooldownTurns = 8;
        maxNewSitesPerScan = 1;
        eventTags = new List<string> { "derelict" };
    }


    public override void Bind(EventDirector director)
    {
        base.Bind(director);

        if (gridMap == null) gridMap = FindFirstObjectByType<GridMap>();
        if (asteroidField == null) asteroidField = FindFirstObjectByType<AsteroidFieldPainter>();
        if (planetManager == null) planetManager = FindFirstObjectByType<PlanetManager>();
        if (enemyRegistry == null) enemyRegistry = FindFirstObjectByType<EnemyShipRegistry>();
        if (visionManager == null) visionManager = director != null ? director.VisionManager : null;
        if (fogOfWar == null) fogOfWar = director != null ? director.FogOfWar : null;

        if (boundRegistry != null) boundRegistry.SiteKnowledgeChanged -= HandleKnowledgeChanged;
        boundRegistry = director != null ? director.Registry : null;
        if (boundRegistry != null) boundRegistry.SiteKnowledgeChanged += HandleKnowledgeChanged;
    }


    private void OnDestroy()
    {
        if (boundRegistry != null) boundRegistry.SiteKnowledgeChanged -= HandleKnowledgeChanged;
    }


    public override void CollectCandidates(Vector3Int centre, int radius, List<Vector3Int> results)
    {
        if (gridMap == null || asteroidField == null) return;

        int near = Director != null ? Director.Tuning.nearAsteroidRadius : 2;
        Vector3Int playerCell = player != null ? player.CurrentCell : centre;

        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                Vector3Int cell = new Vector3Int(centre.x + x, centre.y + y, 0);
                if (IsLegalSpawnCell(cell, playerCell, near)) results.Add(cell);
            }
        }
    }


    private bool IsLegalSpawnCell(Vector3Int cell, Vector3Int playerCell, int nearAsteroidRadius)
    {
        if (cell.x == playerCell.x && cell.y == playerCell.y) return false;
        if (!gridMap.IsWalkable(cell)) return false;
        if (asteroidField.HasAsteroidAtCell(cell)) return false;
        if (planetManager != null && planetManager.TryGetPlanetAtCell(cell, out _)) return false;
        if (enemyRegistry != null && enemyRegistry.IsOccupied(cell)) return false;
        return asteroidField.HasTilesInSquare(cell, Mathf.Max(0, nearAsteroidRadius));
    }


    public override EventSite CreateSite(Vector3Int cell, EventDefinition definition, int turn)
    {
        EventSite site = new EventSite(MakeSiteId(cell, turn), cell, definition, this, turn);
        site.SpawnedObject = SpawnObject(site);
        return site;
    }


    private GameObject SpawnObject(EventSite site)
    {
        if (gridMap == null) return null;

        GameObject go = new GameObject($"Derelict {site.Definition.DisplayName} ({site.Cell.x},{site.Cell.y})");
        go.transform.SetParent(spawnedObjectRoot, false);
        go.transform.position = gridMap.CellToWorld(site.Cell);

        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = site.Definition.IdentifiedSprite != null
            ? site.Definition.IdentifiedSprite
            : PixelGlyphFactory.GetWreckPlaceholder();
        sr.sortingLayerName = identifiedSortingLayer;
        sr.sortingOrder = identifiedSortingOrder;

        go.SetActive(false);   // hidden until identified
        return go;
    }


    private void HandleKnowledgeChanged(EventSite site, PlanetKnowledgeState knowledge)
    {
        if (site == null || !ReferenceEquals(site.Source, this)) return;
        if (knowledge != PlanetKnowledgeState.Identified) return;

        if (site.SpawnedObject != null) site.SpawnedObject.SetActive(true);
        if (fogOfWar != null) fogOfWar.SetLockedLocation(site.Id, site.Cell, identifiedFogLockTier);
    }


    public override bool IsSiteStillValid(EventSite site)
    {
        if (gridMap != null && !gridMap.IsWalkable(site.Cell)) return false;
        return site.SpawnedObject != null || gridMap == null;
    }


    public override void OnSiteRemoved(EventSite site)
    {
        if (fogOfWar != null) fogOfWar.RemoveLockedLocation(site.Id);
        if (site.SpawnedObject != null)
        {
            Destroy(site.SpawnedObject);
            site.SpawnedObject = null;
        }
    }
}
