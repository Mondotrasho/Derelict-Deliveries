using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plain class that rolls candidate cells into event sites. No Unity lifecycle,
/// seeded System.Random, so a seed plus a route reproduces the same marks.
/// </summary>
public sealed class EventScanner
{
    private readonly EventSiteRegistry registry;
    private readonly EventRollMemory memory;
    private readonly System.Random rng;
    private readonly List<Vector3Int> candidates = new List<Vector3Int>();

    public int MinSiteSpacing { get; set; }
    public System.Random Random => rng;
    public EventRollMemory Memory => memory;

    /// <summary>Totals since construction, handy while tuning chances.</summary>
    public int RollsMade { get; private set; }
    public int SitesCreated { get; private set; }


    public EventScanner(EventSiteRegistry registry, EventRollMemory memory, int seed)
    {
        this.registry = registry;
        this.memory = memory;
        rng = new System.Random(seed);
    }


    /// <summary>
    /// Arrival scan: every enabled source with RollsOnArrival. The player's own
    /// cell is never rolled, and cells the ship cannot see are skipped.
    /// </summary>
    public int Scan(
        IReadOnlyList<IEventSiteSource> sources,
        Vector3Int centre,
        int radiusCap,
        int turn,
        Func<Vector3Int, bool> canSeeCell)
    {
        if (registry == null || sources == null) return 0;

        int created = 0;
        foreach (IEventSiteSource source in sources)
        {
            if (source == null || !source.IsEnabled || !source.RollsOnArrival) continue;
            if (source.MaxNewSitesPerScan <= 0) continue;

            candidates.Clear();
            source.CollectCandidates(centre, radiusCap, candidates);
            Shuffle(candidates);

            int madeHere = 0;
            foreach (Vector3Int cell in candidates)
            {
                if (madeHere >= source.MaxNewSitesPerScan) break;
                if (cell.x == centre.x && cell.y == centre.y) continue;
                if (canSeeCell != null && !canSeeCell(cell)) continue;

                if (TryRoll(source, cell, turn))
                {
                    madeHere++;
                    created++;
                }
            }
        }

        return created;
    }


    /// <summary>
    /// One roll for one cell. Push-style sources (planets) call this directly.
    /// Order: occupancy, spacing, cooldown, record the roll, chance, pick a
    /// definition whose conditions pass, register.
    /// </summary>
    public bool TryRoll(IEventSiteSource source, Vector3Int cell, int turn)
    {
        if (registry == null || source == null || source.Table == null) return false;
        if (registry.HasSiteAtCell(cell)) return false;
        if (registry.HasSiteWithin(cell, MinSiteSpacing)) return false;
        if (!source.CanRollCell(cell, turn, memory)) return false;

        source.RecordRoll(cell, turn, memory);
        RollsMade++;

        if (rng.NextDouble() >= source.RollChance) return false;

        EventContext context = source.BuildContext(cell, turn);
        EventDefinition definition = source.Table.PickWeighted(source.EventTags, context, rng);
        if (definition == null) return false;

        EventSite site = source.CreateSite(cell, definition, turn);
        if (site == null) return false;

        if (!registry.TryAdd(site))
        {
            source.OnSiteRemoved(site);
            return false;
        }

        SitesCreated++;
        return true;
    }


    private void Shuffle(List<Vector3Int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            Vector3Int t = list[i];
            list[i] = list[j];
            list[j] = t;
        }
    }
}
