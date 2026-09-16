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

    [Tooltip("Cost of moving into one diagonal cell. Defaulting to 2x the orthogonal cost means a diagonal step costs exactly what two orthogonal steps would have - so it never provides a raw distance shortcut, only a smoother-looking route. MovementAllowance uses these base costs when this GridPathfinder is assigned.")]
    [SerializeField]
    private int diagonalStepCost = 2;

    [Tooltip("If true, a diagonal move is blocked unless both flanking orthogonal cells are also walkable - stops the path clipping through a blocked corner.")]
    [SerializeField]
    private bool preventCornerCutting = true;

    [Tooltip("When a diagonal route and a staircase route cost exactly the same, prefer the diagonal one so paths don't default to zigzagging. Purely a shape preference - does not change the numeric step costs.")]
    [SerializeField]
    private bool preferDiagonalMovement = true;


    /// <summary>Whether diagonal steps are currently allowed by this pathfinder.</summary>
    public bool AllowDiagonalMovement
    {
        get { return allowDiagonalMovement; }
    }


    /// <summary>Base cost used by A* for one orthogonal step.</summary>
    public int OrthogonalStepCost
    {
        get { return orthogonalStepCost; }
    }


    /// <summary>Base cost used by A* for one diagonal step.</summary>
    public int DiagonalStepCost
    {
        get { return diagonalStepCost; }
    }


    /// <summary>
    /// Returns this pathfinder's base cost for one adjacent step.
    ///
    /// This is exposed so NPC movement-range tools and other systems can use
    /// the same pricing that FindPath uses without duplicating Inspector
    /// settings. Terrain/hazard surcharges can still be layered by a caller
    /// through the GetReachableCells(stepCostFunction) overload.
    /// </summary>
    public int GetBaseStepCost(Vector3Int fromCell, Vector3Int toCell)
    {
        Vector3Int delta = toCell - fromCell;

        bool isDiagonalStep =
            delta.x != 0 &&
            delta.y != 0;

        return isDiagonalStep
            ? diagonalStepCost
            : orthogonalStepCost;
    }


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
    /// Calculates the base cost of an already-computed path using the same
    /// orthogonal/diagonal pricing as FindPath. The path is expected not to
    /// include startCell, matching FindPath's returned format.
    /// </summary>
    public int CalculateBasePathCost(
        Vector3Int startCell,
        IReadOnlyList<Vector3Int> path)
    {
        if (path == null || path.Count == 0)
        {
            return 0;
        }

        int total = 0;
        Vector3Int previousCell = startCell;

        foreach (Vector3Int cell in path)
        {
            total += GetBaseStepCost(previousCell, cell);
            previousCell = cell;
        }

        return total;
    }


    /// <summary>
    /// Finds a path from startCell to destinationCell using this
    /// GridPathfinder's normal base orthogonal/diagonal costs.
    ///
    /// The returned path does not include the starting cell.
    /// An empty list means either no movement is needed or no path was found.
    /// </summary>
    public List<Vector3Int> FindPath(
        Vector3Int startCell,
        Vector3Int destinationCell)
    {
        return FindPathInternal(
            startCell,
            destinationCell,
            GetBaseStepCost,
            true
        );
    }


    /// <summary>
    /// Finds the cheapest legal path using a caller-supplied step-cost
    /// function. This is the integration point for terrain, hazard or
    /// ship-specific movement costs without putting those feature rules into
    /// GridPathfinder.
    ///
    /// The callback must return a positive cost for each legal adjacent step.
    /// Because arbitrary feature costs may not match this class's heuristic,
    /// this overload deliberately uses a zero heuristic (Dijkstra behaviour)
    /// so the returned route remains correct.
    /// </summary>
    public List<Vector3Int> FindPath(
        Vector3Int startCell,
        Vector3Int destinationCell,
        System.Func<Vector3Int, Vector3Int, int> stepCostFunction)
    {
        if (stepCostFunction == null)
        {
            Debug.LogError(
                "FindPath requires a stepCostFunction for the custom-cost overload."
            );

            return new List<Vector3Int>();
        }

        return FindPathInternal(
            startCell,
            destinationCell,
            stepCostFunction,
            false
        );
    }


    private List<Vector3Int> FindPathInternal(
        Vector3Int startCell,
        Vector3Int destinationCell,
        System.Func<Vector3Int, Vector3Int, int> stepCostFunction,
        bool useBaseHeuristic)
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
        startNode.HCost =
            useBaseHeuristic
                ? CalculateHeuristic(startCell, destinationCell)
                : 0;

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
                    Mathf.Max(
                        1,
                        stepCostFunction(
                            currentNode.Cell,
                            neighbourCell
                        )
                    );

                long rawNewGCost =
                    (long)currentNode.GCost + stepCost;

                int newGCost =
                    rawNewGCost >= int.MaxValue
                        ? int.MaxValue
                        : (int)rawNewGCost;


                if (!knownNodes.TryGetValue(
                    neighbourCell,
                    out PathNode neighbourNode))
                {
                    neighbourNode = new PathNode(neighbourCell);

                    neighbourNode.GCost = newGCost;
                    neighbourNode.HCost =
                        useBaseHeuristic
                            ? CalculateHeuristic(
                                neighbourCell,
                                destinationCell
                            )
                            : 0;

                    neighbourNode.Parent = currentNode;

                    knownNodes.Add(neighbourCell, neighbourNode);
                    openNodes.Add(neighbourNode);

                    continue;
                }


                bool isCheaper =
                    newGCost < neighbourNode.GCost;

                // Equal-cost routes prefer a diagonal arrival when requested,
                // preserving the existing smoother-looking tie-break without
                // changing which route is considered cheapest.
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
    /// Every cell reachable from startCell without exceeding maxBudget, using
    /// the exact same base step costs as FindPath. This is the simplest entry
    /// point for an NPC movement-range query or generic range overlay.
    ///
    /// Use the overload that accepts stepCostFunction when a feature needs
    /// additional terrain/resource/hazard costs.
    /// </summary>
    public Dictionary<Vector3Int, int> GetReachableCells(
        Vector3Int startCell,
        int maxBudget)
    {
        return GetReachableCells(
            startCell,
            maxBudget,
            GetBaseStepCost
        );
    }


    /// <summary>
    /// Every cell reachable from startCell without exceeding maxBudget,
    /// using stepCostFunction to price each step - not this class's own
    /// orthogonalStepCost/diagonalStepCost, which only shape FindPath's
    /// route choice and have nothing to do with any budget. Pass
    /// MovementAllowance.GetMovementCost (or equivalent) as
    /// stepCostFunction to answer "what can I actually reach this turn" -
    /// see MovementAllowance.GetReachableCellsThisTurn for exactly that.
    ///
    /// Legality is still this class's own CanStepBetween, the same rule
    /// FindPath and RoutePlanner's manual drag-drawing both already use -
    /// obstacles, corner-cutting, and the diagonal-movement toggle all
    /// apply identically here.
    ///
    /// Returns every reachable cell mapped to the cheapest cumulative cost
    /// found to reach it (startCell itself is not included - a budget of
    /// 0 reaches nothing). Dijkstra rather than A*, since there is no
    /// single destination to aim a heuristic at.
    /// </summary>
    public Dictionary<Vector3Int, int> GetReachableCells(
        Vector3Int startCell,
        int maxBudget,
        System.Func<Vector3Int, Vector3Int, int> stepCostFunction)
    {
        Dictionary<Vector3Int, int> reachable =
            new Dictionary<Vector3Int, int>();

        if (gridMap == null)
        {
            Debug.LogError("GridPathfinder has no GridMap assigned.");
            return reachable;
        }

        if (stepCostFunction == null)
        {
            Debug.LogError(
                "GetReachableCells requires a stepCostFunction."
            );

            return reachable;
        }

        if (maxBudget <= 0)
        {
            return reachable;
        }


        List<Vector3Int> directions =
            new List<Vector3Int>(OrthogonalDirections);

        if (allowDiagonalMovement)
        {
            directions.AddRange(DiagonalDirections);
        }


        // Same open/closed shape as FindPath, but expanding outward from
        // one start cell against a cost ceiling instead of racing toward
        // one destination - so nodes only need their accumulated cost,
        // not FindPath's separate heuristic/FCost machinery.
        Dictionary<Vector3Int, int> bestCostSoFar =
            new Dictionary<Vector3Int, int> { { startCell, 0 } };

        List<Vector3Int> frontier = new List<Vector3Int> { startCell };

        while (frontier.Count > 0)
        {
            int bestIndex = 0;

            for (int i = 1; i < frontier.Count; i++)
            {
                if (bestCostSoFar[frontier[i]] <
                    bestCostSoFar[frontier[bestIndex]])
                {
                    bestIndex = i;
                }
            }

            Vector3Int currentCell = frontier[bestIndex];
            int currentCost = bestCostSoFar[currentCell];

            frontier.RemoveAt(bestIndex);


            foreach (Vector3Int direction in directions)
            {
                Vector3Int neighbourCell = currentCell + direction;

                if (!CanStepBetween(currentCell, neighbourCell))
                {
                    continue;
                }

                int stepCost =
                    Mathf.Max(
                        1,
                        stepCostFunction(currentCell, neighbourCell)
                    );

                long rawNewCost =
                    (long)currentCost + stepCost;

                if (rawNewCost > maxBudget ||
                    rawNewCost >= int.MaxValue)
                {
                    continue;
                }

                int newCost = (int)rawNewCost;

                if (bestCostSoFar.TryGetValue(
                        neighbourCell,
                        out int knownCost) &&
                    knownCost <= newCost)
                {
                    continue;
                }

                bestCostSoFar[neighbourCell] = newCost;

                if (!frontier.Contains(neighbourCell))
                {
                    frontier.Add(neighbourCell);
                }
            }
        }

        bestCostSoFar.Remove(startCell);

        return bestCostSoFar;
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