using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Temporary testing input for route planning.
///
/// Left click previews a route to the clicked cell via RoutePlanner.
/// Enter commits the previewed route, Escape cancels it. This stands in
/// for the dedicated RouteInputController (drag-to-draw route
/// construction) planned for a later stage - once that exists, this
/// script can be retired.
/// </summary>
public class GridPlayerInput : MonoBehaviour
{
    [Header("References")]

    [Tooltip("Camera used to convert mouse screen coordinates into world coordinates.")]
    [SerializeField]
    private Camera mainCamera;

    [Tooltip("Grid map used to convert world positions into grid cells.")]
    [SerializeField]
    private GridMap gridMap;

    [Tooltip("Owns the route being previewed by mouse clicks.")]
    [SerializeField]
    private RoutePlanner routePlanner;

    [Tooltip("Commits or cancels the previewed route.")]
    [SerializeField]
    private MovementPlanController movementPlanController;


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


    /// <summary>
    /// Checks for click, commit and cancel input each frame.
    /// </summary>
    private void Update()
    {
        if (Mouse.current != null &&
            Mouse.current.leftButton.wasPressedThisFrame)
        {
            // Clicks on UI (buttons, panels, etc.) shouldn't also plan a route.
            if (EventSystem.current == null ||
                !EventSystem.current.IsPointerOverGameObject())
            {
                PreviewRouteToMousePosition();
            }
        }


        if (Keyboard.current != null)
        {
            if (Keyboard.current.enterKey.wasPressedThisFrame)
            {
                movementPlanController?.Commit();
            }

            if (Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                movementPlanController?.Cancel();
            }
        }
    }


    /// <summary>
    /// Converts the current mouse position into a grid cell and asks
    /// RoutePlanner to build a preview route toward it. This no longer
    /// moves the player directly - that only happens once the route is
    /// committed through MovementPlanController.
    /// </summary>
    private void PreviewRouteToMousePosition()
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


        // Convert the world position into a Unity Grid cell.
        Vector3Int destinationCell =
            gridMap.WorldToCell(
                mouseWorldPosition
            );


        // Ask RoutePlanner to preview a route to that cell.
        routePlanner.SetDestination(
            destinationCell
        );
    }
}