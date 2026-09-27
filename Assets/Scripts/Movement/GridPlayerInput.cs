using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Simple click-to-move input for route planning: left click previews a
/// route to the clicked cell via RoutePlanner, and continues
/// re-previewing to whatever cell the cursor is over for as long as the
/// button stays held - so dragging live-updates the destination rather
/// than needing repeated clicks. Enter commits the previewed route (or
/// an optional assigned commitButton), Escape cancels it (or an optional
/// assigned cancelButton).
///
/// RouteInputController offers the fuller click-chaining/drag-drawing
/// model. Both are kept in the project rather than one being retired,
/// but only one may actually drive a given RoutePlanner at a time - see
/// RouteInputOwnership. This script claims ownership in OnEnable and
/// refuses to activate (logging why) if RouteInputController already
/// holds the claim.
/// </summary>
public class GridPlayerInput : MonoBehaviour
{
    [Header("References")]

    [Tooltip("Camera used to convert mouse or touch screen coordinates into world coordinates.")]
    [SerializeField]
    private Camera mainCamera;

    [Tooltip("Grid map used to convert world positions into grid cells.")]
    [SerializeField]
    private GridMap gridMap;

    [Tooltip("Owns the route being previewed by pointer input.")]
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


    // Tracks the last cell a preview was requested for, so a stationary
    // held mouse button doesn't re-run pathfinding every single frame.
    private Vector3Int? lastPreviewedCell;

    // Remember whether the current touch began over UI. A finger that starts
    // on a button must not begin route planning if it later drags onto the map.
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
                $"{name}: GridPlayerInput was not enabled because " +
                $"{currentOwner.GetType().Name} is already driving this RoutePlanner. " +
                "Only one route-input script can be active on a given RoutePlanner at a " +
                "time - disable that one first if you want GridPlayerInput active instead.",
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
            lastPreviewedCell = null;
            touchGestureActive = false;
            touchStartedOverUI = false;
            return;
        }

        // Touch gets first chance to handle the frame so a touchscreen that
        // also exposes/simulates a mouse cannot preview the route twice.
        bool touchHandled = UpdateTouch();

        if (!touchHandled && Mouse.current != null)
        {
            bool overUI =
                EventSystem.current != null &&
                EventSystem.current.IsPointerOverGameObject();

            // Clicks/drags over UI (buttons, panels, etc.) shouldn't also plan a route.
            if (!overUI)
            {
                if (Mouse.current.leftButton.wasPressedThisFrame)
                {
                    // A fresh press always re-previews, even if the cursor
                    // happens to be over the same cell as a previous drag.
                    lastPreviewedCell = null;

                    PreviewRouteToMousePosition();
                }
                else if (Mouse.current.leftButton.isPressed)
                {
                    PreviewRouteToMousePosition();
                }
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
    /// Touch mirrors the held-left-mouse preview behaviour. It plans while
    /// the primary finger is down; committing remains the job of the normal
    /// Commit button in this input controller.
    /// </summary>
    private bool UpdateTouch()
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
            touchStartedOverUI =
                EventSystem.current != null &&
                EventSystem.current.IsPointerOverGameObject(touch.touchId.ReadValue());

            if (!touchStartedOverUI)
            {
                lastPreviewedCell = null;
                PreviewRouteToScreenPosition(touch.position.ReadValue());
            }
        }
        else if (isPressed && touchGestureActive && !touchStartedOverUI)
        {
            PreviewRouteToScreenPosition(touch.position.ReadValue());
        }

        if (releasedThisFrame)
        {
            touchGestureActive = false;
            touchStartedOverUI = false;
        }

        return true;
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
    /// Reads the current mouse position and forwards it to the shared pointer
    /// preview path.
    /// </summary>
    private void PreviewRouteToMousePosition()
    {
        if (Mouse.current == null)
        {
            return;
        }

        PreviewRouteToScreenPosition(Mouse.current.position.ReadValue());
    }


    /// <summary>
    /// Converts a mouse or touch screen position into a grid cell and asks
    /// RoutePlanner to build a preview route toward it. Repeated samples in
    /// the same cell are ignored so held/dragged input does not rerun
    /// pathfinding unnecessarily.
    /// </summary>
    private void PreviewRouteToScreenPosition(Vector2 screenPosition)
    {
        if (mainCamera == null)
        {
            Debug.LogWarning(
                "GridPlayerInput has no Camera assigned."
            );

            return;
        }

        if (gridMap == null)
        {
            Debug.LogWarning(
                "GridPlayerInput has no GridMap assigned."
            );

            return;
        }

        if (routePlanner == null)
        {
            Debug.LogWarning(
                "GridPlayerInput has no RoutePlanner assigned."
            );

            return;
        }


        // Convert the pointer position into a world position.
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


        // Convert the world position into a Unity Grid cell.
        Vector3Int destinationCell =
            gridMap.WorldToCell(
                pointerWorldPosition
            );


        if (lastPreviewedCell.HasValue &&
            lastPreviewedCell.Value == destinationCell)
        {
            // Still on the same cell as the last sample - nothing changed.
            return;
        }

        lastPreviewedCell = destinationCell;


        // Ask RoutePlanner to preview a route to that cell.
        routePlanner.SetDestination(
            destinationCell
        );
    }

}
