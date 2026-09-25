using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tuning block for Event Discovery. Every value is a plain int/tier so stats
/// or upgrades can write it later. The real scan area is whatever the ship can
/// see (live vision at Roll Tier or better); Scan Radius Cap is only a hard limit.
/// </summary>
[Serializable]
public struct EventDiscoveryTuning
{
    [Tooltip("Hard square limit on the scan. Vision decides the real area.")]
    [Min(0)] public int scanRadiusCap;

    [Tooltip("0 = enter the site's cell. 1 = adjacent also triggers.")]
    [Min(0)] public int triggerRadius;

    [Tooltip("No new site within this many cells (square) of an existing one.")]
    [Min(0)] public int minSiteSpacing;

    [Tooltip("Derelicts only spawn within this many cells of an asteroid.")]
    [Min(0)] public int nearAsteroidRadius;

    [Tooltip("A cell must be seen live at this tier or better to be rolled; also the Detected threshold.")]
    public FogOfWar.VisibilityTier rollTier;

    [Tooltip("Live tier at which a site becomes Identified.")]
    public FogOfWar.VisibilityTier identifyTier;

    public static EventDiscoveryTuning Default => new EventDiscoveryTuning
    {
        scanRadiusCap = 8,
        triggerRadius = 0,
        minSiteSpacing = 2,
        nearAsteroidRadius = 2,
        rollTier = FogOfWar.VisibilityTier.Partial,
        identifyTier = FogOfWar.VisibilityTier.Full
    };
}


/// <summary>
/// Owns Event Discovery's arrival ordering. It is the ONLY event-system
/// subscriber to PlayerShipState.CellEntered:
///   1. not the player phase, or an event is already open -> stop;
///   2. try to trigger a live site on the entered cell;
///   3. if one claimed the arrival, do not scan;
///   4. otherwise roll visible candidate cells around the new location.
///
/// It also raises site knowledge from live fog tiers on
/// FogOfWar.PlayerVisionChanged, and sweeps expired/invalid sites on
/// TurnManager.PlayerPhaseStarted.
/// </summary>
[DisallowMultipleComponent]
public class EventDirector : MonoBehaviour
{
    [Header("Core References")]
    [SerializeField] private PlayerShipState player;
    [SerializeField] private TurnManager turnManager;
    [SerializeField] private VisionManager visionManager;
    [SerializeField] private FogOfWar fogOfWar;

    [Header("Event Discovery")]
    [SerializeField] private EventSiteRegistry registry;
    [SerializeField] private EventTriggerHandler trigger;

    [Tooltip("Sources in roll order.")]
    [SerializeField] private List<EventSiteSourceBase> sources = new List<EventSiteSourceBase>();

    [SerializeField] private EventDiscoveryTuning tuning = EventDiscoveryTuning.Default;

    [Header("Randomness")]
    [SerializeField] private int randomSeed = 48321;
    [SerializeField] private bool randomizeSeedOnAwake = false;

    [Header("Debug")]
    [SerializeField] private bool showScanGizmo = true;
    [SerializeField] private bool logScans = false;

    private EventScanner scanner;
    private readonly EventRollMemory memory = new EventRollMemory();
    private readonly List<IEventSiteSource> sourceList = new List<IEventSiteSource>();
    private readonly List<EventSite> sweepBuffer = new List<EventSite>();

    private PlayerShipState subscribedPlayer;
    private FogOfWar subscribedFog;
    private TurnManager subscribedTurns;
    private EventSiteRegistry subscribedRegistry;

    public PlayerShipState Player => player;
    public TurnManager TurnManager => turnManager;
    public VisionManager VisionManager => visionManager;
    public FogOfWar FogOfWar => fogOfWar;
    public EventSiteRegistry Registry => registry;
    public EventScanner Scanner => scanner;
    public EventRollMemory RollMemory => memory;

    public EventDiscoveryTuning Tuning
    {
        get => tuning;
        set
        {
            tuning = value;
            if (scanner != null) scanner.MinSiteSpacing = tuning.minSiteSpacing;
        }
    }

