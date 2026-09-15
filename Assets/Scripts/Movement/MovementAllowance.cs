using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns how much the player is allowed to move, and answers questions
/// about the cost of a candidate route.
///
/// MovementAllowance does not draw anything (RoutePathRenderer), does not
/// calculate routes (GridPathfinder/RoutePlanner), and does not move the
/// player (PlayerGridController) - it only tracks a budget and prices
/// whatever path it is handed.
///
/// Optionally talks to a TurnManager in both directions: listens for
/// TurnEnded to refill the budget, and can end the turn itself once the
/// budget is spent (see autoEndTurnWhenBudgetExhausted). TurnManager
/// itself has no reference back to this script either way.
/// </summary>
public class MovementAllowance : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private PlayerGridController playerController;

    [Tooltip("Optional. When assigned, movement points refill to max whenever a turn ends - once per real turn, not once per cell moved.")]
    [SerializeField]
    private TurnManager turnManager;


    [Header("Timing")]

    [Tooltip("If true, spending the last movement point in a turn ends the turn automatically (which also refills the budget for the next turn), instead of waiting for an explicit End Turn action.")]
    [SerializeField]
    private bool autoEndTurnWhenBudgetExhausted = false;


    [Header("Budget")]

    [Tooltip("Movement points available at the start of a turn.")]
    [SerializeField]
    private int maxMovementPoints = 8;


    [Header("Cost")]

    [Tooltip("Cost to move into one orthogonal (N/S/E/W) cell.")]
    [SerializeField]
    private int costPerOrthogonalCell = 1;

    [Tooltip("Cost to move into one diagonal cell. Should match GridPathfinder's diagonalStepCost - GridPathfinder deliberately doesn't know about MovementAllowance, so the two fields are not shared automatically and a mismatch would let the pathfinder pick routes this budget disagrees about the price of.")]
    [SerializeField]
    private int costPerDiagonalCell = 2;


    private int currentMovementPoints;


    /// <summary>
    /// Raised whenever CurrentMovementPoints changes, so renderers and UI
    /// can refresh without polling every frame.
    /// </summary>
    public event Action AllowanceChanged;


    /// <summary>
    /// Movement points available at the start of a turn.
    /// </summary>
    public int MaxMovementPoints
    {
        get
        {
            return maxMovementPoints;
        }
    }


    /// <summary>
    /// Movement points currently remaining this turn.
    /// </summary>
    public int CurrentMovementPoints
    {
        get
        {
            return currentMovementPoints;
        }
    }


    private void OnEnable()
    {
        if (turnManager != null)
        {
            turnManager.TurnEnded += HandleTurnEnded;
        }
    }


    private void OnDisable()
    {
        if (turnManager != null)
        {
            turnManager.TurnEnded -= HandleTurnEnded;
        }
    }


    private void HandleTurnEnded(int turnNumber)
    {
        RefillToMax();
    }


    private void Awake()
    {
        currentMovementPoints = maxMovementPoints;
    }


    /// <summary>
    /// Cost to travel from one cell into an adjacent cell. Diagonal steps
    /// (both axes changing) use costPerDiagonalCell; everything else uses
    /// costPerOrthogonalCell. This is also the seam for later per-terrain
    /// or hazard-based variable costs.
    /// </summary>
    public int GetMovementCost(Vector3Int fromCell, Vector3Int toCell)
    {
        Vector3Int delta = toCell - fromCell;

        bool isDiagonalStep =
            delta.x != 0 &&
            delta.y != 0;

        return isDiagonalStep
            ? costPerDiagonalCell
            : costPerOrthogonalCell;
    }


    /// <summary>
    /// Total cost of travelling the full path, starting from the player's
    /// actual current cell.
    /// </summary>
    public int CalculatePathCost(IReadOnlyList<Vector3Int> path)
    {
        if (path == null || path.Count == 0)
        {
            return 0;
        }

        if (playerController == null)
        {
            Debug.LogError(
                "MovementAllowance has no PlayerGridController assigned."
            );

            return 0;
        }


        int totalCost = 0;

        Vector3Int previousCell =
            playerController.CurrentCell;

        foreach (Vector3Int cell in path)
        {
            totalCost +=
                GetMovementCost(
                    previousCell,
                    cell
                );

            previousCell = cell;
        }

        return totalCost;
    }


    /// <summary>
    /// True if the full path costs no more than the remaining
    /// movement points.
    /// </summary>
    public bool CanAfford(IReadOnlyList<Vector3Int> path)
    {
        return CalculatePathCost(path) <=
               currentMovementPoints;
    }


    /// <summary>
    /// How many cells from the start of the path are affordable with the
    /// movement remaining right now. Stops at the first cell that would
    /// exceed the budget, so the result is always a contiguous prefix of
    /// the path - useful for drawing an "affordable" vs "excess" split.
    /// </summary>
    public int GetAffordableCellCount(IReadOnlyList<Vector3Int> path)
    {
        if (path == null || path.Count == 0)
        {
            return 0;
        }

        if (playerController == null)
        {
            Debug.LogError(
                "MovementAllowance has no PlayerGridController assigned."
            );

            return 0;
        }


        int remaining = currentMovementPoints;

        Vector3Int previousCell =
            playerController.CurrentCell;

        int affordableCount = 0;

        foreach (Vector3Int cell in path)
        {
            int cost =
                GetMovementCost(
                    previousCell,
                    cell
                );

            if (cost > remaining)
            {
                break;
            }

            remaining -= cost;
            affordableCount++;

            previousCell = cell;
        }

        return affordableCount;
    }


    /// <summary>
    /// Deducts movement points already spent on a committed route.
    /// Clamped at zero rather than going negative. If
    /// autoEndTurnWhenBudgetExhausted is set and this spend empties the
    /// budget, ends the turn immediately - which in turn calls
    /// RefillToMax() via the TurnEnded subscription above, so the next
    /// turn starts fully refilled in the same call.
    /// </summary>
    public void Spend(int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        currentMovementPoints =
            Mathf.Max(
                0,
                currentMovementPoints - amount
            );

        AllowanceChanged?.Invoke();

        if (autoEndTurnWhenBudgetExhausted &&
            currentMovementPoints == 0 &&
            turnManager != null)
        {
            turnManager.EndTurn();
        }
    }


    /// <summary>
    /// Restores movement points to the maximum. Called automatically
    /// once per real turn if turnManager is assigned (via its TurnEnded
    /// event) - also fine to call directly.
    /// </summary>
    public void RefillToMax()
    {
        currentMovementPoints = maxMovementPoints;

        AllowanceChanged?.Invoke();
    }
}