using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns overworld pursuit pressure: a detection countdown followed by
/// recurring reinforcement waves. Spawn locations are authored as grid cells;
/// this class validates static walkability and live ship occupancy at runtime.
/// </summary>
[DisallowMultipleComponent]
public class EnemySpawnController : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private TurnManager turnManager;

    [SerializeField]
    private GridMap gridMap;

    [SerializeField]
    private PlayerShipState playerShip;

    [SerializeField]
    private EnemyShipRegistry enemyRegistry;

    [SerializeField]
    private EnemyShip enemyPrefab;

    [Tooltip("Optional parent used to keep spawned enemies organised.")]
    [SerializeField]
    private Transform spawnedEnemyParent;

    [Header("Detection")]

    [Min(0)]
    [Tooltip("Completed player turns before the first enemy wave arrives.")]
    [SerializeField]
    private int detectionDelayTurns = 5;

    [Min(0)]
    [Tooltip("Enemies requested when detection first reaches zero.")]
    [SerializeField]
    private int initialWaveSize = 1;

    [Header("Reinforcements")]

    [Min(1)]
    [Tooltip("Completed turns between reinforcement waves after detection.")]
    [SerializeField]
    private int turnsBetweenWaves = 3;

    [Min(0)]
    [Tooltip("Enemies requested by the first reinforcement wave.")]
    [SerializeField]
    private int reinforcementWaveSize = 1;

    [Min(0)]
    [Tooltip("Additional enemies requested by each successive wave.")]
    [SerializeField]
    private int reinforcementGrowthPerWave = 1;

    [Min(0)]
    [Tooltip("Maximum active enemies. Zero means no configured cap.")]
    [SerializeField]
    private int maxActiveEnemies = 0;

    [Header("Entry Cells")]

    [Tooltip("Walkable edge/entry cells used in rotating order.")]
    [SerializeField]
    private List<Vector3Int> spawnCells = new List<Vector3Int>();

    private int nextSpawnCellIndex;

    public event Action<int> DetectionCountdownChanged;
    public event Action<int> ReinforcementCountdownChanged;
    public event Action DetectionStarted;
    public event Action<EnemyShip> EnemySpawned;
    public event Action<int, int> ReinforcementWaveSpawned;

    public int TurnsUntilDetection { get; private set; }
    public int TurnsUntilNextWave { get; private set; }
    public int ReinforcementWaveIndex { get; private set; }
    public bool IsDetectionActive { get; private set; }

    private void Reset()
    {
        ResolveReferences();
    }

    private void Awake()
    {
        ResolveReferences();
        ResetSchedule();
    }

    private void OnEnable()
    {
        ResolveReferences();

        if (turnManager != null)
        {
            turnManager.TurnEnded += HandleTurnEnded;
            turnManager.ClockReset += ResetSchedule;
        }
    }

    private void OnDisable()
    {
        if (turnManager != null)
        {
            turnManager.TurnEnded -= HandleTurnEnded;
            turnManager.ClockReset -= ResetSchedule;
        }
    }

    private void HandleTurnEnded(int turnNumber)
    {
        if (!IsDetectionActive)
        {
            if (TurnsUntilDetection > 0)
            {
                TurnsUntilDetection--;
                DetectionCountdownChanged?.Invoke(TurnsUntilDetection);
            }

            if (TurnsUntilDetection == 0)
            {
                IsDetectionActive = true;
                TurnsUntilNextWave = turnsBetweenWaves;
                DetectionStarted?.Invoke();
                ReinforcementCountdownChanged?.Invoke(TurnsUntilNextWave);

                int spawned = SpawnWave(initialWaveSize);
                ReinforcementWaveSpawned?.Invoke(0, spawned);
            }

            return;
        }

        TurnsUntilNextWave--;
        ReinforcementCountdownChanged?.Invoke(TurnsUntilNextWave);

        if (TurnsUntilNextWave > 0)
        {
            return;
        }

        ReinforcementWaveIndex++;

        int requested = reinforcementWaveSize +
                        ((ReinforcementWaveIndex - 1) *
                         reinforcementGrowthPerWave);

        int reinforcementCount = SpawnWave(requested);

        ReinforcementWaveSpawned?.Invoke(
            ReinforcementWaveIndex,
            reinforcementCount
        );

        TurnsUntilNextWave = turnsBetweenWaves;
        ReinforcementCountdownChanged?.Invoke(TurnsUntilNextWave);
    }

    /// <summary>
    /// Restarts detection timing without deleting existing enemies. Region or
    /// scene teardown remains responsible for removing its own spawned ships.
    /// </summary>
    public void ResetSchedule()
    {
        TurnsUntilDetection = Mathf.Max(0, detectionDelayTurns);
        TurnsUntilNextWave = Mathf.Max(1, turnsBetweenWaves);
        ReinforcementWaveIndex = 0;
        IsDetectionActive = false;
        nextSpawnCellIndex = 0;

        DetectionCountdownChanged?.Invoke(TurnsUntilDetection);
        ReinforcementCountdownChanged?.Invoke(TurnsUntilNextWave);
    }

    /// <summary>
    /// Requests a wave immediately. Useful for authored events and play-mode
    /// testing; normal pressure progression calls the same operation.
    /// </summary>
    public int SpawnWave(int requestedCount)
    {
        if (requestedCount <= 0 || spawnCells.Count == 0)
        {
            return 0;
        }

        if (enemyPrefab == null)
        {
            Debug.LogWarning(
                "EnemySpawnController cannot spawn a wave without an enemy prefab.",
                this
            );

            return 0;
        }

        int allowedCount = ApplyActiveEnemyCap(requestedCount);
        int spawnedCount = 0;
        int cellsChecked = 0;

        while (spawnedCount < allowedCount &&
               cellsChecked < spawnCells.Count)
        {
            int index = nextSpawnCellIndex % spawnCells.Count;
            Vector3Int cell = spawnCells[index];

            nextSpawnCellIndex = (index + 1) % spawnCells.Count;
            cellsChecked++;

            if (TrySpawnEnemyAtCell(cell, out _))
            {
                spawnedCount++;
            }
        }

        return spawnedCount;
    }

    public bool TrySpawnEnemyAtCell(
        Vector3Int cell,
        out EnemyShip spawnedEnemy)
    {
        spawnedEnemy = null;

        if (enemyPrefab == null ||
            gridMap == null ||
            enemyRegistry == null ||
            !gridMap.IsWalkable(cell) ||
            enemyRegistry.IsOccupied(cell) ||
            (playerShip != null && playerShip.CurrentCell == cell) ||
            ApplyActiveEnemyCap(1) == 0)
        {
            return false;
        }

        spawnedEnemy = Instantiate(
            enemyPrefab,
            gridMap.CellToWorld(cell),
            enemyPrefab.transform.rotation,
            spawnedEnemyParent
        );

        spawnedEnemy.name = $"{enemyPrefab.name} ({cell.x}, {cell.y})";

        if (!enemyRegistry.TryGetEnemyAtCell(cell, out EnemyShip occupant) ||
            occupant != spawnedEnemy)
        {
            enemyRegistry.Register(spawnedEnemy);
        }

        if (!enemyRegistry.TryGetEnemyAtCell(cell, out occupant) ||
            occupant != spawnedEnemy)
        {
            Destroy(spawnedEnemy.gameObject);
            spawnedEnemy = null;
            return false;
        }

        EnemySpawned?.Invoke(spawnedEnemy);
        return true;
    }

    private int ApplyActiveEnemyCap(int requestedCount)
    {
        if (maxActiveEnemies <= 0 || enemyRegistry == null)
        {
            return requestedCount;
        }

        return Mathf.Max(
            0,
            Mathf.Min(
                requestedCount,
                maxActiveEnemies - enemyRegistry.Enemies.Count
            )
        );
    }

    private void ResolveReferences()
    {
        if (turnManager == null)
        {
            turnManager = FindFirstObjectByType<TurnManager>();
        }

        if (gridMap == null)
        {
            gridMap = FindFirstObjectByType<GridMap>();
        }

        if (playerShip == null)
        {
            playerShip = FindFirstObjectByType<PlayerShipState>();
        }

        if (enemyRegistry == null)
        {
            enemyRegistry = FindFirstObjectByType<EnemyShipRegistry>();
        }
    }
}
