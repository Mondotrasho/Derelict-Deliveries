using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Runtime-generated, multi-layer fog of war.
///
/// Vision API compatibility:
/// This revision is the matching FogOfWar for VisionManager v6 and exposes:
///     WorldToCell(Vector3)
///     GetPlayerVisibility(Vector3Int targetCell, Vector3Int observerCell)
///
/// Vision integration revision:
/// - exposes live player-only visibility separately from remembered/locked fog
/// - raises PlayerVisionChanged whenever the ship enters a new fog cell
/// - supports ID-addressable locked locations that can be updated or removed safely
///
/// The manager creates all fog Tilemaps as children of itself. The manager should itself live
/// underneath the same Unity Grid as worldBoundsTilemap.
///
/// Instead of manually configuring several separate fog layers, this version generates a stack
/// from a small set of controls:
/// - number of visual layers
/// - inner and outer vision radius
/// - inner and outer edge thickness
/// - outer fog opacity and the desired opacity step toward the centre
/// - one Sorting Layer and one starting sorting order
/// - one shared hidden tile and one shared half-hidden tile
///
/// The generated layers are ordered from the smallest radius to the largest radius. Every larger
/// radius receives the next sorting order, so the outermost vision layer is one order above the
/// layer immediately inside it.
///
/// Opacity is composite-aware. The Inspector opacity values describe the FINAL desired fog
/// opacity of each concentric band, not the raw alpha of every stacked Tilemap. The manager solves
/// the alpha needed by each generated sheet so stacking them does not accidentally make the inner
/// fade much darker than requested.
///
/// Every generated radius keeps its own visible/remembered/hidden state. This means the smaller,
/// fainter inner vision radii also leave a remembered trail and decay instead of existing only as
/// an immediate circle around the ship.
/// </summary>
public class FogOfWar : MonoBehaviour
{
    public enum VisibilityTier
    {
        Hidden,
        Partial,
        Full
    }

    public enum PopEasing
    {
        Linear,
        EaseInQuad,
        EaseOutQuad,
        EaseInOutCubic,
        SmoothStep,
        SmootherStep,
        Custom
    }

    private enum FogState
    {
        Hidden,
        Remembered,
        Visible
    }

    /// <summary>
    /// Radius settings sampled across the generated visual layers.
    /// Layer 0 uses the Inner values, the outermost generated layer uses the Outer values,
    /// and any layers between them are interpolated automatically.
    /// </summary>
    [System.Serializable]
    private class RadiusStackProfile
    {
        [Min(0)]
        [Tooltip("Vision radius used by the smallest / innermost generated fog layer.")]
        public int innerRadius = 2;

        [Min(0)]
        [Tooltip("Vision radius used by the largest / outermost generated fog layer.")]
        public int outerRadius = 4;

        [Min(0)]
        [Tooltip("Half-hidden edge thickness used by the innermost generated layer.")]
        public int innerEdgeThickness = 1;

        [Min(0)]
        [Tooltip("Half-hidden edge thickness used by the outermost generated layer.")]
        public int outerEdgeThickness = 2;

        public int GetRadius(int innerToOuterIndex, int layerCount)
        {
            if (layerCount <= 1)
                return Mathf.Max(0, outerRadius);

            float t = innerToOuterIndex / (float)(layerCount - 1);
            return Mathf.Max(0, Mathf.RoundToInt(Mathf.Lerp(innerRadius, outerRadius, t)));
        }

        public int GetEdgeThickness(int innerToOuterIndex, int layerCount, int radius)
        {
            float t = layerCount <= 1
                ? 1f
                : innerToOuterIndex / (float)(layerCount - 1);

            int thickness = Mathf.RoundToInt(
                Mathf.Lerp(innerEdgeThickness, outerEdgeThickness, t));

            return Mathf.Clamp(thickness, 0, Mathf.Max(0, radius));
        }

        public void ClampValues()
        {
            innerRadius = Mathf.Max(0, innerRadius);
            outerRadius = Mathf.Max(innerRadius, outerRadius);
            innerEdgeThickness = Mathf.Clamp(innerEdgeThickness, 0, innerRadius);
            outerEdgeThickness = Mathf.Clamp(outerEdgeThickness, 0, outerRadius);
        }
    }

    private class FogCell
    {
        public FogState state;
        public int decayStepsRemaining;
        public int displayLevel;
        public int pendingTarget;
        public int lockedCeiling = 2;
    }

    /// <summary>
    /// One generated fog sheet. The list is runtime-only and is rebuilt from the Inspector stack
    /// controls. Each sheet has its own memory and decay state.
    /// </summary>
    private class FogLayer
    {
        public int layerIndex;
        public string layerName;
        public int visionRadius;
        public int edgeThickness;

        // Desired final opacity of the concentric band represented by this layer.
        public float targetCompositeOpacity;

        // Actual alpha assigned to this one Tilemap sheet after compensating for stacking.
        public float sheetOpacity;

        public int sortingOrder;
        public Tilemap fogTilemap;
        public Tilemap overlayTilemap;

        public readonly Dictionary<Vector3Int, FogCell> fogData =
            new Dictionary<Vector3Int, FogCell>();

        public readonly HashSet<Vector3Int> currentlyVisible =
            new HashSet<Vector3Int>();

        public readonly Dictionary<Vector3Int, Coroutine> popCoroutines =
            new Dictionary<Vector3Int, Coroutine>();
    }

    /// <summary>
    /// Describes where a locked fog location came from.
    /// Used for runtime inspection/debugging only.
    /// </summary>
    public enum LockedLocationSource
    {
        Initial,
        RuntimeAnonymous,
        RuntimeNamed
    }


    /// <summary>
    /// Read-only snapshot returned to editor/debug tools.
    /// This does not expose the mutable internal LockedLocation object.
    /// </summary>
    public struct LockedLocationInfo
    {
        public string Id { get; }
        public Vector3Int Cell { get; }
        public VisibilityTier Tier { get; }
        public LockedLocationSource Source { get; }

        public bool IsNamed
        {
            get { return !string.IsNullOrWhiteSpace(Id); }
        }

        public LockedLocationInfo(
            string id,
            Vector3Int cell,
            VisibilityTier tier,
            LockedLocationSource source)
        {
            Id = id;
            Cell = cell;
            Tier = tier;
            Source = source;
        }
    }