    public int ScanRadiusCap { get => tuning.scanRadiusCap; set => tuning.scanRadiusCap = Mathf.Max(0, value); }
    public int TriggerRadius { get => tuning.triggerRadius; set => tuning.triggerRadius = Mathf.Max(0, value); }
    public int NearAsteroidRadius { get => tuning.nearAsteroidRadius; set => tuning.nearAsteroidRadius = Mathf.Max(0, value); }
    public int MinSiteSpacing
    {
        get => tuning.minSiteSpacing;
        set
        {
            tuning.minSiteSpacing = Mathf.Max(0, value);
            if (scanner != null) scanner.MinSiteSpacing = tuning.minSiteSpacing;
        }
    }


    private void Awake()
    {
        if (player == null) player = GetComponent<PlayerShipState>();
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();
        if (visionManager == null) visionManager = GetComponent<VisionManager>();
        if (visionManager == null) visionManager = FindFirstObjectByType<VisionManager>();
        if (fogOfWar == null) fogOfWar = FindFirstObjectByType<FogOfWar>();
        if (registry == null) registry = FindFirstObjectByType<EventSiteRegistry>();
        if (trigger == null) trigger = GetComponent<EventTriggerHandler>();

        if (registry == null)
        {
            Debug.LogError($"{name}: EventDirector needs an EventSiteRegistry in the scene.", this);
            enabled = false;
            return;
        }

        if (randomizeSeedOnAwake) randomSeed = Guid.NewGuid().GetHashCode();

        scanner = new EventScanner(registry, memory, randomSeed) { MinSiteSpacing = tuning.minSiteSpacing };

        sourceList.Clear();
        foreach (EventSiteSourceBase s in sources)
        {
            if (s == null) continue;
            s.Bind(this);
            sourceList.Add(s);
        }
    }


    private void OnEnable()
    {
        if (scanner == null) return;

        subscribedPlayer = player;
        if (subscribedPlayer != null) subscribedPlayer.CellEntered += HandleCellEntered;

        subscribedFog = fogOfWar;
        if (subscribedFog != null) subscribedFog.PlayerVisionChanged += HandlePlayerVisionChanged;

        subscribedTurns = turnManager;
        if (subscribedTurns != null)
        {
            subscribedTurns.PlayerPhaseStarted += HandlePlayerPhaseStarted;
            subscribedTurns.ClockReset += HandleClockReset;
        }

        subscribedRegistry = registry;
        subscribedRegistry.SiteAdded += HandleSiteAdded;
    }


    private void OnDisable()
    {
        if (subscribedPlayer != null) subscribedPlayer.CellEntered -= HandleCellEntered;
        if (subscribedFog != null) subscribedFog.PlayerVisionChanged -= HandlePlayerVisionChanged;
        if (subscribedTurns != null)
        {
            subscribedTurns.PlayerPhaseStarted -= HandlePlayerPhaseStarted;
            subscribedTurns.ClockReset -= HandleClockReset;
        }
        if (subscribedRegistry != null) subscribedRegistry.SiteAdded -= HandleSiteAdded;

        subscribedPlayer = null;
        subscribedFog = null;
        subscribedTurns = null;
        subscribedRegistry = null;
    }


    // ==================== Arrival ====================

    private void HandleCellEntered(Vector3Int cell)
    {
        if (turnManager != null && !turnManager.IsPlayerPhase) return;
        if (trigger != null && trigger.IsBusy) return;

        if (trigger != null && trigger.TryTrigger(cell, tuning.triggerRadius)) return;   // trigger first

        int created = scanner.Scan(sourceList, cell, tuning.scanRadiusCap, CurrentTurn, CanRollCell);
        if (logScans)
            Debug.Log($"EventDirector scan @ {cell}: {created} new site(s), {registry.Count} live, {scanner.RollsMade} rolls total.", this);
    }


    /// <summary>Single roll at one cell for push-style sources (planets).</summary>
    public bool TryRollAt(IEventSiteSource source, Vector3Int cell)
    {
        if (scanner == null || source == null || !source.IsEnabled) return false;
        if (turnManager != null && !turnManager.IsPlayerPhase) return false;
        if (player != null && player.CurrentCell == cell) return false;
        return scanner.TryRoll(source, cell, CurrentTurn);
    }


