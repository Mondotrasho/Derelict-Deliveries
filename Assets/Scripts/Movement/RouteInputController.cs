using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Input for route planning - click-to-destination plus drag-to-draw.
///
/// A plain click adds a waypoint: it does a normal A* leg to the clicked
/// cell, chained onto whatever's already planned (or from the player, if
/// nothing is planned yet) - so several clicks in a row build up a
/// multi-leg route rather than each one discarding the last. Holding and
/// dragging after a click manually appends/trims the route cell-by-cell
/// via RoutePlanner.AppendDraggedCell instead, following the drag gesture
/// directly rather than re-pathfinding from the player every frame.
/// Right-click cancels the plan, same as Escape.
///
/// Offers a fuller model than GridPlayerInput, which only ever supports
/// re-pathfind-to-cursor (no waypoint chaining, no manual drawing). Both
/// scripts stay in the project, but only one may actually drive a given
/// RoutePlanner at a time - see RouteInputOwnership. This script claims
/// ownership in OnEnable and refuses to activate (logging why) if
/// GridPlayerInput already holds the claim.
/// </summary>
public class RouteInputController : MonoBehaviour
{
    [Header("References")]

    [Tooltip("Camera used to convert mouse screen coordinates into world coordinates.")]
    [SerializeField]
    private Camera mainCamera;

    [Tooltip("Grid map used to convert world positions into grid cells.")]
    [SerializeField]
    private GridMap gridMap;

    [Tooltip("Owns the route being previewed/drawn by mouse input.")]
    [SerializeField]
    private RoutePlanner routePlanner;

    [Tooltip("Commits or cancels the previewed route.")]
    [SerializeField]
    private MovementPlanController movementPlanController;


    [Header("UI (Optional)")]

    [Tooltip("Optional Canvas Button. Clicking it calls the same CommitSegment() that pressing Enter does.")]
    [SerializeField]
    private Button commitButton;

    [Tooltip("Optional Canvas Button. Clicking it calls the same Cancel() that pressing Escape does.")]
    [SerializeField]
    private Button cancelButton;


    // Tracks the cell the cursor was last processed over, so a stationary
    // held mouse button doesn't reprocess the same cell every frame.
    private Vector3Int? lastCursorCell;

    // True from the initial mouse-down until it's released - distinguishes
    // "start a new route" (SetDestination) from "keep drawing" (AppendDraggedCell).
    private bool isDragging;


