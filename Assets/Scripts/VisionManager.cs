using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Coordinates live player vision, FogOfWar and visibility-aware world objects.
///
/// Version 2 visual rule:
/// world objects such as planets remain rendered underneath FogOfWar.
/// VisionManager updates discovery state and fog openings, not planet rendering.
///
/// Version 1 currently integrates PlanetManager, but the important ownership
/// boundary is already established:
///
/// Player movement / FogOfWar
///             |
///             v
///        VisionManager
///             |
///             +---- PlanetManager
///             |
///             `---- future POI/object managers
///
/// PlanetManager never edits fog directly.
/// FogOfWar never knows what a planet is.
/// VisionManager owns the relationship between the two.
///
/// Attach this component to the same GameObject as PlayerGridController.
/// </summary>
[RequireComponent(typeof(PlayerGridController))]
public class VisionManager : MonoBehaviour
{
    [Header("Player Vision Source")]
    [Tooltip("Player grid controller. Normally this is on the same GameObject as VisionManager.")]
    [SerializeField]
    private PlayerGridController playerController;

    [Tooltip("Transform whose actual world position drives object visibility. Leave empty to use PlayerGridController.transform.")]
    [SerializeField]
    private Transform visionSource;

    [Header("Managers")]
    [Tooltip("Fog manager used for live player-vision queries and remembered-location locks.")]
    [SerializeField]
    private FogOfWar fogManager;

    [Tooltip("Planet manager whose planets are evaluated against live player vision.")]
    [SerializeField]
    private PlanetManager planetManager;

    [Header("Discovery")]
    [Tooltip("Minimum LIVE FogOfWar tier required for a planet to count as currently visible. " +
             "Partial lets planets appear in the soft outer vision region.")]
    [SerializeField]
    private FogOfWar.VisibilityTier minimumPlanetVisibility =
        FogOfWar.VisibilityTier.Partial;

    [Tooltip("When a planet becomes visible at Partial or Full tier, automatically remember its location " +
             "and create/update a named FogOfWar locked location at that exact observed tier.")]
    [SerializeField]
    private bool autoRememberDiscoveredPlanets = true;

    [Tooltip("At startup, import PlanetManager planets that are already marked Currently Visible as known locations. " +
             "This supports planets intentionally configured as visible before VisionManager performs its first live-vision pass.")]
    [SerializeField]
    private bool importStartingVisiblePlanets = true;

    [Tooltip("Fallback remembered tier for a planet that starts already known/remembered, " +
             "but has no live observation in this session.")]
    [SerializeField]
    private FogOfWar.VisibilityTier preDiscoveredRememberedTier =
        FogOfWar.VisibilityTier.Partial;

    [Header("Runtime Synchronisation")]
    [Tooltip("Also watch planet Inspector/data changes during Play mode. Live movement updates are event-driven either way.")]
    [SerializeField]
    private bool detectPlanetDataChanges = true;

    [Tooltip("Prefix used for named FogOfWar locked locations owned by this manager.")]
    [SerializeField]
    private string planetLockPrefix = "vision:planet:";

    [Header("Runtime Debug")]
    [Tooltip("Actual FogOfWar grid cell currently being used as the player/observer position.")]
    [SerializeField]
    private Vector3Int evaluatedPlayerCell;

    [Tooltip("How many planets are currently inside the configured live visibility threshold.")]
    [SerializeField]
    private int currentlyVisiblePlanetCount;

    [Tooltip("How many planets are currently marked Discovered.")]
    [SerializeField]
    private int discoveredPlanetCount;

    [Tooltip("How many planet-owned named fog locks are currently managed by this VisionManager.")]
    [SerializeField]
    private int managedPlanetLockCount;

    [Tooltip("Number of complete vision evaluations performed since Play began.")]
    [SerializeField]
    private int visionRefreshCount;

    private bool hasEvaluatedPlayerCell;
    private bool startupPlanetStateImported;

