using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Finds paths across the GridMap using A*.
///
/// This class only decides which cells make up the path.
/// It does not move the player.
/// </summary>
public class GridPathfinder : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private GridMap gridMap;


    /// <summary>
    /// Four-directional grid movement.
    /// </summary>
    private static readonly Vector3Int[] NeighbourDirections =
    {
        Vector3Int.up,
        Vector3Int.right,
        Vector3Int.down,
        Vector3Int.left
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


        List<PathNode> openNodes = new List<PathNode>();
        HashSet<Vector3Int> closedCells = new HashSet<Vector3Int>();

        Dictionary<Vector3Int, PathNode> knownNodes =
            new Dictionary<Vector3Int, PathNode>();


        PathNode startNode = new PathNode(startCell);

        startNode.GCost = 0;
        startNode.HCost = CalculateDistance(startCell, destinationCell);

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


            foreach (Vector3Int direction in NeighbourDirections)
            {
                Vector3Int neighbourCell =
                    currentNode.Cell + direction;


                if (closedCells.Contains(neighbourCell))
                {
                    continue;
                }

                if (!gridMap.IsWalkable(neighbourCell))
                {
                    continue;
                }


                int newGCost = currentNode.GCost + 1;


                if (!knownNodes.TryGetValue(
                    neighbourCell,
                    out PathNode neighbourNode))
                {
                    neighbourNode = new PathNode(neighbourCell);

                    neighbourNode.GCost = newGCost;
                    neighbourNode.HCost =
                        CalculateDistance(
                            neighbourCell,
                            destinationCell
                        );

                    neighbourNode.Parent = currentNode;

                    knownNodes.Add(neighbourCell, neighbourNode);
                    openNodes.Add(neighbourNode);

                    continue;
                }


                if (newGCost < neighbourNode.GCost)
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
    /// Manhattan distance suits four-directional movement.
    /// </summary>
    private int CalculateDistance(
        Vector3Int firstCell,
        Vector3Int secondCell)
    {
        int horizontalDistance =
            Mathf.Abs(firstCell.x - secondCell.x);

        int verticalDistance =
            Mathf.Abs(firstCell.y - secondCell.y);

        return horizontalDistance + verticalDistance;
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
