using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Decides which ~3:1 banner the event UI (and later the planet picker) shows.
/// The dialogue UI never shows a banner.
///
/// Resolution order for an event, first hit wins:
///   1. the banner set on the EventDefinition itself (Event UI > Banner),
///   2. an event + exact planet ID override,
///   3. an event + planet tag override,
///   4. an event-family tag + planet tag override (e.g. market + Green),
///   5. the older Definition Overrides list here,
///   6. planet-category events: the planet's normal banner (by tag, see below),
///   7. the category default (Asteroid and Hazard share the asteroid banner),
///   8. none - the banner area collapses.
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

    [Serializable]
    public struct DefinitionPlanetBanner
    {
        public EventDefinition definition;
        [Tooltip("Exact Planet.id to match. Case-insensitive. Use this when one particular planet needs unique art.")]
        public string planetId;
        public Sprite banner;
    }

    [Serializable]
    public struct DefinitionPlanetTagBanner
    {
        public EventDefinition definition;
        [Tooltip("Planet tag to match, e.g. Green, Grey, Yellow or Red. Case-insensitive.")]
        public string planetTag;
        public Sprite banner;
    }

    [Serializable]
    public struct EventTagPlanetTagBanner
    {
        [Tooltip("Event family tag to match, e.g. market. Case-insensitive.")]
        public string eventTag;
        [Tooltip("Planet tag to match, e.g. Green, Grey, Yellow or Red. Case-insensitive.")]
        public string planetTag;
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

    [Header("Per-Event + Planet Overrides")]
    [Tooltip("Most specific option. Match one event on one exact Planet.id.")]
    [SerializeField] private List<DefinitionPlanetBanner> eventPlanetOverrides = new List<DefinitionPlanetBanner>();
    [Tooltip("Match one event on planets carrying a tag. Useful for shared events such as planet.street with Green / Grey / Yellow / Red planet art.")]
    [SerializeField] private List<DefinitionPlanetTagBanner> eventPlanetTagOverrides = new List<DefinitionPlanetTagBanner>();

    [Header("Per-Event-Family + Planet Overrides")]
    [Tooltip("Match an EventDefinition tag plus a planet tag. Useful when a whole family, such as market, should share planet-specific artwork.")]
    [SerializeField] private List<EventTagPlanetTagBanner> eventTagPlanetTagOverrides = new List<EventTagPlanetTagBanner>();

    [Header("Per-Event Overrides (older way: prefer the Banner field on the EventDefinition)")]
    [SerializeField] private List<DefinitionBanner> definitionOverrides = new List<DefinitionBanner>();


    public Sprite Resolve(EventDefinition definition, Planet planet)
    {
        if (definition == null) return null;
        if (definition.Banner != null) return definition.Banner;

        Sprite planetSpecific = ResolveEventPlanet(definition, planet);
        if (planetSpecific != null) return planetSpecific;

        Sprite familySpecific = ResolveEventTagPlanet(definition, planet);
        if (familySpecific != null) return familySpecific;

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


    private Sprite ResolveEventPlanet(EventDefinition definition, Planet planet)
    {
        if (definition == null || planet == null) return null;

        if (!string.IsNullOrWhiteSpace(planet.id))
        {
            foreach (DefinitionPlanetBanner entry in eventPlanetOverrides)
            {
                if (entry.definition != definition || entry.banner == null ||
                    string.IsNullOrWhiteSpace(entry.planetId))
                {
                    continue;
                }

                if (string.Equals(entry.planetId.Trim(), planet.id.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return entry.banner;
                }
            }
        }

        if (planet.eventState == null) return null;

        foreach (DefinitionPlanetTagBanner entry in eventPlanetTagOverrides)
        {
            if (entry.definition != definition || entry.banner == null ||
                string.IsNullOrWhiteSpace(entry.planetTag))
            {
                continue;
            }

            if (PlanetHasTag(planet, entry.planetTag)) return entry.banner;
        }

        return null;
    }


    private Sprite ResolveEventTagPlanet(EventDefinition definition, Planet planet)
    {
        if (definition == null || planet == null || planet.eventState == null) return null;

        foreach (EventTagPlanetTagBanner entry in eventTagPlanetTagOverrides)
        {
            if (entry.banner == null || string.IsNullOrWhiteSpace(entry.eventTag) ||
                string.IsNullOrWhiteSpace(entry.planetTag))
            {
                continue;
            }

            if (definition.HasTag(entry.eventTag) && PlanetHasTag(planet, entry.planetTag))
            {
                return entry.banner;
            }
        }

        return null;
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
