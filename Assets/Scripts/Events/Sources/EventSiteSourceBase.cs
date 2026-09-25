using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared serialized settings and default behaviour for event site sources.
/// Concrete sources mostly supply CollectCandidates / validity / cleanup.
/// </summary>
public abstract class EventSiteSourceBase : MonoBehaviour, IEventSiteSource
{
    [Header("Source")]
    [SerializeField] protected string sourceId = "source";

    [Tooltip("Master switch for this source. Existing sites are left alone when switched off.")]
    [SerializeField] protected bool sourceEnabled = true;

    [Range(0f, 1f)]
    [SerializeField] protected float rollChance = 0.08f;

    [Tooltip("Turns before the same cell may be rolled again (applies after every roll, win or lose).")]
    [Min(0)]
    [SerializeField] protected int rerollCooldownTurns = 5;

    [Min(0)]
    [SerializeField] protected int maxNewSitesPerScan = 1;

    [Tooltip("Event families this source can host. A definition must carry at least one of these tags.")]
    [SerializeField] protected List<string> eventTags = new List<string>();

    [SerializeField] protected EventTable table;

    [Header("Shared References")]
    [SerializeField] protected PlayerShipState player;

    private static int siteSerial;

    protected EventDirector Director { get; private set; }

    public string SourceId => string.IsNullOrWhiteSpace(sourceId) ? GetType().Name : sourceId.Trim();
    public bool IsEnabled => sourceEnabled && isActiveAndEnabled && table != null;
    public virtual bool RollsOnArrival => true;
    public abstract EventCategory Category { get; }
    public float RollChance { get => rollChance; set => rollChance = Mathf.Clamp01(value); }
    public int RerollCooldownTurns { get => rerollCooldownTurns; set => rerollCooldownTurns = Mathf.Max(0, value); }
    public int MaxNewSitesPerScan { get => maxNewSitesPerScan; set => maxNewSitesPerScan = Mathf.Max(0, value); }
    public IReadOnlyList<string> EventTags => eventTags;
    public EventTable Table => table;

    public bool SourceEnabled
    {
        get => sourceEnabled;
        set => sourceEnabled = value;
    }


    /// <summary>Called by EventDirector during its Awake.</summary>
    public virtual void Bind(EventDirector director)
    {
        Director = director;
        if (player == null) player = director != null ? director.Player : null;
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();

        if (sourceEnabled && table == null)
            Debug.LogWarning($"{name}: {GetType().Name} '{SourceId}' is enabled but has no Event Table, so it will never roll.", this);
    }


    public abstract void CollectCandidates(Vector3Int centre, int radius, List<Vector3Int> results);


    public virtual bool CanRollCell(Vector3Int cell, int turn, EventRollMemory memory)
    {
        return memory == null || memory.CanRoll(SourceId, cell, turn);
    }


    public virtual void RecordRoll(Vector3Int cell, int turn, EventRollMemory memory)
    {
        memory?.RecordRoll(SourceId, cell, turn, rerollCooldownTurns);
    }


    public virtual EventContext BuildContext(Vector3Int cell, int turn)
    {
        IEventState playerState = player != null ? player.EventState : null;
        return new EventContext(playerState, null, null, cell, turn);
    }


    public abstract EventSite CreateSite(Vector3Int cell, EventDefinition definition, int turn);
    public abstract bool IsSiteStillValid(EventSite site);
    public virtual void OnSiteRemoved(EventSite site) { }


    protected string MakeSiteId(Vector3Int cell, int turn)
    {
        siteSerial++;
        return $"event:{SourceId}:{cell.x},{cell.y}:t{turn}:{siteSerial}";
    }


    /// <summary>Adds every cell in the (2r+1)^2 square around centre.</summary>
    protected static void AddSquare(Vector3Int centre, int radius, List<Vector3Int> results)
    {
        for (int x = -radius; x <= radius; x++)
        {
            for (int y = -radius; y <= radius; y++)
            {
                results.Add(new Vector3Int(centre.x + x, centre.y + y, centre.z));
            }
        }
    }
}
