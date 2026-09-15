using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Validates and commits the route currently owned by RoutePlanner.
///
/// This is the only component allowed to tell PlayerGridController to
/// actually execute a planned route - RoutePlanner and RoutePathRenderer
/// never move the player themselves. Once MovementAllowance exists,
/// CanCommit is the natural place to also check route cost against the
/// player's remaining movement.
/// </summary>
public class MovementPlanController : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private RoutePlanner routePlanner;

    [SerializeField]
    private PlayerGridController playerController;

    [Tooltip("Optional. If assigned, an unaffordable route cannot be committed.")]
    [SerializeField]
    private MovementAllowance movementAllowance;


    /// <summary>
    /// Raised after a planned route is successfully handed off to
    /// PlayerGridController for execution.
    /// </summary>
    public event Action RouteCommitted;


    /// <summary>
    /// Raised whenever a planned route is discarded without moving.
    /// </summary>
    public event Action RouteCancelled;


    /// <summary>
    /// Raised whenever a Commit attempt is rejected specifically because
    /// the route costs more than the remaining movement allowance, so UI
    /// can show a clear "too far" message rather than nothing happening.
    /// </summary>
    public event Action RouteTooExpensive;


    /// <summary>
    /// True when there is a non-empty, affordable planned route and the
    /// player is currently free to start executing it. MovementAllowance
    /// is optional - if none is assigned, cost is not checked.
    /// </summary>
    public bool CanCommit
    {
        get
        {
            if (routePlanner == null ||
                playerController == null ||
                !routePlanner.HasPlannedRoute ||
                playerController.IsMoving)
            {
                return false;
            }

            if (movementAllowance != null &&
                !movementAllowance.CanAfford(routePlanner.PlannedPath))
            {
                return false;
            }

            return true;
        }
    }


    /// <summary>
    /// Hands the currently planned route to PlayerGridController, spends
    /// its movement cost, and clears the plan, since it is now being
    /// executed rather than merely proposed.
    /// </summary>
    public void Commit()
    {
        if (routePlanner == null ||
            playerController == null ||
            !routePlanner.HasPlannedRoute ||
            playerController.IsMoving)
        {
            return;
        }

        if (movementAllowance != null &&
            !movementAllowance.CanAfford(routePlanner.PlannedPath))
        {
            RouteTooExpensive?.Invoke();
            return;
        }


        // Copy the path before clearing the plan - ClearRoute() would
        // otherwise wipe the very list we are about to hand over.
        List<Vector3Int> pathToExecute =
            new List<Vector3Int>(
                routePlanner.PlannedPath
            );

        int cost =
            movementAllowance != null
                ? movementAllowance.CalculatePathCost(pathToExecute)
                : 0;

        routePlanner.ClearRoute();

        if (movementAllowance != null)
        {
            movementAllowance.Spend(cost);
        }

        playerController.MoveAlongPath(pathToExecute);

        RouteCommitted?.Invoke();
    }


    /// <summary>
    /// Discards the currently planned route without moving the player.
    /// </summary>
    public void Cancel()
    {
        if (routePlanner == null ||
            !routePlanner.HasPlannedRoute)
        {
            return;
        }

        routePlanner.ClearRoute();

        RouteCancelled?.Invoke();
    }
}