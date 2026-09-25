using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One kind of thing that can host an event. Sources say WHERE an event could
/// go and whether a site is still valid; EventScanner owns the rolling.
/// </summary>
public interface IEventSiteSource
{
    string SourceId { get; }
    bool IsEnabled { get; }

    /// <summary>False for push-style sources (planets) that roll from their own events.</summary>
    bool RollsOnArrival { get; }

    EventCategory Category { get; }
    float RollChance { get; }
    int RerollCooldownTurns { get; }
    int MaxNewSitesPerScan { get; }

    /// <summary>The adjustable list of event families this source can host.</summary>
    IReadOnlyList<string> EventTags { get; }

    EventTable Table { get; }

    void CollectCandidates(Vector3Int centre, int radius, List<Vector3Int> results);

    /// <summary>Cooldown check. Default uses EventRollMemory; planets use their own eventState.</summary>
    bool CanRollCell(Vector3Int cell, int turn, EventRollMemory memory);
    void RecordRoll(Vector3Int cell, int turn, EventRollMemory memory);

    EventContext BuildContext(Vector3Int cell, int turn);
    EventSite CreateSite(Vector3Int cell, EventDefinition definition, int turn);
    bool IsSiteStillValid(EventSite site);
    void OnSiteRemoved(EventSite site);
}
