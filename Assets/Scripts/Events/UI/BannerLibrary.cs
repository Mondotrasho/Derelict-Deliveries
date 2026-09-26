using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Decides which ~3:1 banner the event UI (and later the planet picker) shows.
/// The dialogue UI never shows a banner.
///
/// Resolution order for an event, first hit wins:
///   1. the banner set on the EventDefinition itself (Event UI > Banner),
///      then the older Definition Overrides list here,
///   2. planet-category events: the planet's banner (by tag, see below),
///   3. the category default (Asteroid and Hazard share the asteroid banner),
///   4. none - the banner area collapses.
/// </summary>
[CreateAssetMenu(menuName = "Derelict Deliveries/UI/Banner Library", fileName = "BannerLibrary")]
public sealed class BannerLibrary : ScriptableObject
{
    [Serializable]
    public struct TagBanner
    {
        [Tooltip("Planet tag as set on the planet in PlanetManager > Planets > Event State > Tags, e.g. Green. Case-insensitive.")]
        public string tag;
        public Sprite banner;
    }

    [Serializable]
    public struct DefinitionBanner
    {
        public EventDefinition definition;
        public Sprite banner;
    }

    [Header("Category Defaults")]
    [Tooltip("Asteroid marks AND hazards (mining or impact use the same art).")]
    [SerializeField] private Sprite asteroid;
    [SerializeField] private Sprite derelict;

    [Header("Planets")]
    [Tooltip("Ordered: the first tag the planet has wins, so put the most specific tags first.")]
    [SerializeField] private List<TagBanner> planetTags = new List<TagBanner>();
    [Tooltip("Used for planets with no matching tag.")]
    [SerializeField] private Sprite planetFallback;

    [Header("Per-Event Overrides (older way: prefer the Banner field on the EventDefinition)")]
    [SerializeField] private List<DefinitionBanner> definitionOverrides = new List<DefinitionBanner>();


    public Sprite Resolve(EventDefinition definition, Planet planet)
    {
        if (definition == null) return null;
        if (definition.Banner != null) return definition.Banner;

        foreach (DefinitionBanner entry in definitionOverrides)
        {
            if (entry.definition == definition && entry.banner != null) return entry.banner;
        }

        switch (definition.Category)
        {
            case EventCategory.Asteroid:
            case EventCategory.Hazard:
                return asteroid;
            case EventCategory.Derelict:
                return derelict;
            case EventCategory.Planet:
                return ResolvePlanet(planet);
            default:
                return null;
        }
    }


    public Sprite ResolvePlanet(Planet planet)
    {
        if (planet != null && planet.eventState != null)
        {
            foreach (TagBanner entry in planetTags)
            {
                if (entry.banner == null || string.IsNullOrWhiteSpace(entry.tag)) continue;
                if (PlanetHasTag(planet, entry.tag)) return entry.banner;
            }
        }

        return planetFallback;
    }


    /// <summary>
    /// Case- and space-insensitive: PlanetEventState.HasTag is an exact match,
    /// so "Green", "green" and "Green " all count as the same tag here.
    /// </summary>
    private static bool PlanetHasTag(Planet planet, string tag)
    {
        string wanted = tag.Trim();
        foreach (string t in planet.eventState.Tags)
        {
            if (!string.IsNullOrWhiteSpace(t) &&
                string.Equals(t.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
