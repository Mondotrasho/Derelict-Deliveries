using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

/// <summary>
/// Attach this to a GameObject childed to your Grid, with its own Tilemap
/// (separate from the background/star tilemaps) assigned or on this object.
/// Generates clustered asteroid fields, supports spawning debris around a
/// point (e.g. something blew up), consuming/removing tiles, and querying
/// whether any asteroid tiles exist inside a given square area.
/// Can also preview/bake the field in edit mode via the custom inspector.
/// </summary>
[RequireComponent(typeof(Tilemap))]
public class AsteroidFieldPainter : MonoBehaviour
{
    [System.Serializable]
    public class AsteroidTileOption
    {
        public Tile tile;
        [Tooltip("Relative chance this asteroid tile gets picked vs the other tiles. Doesn't need to add to 100 - it's normalised automatically.")]
        [Min(0f)] public float weight = 1f;
    }

    [Header("Target")]
    [Tooltip("The asteroid tilemap this script paints to. Leave empty to use the Tilemap on this GameObject.")]
    public Tilemap asteroidTilemap;

    [Header("Asteroid Tiles (9 different sizes/variants expected)")]
    public AsteroidTileOption[] asteroidTiles = new AsteroidTileOption[9];

    [Header("Field Area")]
    [Tooltip("Bottom-left cell of the region to scatter the field across.")]
    public Vector3Int origin = new Vector3Int(-25, -25, 0);
    [Tooltip("Width/height in cells of the region to scatter the field across.")]
    public Vector2Int size = new Vector2Int(50, 50);

    [Header("Density & Clustering")]
    [Range(0f, 1f)] public float density = 0.12f;
    [Tooltip("Bigger = bigger, smoother clusters. Smaller = tighter, noisier clumps.")]
    public float clusterNoiseScale = 0.1f;
    [Tooltip("Cells below this noise value never get an asteroid, regardless of density.")]
    [Range(0f, 1f)] public float clusterThreshold = 0.5f;
    [Tooltip("How strongly clusters bias placement above the threshold. Higher = tighter, denser clumps rather than an even scatter.")]
    public float clusterInfluence = 2f;

    [Header("Rotation")]
    [Tooltip("Allowed rotation steps in degrees, or leave just {0} to disable.")]
    public float[] allowedRotations = { 0f, 90f, 180f, 270f };

    [Header("Seeding")]
    public int seed = 54321;
    [Tooltip("If true, randomises the seed on Awake instead of using the value above. Note: the edit-mode preview can't match a seed that doesn't exist yet.")]
    public bool randomizeSeedOnAwake = false;

    [Header("Debug")]
    [Tooltip("If true, automatically calls GenerateField() when you press Play.")]
    public bool generateOnStart = false;

    [Header("Editor Preview")]
    [Tooltip("If true, the field regenerates in the Scene view whenever you change a setting in the Inspector (edit mode only).")]
    public bool livePreview = true;
    [Tooltip("Draws the field area outline in the Scene view when this object is selected.")]
    public bool showAreaGizmo = true;

    // Remembers what was last painted so moving/shrinking the area doesn't leave orphaned tiles behind.
    [SerializeField, HideInInspector] private BoundsInt _lastGeneratedArea;
    [SerializeField, HideInInspector] private bool _hasGeneratedArea;

    private System.Random _rng;
    private float _noiseOffsetX;
    private float _noiseOffsetY;

    private BoundsInt CurrentArea =>
        new BoundsInt(origin.x, origin.y, origin.z, Mathf.Max(0, size.x), Mathf.Max(0, size.y), 1);

    private void Awake()
    {
        ResolveTilemap();

        if (randomizeSeedOnAwake)
            seed = System.Guid.NewGuid().GetHashCode();

        ResetRng();
    }

    private void Start()
    {
        if (generateOnStart)
            GenerateField();
    }

    // ==================== Generation ====================

