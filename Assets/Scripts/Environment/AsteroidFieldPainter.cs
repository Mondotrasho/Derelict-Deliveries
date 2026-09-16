using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Attach this to a GameObject childed to your Grid, with its own Tilemap
/// (separate from the background/star tilemaps) assigned or on this object.
/// Generates clustered asteroid fields, supports spawning debris around a
/// point (e.g. something blew up), consuming/removing tiles, and querying
/// whether any asteroid tiles exist inside a given square area.
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
    [Tooltip("If true, randomises the seed on Awake instead of using the value above.")]
    public bool randomizeSeedOnAwake = false;

    [Header("Debug")]
    [Tooltip("If true, automatically calls GenerateField() when you press Play.")]
    public bool generateOnStart = false;

    private System.Random _rng;
    private float _noiseOffsetX;
    private float _noiseOffsetY;

    private void Awake()
    {
        if (asteroidTilemap == null)
            asteroidTilemap = GetComponent<Tilemap>();

        if (randomizeSeedOnAwake)
            seed = System.Guid.NewGuid().GetHashCode();

        _rng = new System.Random(seed);
        _noiseOffsetX = (float)_rng.NextDouble() * 10000f;
        _noiseOffsetY = (float)_rng.NextDouble() * 10000f;
    }

    private void Start()
    {
        if (generateOnStart)
            GenerateField();
    }

    // ==================== Generation ====================

    /// <summary>
    /// Clears the target area and scatters a fresh clustered asteroid field
    /// using the current seed/density/weights.
    /// </summary>
    [ContextMenu("Generate Field")]
    public void GenerateField()
    {
        if (asteroidTilemap == null)
        {
            Debug.LogWarning($"{name}: no Tilemap assigned to AsteroidFieldPainter.", this);
            return;
        }

        float totalWeight = 0f;
        foreach (var option in asteroidTiles)
            if (option != null && option.tile != null)
                totalWeight += option.weight;

        if (totalWeight <= 0f)
        {
            Debug.LogWarning($"{name}: no valid asteroid tiles with weight > 0.", this);
            return;
        }

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
    }

    [ContextMenu("Clear Field")]
    public void ClearArea()
    {
        if (asteroidTilemap == null) return;

        for (int x = 0; x < size.x; x++)
            for (int y = 0; y < size.y; y++)
                asteroidTilemap.SetTile(origin + new Vector3Int(x, y, 0), null);
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
        if (asteroidTilemap == null) return;

        float totalWeight = 0f;
        foreach (var option in asteroidTiles)
            if (option != null && option.tile != null)
                totalWeight += option.weight;

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
        if (asteroidTilemap == null) return;
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
}