    private class LockedLocation
    {
        // Null/empty IDs are legacy anonymous locks created through AddLockedLocation().
        // Named locks are owned by systems such as VisionManager and can be updated/removed.
        public string id;
        public Vector3Int cell;
        public VisibilityTier tier;
        public LockedLocationSource source;
    }

    [System.Serializable]
    private class SerialisedLockedLocation
    {
        [Tooltip("Optional stable ID. Leave blank for a legacy anonymous permanent lock.")]
        public string id;

        public Vector3Int cell;
        public VisibilityTier tier = VisibilityTier.Full;
    }

    [Header("Shared Fog Tiles")]
    [Tooltip("Tile used for fully hidden fog. Shared by every generated layer.")]
    [SerializeField] private TileBase hiddenTile;

    [Tooltip("Tile used for remembered fog and the soft edge ring. Shared by every generated layer.")]
    [SerializeField] private TileBase halfHiddenTile;

    [Header("Vision Layer Stack")]
    [Tooltip("How many concentric visual fog layers are generated. Intermediate radii and edge thicknesses are interpolated automatically.")]
    [SerializeField, Min(1)] private int visionLayerCount = 3;

    [Tooltip("Player vision profile. Inner values belong to the smallest radius. Outer values belong to the largest radius.")]
    [SerializeField] private RadiusStackProfile playerVision = new RadiusStackProfile();

    [Tooltip("Maximum grid distance from the player that counts as Full visibility. " +
             "Cells beyond this distance, but still inside the maximum vision radius, count as Partial.")]
    [SerializeField, Min(0)]
    private int fullVisibilityRadius = 2;

    [Range(0f, 1f)]
    [Tooltip("Final desired fog opacity outside the outermost vision radius. Normally 1 for completely hidden fog.")]
    [SerializeField] private float outerFogOpacity = 1f;

    [Range(0f, 1f)]
    [Tooltip("How much the FINAL fog opacity drops for each smaller generated radius. The manager compensates for Tilemap alpha stacking automatically.")]
    [SerializeField] private float opacityStepTowardCenter = 0.20f;

    [Header("Fog Rendering")]
    [Tooltip("Sorting Layer used by every runtime-generated fog Tilemap.")]
    [SortingLayerName]
    [SerializeField] private string sortingLayerName = "Default";

    [Tooltip("Sorting order used by the innermost settled fog layer. Each larger radius uses +1. Animation overlays are placed in a separate range above all settled layers.")]
    [SerializeField] private int startingSortingOrder = 10;

    [Header("References")]
    [Tooltip("Any Tilemap defining the playable area. Used for fog bounds and world/cell conversion.")]
    [SerializeField] private Tilemap worldBoundsTilemap;

    [Tooltip("Ship/player Transform whose actual world position drives vision.")]
    [SerializeField] private Transform player;

    [Tooltip("Optional logical grid controller. CellReached is used as an exact logical-cell update in addition to world-position tracking.")]
    [SerializeField] private PlayerGridController playerController;

    [Tooltip("Optional turn manager. Remembered fog decays when TurnEnded is raised.")]
    [SerializeField] private TurnManager turnManager;

    [Header("Decay")]
    [Tooltip("How many AdvanceTurn calls a remembered cell survives before returning to fully hidden. Every generated radius decays independently.")]
    [SerializeField, Min(1)] private int decayStepsToHide = 3;

    [Header("Pop Effect")]
    [Tooltip("Base duration of a fog pop before ship speed and cell distance are taken into account.")]
    [SerializeField, Min(0.001f)] private float popDuration = 0.15f;

    [Tooltip("Easing used by fog pop animations. Custom uses the AnimationCurve below.")]
    [SerializeField] private PopEasing popEasing = PopEasing.Custom;

    [Tooltip("Used only when Pop Easing is Custom. Overshoot is allowed.")]
    [SerializeField]
    private AnimationCurve popCurve = new AnimationCurve(
        new Keyframe(0f, 0f),
        new Keyframe(0.7f, 1.15f),
        new Keyframe(1f, 1f));

    [Header("Pop Motion Response")]
    [Tooltip("If enabled, new pops choose their duration from the ship's smoothed world-space speed.")]
    [SerializeField] private bool usePlayerSpeedForPop = true;

    [Tooltip("Ship speed that counts as fully fast for pop timing. Faster movement is clamped here.")]
    [SerializeField, Min(0.01f)] private float speedForFastestPop = 2.5f;

    [Tooltip("At maximum measured ship speed, pop duration is multiplied by this value before final clamping.")]
    [SerializeField, Range(0.1f, 1f)] private float fastestPopDurationMultiplier = 0.60f;

    [Tooltip("How quickly measured ship speed settles. Higher values react faster.")]
    [SerializeField, Min(0f)] private float speedSmoothing = 8f;

    [Tooltip("Near cells can pop slightly faster and outer cells slightly slower. 0 disables this radial timing effect.")]
    [SerializeField, Range(0f, 0.45f)] private float positionDurationInfluence = 0.15f;

    [Tooltip("Hard lower bound for one pop animation.")]
    [SerializeField, Min(0.001f)] private float minimumPopDuration = 0.06f;

    [Tooltip("Hard upper bound for one pop animation.")]
    [SerializeField, Min(0.001f)] private float maximumPopDuration = 0.25f;

    [Header("Locked Location - Partial")]
    [Tooltip("Concentric radius stack used when a location is locked with VisibilityTier.Partial. Uses the same generated opacity steps as player vision.")]
    [SerializeField]
    private RadiusStackProfile partialLockedVision = new RadiusStackProfile
    {
        innerRadius = 0,
        outerRadius = 1,
        innerEdgeThickness = 0,
        outerEdgeThickness = 1
    };

    [Header("Locked Location - Full")]
    [Tooltip("Concentric radius stack used when a location is locked with VisibilityTier.Full. Uses the same generated opacity steps as player vision.")]
    [SerializeField]
    private RadiusStackProfile fullLockedVision = new RadiusStackProfile
    {
        innerRadius = 1,
        outerRadius = 2,
        innerEdgeThickness = 0,
        outerEdgeThickness = 1
    };

    [Header("Initial Locked Locations")]
    [Tooltip("Optional locations already discovered when play starts.")]
    [SerializeField]
    private List<SerialisedLockedLocation> initialLockedLocations =
        new List<SerialisedLockedLocation>();

    private readonly List<FogLayer> layers = new List<FogLayer>();
    private readonly List<LockedLocation> lockedLocations = new List<LockedLocation>();