    /// <summary>
    /// Clears the target area and scatters a fresh clustered asteroid field
    /// using the current seed/density/weights. Works in edit mode too.
    /// </summary>
    [ContextMenu("Generate Field")]
    public void GenerateField()
    {
        if (!ResolveTilemap())
        {
            Debug.LogWarning($"{name}: no Tilemap assigned to AsteroidFieldPainter.", this);
            return;
        }

        float totalWeight = TotalWeight();
        if (totalWeight <= 0f)
        {
            Debug.LogWarning($"{name}: no valid asteroid tiles with weight > 0.", this);
            return;
        }

        // In edit mode, always start from the seed so the preview is exactly what Play will produce.
        // In play mode, keep the existing behaviour (the RNG carries on from where it was).
        if (!Application.isPlaying || _rng == null)
            ResetRng();

        ClearArea();

        for (int x = 0; x < size.x; x++)
        {
            for (int y = 0; y < size.y; y++)
            {
                Vector3Int cell = origin + new Vector3Int(x, y, 0);

                float cluster = ClusterNoise(cell.x, cell.y);
                if (cluster < clusterThreshold)
                    continue;

                float clusterStrength = Mathf.InverseLerp(clusterThreshold, 1f, cluster);
                float placementChance = density * Mathf.Pow(clusterStrength, clusterInfluence == 0 ? 1f : 1f / Mathf.Max(0.01f, clusterInfluence));

                if ((float)_rng.NextDouble() > placementChance)
                    continue;

                Tile tile = PickWeightedTile(totalWeight);
                if (tile == null)
                    continue;

                asteroidTilemap.SetTile(cell, tile);
                asteroidTilemap.SetTransformMatrix(cell, BuildRotationMatrix());
            }
        }

        _lastGeneratedArea = CurrentArea;
        _hasGeneratedArea = true;
    }

    /// <summary>Clears the current field area, plus wherever the field was last generated if that's different.</summary>
    [ContextMenu("Clear Field")]
    public void ClearArea()
    {
        if (!ResolveTilemap()) return;

        ClearRegion(CurrentArea);

        if (_hasGeneratedArea)
        {
            ClearRegion(_lastGeneratedArea);
            _hasGeneratedArea = false;
        }
    }

    private void ClearRegion(BoundsInt area)
    {
        int count = area.size.x * area.size.y * area.size.z;
        if (count <= 0) return;

        // A block of nulls clears the whole region in one call rather than one SetTile per cell.
        asteroidTilemap.SetTilesBlock(area, new TileBase[count]);
    }

    // ==================== Debris / Add ====================

    /// <summary>
    /// Scatters new asteroid tiles in a rough circle around a world-space point -
    /// e.g. debris left behind after something blows up there. By default this
    /// only fills currently-empty cells so it doesn't stomp existing asteroids;
    /// pass overwriteExisting: true if you want it to replace what's there too.
    /// </summary>
    public void AddAsteroidsAtPoint(Vector3 worldPosition, int count, float radiusInCells, bool overwriteExisting = false)
    {
        if (!ResolveTilemap()) return;
        EnsureRng();

        float totalWeight = TotalWeight();
        if (totalWeight <= 0f) return;

        Vector3Int centerCell = asteroidTilemap.WorldToCell(worldPosition);
        int radiusCeil = Mathf.CeilToInt(radiusInCells);

        int placed = 0;
        int attempts = 0;
        int maxAttempts = count * 8; // avoid an infinite loop if the area is already packed

        while (placed < count && attempts < maxAttempts)
        {
            attempts++;

            int dx = _rng.Next(-radiusCeil, radiusCeil + 1);
            int dy = _rng.Next(-radiusCeil, radiusCeil + 1);
            if (dx * dx + dy * dy > radiusInCells * radiusInCells)
                continue;

            Vector3Int cell = centerCell + new Vector3Int(dx, dy, 0);

            if (!overwriteExisting && asteroidTilemap.GetTile(cell) != null)
                continue;

            Tile tile = PickWeightedTile(totalWeight);
            if (tile == null) continue;

            asteroidTilemap.SetTile(cell, tile);
            asteroidTilemap.SetTransformMatrix(cell, BuildRotationMatrix());
            placed++;
        }
    }

    /// <summary>Same as AddAsteroidsAtPoint but takes a cell coordinate directly instead of a world position.</summary>
    public void AddAsteroidsAtCell(Vector3Int centerCell, int count, float radiusInCells, bool overwriteExisting = false)
    {
        if (!ResolveTilemap()) return;
        AddAsteroidsAtPoint(asteroidTilemap.GetCellCenterWorld(centerCell), count, radiusInCells, overwriteExisting);
    }

    // ==================== Query / Consume ====================

