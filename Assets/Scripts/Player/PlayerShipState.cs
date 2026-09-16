using System;
using UnityEngine;

/// <summary>
/// Stable integration-facing facade for the player ship.
///
/// Feature code should prefer this component when it needs player state,
/// rather than reaching through PlayerGridController internals. The purpose is
/// to keep event, combat, UI and resource systems dependent on a small API even
/// if the physical movement implementation is refactored later.
///
/// This component does not implement those features. It only exposes:
/// - player cell/world position;
/// - clean real-cell entry notifications;
/// - player event tags/flags/counters;
/// - ShipResources;
/// - safe multi-owner movement interruption.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(PlayerGridController))]
[RequireComponent(typeof(ShipResources))]
public class PlayerShipState : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private PlayerGridController movementController;

    [SerializeField]
    private ShipResources resources;

    [Tooltip("Optional. Used by CancelCurrentJourney() to clear planned/queued route state as well as physical movement.")]
    [SerializeField]
    private MovementPlanController movementPlanController;


    [Header("Persistent Event State")]

    [Tooltip("Player/ship tags, flags and counters for event systems. Examples: Wanted, Enemy:FactionA, MetCultLeader.")]
    [SerializeField]
    private EventStateData eventState = new EventStateData();


    /// <summary>
    /// Raised only when physical movement genuinely reaches a new commanded
    /// grid cell. Unlike PlayerGridController.CellReached, this is not raised
    /// merely because pause/stop re-synchronised the controller to a cell.
    /// </summary>
    public event Action<Vector3Int> CellEntered;

    /// <summary>
    /// Raised whenever the overall interrupted/not-interrupted state changes.
    /// The argument is the new IsMovementInterrupted value.
    /// </summary>
    public event Action<bool> MovementInterruptionChanged;


    public EventStateData EventState
    {
        get { return eventState; }
    }


    public ShipResources Resources
    {
        get { return resources; }
    }


    public Vector3Int CurrentCell
    {
        get
        {
            return movementController != null
                ? movementController.CurrentCell
                : Vector3Int.zero;
        }
    }


    public Vector3 WorldPosition
    {
        get
        {
            return movementController != null
                ? movementController.transform.position
                : transform.position;
        }
    }


    public bool IsMoving
    {
        get
        {
            return movementController != null &&
                   movementController.IsMoving;
        }
    }


    public bool HasPausedMovement
    {
        get
        {
            return movementController != null &&
                   movementController.HasPausedMovement;
        }
    }


    public bool IsMovementInterrupted
    {
        get
        {
            return movementController != null &&
                   movementController.IsMovementInterrupted;
        }
    }


    private void Reset()
    {
        ResolveReferences();
    }


    private void Awake()
    {
        ResolveReferences();
    }


    private void OnEnable()
    {
        ResolveReferences();

        if (movementController == null)
        {
            return;
        }

        movementController.CellEntered += HandleCellEntered;
        movementController.MovementInterruptionChanged +=
            HandleMovementInterruptionChanged;
    }


    private void OnDisable()
    {
        if (movementController == null)
        {
            return;
        }

        movementController.CellEntered -= HandleCellEntered;
        movementController.MovementInterruptionChanged -=
            HandleMovementInterruptionChanged;
    }


    /// <summary>
    /// Temporarily blocks player movement for an outside system.
    ///
    /// Keep the returned handle and Release/Dispose it when finished.
    /// Multiple systems may hold independent handles safely.
    /// </summary>
    public MovementInterruptionHandle AcquireMovementInterruption(
        string reason)
    {
        if (movementController == null)
        {
            Debug.LogError(
                "PlayerShipState has no PlayerGridController assigned.",
                this
            );

            return null;
        }

        return movementController.AcquireMovementInterruption(reason);
    }


    /// <summary>
    /// Permanently discards the complete current journey: physical movement,
    /// any freshly planned route, and any queued multi-turn remainder.
    ///
    /// This is the integration-facing operation for an event/combat result
    /// that invalidates the old travel plan. It keeps outside systems from
    /// needing to know that physical and logical route state live in separate
    /// movement components.
    /// </summary>
    public void CancelCurrentJourney()
    {
        if (movementController != null)
        {
            movementController.StopMovement();
        }

        if (movementPlanController != null)
        {
            movementPlanController.Cancel();
        }
    }


    /// <summary>
    /// Permanently discards the current physical movement command.
    /// This is separate from releasing an interruption: a combat/event system
    /// can cancel the old journey first, then release its interruption without
    /// the previous path being resumed.
    /// </summary>
    public void CancelCurrentMovement()
    {
        if (movementController != null)
        {
            movementController.StopMovement();
        }
    }


    private void ResolveReferences()
    {
        if (movementController == null)
        {
            movementController = GetComponent<PlayerGridController>();
        }

        if (resources == null)
        {
            resources = GetComponent<ShipResources>();
        }

        if (movementPlanController == null)
        {
            movementPlanController =
                GetComponentInChildren<MovementPlanController>(true);
        }
    }


    private void HandleCellEntered(Vector3Int cell)
    {
        CellEntered?.Invoke(cell);
    }


    private void HandleMovementInterruptionChanged(bool isInterrupted)
    {
        MovementInterruptionChanged?.Invoke(isInterrupted);
    }
}
