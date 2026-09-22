using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Validates and commits the route currently owned by RoutePlanner, one
/// turn-sized segment at a time.
///
/// This is the only component allowed to tell PlayerGridController to
/// actually execute a planned route - RoutePlanner and RoutePathRenderer
/// never move the player themselves.
///
/// A route longer than one turn's movement budget is split (via
/// MovementAllowance.GetTurnSegmentBreakpoints) into as many turn-sized
/// chunks as it takes. CommitSegment() always executes exactly the next
/// unexecuted chunk - for a route that fits in one turn, that chunk is
/// the whole thing, so "the ordinary case" and "the multi-turn case" are
/// the same code path, not two.
///
/// Whatever is left after a segment executes is held here as
/// queuedRemainder, not in RoutePlanner - RoutePlanner's own plan is
/// cleared the moment anything is committed, since it is a *proposal*,
/// not an in-progress journey. A queued remainder stays visible and waits
/// for a fresh Commit on a later player turn; advancing time never moves
/// the ship without another confirmation. Planning a fresh destination via
/// RoutePlanner always takes priority over a queued remainder and
/// discards it, the same way MoveAlongPath discards a paused movement.
///
/// Pressing Commit and ending the turn are deliberately equivalent
/// whenever there is nothing left to spend this turn: if
/// MovementAllowance.CurrentMovementPoints is already 0 when
/// CommitSegment() is called - whether that's a queued remainder waiting
/// on the next turn, or a freshly re-planned route after cancelling one -
/// the turn is advanced (budget refreshed) as part of the same call,
/// rather than requiring a separate End Turn press first. Both actions
/// end up doing the same thing in that situation, which is the point.
///
/// Outside systems should normally interrupt movement through
/// PlayerGridController.AcquireMovementInterruption (or PlayerShipState's
/// facade) rather than toggling pause state directly. This class only
/// decides what counts as a segment and when to start the next one, never
/// how a segment already in flight gets interrupted or continued. It refuses
/// to start a new segment while movement is active, paused, or externally
/// interrupted. Releasing the final interruption can resume the preserved
/// route automatically; cancelling it remains an explicit action.
/// </summary>
public class MovementPlanController : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private RoutePlanner routePlanner;

    [SerializeField]
    private PlayerGridController playerController;

    [Tooltip("Optional. If assigned, an unaffordable segment cannot be committed, and its cost is spent on commit.")]
    [SerializeField]
    private MovementAllowance movementAllowance;

    [Tooltip("Optional. Used to end the current player phase when no movement remains.")]
    [SerializeField]
    private TurnManager turnManager;


    private List<Vector3Int> queuedRemainder = new List<Vector3Int>();

    // Guards CommitSegment against being re-entered while it is already
    // handing a segment to the physical movement layer.
    private bool isCommittingSegment;
    private bool isChargingActiveMovement;
    private Vector3Int lastChargedCell;


    /// <summary>
    /// Raised after any single segment is successfully handed off to
    /// PlayerGridController for execution - once per CommitSegment() call
    /// that actually moves something, whether or not more segments remain
    /// queued after it.
    /// </summary>
    public event Action RouteCommitted;


    /// <summary>
    /// Raised immediately after RouteCommitted, but only when that
    /// segment was also the last one - HasQueuedRemainder is false by the
    /// time this fires. For a route that fits in a single turn, this
    /// fires alongside RouteCommitted the one time it is committed.
    /// </summary>
    public event Action RouteFullyCommitted;


    /// <summary>
    /// Raised whenever a planned route (and any queued remainder) is
    /// discarded without moving.
    /// </summary>
    public event Action RouteCancelled;


    /// <summary>
    /// Raised whenever a Commit attempt is rejected specifically because
    /// not even one cell of the route is affordable this turn, so UI can
    /// show a clear "too far" message rather than nothing happening.
    /// </summary>
    public event Action RouteTooExpensive;


    /// <summary>
    /// Raised whenever queuedRemainder changes - set after a segment
    /// commits with more still to come, or cleared (by finishing, by
    /// Cancel, or by RoutePlanner starting something new). RoutePathRenderer
    /// uses this to keep drawing future segments even after RoutePlanner's
    /// own plan has already been cleared by commit.
    /// </summary>
    public event Action QueuedRemainderChanged;


    /// <summary>
    /// Whatever is left to travel after the most recently committed
    /// segment - empty, never null, if nothing is queued.
    /// </summary>
    public IReadOnlyList<Vector3Int> QueuedRemainder
    {
        get
        {
            return queuedRemainder;
        }
    }


    /// <summary>True while a queued remainder is waiting for its turn.</summary>
    public bool HasQueuedRemainder
    {
        get
        {
            return queuedRemainder.Count > 0;
        }
    }


    private void OnEnable()
    {
        if (playerController != null)
        {
            playerController.CellEntered += HandlePlayerCellEntered;
            playerController.RouteCompleted += HandleRouteCompleted;
        }
    }


    private void OnDisable()
    {
        if (playerController != null)
        {
            playerController.CellEntered -= HandlePlayerCellEntered;
            playerController.RouteCompleted -= HandleRouteCompleted;
        }

        isChargingActiveMovement = false;
    }


    /// <summary>
    /// True while an outside feature currently owns one or more movement
    /// interruption handles. Route-input scripts use this public boundary
    /// rather than reaching into PlayerGridController themselves.
    /// </summary>
    public bool IsMovementInterrupted
    {
        get
        {
            return playerController != null &&
                   playerController.IsMovementInterrupted;
        }
    }


    public bool CanAcceptPlayerInput
    {
        get
        {
            return (turnManager == null || turnManager.IsPlayerPhase) &&
                   !IsMovementInterrupted;
        }
    }


    /// <summary>
    /// True when there is something committable right now and the player
    /// is free to start it - either a freshly planned route, or a queued
    /// remainder if nothing new has been planned since. MovementAllowance
    /// is optional - if none is assigned, cost is not checked at all.
    ///
    /// A current budget of exactly 0 does NOT make this false: Commit can
    /// still request the end of the current player phase. The retained route
    /// then waits for another explicit Commit in the fresh player phase.
    /// </summary>
    public bool CanCommit
    {
        get
        {
            if (playerController == null ||
                (turnManager != null && !turnManager.IsPlayerPhase) ||
                playerController.IsMoving ||
                playerController.HasPausedMovement ||
                playerController.IsMovementInterrupted)
            {
                return false;
            }

            IReadOnlyList<Vector3Int> candidate = GetCandidatePath();

            if (candidate == null || candidate.Count == 0)
            {
                return false;
            }

            if (movementAllowance == null)
            {
                return true;
            }

            if (movementAllowance.CurrentMovementPoints == 0)
            {
                return true;
            }

            if (movementAllowance.GetAffordableCellCount(candidate) == 0)
            {
                return false;
            }

            return true;
        }
    }


    /// <summary>
    /// Commits exactly the next unexecuted turn-sized segment of whatever
    /// is currently committable - a freshly planned RoutePlanner route if
    /// one exists (which always supersedes any old queued remainder),
    /// otherwise the queued remainder itself. Spends that segment's cost,
    /// hands it to PlayerGridController, and stores anything left over as
    /// the new queued remainder for a future explicit Commit call to pick up.
    ///
    /// If there is nothing left to spend this turn at all
    /// (MovementAllowance.CurrentMovementPoints == 0) when this is
    /// called, the turn is advanced as part of the same call rather than
    /// requiring a separate End Turn press first - this is what makes
    /// pressing Commit and ending the turn behave identically once
    /// nothing more can happen this turn, whether what's being committed
    /// is a queued remainder or a freshly re-planned route after
    /// cancelling one.
    ///
    /// Safe to call re-entrantly; a nested call is ignored rather than
    /// committing the same segment twice.
    /// </summary>
    public void CommitSegment()
    {
        if (isCommittingSegment)
        {
            return;
        }

        isCommittingSegment = true;

        try
        {
            CommitSegmentCore();
        }
        finally
        {
            isCommittingSegment = false;
        }
    }


    private void CommitSegmentCore()
    {
        if (playerController == null ||
            (turnManager != null && !turnManager.IsPlayerPhase) ||
            playerController.IsMoving ||
            playerController.HasPausedMovement ||
            playerController.IsMovementInterrupted)
        {
            return;
        }

        bool candidateIsFreshPlan =
            routePlanner != null && routePlanner.HasPlannedRoute;

        IReadOnlyList<Vector3Int> candidatePath = GetCandidatePath();

        if (candidatePath == null || candidatePath.Count == 0)
        {
            return;
        }


        // A queued route with no budget waits through the enemy phase and
        // resumes when PlayerPhaseStarted supplies the next full budget.
        if (movementAllowance != null &&
            movementAllowance.CurrentMovementPoints == 0)
        {
            if (turnManager != null)
            {
                turnManager.EndTurn();
            }

            return;
        }


        List<int> breakpoints =
            movementAllowance != null
                ? movementAllowance.GetTurnSegmentBreakpoints(candidatePath)
                : new List<int> { candidatePath.Count };

        int segmentLength =
            breakpoints.Count > 0 ? breakpoints[0] : 0;

        if (segmentLength == 0)
        {
            RouteTooExpensive?.Invoke();
            return;
        }


        // Copy before touching RoutePlanner - ClearRoute() would
        // otherwise invalidate the very list candidatePath points at.
        List<Vector3Int> segment = new List<Vector3Int>(segmentLength);

        for (int i = 0; i < segmentLength; i++)
        {
            segment.Add(candidatePath[i]);
        }

        List<Vector3Int> remainder =
            new List<Vector3Int>(candidatePath.Count - segmentLength);

        for (int i = segmentLength; i < candidatePath.Count; i++)
        {
            remainder.Add(candidatePath[i]);
        }


        // Ask the physical movement layer to accept the segment BEFORE we
        // spend budget or destroy planning state. This matters now that
        // outside systems can hold movement-interruption handles.
        lastChargedCell = playerController.CurrentCell;
        isChargingActiveMovement = true;

        if (!playerController.TryMoveAlongPath(segment))
        {
            isChargingActiveMovement = false;
            return;
        }

        if (candidateIsFreshPlan)
        {
            routePlanner.ClearRoute();
        }

        SetQueuedRemainder(remainder.Count > 0 ? remainder : null);

        RouteCommitted?.Invoke();

        if (!HasQueuedRemainder)
        {
            RouteFullyCommitted?.Invoke();
        }
    }


    /// <summary>
    /// Discards whatever is currently planned or queued without moving
    /// the player.
    /// </summary>
    public void Cancel()
    {
        ChargeThroughCurrentCell();
        isChargingActiveMovement = false;

        bool hadAnything =
            (routePlanner != null && routePlanner.HasPlannedRoute) ||
            HasQueuedRemainder;

        if (!hadAnything)
        {
            return;
        }

        if (routePlanner != null)
        {
            routePlanner.ClearRoute();
        }

        SetQueuedRemainder(null);

        RouteCancelled?.Invoke();
    }


    private void HandlePlayerCellEntered(Vector3Int cell)
    {
        if (!isChargingActiveMovement)
        {
            return;
        }

        ChargeThroughCell(cell);
    }


    private void HandleRouteCompleted()
    {
        isChargingActiveMovement = false;
    }


    private void ChargeThroughCurrentCell()
    {
        if (isChargingActiveMovement && playerController != null)
        {
            ChargeThroughCell(playerController.CurrentCell);
        }
    }


    private void ChargeThroughCell(Vector3Int cell)
    {
        if (cell == lastChargedCell)
        {
            return;
        }

        if (movementAllowance != null)
        {
            movementAllowance.Spend(
                movementAllowance.GetMovementCost(lastChargedCell, cell)
            );
        }

        lastChargedCell = cell;
    }


    /// <summary>
    /// A freshly planned RoutePlanner route if one exists - it always
    /// takes priority - otherwise the queued remainder from a previous
    /// segment, otherwise null.
    /// </summary>
    private IReadOnlyList<Vector3Int> GetCandidatePath()
    {
        if (routePlanner != null && routePlanner.HasPlannedRoute)
        {
            return routePlanner.PlannedPath;
        }

        return HasQueuedRemainder ? queuedRemainder : null;
    }


    private void SetQueuedRemainder(List<Vector3Int> newRemainder)
    {
        queuedRemainder = newRemainder ?? new List<Vector3Int>();

        QueuedRemainderChanged?.Invoke();
    }
}