    /// <summary>
    /// True when the requested grid cell currently contains an asteroid tile.
    /// This is the simplest per-cell query for movement hazards, mining or
    /// event placement code.
    /// </summary>
    public bool HasAsteroidAtCell(Vector3Int cell)
    {
        return asteroidTilemap != null &&
               asteroidTilemap.GetTile(cell) != null;
    }

    /// <summary>
    /// Snapshot of every currently occupied asteroid cell in the Tilemap.
    /// The returned list is independent of the painter and may be filtered or
    /// cached by the caller without modifying asteroid state.
    /// </summary>
    public List<Vector3Int> GetAllAsteroidCells()
    {
        List<Vector3Int> result = new List<Vector3Int>();

        if (asteroidTilemap == null)
        {
            return result;
        }

        foreach (Vector3Int cell in asteroidTilemap.cellBounds.allPositionsWithin)
        {
            if (asteroidTilemap.GetTile(cell) != null)
            {
                result.Add(cell);
            }
        }

        return result;
    }

    /// <summary>True if any asteroid tile exists anywhere inside the given cell-space square/rectangle.</summary>
    public bool HasTilesInArea(BoundsInt area)
    {
        if (asteroidTilemap == null) return false;

        foreach (var cell in area.allPositionsWithin)
            if (asteroidTilemap.GetTile(cell) != null)
                return true;

        return false;
    }

    /// <summary>True if any asteroid tile exists within radius cells (a square, not a circle) of centerCell.</summary>
    public bool HasTilesInSquare(Vector3Int centerCell, int radius)
    {
        var area = new BoundsInt(centerCell.x - radius, centerCell.y - radius, 0, radius * 2 + 1, radius * 2 + 1, 1);
        return HasTilesInArea(area);
    }

    /// <summary>Returns every occupied cell inside the given area.</summary>
    public List<Vector3Int> GetTilesInArea(BoundsInt area)
    {
        var result = new List<Vector3Int>();
        if (asteroidTilemap == null) return result;

        foreach (var cell in area.allPositionsWithin)
            if (asteroidTilemap.GetTile(cell) != null)
                result.Add(cell);

        return result;
    }

    /// <summary>
    /// Removes ("consumes") any asteroid tiles inside the given area.
    /// Retained for existing callers; use ConsumeAreaAndCount when the caller
    /// needs to know how much material was actually present.
    /// </summary>
    public void ConsumeArea(BoundsInt area)
    {
        ConsumeAreaAndCount(area);
    }

    /// <summary>
    /// Removes asteroid tiles inside area and returns the number actually
    /// removed. Useful for mining/reward code without requiring it to query
    /// and then mutate the field in two separate passes.
    /// </summary>
    public int ConsumeAreaAndCount(BoundsInt area)
    {
        if (asteroidTilemap == null)
        {
            return 0;
        }

        int consumed = 0;

        foreach (Vector3Int cell in area.allPositionsWithin)
        {
            if (TryConsumeCell(cell))
            {
                consumed++;
            }
        }

        return consumed;
    }

    /// <summary>
    /// Removes ("consumes") the asteroid tile at a single cell, if any.
    /// Retained for existing callers that do not need a result.
    /// </summary>
    public void ConsumeCell(Vector3Int cell)
    {
        TryConsumeCell(cell);
    }

    /// <summary>
    /// Removes one asteroid cell and returns true only when a tile was actually
    /// present and consumed. This is the preferred mutation API for mining and
    /// one-off world events.
    /// </summary>
    public bool TryConsumeCell(Vector3Int cell)
    {
        if (!HasAsteroidAtCell(cell))
        {
            return false;
        }

        asteroidTilemap.SetTile(cell, null);
        return true;
    }

    // ==================== Internals ====================

    private bool ResolveTilemap()
    {
        if (asteroidTilemap == null)
            asteroidTilemap = GetComponent<Tilemap>();
        return asteroidTilemap != null;
    }

    private void ResetRng()
    {
        _rng = new System.Random(seed);
        _noiseOffsetX = (float)_rng.NextDouble() * 10000f;
        _noiseOffsetY = (float)_rng.NextDouble() * 10000f;
    }

    private void EnsureRng()
    {
        if (_rng == null)
            ResetRng();
    }

    private float TotalWeight()
    {
        float total = 0f;
        if (asteroidTiles == null) return total;

        foreach (var option in asteroidTiles)
            if (option != null && option.tile != null)
                total += option.weight;

        return total;
    }

