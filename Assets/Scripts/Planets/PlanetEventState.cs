using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Flexible quest/event data stored by a planet.
///
/// The exact event and quest design is intentionally left open. These simple
/// string-keyed collections give later systems somewhere stable to store tags,
/// booleans, counters and delivery progress without PlanetManager itself becoming
/// responsible for quest logic.
/// </summary>
[Serializable]
public class PlanetEventState
{
    [Tooltip("General-purpose planet/event tags. Examples later might be Trade, Pirate, Story, Fuel, etc.")]
    public List<string> tags = new List<string>();

    [Tooltip("General-purpose named true/false quest state.")]
    public List<PlanetStateFlag> flags = new List<PlanetStateFlag>();

    [Tooltip("General-purpose named integer state.")]
    public List<PlanetStateCounter> counters = new List<PlanetStateCounter>();

    [Tooltip("Simple delivery progress records. The final delivery system can replace or extend these later.")]
    public List<PlanetDeliveryState> deliveries = new List<PlanetDeliveryState>();

    public bool HasTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        return tags.Contains(tag);
    }

    public bool GetFlag(string key, bool fallback = false)
    {
        PlanetStateFlag entry =
            flags.Find(item => item != null && item.key == key);

        return entry != null
            ? entry.value
            : fallback;
    }

    public void SetFlag(string key, bool value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        PlanetStateFlag entry =
            flags.Find(item => item != null && item.key == key);

        if (entry == null)
        {
            entry = new PlanetStateFlag { key = key };
            flags.Add(entry);
        }

        entry.value = value;
    }

    public int GetCounter(string key, int fallback = 0)
    {
        PlanetStateCounter entry =
            counters.Find(item => item != null && item.key == key);

        return entry != null
            ? entry.value
            : fallback;
    }

    public void SetCounter(string key, int value)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        PlanetStateCounter entry =
            counters.Find(item => item != null && item.key == key);

        if (entry == null)
        {
            entry = new PlanetStateCounter { key = key };
            counters.Add(entry);
        }

        entry.value = value;
    }
}


[Serializable]
public class PlanetStateFlag
{
    public string key;
    public bool value;
}


[Serializable]
public class PlanetStateCounter
{
    public string key;
    public int value;
}


[Serializable]
public class PlanetDeliveryState
{
    [Tooltip("Unique name/id for this delivery objective.")]
    public string deliveryId;

    [Tooltip("Optional item/cargo identifier.")]
    public string itemTag;

    [Min(0)]
    public int requiredAmount = 1;

    [Min(0)]
    public int deliveredAmount = 0;

    public bool completed = false;

    public void RefreshCompleted()
    {
        completed =
            requiredAmount > 0 &&
            deliveredAmount >= requiredAmount;
    }
}
