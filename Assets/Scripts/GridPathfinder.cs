using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Finds paths across the GridMap using A*.
///
/// This class only decides which cells make up the path.
/// It does not move the player.
///
/// Diagonal movement is supported alongside the original four orthogonal
/// directions. By default a diagonal step costs exactly twice an
/// orthogonal step - i.e. what two orthogonal moves would have cost
/// anyway - so diagonal movement is a shape/smoothness choice (fewer
/// turns, a nicer-looking route) rather than a raw distance shortcut.
/// Total route cost works out the same either way.
/// </summary>
public class GridPathfinder : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private GridMap gridMap;


    [Header("Diagonal Movement")]

    [SerializeField]
    private bool allowDiagonalMovement = true;

    [Tooltip("Cost of moving into one orthogonal (N/S/E/W) cell.")]
    [SerializeField]
    private int orthogonalStepCost = 1;

    [Tooltip("Cost of moving into one diagonal cell. Defaulting to 2x the orthogonal cost means a diagonal step costs exactly what two orthogonal steps would have - so it never provides a raw distance shortcut, only a smoother-looking route. Keep this in sync with MovementAllowance's matching field: GridPathfinder deliberately doesn't know about MovementAllowance, so the two costs are not shared automatically.")]
    [SerializeField]
    private int diagonalStepCost = 2;

    [Tooltip("If true, a diagonal move is blocked unless both flanking orthogonal cells are also walkable - stops the path clipping through a blocked corner.")]
    [SerializeField]
    private bool preventCornerCutting = true;

    [Tooltip("When a diagonal route and a staircase route cost exactly the same, prefer the diagonal one so paths don't default to zigzagging. Purely a shape preference - does not change what MovementAllowance charges.")]
    [SerializeField]
    private bool preferDiagonalMovement = true;


    /// <summary>
    /// The four orthogonal directions, always available.
    /// </summary>
    private static readonly Vector3Int[] OrthogonalDirections =
    {
        Vector3Int.up,
        Vector3Int.right,
        Vector3Int.down,
        Vector3Int.left
    };


    /// <summary>
    /// The four diagonal directions, only used when allowDiagonalMovement is true.
    /// </summary>
    private static readonly Vector3Int[] DiagonalDirections =
    {
        new Vector3Int(1, 1, 0),
        new Vector3Int(1, -1, 0),
        new Vector3Int(-1, -1, 0),
        new Vector3Int(-1, 1, 0)
    };


    /// <summary>
    /// Finds a path from startCell to destinationCell.
    ///
    /// The returned path does not include the starting cell.
    /// An empty list means either no movement is needed or no path was found.
    /// </summary>
    public List<Vector3Int> FindPath(
        Vector3Int startCell,
        Vector3Int destinationCell)
    {
        List<Vector3Int> emptyPath = new List<Vector3Int>();

        if (gridMap == null)
        {
            Debug.LogError("GridPathfinder has no GridMap assigned.");
            return emptyPath;
        }

        if (startCell == destinationCell)
        {
            return emptyPath;
        }

        if (!gridMap.IsWalkable(destinationCell))
        {
            Debug.LogWarning(
                $"Destination cell {destinationCell} is not walkable."
            );

            return emptyPath;
        }


        List<Vector3Int> directions =
            new List<Vector3Int>(OrthogonalDirections);

        if (allowDiagonalMovement)
        {
            directions.AddRange(DiagonalDirections);
        }


        List<PathNode> openNodes = new List<PathNode>();
        HashSet<Vector3Int> closedCells = new HashSet<Vector3Int>();

        Dictionary<Vector3Int, PathNode> knownNodes =
            new Dictionary<Vector3Int, PathNode>();


        PathNode startNode = new PathNode(startCell);

        startNode.GCost = 0;
        startNode.HCost = CalculateHeuristic(startCell, destinationCell);

        openNodes.Add(startNode);
        knownNodes.Add(startCell, startNode);


        while (openNodes.Count > 0)
        {
            PathNode currentNode = GetBestNode(openNodes);

            if (currentNode.Cell == destinationCell)
            {
                return BuildPath(currentNode);
            }

            openNodes.Remove(currentNode);
            closedCells.Add(currentNode.Cell);


            foreach (Vector3Int direction in directions)
            {
                Vector3Int neighbourCell =
                    currentNode.Cell + direction;


                if (closedCells.Contains(neighbourCell))
                {
                    continue;
                }

                if (!CanStepBetween(currentNode.Cell, neighbourCell))
                {
                    continue;
                }


                bool isDiagonalStep =
                    direction.x != 0 &&
                    direction.y != 0;

                int stepCost =
                    isDiagonalStep
                        ? diagonalStepCost
                        : orthogonalStepCost;

                int newGCost =
                    currentNode.GCost +
                    stepCost;


                if (!knownNodes.TryGetValue(
                    neighbourCell,
                    out PathNode neighbourNode))
                {
                    neighbourNode = new PathNode(neighbourCell);

                    neighbourNode.GCost = newGCost;
                    neighbourNode.HCost =
                        CalculateHeuristic(
                            neighbourCell,
                            destinationCell
                        );

                    neighbourNode.Parent = currentNode;

                    knownNodes.Add(neighbourCell, neighbourNode);
                    openNodes.Add(neighbourNode);

                    continue;
                }


                bool isCheaper =
                    newGCost < neighbourNode.GCost;

                // With diagonalStepCost at its default (2x orthogonal),
                // a diagonal route and an equivalent staircase route cost
                // exactly the same - so without a tie-break, whichever
                // shape reached a cell first "wins" and never gets
                // replaced (newGCost < GCost is strict). Since directions
                // are checked orthogonal-first, that meant staircases by
                // default. This tie-break lets an equal-cost diagonal
                // step take over from an existing orthogonal one, purely
                // for a smoother-looking route - it does not change the
                // cost charged by MovementAllowance, which prices the
                // final path's actual cell deltas independently.
                bool isEqualCostButSmoother =
                    preferDiagonalMovement &&
                    newGCost == neighbourNode.GCost &&
                    isDiagonalStep &&
                    !ArrivedDiagonally(neighbourNode);

                if (isCheaper || isEqualCostButSmoother)
                {
                    neighbourNode.GCost = newGCost;
                    neighbourNode.Parent = currentNode;

                    if (!openNodes.Contains(neighbourNode))
                    {
                        openNodes.Add(neighbourNode);
                    }
                }
            }
        }


        Debug.LogWarning(
            $"No path found from {startCell} to {destinationCell}."
        );

        return emptyPath;
    }


    /// <summary>
    /// True if this node's current best-known route arrives via a
    /// diagonal step from its parent.
    /// </summary>
    private bool ArrivedDiagonally(PathNode node)
    {
        if (node.Parent == null)
        {
            return false;
        }

        Vector3Int delta = node.Cell - node.Parent.Cell;

        return delta.x != 0 && delta.y != 0;
    }


    /// <summary>
    /// True if a single step from fromCell directly into toCell is legal
    /// under the current settings - walkable, diagonal-allowed if it's a
    /// diagonal step, and not cutting a blocked corner. This is the one
    /// place that defines "is this move legal", shared by the internal A*
    /// search and by RoutePlanner's manual drag-drawing (which appends
    /// single steps without running a full search).
    ///
    /// Does not check adjacency - fromCell and toCell are assumed to
    /// already be one step apart in some direction.
    /// </summary>
    public bool CanStepBetween(Vector3Int fromCell, Vector3Int toCell)
    {
        if (gridMap == null)
        {
            Debug.LogError("GridPathfinder has no GridMap assigned.");
            return false;
        }


        Vector3Int delta = toCell - fromCell;

        bool isDiagonalStep =
            delta.x != 0 &&
            delta.y != 0;

        if (isDiagonalStep && !allowDiagonalMovement)
        {
            return false;
        }

        if (!gridMap.IsWalkable(toCell))
        {
            return false;
        }

        if (isDiagonalStep && preventCornerCutting)
        {
            Vector3Int horizontalNeighbour =
                fromCell +
                new Vector3Int(delta.x, 0, 0);

            Vector3Int verticalNeighbour =
                fromCell +
                new Vector3Int(0, delta.y, 0);

            bool cornerBlocked =
                !gridMap.IsWalkable(horizontalNeighbour) ||
                !gridMap.IsWalkable(verticalNeighbour);

            if (cornerBlocked)
            {
                return false;
            }
        }

        return true;
    }


    /// <summary>
    /// Chooses the most promising open node.
    /// </summary>
    private PathNode GetBestNode(List<PathNode> openNodes)
    {
        PathNode bestNode = openNodes[0];

        for (int i = 1; i < openNodes.Count; i++)
        {
            PathNode candidate = openNodes[i];

            if (candidate.FCost < bestNode.FCost)
            {
                bestNode = candidate;
                continue;
            }

            if (candidate.FCost == bestNode.FCost &&
                candidate.HCost < bestNode.HCost)
            {
                bestNode = candidate;
            }
        }

        return bestNode;
    }


    /// <summary>
    /// Admissible distance estimate that accounts for diagonal movement.
    ///
    /// When diagonalStepCost is exactly 2x orthogonalStepCost (the
    /// default), this reduces to plain Manhattan distance - which also
    /// happens to be the *exact* real cost of any route between the two
    /// cells, diagonal shortcuts or not, since a diagonal step costs the
    /// same as the two orthogonal steps it replaces. If diagonalStepCost
    /// is set cheaper than that, the heuristic correctly accounts for the
    /// resulting shortcut; if set more expensive, the extra term is
    /// clamped so the heuristic never overestimates (which would break
    /// A*'s guarantee of finding the shortest path).
    /// </summary>
    private int CalculateHeuristic(
        Vector3Int firstCell,
        Vector3Int secondCell)
    {
        int dx = Mathf.Abs(firstCell.x - secondCell.x);
        int dy = Mathf.Abs(firstCell.y - secondCell.y);

        if (!allowDiagonalMovement)
        {
            return orthogonalStepCost * (dx + dy);
        }


        int diagonalSavingsPerStep =
            Mathf.Min(
                0,
                diagonalStepCost - (2 * orthogonalStepCost)
            );

        return (orthogonalStepCost * (dx + dy)) +
               (diagonalSavingsPerStep * Mathf.Min(dx, dy));
    }


    /// <summary>
    /// Rebuilds the final cell list by following parent links.
    /// </summary>
    private List<Vector3Int> BuildPath(PathNode destinationNode)
    {
        List<Vector3Int> path = new List<Vector3Int>();

        PathNode currentNode = destinationNode;

        while (currentNode.Parent != null)
        {
            path.Add(currentNode.Cell);
            currentNode = currentNode.Parent;
        }

        path.Reverse();

        return path;
    }


    /// <summary>
    /// Small internal data object used by A*.
    /// </summary>
    private class PathNode
    {
        public Vector3Int Cell { get; }

        public int GCost { get; set; }

        public int HCost { get; set; }

        public int FCost
        {
            get
            {
                return GCost + HCost;
            }
        }

        public PathNode Parent { get; set; }


        public PathNode(Vector3Int cell)
        {
            Cell = cell;

            GCost = int.MaxValue;
            HCost = 0;

            Parent = null;
        }
    }
}