/// <summary>
/// Shared IEventState key strings used by Event Discovery.
/// These strings are a cross-feature data contract (see the Integration Guide),
/// so always build them through this class rather than typing them inline.
/// </summary>
public static class EventKeys
{
    /// <summary>Planet counter: first turn the planet source may roll this planet again.</summary>
    public const string NextRollTurn = "event:nextRollTurn";

    /// <summary>Planet tag: an event site is currently active on this planet.</summary>
    public const string Available = "event:available";

    /// <summary>Player counter: supplies (a resource until it gets a real home on ShipResources).</summary>
    public const string Supplies = "res.supplies";

    /// <summary>Player tag while this officer is aboard (written by OfficerRoster).</summary>
    public static string Officer(string officerId) => "officer:" + (officerId ?? "").Trim();

    /// <summary>Player counter: total of a stat across officers aboard (written by OfficerRoster).</summary>
    public static string Bonus(string stat) => "bonus:" + (stat ?? "").Trim().ToLowerInvariant();

    /// <summary>Planet flag: a once-per-planet event definition has been resolved here.</summary>
    public static string Done(string definitionId)
    {
        return "event:" + definitionId + ":done";
    }

    /// <summary>Player counter: number of resolved events in a category.</summary>
    public static string Resolved(EventCategory category)
    {
        return "events.resolved." + category.ToString().ToLowerInvariant();
    }
}
