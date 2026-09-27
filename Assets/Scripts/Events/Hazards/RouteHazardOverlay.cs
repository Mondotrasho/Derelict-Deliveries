using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Marks the risky cells along the player's route with a tile sprite. Hazard
/// tiles are always drawn UNDER FogOfWar, while the normal dotted route remains
/// free to draw above the fog. Covers the fresh plan, the part of the current
/// segment still to fly, and the queued remainder - the same cells
/// RoutePathRenderer draws. Presentation only: it never decides whether a
/// hazard fires.
///
/// No sprite asset is needed: leave Hazard Sprite empty and a pixel tile is
/// generated from the Generated Tile settings (pattern, size, spacing). Assign a
/// sprite later and it replaces the generated one.
/// </summary>
public class RouteHazardOverlay : MonoBehaviour
{
    public enum GeneratedPattern
    {
        DiagonalHatch,
        CrossHatch,
        Border,
        BorderAndHatch,
        Dots,
        Solid
    }

    [Header("Route Sources")]
    [SerializeField] private RoutePlanner routePlanner;
    [SerializeField] private MovementPlanController movementPlanController;
    [SerializeField] private PlayerGridController playerController;
    [SerializeField] private GridMap gridMap;
    [SerializeField] private HazardEventController hazards;

    [Tooltip("Used to keep hazard stripes below the generated fog. Found automatically if empty.")]
    [SerializeField] private FogOfWar fogOfWar;

    [Header("Look")]
    [Tooltip("Optional tile sprite drawn on hazard cells. Empty = use the Generated Tile below. Scaled to fit one tile.")]
    [SerializeField] private Sprite hazardSprite;

    [Header("Generated Tile (used when Hazard Sprite is empty)")]
    [SerializeField] private GeneratedPattern pattern = GeneratedPattern.BorderAndHatch;

    [Tooltip("Width/height of the generated tile in pixels. Match your tile art (8).")]
    [Range(4, 32)]
    [SerializeField] private int tilePixels = 8;

    [Tooltip("Pixels between hatch lines / dots.")]
    [Range(2, 16)]
    [SerializeField] private int spacing = 4;

    [Tooltip("Border thickness in pixels (Border patterns).")]
    [Range(1, 4)]
    [SerializeField] private int borderPixels = 1;
    [SerializeField] private Color tint = new Color(1f, 0.45f, 0.25f, 0.55f);

    [Header("Fallback Sorting (only used if FogOfWar is unavailable)")]
    [SortingLayerName]
    [SerializeField] private string sortingLayerName = "Foreground";

    [Tooltip("Fallback sorting order only. With FogOfWar available, hazards are forced to one order below its lowest fog layer.")]
    [SerializeField] private int sortingOrder = 9;

    private const string RootName = "Route Hazards (Generated)";

    private readonly List<SpriteRenderer> pool = new List<SpriteRenderer>();
    private readonly List<Vector3Int> cells = new List<Vector3Int>();
    private readonly HashSet<Vector3Int> seen = new HashSet<Vector3Int>();
    private Transform root;
    private int lastSignature;


    private void Awake()
    {
        if (routePlanner == null) routePlanner = FindFirstObjectByType<RoutePlanner>();
        if (movementPlanController == null) movementPlanController = FindFirstObjectByType<MovementPlanController>();
        if (playerController == null) playerController = FindFirstObjectByType<PlayerGridController>();
        if (gridMap == null) gridMap = FindFirstObjectByType<GridMap>();
        if (hazards == null) hazards = FindFirstObjectByType<HazardEventController>();
        if (fogOfWar == null) fogOfWar = FindFirstObjectByType<FogOfWar>();
    }


    private void OnDisable()
    {
        Show(0);
        lastSignature = 0;
    }


    private void LateUpdate()
    {
        CollectHazardCells();
        GetSorting(out string layer, out int order);

        int signature = 17;
        foreach (Vector3Int c in cells) signature = signature * 31 + c.GetHashCode();
        signature = signature * 31 + cells.Count;
        signature = signature * 31 + layer.GetHashCode();
        signature = signature * 31 + order;
        if (signature == lastSignature) return;
        lastSignature = signature;

        Redraw(layer, order);
    }


