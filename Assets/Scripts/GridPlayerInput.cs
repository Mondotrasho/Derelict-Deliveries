using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

/// <summary>
/// Handles simple mouse input for testing player grid movement.
///
/// When the left mouse button is clicked, the mouse position is converted
/// into a world position, then into a grid cell, and finally sent to the
/// PlayerGridController as a destination.
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

    [Tooltip("Player controller that receives the destination cell.")]
    [SerializeField]
    private PlayerGridController playerController;


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
    /// Checks for a left mouse click each frame.
    /// </summary>
    private void Update()
    {
        if (Mouse.current == null)
        {
            return;
        }

        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            // Clicks on UI (buttons, panels, etc.) shouldn't also move the player.
            if (EventSystem.current != null &&
                EventSystem.current.IsPointerOverGameObject())
            {
                return;
            }

            MovePlayerToMousePosition();
        }
    }


    /// <summary>
    /// Converts the current mouse position into a grid destination
    /// and sends it to the player controller.
    /// </summary>
    private void MovePlayerToMousePosition()
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

        if (playerController == null)
        {
            Debug.LogWarning(
                "GridPlayerInput has no PlayerGridController assigned."
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


        // Ask the player to pathfind and move to that cell.
        playerController.MoveToCell(
            destinationCell
        );
    }
}