    /// <summary>
    /// Finds the Main Camera automatically if one has not been assigned.
    /// </summary>
    private void Awake()
    {
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
        }
    }


    private void OnEnable()
    {
        if (!RouteInputOwnership.TryClaim(routePlanner, this, out Behaviour currentOwner))
        {
            Debug.LogWarning(
                $"{name}: RouteInputController was not enabled because " +
                $"{currentOwner.GetType().Name} is already driving this RoutePlanner. " +
                "Only one route-input script can be active on a given RoutePlanner at a " +
                "time - disable that one first if you want RouteInputController active instead.",
                this
            );

            enabled = false;
            return;
        }

        if (commitButton != null)
        {
            commitButton.onClick.AddListener(HandleCommitButtonClicked);
        }

        if (cancelButton != null)
        {
            cancelButton.onClick.AddListener(HandleCancelButtonClicked);
        }
    }


    private void OnDisable()
    {
        RouteInputOwnership.Release(routePlanner, this);

        if (commitButton != null)
        {
            commitButton.onClick.RemoveListener(HandleCommitButtonClicked);
        }

        if (cancelButton != null)
        {
            cancelButton.onClick.RemoveListener(HandleCancelButtonClicked);
        }
    }


    /// <summary>
    /// Checks for click/drag, commit and cancel input each frame.
    /// </summary>
    private void Update()
    {
        if (IsRouteInputBlocked())
        {
            // If an interruption begins mid-drag, do not let the held
            // mouse resume editing the route when gameplay input unlocks.
            isDragging = false;
            lastCursorCell = null;
            return;
        }

        if (Mouse.current != null)
        {
            bool overUI =
                EventSystem.current != null &&
                EventSystem.current.IsPointerOverGameObject();

            if (!overUI)
            {
                if (Mouse.current.leftButton.wasPressedThisFrame)
                {
                    BeginRouteAtMousePosition();
                }
                else if (Mouse.current.leftButton.isPressed && isDragging)
                {
                    ContinueDragAtMousePosition();
                }
            }

            if (Mouse.current.leftButton.wasReleasedThisFrame)
            {
                // The route stays exactly as drawn - only Commit/Cancel
                // change it from here.
                isDragging = false;
            }

            if (Mouse.current.rightButton.wasPressedThisFrame)
            {
                movementPlanController?.Cancel();
            }
        }


        if (Keyboard.current != null)
        {
            if (Keyboard.current.enterKey.wasPressedThisFrame)
            {
                movementPlanController?.CommitSegment();
            }

            if (Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                movementPlanController?.Cancel();
            }
        }
    }


    /// <summary>
    /// Shared with commitButton.onClick - same call as the Enter key.
    /// </summary>
    private void HandleCommitButtonClicked()
    {
        if (!IsRouteInputBlocked())
        {
            movementPlanController?.CommitSegment();
        }
    }


    /// <summary>
    /// Shared with cancelButton.onClick - same call as the Escape key.
    /// </summary>
    private void HandleCancelButtonClicked()
    {
        if (!IsRouteInputBlocked())
        {
            movementPlanController?.Cancel();
        }
    }


    /// <summary>
    /// Blocking is owned by the movement/plan boundary, not by Combat, Events
    /// or any other feature directly. This prevents route editing, commit and
    /// cancel input from leaking through a modal gameplay interruption.
    /// </summary>
    private bool IsRouteInputBlocked()
    {
        return movementPlanController != null &&
               movementPlanController.IsMovementInterrupted;
    }


    /// <summary>
    /// Starts or extends the route toward the clicked cell, then arms drag
    /// mode so subsequent movement manually extends/trims it further.
    ///
    /// If nothing is planned yet, this is a fresh A* route from the
    /// player. If a route is already planned (from an earlier click this
    /// hasn't been committed yet), this instead chains a new leg onto the
    /// end of it via RoutePlanner.AppendDraggedCell - so repeated plain
    /// clicks build up a multi-waypoint route rather than each one
    /// discarding the last. Clicking back onto the existing route trims
    /// it via the same backtracking rule dragging uses.
    /// </summary>
    private void BeginRouteAtMousePosition()
    {
        if (!TryGetCellUnderMouse(out Vector3Int cell))
        {
            return;
        }

        lastCursorCell = cell;
        isDragging = true;

        routePlanner.AppendDraggedCell(cell);
    }


    /// <summary>
    /// Extends or trims the route toward wherever the cursor has moved to,
    /// only doing anything when that's a different cell than last frame.
    /// </summary>
    private void ContinueDragAtMousePosition()
    {
        if (!TryGetCellUnderMouse(out Vector3Int cell))
        {
            return;
        }

        if (lastCursorCell.HasValue &&
            lastCursorCell.Value == cell)
        {
            return;
        }

        lastCursorCell = cell;

        routePlanner.AppendDraggedCell(cell);
    }


    /// <summary>
    /// Converts the current mouse position into a grid cell. Returns false
    /// (and logs why) if a required reference is missing.
    /// </summary>
    private bool TryGetCellUnderMouse(out Vector3Int cell)
    {
        cell = default;

        if (mainCamera == null)
        {
            Debug.LogWarning(
                "RouteInputController has no Camera assigned."
            );

            return false;
        }

        if (gridMap == null)
        {
            Debug.LogWarning(
                "RouteInputController has no GridMap assigned."
            );

            return false;
        }

        if (routePlanner == null)
        {
            Debug.LogWarning(
                "RouteInputController has no RoutePlanner assigned."
            );

            return false;
        }


        // Read the mouse position using Unity's new Input System.
        Vector2 mouseScreenPosition =
            Mouse.current.position.ReadValue();


        // Convert the screen position into a world position.
        Vector3 mouseWorldPosition =
            mainCamera.ScreenToWorldPoint(
                new Vector3(
                    mouseScreenPosition.x,
                    mouseScreenPosition.y,
                    0.0f
                )
            );


        // This is a 2D game, so movement stays on the Z = 0 plane.
        mouseWorldPosition.z = 0.0f;


        cell =
            gridMap.WorldToCell(
                mouseWorldPosition
            );

        return true;
    }
}