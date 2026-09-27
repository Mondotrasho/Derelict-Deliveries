using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Authoritative cell -> event site lookup (shaped like EnemyShipRegistry).
/// One per scene. Sources, the scanner, the trigger handler and the marker
/// presenter all go through this rather than keeping their own copies.
/// </summary>
[DisallowMultipleComponent]
public class EventSiteRegistry : MonoBehaviour
{
    [Header("Debug")]
    [Tooltip("Draws a coloured square on every registered site in the Scene view.")]
    [SerializeField]
    private bool showSiteGizmos = true;

    [Tooltip("Used only to place gizmos. Auto-found when empty.")]
    [SerializeField]
    private GridMap gridMap;

    public event Action<EventSite> SiteAdded;
    public event Action<EventSite> SiteRemoved;
    public event Action<EventSite, EventSiteState> SiteStateChanged;
    public event Action<EventSite, PlanetKnowledgeState> SiteKnowledgeChanged;

    private readonly Dictionary<Vector3Int, EventSite> byCell = new Dictionary<Vector3Int, EventSite>();
    private readonly List<EventSite> sites = new List<EventSite>();

    public IReadOnlyList<EventSite> Sites => sites;
    public int Count => sites.Count;

    public GridMap GridMap
    {
        get
        {
            if (gridMap == null) gridMap = FindFirstObjectByType<GridMap>();
            return gridMap;
        }
    }


    /// <summary>Adds a site. Refuses a null site or an already occupied cell.</summary>
    public bool TryAdd(EventSite site)
    {
        if (site == null || byCell.ContainsKey(site.Cell)) return false;

        byCell.Add(site.Cell, site);
        sites.Add(site);
        SiteAdded?.Invoke(site);
        return true;
    }


    public bool TryGetSiteAtCell(Vector3Int cell, out EventSite site)
    {
        return byCell.TryGetValue(cell, out site);
    }


    public bool HasSiteAtCell(Vector3Int cell)
    {
        return byCell.ContainsKey(cell);
    }


    /// <summary>True if any site lies within radius cells (square / Chebyshev) of cell.</summary>
    public bool HasSiteWithin(Vector3Int cell, int radius)
    {
        if (radius <= 0) return byCell.ContainsKey(cell);

        foreach (EventSite s in sites)
        {
            if (Mathf.Abs(s.Cell.x - cell.x) <= radius &&
                Mathf.Abs(s.Cell.y - cell.y) <= radius)
            {
                return true;
            }
        }
        return false;
    }


    /// <summary>Nearest live site within radius cells (square), or null.</summary>
    public EventSite FindNearestLive(Vector3Int cell, int radius)
    {
        if (byCell.TryGetValue(cell, out EventSite exact) && exact.IsLive) return exact;
        if (radius <= 0) return null;

        EventSite best = null;
        int bestDist = int.MaxValue;
        foreach (EventSite s in sites)
        {
            if (!s.IsLive) continue;
            int d = Mathf.Max(Mathf.Abs(s.Cell.x - cell.x), Mathf.Abs(s.Cell.y - cell.y));
            if (d <= radius && d < bestDist)
            {
                best = s;
                bestDist = d;
            }
        }
        return best;
    }


    public void SetState(EventSite site, EventSiteState state)
    {
        if (site == null || site.LifecycleState == state) return;
        site.LifecycleState = state;
        SiteStateChanged?.Invoke(site, state);
    }


    /// <summary>Raises (never lowers) a site's knowledge. Returns true if it changed.</summary>
    public bool RaiseKnowledge(EventSite site, PlanetKnowledgeState to)
    {
        if (site == null || to <= site.Knowledge) return false;
        site.Knowledge = to;
        SiteKnowledgeChanged?.Invoke(site, to);
        return true;
    }


    /// <summary>Removes a site, raises SiteRemoved, then lets its source clean up.</summary>
    public bool Remove(EventSite site)
    {
        if (site == null) return false;
        if (!byCell.TryGetValue(site.Cell, out EventSite stored) || stored != site) return false;

        byCell.Remove(site.Cell);
        sites.Remove(site);
        SiteRemoved?.Invoke(site);
        site.Source?.OnSiteRemoved(site);
        return true;
    }


    /// <summary>Marks every site Expired and removes it.</summary>
    public void ClearAll()
    {
        for (int i = sites.Count - 1; i >= 0; i--)
        {
            EventSite s = sites[i];
            SetState(s, EventSiteState.Expired);
            Remove(s);
        }
    }


    private void OnDrawGizmos()
    {
        if (!showSiteGizmos || sites.Count == 0) return;
        GridMap map = GridMap;
        if (map == null) return;

        foreach (EventSite s in sites)
        {
            Vector3 c = map.CellToWorld(s.Cell);
            Vector3 size = map.CellToWorld(s.Cell + Vector3Int.one) - c;
            size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), 0.01f);

            switch (s.Knowledge)
            {
                case PlanetKnowledgeState.Identified: Gizmos.color = Color.green; break;
                case PlanetKnowledgeState.Detected: Gizmos.color = Color.yellow; break;
                default: Gizmos.color = Color.grey; break;
            }
            if (!s.IsLive) Gizmos.color = Color.red;

            Gizmos.DrawWireCube(c, size * 0.9f);
        }
    }
}
