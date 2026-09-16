using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Flexible quest/event data stored by a planet.
///
/// The existing serialized field layout is deliberately preserved so current
/// scene data remains compatible. The class now also implements IEventState,
/// giving event selectors the same tag/flag/counter contract used by the
/// player's EventStateData without PlanetManager becoming event logic.
/// </summary>
[Serializable]
public class PlanetEventState : IEventState
{
    [Tooltip("General-purpose planet/event tags. Examples: Industrial, Cop, Cult, Fuel, PassengerLounge.")]
    public List<string> tags = new List<string>();

    [Tooltip("General-purpose named true/false quest state.")]
    public List<PlanetStateFlag> flags = new List<PlanetStateFlag>();

    [Tooltip("General-purpose named integer state.")]
    public List<PlanetStateCounter> counters = new List<PlanetStateCounter>();

    [Tooltip("Simple delivery progress records. Event/inventory systems can read or update these without adding delivery logic to PlanetManager.")]
    public List<PlanetDeliveryState> deliveries = new List<PlanetDeliveryState>();


    public IReadOnlyList<string> Tags
    {
        get { return tags; }
    }


    public IReadOnlyList<PlanetStateFlag> Flags
    {
        get { return flags; }
    }


    public IReadOnlyList<PlanetStateCounter> Counters
    {
        get { return counters; }
    }


    public IReadOnlyList<PlanetDeliveryState> Deliveries
    {
        get { return deliveries; }
    }


    public bool HasTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        return tags.Contains(tag);
    }


    public bool AddTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tags.Contains(tag))
        {
            return false;
        }

        tags.Add(tag);
        return true;
    }


    public bool RemoveTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        return tags.Remove(tag);
    }


    public bool GetFlag(string key, bool fallback = false)
    {
        PlanetStateFlag entry = FindFlag(key);

        return entry != null
            ? entry.value
            : fallback;
    }


    public bool TryGetFlag(string key, out bool value)
    {
        PlanetStateFlag entry = FindFlag(key);

        if (entry == null)
        {
            value = false;
            return false;
        }

        value = entry.value;
        return true;
    }


    public void SetFlag(string key, bool value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        PlanetStateFlag entry = FindFlag(key);

        if (entry == null)
        {
            entry = new PlanetStateFlag { key = key };
            flags.Add(entry);
        }

        entry.value = value;
    }


    public bool RemoveFlag(string key)
    {
        PlanetStateFlag entry = FindFlag(key);
        return entry != null && flags.Remove(entry);
    }


    public int GetCounter(string key, int fallback = 0)
    {
        PlanetStateCounter entry = FindCounter(key);

        return entry != null
            ? entry.value
            : fallback;
    }


    public bool TryGetCounter(string key, out int value)
    {
        PlanetStateCounter entry = FindCounter(key);

        if (entry == null)
        {
            value = 0;
            return false;
        }

        value = entry.value;
        return true;
    }


    public void SetCounter(string key, int value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        PlanetStateCounter entry = FindCounter(key);

        if (entry == null)
        {
            entry = new PlanetStateCounter { key = key };
            counters.Add(entry);
        }

        entry.value = value;
    }


    public int IncrementCounter(string key, int delta = 1)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return 0;
        }

        int newValue = GetCounter(key) + delta;
        SetCounter(key, newValue);
        return newValue;
    }


    public bool RemoveCounter(string key)
    {
        PlanetStateCounter entry = FindCounter(key);
        return entry != null && counters.Remove(entry);
    }


    public PlanetDeliveryState FindDelivery(string deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId))
        {
            return null;
        }

        return deliveries.Find(
            item => item != null && item.deliveryId == deliveryId
        );
    }


    public bool TryGetDelivery(
        string deliveryId,
        out PlanetDeliveryState delivery)
    {
        delivery = FindDelivery(deliveryId);
        return delivery != null;
    }


    private PlanetStateFlag FindFlag(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return flags.Find(
            item => item != null && item.key == key
        );
    }


    private PlanetStateCounter FindCounter(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return counters.Find(
            item => item != null && item.key == key
        );
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
