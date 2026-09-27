using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws event sites. Listens to EventSiteRegistry and keeps one pooled
/// QuestionMarkBurst per shown site under a runtime "Event Markers (Generated)"
/// root, driving every burst from this component's single Update.
///
///   Unknown                    -> nothing drawn
///   Detected asteroid/planet   -> "?" in the definition (or category) colour, slightly dimmed
///   Identified asteroid/planet -> "?" at full colour
///   Detected derelict          -> "?" in the neutral colour (does not give the game away)
///   Identified derelict        -> burst crossfades out, the spawned sprite fades in
///
/// Markers are hidden where effective fog is Hidden. Visibility is re-checked on
/// FogOfWar.PlayerVisionChanged and registry changes (plus a slow safety poll).
/// </summary>
public class EventMarkerPresenter : MonoBehaviour
{
    [Serializable]
    public sealed class CategoryColour
    {
        public EventCategory category = EventCategory.Asteroid;
        public Color colour = Color.white;
    }

    [Header("References")]
    [SerializeField] private EventSiteRegistry registry;
    [SerializeField] private VisionManager visionManager;
    [SerializeField] private GridMap gridMap;

    [Tooltip("Optional. Used only to know when to re-check fog. Auto-found when empty.")]
    [SerializeField] private FogOfWar fogOfWar;

    [Header("Sorting")]
    [SortingLayerName]
    [SerializeField] private string sortingLayerName = "Default";
    [SerializeField] private int sortingOrder = 10;

    [Header("Colours")]
    [Tooltip("Used when a definition has Use Marker Colour off.")]
    [SerializeField] private List<CategoryColour> categoryColours = new List<CategoryColour>();
    [Tooltip("Colour of an unidentified derelict's '?'.")]
    [SerializeField] private Color neutralColour = new Color(0.78f, 0.78f, 0.78f, 1f);
    [Range(0f, 1f)]
    [SerializeField] private float detectedAlpha = 0.8f;

    [Header("Effect")]
    [SerializeField] private QuestionMarkBurstSettings burstSettings = new QuestionMarkBurstSettings();
    [Min(0f)]
    [SerializeField] private float identifyCrossfadeDuration = 0.3f;

    [Header("Fog")]
    [SerializeField] private bool hideUnderFog = true;
    [Min(0.05f)]
    [SerializeField] private float visibilityPollSeconds = 0.5f;

    private const string RootName = "Event Markers (Generated)";

    private sealed class Marker
    {
        public QuestionMarkBurst burst;
        public bool visibleByFog = true;
        public float crossfadeStart = -1f;
    }

    private readonly Dictionary<EventSite, Marker> markers = new Dictionary<EventSite, Marker>();
    private readonly Stack<QuestionMarkBurst> pool = new Stack<QuestionMarkBurst>();
    private readonly List<EventSite> keyBuffer = new List<EventSite>();

    private Transform root;
    private bool visibilityDirty = true;
    private float nextPoll;
    private float tileWorldSize = 1f;

    private EventSiteRegistry subscribedRegistry;
    private FogOfWar subscribedFog;


    private void Awake()
    {
        if (registry == null) registry = FindFirstObjectByType<EventSiteRegistry>();
        if (visionManager == null) visionManager = FindFirstObjectByType<VisionManager>();
        if (gridMap == null) gridMap = FindFirstObjectByType<GridMap>();
        if (fogOfWar == null) fogOfWar = FindFirstObjectByType<FogOfWar>();
    }


    private void OnEnable()
    {
        subscribedRegistry = registry;
        if (subscribedRegistry != null)
        {
            subscribedRegistry.SiteAdded += HandleSiteAdded;
            subscribedRegistry.SiteRemoved += HandleSiteRemoved;
            subscribedRegistry.SiteKnowledgeChanged += HandleKnowledgeChanged;
            subscribedRegistry.SiteStateChanged += HandleStateChanged;

            foreach (EventSite s in subscribedRegistry.Sites) Ensure(s);
        }

        subscribedFog = fogOfWar;
        if (subscribedFog != null) subscribedFog.PlayerVisionChanged += HandleVisionChanged;
    }


    private void OnDisable()
    {
        if (subscribedRegistry != null)
        {
            subscribedRegistry.SiteAdded -= HandleSiteAdded;
            subscribedRegistry.SiteRemoved -= HandleSiteRemoved;
            subscribedRegistry.SiteKnowledgeChanged -= HandleKnowledgeChanged;
            subscribedRegistry.SiteStateChanged -= HandleStateChanged;
        }
        if (subscribedFog != null) subscribedFog.PlayerVisionChanged -= HandleVisionChanged;

        subscribedRegistry = null;
        subscribedFog = null;

        keyBuffer.Clear();
        keyBuffer.AddRange(markers.Keys);
        foreach (EventSite s in keyBuffer) Release(s);
    }