    // Strongest live tier observed for each planet during this session.
    // This lets a planet first found at Partial later upgrade its remembered
    // fog area to Full if the ship gets closer.
    private readonly Dictionary<string, FogOfWar.VisibilityTier>
        strongestObservedPlanetTier =
            new Dictionary<string, FogOfWar.VisibilityTier>();

    // IDs of fog locks that this VisionManager currently owns.
    // On every refresh, locks no longer represented by a remembered planet are
    // removed so deleted/renamed/unremembered planets do not leave stale fog.
    private readonly HashSet<string>
        managedPlanetLockIds =
            new HashSet<string>();

    private readonly HashSet<string>
        duplicateIdWarnings =
            new HashSet<string>();

    private int lastPlanetDataHash =
        int.MinValue;

    /// <summary>
    /// Raised once when a planet changes from not-discovered to discovered.
    /// Quest/event systems can subscribe later without PlanetManager owning
    /// any quest logic.
    /// </summary>
    public event Action<Planet, FogOfWar.VisibilityTier>
        PlanetDiscovered;

    /// <summary>
    /// Raised when another system explicitly gives/reveals a planet location.
    /// This is separate from physically seeing the planet through live player vision.
    /// </summary>
    public event Action<Planet, FogOfWar.VisibilityTier>
        PlanetLocationGranted;

    /// <summary>
    /// Raised when a planet enters or leaves live player vision.
    /// </summary>
    public event Action<Planet, bool>
        PlanetCurrentVisibilityChanged;


    private void Awake()
    {
        ResolveMissingReferences();
        ResolveVisionSource();
    }


    private void OnEnable()
    {
        ResolveMissingReferences();
        ResolveVisionSource();

        if (fogManager != null)
        {
            fogManager.PlayerVisionChanged +=
                HandlePlayerVisionChanged;
        }
    }


    private void Start()
    {
        ImportStartingPlanetState();
        RefreshVisionFromPlayerPosition(force: true);
    }


    private void OnDisable()
    {
        if (fogManager != null)
        {
            fogManager.PlayerVisionChanged -=
                HandlePlayerVisionChanged;
        }
    }


    private void LateUpdate()
    {
        // Primary movement path:
        // read the Player Transform itself, convert it through FogOfWar's grid,
        // and refresh as soon as the ship crosses into another grid cell.
        RefreshVisionFromPlayerPosition(force: false);

        if (!detectPlanetDataChanges ||
            planetManager == null)
        {
            return;
        }

        int currentHash =
            CalculatePlanetDataHash();

        if (currentHash == lastPlanetDataHash)
            return;

        // Planet data changed without player movement, so re-evaluate using the
        // same actual player cell.
        RefreshVisionFromPlayerPosition(force: true);
    }


    private void OnValidate()
    {
        if (minimumPlanetVisibility ==
            FogOfWar.VisibilityTier.Hidden)
        {
            minimumPlanetVisibility =
                FogOfWar.VisibilityTier.Partial;
        }

        if (preDiscoveredRememberedTier ==
            FogOfWar.VisibilityTier.Hidden)
        {
            preDiscoveredRememberedTier =
                FogOfWar.VisibilityTier.Partial;
        }

        if (string.IsNullOrWhiteSpace(planetLockPrefix))
            planetLockPrefix = "vision:planet:";
    }


    /// <summary>
    /// Finds the common scene managers when a reference was left empty.
    ///
    /// Explicit Inspector references still win, which is important if a future
    /// scene contains more than one fog/planet manager.
    /// </summary>
    private void ResolveMissingReferences()
    {
        if (fogManager == null)
            fogManager = FindFirstObjectByType<FogOfWar>();

        if (planetManager == null)
            planetManager = FindFirstObjectByType<PlanetManager>();
    }


    private void ResolveVisionSource()
    {
        if (playerController == null)
            playerController = GetComponent<PlayerGridController>();

        if (visionSource == null &&
            playerController != null)
        {
            visionSource = playerController.transform;
        }

        if (visionSource == null)
            visionSource = transform;
    }