    private Vector3Int lastPlayerCell;
    private bool initialised;

    private Vector3 previousPlayerPosition;
    private float smoothedPlayerSpeed;
    private bool hasPreviousPlayerPosition;

    private bool missingGridErrorLogged;

    /// <summary>
    /// Raised whenever the ship's live fog-centre cell changes or current vision is force-refreshed.
    /// This is the event VisionManager listens to. Locked-location changes do not raise it.
    /// </summary>
    public event Action<Vector3Int> PlayerVisionChanged;

    /// <summary>The last grid cell used as the centre of live player vision.</summary>
    public Vector3Int CurrentPlayerVisionCell
    {
        get { return lastPlayerCell; }
    }

    /// <summary>True once live player vision has been evaluated at least once.</summary>
    public bool HasInitialisedPlayerVision
    {
        get { return initialised; }
    }

    private void Start()
    {
        BuildGeneratedLayers();

        foreach (SerialisedLockedLocation entry in initialLockedLocations)
        {
            if (!string.IsNullOrWhiteSpace(entry.id))
            {
                SetLockedLocationInternal(
                    entry.id,
                    entry.cell,
                    entry.tier,
                    LockedLocationSource.Initial
                );
            }
            else
            {
                AddLockedLocationInternal(
                    entry.cell,
                    entry.tier,
                    LockedLocationSource.Initial
                );
            }
        }

        if (player != null)
        {
            previousPlayerPosition = player.position;
            hasPreviousPlayerPosition = true;
        }

        UpdateVisibility(force: true);
    }

    private void OnEnable()
    {
        if (playerController != null)
            playerController.CellReached += HandleCellReached;

        if (turnManager != null)
            turnManager.TurnEnded += HandleTurnEnded;
    }

    private void OnDisable()
    {
        if (playerController != null)
            playerController.CellReached -= HandleCellReached;

        if (turnManager != null)
            turnManager.TurnEnded -= HandleTurnEnded;
    }

    private void LateUpdate()
    {
        UpdatePlayerMotion();
        UpdateVisibility(force: false);
    }

    private void OnValidate()
    {
        visionLayerCount = Mathf.Max(1, visionLayerCount);

        if (playerVision != null)
        {
            fullVisibilityRadius =
                Mathf.Clamp(
                    fullVisibilityRadius,
                    0,
                    Mathf.Max(0, playerVision.outerRadius)
                );
        }
        else
        {
            fullVisibilityRadius =
                Mathf.Max(0, fullVisibilityRadius);
        }

        decayStepsToHide = Mathf.Max(1, decayStepsToHide);

        outerFogOpacity = Mathf.Clamp01(outerFogOpacity);
        opacityStepTowardCenter = Mathf.Clamp01(opacityStepTowardCenter);

        playerVision?.ClampValues();
        partialLockedVision?.ClampValues();
        fullLockedVision?.ClampValues();

        minimumPopDuration = Mathf.Max(0.001f, minimumPopDuration);
        maximumPopDuration = Mathf.Max(minimumPopDuration, maximumPopDuration);
        popDuration = Mathf.Clamp(popDuration, minimumPopDuration, maximumPopDuration);
        speedForFastestPop = Mathf.Max(0.01f, speedForFastestPop);
        speedSmoothing = Mathf.Max(0f, speedSmoothing);
    }

    private void HandleCellReached(Vector3Int cell)
    {
        UpdateVisibilityAtCell(cell, force: false);
    }

    private void HandleTurnEnded(int turnNumber)
    {
        AdvanceTurn();
    }