    private bool CanRollCell(Vector3Int cell)
    {
        if (visionManager == null) return true;
        return visionManager.GetLiveVisibility(cell) >= tuning.rollTier;
    }


    private int CurrentTurn => turnManager != null ? turnManager.CurrentTurn : 0;


    // ==================== Knowledge ====================

    private void HandleSiteAdded(EventSite site)
    {
        UpdateKnowledge(site);
    }


    private void HandlePlayerVisionChanged(Vector3Int visionCell)
    {
        RefreshAllKnowledge();
    }


    /// <summary>Raises every site's knowledge from the current live fog tiers.</summary>
    public void RefreshAllKnowledge()
    {
        if (registry == null) return;
        sweepBuffer.Clear();
        sweepBuffer.AddRange(registry.Sites);
        foreach (EventSite site in sweepBuffer) UpdateKnowledge(site);
    }


    private void UpdateKnowledge(EventSite site)
    {
        if (site == null) return;

        if (site.Planet != null)
        {
            registry.RaiseKnowledge(site, site.Planet.visibility.knowledgeState);
            return;
        }

        if (visionManager == null) return;

        FogOfWar.VisibilityTier live = visionManager.GetLiveVisibility(site.Cell);
        if (live >= tuning.identifyTier)
            registry.RaiseKnowledge(site, PlanetKnowledgeState.Identified);
        else if (live >= tuning.rollTier)
            registry.RaiseKnowledge(site, PlanetKnowledgeState.Detected);
    }


    // ==================== Turn sweep ====================

    private void HandlePlayerPhaseStarted(int turn)
    {
        memory.Prune(turn);
        SweepSites(turn);
        RefreshAllKnowledge();
    }


    private void HandleClockReset()
    {
        memory.Clear();
    }


    private void SweepSites(int turn)
    {
        sweepBuffer.Clear();
        sweepBuffer.AddRange(registry.Sites);

        foreach (EventSite site in sweepBuffer)
        {
            if (!site.IsLive) continue;

            int lifetime = site.Definition != null ? site.Definition.LifetimeTurns : 0;
            bool expired = lifetime > 0 && turn - site.CreatedTurn >= lifetime;
            bool invalid = site.Source != null && !site.Source.IsSiteStillValid(site);

            if (expired || invalid)
            {
                registry.SetState(site, EventSiteState.Expired);
                registry.Remove(site);
            }
        }
    }


    // ==================== Debug helpers ====================

    [ContextMenu("Scan Now")]
    private void ScanNow()
    {
        if (scanner == null || player == null) return;
        int created = scanner.Scan(sourceList, player.CurrentCell, tuning.scanRadiusCap, CurrentTurn, CanRollCell);
        Debug.Log($"EventDirector manual scan: {created} new site(s).", this);
    }


    [ContextMenu("Clear All Sites")]
    private void ClearAllSites()
    {
        registry?.ClearAll();
    }


    [ContextMenu("Clear Roll Memory")]
    private void ClearRollMemory()
    {
        memory.Clear();
    }


    private void OnDrawGizmosSelected()
    {
        if (!showScanGizmo || player == null || registry == null) return;
        GridMap map = registry.GridMap;
        if (map == null) return;

        Vector3Int c = Application.isPlaying ? player.CurrentCell : map.GetCellForTransform(player.transform);
        DrawSquare(map, c, tuning.scanRadiusCap, new Color(0.3f, 0.8f, 1f, 0.8f));
        DrawSquare(map, c, tuning.triggerRadius, new Color(1f, 0.4f, 0.2f, 0.9f));
    }


    private static void DrawSquare(GridMap map, Vector3Int centre, int radius, Color colour)
    {
        Vector3 min = map.CellToWorld(centre - new Vector3Int(radius, radius, 0));
        Vector3 max = map.CellToWorld(centre + new Vector3Int(radius, radius, 0));
        Vector3 step = map.CellToWorld(centre + Vector3Int.one) - map.CellToWorld(centre);
        Vector3 size = (max - min) + new Vector3(Mathf.Abs(step.x), Mathf.Abs(step.y), 0f);

        Gizmos.color = colour;
        Gizmos.DrawWireCube((min + max) * 0.5f, new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), 0.01f));
    }
}