    private void HandlePlayerVisionChanged(
        Vector3Int playerCell)
    {
        // Secondary/event-driven path. FogOfWar already knows the exact cell it
        // just used, so use that directly rather than recalculating it.
        RefreshVisionAtCell(
            playerCell,
            force: true
        );
    }


    /// <summary>
    /// Imports PlanetManager state that intentionally existed before the first
    /// live-player visibility evaluation.
    ///
    /// This lets preconfigured visible/discovered planets become actual
    /// VisionManager-owned remembered fog locations at startup.
    /// </summary>
    private void ImportStartingPlanetState()
    {
        if (startupPlanetStateImported)
            return;

        startupPlanetStateImported = true;

        ResolveMissingReferences();

        if (planetManager == null ||
            fogManager == null)
        {
            return;
        }

        IReadOnlyList<Planet> planets =
            planetManager.Planets;

        for (int i = 0; i < planets.Count; i++)
        {
            Planet planet =
                planets[i];

            if (planet == null)
                continue;

            EnsurePlanetVisibilityData(planet);

            // Capture the serialized/startup value before the first live pass
            // overwrites Currently Visible from the player's real position.
            bool configuredStartingVisible =
                planet.visibility.currentlyVisible;

            if (importStartingVisiblePlanets &&
                configuredStartingVisible)
            {
                planet.visibility.discovered = true;

                if (autoRememberDiscoveredPlanets)
                {
                    planet.visibility.rememberLocation = true;
                }
            }

            if (!planet.visibility.discovered ||
                !planet.visibility.rememberLocation ||
                !planet.revealFogWhenDiscovered ||
                string.IsNullOrWhiteSpace(planet.id))
            {
                continue;
            }

            string lockId =
                planetLockPrefix +
                planet.id;

            FogOfWar.VisibilityTier rememberedTier =
                preDiscoveredRememberedTier;

            if (rememberedTier ==
                FogOfWar.VisibilityTier.Hidden)
            {
                rememberedTier =
                    FogOfWar.VisibilityTier.Partial;
            }

            fogManager.SetLockedLocation(
                lockId,
                planet.cell,
                rememberedTier
            );

            managedPlanetLockIds.Add(
                lockId
            );

            strongestObservedPlanetTier[lockId] =
                rememberedTier;
        }

        managedPlanetLockCount =
            managedPlanetLockIds.Count;
    }


    /// <summary>
    /// Read-only runtime snapshot for Inspector/debug tooling.
    /// </summary>
    public struct PlanetVisionInfo
    {
        public string Id { get; }
        public Vector3Int Cell { get; }
        public FogOfWar.VisibilityTier LiveTier { get; }
        public bool CurrentlyVisible { get; }
        public bool Discovered { get; }
        public bool RememberLocation { get; }
        public bool RevealFogWhenDiscovered { get; }
        public bool HasFogLock { get; }
        public FogOfWar.VisibilityTier FogLockTier { get; }

        public PlanetVisionInfo(
            string id,
            Vector3Int cell,
            FogOfWar.VisibilityTier liveTier,
            bool currentlyVisible,
            bool discovered,
            bool rememberLocation,
            bool revealFogWhenDiscovered,
            bool hasFogLock,
            FogOfWar.VisibilityTier fogLockTier)
        {
            Id = id;
            Cell = cell;
            LiveTier = liveTier;
            CurrentlyVisible = currentlyVisible;
            Discovered = discovered;
            RememberLocation = rememberLocation;
            RevealFogWhenDiscovered = revealFogWhenDiscovered;
            HasFogLock = hasFogLock;
            FogLockTier = fogLockTier;
        }
    }


    public int CurrentlyVisiblePlanetCount
    {
        get { return currentlyVisiblePlanetCount; }
    }

    public int DiscoveredPlanetCount
    {
        get { return discoveredPlanetCount; }
    }