    /// <summary>
    /// Rebuilds all runtime fog Tilemaps from the current stack settings.
    /// This is mainly useful while tuning settings during Play mode. Rebuilding resets ordinary
    /// explored/remembered fog state, but replays all locked locations afterwards.
    /// </summary>
    [ContextMenu("Rebuild Runtime Fog Layers")]
    public void RebuildGeneratedLayers()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning(
                $"{name}: runtime fog layers are created in Play mode. Enter Play mode before rebuilding.",
                this);
            return;
        }

        BuildGeneratedLayers();
        UpdateVisibility(force: true);
    }

    /// <summary>
    /// Manual alternative to event wiring. Updates current vision and advances remembered decay.
    /// </summary>
    public void Step()
    {
        UpdateVisibility(force: false);
        AdvanceTurn();
    }

    /// <summary>
    /// Changes the generated layer count at runtime and rebuilds the stack.
    /// </summary>
    public void SetVisionLayerCount(int count)
    {
        visionLayerCount = Mathf.Max(1, count);
        BuildGeneratedLayers();
        UpdateVisibility(force: true);
    }

    /// <summary>
    /// Reports the currently rendered fog visibility after combining:
    /// - live player vision
    /// - remembered fog
    /// - permanently locked locations
    ///
    /// Use GetPlayerVisibility() instead when game logic needs to know whether
    /// the ship can see a cell RIGHT NOW.
    /// </summary>
    public VisibilityTier GetVisibility(Vector3 worldPosition)
    {
        if (worldBoundsTilemap == null)
            return VisibilityTier.Hidden;

        return GetVisibility(
            worldBoundsTilemap.WorldToCell(worldPosition)
        );
    }

    public VisibilityTier GetVisibility(Vector3Int cell)
    {
        bool foundRelevantLayer = false;
        bool anyReveal = false;
        bool allClear = true;

        foreach (FogLayer layer in layers)
        {
            if (layer.sheetOpacity <= 0.001f)
                continue;

            if (!layer.fogData.TryGetValue(cell, out FogCell data))
                continue;

            foundRelevantLayer = true;

            // During an animation pendingTarget already describes the requested
            // logical visibility, so game logic does not wait for the cosmetic pop.
            int logicalLevel =
                Mathf.Min(
                    data.displayLevel,
                    data.pendingTarget
                );

            if (logicalLevel < 2)
                anyReveal = true;

            if (logicalLevel > 0)
                allClear = false;
        }

        return ConvertLayerLevelsToTier(
            foundRelevantLayer,
            anyReveal,
            allClear
        );
    }

    /// <summary>
    /// Converts the composite logical state of the rendered fog layers into
    /// the public Hidden / Partial / Full visibility tier.
    ///
    /// This is used by GetVisibility(), which includes remembered and locked
    /// fog state. Live player visibility uses the separate explicit radial rule.
    /// </summary>
    private VisibilityTier ConvertLayerLevelsToTier(
        bool foundRelevantLayer,
        bool anyReveal,
        bool allClear)
    {
        if (!foundRelevantLayer)
            return VisibilityTier.Hidden;

        if (allClear)
            return VisibilityTier.Full;

        if (anyReveal)
            return VisibilityTier.Partial;

        return VisibilityTier.Hidden;
    }


    /// <summary>
    /// Converts a world-space position into the same grid-cell coordinates used
    /// internally by this FogOfWar instance.
    ///
    /// VisionManager uses this so its explicit player Transform tracking cannot
    /// accidentally disagree with the Fog Manager's grid conversion.
    /// </summary>
    public Vector3Int WorldToCell(Vector3 worldPosition)
    {
        if (worldBoundsTilemap == null)
            return Vector3Int.zero;

        return worldBoundsTilemap.WorldToCell(worldPosition);
    }


    /// <summary>
    /// Reports ONLY the player's live vision at a world position.
    ///
    /// Remembered fog and locked locations are deliberately ignored. This is
    /// the query VisionManager should use to decide whether an object is
    /// currently visible to the player.
    /// </summary>
    public VisibilityTier GetPlayerVisibility(Vector3 worldPosition)
    {
        if (worldBoundsTilemap == null)
            return VisibilityTier.Hidden;

        return GetPlayerVisibility(
            worldBoundsTilemap.WorldToCell(worldPosition)
        );
    }

    /// <summary>
    /// Grid-cell overload of GetPlayerVisibility().
    ///
    /// This version uses FogOfWar's current cached live player cell.
    /// VisionManager normally uses the explicit observer-cell overload below.
    /// </summary>
    public VisibilityTier GetPlayerVisibility(Vector3Int cell)
    {
        Vector3Int playerCell;

        if (initialised)
        {
            playerCell = lastPlayerCell;
        }
        else
        {
            if (worldBoundsTilemap == null || player == null)
                return VisibilityTier.Hidden;

            playerCell =
                worldBoundsTilemap.WorldToCell(
                    player.position
                );
        }

        return GetPlayerVisibility(
            cell,
            playerCell
        );
    }


    /// <summary>
    /// Reports LIVE player vision for a target cell using an explicit observer cell.
    ///
    /// This ignores remembered fog and locked locations. Passing the observer cell
    /// explicitly is useful for systems such as VisionManager because it removes any
    /// dependency on script execution order or FogOfWar's cached lastPlayerCell.
    /// </summary>
    public VisibilityTier GetPlayerVisibility(
        Vector3Int targetCell,
        Vector3Int observerCell)
    {
        // The visual fog still uses the complete generated layer stack.
        //
        // Gameplay visibility uses two direct radii:
        //
        // distance <= Full Visibility Radius = Full
        // distance <= Maximum Vision Radius   = Partial
        // outside Maximum Vision Radius       = Hidden
        //
        // This makes the distance required for a clear/full reveal independent
        // from how far the fog visually opens around the player.
        int dx =
            targetCell.x - observerCell.x;

        int dy =
            targetCell.y - observerCell.y;

        int distanceSqr =
            dx * dx + dy * dy;

        int maxRadius =
            Mathf.Max(
                0,
                playerVision.outerRadius
            );

        int maxRadiusSqr =
            maxRadius * maxRadius;

        if (distanceSqr > maxRadiusSqr)
            return VisibilityTier.Hidden;

        int clampedFullRadius =
            Mathf.Clamp(
                fullVisibilityRadius,
                0,
                maxRadius
            );

        int fullRadiusSqr =
            clampedFullRadius *
            clampedFullRadius;

        if (distanceSqr <= fullRadiusSqr)
            return VisibilityTier.Full;

        return VisibilityTier.Partial;
    }


    /// <summary>
    /// Returns this one generated layer's live player-vision level:
    /// 0 = clear core, 1 = soft edge, 2 = outside this layer's live radius.
    /// </summary>
    private int GetPlayerVisionLevelForLayer(
        FogLayer layer,
        Vector3Int cell,
        Vector3Int playerCell)
    {
        int dx =
            cell.x - playerCell.x;

        int dy =
            cell.y - playerCell.y;

        int distanceSqr =
            dx * dx + dy * dy;

        int radius =
            Mathf.Max(0, layer.visionRadius);

        int radiusSqr =
            radius * radius;

        if (distanceSqr > radiusSqr)
            return 2;

        int innerRadius =
            Mathf.Max(
                0,
                radius - layer.edgeThickness
            );

        int innerRadiusSqr =
            innerRadius * innerRadius;

        return distanceSqr > innerRadiusSqr
            ? 1
            : 0;
    }

    // =====================================================================
    // Locked locations
    // =====================================================================

    /// <summary>
    /// Legacy anonymous permanent lock. Existing code can keep using this.
    /// VisionManager should prefer SetLockedLocation() with a stable ID.
    /// </summary>
    public void AddLockedLocation(
        Vector3 worldPosition,
        VisibilityTier tier)
    {
        if (worldBoundsTilemap == null)
        {
            Debug.LogError(
                $"{name}: AddLockedLocation needs worldBoundsTilemap assigned to convert world position to a cell.",
                this);
            return;
        }

        AddLockedLocation(
            worldBoundsTilemap.WorldToCell(worldPosition),
            tier
        );
    }

    /// <summary>
    /// Legacy anonymous permanent lock.
    /// </summary>
    public void AddLockedLocation(
        Vector3Int cell,
        VisibilityTier tier)
    {
        AddLockedLocationInternal(
            cell,
            tier,
            LockedLocationSource.RuntimeAnonymous
        );
    }


    private void AddLockedLocationInternal(
        Vector3Int cell,
        VisibilityTier tier,
        LockedLocationSource source)
    {
        if (tier == VisibilityTier.Hidden)
        {
            Debug.LogWarning(
                $"{name}: AddLockedLocation was given VisibilityTier.Hidden, so nothing was locked.",
                this);
            return;
        }

        LockedLocation location =
            new LockedLocation
            {
                id = null,
                cell = cell,
                tier = tier,
                source = source
            };

        lockedLocations.Add(location);

        for (int i = 0; i < layers.Count; i++)
        {
            RevealLockedLocationOnLayer(
                layers[i],
                location
            );
        }
    }

    /// <summary>
    /// Creates or updates one named locked location.
    ///
    /// If the ID already exists, its cell/tier are replaced instead of another
    /// duplicate entry being appended. Hidden means "remove this named lock".
    ///
    /// Returns true only when the stored lock actually changed.
    /// </summary>
    public bool SetLockedLocation(
        string id,
        Vector3Int cell,
        VisibilityTier tier)
    {
        return SetLockedLocationInternal(
            id,
            cell,
            tier,
            LockedLocationSource.RuntimeNamed
        );
    }


    private bool SetLockedLocationInternal(
        string id,
        Vector3Int cell,
        VisibilityTier tier,
        LockedLocationSource source)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            Debug.LogWarning(
                $"{name}: SetLockedLocation needs a non-empty ID.",
                this);
            return false;
        }

        if (tier == VisibilityTier.Hidden)
            return RemoveLockedLocation(id);

        LockedLocation existing =
            lockedLocations.Find(
                location =>
                    location != null &&
                    location.id == id
            );

        if (existing != null)
        {
            bool changed =
                existing.cell != cell ||
                existing.tier != tier ||
                existing.source != source;

            if (!changed)
                return false;

            existing.cell = cell;
            existing.tier = tier;
            existing.source = source;

            RebuildLockedLocationInfluence();
            return true;
        }

        lockedLocations.Add(
            new LockedLocation
            {
                id = id,
                cell = cell,
                tier = tier,
                source = source
            });

        RebuildLockedLocationInfluence();
        return true;
    }


    public bool SetLockedLocation(
        string id,
        Vector3 worldPosition,
        VisibilityTier tier)
    {
        if (worldBoundsTilemap == null)
            return false;

        return SetLockedLocation(
            id,
            worldBoundsTilemap.WorldToCell(worldPosition),
            tier
        );
    }

    /// <summary>
    /// Removes one named locked location and recalculates the remaining lock
    /// influence without resetting normal explored/remembered fog state.
    /// </summary>
    public bool RemoveLockedLocation(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        int removed =
            lockedLocations.RemoveAll(
                location =>
                    location != null &&
                    location.id == id
            );

        if (removed == 0)
            return false;

        RebuildLockedLocationInfluence();
        return true;
    }

    public bool HasLockedLocation(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return false;

        return lockedLocations.Exists(
            location =>
                location != null &&
                location.id == id
        );
    }

    /// <summary>
    /// Reads one named lock without exposing the private LockedLocation type.
    /// </summary>
    public bool TryGetLockedLocation(
        string id,
        out Vector3Int cell,
        out VisibilityTier tier)
    {
        LockedLocation existing =
            lockedLocations.Find(
                location =>
                    location != null &&
                    location.id == id
            );

        if (existing == null)
        {
            cell = default;
            tier = VisibilityTier.Hidden;
            return false;
        }

        cell = existing.cell;
        tier = existing.tier;
        return true;
    }

    /// <summary>
    /// Number of locked locations currently active inside the fog system.
    ///
    /// This includes:
    /// - Inspector-seeded Initial Locked Locations
    /// - anonymous runtime AddLockedLocation entries
    /// - named runtime SetLockedLocation entries, including VisionManager planets
    /// </summary>
    public int LockedLocationCount
    {
        get { return lockedLocations.Count; }
    }


    /// <summary>
    /// Returns a read-only snapshot of every currently active locked location.
    /// Intended for debugging, UI and editor inspection.
    /// </summary>
    public List<LockedLocationInfo> GetLockedLocationSnapshot()
    {
        List<LockedLocationInfo> snapshot =
            new List<LockedLocationInfo>(
                lockedLocations.Count
            );

        foreach (LockedLocation location in lockedLocations)
        {
            if (location == null)
                continue;

            snapshot.Add(
                new LockedLocationInfo(
                    location.id,
                    location.cell,
                    location.tier,
                    location.source
                )
            );
        }

        return snapshot;
    }


    private void BuildGeneratedLayers()
    {
        if (!ValidateGridParent())
            return;

        if (worldBoundsTilemap == null)
        {
            Debug.LogError(
                $"{name}: FogOfWar needs worldBoundsTilemap assigned before runtime layers can be built.",
                this);
            return;
        }

        StopAllCoroutines();
        DestroyGeneratedLayers();
        layers.Clear();

        int count = Mathf.Max(1, visionLayerCount);
        float previousCompositeOpacity = 0f;

        for (int i = 0; i < count; i++)
        {
            int radius = playerVision.GetRadius(i, count);
            int edgeThickness = playerVision.GetEdgeThickness(i, count, radius);

            float targetCompositeOpacity = GetTargetCompositeOpacity(i, count);
            float sheetOpacity = SolveSheetOpacity(previousCompositeOpacity, targetCompositeOpacity);

            FogLayer layer = new FogLayer
            {
                layerIndex = i,
                layerName = $"Vision {i + 1} R{radius}",
                visionRadius = radius,
                edgeThickness = edgeThickness,
                targetCompositeOpacity = targetCompositeOpacity,
                sheetOpacity = sheetOpacity,
                sortingOrder = startingSortingOrder + i
            };

            InitialiseLayer(layer, count);
            layers.Add(layer);

            previousCompositeOpacity = CompositeOpacity(previousCompositeOpacity, sheetOpacity);
        }

        initialised = false;
    }

    /// <summary>
    /// Returns the desired FINAL fog opacity of the annulus represented by one generated layer.
    /// Example with 3 layers, outer opacity 1 and step 0.2: inner=0.6, middle=0.8, outer=1.0.
    /// </summary>
    private float GetTargetCompositeOpacity(int innerToOuterIndex, int count)
    {
        int stepsInwardFromOuter = (count - 1) - innerToOuterIndex;

        return Mathf.Clamp01(
            outerFogOpacity - opacityStepTowardCenter * stepsInwardFromOuter);
    }

    /// <summary>
    /// Solves the alpha required by the next physical Tilemap sheet to reach a requested final
    /// composite opacity when it is stacked over the already-generated inner sheets.
    /// </summary>
    private float SolveSheetOpacity(float existingCompositeOpacity, float targetCompositeOpacity)
    {
        float existing = Mathf.Clamp01(existingCompositeOpacity);
        float target = Mathf.Clamp01(targetCompositeOpacity);

        if (target <= existing + 0.0001f)
            return 0f;

        if (existing >= 0.9999f)
            return 0f;

        return Mathf.Clamp01(
            1f - (1f - target) / (1f - existing));
    }

    private float CompositeOpacity(float belowOpacity, float topOpacity)
    {
        return 1f - (1f - Mathf.Clamp01(belowOpacity)) * (1f - Mathf.Clamp01(topOpacity));
    }

    private void InitialiseLayer(FogLayer layer, int totalLayerCount)
    {
        int overlayOrder = startingSortingOrder + totalLayerCount + layer.layerIndex;

        layer.fogTilemap = CreateTilemapLayer(
            $"{name} - {layer.layerName} Fog",
            layer.sortingOrder);

        layer.overlayTilemap = CreateTilemapLayer(
            $"{name} - {layer.layerName} Fog Overlay",
            overlayOrder);

        ApplyLayerOpacity(layer);

        BoundsInt bounds = worldBoundsTilemap.cellBounds;

        foreach (Vector3Int cell in bounds.allPositionsWithin)
        {
            FogCell data = new FogCell
            {
                state = FogState.Hidden,
                decayStepsRemaining = 0,
                displayLevel = 2,
                pendingTarget = 2,
                lockedCeiling = 2
            };

            layer.fogData[cell] = data;
            layer.fogTilemap.SetTile(cell, hiddenTile);
            layer.overlayTilemap.SetTile(cell, null);
        }

        foreach (LockedLocation location in lockedLocations)
            RevealLockedLocationOnLayer(layer, location);
    }

    private void DestroyGeneratedLayers()
    {
        foreach (FogLayer layer in layers)
        {
            if (layer.fogTilemap != null)
                Destroy(layer.fogTilemap.gameObject);

            if (layer.overlayTilemap != null)
                Destroy(layer.overlayTilemap.gameObject);
        }
    }

    private bool ValidateGridParent()
    {
        Grid managerGrid = GetComponentInParent<Grid>();

        if (managerGrid == null)
        {
            if (!missingGridErrorLogged)
            {
                Debug.LogError(
                    $"{name}: FogOfWar must be parented underneath a GameObject with a Grid component.",
                    this);
                missingGridErrorLogged = true;
            }

            return false;
        }

        missingGridErrorLogged = false;

        if (worldBoundsTilemap != null)
        {
            Grid boundsGrid = worldBoundsTilemap.GetComponentInParent<Grid>();

            if (boundsGrid != null && boundsGrid != managerGrid)
            {
                Debug.LogWarning(
                    $"{name}: worldBoundsTilemap belongs to a different Grid than this FogOfWar manager.",
                    this);
            }
        }

        return true;
    }

    private Tilemap CreateTilemapLayer(string objectName, int sortingOrder)
    {
        GameObject layerObject = new GameObject(objectName);
        layerObject.transform.SetParent(transform, worldPositionStays: false);
        layerObject.transform.localPosition = Vector3.zero;
        layerObject.transform.localRotation = Quaternion.identity;
        layerObject.transform.localScale = Vector3.one;

        Tilemap tilemap = layerObject.AddComponent<Tilemap>();
        TilemapRenderer renderer = layerObject.AddComponent<TilemapRenderer>();

        renderer.sortingLayerName = ResolveSortingLayerName(sortingLayerName);
        renderer.sortingOrder = sortingOrder;

        return tilemap;
    }

    private string ResolveSortingLayerName(string requestedName)
    {
        foreach (SortingLayer layer in SortingLayer.layers)
        {
            if (layer.name == requestedName)
                return requestedName;
        }

        Debug.LogWarning(
            $"{name}: Sorting Layer \"{requestedName}\" does not exist. Falling back to Default.",
            this);

        return "Default";
    }

    private void ApplyLayerOpacity(FogLayer layer)
    {
        Color tint = new Color(1f, 1f, 1f, Mathf.Clamp01(layer.sheetOpacity));

        if (layer.fogTilemap != null)
            layer.fogTilemap.color = tint;

        if (layer.overlayTilemap != null)
            layer.overlayTilemap.color = tint;
    }

    /// <summary>
    /// Recalculates every named/anonymous lock ceiling after a named lock is
    /// moved, upgraded, downgraded or removed.
    ///
    /// Normal FogState and decay timers are preserved. Only the permanent
    /// visibility ceiling is rebuilt.
    /// </summary>
    private void RebuildLockedLocationInfluence()
    {
        if (layers.Count == 0)
            return;

        foreach (FogLayer layer in layers)
        {
            foreach (KeyValuePair<Vector3Int, FogCell> pair in layer.fogData)
                pair.Value.lockedCeiling = 2;
        }

        foreach (LockedLocation location in lockedLocations)
        {
            if (location == null)
                continue;

            foreach (FogLayer layer in layers)
                ApplyLockedLocationCeilingOnLayer(
                    layer,
                    location
                );
        }

        foreach (FogLayer layer in layers)
        {
            foreach (KeyValuePair<Vector3Int, FogCell> pair in layer.fogData)
            {
                FogCell data =
                    pair.Value;

                int naturalTarget =
                    GetNaturalTargetForCell(
                        layer,
                        pair.Key,
                        data
                    );

                RequestLevel(
                    layer,
                    pair.Key,
                    data,
                    Mathf.Min(
                        naturalTarget,
                        data.lockedCeiling
                    )
                );
            }
        }
    }

    /// <summary>
    /// Returns the level this cell would currently want without any permanent
    /// lock applied.
    /// </summary>
    private int GetNaturalTargetForCell(
        FogLayer layer,
        Vector3Int cell,
        FogCell data)
    {
        switch (data.state)
        {
            case FogState.Hidden:
                return 2;

            case FogState.Remembered:
                return 1;

            case FogState.Visible:
            default:
                if (initialised)
                {
                    int liveLevel =
                        GetPlayerVisionLevelForLayer(
                            layer,
                            cell,
                            lastPlayerCell
                        );

                    if (liveLevel <= 1)
                        return liveLevel;
                }

                // A Visible state should normally be inside currentlyVisible.
                // Keep it fully visible rather than accidentally hiding it if
                // lock recalculation happens during a transient rebuild.
                return 0;
        }
    }

    private RadiusStackProfile GetLockedProfile(VisibilityTier tier)
    {
        return tier == VisibilityTier.Full
            ? fullLockedVision
            : partialLockedVision;
    }

    /// <summary>
    /// Applies one locked location to a layer and immediately requests the
    /// resulting visual level. Used when layers are first built and by the
    /// legacy anonymous AddLockedLocation API.
    /// </summary>
    private void RevealLockedLocationOnLayer(
        FogLayer layer,
        LockedLocation location)
    {
        List<Vector3Int> affectedCells =
            ApplyLockedLocationCeilingOnLayer(
                layer,
                location
            );

        foreach (Vector3Int cell in affectedCells)
        {
            if (!layer.fogData.TryGetValue(cell, out FogCell data))
                continue;

            int naturalTarget =
                GetNaturalTargetForCell(
                    layer,
                    cell,
                    data
                );

            RequestLevel(
                layer,
                cell,
                data,
                Mathf.Min(
                    naturalTarget,
                    data.lockedCeiling
                )
            );
        }
    }

    /// <summary>
    /// Applies only the permanent visibility ceiling and returns cells touched
    /// by this lock. It does not start animations itself.
    /// </summary>
    private List<Vector3Int> ApplyLockedLocationCeilingOnLayer(
        FogLayer layer,
        LockedLocation location)
    {
        List<Vector3Int> affectedCells =
            new List<Vector3Int>();

        RadiusStackProfile profile =
            GetLockedProfile(location.tier);

        int count =
            Mathf.Max(1, visionLayerCount);

        int radius =
            profile.GetRadius(
                layer.layerIndex,
                count
            );

        int edgeThickness =
            profile.GetEdgeThickness(
                layer.layerIndex,
                count,
                radius
            );

        Dictionary<Vector3Int, bool> lockedCells =
            GetVisibleCells(
                location.cell,
                radius,
                edgeThickness
            );

        foreach (KeyValuePair<Vector3Int, bool> pair in lockedCells)
        {
            if (!layer.fogData.TryGetValue(pair.Key, out FogCell data))
                continue;

            int desiredLevel =
                pair.Value
                    ? 1
                    : 0;

            data.lockedCeiling =
                Mathf.Min(
                    data.lockedCeiling,
                    desiredLevel
                );

            affectedCells.Add(pair.Key);
        }

        return affectedCells;
    }

    private void UpdatePlayerMotion()
    {
        if (player == null)
        {
            hasPreviousPlayerPosition = false;
            smoothedPlayerSpeed = 0f;
            return;
        }

        Vector3 currentPosition = player.position;

        if (!hasPreviousPlayerPosition)
        {
            previousPlayerPosition = currentPosition;
            hasPreviousPlayerPosition = true;
            smoothedPlayerSpeed = 0f;
            return;
        }

        float deltaTime = Mathf.Max(Time.unscaledDeltaTime, 0.0001f);

        float instantaneousSpeed =
            Vector3.Distance(previousPlayerPosition, currentPosition) / deltaTime;

        float smoothingAmount =
            1f - Mathf.Exp(-Mathf.Max(0f, speedSmoothing) * deltaTime);

        smoothedPlayerSpeed =
            Mathf.Lerp(smoothedPlayerSpeed, instantaneousSpeed, smoothingAmount);

        previousPlayerPosition = currentPosition;
    }

    private float GetPopDuration(FogLayer layer, Vector3Int cell)
    {
        float duration = popDuration;

        if (usePlayerSpeedForPop)
        {
            float speed01 = Mathf.Clamp01(
                smoothedPlayerSpeed / Mathf.Max(0.01f, speedForFastestPop));

            duration *= Mathf.Lerp(
                1f,
                fastestPopDurationMultiplier,
                speed01);
        }

        if (player != null &&
            worldBoundsTilemap != null &&
            positionDurationInfluence > 0f)
        {
            Vector3 cellWorldPosition =
                worldBoundsTilemap.GetCellCenterWorld(cell);

            float worldDistance = Vector2.Distance(
                new Vector2(player.position.x, player.position.y),
                new Vector2(cellWorldPosition.x, cellWorldPosition.y));

            Vector3 nextCellX =
                worldBoundsTilemap.GetCellCenterWorld(cell + Vector3Int.right);

            Vector3 nextCellY =
                worldBoundsTilemap.GetCellCenterWorld(cell + Vector3Int.up);

            float oneCellWorldSize = Mathf.Max(
                Vector3.Distance(cellWorldPosition, nextCellX),
                Vector3.Distance(cellWorldPosition, nextCellY),
                0.0001f);

            float visionWorldRadius = Mathf.Max(
                oneCellWorldSize,
                layer.visionRadius * oneCellWorldSize);

            float distance01 = Mathf.Clamp01(
                worldDistance / visionWorldRadius);

            float nearMultiplier = 1f - positionDurationInfluence;
            float farMultiplier = 1f + positionDurationInfluence;

            duration *= Mathf.Lerp(
                nearMultiplier,
                farMultiplier,
                distance01);
        }

        return Mathf.Clamp(
            duration,
            minimumPopDuration,
            maximumPopDuration);
    }

    private float EvaluatePopEasing(float time)
    {
        float t = Mathf.Clamp01(time);

        switch (popEasing)
        {
            case PopEasing.Linear:
                return t;

            case PopEasing.EaseInQuad:
                return t * t;

            case PopEasing.EaseOutQuad:
                return 1f - (1f - t) * (1f - t);

            case PopEasing.EaseInOutCubic:
                return t < 0.5f
                    ? 4f * t * t * t
                    : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;

            case PopEasing.SmoothStep:
                return t * t * (3f - 2f * t);

            case PopEasing.SmootherStep:
                return t * t * t * (t * (t * 6f - 15f) + 10f);

            case PopEasing.Custom:
            default:
                return popCurve != null
                    ? popCurve.Evaluate(t)
                    : t;
        }
    }

    public void UpdateVisibility(bool force)
    {
        if (worldBoundsTilemap == null || player == null || layers.Count == 0)
            return;

        Vector3Int playerCell =
            worldBoundsTilemap.WorldToCell(player.position);

        UpdateVisibilityAtCell(playerCell, force);
    }

    private void UpdateVisibilityAtCell(Vector3Int playerCell, bool force)
    {
        if (layers.Count == 0)
            return;

        if (!force && initialised && playerCell == lastPlayerCell)
            return;

        lastPlayerCell = playerCell;
        initialised = true;

        foreach (FogLayer layer in layers)
            UpdateLayerVisibility(layer, playerCell);

        PlayerVisionChanged?.Invoke(playerCell);
    }

    private void UpdateLayerVisibility(FogLayer layer, Vector3Int playerCell)
    {
        Dictionary<Vector3Int, bool> newlyVisible =
            GetVisibleCells(
                playerCell,
                layer.visionRadius,
                layer.edgeThickness);

        // Leaving ANY generated radius creates remembered fog for that same layer.
        // Because each radius has independent state, the inner/fainter fade now continues into
        // the trail behind the ship instead of existing only in the immediate current circle.
        foreach (Vector3Int cell in layer.currentlyVisible)
        {
            if (newlyVisible.ContainsKey(cell))
                continue;

            if (!layer.fogData.TryGetValue(cell, out FogCell data))
                continue;

            data.state = FogState.Remembered;
            data.decayStepsRemaining = decayStepsToHide;

            RequestLevel(
                layer,
                cell,
                data,
                Mathf.Min(1, data.lockedCeiling));
        }

        foreach (KeyValuePair<Vector3Int, bool> pair in newlyVisible)
        {
            if (!layer.fogData.TryGetValue(pair.Key, out FogCell data))
                continue;

            data.state = FogState.Visible;
            data.decayStepsRemaining = decayStepsToHide;

            int desiredLevel = pair.Value ? 1 : 0;

            RequestLevel(
                layer,
                pair.Key,
                data,
                Mathf.Min(desiredLevel, data.lockedCeiling));
        }

        layer.currentlyVisible.Clear();
        layer.currentlyVisible.UnionWith(newlyVisible.Keys);
    }

    public void AdvanceTurn()
    {
        foreach (FogLayer layer in layers)
        {
            foreach (KeyValuePair<Vector3Int, FogCell> pair in layer.fogData)
            {
                FogCell data = pair.Value;

                if (data.state != FogState.Remembered)
                    continue;

                data.decayStepsRemaining--;

                if (data.decayStepsRemaining > 0)
                    continue;

                data.state = FogState.Hidden;

                RequestLevel(
                    layer,
                    pair.Key,
                    data,
                    Mathf.Min(2, data.lockedCeiling));
            }
        }
    }

    private TileBase GetTileForLevel(int level)
    {
        if (level >= 2)
            return hiddenTile;

        if (level == 1)
            return halfHiddenTile;

        return null;
    }

    private void RequestLevel(
        FogLayer layer,
        Vector3Int cell,
        FogCell data,
        int targetLevel)
    {
        data.pendingTarget = Mathf.Clamp(targetLevel, 0, 2);

        if (data.displayLevel == data.pendingTarget)
            return;

        if (layer.popCoroutines.ContainsKey(cell))
            return;

        layer.popCoroutines[cell] =
            StartCoroutine(StepTowardTarget(layer, cell, data));
    }

    private IEnumerator StepTowardTarget(
        FogLayer layer,
        Vector3Int cell,
        FogCell data)
    {
        while (data.displayLevel != data.pendingTarget)
        {
            bool becomingMoreHidden =
                data.pendingTarget > data.displayLevel;

            int nextLevel = becomingMoreHidden
                ? data.displayLevel + 1
                : data.pendingTarget;

            if (becomingMoreHidden)
                yield return PopLevelIn(layer, cell, nextLevel);
            else
                yield return PopLevelOut(layer, cell, nextLevel);

            data.displayLevel = nextLevel;
        }

        layer.popCoroutines.Remove(cell);
    }

    private IEnumerator PopLevelIn(
        FogLayer layer,
        Vector3Int cell,
        int nextLevel)
    {
        TileBase newTile = GetTileForLevel(nextLevel);
        float animationDuration = GetPopDuration(layer, cell);

        if (layer.overlayTilemap != null)
        {
            layer.overlayTilemap.SetTile(cell, newTile);
            layer.overlayTilemap.SetTileFlags(cell, TileFlags.None);

            float elapsed = 0f;

            while (elapsed < animationDuration)
            {
                elapsed += Time.deltaTime;

                float eased =
                    EvaluatePopEasing(elapsed / animationDuration);

                layer.overlayTilemap.SetTransformMatrix(
                    cell,
                    Matrix4x4.Scale(Vector3.one * eased));

                yield return null;
            }

            layer.overlayTilemap.SetTile(cell, null);
            layer.overlayTilemap.SetTransformMatrix(cell, Matrix4x4.identity);
        }

        layer.fogTilemap.SetTile(cell, newTile);
        layer.fogTilemap.SetTileFlags(cell, TileFlags.None);
        layer.fogTilemap.SetTransformMatrix(cell, Matrix4x4.identity);
    }

    private IEnumerator PopLevelOut(
        FogLayer layer,
        Vector3Int cell,
        int nextLevel)
    {
        TileBase oldTile = layer.fogTilemap.GetTile(cell);

        layer.fogTilemap.SetTile(
            cell,
            GetTileForLevel(nextLevel));

        layer.fogTilemap.SetTileFlags(cell, TileFlags.None);

        float animationDuration = GetPopDuration(layer, cell);

        if (layer.overlayTilemap != null)
        {
            layer.overlayTilemap.SetTile(cell, oldTile);
            layer.overlayTilemap.SetTileFlags(cell, TileFlags.None);

            float elapsed = 0f;

            while (elapsed < animationDuration)
            {
                elapsed += Time.deltaTime;

                float eased =
                    EvaluatePopEasing(elapsed / animationDuration);

                float scale =
                    Mathf.Max(0f, 1f - eased);

                layer.overlayTilemap.SetTransformMatrix(
                    cell,
                    Matrix4x4.Scale(Vector3.one * scale));

                yield return null;
            }

            layer.overlayTilemap.SetTile(cell, null);
            layer.overlayTilemap.SetTransformMatrix(cell, Matrix4x4.identity);
        }
    }

    private Dictionary<Vector3Int, bool> GetVisibleCells(
        Vector3Int centre,
        int radius,
        int edgeThickness)
    {
        Dictionary<Vector3Int, bool> result =
            new Dictionary<Vector3Int, bool>();

        radius = Mathf.Max(0, radius);
        edgeThickness = Mathf.Clamp(edgeThickness, 0, radius);

        int radiusSqr = radius * radius;
        int coreRadius = Mathf.Max(0, radius - edgeThickness);
        int coreRadiusSqr = coreRadius * coreRadius;

        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int distanceSqr = dx * dx + dy * dy;

                if (distanceSqr > radiusSqr)
                    continue;

                bool isEdge =
                    distanceSqr > coreRadiusSqr;

                result[centre + new Vector3Int(dx, dy, 0)] = isEdge;
            }
        }

        return result;
    }
}
