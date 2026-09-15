using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Moves the player ship along a grid path.
///
/// The empty Player object moves through the world.
/// The PlayerSprite child rotates to face the direction of travel.
///
/// Speed is managed across the whole path:
/// - Accelerates away from the starting point.
/// - Cruises along straight sections.
/// - Slows slightly when approaching a turn.
/// - Accelerates again after the turn.
/// - Smoothly slows to a stop at the final destination.
///
/// This controller only decides HOW the player physically travels a path.
/// It does not decide what the route costs, whether it may be committed,
/// or which cells make up a route in the first place - see RoutePlanner
/// and MovementPlanController for that.
/// </summary>
public class PlayerGridController : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private GridMap gridMap;

    [SerializeField]
    private GridPathfinder pathfinder;

    [Tooltip("Child object containing the visible ship sprite.")]
    [SerializeField]
    private Transform playerSprite;


    [Header("Movement Speed")]

    [Tooltip("Normal travelling speed in world units per second.")]
    [SerializeField]
    private float cruiseSpeed = 2.5f;

    [Tooltip("Speed used while passing through a turn.")]
    [SerializeField]
    private float turnSpeed = 1.6f;

    [Tooltip("How quickly the ship can increase its speed.")]
    [SerializeField]
    private float acceleration = 7.0f;

    [Tooltip("How quickly the ship slows down.")]
    [SerializeField]
    private float deceleration = 8.0f;

    [Tooltip("Distance from a corner where the ship starts reducing speed.")]
    [SerializeField]
    private float turnSlowDistance = 0.35f;

    [Tooltip("Distance from the final destination where final braking begins.")]
    [SerializeField]
    private float destinationSlowDistance = 0.75f;

    [Tooltip("Speed floor while braking into the final cell, so the last few centimetres don't asymptotically crawl forever.")]
    [SerializeField]
    private float minimumFinalApproachSpeed = 0.4f;


    [Header("Rotation")]

    [Tooltip("Rotation speed of the visible ship sprite in degrees per second.")]
    [SerializeField]
    private float rotationSpeed = 540.0f;


    [Header("Arrival")]

    [Tooltip("Small distance considered close enough to a cell centre.")]
    [SerializeField]
    private float arrivalDistance = 0.005f;


    private Vector3Int currentCell;

    private Coroutine movementCoroutine;

    private float currentSpeed;


    /// <summary>
    /// Raised each time the player arrives at a new grid cell - including every
    /// intermediate cell along a multi-cell path, not just the final destination.
    /// Other systems (fog of war, triggers, etc.) subscribe to this rather than
    /// this controller needing to know they exist.
    /// </summary>
    public event Action<Vector3Int> CellReached;


    /// <summary>
    /// Raised once, after the player finishes travelling the complete path
    /// passed to MoveToCell/MoveAlongPath - i.e. after the final CellReached,
    /// not per cell. MovementPlanController (and later TurnManager-style
    /// systems) use this to know when it is safe to plan or commit the
    /// next route.
    /// </summary>
    public event Action RouteCompleted;


    /// <summary>
    /// True while the ship is travelling along a path.
    /// </summary>
    public bool IsMoving
    {
        get
        {
            return movementCoroutine != null;
        }
    }


    /// <summary>
    /// Current logical grid cell occupied by the player.
    /// </summary>
    public Vector3Int CurrentCell
    {
        get
        {
            return currentCell;
        }
    }


    private void Start()
    {
        InitialisePlayerPosition();
    }


    /// <summary>
    /// Finds the starting grid cell and snaps the Player root
    /// to the exact centre of that cell.
    /// </summary>
    private void InitialisePlayerPosition()
    {
        if (gridMap == null)
        {
            Debug.LogError(
                "PlayerGridController has no GridMap assigned."
            );

            return;
        }

        currentCell =
            gridMap.WorldToCell(transform.position);

        gridMap.SnapTransformToCell(
            transform,
            currentCell
        );

        currentSpeed = 0.0f;

        CellReached?.Invoke(currentCell);
    }


    /// <summary>
    /// Requests movement to a grid coordinate.
    ///
    /// The path is computed internally, which is convenient for direct
    /// click-to-move testing (see GridPlayerInput). Anything that plans a
    /// route ahead of time - RoutePlanner, then MovementPlanController -
    /// should use MoveAlongPath instead, so the path that gets executed is
    /// guaranteed to match the one that was previewed to the player.
    /// </summary>
    public void MoveToCell(Vector3Int destinationCell)
    {
        if (pathfinder == null)
        {
            Debug.LogError(
                "PlayerGridController is missing a required reference."
            );

            return;
        }


        List<Vector3Int> path =
            pathfinder.FindPath(
                currentCell,
                destinationCell
            );

        MoveAlongPath(path);
    }


    /// <summary>
    /// Executes an already-computed path, such as one just handed over by
    /// MovementPlanController.Commit(). The path is not recalculated here -
    /// whatever route was planned is exactly what gets travelled.
    /// </summary>
    public void MoveAlongPath(List<Vector3Int> path)
    {
        if (gridMap == null)
        {
            Debug.LogError(
                "PlayerGridController is missing a required reference."
            );

            return;
        }

        if (path == null || path.Count == 0)
        {
            return;
        }


        // A new movement command replaces the current path.
        if (movementCoroutine != null)
        {
            StopCoroutine(movementCoroutine);
        }


        movementCoroutine =
            StartCoroutine(
                FollowPath(path)
            );
    }


    /// <summary>
    /// Travels through the complete path while keeping speed continuous
    /// between neighbouring cells.
    /// </summary>
    private IEnumerator FollowPath(
        List<Vector3Int> path)
    {
        for (int pathIndex = 0;
             pathIndex < path.Count;
             pathIndex++)
        {
            Vector3Int destinationCell =
                path[pathIndex];

            bool isFinalCell =
                pathIndex == path.Count - 1;

            bool turnAhead =
                IsTurnAhead(
                    path,
                    pathIndex
                );


            yield return MoveToNextCell(
                destinationCell,
                turnAhead,
                isFinalCell
            );


            currentCell = destinationCell;

            CellReached?.Invoke(currentCell);
        }


        currentSpeed = 0.0f;
        movementCoroutine = null;

        RouteCompleted?.Invoke();
    }


    /// <summary>
    /// Moves from the current position to one cell centre.
    ///
    /// Unlike a per-cell easing animation, speed is not reset when each
    /// cell is reached. This makes long straight paths feel continuous.
    /// </summary>
    private IEnumerator MoveToNextCell(
        Vector3Int destinationCell,
        bool turnAhead,
        bool isFinalCell)
    {
        Vector3 destinationPosition =
            gridMap.CellToWorld(destinationCell);


        while (Vector3.Distance(
                   transform.position,
                   destinationPosition) > arrivalDistance)
        {
            Vector3 toDestination =
                destinationPosition -
                transform.position;

            float distanceToCell =
                toDestination.magnitude;

            Vector3 direction =
                toDestination.normalized;


            float targetSpeed =
                CalculateTargetSpeed(
                    distanceToCell,
                    turnAhead,
                    isFinalCell
                );


            currentSpeed =
                MoveSpeedTowards(
                    currentSpeed,
                    targetSpeed
                );


            float movementDistance =
                currentSpeed *
                Time.deltaTime;


            // Do not travel past the exact centre of the target cell.
            movementDistance =
                Mathf.Min(
                    movementDistance,
                    distanceToCell
                );


            transform.position +=
                direction *
                movementDistance;


            RotateSpriteTowards(
                direction
            );


            yield return null;
        }


        transform.position = destinationPosition;
    }


    /// <summary>
    /// Chooses the desired speed for the ship's current situation.
    /// </summary>
    private float CalculateTargetSpeed(
        float distanceToCell,
        bool turnAhead,
        bool isFinalCell)
    {
        // Final destination gets the strongest slowdown.
        if (isFinalCell)
        {
            float slowFactor =
                Mathf.Clamp01(
                    distanceToCell /
                    destinationSlowDistance
                );

            float brakingSpeed =
                Mathf.Lerp(
                    0.0f,
                    cruiseSpeed,
                    SmoothStep(slowFactor)
                );

            // SmoothStep shrinks roughly with the square of the remaining
            // distance, so without a floor the last few centimetres take
            // longer and longer to close - the ship looks like it never
            // quite arrives. Flooring the speed guarantees the approach
            // finishes in bounded time; movementDistance is still clamped
            // to distanceToCell in MoveToNextCell, so this cannot overshoot
            // the cell centre.
            return Mathf.Max(
                brakingSpeed,
                minimumFinalApproachSpeed
            );
        }


        // When a direction change is coming, gently reduce speed
        // while approaching the corner.
        if (turnAhead &&
            distanceToCell < turnSlowDistance)
        {
            float turnFactor =
                Mathf.Clamp01(
                    distanceToCell /
                    turnSlowDistance
                );

            return Mathf.Lerp(
                turnSpeed,
                cruiseSpeed,
                SmoothStep(turnFactor)
            );
        }


        return cruiseSpeed;
    }


    /// <summary>
    /// Moves currentSpeed toward targetSpeed using separate acceleration
    /// and deceleration values.
    /// </summary>
    private float MoveSpeedTowards(
        float speed,
        float targetSpeed)
    {
        float rate =
            targetSpeed > speed
                ? acceleration
                : deceleration;


        return Mathf.MoveTowards(
            speed,
            targetSpeed,
            rate * Time.deltaTime
        );
    }


    /// <summary>
    /// Returns true when travelling into this cell will be followed
    /// by a change in direction.
    /// </summary>
    private bool IsTurnAhead(
        List<Vector3Int> path,
        int currentPathIndex)
    {
        // There cannot be a turn after the final path cell.
        if (currentPathIndex >= path.Count - 1)
        {
            return false;
        }


        Vector3Int previousCell;

        if (currentPathIndex == 0)
        {
            previousCell = currentCell;
        }
        else
        {
            previousCell =
                path[currentPathIndex - 1];
        }


        Vector3Int currentPathCell =
            path[currentPathIndex];

        Vector3Int nextPathCell =
            path[currentPathIndex + 1];


        Vector3Int incomingDirection =
            currentPathCell -
            previousCell;

        Vector3Int outgoingDirection =
            nextPathCell -
            currentPathCell;


        return incomingDirection !=
               outgoingDirection;
    }


    /// <summary>
    /// Rotates the visible ship toward its current movement direction.
    ///
    /// The sprite artwork is expected to face UP when Z rotation is zero.
    /// </summary>
    private void RotateSpriteTowards(
        Vector3 direction)
    {
        if (playerSprite == null)
        {
            return;
        }


        if (direction.sqrMagnitude <
            0.0001f)
        {
            return;
        }


        float targetAngle =
            Mathf.Atan2(
                direction.y,
                direction.x
            )
            * Mathf.Rad2Deg
            - 90.0f;


        Quaternion targetRotation =
            Quaternion.Euler(
                0.0f,
                0.0f,
                targetAngle
            );


        playerSprite.rotation =
            Quaternion.RotateTowards(
                playerSprite.rotation,
                targetRotation,
                rotationSpeed *
                Time.deltaTime
            );
    }


    /// <summary>
    /// Standard smooth interpolation curve used when blending speeds.
    /// </summary>
    private float SmoothStep(float time)
    {
        time =
            Mathf.Clamp01(time);

        return time *
               time *
               (3.0f - 2.0f * time);
    }


    /// <summary>
    /// Immediately stops the current movement command.
    ///
    /// The Player remains at its current world position. This is treated
    /// as an interruption rather than a completion, so RouteCompleted is
    /// not raised here - only CellReached, since the player's logical cell
    /// still needs to be re-synced to wherever it actually stopped.
    /// </summary>
    public void StopMovement()
    {
        if (movementCoroutine == null)
        {
            return;
        }


        StopCoroutine(
            movementCoroutine
        );

        movementCoroutine = null;

        currentSpeed = 0.0f;


        currentCell =
            gridMap.WorldToCell(
                transform.position
            );

        CellReached?.Invoke(currentCell);
    }
}