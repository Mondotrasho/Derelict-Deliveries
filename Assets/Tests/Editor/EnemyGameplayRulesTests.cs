using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Tilemaps;

public sealed class EnemyGameplayRulesTests
{
    private GameObject testObject;
    private Object transientAsset;

    [TearDown]
    public void TearDown()
    {
        if (testObject != null)
        {
            Object.DestroyImmediate(testObject);
        }

        if (transientAsset != null)
        {
            Object.DestroyImmediate(transientAsset);
        }
    }

    [Test]
    public void EndTurn_WaitsForEnemyPhaseBeforeAdvancingCounter()
    {
        TurnManager manager = CreateTurnManager();

        manager.EndTurn();

        Assert.That(manager.CurrentPhase,
            Is.EqualTo(TurnPhase.WaitingForPlayerMovement));
        Assert.That(manager.CurrentTurn, Is.EqualTo(1));

        Assert.That(manager.BeginEnemyPhase(), Is.True);
        manager.CompleteEnemyPhase();

        Assert.That(manager.CurrentPhase, Is.EqualTo(TurnPhase.Player));
        Assert.That(manager.CurrentTurn, Is.EqualTo(2));
    }

    [Test]
    public void PlayerStartedVictory_ReturnsToSamePlayerPhase()
    {
        TurnManager manager = CreateTurnManager();

        Assert.That(manager.BeginCombat(), Is.True);
        manager.CompleteCombat();

        Assert.That(manager.CurrentPhase, Is.EqualTo(TurnPhase.Player));
        Assert.That(manager.CurrentTurn, Is.EqualTo(1));
    }

    [Test]
    public void PlayerStartedFlee_ForfeitsRemainingPlayerPhase()
    {
        TurnManager manager = CreateTurnManager();

        Assert.That(manager.BeginCombat(), Is.True);
        manager.CompleteCombat(endPlayerPhase: true);

        Assert.That(manager.CurrentPhase,
            Is.EqualTo(TurnPhase.WaitingForPlayerMovement));
        Assert.That(manager.CurrentTurn, Is.EqualTo(1));
        Assert.That(manager.BeginEnemyPhase(), Is.True);
    }

    [Test]
    public void EnemyStartedCombat_EndsEnemyPhaseAfterResolution()
    {
        TurnManager manager = CreateTurnManager();
        manager.EndTurn();
        manager.BeginEnemyPhase();

        Assert.That(manager.BeginCombat(), Is.True);
        manager.CompleteCombat();

        Assert.That(manager.CurrentPhase, Is.EqualTo(TurnPhase.Player));
        Assert.That(manager.CurrentTurn, Is.EqualTo(2));
    }

    [Test]
    public void FleeGrace_BlocksOneFullPlayerPhaseThenAllowsEnemyAction()
    {
        EnemyShip enemy = CreateEnemyShip();

        enemy.GrantFleeGrace(resumeOnEnemyTurn: 2);

        Assert.That(enemy.CanActInEnemyPhase(1), Is.False);
        Assert.That(enemy.CanStartPlayerEncounter(2), Is.False);
        Assert.That(enemy.CanActInEnemyPhase(2), Is.True);
        Assert.That(enemy.CanStartPlayerEncounter(3), Is.True);
    }

    [Test]
    public void NewlySpawnedEnemy_WaitsForItsScheduledEnemyPhase()
    {
        EnemyShip enemy = CreateEnemyShip();

        enemy.ScheduleFirstEnemyPhase(4);

        Assert.That(enemy.CanActInEnemyPhase(3), Is.False);
        Assert.That(enemy.CanActInEnemyPhase(4), Is.True);
        Assert.That(enemy.CanStartPlayerEncounter(4), Is.True,
            "Spawn delay must not prevent the player choosing to engage it.");
    }

    [Test]
    public void FirstAreaRaiderDefinition_IsAppliedByEnemyPrefab()
    {
        const string prefabPath =
            "Assets/Prefabs/Enemies/EnemyShip.prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            prefabPath
        );

        Assert.That(prefab, Is.Not.Null);

        testObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        EnemyShip ship = testObject.GetComponent<EnemyShip>();
        EnemyCombatState combat = testObject.GetComponent<EnemyCombatState>();
        ship.ApplyDefinition();

