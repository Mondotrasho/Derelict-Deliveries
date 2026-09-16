using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Common read/write contract for tag, flag and counter state.
///
/// PlanetEventState keeps its existing serialized field layout, while the
/// player uses EventStateData directly. Event selectors can depend on this
/// interface rather than caring which kind of object supplied the state.
/// </summary>
public interface IEventState
{
    IReadOnlyList<string> Tags { get; }

    bool HasTag(string tag);
    bool AddTag(string tag);
    bool RemoveTag(string tag);

    bool GetFlag(string key, bool fallback = false);
    void SetFlag(string key, bool value);

    int GetCounter(string key, int fallback = 0);
    void SetCounter(string key, int value);
    int IncrementCounter(string key, int delta = 1);
}


/// <summary>
/// Small, reusable collection of tags, boolean flags and integer counters.
///
/// This is deliberately gameplay-agnostic. It does not choose events,
/// evaluate quests, know about factions, or display UI. It only provides
/// stable state that feature systems can read and update without adding
/// their own fields to PlayerGridController, PlanetManager, or other core
/// world classes.
///
/// Typical uses:
/// - Tags: "Industrial", "Cult", "Wanted", "Enemy:FactionA".
/// - Flags: "MetCultLeader", "FoundLostCapsule".
/// - Counters: "IndustrialJobsCompleted", "FactionAReputation".
///
/// PlanetEventState derives from this type. PlayerShipState also owns one,
/// so event code can use the same basic API for both planet and player state.
/// </summary>
[Serializable]
public class EventStateData : IEventState
{
    [Tooltip("General-purpose classification/state tags used by outside systems.")]
    [SerializeField]
    private List<string> tags = new List<string>();

    [Tooltip("General-purpose named true/false state.")]
    [SerializeField]
    private List<EventStateFlag> flags = new List<EventStateFlag>();

    [Tooltip("General-purpose named integer state.")]
    [SerializeField]
    private List<EventStateCounter> counters = new List<EventStateCounter>();


    /// <summary>
    /// Read-only view intended for event selectors and other consumers that
    /// need to enumerate tags without taking ownership of the backing list.
    /// </summary>
    public IReadOnlyList<string> Tags
    {
        get { return tags; }
    }


    /// <summary>Read-only view of the named boolean state entries.</summary>
    public IReadOnlyList<EventStateFlag> Flags
    {
        get { return flags; }
    }


    /// <summary>Read-only view of the named integer state entries.</summary>
    public IReadOnlyList<EventStateCounter> Counters
    {
        get { return counters; }
    }


    public bool HasTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        return tags.Contains(tag);
    }


    /// <summary>
    /// Adds a tag if it is not already present.
    /// Returns true only when the collection actually changed.
    /// </summary>
    public bool AddTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tags.Contains(tag))
        {
            return false;
        }

        tags.Add(tag);
        return true;
    }


    /// <summary>
    /// Removes a tag.
    /// Returns true only when the collection actually changed.
    /// </summary>
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
        EventStateFlag entry = FindFlag(key);

        return entry != null
            ? entry.value
            : fallback;
    }


    public bool TryGetFlag(string key, out bool value)
    {
        EventStateFlag entry = FindFlag(key);

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

        EventStateFlag entry = FindFlag(key);

        if (entry == null)
        {
            entry = new EventStateFlag { key = key };
            flags.Add(entry);
        }

        entry.value = value;
    }


    /// <summary>
    /// Removes a named flag entirely rather than setting it false.
    /// Useful when "missing" and "false" need to remain distinct.
    /// </summary>
    public bool RemoveFlag(string key)
    {
        EventStateFlag entry = FindFlag(key);

        return entry != null && flags.Remove(entry);
    }


    public int GetCounter(string key, int fallback = 0)
    {
        EventStateCounter entry = FindCounter(key);

        return entry != null
            ? entry.value
            : fallback;
    }


    public bool TryGetCounter(string key, out int value)
    {
        EventStateCounter entry = FindCounter(key);

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

        EventStateCounter entry = FindCounter(key);

        if (entry == null)
        {
            entry = new EventStateCounter { key = key };
            counters.Add(entry);
        }

        entry.value = value;
    }


    /// <summary>
    /// Adds delta to a counter, creating it at zero if necessary.
    /// Returns the resulting value.
    /// </summary>
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
        EventStateCounter entry = FindCounter(key);

        return entry != null && counters.Remove(entry);
    }


    private EventStateFlag FindFlag(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return flags.Find(
            item => item != null && item.key == key
        );
    }


    private EventStateCounter FindCounter(string key)
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
public class EventStateFlag
{
    public string key;
    public bool value;
}


[Serializable]
public class EventStateCounter
{
    public string key;
    public int value;
}
