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

    [Tooltip("Optional but recommended. When assigned, MovementAllowance uses GridPathfinder's base step costs so A* route choice and player budget pricing cannot drift apart. It is also required for reachable-cell queries.")]
    [SerializeField]
    private GridPathfinder pathfinder;


    [Header("Timing")]

    [Tooltip("If true, spending the last movement point in a turn ends the turn automatically (which also refills the budget for the next turn), instead of waiting for an explicit End Turn action.")]
    [SerializeField]
    private bool autoEndTurnWhenBudgetExhausted = false;


    [Header("Budget")]

    [Tooltip("Movement points available at the start of a turn.")]
    [SerializeField]
    private int maxMovementPoints = 8;


    [Header("Cost")]

    [Tooltip("Fallback orthogonal cost used only when no GridPathfinder is assigned.")]
    [SerializeField]
    private int costPerOrthogonalCell = 1;

    [Tooltip("Fallback diagonal cost used only when no GridPathfinder is assigned.")]
    [SerializeField]
    private int costPerDiagonalCell = 2;


    private int currentMovementPoints;

    // Feature-owned movement cost rules register here rather than adding
    // hazard/event-specific code to MovementAllowance.
    private readonly List<IMovementCostModifier> movementCostModifiers =
        new List<IMovementCostModifier>();


    /// <summary>
    /// Raised whenever CurrentMovementPoints changes, so renderers and UI
    /// can refresh without polling every frame.
    /// </summary>
    public event Action AllowanceChanged;

    /// <summary>
    /// Raised whenever the set or state of movement-cost rules changes.
    /// Route/range visuals can refresh without knowing which outside feature
    /// supplied the rule.
    /// </summary>
    public event Action MovementCostRulesChanged;


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


    /// <summary>
    /// Remaining movement as a 0-1 value for UI bars/dials. The underlying
    /// integer values remain available through CurrentMovementPoints and
    /// MaxMovementPoints.
    /// </summary>
    public float RemainingFraction
    {
        get
        {
            return maxMovementPoints > 0
                ? (float)currentMovementPoints / maxMovementPoints
                : 0.0f;
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
    /// Base cost to travel from one cell into an adjacent cell before any
    /// feature-owned modifiers are applied. When a GridPathfinder is assigned
    /// this uses its exposed base step cost, keeping A* and the player budget
    /// on the same numbers. Local orthogonal/diagonal fields are fallback only.
    /// </summary>
    public int GetBaseMovementCost(Vector3Int fromCell, Vector3Int toCell)
    {
        if (pathfinder != null)
        {
            return pathfinder.GetBaseStepCost(fromCell, toCell);
        }

        Vector3Int delta = toCell - fromCell;

        bool isDiagonalStep =
            delta.x != 0 &&
            delta.y != 0;

        return isDiagonalStep
            ? costPerDiagonalCell
            : costPerOrthogonalCell;
    }


    /// <summary>
    /// Final player movement cost for one adjacent step. Starts with the base
    /// orthogonal/diagonal price, then adds every registered
    /// IMovementCostModifier surcharge.
    ///
    /// Outside features should register a modifier rather than editing this
    /// class. Negative modifier results are ignored so a feature cannot
    /// accidentally make a step free or produce an invalid A* cost.
    /// </summary>
    public int GetMovementCost(Vector3Int fromCell, Vector3Int toCell)
    {
        long totalCost = GetBaseMovementCost(fromCell, toCell);

        foreach (IMovementCostModifier modifier in movementCostModifiers)
        {
            if (modifier == null)
            {
                continue;
            }

            int additionalCost =
                Mathf.Max(
                    0,
                    modifier.GetAdditionalMovementCost(fromCell, toCell)
                );

            totalCost += additionalCost;

            if (totalCost >= int.MaxValue)
            {
                return int.MaxValue;
            }
        }

        return Mathf.Max(1, (int)totalCost);
    }


    /// <summary>
    /// Registers one feature-owned movement-cost rule. Safe to call more than
    /// once for the same object; duplicates are ignored.
    /// </summary>
    public bool RegisterMovementCostModifier(IMovementCostModifier modifier)
    {
        if (modifier == null || movementCostModifiers.Contains(modifier))
        {
            return false;
        }

        movementCostModifiers.Add(modifier);
        MovementCostRulesChanged?.Invoke();
        return true;
    }


    /// <summary>
    /// Removes a previously registered movement-cost rule.
    /// </summary>
    public bool UnregisterMovementCostModifier(IMovementCostModifier modifier)
    {
        if (modifier == null || !movementCostModifiers.Remove(modifier))
        {
            return false;
        }

        MovementCostRulesChanged?.Invoke();
        return true;
    }


    /// <summary>
    /// Call when an already-registered modifier changed its internal state
    /// without being registered/unregistered. This lets route/range displays
    /// refresh while keeping the modifier's implementation outside this class.
    /// </summary>
    public void NotifyMovementCostsChanged()
    {
        MovementCostRulesChanged?.Invoke();
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
    /// Splits path into the sequence of turn-sized chunks it would
    /// actually take to travel the whole thing - the first chunk priced
    /// against CurrentMovementPoints (whatever's left right now), every
    /// chunk after that against MaxMovementPoints (a turn will have ended
    /// and refilled by the time it executes). Returns the cumulative cell
    /// count at the end of each chunk - e.g. [5, 12, 18] for an 18-cell
    /// path means cells 0-4 this turn, 5-11 the turn after, 12-17 the
    /// turn after that.
    ///
    /// A path that fits in the current budget returns a single-entry list
    /// equal to path.Count - the "many turns" case and the ordinary "it
    /// all fits" case are the same shape, not two different ones.
    /// MovementPlanController.CommitSegment() relies on that to treat
    /// both uniformly.
    /// </summary>
    public List<int> GetTurnSegmentBreakpoints(IReadOnlyList<Vector3Int> path)
    {
        List<int> breakpoints = new List<int>();

        if (path == null || path.Count == 0)
        {
            return breakpoints;
        }

        if (playerController == null)
        {
            Debug.LogError(
                "MovementAllowance has no PlayerGridController assigned."
            );

            return breakpoints;
        }


        Vector3Int previousCell = playerController.CurrentCell;

        int budgetRemainingThisWindow = currentMovementPoints;

        for (int i = 0; i < path.Count; i++)
        {
            int cost = GetMovementCost(previousCell, path[i]);

            // A single step costing more than a full turn's budget can
            // never be affordable no matter how many turns pass - without
            // this check the loop would keep opening new windows that
            // can still never fit it.
            if (cost > maxMovementPoints)
            {
                Debug.LogWarning(
                    $"MovementAllowance: a single step in this path costs {cost}, " +
                    "more than any turn's full budget - it can never be " +
                    "travelled, segmented or not."
                );

                break;
            }

            if (cost > budgetRemainingThisWindow)
            {
                breakpoints.Add(i);
                budgetRemainingThisWindow = maxMovementPoints;
            }

            budgetRemainingThisWindow -= cost;
            previousCell = path[i];
        }

        breakpoints.Add(path.Count);

        return breakpoints;
    }


    /// <summary>
    /// Convenience overload for the player's current position. This is the
    /// normal API for a movement-range overlay: the caller does not need to
    /// know which movement component owns the current cell.
    /// </summary>
    public Dictionary<Vector3Int, int> GetReachableCellsThisTurn()
    {
        if (playerController == null)
        {
            Debug.LogError(
                "MovementAllowance has no PlayerGridController assigned."
            );

            return new Dictionary<Vector3Int, int>();
        }

        return GetReachableCellsThisTurn(playerController.CurrentCell);
    }


    /// <summary>
    /// Every cell reachable from fromCell without exceeding the movement
    /// points remaining right now - the "how far can I go this turn"
    /// query. Thin wrapper around GridPathfinder.GetReachableCells,
    /// supplying CurrentMovementPoints as the budget and GetMovementCost
    /// as the pricing, so callers don't need to know that pairing
    /// themselves.
    /// </summary>
    public Dictionary<Vector3Int, int> GetReachableCellsThisTurn(Vector3Int fromCell)
    {
        if (pathfinder == null)
        {
            Debug.LogError(
                "MovementAllowance has no GridPathfinder assigned."
            );

            return new Dictionary<Vector3Int, int>();
        }

        return pathfinder.GetReachableCells(
            fromCell,
            currentMovementPoints,
            GetMovementCost
        );
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