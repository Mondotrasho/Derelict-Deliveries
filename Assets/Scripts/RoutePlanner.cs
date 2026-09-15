using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns the currently planned route for the player.
///
/// RoutePlanner only decides WHICH cells the player intends to travel
/// through next. It does not draw anything (RoutePathRenderer), does not
/// check movement allowance (MovementAllowance), and does not move the
/// player (MovementPlanController / PlayerGridController).
/// </summary>
public class RoutePlanner : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private PlayerGridController playerController;

    [SerializeField]
    private GridPathfinder pathfinder;


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


    /// <summary>
    /// Requests a candidate route from the player's current cell to
    /// destinationCell. Replaces any previously planned route, even if
    /// no path could be found (in which case the plan becomes empty).
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


        plannedDestination = destinationCell;

        plannedPath =
            pathfinder.FindPath(
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
}