        Assert.That(ship.Definition, Is.Not.Null);
        Assert.That(ship.Definition.name, Is.EqualTo("FirstAreaRaider"));
        Assert.That(ship.MovementBudget, Is.EqualTo(3));
        Assert.That(combat.DisplayName, Is.EqualTo("Raider Scout"));
        Assert.That(combat.MaxHull, Is.EqualTo(45.0f));
        Assert.That(combat.Capabilities.WeaponDamage, Is.EqualTo(15.0f));
        Assert.That(combat.RewardValue, Is.EqualTo(10));
    }

    [Test]
    public void RaiderActionWeights_ChooseExpectedHiddenActions()
    {
        EnemyShip enemy = CreateEnemyShip();
        EnemyCombatState combat = enemy.gameObject.AddComponent<EnemyCombatState>();

        Assert.That(combat.ChooseHiddenAction(0.0f),
            Is.EqualTo(CombatAction.Fire));
        Assert.That(combat.ChooseHiddenAction(0.70f),
            Is.EqualTo(CombatAction.Defend));
        Assert.That(combat.ChooseHiddenAction(0.95f),
            Is.EqualTo(CombatAction.Evade));
    }

    [Test]
    public void EnemyPlanning_ReservesUniqueDestinationsFromSharedSnapshot()
    {
        testObject = new GameObject("Enemy Planning Test Grid");
        Grid grid = testObject.AddComponent<Grid>();

        GameObject tilemapObject = new GameObject("Ground");
        tilemapObject.transform.SetParent(testObject.transform);
        Tilemap ground = tilemapObject.AddComponent<Tilemap>();
        tilemapObject.AddComponent<TilemapRenderer>();

        Tile tile = ScriptableObject.CreateInstance<Tile>();
        transientAsset = tile;

        for (int x = -4; x <= 4; x++)
        {
            for (int y = -2; y <= 5; y++)
            {
                ground.SetTile(new Vector3Int(x, y, 0), tile);
            }
        }

        GridMap map = testObject.AddComponent<GridMap>();
        SetPrivateField(map, "grid", grid);
        SetPrivateField(map, "groundTilemap", ground);

        GridPathfinder pathfinder = testObject.AddComponent<GridPathfinder>();
        SetPrivateField(pathfinder, "gridMap", map);
        EnemyShipRegistry registry =
            testObject.AddComponent<EnemyShipRegistry>();
        TurnManager turnManager = testObject.AddComponent<TurnManager>();
        EnemyTurnController controller =
            testObject.AddComponent<EnemyTurnController>();
        SetPrivateField(controller, "turnManager", turnManager);

        EnemyShip left = CreateGridEnemy(
            "Left Enemy",
            new Vector3Int(-2, 0, 0),
            map,
            pathfinder,
            registry
        );
        EnemyShip right = CreateGridEnemy(
            "Right Enemy",
            new Vector3Int(2, 0, 0),
            map,
            pathfinder,
            registry
        );
        SetPrivateField(
            controller,
            "enemies",
            new List<EnemyShip> { left, right }
        );

        Vector3Int sharedPlayerSnapshot = new Vector3Int(0, 3, 0);
        MethodInfo planMethod = typeof(EnemyTurnController).GetMethod(
            "PlanEnemyMoves",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        IEnumerable plans = (IEnumerable)planMethod.Invoke(
            controller,
            new object[] { sharedPlayerSnapshot }
        );
        HashSet<Vector3Int> destinations = new HashSet<Vector3Int>();
        int planCount = 0;

        foreach (object plan in plans)
        {
            planCount++;
            PropertyInfo pathProperty = plan.GetType().GetProperty("Path");
            List<Vector3Int> path =
                (List<Vector3Int>)pathProperty.GetValue(plan);
            Assert.That(path, Is.Not.Empty);
            Assert.That(path[path.Count - 1], Is.Not.EqualTo(sharedPlayerSnapshot));
            Assert.That(destinations.Add(path[path.Count - 1]), Is.True,
                "Two enemies reserved the same destination cell.");
        }

        Assert.That(planCount, Is.EqualTo(2));
    }

    private TurnManager CreateTurnManager()
    {
        testObject = new GameObject("TurnManager Test");
        return testObject.AddComponent<TurnManager>();
    }

    private EnemyShip CreateEnemyShip()
    {
        testObject = new GameObject("EnemyShip Test");
        return testObject.AddComponent<EnemyShip>();
    }

    private EnemyShip CreateGridEnemy(
        string enemyName,
        Vector3Int cell,
        GridMap map,
        GridPathfinder pathfinder,
        EnemyShipRegistry registry)
    {
        GameObject enemyObject = new GameObject(enemyName);
        enemyObject.SetActive(false);
        enemyObject.transform.SetParent(testObject.transform);
        enemyObject.transform.position = map.CellToWorld(cell);

        EnemyShip enemy = enemyObject.AddComponent<EnemyShip>();
        SetPrivateField(enemy, "gridMap", map);
        SetPrivateField(enemy, "pathfinder", pathfinder);
        SetPrivateField(enemy, "registry", registry);
        enemyObject.SetActive(true);
        return enemy;
    }

    private static void SetPrivateField<T>(
        object target,
        string fieldName,
        T value)
    {
        FieldInfo field = target.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        Assert.That(field, Is.Not.Null, $"Missing field {fieldName}.");
        field.SetValue(target, value);
    }
}
