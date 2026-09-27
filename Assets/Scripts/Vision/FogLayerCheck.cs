using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Keeps map layers (asteroids, clouds...) underneath the fog of war.
///
/// FogOfWar builds its fog tilemaps at runtime on one sorting layer
/// (Foreground by default) starting at a sorting order (10). A renderer on a
/// sorting layer that comes later in Project Settings > Tags and Layers >
/// Sorting Layers draws over the fog whatever its order - so obstacles show
/// through unexplored space. At start this checks each listed renderer and
/// logs any that would draw over the fog; with Auto Fix on it moves them onto
/// the fog's layer just below the fog (order Fog Starting Order - 1 or lower).
///
/// Set Fog Sorting Layer / Fog Starting Order to match the FogOfWar component.
/// </summary>
public sealed class FogLayerCheck : MonoBehaviour
{
    [SortingLayerName]
    [SerializeField] private string fogSortingLayer = "Foreground";
    [SerializeField] private int fogStartingOrder = 10;

    [Tooltip("Renderers that must stay under the fog, e.g. the Asteroids and Clouds tilemap renderers.")]
    [SerializeField] private List<Renderer> keepUnderFog = new List<Renderer>();

    [Tooltip("Move offending renderers under the fog at start (the scene file is not changed).")]
    [SerializeField] private bool autoFix = true;


    private void Start()
    {
        int fogLayerId = SortingLayer.NameToID(fogSortingLayer);
        if (!SortingLayer.IsValid(fogLayerId))
        {
            Debug.LogWarning($"FogLayerCheck: sorting layer '{fogSortingLayer}' does not exist.", this);
            return;
        }
        int fogValue = SortingLayer.GetLayerValueFromID(fogLayerId);

        foreach (Renderer r in keepUnderFog)
        {
            if (r == null) continue;
            int value = SortingLayer.GetLayerValueFromID(r.sortingLayerID);
            bool above = value > fogValue || (value == fogValue && r.sortingOrder >= fogStartingOrder);
            if (!above) continue;

            string was = $"'{SortingLayer.IDToName(r.sortingLayerID)}' order {r.sortingOrder}";
            if (autoFix)
            {
                r.sortingLayerID = fogLayerId;
                r.sortingOrder = Mathf.Min(r.sortingOrder, fogStartingOrder - 1);
                Debug.Log($"FogLayerCheck: {r.name} was on {was}, which draws over the fog; moved to '{fogSortingLayer}' order {r.sortingOrder}. " +
                          "To make it permanent, set this in the renderer's Sorting Layer / Order in Layer.", r);
            }
            else
            {
                Debug.LogWarning($"FogLayerCheck: {r.name} is on {was} and draws over the fog ('{fogSortingLayer}' from order {fogStartingOrder}).", r);
            }
        }
    }
}
