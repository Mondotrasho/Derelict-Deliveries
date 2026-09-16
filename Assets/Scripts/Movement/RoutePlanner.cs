using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns the currently planned route for the player.
///
/// RoutePlanner only decides WHICH cells the player intends to travel
/// through next. It does not draw anything (RoutePathRenderer), enforce or
/// spend the movement budget (MovementAllowance), or move the player
/// (MovementPlanController / PlayerGridController). When MovementAllowance is
/// available it may use its public step-cost API to choose the cheapest route.
/// </summary>
public class RoutePlanner : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private PlayerGridController playerController;

    [SerializeField]
    private GridPathfinder pathfinder;

    [Tooltip("Optional. When assigned, automatic A* route choice uses the same final movement costs as the player's budget, including registered feature modifiers.")]
    [SerializeField]
    private MovementAllowance movementAllowance;


    private Vector3Int? plannedDestination;

    private List<Vector3Int> plannedPath = new List<Vector3Int>();


    /// <summary>
    /// Raised whenever the planned path changes - including when it is
    /// cleared, in which case the path passed will be empty.
    /// </summary>
    public event Action<List<Vector3Int>> RouteChanged;


    /// <summary>
    /// The route currently being planned. Never null - an empty list
    /// means no route is currently planned.
    /// </summary>
    public IReadOnlyList<Vector3Int> PlannedPath
    {
        get
        {
            return plannedPath;
        }
    }


    /// <summary>
    /// True while a non-empty route is planned.
    /// </summary>
    public bool HasPlannedRoute
    {
        get
        {
            return plannedPath.Count > 0;
        }
    }


    private void Awake()
    {
        // RoutePlanner and MovementAllowance currently live together on the
        // MovementPlanning object. Auto-resolving keeps the existing scene
        // compatible while still allowing an explicit reference if the
        // hierarchy changes later.
        if (movementAllowance == null)
        {
            movementAllowance = GetComponent<MovementAllowance>();
        }
    }


    /// <summary>
    /// Uses the player's final movement-cost API when available so route
    /// choice can avoid an expensive hazard rather than merely charging for it
    /// after the path has already been chosen. Falls back to normal base-cost
    /// A* when no MovementAllowance is assigned.
    /// </summary>
    private List<Vector3Int> FindPlayerPath(
        Vector3Int startCell,
        Vector3Int destinationCell)
    {
        if (movementAllowance != null)
        {
            return pathfinder.FindPath(
                startCell,
                destinationCell,
                movementAllowance.GetMovementCost
            );
        }

        return pathfinder.FindPath(
            startCell,
            destinationCell
        );
    }


    /// <summary>
    /// Requests a candidate route from the player's current cell to
    /// destinationCell. Replaces any previously planned route, even if
    /// no path could be found (in which case the plan becomes empty).
    ///
    /// Does nothing while the player is already executing a committed
    /// route - a plan built mid-move would be stale by the time it could
    /// ever be acted on, and recalculating one every frame during a drag
    /// would be wasted pathfinding for no visible benefit.
    /// </summary>
    public void SetDestination(Vector3Int destinationCell)
    {
        if (playerController == null || pathfinder == null)
        {
            Debug.LogError(
                "RoutePlanner is missing a required reference."
            );

            return;
        }

        if (playerController.IsMoving)
        {
            return;
        }


        plannedDestination = destinationCell;

        plannedPath =
            FindPlayerPath(
                playerController.CurrentCell,
                destinationCell
            );

        RouteChanged?.Invoke(plannedPath);
    }


    /// <summary>
    /// Rebuilds the planned route from the player's current cell using the
    /// last requested destination. Useful if the player's position changes
    /// (e.g. an interrupted move) while a route is still only planned and
    /// not yet committed.
    /// </summary>
    public void RefreshFromCurrentPosition()
    {
        if (!plannedDestination.HasValue)
        {
            return;
        }

        SetDestination(plannedDestination.Value);
    }


    /// <summary>
    /// Clears the currently planned route without moving the player.
    /// </summary>
    public void ClearRoute()
    {
        if (plannedPath.Count == 0 &&
            !plannedDestination.HasValue)
        {
            return;
        }

        plannedDestination = null;
        plannedPath = new List<Vector3Int>();

        RouteChanged?.Invoke(plannedPath);
    }


    /// <summary>
    /// One step of manually drawing a route by dragging - called once per
    /// newly hovered cell. Rather than re-pathfinding from the player
    /// (SetDestination), this extends or trims the existing plan from
    /// wherever it currently ends:
    ///
    /// - dragging onto an already-planned cell trims the route back to
    ///   that point (backtracking), instead of looping through it
    /// - dragging onto the player's own current cell clears the plan
    /// - dragging onto a non-adjacent cell (the cursor jumped more than
    ///   one cell between frames) bridges the gap with a short pathfound
    ///   segment, applying the same backtracking check to every cell in it
    /// - dragging onto an ordinary adjacent cell appends it, but only if
    ///   GridPathfinder.CanStepBetween allows that step - an obstacle,
    ///   a blocked corner, or diagonal movement being disabled all reject
    ///   the drag rather than creating an illegal route
    ///
    /// Does nothing while the player is already executing a committed
    /// route, for the same reason as SetDestination.
    /// </summary>
    public void AppendDraggedCell(Vector3Int cell)
    {
        if (playerController == null || pathfinder == null)
        {
            Debug.LogError(
                "RoutePlanner is missing a required reference."
            );

            return;
        }

        if (playerController.IsMoving)
        {
            return;
        }


        Vector3Int lastCell = GetLastPlannedCellOrCurrent();

        if (cell == lastCell)
        {
            return;
        }


        bool changed;

        if (IsAdjacent(lastCell, cell))
        {
            changed = AppendOrBacktrackSingleCell(lastCell, cell);
        }
        else
        {
            // The cursor moved further than one cell since the last drag
            // update - bridge the gap instead of leaving a hole in the
            // route. FindPath already only ever returns legal steps, so
            // every cell in this segment will pass CanStepBetween too.
            List<Vector3Int> bridgeSegment =
                FindPlayerPath(lastCell, cell);

            changed = false;

            foreach (Vector3Int bridgeCell in bridgeSegment)
            {
                Vector3Int fromCell = GetLastPlannedCellOrCurrent();

                if (AppendOrBacktrackSingleCell(fromCell, bridgeCell))
                {
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return;
        }

        plannedDestination =
            plannedPath.Count > 0
                ? plannedPath[plannedPath.Count - 1]
                : (Vector3Int?)null;

        RouteChanged?.Invoke(plannedPath);
    }


    /// <summary>
    /// The cell the planned route currently ends at, or the player's
    /// actual current cell if nothing is planned yet.
    /// </summary>
    private Vector3Int GetLastPlannedCellOrCurrent()
    {
        return plannedPath.Count > 0
            ? plannedPath[plannedPath.Count - 1]
            : playerController.CurrentCell;
    }


    /// <summary>
    /// One step of manual route drawing: trims back to an already-visited
    /// cell (backtracking), clears the plan if dragged back onto the
    /// player's own cell, or appends a new cell - rejecting the append if
    /// GridPathfinder says the step from fromCell isn't actually legal.
    /// Returns whether the planned path actually changed.
    /// </summary>
    private bool AppendOrBacktrackSingleCell(Vector3Int fromCell, Vector3Int cell)
    {
        if (cell == playerController.CurrentCell)
        {
            bool hadRoute = plannedPath.Count > 0;
            plannedPath.Clear();
            return hadRoute;
        }


        int existingIndex = plannedPath.IndexOf(cell);

        if (existingIndex >= 0)
        {
            int removeCount =
                plannedPath.Count - existingIndex - 1;

            if (removeCount == 0)
            {
                return false;
            }

            plannedPath.RemoveRange(
                existingIndex + 1,
                removeCount
            );

            return true;
        }


        if (!pathfinder.CanStepBetween(fromCell, cell))
        {
            // Obstacle, blocked corner, or diagonal movement disabled -
            // reject rather than create an illegal route.
            return false;
        }

        plannedPath.Add(cell);
        return true;
    }


    /// <summary>
    /// True if the two cells are exactly one step apart, including
    /// diagonally.
    /// </summary>
    private bool IsAdjacent(Vector3Int a, Vector3Int b)
    {
        int dx = Mathf.Abs(a.x - b.x);
        int dy = Mathf.Abs(a.y - b.y);

        return dx <= 1 && dy <= 1 && (dx != 0 || dy != 0);
    }
}