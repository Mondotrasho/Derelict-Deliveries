using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runtime roll cooldowns for cells with no state object of their own
/// (asteroids, empty space). Planet cooldowns live in planet.eventState instead.
/// </summary>
public sealed class EventRollMemory
{
    private readonly struct Key : IEquatable<Key>
    {
        public readonly string Source;
        public readonly Vector3Int Cell;

        public Key(string source, Vector3Int cell)
        {
            Source = source;
            Cell = cell;
        }

        public bool Equals(Key other) => Cell == other.Cell && string.Equals(Source, other.Source);
        public override bool Equals(object obj) => obj is Key other && Equals(other);
        public override int GetHashCode() => (Source != null ? Source.GetHashCode() : 0) * 397 ^ Cell.GetHashCode();
    }

    private readonly Dictionary<Key, int> nextEligibleTurn = new Dictionary<Key, int>();
    private readonly List<Key> pruneBuffer = new List<Key>();

    public int Count => nextEligibleTurn.Count;


    public bool CanRoll(string sourceId, Vector3Int cell, int turn)
    {
        return !nextEligibleTurn.TryGetValue(new Key(sourceId, cell), out int next) || turn >= next;
    }


    /// <summary>A cooldown of 0 allows a re-roll on the very next arrival.</summary>
    public void RecordRoll(string sourceId, Vector3Int cell, int turn, int cooldownTurns)
    {
        nextEligibleTurn[new Key(sourceId, cell)] = turn + Mathf.Max(0, cooldownTurns);
    }


    /// <summary>Drops entries that are already eligible again.</summary>
    public void Prune(int turn)
    {
        pruneBuffer.Clear();
        foreach (KeyValuePair<Key, int> pair in nextEligibleTurn)
        {
            if (turn >= pair.Value) pruneBuffer.Add(pair.Key);
        }
        foreach (Key k in pruneBuffer) nextEligibleTurn.Remove(k);
    }


    public void Clear()
    {
        nextEligibleTurn.Clear();
    }
}
