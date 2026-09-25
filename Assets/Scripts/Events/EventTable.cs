using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Weighted pool of event definitions a source draws from.
/// </summary>
[CreateAssetMenu(
    fileName = "EventTable",
    menuName = "Derelict Deliveries/Events/Event Table")]
public sealed class EventTable : ScriptableObject
{
    [Serializable]
    public sealed class Entry
    {
        public EventDefinition definition;
        [Min(0f)] public float weight = 1f;
    }

    [SerializeField] private List<Entry> entries = new List<Entry>();

    public IReadOnlyList<Entry> Entries => entries;

    private readonly List<Entry> eligible = new List<Entry>();


    /// <summary>
    /// Picks one eligible definition by weight, or null if none qualifies.
    /// Eligible = has at least one of the source's tags, conditions pass for the
    /// context, and it is not a once-per-planet event already done on that planet.
    /// </summary>
    public EventDefinition PickWeighted(
        IReadOnlyList<string> sourceTags,
        EventContext context,
        System.Random rng)
    {
        eligible.Clear();
        float total = 0f;

        foreach (Entry e in entries)
        {
            if (e == null || e.definition == null || e.weight <= 0f) continue;
            if (!e.definition.IsEligible(sourceTags, context)) continue;
            eligible.Add(e);
            total += e.weight;
        }

        if (eligible.Count == 0 || total <= 0f) return null;

        double roll = rng.NextDouble() * total;
        float acc = 0f;
        foreach (Entry e in eligible)
        {
            acc += e.weight;
            if (roll <= acc) return e.definition;
        }

        return eligible[eligible.Count - 1].definition;
    }
}
