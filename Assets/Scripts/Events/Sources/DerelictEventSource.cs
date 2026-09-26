using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Arrival-driven source: empty, walkable cells near an asteroid field.
///
/// Each site spawns a hidden wreck/pod/station object. Knowledge follows live
/// vision (Partial = Detected "?", Full = Identified), driven by EventDirector.
/// On Identified the object is shown and a named fog lock (the site id) keeps it
/// visible on the map, exactly like EnemyShip's fog signature.
///
/// Look (set per event definition): the spawned sprite is scaled to fit its
/// Identified Size In Tiles whatever its pixels-per-unit, tinted, and slowly spins.
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

    [Header("Spawned Sprite Look (size, tint and spin are on each EventDefinition)")]
    [Tooltip("Each wreck spins clockwise or anticlockwise at random.")]
    [SerializeField] private bool randomSpinDirection = true;

    [Tooltip("Each wreck starts at a random angle so they don't all line up.")]
    [SerializeField] private bool randomStartAngle = true;

    [Tooltip("Fog tier the named lock keeps around an identified derelict.")]
    [SerializeField] private FogOfWar.VisibilityTier identifiedFogLockTier = FogOfWar.VisibilityTier.Partial;

    public override EventCategory Category => EventCategory.Derelict;

    private EventSiteRegistry boundRegistry;

    private struct Spinner
    {
        public Transform transform;
        public float degreesPerSecond;   // signed: direction included
    }

    private readonly List<Spinner> spinners = new List<Spinner>();


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


    private void Update()
    {
        if (spinners.Count == 0) return;

        float dt = Time.deltaTime;
        for (int i = spinners.Count - 1; i >= 0; i--)
        {
            Transform t = spinners[i].transform;
            if (t == null)
            {
                spinners.RemoveAt(i);   // site removed, object destroyed
                continue;
            }

            if (spinners[i].degreesPerSecond != 0f && t.gameObject.activeInHierarchy)
            {
                t.Rotate(0f, 0f, spinners[i].degreesPerSecond * dt);
            }
        }
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

        // Tint RGB only: EventMarkerPresenter drives alpha for the identify fade-in.
        EventDefinition def = site.Definition;
        Color tint = def.IdentifiedTint;
        tint.a = 1f;
        sr.color = tint;

        // Fit one tile (times sizeInTiles) regardless of the sprite's pixels-per-unit.
        if (sr.sprite != null)
        {
            Vector3 spriteSize = sr.sprite.bounds.size;
            float largest = Mathf.Max(spriteSize.x, spriteSize.y);
            if (largest > 0.0001f)
            {
                float scale = TileWorldSize() * def.IdentifiedSizeInTiles / largest;
                go.transform.localScale = new Vector3(scale, scale, 1f);
            }
        }

        if (randomStartAngle) go.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
        float direction = randomSpinDirection && Random.value < 0.5f ? -1f : 1f;
        spinners.Add(new Spinner { transform = go.transform, degreesPerSecond = def.IdentifiedSpinDegreesPerSecond * direction });

        go.SetActive(false);   // hidden until identified
        return go;
    }


    private float TileWorldSize()
    {
        Vector3 a = gridMap.CellToWorld(Vector3Int.zero);
        Vector3 b = gridMap.CellToWorld(Vector3Int.right);
        return Mathf.Max(0.0001f, Vector3.Distance(a, b));
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
