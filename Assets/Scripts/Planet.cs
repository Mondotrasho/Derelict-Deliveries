using System;
using UnityEngine;

/// <summary>
/// Serialisable description of one planet placed by PlanetManager.
///
/// This is data rather than a MonoBehaviour. PlanetManager owns the shared
/// Tilemap and applies each planet's tile, visibility and animation.
/// </summary>
[Serializable]
public class Planet
{
    [Tooltip("Stable identifier used by code and future quest/vision systems.")]
    public string id = "Planet";

    [Tooltip("Grid cell occupied by this planet.")]
    public Vector3Int cell = Vector3Int.zero;

    [Tooltip("Index into PlanetManager's Planet Tile Library. The custom Inspector shows this as a dropdown.")]
    [Min(0)]
    public int tileIndex = 0;

    [Header("Visibility")]
    public PlanetVisibilityState visibility =
        new PlanetVisibilityState();

    [Tooltip("If enabled, discovering/remembering this planet may create a persistent FogOfWar locked location. " +
             "Disable this for moons, satellites, decorative bodies, or anything that should never open its own hole in the fog.")]
    public bool revealFogWhenDiscovered = true;

    [Header("Animation")]
    [Tooltip("Allow this planet to use the manager's wiggle/skew animation.")]
    public bool animate = true;

    [Tooltip("Per-planet multiplier for the shared animation amounts.")]
    [Range(0f, 3f)]
    public float animationStrength = 1f;

    [Header("Quest / Event Data")]
    public PlanetEventState eventState =
        new PlanetEventState();

    // Runtime-only deterministic animation phases.
    [NonSerialized] public float phaseX;
    [NonSerialized] public float phaseY;
    [NonSerialized] public float phaseSkewX;
    [NonSerialized] public float phaseSkewY;
    [NonSerialized] public float phaseRotation;
    [NonSerialized] public bool animationStateInitialised;
}
