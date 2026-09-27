using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Faint green flood fill of every cell the ship can still reach this turn.
///
/// Uses exactly the rules the route uses: GridPathfinder.CanStepBetween for
/// what a legal step is (walkable, diagonals, no corner cutting) and
/// MovementAllowance.GetMovementCost for what it costs (cost modifiers such as
/// hazards included), searched outwards from the ship until the remaining
/// movement points run out. So a green cell is a cell a route can end on this
/// turn, never an approximation.
///
/// Shown during the player's phase while the ship is still; hidden while it
/// moves, during other phases, and when no movement is left. When it appears
/// it ripples outwards from the ship. Cells on the edge of the reachable area
/// are drawn a little brighter so the boundary reads clearly.
///
/// The generated tile is an 8x8 ordered-dither sprite rather than a solid wash.
/// This lets the colour itself stay reasonably opaque while the transparent
/// pixels control how visually heavy the overlay feels.
/// Presentation only: it never changes movement.
/// </summary>
[DisallowMultipleComponent]
public sealed class ReachOverlay : MonoBehaviour
{
    public enum DitherPattern
    {
        Sparse,
        Light,
        Medium,
        Half,
        Dense,
        Heavy,
        Solid
    }

    [Header("References (found automatically if empty)")]
    [SerializeField] private PlayerShipState player;
    [SerializeField] private GridMap gridMap;
    [SerializeField] private GridPathfinder pathfinder;
    [SerializeField] private MovementAllowance movementAllowance;
    [SerializeField] private TurnManager turnManager;

    [Header("Look")]
    [Tooltip("8x8 ordered-dither coverage. Sparse is about 12.5% filled, Light 25%, Medium 37.5%, Half 50%, Dense 62.5%, Heavy 75%, and Solid 100%.")]
    [SerializeField] private DitherPattern ditherPattern = DitherPattern.Light;

    [Tooltip("Colour used inside the reachable area. The dither controls most of the visual strength, so this no longer needs an extremely low alpha.")]
    [SerializeField] private Color fillColour = new Color(0.35f, 1f, 0.55f, 0.65f);

    [Tooltip("Colour used on reachable cells touching the boundary of the reachable area.")]
    [SerializeField] private Color edgeColour = new Color(0.35f, 1f, 0.55f, 0.95f);

    [Tooltip("Cells per second the reveal ripples outwards. 0 = appear at once.")]
    [Min(0f)] [SerializeField] private float rippleCellsPerSecond = 40f;
    [Min(0f)] [SerializeField] private float fadeSeconds = 0.15f;
    [SortingLayerName]
    [SerializeField] private string sortingLayerName = "Foreground";
    [Tooltip("Above the fog so you can see how far you can go into it; keep below the route itself.")]
    [SerializeField] private int sortingOrder = 60;

    [Header("When")]
    [SerializeField] private bool showWhileMoving = false;

    private struct Tile
    {
        public SpriteRenderer renderer;
        public float delay;     // seconds after the reveal starts
        public bool edge;
    }

    // Standard 8x8 Bayer matrix. Values below the selected threshold become
    // opaque pixels; the rest stay transparent. This gives a regular pixel-art
    // dither whose density can change without changing the tile dimensions.
    private static readonly int[,] Bayer8 =
    {
        {  0, 48, 12, 60,  3, 51, 15, 63 },
        { 32, 16, 44, 28, 35, 19, 47, 31 },
        {  8, 56,  4, 52, 11, 59,  7, 55 },
        { 40, 24, 36, 20, 43, 27, 39, 23 },
        {  2, 50, 14, 62,  1, 49, 13, 61 },
        { 34, 18, 46, 30, 33, 17, 45, 29 },
        { 10, 58,  6, 54,  9, 57,  5, 53 },
        { 42, 26, 38, 22, 41, 25, 37, 21 }
    };

    private static readonly Vector3Int[] Orthogonal =
        { Vector3Int.up, Vector3Int.down, Vector3Int.left, Vector3Int.right };
    private static readonly Vector3Int[] Diagonal =
        { new Vector3Int(1, 1, 0), new Vector3Int(1, -1, 0), new Vector3Int(-1, 1, 0), new Vector3Int(-1, -1, 0) };