    private Tile PickWeightedTile(float totalWeight)
    {
        float roll = (float)_rng.NextDouble() * totalWeight;
        float accum = 0f;

        foreach (var option in asteroidTiles)
        {
            if (option == null || option.tile == null) continue;
            accum += option.weight;
            if (roll <= accum)
                return option.tile;
        }

        foreach (var option in asteroidTiles)
            if (option != null && option.tile != null)
                return option.tile;

        return null;
    }

    private Matrix4x4 BuildRotationMatrix()
    {
        float angle = 0f;
        if (allowedRotations != null && allowedRotations.Length > 0)
            angle = allowedRotations[_rng.Next(0, allowedRotations.Length)];

        return Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 0f, angle), Vector3.one);
    }

    private float ClusterNoise(int cellX, int cellY)
    {
        float sampleX = (cellX * clusterNoiseScale) + _noiseOffsetX;
        float sampleY = (cellY * clusterNoiseScale) + _noiseOffsetY;
        return Mathf.PerlinNoise(sampleX, sampleY);
    }

    private void OnDrawGizmosSelected()
    {
        if (!showAreaGizmo || !ResolveTilemap()) return;

        Vector3 min = asteroidTilemap.CellToWorld(origin);
        Vector3 max = asteroidTilemap.CellToWorld(origin + new Vector3Int(size.x, size.y, 0));

        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.8f);
        Gizmos.DrawWireCube((min + max) * 0.5f, max - min);
    }
}

#if UNITY_EDITOR
/// <summary>
/// Inspector for AsteroidFieldPainter: Generate/Clear/New Seed buttons plus
/// live regeneration in edit mode when any value changes.
/// </summary>
[CustomEditor(typeof(AsteroidFieldPainter))]
public class AsteroidFieldPainterEditor : Editor
{
    private void OnEnable()
    {
        Undo.undoRedoPerformed += OnUndoRedo;
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnUndoRedo;
    }

    // Undoing a slider change doesn't go through OnInspectorGUI's change check,
    // so re-sync the preview here or the tiles drift out of step with the values.
    private void OnUndoRedo()
    {
        var painter = target as AsteroidFieldPainter;
        if (painter != null && painter.livePreview && !Application.isPlaying)
            Regenerate(painter, recordUndo: false);
    }

    public override void OnInspectorGUI()
    {
        var painter = (AsteroidFieldPainter)target;

        EditorGUI.BeginChangeCheck();
        DrawDefaultInspector();
        bool changed = EditorGUI.EndChangeCheck();

        EditorGUILayout.Space();

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Generate Field"))
                Regenerate(painter, recordUndo: true);

            if (GUILayout.Button("Clear Field"))
            {
                RecordUndo(painter, "Clear Asteroid Field");
                painter.ClearArea();
                MarkDirty(painter);
            }
        }

        if (GUILayout.Button("New Seed + Generate"))
        {
            Undo.RecordObject(painter, "Randomise Asteroid Seed");
            painter.seed = System.Guid.NewGuid().GetHashCode();
            Regenerate(painter, recordUndo: true);
        }

        // Skip undo recording on live tweaks - a full tilemap snapshot per slider tick gets heavy fast.
        if (changed && painter.livePreview && !Application.isPlaying)
            Regenerate(painter, recordUndo: false);
    }

    private static void Regenerate(AsteroidFieldPainter painter, bool recordUndo)
    {
        if (recordUndo)
            RecordUndo(painter, "Generate Asteroid Field");

        painter.GenerateField();
        MarkDirty(painter);
    }

    private static void RecordUndo(AsteroidFieldPainter painter, string label)
    {
        if (Application.isPlaying) return;

        var tilemap = painter.asteroidTilemap != null ? painter.asteroidTilemap : painter.GetComponent<Tilemap>();
        if (tilemap != null)
            Undo.RegisterCompleteObjectUndo(new Object[] { tilemap, painter }, label);
    }

    private static void MarkDirty(AsteroidFieldPainter painter)
    {
        if (Application.isPlaying) return;

        if (painter.asteroidTilemap != null)
            EditorUtility.SetDirty(painter.asteroidTilemap);
        EditorUtility.SetDirty(painter);
        EditorSceneManager.MarkSceneDirty(painter.gameObject.scene);
    }
}
#endif