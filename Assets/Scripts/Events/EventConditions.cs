using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Which IEventState a condition or write targets.</summary>
public enum StateScope
{
    Player,
    Planet,
    Site
}


public enum CounterCompare
{
    AtLeast,
    AtMost,
    Equal
}


[Serializable]
public sealed class TagCondition
{
    public StateScope scope = StateScope.Player;
    public string tag = "";

    [Tooltip("Condition: the tag must be present (on) or absent (off). Write: add (on) or remove (off).")]
    public bool mustHave = true;
}


[Serializable]
public sealed class FlagCondition
{
    public StateScope scope = StateScope.Player;
    public string key = "";
    public bool expected = true;
}


[Serializable]
public sealed class CounterCondition
{
    public StateScope scope = StateScope.Player;
    public string key = "";
    public CounterCompare compare = CounterCompare.AtLeast;

    [Tooltip("Condition: value compared against. Write: delta added to the counter.")]
    public int value = 1;
}


/// <summary>
/// Serializable condition block evaluated against existing IEventState objects.
/// It adds no state of its own: every check is HasTag / GetFlag / GetCounter.
///
/// Scope rules:
///   Planet-scope conditions FAIL when there is no planet in the context, so
///   planet-only events never appear on asteroids or derelicts.
///   Site-scope conditions are SKIPPED when there is no site yet (they cannot be
///   evaluated while a definition is being chosen for a new site) and are
///   checked normally at trigger time.
/// </summary>
[Serializable]
public sealed class EventConditions
{
    public List<TagCondition> tags = new List<TagCondition>();
    public List<FlagCondition> flags = new List<FlagCondition>();
    public List<CounterCondition> counters = new List<CounterCondition>();

    public bool IsEmpty =>
        (tags == null || tags.Count == 0) &&
        (flags == null || flags.Count == 0) &&
        (counters == null || counters.Count == 0);


    public bool IsMet(EventContext context)
    {
        if (tags != null)
        {
            foreach (TagCondition c in tags)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.tag)) continue;
                if (!TryResolve(context, c.scope, out IEventState state, out bool pass))
                {
                    if (!pass) return false;
                    continue;
                }

                if (state.HasTag(c.tag) != c.mustHave) return false;
            }
        }

        if (flags != null)
        {
            foreach (FlagCondition c in flags)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.key)) continue;
                if (!TryResolve(context, c.scope, out IEventState state, out bool pass))
                {
                    if (!pass) return false;
                    continue;
                }

                if (state.GetFlag(c.key) != c.expected) return false;
            }
        }

        if (counters != null)
        {
            foreach (CounterCondition c in counters)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.key)) continue;
                if (!TryResolve(context, c.scope, out IEventState state, out bool pass))
                {
                    if (!pass) return false;
                    continue;
                }

                int current = state.GetCounter(c.key);
                bool ok;
                switch (c.compare)
                {
                    case CounterCompare.AtMost: ok = current <= c.value; break;
                    case CounterCompare.Equal: ok = current == c.value; break;
                    default: ok = current >= c.value; break;
                }

                if (!ok) return false;
            }
        }

        return true;
    }


    /// <summary>
    /// Returns true with a state when the condition can be evaluated.
    /// Otherwise returns false and says whether the missing state counts as a
    /// pass (Site scope, no site yet) or a fail (Player/Planet scope missing).
    /// </summary>
    private static bool TryResolve(
        EventContext context,
        StateScope scope,
        out IEventState state,
        out bool passWhenMissing)
    {
        state = context.Get(scope);
        passWhenMissing = scope == StateScope.Site;
        return state != null;
    }
}


/// <summary>
/// Tags/flags/counters written through IEventState when an event is resolved.
/// </summary>
[Serializable]
public sealed class EventStateWrites
{
    [Tooltip("mustHave on = AddTag, off = RemoveTag.")]
    public List<TagCondition> tags = new List<TagCondition>();

    [Tooltip("SetFlag(key, expected).")]
    public List<FlagCondition> flags = new List<FlagCondition>();

    [Tooltip("IncrementCounter(key, value). The compare field is ignored here.")]
    public List<CounterCondition> counters = new List<CounterCondition>();


    public void Apply(EventContext context)
    {
        if (tags != null)
        {
            foreach (TagCondition w in tags)
            {
                if (w == null || string.IsNullOrWhiteSpace(w.tag)) continue;
                IEventState state = context.Get(w.scope);
                if (state == null) continue;
                if (w.mustHave) state.AddTag(w.tag);
                else state.RemoveTag(w.tag);
            }
        }

        if (flags != null)
        {
            foreach (FlagCondition w in flags)
            {
                if (w == null || string.IsNullOrWhiteSpace(w.key)) continue;
                IEventState state = context.Get(w.scope);
                if (state != null) state.SetFlag(w.key, w.expected);
            }
        }

        if (counters != null)
        {
            foreach (CounterCondition w in counters)
            {
                if (w == null || string.IsNullOrWhiteSpace(w.key)) continue;
                IEventState state = context.Get(w.scope);
                if (state != null) state.IncrementCounter(w.key, w.value);
            }
        }
    }
}