    private void Update()
    {
        if (markers.Count == 0) return;

        if (visibilityDirty || Time.unscaledTime >= nextPoll)
        {
            RefreshFogVisibility();
            visibilityDirty = false;
            nextPoll = Time.unscaledTime + visibilityPollSeconds;
        }

        float time = Time.time;
        foreach (KeyValuePair<EventSite, Marker> pair in markers)
        {
            EventSite site = pair.Key;
            Marker m = pair.Value;
            m.burst.SetColour(ColourFor(site));
            m.burst.SetAlpha(AlphaFor(site, m));
            m.burst.Tick(time);
        }
    }


    // ==================== Registry events ====================

    private void HandleSiteAdded(EventSite site)
    {
        Ensure(site);
        visibilityDirty = true;
    }


    private void HandleSiteRemoved(EventSite site)
    {
        Release(site);
    }


    private void HandleStateChanged(EventSite site, EventSiteState state)
    {
        visibilityDirty = true;
    }


    private void HandleKnowledgeChanged(EventSite site, PlanetKnowledgeState knowledge)
    {
        Marker m = Ensure(site);
        if (m != null && HasSpawnedSprite(site) && knowledge == PlanetKnowledgeState.Identified)
        {
            m.crossfadeStart = Time.time;
            SetSpawnedAlpha(site, 0f);
        }
        visibilityDirty = true;
    }


    private void HandleVisionChanged(Vector3Int cell)
    {
        visibilityDirty = true;
    }


    // ==================== Markers ====================

    private Marker Ensure(EventSite site)
    {
        if (site == null) return null;
        if (markers.TryGetValue(site, out Marker existing)) return existing;
        if (gridMap == null) return null;

        EnsureRoot();

        QuestionMarkBurst burst = pool.Count > 0 ? pool.Pop() : CreateBurst();
        burst.Build(burstSettings, sortingLayerName, sortingOrder);
        burst.gameObject.name = "? " + site.Id;
        burst.Bind(site, gridMap.CellToWorld(site.Cell), tileWorldSize, site.Id.GetHashCode());

        Marker m = new Marker { burst = burst };
        markers.Add(site, m);
        return m;
    }


    private void Release(EventSite site)
    {
        if (site == null || !markers.TryGetValue(site, out Marker m)) return;
        markers.Remove(site);
        if (m.burst != null)
        {
            m.burst.Unbind();
            pool.Push(m.burst);
        }
    }


    private QuestionMarkBurst CreateBurst()
    {
        GameObject go = new GameObject("? marker");
        go.transform.SetParent(root, false);
        return go.AddComponent<QuestionMarkBurst>();
    }


    private void EnsureRoot()
    {
        if (root != null) return;

        GameObject go = new GameObject(RootName);
        root = go.transform;

        if (gridMap != null)
        {
            Vector3 a = gridMap.CellToWorld(Vector3Int.zero);
            Vector3 b = gridMap.CellToWorld(Vector3Int.right);
            tileWorldSize = Mathf.Max(0.0001f, Vector3.Distance(a, b));
        }
    }


    private void RefreshFogVisibility()
    {
        foreach (KeyValuePair<EventSite, Marker> pair in markers)
        {
            bool visible = true;
            if (hideUnderFog && visionManager != null)
                visible = visionManager.GetEffectiveVisibility(pair.Key.Cell) != FogOfWar.VisibilityTier.Hidden;
            pair.Value.visibleByFog = visible;
        }
    }


    private float AlphaFor(EventSite site, Marker m)
    {
        if (site.Knowledge == PlanetKnowledgeState.Unknown) return 0f;
        if (!m.visibleByFog) return 0f;

        if (HasSpawnedSprite(site))
        {
            if (site.Knowledge != PlanetKnowledgeState.Identified) return 1f;

            float t = CrossfadeT(m);
            SetSpawnedAlpha(site, t);
            return 1f - t;
        }

        return site.Knowledge == PlanetKnowledgeState.Identified ? 1f : detectedAlpha;
    }


    private float CrossfadeT(Marker m)
    {
        if (m.crossfadeStart < 0f || identifyCrossfadeDuration <= 0f) return 1f;
        return Mathf.Clamp01((Time.time - m.crossfadeStart) / identifyCrossfadeDuration);
    }


    /// <summary>Derelicts and pickups show a spawned sprite once identified instead of the "?".</summary>
    private static bool HasSpawnedSprite(EventSite site)
    {
        return site.Category == EventCategory.Derelict || site.Category == EventCategory.Pickup;
    }


    private static void SetSpawnedAlpha(EventSite site, float a)
    {
        if (site.SpawnedObject == null) return;
        SpriteRenderer sr = site.SpawnedObject.GetComponent<SpriteRenderer>();
        if (sr == null) return;
        Color c = sr.color;
        c.a = a;
        sr.color = c;
    }


    private Color ColourFor(EventSite site)
    {
        if (HasSpawnedSprite(site) && site.Knowledge != PlanetKnowledgeState.Identified)
            return neutralColour;

        if (site.Definition != null && site.Definition.UseMarkerColour)
            return site.Definition.MarkerColour;

        if (categoryColours != null)
        {
            foreach (CategoryColour cc in categoryColours)
            {
                if (cc != null && cc.category == site.Category) return cc.colour;
            }
        }

        return Color.white;
    }
}