    public int ManagedPlanetLockCount
    {
        get { return managedPlanetLockCount; }
    }


    /// <summary>
    /// Returns a read-only snapshot of each planet's current live visibility
    /// and fog-lock ownership.
    /// </summary>
    public List<PlanetVisionInfo> GetPlanetVisionSnapshot()
    {
        List<PlanetVisionInfo> snapshot =
            new List<PlanetVisionInfo>();

        ResolveMissingReferences();
        ResolveVisionSource();

        if (planetManager == null ||
            fogManager == null)
        {
            return snapshot;
        }

        Vector3Int playerCell =
            hasEvaluatedPlayerCell
                ? evaluatedPlayerCell
                : fogManager.WorldToCell(
                    visionSource != null
                        ? visionSource.position
                        : transform.position
                );

        IReadOnlyList<Planet> planets =
            planetManager.Planets;

        for (int i = 0; i < planets.Count; i++)
        {
            Planet planet =
                planets[i];

            if (planet == null)
                continue;

            EnsurePlanetVisibilityData(planet);

            FogOfWar.VisibilityTier liveTier =
                fogManager.GetPlayerVisibility(
                    planet.cell,
                    playerCell
                );

            string lockId =
                string.IsNullOrWhiteSpace(planet.id)
                    ? null
                    : planetLockPrefix + planet.id;

            bool hasFogLock = false;
            FogOfWar.VisibilityTier fogLockTier =
                FogOfWar.VisibilityTier.Hidden;

            if (!string.IsNullOrEmpty(lockId))
            {
                hasFogLock =
                    fogManager.TryGetLockedLocation(
                        lockId,
                        out Vector3Int lockCell,
                        out fogLockTier
                    );
            }

            snapshot.Add(
                new PlanetVisionInfo(
                    planet.id,
                    planet.cell,
                    liveTier,
                    planet.visibility.currentlyVisible,
                    planet.visibility.discovered,
                    planet.visibility.rememberLocation,
                    planet.revealFogWhenDiscovered,
                    hasFogLock,
                    fogLockTier
                )
            );
        }

        return snapshot;
    }


    // =====================================================================
    // Public API
    // =====================================================================

    /// <summary>
    /// Re-evaluates all currently-supported visibility targets.
    ///
    /// Version 1 evaluates planets. Future object/POI managers can be added to
    /// this method without changing FogOfWar or PlanetManager ownership.
    /// </summary>
    [ContextMenu("Refresh Vision Now")]
    public void RefreshVision()
    {
        RefreshVisionFromPlayerPosition(
            force: true
        );
    }


    /// <summary>
    /// Reads the actual player/vision-source Transform, converts that world
    /// position through FogOfWar's own Tilemap, and refreshes when the cell changes.
    /// </summary>
    private void RefreshVisionFromPlayerPosition(
        bool force)
    {
        ResolveMissingReferences();
        ResolveVisionSource();

        if (fogManager == null ||
            planetManager == null ||
            visionSource == null)
        {
            return;
        }

        Vector3Int actualPlayerCell =
            fogManager.WorldToCell(
                visionSource.position
            );

        RefreshVisionAtCell(
            actualPlayerCell,
            force
        );
    }


    /// <summary>
    /// Performs one complete visibility pass using an explicit player cell.
    /// </summary>
    private void RefreshVisionAtCell(
        Vector3Int playerCell,
        bool force)
    {
        ResolveMissingReferences();

        if (fogManager == null ||
            planetManager == null)
        {
            return;
        }

        if (!force &&
            hasEvaluatedPlayerCell &&
            playerCell == evaluatedPlayerCell)
        {
            return;
        }

        evaluatedPlayerCell =
            playerCell;

        hasEvaluatedPlayerCell =
            true;

        visionRefreshCount++;

        RefreshPlanetVision(
            playerCell
        );

        lastPlanetDataHash =
            CalculatePlanetDataHash();
    }