    private void CollectHazardCells()
    {
        cells.Clear();
        seen.Clear();
        if (hazards == null || !hazards.HazardsEnabled) return;

        if (playerController != null) AddHazards(playerController.RemainingCommandedPath);
        if (routePlanner != null && routePlanner.HasPlannedRoute) AddHazards(routePlanner.PlannedPath);
        if (movementPlanController != null) AddHazards(movementPlanController.QueuedRemainder);
    }


    private void AddHazards(IReadOnlyList<Vector3Int> path)
    {
        if (path == null) return;
        foreach (Vector3Int cell in path)
        {
            if (seen.Add(cell) && hazards.IsHazardCell(cell)) cells.Add(cell);
        }
    }


    private void Redraw(string layer, int order)
    {
        if (gridMap == null) return;
        if (root == null) root = new GameObject(RootName).transform;

        Sprite sprite = hazardSprite != null ? hazardSprite : GetGeneratedSprite();
        float tile = TileWorldSize();
        float spriteSize = sprite != null ? Mathf.Max(0.0001f, sprite.bounds.size.x) : 1f;
        float scale = tile / spriteSize;

        for (int i = 0; i < cells.Count; i++)
        {
            SpriteRenderer sr = Get(i);
            sr.sprite = sprite;
            sr.color = tint;
            sr.sortingLayerName = layer;
            sr.sortingOrder = order;
            sr.transform.position = gridMap.CellToWorld(cells[i]);
            sr.transform.localScale = new Vector3(scale, scale, 1f);
        }

        Show(cells.Count);
    }


    /// <summary>
    /// Fog owns the final sorting decision. Matching its sorting layer is
    /// important because Unity resolves Sorting Layers before sorting order;
    /// simply choosing a low order on a later layer can still draw over fog.
    /// </summary>
    private void GetSorting(out string layer, out int order)
    {
        if (fogOfWar != null)
        {
            layer = fogOfWar.FogSortingLayerName;
            order = fogOfWar.LowestFogSortingOrder - 1;
            return;
        }

        layer = sortingLayerName;
        order = sortingOrder;
    }


    private Sprite generatedSprite;
    private string generatedKey;


    /// <summary>Builds (and caches) a white pixel tile; Tint colours it.</summary>
    private Sprite GetGeneratedSprite()
    {
        int size = Mathf.Clamp(tilePixels, 4, 32);
        int gap = Mathf.Max(2, spacing);
        int border = Mathf.Clamp(borderPixels, 1, size / 2);
        string key = pattern + "/" + size + "/" + gap + "/" + border;
        if (generatedSprite != null && generatedKey == key) return generatedSprite;

        string[] rows = new string[size];
        for (int row = 0; row < size; row++)
        {
            char[] line = new char[size];
            for (int x = 0; x < size; x++)
            {
                int y = size - 1 - row;   // rows are top-first
                bool onBorder = x < border || y < border || x >= size - border || y >= size - border;
                bool diag = (x + y) % gap == 0;
                bool anti = ((x - y) % gap + gap) % gap == 0;
                bool dot = x % gap == gap / 2 && y % gap == gap / 2;

                bool on;
                switch (pattern)
                {
                    case GeneratedPattern.CrossHatch: on = diag || anti; break;
                    case GeneratedPattern.Border: on = onBorder; break;
                    case GeneratedPattern.BorderAndHatch: on = onBorder || diag; break;
                    case GeneratedPattern.Dots: on = dot; break;
                    case GeneratedPattern.Solid: on = true; break;
                    default: on = diag; break;
                }
                line[x] = on ? '#' : '.';
            }
            rows[row] = new string(line);
        }

        generatedSprite = PixelGlyphFactory.GetSprite("route-hazard:" + key, rows, size);
        generatedKey = key;
        return generatedSprite;
    }


    private void OnValidate()
    {
        lastSignature = 0;   // redraw with the new look next frame
    }


    private SpriteRenderer Get(int index)
    {
        while (pool.Count <= index)
        {
            GameObject go = new GameObject("Hazard " + pool.Count);
            go.transform.SetParent(root, false);
            pool.Add(go.AddComponent<SpriteRenderer>());
        }
        return pool[index];
    }


    private void Show(int count)
    {
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i] != null) pool[i].gameObject.SetActive(i < count);
        }
    }


    private float TileWorldSize()
    {
        Vector3 a = gridMap.CellToWorld(Vector3Int.zero);
        Vector3 b = gridMap.CellToWorld(Vector3Int.right);
        return Mathf.Max(0.0001f, Vector3.Distance(a, b));
    }
}
