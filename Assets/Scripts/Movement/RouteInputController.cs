using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Input for route planning. Two modes (Mode field):
///
/// ClickToGo (default): hovering previews the route to the cell under the
/// cursor; a click goes there straight away. Shift+click adds a waypoint
/// without going (the next plain click extends the chain and goes). Pressing
/// and dragging draws the route cell by cell and goes on release. A route
/// longer than one turn continues by itself on later turns when
/// MovementPlanController's Auto Continue Route is on. Right-click / Escape
/// cancel; Enter / the commit button still commit.
///
/// PlanThenCommit (the original model, described below): clicks and drags
/// only plan; Enter or the commit button starts the move.
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
    public enum RouteInputMode
    {
        ClickToGo,
        PlanThenCommit
    }

    [Header("Mode")]
    [Tooltip("ClickToGo: hover previews, click goes (Shift+click adds a waypoint, drag draws and goes on release). PlanThenCommit: the original plan, then Enter/Commit.")]
    [SerializeField]
    private RouteInputMode mode = RouteInputMode.ClickToGo;

    [Tooltip("ClickToGo only: show the route to whatever cell the cursor is over.")]
    [SerializeField]
    private bool hoverPreview = true;

    [Header("References")]

    [Tooltip("Camera used to convert mouse or touch screen coordinates into world coordinates.")]
    [SerializeField]
    private Camera mainCamera;

    [Tooltip("Grid map used to convert world positions into grid cells.")]
    [SerializeField]
    private GridMap gridMap;

    [Tooltip("Owns the route being previewed/drawn by pointer input.")]
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

    // ClickToGo state
    private bool chaining;          // Shift+click waypoints are waiting for a final click
    private bool hoverOwnsPlan;     // the current plan is only a hover preview
    private Vector3Int? lastHoverCell;

    // Touch has no hover state, so a finger press is treated as the same
    // route gesture as a left mouse press. Remember whether that gesture
    // began over UI so dragging off a button cannot start a route underneath it.
    private bool touchGestureActive;
    private bool touchStartedOverUI;


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
            // If an interruption begins mid-gesture, do not let the held
            // pointer resume editing the route when gameplay input unlocks.
            isDragging = false;
            lastCursorCell = null;
            lastHoverCell = null;
            touchGestureActive = false;
            touchStartedOverUI = false;
            return;
        }

        if (mode == RouteInputMode.ClickToGo)
        {
            UpdateClickToGo();
            return;
        }

        // Touch gets first chance to handle the frame. This prevents a
        // touchscreen that also exposes/simulates a mouse from processing
        // the same finger gesture twice.
        bool touchHandled = UpdatePlanThenCommitTouch();

        if (!touchHandled && Mouse.current != null)
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
    /// PlanThenCommit touch input mirrors the left-mouse gesture: touch down
    /// starts a route, dragging edits it cell by cell, and lifting the finger
    /// leaves the route planned for the normal Commit button.
    /// </summary>
    private bool UpdatePlanThenCommitTouch()
    {
        if (Touchscreen.current == null)
        {
            return false;
        }

        var touch = Touchscreen.current.primaryTouch;
        bool pressedThisFrame = touch.press.wasPressedThisFrame;
        bool isPressed = touch.press.isPressed;
        bool releasedThisFrame = touch.press.wasReleasedThisFrame;

        if (!pressedThisFrame && !isPressed && !releasedThisFrame && !touchGestureActive)
        {
            return false;
        }

        if (pressedThisFrame)
        {
            touchGestureActive = true;
            touchStartedOverUI = IsTouchOverUI(touch.touchId.ReadValue());

            if (!touchStartedOverUI)
            {
                BeginRouteAtScreenPosition(touch.position.ReadValue());
            }
        }
        else if (isPressed && touchGestureActive &&
                 !touchStartedOverUI && isDragging)
        {
            ContinueDragAtScreenPosition(touch.position.ReadValue());
        }

        if (releasedThisFrame)
        {
            isDragging = false;
            touchGestureActive = false;
            touchStartedOverUI = false;
        }

        return true;
    }


    /// <summary>
    /// ClickToGo: hover previews, click goes, Shift+click chains waypoints,
    /// drag draws and goes on release.
    /// </summary>
    private void UpdateClickToGo()
    {
        bool shift = Keyboard.current != null &&
                     (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);

        // Touch is handled before mouse input so a finger cannot also arrive
        // as a simulated mouse click on platforms that expose both devices.
        bool touchHandled = UpdateClickToGoTouch();

        if (!touchHandled && Mouse.current != null)
        {
            bool overUI =
                EventSystem.current != null &&
                EventSystem.current.IsPointerOverGameObject();

            if (!overUI && Mouse.current.leftButton.wasPressedThisFrame &&
                TryGetCellUnderMouse(out Vector3Int pressed))
            {
                lastCursorCell = pressed;
                isDragging = true;

                if (IsQueuedRemainderEnd(pressed) && !routePlanner.HasPlannedRoute)
                {
                    // Clicking the end of a waiting route just carries on along it.
                }
                else if (chaining || shift)
                {
                    routePlanner.AppendDraggedCell(pressed);   // add / extend a waypoint chain
                    chaining = true;
                }
                else if (!(hoverOwnsPlan && PlanEndsAt(pressed)))
                {
                    routePlanner.SetDestination(pressed);      // fresh route from the ship
                    ClampPlanToThisTurn();
                }

                hoverOwnsPlan = false;
            }
            else if (!overUI && Mouse.current.leftButton.isPressed && isDragging)
            {
                ContinueDragAtMousePosition();                  // draw the route cell by cell
            }

            if (Mouse.current.leftButton.wasReleasedThisFrame && isDragging)
            {
                isDragging = false;
                if (!shift)
                {
                    chaining = false;
                    movementPlanController?.CommitSegment();     // go
                }
            }

            if (Mouse.current.rightButton.wasPressedThisFrame)
            {
                CancelClickToGo();
            }

            // Hover preview is deliberately mouse-only. A touchscreen has no
            // stable hover position between gestures.
            if (!overUI && !isDragging && hoverPreview && !chaining &&
                !Mouse.current.leftButton.isPressed)
            {
                UpdateHoverPreview();
            }
        }

        if (Keyboard.current != null)
        {
            if (Keyboard.current.enterKey.wasPressedThisFrame)
            {
                chaining = false;
                movementPlanController?.CommitSegment();
            }

            if (Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                CancelClickToGo();
            }
        }
    }


    /// <summary>
    /// ClickToGo touch input behaves like a plain left-mouse gesture:
    /// touching a cell plans to it, dragging edits the path, and lifting the
    /// finger commits it. Shift-waypoint chaining remains a desktop feature.
    /// </summary>
    private bool UpdateClickToGoTouch()
    {
        if (Touchscreen.current == null)
        {
            return false;
        }

        var touch = Touchscreen.current.primaryTouch;
        bool pressedThisFrame = touch.press.wasPressedThisFrame;
        bool isPressed = touch.press.isPressed;
        bool releasedThisFrame = touch.press.wasReleasedThisFrame;

        if (!pressedThisFrame && !isPressed && !releasedThisFrame && !touchGestureActive)
        {
            return false;
        }

        if (pressedThisFrame)
        {
            touchGestureActive = true;
            touchStartedOverUI = IsTouchOverUI(touch.touchId.ReadValue());

            if (!touchStartedOverUI &&
                TryGetCellAtScreenPosition(touch.position.ReadValue(), out Vector3Int pressed))
            {
                lastCursorCell = pressed;
                isDragging = true;

                if (IsQueuedRemainderEnd(pressed) && !routePlanner.HasPlannedRoute)
                {
                    // Tapping the end of a waiting route carries on along it.
                }
                else if (chaining)
                {
                    // A touch can finish a waypoint chain that was started
                    // with Shift+click on desktop.
                    routePlanner.AppendDraggedCell(pressed);
                }
                else if (!(hoverOwnsPlan && PlanEndsAt(pressed)))
                {
                    routePlanner.SetDestination(pressed);
                    ClampPlanToThisTurn();
                }

                hoverOwnsPlan = false;
            }
        }
        else if (isPressed && touchGestureActive &&
                 !touchStartedOverUI && isDragging)
        {
            ContinueDragAtScreenPosition(touch.position.ReadValue());
        }

        if (releasedThisFrame)
        {
            if (isDragging && !touchStartedOverUI)
            {
                isDragging = false;
                chaining = false;
                movementPlanController?.CommitSegment();
            }
            else
            {
                isDragging = false;
            }

            touchGestureActive = false;
            touchStartedOverUI = false;
        }

        return true;
    }


    /// <summary>Re-plans to the hovered cell whenever the cursor moves onto a new one.</summary>
    private void UpdateHoverPreview()
    {
        // Never replace a route that is waiting to continue on a later turn.
        if (movementPlanController != null && movementPlanController.HasQueuedRemainder)
        {
            return;
        }

        if (!TryGetCellUnderMouse(out Vector3Int cell))
        {
            return;
        }

        if (lastHoverCell.HasValue && lastHoverCell.Value == cell && (hoverOwnsPlan || !routePlanner.HasPlannedRoute))
        {
            return;
        }

        lastHoverCell = cell;
        routePlanner.SetDestination(cell);
        ClampPlanToThisTurn();
        hoverOwnsPlan = true;
    }


    /// <summary>
    /// One-turn planning: cut the plan back to the furthest cell this turn's
    /// movement reaches along it, so the preview (and the click) never promise
    /// more than the ship will do. A target outside the green reach area moves
    /// the ship as far toward it as it can this turn.
    /// </summary>
    private void ClampPlanToThisTurn()
    {
        if (movementPlanController == null ||
            movementPlanController.Mode != MovementPlanController.PlanningMode.OneTurn)
        {
            return;
        }

        MovementAllowance allowance = movementPlanController.Allowance;
        IReadOnlyList<Vector3Int> path = routePlanner.PlannedPath;
        if (allowance == null || path == null || path.Count == 0)
        {
            return;
        }

        int affordable = allowance.GetAffordableCellCount(path);
        if (affordable >= path.Count)
        {
            return;
        }

        if (affordable <= 0)
        {
            routePlanner.ClearRoute();
            return;
        }

        Vector3Int furthest = path[affordable - 1];
        routePlanner.SetDestination(furthest);
    }


    private void CancelClickToGo()
    {
        chaining = false;
        hoverOwnsPlan = false;
        isDragging = false;
        lastHoverCell = null;
        movementPlanController?.Cancel();
    }


    private bool PlanEndsAt(Vector3Int cell)
    {
        IReadOnlyList<Vector3Int> path = routePlanner.PlannedPath;
        return path != null && path.Count > 0 && path[path.Count - 1] == cell;
    }


    private bool IsQueuedRemainderEnd(Vector3Int cell)
    {
        if (movementPlanController == null || !movementPlanController.HasQueuedRemainder)
        {
            return false;
        }

        IReadOnlyList<Vector3Int> rest = movementPlanController.QueuedRemainder;
        return rest.Count > 0 && rest[rest.Count - 1] == cell;
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
               !movementPlanController.CanAcceptPlayerInput;
    }


    /// <summary>
    /// Starts or extends the route toward the current mouse cell.
    /// </summary>
    private void BeginRouteAtMousePosition()
    {
        if (Mouse.current == null)
        {
            return;
        }

        BeginRouteAtScreenPosition(Mouse.current.position.ReadValue());
    }


    /// <summary>
    /// Starts or extends a route at a screen position, then arms drag mode.
    /// Shared by mouse and touch so both input types feed the same planning
    /// behaviour after the pointer position has been read.
    /// </summary>
    private void BeginRouteAtScreenPosition(Vector2 screenPosition)
    {
        if (!TryGetCellAtScreenPosition(screenPosition, out Vector3Int cell))
        {
            return;
        }

        lastCursorCell = cell;
        isDragging = true;

        routePlanner.AppendDraggedCell(cell);
    }


    /// <summary>
    /// Continues the current drag at the mouse position.
    /// </summary>
    private void ContinueDragAtMousePosition()
    {
        if (Mouse.current == null)
        {
            return;
        }

        ContinueDragAtScreenPosition(Mouse.current.position.ReadValue());
    }


    /// <summary>
    /// Extends or trims the route toward a screen position, only doing
    /// anything when the pointer has moved into a different grid cell.
    /// </summary>
    private void ContinueDragAtScreenPosition(Vector2 screenPosition)
    {
        if (!TryGetCellAtScreenPosition(screenPosition, out Vector3Int cell))
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
    /// Converts the current mouse position into a grid cell.
    /// </summary>
    private bool TryGetCellUnderMouse(out Vector3Int cell)
    {
        cell = default;

        if (Mouse.current == null)
        {
            return false;
        }

        return TryGetCellAtScreenPosition(
            Mouse.current.position.ReadValue(),
            out cell
        );
    }


    /// <summary>
    /// Converts a mouse or touch screen position into a grid cell.
    /// </summary>
    private bool TryGetCellAtScreenPosition(
        Vector2 screenPosition,
        out Vector3Int cell)
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


        // Convert the pointer's screen position into a world position.
        Vector3 pointerWorldPosition =
            mainCamera.ScreenToWorldPoint(
                new Vector3(
                    screenPosition.x,
                    screenPosition.y,
                    0.0f
                )
            );


        // This is a 2D game, so movement stays on the Z = 0 plane.
        pointerWorldPosition.z = 0.0f;


        cell =
            gridMap.WorldToCell(
                pointerWorldPosition
            );

        return true;
    }


    /// <summary>
    /// Uses the touch pointer ID so tapping a UI control cannot also create
    /// or edit a route underneath that control.
    /// </summary>
    private bool IsTouchOverUI(int touchId)
    {
        return EventSystem.current != null &&
               EventSystem.current.IsPointerOverGameObject(touchId);
    }

}
