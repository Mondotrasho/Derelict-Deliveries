using UnityEngine;

/// <summary>
/// Bridges newly rolled event sites into the player's direction indicator.
/// Sites produce only the short echo pulse: no persistent arc or chevron.
/// </summary>
[DisallowMultipleComponent]
public sealed class EventSitePulse : MonoBehaviour
{
    [Header("References (found automatically if empty)")]
    [SerializeField] private EventSiteRegistry registry;
    [SerializeField] private GridMap gridMap;
    [SerializeField] private EventMarkerPresenter markerPresenter;
    [SerializeField] private EnemyDirectionIndicator directionIndicator;

    [Header("Pulse")]
    [Tooltip("Relative strength of event discovery pulses. This is multiplied by the Direction Indicator Pulse Opacity; RGB still comes from the event marker presenter.")]
    [Range(0f, 1f)][SerializeField] private float strength = 0.6f;

    private EventSiteRegistry subscribedRegistry;


    private void Awake()
    {
        FindReferences();
    }


    private void OnEnable()
    {
        FindReferences();
        subscribedRegistry = registry;
        if (subscribedRegistry != null) subscribedRegistry.SiteAdded += HandleSiteAdded;
    }


    private void OnDisable()
    {
        if (subscribedRegistry != null) subscribedRegistry.SiteAdded -= HandleSiteAdded;
        subscribedRegistry = null;
    }


    private void FindReferences()
    {
        if (directionIndicator == null) directionIndicator = GetComponent<EnemyDirectionIndicator>();
        if (directionIndicator == null) directionIndicator = FindFirstObjectByType<EnemyDirectionIndicator>();
        if (registry == null) registry = FindFirstObjectByType<EventSiteRegistry>();
        if (gridMap == null && registry != null) gridMap = registry.GridMap;
        if (gridMap == null) gridMap = FindFirstObjectByType<GridMap>();
        if (markerPresenter == null) markerPresenter = FindFirstObjectByType<EventMarkerPresenter>();
    }


    private void HandleSiteAdded(EventSite site)
    {
        if (site == null || !site.IsLive) return;

        // Hazards are intentionally undisclosed. Pickups are included so newly
        // discovered fuel sites produce the same direction pulse as other shown sites.
        if (site.Category == EventCategory.Hazard) return;

        if (directionIndicator == null || gridMap == null) FindReferences();
        if (directionIndicator == null || gridMap == null) return;

        Color pulseColour = ColourFor(site);
        directionIndicator.Pulse(gridMap.CellToWorld(site.Cell), pulseColour, strength);
    }


    private Color ColourFor(EventSite site)
    {
        // This is the exact same resolver used by the visible question-mark marker,
        // including per-definition colours, category fallbacks and neutral derelicts.
        if (markerPresenter != null) return markerPresenter.GetMarkerColour(site);

        // Safe fallback if no presenter exists in the scene. Definition-authored
        // colours can still be preserved; category colours live on the presenter.
        if (site.Definition != null && site.Definition.UseMarkerColour)
            return site.Definition.MarkerColour;

        return Color.white;
    }
}
