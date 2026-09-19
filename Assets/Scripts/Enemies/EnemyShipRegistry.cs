using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Authoritative lookup for active enemy occupancy on the overworld grid.
/// Spawning, combat and navigation can share this boundary without making
/// GridMap responsible for dynamic objects.
/// </summary>
[DisallowMultipleComponent]
public class EnemyShipRegistry : MonoBehaviour
{
    private static readonly Vector3Int[] AdjacentOffsets =
    {
        Vector3Int.up,
        Vector3Int.right,
        Vector3Int.down,
        Vector3Int.left,
        new Vector3Int(1, 1, 0),
        new Vector3Int(1, -1, 0),
        new Vector3Int(-1, -1, 0),
        new Vector3Int(-1, 1, 0)
    };

    private readonly Dictionary<Vector3Int, EnemyShip> enemiesByCell =
        new Dictionary<Vector3Int, EnemyShip>();

    private readonly HashSet<EnemyShip> enemies =
        new HashSet<EnemyShip>();

    public event Action<EnemyShip> EnemyRegistered;
    public event Action<EnemyShip> EnemyUnregistered;
    public event Action<EnemyShip, Vector3Int> EnemyEnteredCell;

    public IReadOnlyCollection<EnemyShip> Enemies
    {
        get { return enemies; }
    }

    public bool Register(EnemyShip enemy)
    {
        if (enemy == null || enemies.Contains(enemy))
        {
            return false;
        }

        if (enemiesByCell.TryGetValue(
                enemy.CurrentCell,
                out EnemyShip occupant) &&
            occupant != enemy)
        {
            Debug.LogWarning(
                $"Cannot register {enemy.name}: cell {enemy.CurrentCell} " +
                $"is already occupied by {occupant.name}.",
                enemy
            );

            return false;
        }

        enemies.Add(enemy);
        enemiesByCell[enemy.CurrentCell] = enemy;
        EnemyRegistered?.Invoke(enemy);
        return true;
    }

    public bool Unregister(EnemyShip enemy)
    {
        if (enemy == null || !enemies.Remove(enemy))
        {
            return false;
        }

        if (enemiesByCell.TryGetValue(enemy.CurrentCell, out EnemyShip occupant) &&
            occupant == enemy)
        {
            enemiesByCell.Remove(enemy.CurrentCell);
        }

        EnemyUnregistered?.Invoke(enemy);
        return true;
    }

    public bool TryGetEnemyAtCell(
        Vector3Int cell,
        out EnemyShip enemy)
    {
        return enemiesByCell.TryGetValue(cell, out enemy) && enemy != null;
    }

    public bool IsOccupied(Vector3Int cell)
    {
        return TryGetEnemyAtCell(cell, out _);
    }

    /// <summary>
    /// Finds an enemy sharing or neighbouring the supplied cell. Orthogonal
    /// and diagonal neighbours both count because the overworld permits both
    /// kinds of grid step.
    /// </summary>
    public bool TryGetEnemyAtOrAdjacentToCell(
        Vector3Int cell,
        out EnemyShip enemy)
    {
        if (TryGetEnemyAtCell(cell, out enemy))
        {
            return true;
        }

        foreach (Vector3Int offset in AdjacentOffsets)
        {
            if (TryGetEnemyAtCell(cell + offset, out enemy))
            {
                return true;
            }
        }

        enemy = null;
        return false;
    }

    public bool CanEnemyEnterCell(EnemyShip enemy, Vector3Int cell)
    {
        return !enemiesByCell.TryGetValue(cell, out EnemyShip occupant) ||
               occupant == null ||
               occupant == enemy;
    }

    internal bool ReportEnemyEnteredCell(
        EnemyShip enemy,
        Vector3Int previousCell,
        Vector3Int newCell)
    {
        if (enemy == null || !enemies.Contains(enemy))
        {
            return false;
        }

        if (enemiesByCell.TryGetValue(newCell, out EnemyShip occupant) &&
            occupant != enemy)
        {
            Debug.LogError(
                $"Enemy occupancy conflict at {newCell}: " +
                $"{enemy.name} and {occupant.name}.",
                enemy
            );

            return false;
        }

        if (enemiesByCell.TryGetValue(previousCell, out occupant) &&
            occupant == enemy)
        {
            enemiesByCell.Remove(previousCell);
        }

        enemiesByCell[newCell] = enemy;
        EnemyEnteredCell?.Invoke(enemy, newCell);
        return true;
    }
}