    /// <summary>
    /// Convenience query for future non-planet visibility targets.
    /// This deliberately returns LIVE player vision only.
    /// </summary>
    public FogOfWar.VisibilityTier GetLiveVisibility(
        Vector3Int cell)
    {
        ResolveMissingReferences();
        ResolveVisionSource();

        if (fogManager == null ||
            visionSource == null)
        {
            return FogOfWar.VisibilityTier.Hidden;
        }

        Vector3Int playerCell =
            fogManager.WorldToCell(
                visionSource.position
            );

        return fogManager.GetPlayerVisibility(
            cell,
            playerCell
        );
    }


    /// <summary>
    /// Reveals a known planet location without pretending the player can
    /// currently see it.
    ///
    /// Typical use:
    ///     quest reward
    ///     NPC gives coordinates
    ///     map/intel purchase
    ///     scripted discovery
    ///
    /// The planet becomes Discovered + Remember Location and receives a named
    /// FogOfWar locked location using the requested Partial or Full tier.
    ///
    /// Currently Visible is NOT forced true. That value remains controlled by
    /// the player's actual position and live FogOfWar radius.
    /// </summary>
    public bool GrantPlanetLocation(
        string planetId,
        FogOfWar.VisibilityTier rememberedTier =
            FogOfWar.VisibilityTier.Partial)
    {
        ResolveMissingReferences();

        if (fogManager == null ||
            planetManager == null)
        {
            return false;
        }

        Planet planet =
            planetManager.FindPlanet(
                planetId
            );

        if (planet == null)
        {
            Debug.LogWarning(
                $"{name}: GrantPlanetLocation could not find planet \"{planetId}\".",
                this);

            return false;
        }

        return GrantPlanetLocation(
            planet,
            rememberedTier
        );
    }


    /// <summary>
    /// Direct Planet overload of GrantPlanetLocation().
    /// </summary>
    public bool GrantPlanetLocation(
        Planet planet,
        FogOfWar.VisibilityTier rememberedTier =
            FogOfWar.VisibilityTier.Partial)
    {
        ResolveMissingReferences();

        if (fogManager == null ||
            planetManager == null ||
            planet == null)
        {
            return false;
        }

        EnsurePlanetVisibilityData(
            planet
        );

        if (rememberedTier ==
            FogOfWar.VisibilityTier.Hidden)
        {
            rememberedTier =
                FogOfWar.VisibilityTier.Partial;
        }

        bool wasDiscovered =
            planet.visibility.discovered;

        planet.visibility.discovered =
            true;

        planet.visibility.rememberLocation =
            true;

        if (string.IsNullOrWhiteSpace(planet.id))
        {
            Debug.LogWarning(
                $"{name}: planet at {planet.cell} has no stable ID, so its location cannot own a named fog lock.",
                this);

            return false;
        }

        if (!planet.revealFogWhenDiscovered)
        {
            if (!wasDiscovered)
            {
                PlanetDiscovered?.Invoke(
                    planet,
                    rememberedTier
                );
            }

            PlanetLocationGranted?.Invoke(
                planet,
                rememberedTier
            );

            lastPlanetDataHash =
                CalculatePlanetDataHash();

            return true;
        }

        string lockId =
            planetLockPrefix +
            planet.id;

        // Store this as the strongest known tier so later automatic refreshes
        // cannot downgrade an externally-granted Full location back to Partial.
        if (!strongestObservedPlanetTier.TryGetValue(
            lockId,
            out FogOfWar.VisibilityTier previousTier) ||
            (int)rememberedTier > (int)previousTier)
        {
            strongestObservedPlanetTier[lockId] =
                rememberedTier;
        }

        fogManager.SetLockedLocation(
            lockId,
            planet.cell,
            rememberedTier
        );

        managedPlanetLockIds.Add(
            lockId
        );

        if (!wasDiscovered)
        {
            PlanetDiscovered?.Invoke(
                planet,
                rememberedTier
            );
        }

        PlanetLocationGranted?.Invoke(
            planet,
            rememberedTier
        );

        lastPlanetDataHash =
            CalculatePlanetDataHash();

        return true;
    }


