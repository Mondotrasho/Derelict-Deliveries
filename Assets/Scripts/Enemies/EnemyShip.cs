using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One enemy ship's grid position, movement rules, and physical movement.
/// EnemyTurnController owns turn timing and planning order; this component
/// only answers "where would I move?" and executes the resulting path.
/// </summary>
[DisallowMultipleComponent]
public class EnemyShip : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private GridMap gridMap;

    [SerializeField]
    private GridPathfinder pathfinder;

    [SerializeField]
    private EnemyShipRegistry registry;

    [Header("Fog Signature")]

    [Tooltip("Optional. When assigned, this enemy owns a small moving named fog reveal.")]
    [SerializeField]
    private FogOfWar fogOfWar;

    [Tooltip("Partial is recommended so enemies are legible without clearing a large area.")]
    [SerializeField]
    private FogOfWar.VisibilityTier fogRevealTier =
        FogOfWar.VisibilityTier.Partial;

    [Tooltip("Namespace used for this enemy's unique runtime fog lock.")]
    [SerializeField]
    private string fogRevealIdPrefix = "enemy-visibility:";

    [Header("Movement")]

    [Min(0)]
    [Tooltip("Maximum base pathfinding cost this ship can spend each enemy phase.")]
    [SerializeField]
    private int movementBudget = 4;

    [Min(0.01f)]
    [Tooltip("Physical travel speed in world units per second.")]
    [SerializeField]
    private float movementSpeed = 4.0f;

    [Header("Presentation")]

    [Tooltip("Transform rotated to face travel direction. Defaults to the first child SpriteRenderer, then this Transform.")]
    [SerializeField]
    private Transform shipVisual;

    [Min(0.0f)]
    [Tooltip("Heading rotation speed in degrees per second. Zero snaps instantly.")]
    [SerializeField]
    private float rotationSpeed = 360.0f;

    [Tooltip("Added to the world-space movement angle. Use -90 when the source sprite faces up.")]
    [SerializeField]
    private float spriteForwardAngleOffset = -90.0f;

    private Coroutine movementCoroutine;
    private Vector3Int currentCell;
    private bool stopAfterCurrentCell;
    private string fogRevealId;

    /// <summary>Raised after this ship finishes its complete assigned path.</summary>
    public event Action<EnemyShip> MovementCompleted;

    /// <summary>Raised after this ship physically enters a new grid cell.</summary>
    public event Action<EnemyShip, Vector3Int> CellEntered;

    public Vector3Int CurrentCell
    {
        get { return currentCell; }
    }

    public int MovementBudget
    {
        get { return movementBudget; }
    }

    public bool IsMoving
    {
        get { return movementCoroutine != null; }
    }

    private void Reset()
    {
        ResolveReferences();
    }

    private void Awake()
    {
        ResolveReferences();

        if (gridMap != null)
        {
            currentCell = gridMap.GetCellForTransform(transform);
        }
    }

    private void OnEnable()
    {
        ResolveReferences();

        if (gridMap != null)
        {
            currentCell = gridMap.GetCellForTransform(transform);
        }

        registry?.Register(this);
        UpdateFogReveal();
    }

    private void OnDisable()
    {
        RemoveFogReveal();
        registry?.Unregister(this);

        if (movementCoroutine != null)
        {
            StopCoroutine(movementCoroutine);
            movementCoroutine = null;
        }
    }

    /// <summary>
    /// Finds the shortest route toward the supplied player snapshot, removes
    /// the player's own cell so the ship stops adjacent, then returns the
    /// longest prefix affordable within this ship's movement budget. Dynamic
    /// blocked cells apply to the complete route, not only its destination.
    /// </summary>
    public List<Vector3Int> PlanMovement(
        Vector3Int playerCellSnapshot,
        ISet<Vector3Int> blockedCells)
    {
        List<Vector3Int> plannedMovement = new List<Vector3Int>();

        if (gridMap == null || pathfinder == null || movementBudget <= 0)
        {
            return plannedMovement;
        }

        List<Vector3Int> routeToPlayer = pathfinder.FindPath(
            currentCell,
            playerCellSnapshot,
            blockedCells
        );

        if (routeToPlayer.Count == 0)
        {
            return plannedMovement;
        }

        if (routeToPlayer[routeToPlayer.Count - 1] == playerCellSnapshot)
        {
            routeToPlayer.RemoveAt(routeToPlayer.Count - 1);
        }

        int spent = 0;
        Vector3Int previousCell = currentCell;

        foreach (Vector3Int cell in routeToPlayer)
        {
            int stepCost = pathfinder.GetBaseStepCost(previousCell, cell);

            if (spent + stepCost > movementBudget)
            {
                break;
            }

            spent += stepCost;
            plannedMovement.Add(cell);
            previousCell = cell;
        }

        return plannedMovement;
    }

    /// <summary>
    /// Starts physical movement along an already planned path. Returns false
    /// when the command cannot be accepted.
    /// </summary>
    public bool TryMoveAlongPath(IReadOnlyList<Vector3Int> path)
    {
        if (IsMoving || gridMap == null || path == null || path.Count == 0)
        {
            return false;
        }

        List<Vector3Int> pathCopy = new List<Vector3Int>(path.Count);

        for (int i = 0; i < path.Count; i++)
        {
            pathCopy.Add(path[i]);
        }

        stopAfterCurrentCell = false;
        movementCoroutine = StartCoroutine(FollowPath(pathCopy));
        return true;
    }

    public void StopAfterCurrentCell()
    {
        stopAfterCurrentCell = true;
    }

    private IEnumerator FollowPath(List<Vector3Int> path)
    {
        foreach (Vector3Int cell in path)
        {
            if (registry != null &&
                !registry.CanEnemyEnterCell(this, cell))
            {
                break;
            }

            Vector3 targetPosition = gridMap.CellToWorld(cell);

            while (transform.position != targetPosition)
            {
                UpdateHeading(targetPosition - transform.position);

                transform.position = Vector3.MoveTowards(
                    transform.position,
                    targetPosition,
                    movementSpeed * Time.deltaTime
                );

                yield return null;
            }

            Vector3Int previousCell = currentCell;
            currentCell = cell;
            registry?.ReportEnemyEnteredCell(this, previousCell, currentCell);
            UpdateFogReveal();
            CellEntered?.Invoke(this, currentCell);

            if (stopAfterCurrentCell)
            {
                break;
            }
        }

        stopAfterCurrentCell = false;
        movementCoroutine = null;
        MovementCompleted?.Invoke(this);
    }

    private void ResolveReferences()
    {
        if (gridMap == null)
        {
            gridMap = FindFirstObjectByType<GridMap>();
        }

        if (pathfinder == null)
        {
            pathfinder = FindFirstObjectByType<GridPathfinder>();
        }

        if (registry == null)
        {
            registry = FindFirstObjectByType<EnemyShipRegistry>();
        }

        if (fogOfWar == null)
        {
            fogOfWar = FindFirstObjectByType<FogOfWar>();
        }

        if (shipVisual == null)
        {
            SpriteRenderer spriteRenderer =
                GetComponentInChildren<SpriteRenderer>(true);

            shipVisual = spriteRenderer != null
                ? spriteRenderer.transform
                : transform;
        }
    }

    private void UpdateFogReveal()
    {
        if (fogOfWar == null)
        {
            return;
        }

        EnsureFogRevealId();

        if (fogRevealTier == FogOfWar.VisibilityTier.Hidden)
        {
            fogOfWar.RemoveLockedLocation(fogRevealId);
            return;
        }

        fogOfWar.SetLockedLocation(
            fogRevealId,
            currentCell,
            fogRevealTier
        );
    }

    private void RemoveFogReveal()
    {
        if (fogOfWar == null || string.IsNullOrEmpty(fogRevealId))
        {
            return;
        }

        fogOfWar.RemoveLockedLocation(fogRevealId);
    }

    private void EnsureFogRevealId()
    {
        if (!string.IsNullOrEmpty(fogRevealId))
        {
            return;
        }

        string prefix = string.IsNullOrWhiteSpace(fogRevealIdPrefix)
            ? "enemy-visibility:"
            : fogRevealIdPrefix.Trim();

        fogRevealId = prefix + GetInstanceID();
    }

    private void UpdateHeading(Vector3 direction)
    {
        if (shipVisual == null || direction.sqrMagnitude <= 0.000001f)
        {
            return;
        }

        float angle = Mathf.Atan2(direction.y, direction.x) *
                      Mathf.Rad2Deg +
                      spriteForwardAngleOffset;

        Quaternion targetRotation = Quaternion.Euler(0.0f, 0.0f, angle);

        shipVisual.rotation = rotationSpeed <= 0.0f
            ? targetRotation
            : Quaternion.RotateTowards(
                shipVisual.rotation,
                targetRotation,
                rotationSpeed * Time.deltaTime
            );
    }
}