    private readonly List<Tile> tiles = new List<Tile>();
    private readonly List<SpriteRenderer> pool = new List<SpriteRenderer>();
    private Transform root;
    private Sprite fillSprite;
    private DitherPattern fillSpritePattern;
    private bool dirty = true;
    private bool shown;
    private float revealStart;
    private Vector3Int lastCell;
    private int lastBudget = -1;


    private void Awake()
    {
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (gridMap == null) gridMap = FindFirstObjectByType<GridMap>();
        if (pathfinder == null) pathfinder = FindFirstObjectByType<GridPathfinder>();
        if (movementAllowance == null) movementAllowance = FindFirstObjectByType<MovementAllowance>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();

        root = new GameObject("Reach Overlay (Generated)").transform;
        root.SetParent(transform, false);
    }


    private void OnEnable()
    {
        if (movementAllowance != null)
        {
            movementAllowance.AllowanceChanged += MarkDirty;
            movementAllowance.MovementCostRulesChanged += MarkDirty;
        }
        if (turnManager != null) turnManager.PhaseChanged += HandlePhaseChanged;
        dirty = true;
    }


    private void OnDisable()
    {
        if (movementAllowance != null)
        {
            movementAllowance.AllowanceChanged -= MarkDirty;
            movementAllowance.MovementCostRulesChanged -= MarkDirty;
        }
        if (turnManager != null) turnManager.PhaseChanged -= HandlePhaseChanged;
        Hide();
    }


    private void OnValidate()
    {
        dirty = true;
        fillSprite = null;
    }


    private void MarkDirty() => dirty = true;
    private void HandlePhaseChanged(TurnPhase phase) => dirty = true;


    private void Update()
    {
        bool want = player != null && movementAllowance != null && pathfinder != null && gridMap != null
                    && (turnManager == null || turnManager.IsPlayerPhase)
                    && (showWhileMoving || !player.IsMoving)
                    && !player.IsMovementInterrupted
                    && movementAllowance.CurrentMovementPoints > 0;

        if (!want)
        {
            if (shown) Hide();
            return;
        }

        if (!shown || dirty || player.CurrentCell != lastCell || movementAllowance.CurrentMovementPoints != lastBudget)
        {
            Rebuild();
        }

        Animate();
    }


    // ---------------------------------------------------------------- search

    /// <summary>Cells reachable with the remaining points, with their cost.</summary>
    public Dictionary<Vector3Int, int> ComputeReachable()
    {
        var dist = new Dictionary<Vector3Int, int>();
        Vector3Int start = player.CurrentCell;
        int budget = movementAllowance.CurrentMovementPoints;
        dist[start] = 0;

        // Dijkstra over a plain list: budgets are a handful of points, so a
        // linear "cheapest next" scan is simpler than a heap and just as fast.
        var frontier = new List<KeyValuePair<Vector3Int, int>> { new KeyValuePair<Vector3Int, int>(start, 0) };
        while (frontier.Count > 0)
        {
            int best = 0;
            for (int i = 1; i < frontier.Count; i++)
            {
                if (frontier[i].Value < frontier[best].Value) best = i;
            }
            Vector3Int cell = frontier[best].Key;
            int cost = frontier[best].Value;
            frontier.RemoveAt(best);
            if (cost > dist[cell]) continue;              // stale entry

            foreach (Vector3Int step in Steps())
            {
                Vector3Int next = cell + step;
                if (!pathfinder.CanStepBetween(cell, next)) continue;
                int total = cost + Mathf.Max(1, movementAllowance.GetMovementCost(cell, next));
                if (total > budget) continue;
                if (dist.TryGetValue(next, out int known) && known <= total) continue;
                dist[next] = total;
                frontier.Add(new KeyValuePair<Vector3Int, int>(next, total));
            }
        }

        dist.Remove(start);
        return dist;
    }


    private IEnumerable<Vector3Int> Steps()
    {
        foreach (Vector3Int d in Orthogonal) yield return d;
        if (pathfinder.AllowDiagonalMovement)
        {
            foreach (Vector3Int d in Diagonal) yield return d;
        }
    }


    // -------------------------------------------------------------- drawing