    /// <summary>
    /// Removes the remembered map/fog opening for a planet.
    ///
    /// By default the planet remains Discovered, but Remember Location becomes
    /// false and its named FogOfWar lock is removed.
    ///
    /// Set alsoForgetDiscovery = true if the caller wants to clear the
    /// Discovered flag as well.
    /// </summary>
    public bool RemoveGrantedPlanetLocation(
        string planetId,
        bool alsoForgetDiscovery = false)
    {
        ResolveMissingReferences();

        if (fogManager == null ||
            planetManager == null)
        {
            return false;
        }

        Planet planet =
            planetManager.FindPlanet(
                planetId
            );

        if (planet == null)
            return false;

        EnsurePlanetVisibilityData(
            planet
        );

        planet.visibility.rememberLocation =
            false;

        if (alsoForgetDiscovery)
        {
            planet.visibility.discovered =
                false;
        }

        if (!string.IsNullOrWhiteSpace(planet.id))
        {
            string lockId =
                planetLockPrefix +
                planet.id;

            fogManager.RemoveLockedLocation(
                lockId
            );

            managedPlanetLockIds.Remove(
                lockId
            );

            strongestObservedPlanetTier.Remove(
                lockId
            );
        }

        lastPlanetDataHash =
            CalculatePlanetDataHash();

        return true;
    }


    /// <summary>
    /// Convenience alias for quest/dialogue/map code that reads more naturally.
    /// </summary>
    public bool ShowPlanetOnMap(
        string planetId,
        FogOfWar.VisibilityTier rememberedTier =
            FogOfWar.VisibilityTier.Partial)
    {
        return GrantPlanetLocation(
            planetId,
            rememberedTier
        );
    }


    // =====================================================================
    // Planet integration
    // =====================================================================

