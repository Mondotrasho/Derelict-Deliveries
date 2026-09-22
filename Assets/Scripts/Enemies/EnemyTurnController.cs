using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Coordinates one enemy phase after each player turn. Planning is completed
/// for every ship before any ship moves, using one immutable player-position
/// snapshot and a shared destination reservation set. Planned paths are then
/// executed sequentially.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(EnemyShipRegistry))]
public class EnemyTurnController : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private TurnManager turnManager;

    [SerializeField]
    private PlayerShipState playerShip;

    [SerializeField]
    private EnemyShipRegistry enemyRegistry;

    [Tooltip("Ships act in this list order. Empty entries are ignored.")]
    [SerializeField]
    private List<EnemyShip> enemies = new List<EnemyShip>();

    [Tooltip("When enabled, active EnemyShip components in the scene are added before each phase.")]
    [SerializeField]
    private bool discoverEnemiesInScene = true;

    private Coroutine enemyPhaseCoroutine;
    private MovementInterruptionHandle enemyPhaseInterruption;
    private MovementInterruptionHandle encounterInterruption;

    /// <summary>
    /// Raised once when either side enters the other's cell. Combat can
    /// subscribe later without being coupled to movement or pathfinding.
    /// </summary>
    public event Action<EnemyShip, Vector3Int> PlayerContactedEnemy;

    public bool IsEnemyPhaseActive
    {
        get { return enemyPhaseCoroutine != null; }
    }

    /// <summary>The player cell captured for the currently running phase.</summary>
    public Vector3Int PlayerCellSnapshot { get; private set; }

    public bool IsEncounterActive { get; private set; }

    public EnemyShip CurrentEncounterEnemy { get; private set; }

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

        if (turnManager != null)
        {
            turnManager.PlayerPhaseEnding += HandlePlayerPhaseEnding;
            turnManager.ClockReset += HandleClockReset;
        }

        if (playerShip != null)
        {
            playerShip.CellEntered += HandlePlayerEnteredCell;
        }

        if (enemyRegistry != null)
        {
            enemyRegistry.EnemyEnteredCell += HandleEnemyEnteredCell;
        }
    }

    private void OnDisable()
    {
        if (turnManager != null)
        {
            turnManager.PlayerPhaseEnding -= HandlePlayerPhaseEnding;
            turnManager.ClockReset -= HandleClockReset;
        }

        if (playerShip != null)
        {
            playerShip.CellEntered -= HandlePlayerEnteredCell;
        }

        if (enemyRegistry != null)
        {
            enemyRegistry.EnemyEnteredCell -= HandleEnemyEnteredCell;
        }

        if (enemyPhaseCoroutine != null)
        {
            StopCoroutine(enemyPhaseCoroutine);
            enemyPhaseCoroutine = null;
        }

        ReleaseHandle(ref enemyPhaseInterruption);
        ReleaseHandle(ref encounterInterruption);
        IsEncounterActive = false;
        CurrentEncounterEnemy = null;
    }

    private void HandlePlayerPhaseEnding(int turnNumber)
    {
        if (enemyPhaseCoroutine == null &&
            playerShip != null &&
            !IsEncounterActive)
        {
            enemyPhaseCoroutine = StartCoroutine(RunEnemyPhase());
        }
    }

    private IEnumerator RunEnemyPhase()
    {
        // Other TurnEnded subscribers may start a queued player segment later
        // in the same event invocation. Waiting one frame makes subscriber
        // order irrelevant before checking the facade's movement state.
        yield return null;

        while (playerShip != null &&
               (playerShip.IsMoving || playerShip.HasPausedMovement))
        {
            yield return null;
        }

        if (playerShip == null)
        {
            enemyPhaseCoroutine = null;
            yield break;
        }

        if (turnManager == null || !turnManager.BeginEnemyPhase())
        {
            enemyPhaseCoroutine = null;
            yield break;
        }

        // Spawn and other enemy-phase listeners run synchronously from the
        // phase event. One frame lets their registration settle before the
        // shared planning snapshot is built.
        yield return null;

        enemyPhaseInterruption =
            playerShip.AcquireMovementInterruption("Enemy phase");

        RefreshEnemyList();
        PlayerCellSnapshot = playerShip.CurrentCell;

        if (TryBeginAdjacentEncounter(PlayerCellSnapshot))
        {
            ReleaseHandle(ref enemyPhaseInterruption);
            enemyPhaseCoroutine = null;
            yield break;
        }

        List<PlannedEnemyMove> plans = PlanEnemyMoves(PlayerCellSnapshot);

        foreach (PlannedEnemyMove plan in plans)
        {
            if (IsEncounterActive ||
                turnManager.CurrentPhase != TurnPhase.Enemy)
            {
                break;
            }

            if (plan.Enemy == null ||
                !plan.Enemy.isActiveAndEnabled ||
                plan.Path.Count == 0)
            {
                continue;
            }

            if (!plan.Enemy.TryMoveAlongPath(plan.Path))
            {
                continue;
            }

            while (plan.Enemy != null && plan.Enemy.IsMoving)
            {
                yield return null;
            }
        }

        ReleaseHandle(ref enemyPhaseInterruption);
        enemyPhaseCoroutine = null;

        if (!IsEncounterActive &&
            turnManager.CurrentPhase == TurnPhase.Enemy)
        {
            turnManager.CompleteEnemyPhase();
        }
    }

    private List<PlannedEnemyMove> PlanEnemyMoves(
        Vector3Int playerCellSnapshot)
    {
        List<PlannedEnemyMove> plans = new List<PlannedEnemyMove>();
        HashSet<Vector3Int> blockedCells =
            new HashSet<Vector3Int>();
        HashSet<EnemyShip> plannedEnemies = new HashSet<EnemyShip>();

        foreach (EnemyShip enemy in enemies)
        {
            if (enemy != null && enemy.isActiveAndEnabled)
            {
                blockedCells.Add(enemy.CurrentCell);
            }
        }

        foreach (EnemyShip enemy in enemies)
        {
            if (enemy == null ||
                !enemy.isActiveAndEnabled ||
                !enemy.CanActInEnemyPhase(turnManager.CurrentTurn) ||
                !plannedEnemies.Add(enemy))
            {
                continue;
            }

            blockedCells.Remove(enemy.CurrentCell);

            List<Vector3Int> path = enemy.PlanMovement(
                playerCellSnapshot,
                blockedCells
            );

            Vector3Int destination = path.Count > 0
                ? path[path.Count - 1]
                : enemy.CurrentCell;

            blockedCells.Add(destination);
            plans.Add(new PlannedEnemyMove(enemy, path));
        }

        return plans;
    }

    private void RefreshEnemyList()
    {
        enemies.RemoveAll(enemy => enemy == null);

        if (!discoverEnemiesInScene)
        {
            return;
        }

        EnemyShip[] discovered =
            FindObjectsByType<EnemyShip>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.InstanceID
            );

        foreach (EnemyShip enemy in discovered)
        {
            if (!enemies.Contains(enemy))
            {
                enemies.Add(enemy);
            }
        }
    }

    private void ResolveReferences()
    {
        if (turnManager == null)
        {
            turnManager = FindFirstObjectByType<TurnManager>();
        }

        if (playerShip == null)
        {
            playerShip = FindFirstObjectByType<PlayerShipState>();
        }

        if (enemyRegistry == null)
        {
            enemyRegistry = GetComponent<EnemyShipRegistry>();
        }
    }

    private void HandlePlayerEnteredCell(Vector3Int cell)
    {
        if (enemyRegistry == null)
        {
            return;
        }

        foreach (EnemyShip enemy in enemyRegistry.Enemies)
        {
            if (enemy != null &&
                AreAdjacentOrEqual(cell, enemy.CurrentCell) &&
                IsEnemyEligibleForEncounter(enemy))
            {
                BeginEncounter(enemy, enemy.CurrentCell);
                return;
            }
        }
    }

    private void HandleEnemyEnteredCell(
        EnemyShip enemy,
        Vector3Int cell)
    {
        if (playerShip != null &&
            AreAdjacentOrEqual(cell, playerShip.CurrentCell))
        {
            BeginEncounter(enemy, cell);
        }
    }

    private void BeginEncounter(EnemyShip enemy, Vector3Int cell)
    {
        if (IsEncounterActive || enemy == null || playerShip == null ||
            !IsEnemyEligibleForEncounter(enemy) ||
            turnManager == null || !turnManager.BeginCombat())
        {
            return;
        }

        IsEncounterActive = true;
        CurrentEncounterEnemy = enemy;
        enemy.StopAfterCurrentCell();
        encounterInterruption =
            playerShip.AcquireMovementInterruption("Enemy contact");

        PlayerContactedEnemy?.Invoke(enemy, cell);
    }

    private bool TryBeginAdjacentEncounter(Vector3Int playerCell)
    {
        if (enemyRegistry == null)
        {
            return false;
        }

        foreach (EnemyShip enemy in enemyRegistry.Enemies)
        {
            if (enemy != null &&
                AreAdjacentOrEqual(playerCell, enemy.CurrentCell) &&
                IsEnemyEligibleForEncounter(enemy))
            {
                BeginEncounter(enemy, enemy.CurrentCell);
                return IsEncounterActive;
            }
        }

        return false;
    }

    private static bool AreAdjacentOrEqual(
        Vector3Int first,
        Vector3Int second)
    {
        Vector3Int delta = first - second;

        return Mathf.Abs(delta.x) <= 1 &&
               Mathf.Abs(delta.y) <= 1 &&
               delta.z == 0;
    }

    /// <summary>
    /// Releases the temporary overworld hold after a future encounter system
    /// has removed or repositioned one of the ships. It does not resolve any
    /// combat state itself.
    /// </summary>
    public void ReleaseEncounter(
        bool playerDefeated = false,
        bool endPlayerPhase = false)
    {
        ReleaseHandle(ref encounterInterruption);
        IsEncounterActive = false;
        CurrentEncounterEnemy = null;
        turnManager?.CompleteCombat(playerDefeated, endPlayerPhase);
    }

    /// <summary>
    /// Keeps one enemy stationary and unable to initiate contact through the
    /// player's next complete phase after a successful flee.
    /// </summary>
    public void GrantFleeGrace(EnemyShip enemy)
    {
        if (enemy == null || turnManager == null)
        {
            return;
        }

        enemy.GrantFleeGrace(turnManager.CurrentTurn + 1);
    }

    public bool IsEncounterSuppressed(EnemyShip enemy)
    {
        if (enemy == null || turnManager == null)
        {
            return false;
        }

        return !IsEnemyEligibleForEncounter(enemy);
    }

    private bool IsEnemyEligibleForEncounter(EnemyShip enemy)
    {
        if (enemy == null || turnManager == null)
        {
            return false;
        }

        if (turnManager.CurrentPhase == TurnPhase.Enemy)
        {
            return enemy.CanActInEnemyPhase(turnManager.CurrentTurn);
        }

        return enemy.CanStartPlayerEncounter(turnManager.CurrentTurn);
    }

    private void HandleClockReset()
    {
        if (enemyPhaseCoroutine != null)
        {
            StopCoroutine(enemyPhaseCoroutine);
            enemyPhaseCoroutine = null;
        }

        ReleaseHandle(ref enemyPhaseInterruption);
    }

    private static void ReleaseHandle(
        ref MovementInterruptionHandle handle)
    {
        handle?.Release();
        handle = null;
    }

    private sealed class PlannedEnemyMove
    {
        public EnemyShip Enemy { get; }
        public List<Vector3Int> Path { get; }

        public PlannedEnemyMove(
            EnemyShip enemy,
            List<Vector3Int> path)
        {
            Enemy = enemy;
            Path = path ?? new List<Vector3Int>();
        }
    }
}
