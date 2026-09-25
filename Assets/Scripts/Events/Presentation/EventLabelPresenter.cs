using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Hover label for event sites, using the same garbled-name effect as planets:
///
///   Unknown     -> no label (the site is not drawn either)
///   Detected    -> the definition's Display Name, corrupted. More letters
///                  survive when the site's cell is seen at Full than at Partial,
///                  and the corruption re-rolls every Garble Refresh Interval.
///   Identified  -> the real Display Name.
///
/// Presented exactly like planet hover labels: a pixel reticle on the tile, a
/// one-pixel connector line and the text locked to a diagonal anchor in the
/// quadrant with the most screen room (HoverCallout - the same layout as
/// PlanetLabelManager). Planet sites are skipped by default because
/// PlanetLabelManager already labels the planet itself.
/// </summary>
public class EventLabelPresenter : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private EventSiteRegistry registry;
    [SerializeField] private VisionManager visionManager;
    [SerializeField] private GridMap gridMap;

    [Tooltip("Camera for mouse-to-world conversion. Empty = Camera.main.")]
    [SerializeField] private Camera mainCamera;

    [Header("Label Appearance (defaults match PlanetLabelManager)")]
    [SerializeField] private Font labelFont;
    [SerializeField] private int fontSize = 20;
    [SerializeField] private float labelScale = 0.5f;

    [Header("Callout (defaults match PlanetLabelManager)")]
    [SerializeField] private HoverCalloutStyle callout = new HoverCalloutStyle();

    [SerializeField] private Color identifiedColor = new Color(0.6f, 0.8509804f, 1f, 1f);
    [SerializeField] private Color garbledColor = new Color(0.6f, 0.85f, 1f, 1f);

    [SortingLayerName]
    [SerializeField] private string sortingLayerName = "Foreground";

    [Header("Garble (defaults match PlanetLabelManager)")]
    [Range(0f, 1f)] [SerializeField] private float partialRevealChance = 0.35f;
    [Range(0f, 1f)] [SerializeField] private float fullRevealChance = 0.85f;
    [SerializeField] private float garbleRefreshInterval = 0.35f;
    [SerializeField] private string garbleCharacters = TextGarbler.DefaultCharacters;
    [SerializeField] private int unknownFallbackLength = 5;

    [Header("Behaviour")]
    [Tooltip("Also label planet event sites (PlanetLabelManager already labels the planet).")]
    [SerializeField] private bool labelPlanetSites = false;

    [Tooltip("Hide the label where the site's cell is under Hidden fog.")]
    [SerializeField] private bool hideUnderFog = true;

    private HoverCallout label;
    private Transform labelRoot;
    private EventSite shownSite;
    private PlanetKnowledgeState shownKnowledge;
    private float garbleTimer;


    private void Awake()
    {
        if (registry == null) registry = FindFirstObjectByType<EventSiteRegistry>();
        if (visionManager == null) visionManager = FindFirstObjectByType<VisionManager>();
        if (gridMap == null) gridMap = FindFirstObjectByType<GridMap>();
    }


    private void OnDisable()
    {
        Hide();
    }


    private void Update()
    {
        EventSite site = FindHoveredSite();
        if (site == null)
        {
            Hide();
            return;
        }

        if (label == null)
        {
            // Planet labels live under PlanetManager, which inherits the map's
            // scale (VisualTilemaps is scaled 0.5). Parent ours under a root with
            // the same world scale so identical settings give identical text size.
            labelRoot = new GameObject("Event Labels").transform;
            labelRoot.SetParent(transform, false);
            label = new HoverCallout(labelRoot, "EventCallout", callout, labelFont, fontSize, labelScale, sortingLayerName);
        }
        labelRoot.localScale = MapScale();

        bool changed = site != shownSite || site.Knowledge != shownKnowledge;
        garbleTimer += Time.unscaledDeltaTime;
        bool refresh = garbleTimer >= garbleRefreshInterval;
        if (refresh) garbleTimer = 0f;

        string name = site.Definition != null ? site.Definition.DisplayName : "Unknown signal";

        if (site.Knowledge == PlanetKnowledgeState.Identified)
        {
            if (changed) label.SetText(name, identifiedColor);
        }
        else if (changed || refresh || !label.Label.HasText)
        {
            float reveal = EffectiveTier(site) == FogOfWar.VisibilityTier.Full ? fullRevealChance : partialRevealChance;
            label.SetText(TextGarbler.Garble(name, reveal, garbleCharacters, unknownFallbackLength), garbledColor);
        }

        label.Show(gridMap.CellToWorld(site.Cell), CameraToUse());

        shownSite = site;
        shownKnowledge = site.Knowledge;
    }


    private EventSite FindHoveredSite()
    {
        if (registry == null || gridMap == null || Mouse.current == null) return null;

        Camera cam = CameraToUse();
        if (cam == null) return null;

        Vector2 screen = Mouse.current.position.ReadValue();
        Ray ray = cam.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
        Plane plane = new Plane(Vector3.forward, new Vector3(0f, 0f, gridMap.transform.position.z));
        if (!plane.Raycast(ray, out float distance)) return null;

        Vector3Int cell = gridMap.WorldToCell(ray.GetPoint(distance));
        cell.z = 0;

        if (!registry.TryGetSiteAtCell(cell, out EventSite site)) return null;
        if (site.Knowledge == PlanetKnowledgeState.Unknown) return null;
        if (site.Planet != null && !labelPlanetSites) return null;
        if (hideUnderFog && EffectiveTier(site) == FogOfWar.VisibilityTier.Hidden) return null;
        return site;
    }


    private Camera CameraToUse()
    {
        return mainCamera != null ? mainCamera : Camera.main;
    }


    /// <summary>The map's world scale, divided by this object's own, so labelRoot ends up matching it.</summary>
    private Vector3 MapScale()
    {
        Vector3 map = gridMap != null ? gridMap.transform.lossyScale : Vector3.one;
        Vector3 own = transform.lossyScale;
        return new Vector3(
            own.x != 0f ? map.x / own.x : map.x,
            own.y != 0f ? map.y / own.y : map.y,
            1f);
    }


    private FogOfWar.VisibilityTier EffectiveTier(EventSite site)
    {
        return visionManager != null
            ? visionManager.GetEffectiveVisibility(site.Cell)
            : FogOfWar.VisibilityTier.Full;
    }


    private void Hide()
    {
        if (label != null) label.Hide();
        shownSite = null;
        garbleTimer = 0f;
    }
}