    private void RefreshPlanetVision(
        Vector3Int playerCell)
    {
        IReadOnlyList<Planet> planets =
            planetManager.Planets;

        HashSet<string> desiredLockIds =
            new HashSet<string>();

        HashSet<string> seenPlanetIds =
            new HashSet<string>();

        currentlyVisiblePlanetCount = 0;
        discoveredPlanetCount = 0;

        for (int i = 0; i < planets.Count; i++)
        {
            Planet planet =
                planets[i];

            if (planet == null)
                continue;

            EnsurePlanetVisibilityData(planet);

            FogOfWar.VisibilityTier liveTier =
                fogManager.GetPlayerVisibility(
                    planet.cell,
                    playerCell
                );

            bool currentlyVisible =
                IsAtLeastVisibility(
                    liveTier,
                    minimumPlanetVisibility
                );

            // If the planet is inside the configured live visibility threshold,
            // it is discoverable at THAT exact tier.
            //
            // With Minimum Planet Visibility = Partial:
            //     Partial -> discovered/remembered as Partial
            //     Full    -> upgrades the same remembered lock to Full
            bool discoverable =
                currentlyVisible;

            bool wasCurrentlyVisible =
                planet.visibility.currentlyVisible;

            bool wasDiscovered =
                planet.visibility.discovered;

            if (currentlyVisible)
            {
                currentlyVisiblePlanetCount++;

                RememberStrongestObservedTier(
                    planet,
                    liveTier
                );
            }

            // Current visibility is live state only.
            planetManager.SetPlanetCurrentlyVisible(
                planet,
                currentlyVisible,
                refreshRuntimePlanets: false
            );

            // Discovery uses the same threshold as live visibility.
            //
            // This is important because the remembered FogOfWar tier is taken
            // from the strongest live tier actually observed:
            //
            //     first seen at Partial -> Partial locked location
            //     later seen at Full    -> same named lock upgrades to Full
            //
            // The Fog Manager's Full Visibility Radius controls where that
            // Partial -> Full upgrade happens.
            if (discoverable)
            {
                planet.visibility.discovered = true;

                if (autoRememberDiscoveredPlanets)
                {
                    planet.visibility.rememberLocation = true;
                }
            }

            if (wasCurrentlyVisible !=
                planet.visibility.currentlyVisible)
            {
                PlanetCurrentVisibilityChanged?.Invoke(
                    planet,
                    currentlyVisible
                );
            }

            if (!wasDiscovered &&
                planet.visibility.discovered)
            {
                PlanetDiscovered?.Invoke(
                    planet,
                    liveTier
                );
            }

            if (planet.visibility.discovered)
            {
                discoveredPlanetCount++;
            }

            // A remembered planet owns exactly one named FogOfWar lock.
            if (planet.visibility.discovered &&
                planet.visibility.rememberLocation &&
                planet.revealFogWhenDiscovered)
            {
                if (TryBuildPlanetLockId(
                    planet,
                    i,
                    seenPlanetIds,
                    out string lockId))
                {
                    desiredLockIds.Add(lockId);

                    FogOfWar.VisibilityTier rememberedTier =
                        GetRememberedTier(
                            planet,
                            lockId,
                            liveTier
                        );

                    fogManager.SetLockedLocation(
                        lockId,
                        planet.cell,
                        rememberedTier
                    );

                    managedPlanetLockIds.Add(
                        lockId
                    );
                }
            }
        }

        // Remove only locks owned by this VisionManager. Anonymous locks and
        // other systems' named locks are never touched.
        List<string> staleLockIds =
            new List<string>();

        foreach (string managedId in managedPlanetLockIds)
        {
            if (!desiredLockIds.Contains(managedId))
                staleLockIds.Add(managedId);
        }

        foreach (string staleId in staleLockIds)
        {
            fogManager.RemoveLockedLocation(
                staleId
            );

            managedPlanetLockIds.Remove(
                staleId
            );

            strongestObservedPlanetTier.Remove(
                staleId
            );
        }

        managedPlanetLockCount =
            managedPlanetLockIds.Count;

        // No PlanetManager rendering refresh is required here.
        //
        // Planets are permanently present on their runtime Tilemap underneath FogOfWar.
        // Live visibility affects state only, while remembered visibility is represented
        // by the named FogOfWar locks managed above.
    }


    private void EnsurePlanetVisibilityData(
        Planet planet)
    {
        if (planet.visibility == null)
        {
            planet.visibility =
                new PlanetVisibilityState();
        }
    }


    private bool IsAtLeastVisibility(
        FogOfWar.VisibilityTier actual,
        FogOfWar.VisibilityTier minimum)
    {
        return (int)actual >= (int)minimum;
    }


    private void RememberStrongestObservedTier(
        Planet planet,
        FogOfWar.VisibilityTier liveTier)
    {
        if (liveTier == FogOfWar.VisibilityTier.Hidden)
            return;

        string observationKey =
            BuildPlanetObservationKey(planet);

        if (string.IsNullOrEmpty(observationKey))
            return;

        if (!strongestObservedPlanetTier.TryGetValue(
            observationKey,
            out FogOfWar.VisibilityTier previous))
        {
            strongestObservedPlanetTier[observationKey] =
                liveTier;
            return;
        }

        if ((int)liveTier > (int)previous)
        {
            strongestObservedPlanetTier[observationKey] =
                liveTier;
        }
    }


