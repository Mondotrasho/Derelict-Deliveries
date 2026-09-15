using System;
using UnityEngine;

/// <summary>
/// Small serialisable visibility record used by a Planet.
///
/// A future VisionManager can update these values without needing to know how
/// PlanetManager renders the planet.
///
/// currentlyVisible:
///     The object is inside the player's current LIVE vision.
///
/// discovered:
///     The player has discovered this object at least once.
///
/// rememberLocation:
///     If discovered, VisionManager should keep a remembered opening in FogOfWar
///     around this object's location.
///
/// These values DO NOT control whether PlanetManager renders the planet tile.
/// Planet tiles always exist underneath the fog.
/// </summary>
[Serializable]
public class PlanetVisibilityState
{
    [Tooltip("True while the planet is inside current player vision.")]
    public bool currentlyVisible = false;

    [Tooltip("True once the player has discovered this planet.")]
    public bool discovered = false;

    [Tooltip("If discovered, keep the planet graphic visible when it leaves current vision.")]
    public bool rememberLocation = false;

    /// <summary>
    /// Legacy/convenience state query.
    ///
    /// PlanetManager intentionally does not use this to render planets anymore.
    /// It can still be useful to UI or map-marker systems that care whether a
    /// discovered location should remain known.
    /// </summary>
    public bool ShouldRender
    {
        get
        {
            return currentlyVisible ||
                   (discovered && rememberLocation);
        }
    }

    public void SetCurrentlyVisible(bool visible)
    {
        // Live visibility and discovery are deliberately separate.
        //
        // A planet can be partially visible without being clearly discovered.
        // VisionManager decides when the live FogOfWar tier is strong enough
        // to count as an actual discovery.
        currentlyVisible = visible;
    }

    public void SetDiscovered(bool value)
    {
        discovered = value;
    }

    public void SetRememberLocation(bool value)
    {
        rememberLocation = value;
    }
}
