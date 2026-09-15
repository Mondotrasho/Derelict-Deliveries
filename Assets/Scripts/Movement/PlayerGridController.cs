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
///
/// Optionally reports real cells crossed to a TurnManager as ticks (see
/// cellsPerTick) - this is the only place movement and the shared clock
/// touch. TurnManager itself has no reference back to this script.
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


    [Header("Path Smoothing")]

    [Tooltip("Flies a smoothed curve through the path instead of straight cell-to-cell segments, so a staircase-shaped grid path doesn't translate into a series of sharp turns. The ship still passes exactly through every cell centre - CellReached still fires at the right place, at the right time - only the curve BETWEEN cells is smoothed.")]
    [SerializeField]
    private bool smoothPath = true;

    [Tooltip("How many points to interpolate per grid step when smoothing. Higher looks smoother but costs more distance checks per step.")]
    [Range(2, 16)]
    [SerializeField]
    private int smoothingSamplesPerStep = 6;

    [Tooltip("How strongly the curve bends through each waypoint. 0 = full Catmull-Rom smoothing, 1 = an eased straight line with no influence from neighbouring cells.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float smoothingTension = 0.5f;

    [Tooltip("Minimum direction change (in degrees) between consecutive movement steps before the turn-slowdown kicks in. Only used by the StepByStep movement style - ArcLength has no turn-slowdown branch at all, and with smoothing on, consecutive samples already change direction gradually regardless.")]
    [Range(1f, 90f)]
    [SerializeField]
    private float turnAngleThresholdDegrees = 20.0f;


    [Header("Movement Style")]

    [Tooltip("StepByStep (the original approach) walks the path one waypoint at a time, each with its own speed calculation (turn slowdown, final braking measured against that waypoint). ArcLength instead flattens the whole route into one polyline up front and tracks a single distance-travelled scalar along it, with speed purely a function of distance travelled/remaining across the ENTIRE route - simpler, no per-waypoint edge cases, at the cost of no turn-slowdown feature.")]
    [SerializeField]
    private MovementStyle movementStyle = MovementStyle.StepByStep;

    [Tooltip("ArcLength only. Distance travelled before reaching full cruise speed when leaving a standing start.")]
    [SerializeField]
    private float departureAccelDistance = 0.75f;

    [Tooltip("ArcLength only. How far ahead of the ship's nearest point on the path it steers. Smaller values hug the path more tightly; larger values produce wider, smoother turns.")]
    [SerializeField]
    private float pathLookAheadDistance = 0.20f;

    [Tooltip("ArcLength only. When the ship is facing away from the path target, reduce its speed so it cannot fly wide through a corner while rotating.")]
    [SerializeField]
    private bool slowForHeadingError = true;

    [Tooltip("ArcLength only. Heading error in degrees at which corner-speed reduction reaches its strongest effect.")]
    [Range(1.0f, 180.0f)]
    [SerializeField]
    private float headingSlowAngle = 75.0f;

    [Tooltip("ArcLength only. Lowest fraction of current target speed allowed while the ship is turning sharply.")]
    [Range(0.05f, 1.0f)]
    [SerializeField]
    private float minimumHeadingSpeedFactor = 0.35f;


    private enum MovementStyle
    {
        StepByStep,
        ArcLength
    }


    [Header("Timing")]

    [Tooltip("Optional. Real cells moved are reported to this as ticks, at the rate below - the mover's own speed relative to everything else that reacts to ticks (fog decay, later enemy stepping). Not required for movement to work.")]
    [SerializeField]
    private TurnManager turnManager;

    [Tooltip("How many cells this mover crosses per tick reported to TurnManager. 1 is normal speed. 2 means the world only reacts once for every 2 cells travelled - this is the actual mechanism behind a temporary 'boosted' speed: raise this for the duration of the boost, then put it back.")]
    [SerializeField, Min(1)]
    private int cellsPerTick = 1;


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

    // Cells crossed since the last AdvanceTick() report - carries a
    // remainder forward when cellsPerTick > 1, so an in-progress count
    // toward the next tick is never lost between calls.
    private int cellsSinceLastTick;

    // Tracked independently of playerSprite so heading works identically
    // with or without a visual sprite assigned, and survives across
    // separate MoveAlongPath calls rather than resetting between routes.
    private Quaternion currentHeading = Quaternion.identity;


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

        // Match whatever the sprite already happens to be facing in the
        // scene, so the very first move doesn't snap-rotate from identity.
        if (playerSprite != null)
        {
            currentHeading = playerSprite.rotation;
        }

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
                movementStyle == MovementStyle.ArcLength
                    ? FollowPathArcLength(path)
                    : FollowPath(path)
            );
    }


    /// <summary>
    /// One point the ship physically travels through. ArrivedCell is only
    /// set for the sample that coincides exactly with a real grid cell
    /// centre - every other sample (interpolated smoothing points) is
    /// pure motion with no gameplay event attached, so fog/vision/turn
    /// systems still see exactly one CellReached per actual cell.
    /// </summary>
    private struct PathStep
    {
        public Vector3 WorldPosition;
        public Vector3Int? ArrivedCell;
    }


    /// <summary>
    /// Travels through the complete path while keeping speed continuous
    /// between neighbouring steps.
    /// </summary>
    private IEnumerator FollowPath(
        List<Vector3Int> path)
    {
        List<PathStep> steps = BuildExecutionSteps(path);

        Vector3 previousPosition = transform.position;

        for (int stepIndex = 0;
             stepIndex < steps.Count;
             stepIndex++)
        {
            PathStep step = steps[stepIndex];

            bool isFinalStep =
                stepIndex == steps.Count - 1;

            bool turnAhead =
                IsTurnAhead(
                    steps,
                    stepIndex,
                    previousPosition
                );


            yield return MoveToPosition(
                step.WorldPosition,
                turnAhead,
                isFinalStep
            );


            previousPosition = step.WorldPosition;

            if (step.ArrivedCell.HasValue)
            {
                currentCell = step.ArrivedCell.Value;
                CellReached?.Invoke(currentCell);
                ReportMovementTick();
            }
        }


        currentSpeed = 0.0f;
        movementCoroutine = null;

        RouteCompleted?.Invoke();
    }


    /// <summary>
    /// Alternative to FollowPath for MovementStyle.ArcLength: flattens the
    /// whole route (whatever BuildExecutionSteps produced - smoothed or
    /// not) into one polyline with a cumulative-distance table, then
    /// tracks a single distanceTravelled scalar along it. Position is a
    /// direct lookup into that table rather than a per-waypoint coroutine
    /// call, and speed is a single continuous function of distance
    /// travelled/remaining across the entire route - no per-waypoint
    /// turn-slowdown branch, no restarting the speed calculation at every
    /// sample. Arrival is just distanceTravelled reaching totalLength.
    /// </summary>
    private IEnumerator FollowPathArcLength(
        List<Vector3Int> path)
    {
        List<PathStep> steps = BuildExecutionSteps(path);


        List<Vector3> points = new List<Vector3>(steps.Count + 1);
        List<float> cumulativeDistances = new List<float>(steps.Count + 1);

        points.Add(transform.position);
        cumulativeDistances.Add(0.0f);

        for (int i = 0; i < steps.Count; i++)
        {
            Vector3 previousPoint = points[points.Count - 1];
            Vector3 nextPoint = steps[i].WorldPosition;

            float segmentLength =
                Vector3.Distance(previousPoint, nextPoint);

            points.Add(nextPoint);
            cumulativeDistances.Add(
                cumulativeDistances[cumulativeDistances.Count - 1] +
                segmentLength
            );
        }


        float totalLength =
            cumulativeDistances[cumulativeDistances.Count - 1];


        if (totalLength < 0.0001f)
        {
            int zeroLengthArrivalIndex = 0;

            FireRemainingArrivals(
                steps,
                ref zeroLengthArrivalIndex
            );

            currentSpeed = 0.0f;
            movementCoroutine = null;

            RouteCompleted?.Invoke();

            yield break;
        }


        // nextArrivalIndex refers to the next PathStep whose logical arrival
        // still needs to be considered. Interpolated smoothing samples have
        // no ArrivedCell and are skipped naturally by FireArrival().
        int nextArrivalIndex = 0;

        // Physical path progress is never allowed to move backwards. The
        // closest-point calculation below is based on the ship's REAL world
        // position, rather than an independently advancing virtual cursor.
        float physicalPathProgress = 0.0f;


        while (physicalPathProgress < totalLength)
        {
            float closestProgress =
                FindClosestDistanceAlongPolyline(
                    transform.position,
                    points,
                    cumulativeDistances
                );

            // Do not let tiny numerical changes around a corner make logical
            // route progress move backwards.
            physicalPathProgress =
                Mathf.Max(
                    physicalPathProgress,
                    closestProgress
                );


            float distanceRemaining =
                totalLength - physicalPathProgress;

            float targetSpeed =
                CalculateArcLengthSpeed(
                    physicalPathProgress,
                    distanceRemaining
                );


            // Steer toward a point only a small distance ahead of the ship's
            // actual position on the path. The target can therefore never run
            // several cells ahead while the ship is still rotating through a
            // corner.
            float lookAheadDistance =
                Mathf.Max(
                    0.001f,
                    pathLookAheadDistance
                );

            float steeringDistance =
                Mathf.Min(
                    totalLength,
                    physicalPathProgress + lookAheadDistance
                );

            Vector3 steeringTarget =
                SamplePolylineAtDistance(
                    points,
                    cumulativeDistances,
                    steeringDistance
                );

            Vector3 desiredDirection =
                steeringTarget - transform.position;


            // A large heading error means the ship is still physically
            // turning toward the route. Reducing translation speed here keeps
            // it from tracing a large outward arc through sharp corners.
            if (slowForHeadingError &&
                desiredDirection.sqrMagnitude > 0.0001f)
            {
                Vector3 currentForward =
                    currentHeading * Vector3.up;

                float headingError =
                    Vector3.Angle(
                        currentForward,
                        desiredDirection
                    );

                float headingFactor =
                    1.0f -
                    Mathf.Clamp01(
                        headingError /
                        Mathf.Max(
                            1.0f,
                            headingSlowAngle
                        )
                    );

                float speedFactor =
                    Mathf.Lerp(
                        minimumHeadingSpeedFactor,
                        1.0f,
                        SmoothStep(headingFactor)
                    );

                targetSpeed *= speedFactor;
            }


            currentSpeed =
                MoveSpeedTowards(
                    currentSpeed,
                    targetSpeed
                );


            float movementDistance =
                currentSpeed *
                Time.deltaTime;


            // Rotate first, then translate strictly along the resulting
            // heading. This preserves the existing rule that the visible ship
            // never moves in a direction different from the one it faces.
            Vector3 travelDirection =
                UpdateHeading(desiredDirection);

            transform.position +=
                travelDirection *
                movementDistance;


            // Re-evaluate progress AFTER moving so CellReached is tied to the
            // real ship position, not to the steering target.
            float newClosestProgress =
                FindClosestDistanceAlongPolyline(
                    transform.position,
                    points,
                    cumulativeDistances
                );

            physicalPathProgress =
                Mathf.Max(
                    physicalPathProgress,
                    newClosestProgress
                );


            // Fire every reached real cell in order. A cell is considered
            // reached once the ship's physical path projection reaches that
            // point on the route. Interpolated smoothing samples do not raise
            // gameplay events.
            while (nextArrivalIndex < steps.Count &&
                   physicalPathProgress >=
                   cumulativeDistances[nextArrivalIndex + 1] - 0.0001f)
            {
                FireArrival(
                    steps,
                    nextArrivalIndex
                );

                nextArrivalIndex++;
            }


            // Once the physical route progress reaches the end, finish by
            // moving toward the exact final route point. This prevents a ship
            // that cut slightly inside the last bend from stopping offset.
            if (distanceRemaining <= arrivalDistance)
            {
                break;
            }


            yield return null;
        }


        Vector3 finalPosition =
            points[points.Count - 1];

        while (Vector3.Distance(
                   transform.position,
                   finalPosition) > arrivalDistance)
        {
            Vector3 desiredDirection =
                finalPosition -
                transform.position;

            currentSpeed =
                MoveSpeedTowards(
                    currentSpeed,
                    minimumFinalApproachSpeed
                );

            float movementDistance =
                Mathf.Min(
                    currentSpeed * Time.deltaTime,
                    desiredDirection.magnitude
                );

            Vector3 travelDirection =
                UpdateHeading(desiredDirection);

            transform.position +=
                travelDirection *
                movementDistance;

            yield return null;
        }


        transform.position = finalPosition;

        FireRemainingArrivals(
            steps,
            ref nextArrivalIndex
        );

        currentSpeed = 0.0f;
        movementCoroutine = null;

        RouteCompleted?.Invoke();
    }


    /// <summary>
    /// Finds the point on the complete polyline that is closest to a world
    /// position and returns that point as a distance measured from the start
    /// of the route.
    ///
    /// ArcLength movement uses this to make route progress follow the actual
    /// ship instead of an invisible cursor that can run ahead during turns.
    /// </summary>
    private float FindClosestDistanceAlongPolyline(
        Vector3 worldPosition,
        List<Vector3> points,
        List<float> cumulativeDistances)
    {
        float bestSqrDistance = float.PositiveInfinity;
        float bestDistanceAlongPath = 0.0f;

        for (int i = 0; i < points.Count - 1; i++)
        {
            Vector3 segmentStart = points[i];
            Vector3 segmentEnd = points[i + 1];

            Vector3 segment =
                segmentEnd -
                segmentStart;

            float segmentSqrLength =
                segment.sqrMagnitude;

            float segmentT = 0.0f;

            if (segmentSqrLength > 0.000001f)
            {
                segmentT =
                    Mathf.Clamp01(
                        Vector3.Dot(
                            worldPosition - segmentStart,
                            segment
                        ) /
                        segmentSqrLength
                    );
            }


            Vector3 closestPoint =
                segmentStart +
                segment * segmentT;

            float sqrDistance =
                (worldPosition - closestPoint).sqrMagnitude;


            if (sqrDistance < bestSqrDistance)
            {
                bestSqrDistance = sqrDistance;

                float segmentLength =
                    cumulativeDistances[i + 1] -
                    cumulativeDistances[i];

                bestDistanceAlongPath =
                    cumulativeDistances[i] +
                    segmentLength * segmentT;
            }
        }


        return bestDistanceAlongPath;
    }


    /// <summary>
    /// Speed as a single continuous function of distance travelled and
    /// distance remaining across the WHOLE route - eases up to cruise
    /// speed leaving a standing start, cruises, eases down approaching
    /// the final destination. No turn-slowdown term; ArcLength trades
    /// that feature for not needing to know where along the route it is
    /// relative to individual waypoints at all.
    /// </summary>
    private float CalculateArcLengthSpeed(
        float distanceTravelled,
        float distanceRemaining)
    {
        if (distanceRemaining < destinationSlowDistance)
        {
            float slowFactor =
                Mathf.Clamp01(
                    distanceRemaining /
                    destinationSlowDistance
                );

            float brakingSpeed =
                Mathf.Lerp(
                    0.0f,
                    cruiseSpeed,
                    SmoothStep(slowFactor)
                );

            return Mathf.Max(
                brakingSpeed,
                minimumFinalApproachSpeed
            );
        }

        if (distanceTravelled < departureAccelDistance)
        {
            float accelFactor =
                Mathf.Clamp01(
                    distanceTravelled /
                    departureAccelDistance
                );

            return Mathf.Lerp(
                minimumFinalApproachSpeed,
                cruiseSpeed,
                SmoothStep(accelFactor)
            );
        }

        return cruiseSpeed;
    }


    /// <summary>
    /// Finds which segment of the polyline the given distance-along-the-
    /// route falls in and Lerps between its two endpoints. A linear scan
    /// is fine here - routes are short enough (a handful of cells, each
    /// with a handful of smoothing samples) that this is nowhere near a
    /// hot path.
    /// </summary>
    private Vector3 SamplePolylineAtDistance(
        List<Vector3> points,
        List<float> cumulativeDistances,
        float distance)
    {
        for (int i = 0; i < cumulativeDistances.Count - 1; i++)
        {
            if (distance <= cumulativeDistances[i + 1])
            {
                float segmentStart = cumulativeDistances[i];
                float segmentEnd = cumulativeDistances[i + 1];
                float segmentLength = segmentEnd - segmentStart;

                float localT =
                    segmentLength > 0.0001f
                        ? (distance - segmentStart) / segmentLength
                        : 0.0f;

                return Vector3.Lerp(
                    points[i],
                    points[i + 1],
                    localT
                );
            }
        }

        return points[points.Count - 1];
    }


    /// <summary>
    /// Updates currentCell and raises CellReached for one step, if that
    /// step actually corresponds to a real grid cell (smoothing samples
    /// in between do not).
    /// </summary>
    private void FireArrival(List<PathStep> steps, int index)
    {
        if (!steps[index].ArrivedCell.HasValue)
        {
            return;
        }

        currentCell = steps[index].ArrivedCell.Value;
        CellReached?.Invoke(currentCell);
        ReportMovementTick();
    }


    /// <summary>
    /// Reports one real cell of movement toward the next tick, at
    /// cellsPerTick. Only called from the two places above that fire
    /// CellReached for an actual cell crossed during active movement -
    /// not from the initial spawn-position announcement or from
    /// StopMovement's re-sync, since neither of those represents a full
    /// cell of travel.
    /// </summary>
    private void ReportMovementTick()
    {
        if (turnManager == null)
        {
            return;
        }

        cellsSinceLastTick++;

        int rate = Mathf.Max(1, cellsPerTick);

        if (cellsSinceLastTick < rate)
        {
            return;
        }

        cellsSinceLastTick -= rate;
        turnManager.AdvanceTick();
    }


    /// <summary>
    /// Fires every not-yet-fired arrival from startIndex onward, in
    /// order. Used when the route resolves in one go (zero-length route)
    /// or to guarantee nothing is left unfired at the very end.
    /// </summary>
    private void FireRemainingArrivals(
        List<PathStep> steps,
        ref int nextArrivalIndex)
    {
        while (nextArrivalIndex < steps.Count)
        {
            FireArrival(steps, nextArrivalIndex);
            nextArrivalIndex++;
        }
    }


    /// <summary>
    /// Converts a grid cell path into the actual sequence of world points
    /// the ship will travel through.
    ///
    /// With smoothing off, this is just each cell centre, one PathStep
    /// each - identical to the original cell-to-cell behaviour.
    ///
    /// With smoothing on, a Catmull-Rom-style curve is sampled through
    /// the ship's current position and every cell centre in the path.
    /// Only the last sample of each segment (t = 1) coincides exactly
    /// with a real cell - every earlier sample is an interpolated point
    /// with no ArrivedCell, so the smoothing never changes which cell the
    /// ship is considered to be in, or when.
    /// </summary>
    private List<PathStep> BuildExecutionSteps(List<Vector3Int> path)
    {
        List<PathStep> steps = new List<PathStep>();

        if (!smoothPath)
        {
            foreach (Vector3Int cell in path)
            {
                steps.Add(new PathStep
                {
                    WorldPosition = gridMap.CellToWorld(cell),
                    ArrivedCell = cell
                });
            }

            return steps;
        }


        List<Vector3> controlPoints = new List<Vector3>();

        controlPoints.Add(transform.position);

        foreach (Vector3Int cell in path)
        {
            controlPoints.Add(
                gridMap.CellToWorld(cell)
            );
        }


        int segmentCount = controlPoints.Count - 1;

        for (int segmentIndex = 0;
             segmentIndex < segmentCount;
             segmentIndex++)
        {
            Vector3 p1 = controlPoints[segmentIndex];
            Vector3 p2 = controlPoints[segmentIndex + 1];

            // Open spline - duplicate the nearer endpoint instead of
            // wrapping around when there's no real neighbour to look at.
            Vector3 p0 =
                segmentIndex > 0
                    ? controlPoints[segmentIndex - 1]
                    : p1;

            Vector3 p3 =
                segmentIndex < segmentCount - 1
                    ? controlPoints[segmentIndex + 2]
                    : p2;


            int sampleCount =
                Mathf.Max(1, smoothingSamplesPerStep);

            for (int sample = 1; sample <= sampleCount; sample++)
            {
                float t = sample / (float)sampleCount;

                Vector3 point =
                    SampleSmoothedPoint(
                        p0, p1, p2, p3, t
                    );

                steps.Add(new PathStep
                {
                    WorldPosition = point,
                    ArrivedCell =
                        sample == sampleCount
                            ? path[segmentIndex]
                            : (Vector3Int?)null
                });
            }
        }

        return steps;
    }


    /// <summary>
    /// Cardinal spline sample between p1 (t=0) and p2 (t=1), using p0/p3
    /// as neighbours to shape the tangents at each end. Always passes
    /// exactly through p1 and p2 regardless of tension - smoothing only
    /// ever bends the curve BETWEEN them, never moves the endpoints
    /// themselves. smoothingTension of 0 gives the classic Catmull-Rom
    /// tangent; 1 zeroes the tangents out entirely, giving an eased
    /// straight line with no influence from neighbouring cells.
    /// </summary>
    private Vector3 SampleSmoothedPoint(
        Vector3 p0,
        Vector3 p1,
        Vector3 p2,
        Vector3 p3,
        float t)
    {
        float tangentScale = 1.0f - smoothingTension;

        Vector3 m1 = tangentScale * (p2 - p0) * 0.5f;
        Vector3 m2 = tangentScale * (p3 - p1) * 0.5f;

        float t2 = t * t;
        float t3 = t2 * t;

        float h00 = (2.0f * t3) - (3.0f * t2) + 1.0f;
        float h10 = t3 - (2.0f * t2) + t;
        float h01 = (-2.0f * t3) + (3.0f * t2);
        float h11 = t3 - t2;

        return (h00 * p1) +
               (h10 * m1) +
               (h01 * p2) +
               (h11 * m2);
    }


    /// <summary>
    /// Moves from the current position to one step's target position.
    ///
    /// Unlike a per-step easing animation, speed is not reset when each
    /// step is reached. This makes long straight paths feel continuous.
    /// </summary>
    private IEnumerator MoveToPosition(
        Vector3 destinationPosition,
        bool turnAhead,
        bool isFinalStep)
    {
        while (Vector3.Distance(
                   transform.position,
                   destinationPosition) > arrivalDistance)
        {
            Vector3 toDestination =
                destinationPosition -
                transform.position;

            float distanceToCell =
                toDestination.magnitude;

            Vector3 desiredDirection =
                toDestination.normalized;


            float targetSpeed =
                CalculateTargetSpeed(
                    distanceToCell,
                    turnAhead,
                    isFinalStep
                );


            currentSpeed =
                MoveSpeedTowards(
                    currentSpeed,
                    targetSpeed
                );


            float movementDistance =
                currentSpeed *
                Time.deltaTime;


            // Do not travel past the exact target position.
            movementDistance =
                Mathf.Min(
                    movementDistance,
                    distanceToCell
                );


            // Rotate heading toward the target, then move along whatever
            // the ship is now actually facing - never along the raw
            // direction-to-target - so translation and facing can never
            // disagree, even mid-turn.
            Vector3 travelDirection =
                UpdateHeading(desiredDirection);

            transform.position +=
                travelDirection *
                movementDistance;


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
        bool isFinalStep)
    {
        // Final destination gets the strongest slowdown.
        if (isFinalStep)
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
            // to distanceToCell in MoveToPosition, so this cannot overshoot
            // the destination.
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
    /// Returns true when the direction change between the incoming and
    /// outgoing movement at this step is sharp enough to count as a turn.
    /// Angle-based (rather than exact-direction-equality) so it works for
    /// both raw grid steps (orthogonal or diagonal) and interpolated
    /// smoothing samples, which will essentially never be exactly equal
    /// as floating-point vectors.
    /// </summary>
    private bool IsTurnAhead(
        List<PathStep> steps,
        int stepIndex,
        Vector3 previousPosition)
    {
        // There cannot be a turn after the final step.
        if (stepIndex >= steps.Count - 1)
        {
            return false;
        }


        Vector3 currentPosition = steps[stepIndex].WorldPosition;
        Vector3 nextPosition = steps[stepIndex + 1].WorldPosition;

        Vector3 incoming = currentPosition - previousPosition;
        Vector3 outgoing = nextPosition - currentPosition;

        if (incoming.sqrMagnitude < 0.0001f ||
            outgoing.sqrMagnitude < 0.0001f)
        {
            return false;
        }


        float angle =
            Vector3.Angle(
                incoming,
                outgoing
            );

        return angle > turnAngleThresholdDegrees;
    }


    /// <summary>
    /// Rotates the ship's tracked heading toward desiredDirection by up to
    /// rotationSpeed degrees this frame, applies that heading to the
    /// visible sprite (if assigned), and returns the resulting forward
    /// vector - the direction translation should actually use this frame.
    ///
    /// Heading is tracked internally rather than read back from
    /// playerSprite, so this works identically with or without one
    /// assigned, and persists smoothly across separate MoveAlongPath
    /// calls instead of resetting - a new route continues turning from
    /// wherever the ship already happened to be facing.
    ///
    /// This is what guarantees the ship can never visually travel in a
    /// direction other than the one it's facing: translation always uses
    /// THIS method's return value, never the raw direction to a target.
    /// </summary>
    private Vector3 UpdateHeading(Vector3 desiredDirection)
    {
        if (desiredDirection.sqrMagnitude > 0.0001f)
        {
            float targetAngle =
                Mathf.Atan2(
                    desiredDirection.y,
                    desiredDirection.x
                )
                * Mathf.Rad2Deg
                - 90.0f;

            Quaternion targetRotation =
                Quaternion.Euler(
                    0.0f,
                    0.0f,
                    targetAngle
                );

            currentHeading =
                Quaternion.RotateTowards(
                    currentHeading,
                    targetRotation,
                    rotationSpeed *
                    Time.deltaTime
                );
        }


        if (playerSprite != null)
        {
            playerSprite.rotation = currentHeading;
        }


        // currentHeading's forward is +Y at zero rotation, matching the
        // -90 degree offset used above (sprite art faces up at Z = 0).
        return currentHeading * Vector3.up;
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