    /// <summary>
    /// Chooses the strongest sensible remembered tier:
    /// 1. strongest live observation this session
    /// 2. an existing named fog lock, if stronger
    /// 3. configured fallback for pre-discovered planets
    /// </summary>
    private FogOfWar.VisibilityTier GetRememberedTier(
        Planet planet,
        string lockId,
        FogOfWar.VisibilityTier currentLiveTier)
    {
        FogOfWar.VisibilityTier bestTier =
            preDiscoveredRememberedTier;

        string observationKey =
            BuildPlanetObservationKey(planet);

        if (!string.IsNullOrEmpty(observationKey) &&
            strongestObservedPlanetTier.TryGetValue(
                observationKey,
                out FogOfWar.VisibilityTier observedTier))
        {
            bestTier =
                StrongerTier(
                    bestTier,
                    observedTier
                );
        }

        if (currentLiveTier !=
            FogOfWar.VisibilityTier.Hidden)
        {
            bestTier =
                StrongerTier(
                    bestTier,
                    currentLiveTier
                );
        }

        if (fogManager.TryGetLockedLocation(
            lockId,
            out Vector3Int existingCell,
            out FogOfWar.VisibilityTier existingTier))
        {
            bestTier =
                StrongerTier(
                    bestTier,
                    existingTier
                );
        }

        if (bestTier == FogOfWar.VisibilityTier.Hidden)
            bestTier = FogOfWar.VisibilityTier.Partial;

        return bestTier;
    }


    private FogOfWar.VisibilityTier StrongerTier(
        FogOfWar.VisibilityTier first,
        FogOfWar.VisibilityTier second)
    {
        return (int)second > (int)first
            ? second
            : first;
    }


    private bool TryBuildPlanetLockId(
        Planet planet,
        int planetIndex,
        HashSet<string> seenPlanetIds,
        out string lockId)
    {
        lockId = null;

        if (string.IsNullOrWhiteSpace(planet.id))
        {
            string warningKey =
                $"blank:{planetIndex}";

            if (duplicateIdWarnings.Add(warningKey))
            {
                Debug.LogWarning(
                    $"{name}: planet at index {planetIndex} has no ID. " +
                    "Its live visibility still works, but it cannot own a stable remembered fog location.",
                    this);
            }

            return false;
        }

        if (!seenPlanetIds.Add(planet.id))
        {
            string warningKey =
                $"duplicate:{planet.id}";

            if (duplicateIdWarnings.Add(warningKey))
            {
                Debug.LogWarning(
                    $"{name}: more than one planet uses ID \"{planet.id}\". " +
                    "Planet IDs must be unique before remembered fog locks can be managed safely.",
                    this);
            }

            return false;
        }

        lockId =
            planetLockPrefix +
            planet.id;

        return true;
    }


    private string BuildPlanetObservationKey(
        Planet planet)
    {
        if (planet == null ||
            string.IsNullOrWhiteSpace(planet.id))
        {
            return null;
        }

        return planetLockPrefix +
               planet.id;
    }


    // =====================================================================
    // Runtime Inspector/data change detection
    // =====================================================================

    /// <summary>
    /// Watches only data that changes vision ownership/results.
    ///
    /// This is not the movement update path. Player movement remains driven by
    /// FogOfWar.PlayerVisionChanged.
    /// </summary>
    private int CalculatePlanetDataHash()
    {
        if (planetManager == null)
            return 0;

        unchecked
        {
            int hash = 17;

            IReadOnlyList<Planet> planets =
                planetManager.Planets;

            hash =
                hash * 31 +
                planets.Count;

            for (int i = 0; i < planets.Count; i++)
            {
                Planet planet =
                    planets[i];

                if (planet == null)
                {
                    hash *= 31;
                    continue;
                }

                hash =
                    hash * 31 +
                    (planet.id != null
                        ? planet.id.GetHashCode()
                        : 0);

                hash =
                    hash * 31 +
                    planet.cell.GetHashCode();

                hash =
                    hash * 31 +
                    planet.revealFogWhenDiscovered.GetHashCode();

                if (planet.visibility != null)
                {
                    hash =
                        hash * 31 +
                        planet.visibility.discovered.GetHashCode();

                    hash =
                        hash * 31 +
                        planet.visibility.rememberLocation.GetHashCode();
                }
            }

            return hash;
        }
    }
}