    private void Rebuild()
    {
        dirty = false;
        bool wasShown = shown;
        lastCell = player.CurrentCell;
        lastBudget = movementAllowance.CurrentMovementPoints;
        fillSprite = GetFillSprite();

        Dictionary<Vector3Int, int> reach = ComputeReachable();
        foreach (Tile t in tiles) pool.Add(t.renderer);
        tiles.Clear();

        float tile = Vector3.Distance(gridMap.CellToWorld(Vector3Int.zero), gridMap.CellToWorld(Vector3Int.right));
        float scale = tile / Mathf.Max(0.0001f, fillSprite.bounds.size.x);
        Vector3 origin = gridMap.CellToWorld(player.CurrentCell);

        foreach (var kv in reach)
        {
            bool edge = false;
            foreach (Vector3Int d in Orthogonal)
            {
                Vector3Int n = kv.Key + d;
                if (n != player.CurrentCell && !reach.ContainsKey(n)) { edge = true; break; }
            }

            SpriteRenderer sr = TakeRenderer();
            sr.sprite = fillSprite;
            sr.transform.position = gridMap.CellToWorld(kv.Key);
            sr.transform.localScale = new Vector3(scale, scale, 1f);
            float distance = Vector3.Distance(origin, sr.transform.position) / Mathf.Max(0.0001f, tile);
            tiles.Add(new Tile
            {
                renderer = sr,
                edge = edge,
                // a rebuild while already shown (budget spent) updates in place, no second ripple
                delay = wasShown || rippleCellsPerSecond <= 0f ? 0f : distance / rippleCellsPerSecond
            });
        }

        foreach (SpriteRenderer spare in pool) spare.gameObject.SetActive(false);
        if (!wasShown) revealStart = Time.unscaledTime;
        shown = true;
    }


    private void Animate()
    {
        float t = Time.unscaledTime - revealStart;
        foreach (Tile tl in tiles)
        {
            float k = fadeSeconds <= 0f ? 1f : Mathf.Clamp01((t - tl.delay) / fadeSeconds);
            Color c = tl.edge ? edgeColour : fillColour;
            c.a *= k;
            tl.renderer.color = c;
        }
    }


    private void Hide()
    {
        foreach (Tile t in tiles)
        {
            t.renderer.gameObject.SetActive(false);
            pool.Add(t.renderer);
        }
        tiles.Clear();
        shown = false;
    }


    private SpriteRenderer TakeRenderer()
    {
        SpriteRenderer sr;
        if (pool.Count > 0)
        {
            sr = pool[pool.Count - 1];
            pool.RemoveAt(pool.Count - 1);
        }
        else
        {
            var go = new GameObject("Reach");
            go.transform.SetParent(root, false);
            sr = go.AddComponent<SpriteRenderer>();
        }

        // Apply these every time so Inspector changes affect already-pooled tiles.
        sr.sortingLayerName = sortingLayerName;
        sr.sortingOrder = sortingOrder;
        sr.gameObject.SetActive(true);
        return sr;
    }


    private Sprite GetFillSprite()
    {
        if (fillSprite != null && fillSpritePattern == ditherPattern)
            return fillSprite;

        fillSpritePattern = ditherPattern;
        fillSprite = PixelGlyphFactory.GetSprite(
            "reach-dither:" + ditherPattern,
            BuildDitherRows(ditherPattern),
            8);

        return fillSprite;
    }


    /// <summary>
    /// Builds one 8x8 ordered-dither tile. The matrix itself determines where
    /// pixels are placed; the selected pattern only changes how many survive.
    /// </summary>
    private static string[] BuildDitherRows(DitherPattern pattern)
    {
        int threshold = DitherThreshold(pattern);
        var rows = new string[8];

        for (int y = 0; y < 8; y++)
        {
            char[] line = new char[8];
            for (int x = 0; x < 8; x++)
            {
                line[x] = Bayer8[y, x] < threshold ? '#' : '.';
            }
            rows[y] = new string(line);
        }

        return rows;
    }


    private static int DitherThreshold(DitherPattern pattern)
    {
        switch (pattern)
        {
            case DitherPattern.Sparse: return 8;    // 12.5%
            case DitherPattern.Light: return 16;    // 25%
            case DitherPattern.Medium: return 24;   // 37.5%
            case DitherPattern.Half: return 32;     // 50%
            case DitherPattern.Dense: return 40;    // 62.5%
            case DitherPattern.Heavy: return 48;    // 75%
            case DitherPattern.Solid: return 64;    // 100%
            default: return 16;
        }
    }
}
