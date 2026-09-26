using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Provides access to the game's Unity Grid and Tilemaps.
///
/// This class is responsible for:
/// - Converting between grid cells and world positions.
/// - Checking whether a grid cell can be travelled through.
/// - Keeping grid-related logic out of the player controller.
/// </summary>
public class GridMap : MonoBehaviour
{
    [Header("Grid References")]

    [Tooltip("The Unity Grid component used by the Tilemaps.")]
    [SerializeField]
    private Grid grid;

    [Tooltip("Cells containing tiles on this Tilemap are considered valid ground.")]
    [SerializeField]
    private Tilemap groundTilemap;

    [Header("Optional Obstacles")]

    [Tooltip("If assigned, cells containing tiles here are blocked.")]
    [SerializeField]
    private Tilemap obstacleTilemap;


    /// <summary>
    /// Converts a grid cell into the world position at the centre of that cell.
    /// </summary>
    public Vector3 CellToWorld(Vector3Int cell)
    {
        return grid.GetCellCenterWorld(cell);
    }


    /// <summary>
    /// Converts a world position into a grid cell coordinate.
    /// </summary>
    public Vector3Int WorldToCell(Vector3 worldPosition)
    {
        return grid.WorldToCell(worldPosition);
    }


    /// <summary>
    /// Returns true if the given cell can be used by the pathfinder.
    /// </summary>
    public bool IsWalkable(Vector3Int cell)
    {
        if (groundTilemap == null)
        {
            return false;
        }

        if (!groundTilemap.HasTile(cell))
        {
            return false;
        }

        if (obstacleTilemap == null)
        {
            return true;
        }

        return !obstacleTilemap.HasTile(cell);
    }


    /// <summary>True if the ground Tilemap has a tile here (obstacles ignored).</summary>
    public bool HasGround(Vector3Int cell)
    {
        return groundTilemap != null && groundTilemap.HasTile(cell);
    }


    /// <summary>Cell bounds of the ground Tilemap (the playable area).</summary>
    public BoundsInt GroundBounds
    {
        get { return groundTilemap != null ? groundTilemap.cellBounds : new BoundsInt(); }
    }


    /// <summary>
    /// A walkable cell on the edge of the map: at least one of its four
    /// neighbours has no ground at all (an obstacle does not count).
    /// </summary>
    public bool IsMapEdgeCell(Vector3Int cell)
    {
        if (!IsWalkable(cell))
        {
            return false;
        }

        return !HasGround(cell + Vector3Int.up) ||
               !HasGround(cell + Vector3Int.down) ||
               !HasGround(cell + Vector3Int.left) ||
               !HasGround(cell + Vector3Int.right);
    }


    /// <summary>
    /// Returns the grid cell currently occupied by a Transform.
    /// </summary>
    public Vector3Int GetCellForTransform(Transform target)
    {
        return WorldToCell(target.position);
    }


    /// <summary>
    /// Moves a Transform directly to the centre of a grid cell.
    /// </summary>
    public void SnapTransformToCell(Transform target, Vector3Int cell)
    {
        target.position = CellToWorld(cell);
    }
}
