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
    [Serializable]
    private sealed class SpawnEntry
    {
        public Vector3Int cell = Vector3Int.zero;
        public Vector2Int facingDirection = Vector2Int.up;
    }

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

    [Serializable]
    private sealed class SpawnType
    {
        public EnemyShipDefinition definition;
        [Min(0f)] public float weight = 1f;
    }

    [Tooltip("Enemy types for waves, picked by weight (e.g. the regular ship and the eldritch monster). Empty = always the prefab's own definition.")]
    [SerializeField]
    private List<SpawnType> spawnTypes = new List<SpawnType>();

    [Header("Detection")]

    [Min(0)]
    [Tooltip("Completed player turns before the first enemy wave arrives.")]
    [SerializeField]
    private int detectionDelayTurns = 6;

    [Min(0)]
    [Tooltip("Enemies requested when detection first reaches zero.")]
    [SerializeField]
    private int initialWaveSize = 1;

    [Header("Reinforcements")]

    [Min(1)]
    [Tooltip("Completed turns between reinforcement waves after detection.")]
    [SerializeField]
    private int turnsBetweenWaves = 6;

    [Min(0)]
    [Tooltip("Enemies requested by the first reinforcement wave.")]
    [SerializeField]
    private int reinforcementWaveSize = 1;

    [Min(0)]
    [Tooltip("Additional enemies requested by each successive wave.")]
    [SerializeField]
    private int reinforcementGrowthPerWave = 0;

    [Min(0)]
    [Tooltip("Maximum active enemies. Zero means no configured cap.")]
    [SerializeField]
    private int maxActiveEnemies = 2;

    [Header("Entry Cells")]

    [Tooltip("Walkable edge/entry cells used in rotating order.")]
    [SerializeField]
    private List<SpawnEntry> spawnEntries = new List<SpawnEntry>();

    private int nextSpawnCellIndex;

    private struct PendingSpawn
    {
        public EnemyShipDefinition type;
        public int dueTurn;
    }

    private readonly List<PendingSpawn> pendingSpawns = new List<PendingSpawn>();

    public event Action<int> DetectionCountdownChanged;
    public event Action<int> ReinforcementCountdownChanged;
    public event Action DetectionStarted;
    public event Action<EnemyShip> EnemySpawned;
    public event Action<int, int> ReinforcementWaveSpawned;

    /// <summary>Turns from the start of a run until the hunt begins (the threat bar's full length).</summary>
    public int DetectionDelayTurns => detectionDelayTurns;

    /// <summary>Turns between hunter waves once detected.</summary>
    public int TurnsBetweenWaves => turnsBetweenWaves;

    public int TurnsUntilDetection { get; private set; }
    public int TurnsUntilNextWave { get; private set; }
    public int ReinforcementWaveIndex { get; private set; }
    public bool IsDetectionActive { get; private set; }

    /// <summary>True while the active-enemy cap is full, so waves would spawn nothing.</summary>
    public bool AtEnemyCap => maxActiveEnemies > 0 &&
                              enemyRegistry != null &&
                              enemyRegistry.Enemies.Count >= maxActiveEnemies;

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
            turnManager.EnemyPhaseStarted += HandleEnemyPhaseStarted;
            turnManager.PlayerPhaseStarted += HandlePlayerPhaseStarted;
            turnManager.ClockReset += ResetSchedule;
        }
    }

    private void OnDisable()
    {
        if (turnManager != null)
        {
            turnManager.EnemyPhaseStarted -= HandleEnemyPhaseStarted;
            turnManager.PlayerPhaseStarted -= HandlePlayerPhaseStarted;
            turnManager.ClockReset -= ResetSchedule;
        }
    }

    private void HandleEnemyPhaseStarted(int turnNumber)
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
                BeginDetection();
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
    /// Detection hits zero: start the hunt and send the initial wave straight
    /// away, so the UI's full bar and the first enemy arrive together.
    /// </summary>
    private void BeginDetection()
    {
        if (IsDetectionActive)
        {
            return;
        }

        IsDetectionActive = true;
        TurnsUntilNextWave = Mathf.Max(1, turnsBetweenWaves);
        DetectionStarted?.Invoke();
        ReinforcementCountdownChanged?.Invoke(TurnsUntilNextWave);

        int spawned = SpawnWave(initialWaveSize);
        ReinforcementWaveSpawned?.Invoke(0, spawned);
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
        pendingSpawns.Clear();

        DetectionCountdownChanged?.Invoke(TurnsUntilDetection);
        ReinforcementCountdownChanged?.Invoke(TurnsUntilNextWave);
    }

    /// <summary>
    /// Requests a wave immediately. Useful for authored events and play-mode
    /// testing; normal pressure progression calls the same operation.
    /// </summary>
    public int SpawnWave(int requestedCount)
    {
        if (requestedCount <= 0 || spawnEntries.Count == 0)
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
               cellsChecked < spawnEntries.Count)
        {
            int index = nextSpawnCellIndex % spawnEntries.Count;
            SpawnEntry entry = spawnEntries[index];

            nextSpawnCellIndex = (index + 1) % spawnEntries.Count;
            cellsChecked++;

            if (entry != null &&
                TrySpawnEnemyAtCell(
                    entry.cell,
                    entry.facingDirection,
                    out _))
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
        return TrySpawnEnemyAtCell(cell, Vector2Int.up, out spawnedEnemy);
    }

    /// <summary>
    /// Moves the hunt: before detection, adds (or with a negative value removes)
    /// turns until detection; after detection, the same for the next wave.
    /// Pushing detection to 0 starts the hunt immediately (no dead turn with a
    /// full bar and no enemy).
    /// </summary>
    public void AddDetectionTurns(int delta)
    {
        if (delta == 0)
        {
            return;
        }

        if (!IsDetectionActive)
        {
            TurnsUntilDetection = Mathf.Max(0, TurnsUntilDetection + delta);
            DetectionCountdownChanged?.Invoke(TurnsUntilDetection);

            if (TurnsUntilDetection == 0)
            {
                BeginDetection();
            }
        }
        else
        {
            TurnsUntilNextWave = Mathf.Max(1, TurnsUntilNextWave + delta);
            ReinforcementCountdownChanged?.Invoke(TurnsUntilNextWave);
        }
    }


    /// <summary>
    /// Queues a map-edge spawn for the start of a later player turn (after the
    /// event window has closed, with the map in view) so the player sees it
    /// arrive. Ignores the active-enemy cap, like TrySpawnAtMapEdge.
    /// </summary>
    public void ScheduleMapEdgeSpawn(EnemyShipDefinition type, int turnsFromNow)
    {
        if (type == null) return;
        int now = turnManager != null ? turnManager.CurrentTurn : 0;
        pendingSpawns.Add(new PendingSpawn { type = type, dueTurn = now + Mathf.Max(1, turnsFromNow) });
    }


    private void HandlePlayerPhaseStarted(int turnNumber)
    {
        int now = turnManager != null ? turnManager.CurrentTurn : turnNumber;
        for (int i = pendingSpawns.Count - 1; i >= 0; i--)
        {
            if (pendingSpawns[i].dueTurn > now) continue;
            PendingSpawn due = pendingSpawns[i];
            pendingSpawns.RemoveAt(i);
            if (!TrySpawnAtMapEdge(due.type, out _))
            {
                // every entry cell blocked: try again next turn rather than losing it
                pendingSpawns.Add(new PendingSpawn { type = due.type, dueTurn = now + 1 });
                Debug.LogWarning($"EnemySpawnController: no free edge cell for {due.type.DisplayName}; retrying next turn.", this);
            }
        }
    }


    /// <summary>
    /// Spawns one enemy of a given type at the next free map-edge entry cell
    /// (the same cells waves use), ignoring the active-enemy cap. For quests,
    /// e.g. defiling the red planet shrine brings the eldritch monster.
    /// </summary>
    public bool TrySpawnAtMapEdge(EnemyShipDefinition type, out EnemyShip spawnedEnemy)
    {
        spawnedEnemy = null;
        if (spawnEntries.Count == 0) return false;

        for (int checkedCells = 0; checkedCells < spawnEntries.Count; checkedCells++)
        {
            int index = nextSpawnCellIndex % spawnEntries.Count;
            nextSpawnCellIndex = (index + 1) % spawnEntries.Count;
            SpawnEntry entry = spawnEntries[index];

            if (entry != null &&
                TrySpawnEnemyAtCell(entry.cell, entry.facingDirection, out spawnedEnemy, type, true))
            {
                return true;
            }
        }

        return false;
    }


    private bool TrySpawnEnemyAtCell(
        Vector3Int cell,
        Vector2Int facingDirection,
        out EnemyShip spawnedEnemy,
        EnemyShipDefinition forcedType = null,
        bool ignoreCap = false)
    {
        spawnedEnemy = null;

        if (enemyPrefab == null ||
            gridMap == null ||
            enemyRegistry == null ||
            !gridMap.IsWalkable(cell) ||
            enemyRegistry.IsOccupied(cell) ||
            (playerShip != null && playerShip.CurrentCell == cell) ||
            (!ignoreCap && ApplyActiveEnemyCap(1) == 0))
        {
            return false;
        }

        spawnedEnemy = Instantiate(
            enemyPrefab,
            gridMap.CellToWorld(cell),
            enemyPrefab.transform.rotation,
            spawnedEnemyParent
        );

        EnemyShipDefinition spawnType = forcedType != null ? forcedType : PickSpawnType();
        if (spawnType != null) spawnedEnemy.SetDefinition(spawnType);

        spawnedEnemy.name = $"{(spawnType != null ? spawnType.DisplayName : enemyPrefab.name)} ({cell.x}, {cell.y})";
        spawnedEnemy.SetFacingDirection(facingDirection);

        if (turnManager != null &&
            turnManager.CurrentPhase == TurnPhase.Enemy)
        {
            spawnedEnemy.ScheduleFirstEnemyPhase(turnManager.CurrentTurn + 1);
        }

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

    private EnemyShipDefinition PickSpawnType()
    {
        float total = 0f;
        foreach (SpawnType t in spawnTypes)
        {
            if (t != null && t.definition != null) total += Mathf.Max(0f, t.weight);
        }
        if (total <= 0f) return null;

        float roll = UnityEngine.Random.value * total;
        foreach (SpawnType t in spawnTypes)
        {
            if (t == null || t.definition == null) continue;
            roll -= Mathf.Max(0f, t.weight);
            if (roll <= 0f) return t.definition;
        }
        return null;
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
