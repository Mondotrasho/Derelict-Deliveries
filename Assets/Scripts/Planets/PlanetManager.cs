using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Owns planet placement, runtime rendering, editor placement preview and
/// lightweight per-planet animation.
///
/// Vision integration revision:
/// VisionManager can update Planet objects directly and defer the Tilemap
/// rebuild until the whole visibility pass is complete.
///
/// Put this manager somewhere underneath the same Unity Grid used by the map.
/// It creates its Tilemaps as children of itself.
///
/// Runtime:
///     PlanetManager
///     `-- PlanetManager - Runtime Planets
///
/// IMPORTANT:
/// Planet tiles are always rendered at runtime. Visibility state does NOT add/remove
/// planet tiles. FogOfWar is the visual mask that hides and reveals them.
///
/// Edit mode:
///     PlanetManager
///     `-- PlanetManager - Planet Placement Preview
///
/// The editor preview always shows configured planets.
///
/// Runtime planets are also always present. PlanetVisibilityState is game-state metadata
/// only; FogOfWar decides whether the player can actually see the planet.
/// </summary>
[ExecuteAlways]
public class PlanetManager : MonoBehaviour
{
    [Header("Planet Tile Library")]
    [Tooltip("Tiles available to planets. Each Planet chooses from this list using an Inspector dropdown.")]
    [SerializeField]
    private List<TileBase> planetTiles =
        new List<TileBase>();

    [Header("Planet Placements")]
    [Tooltip("Fixed planet placements. Random placement can be added later without changing the Planet data model.")]
    [SerializeField]
    private List<Planet> planets =
        new List<Planet>();

    [Header("Rendering")]
    [SortingLayerName]
    [SerializeField]
    private string sortingLayerName = "Default";

    [SerializeField]
    private int sortingOrder = 0;

    [Header("Editor Placement Preview")]
    [Tooltip("Show planet tiles at their configured grid cells while editing the scene.")]
    [SerializeField]
    private bool showEditorPreview = true;

    [Tooltip("Opacity of the editor-only placement preview.")]
    [SerializeField, Range(0.1f, 1f)]
    private float editorPreviewOpacity = 0.75f;

    [Header("Planet Animation")]
    [Tooltip("Master switch for per-planet runtime wiggle/skew.")]
    [SerializeField]
    private bool animatePlanets = true;

    [Tooltip("Small local movement inside the planet's own grid cell.")]
    [SerializeField, Min(0f)]
    private float wiggleAmount = 0.025f;

    [Tooltip("Speed of the local wiggle.")]
    [SerializeField, Min(0f)]
    private float wiggleSpeed = 0.35f;

    [Tooltip("Maximum horizontal/vertical shear applied to the planet tile.")]
    [SerializeField, Range(0f, 0.25f)]
    private float skewAmount = 0.035f;

    [Tooltip("Speed of the skew animation.")]
    [SerializeField, Min(0f)]
    private float skewSpeed = 0.28f;

    [Tooltip("Optional very small rotational wobble in degrees.")]
    [SerializeField, Min(0f)]
    private float rotationWiggle = 1.25f;

    private Tilemap runtimeTilemap;
    private Tilemap editorPreviewTilemap;

    private bool missingGridErrorLogged;

    // Used to avoid rebuilding the editor preview every editor frame.
    private int lastPreviewHash = int.MinValue;

    // IMPORTANT:
    // OnValidate/Awake/OnEnable can run while Unity is performing internal
    // consistency checks. Creating GameObjects or adding components from those
    // callbacks can produce:
    //
    // "SendMessage cannot be called during Awake, CheckConsistency, or OnValidate"
    //
    // These flags defer all hierarchy/component changes until Start/Update.
    private bool runtimeStarted;
    private bool runtimeRefreshRequested = true;
    private bool editorPreviewRefreshRequested = true;


    public IReadOnlyList<Planet> Planets
    {
        get { return planets; }
    }


    public IReadOnlyList<TileBase> PlanetTiles
    {
        get { return planetTiles; }
    }


    private void OnEnable()
    {
        // Never create/destroy Tilemap GameObjects here.
        // OnEnable can run while Unity is still adding/validating components.
        runtimeRefreshRequested = true;
        editorPreviewRefreshRequested = true;
        lastPreviewHash = int.MinValue;
    }


    private void Start()
    {
        if (!Application.isPlaying)
            return;

        // Start is the first safe lifecycle point where this manager creates
        // its runtime Tilemap hierarchy.
        runtimeStarted = true;

        DestroyEditorPreview();

        EnsureRuntimeTilemap();
        InitialiseAllPlanetAnimationState();
        RefreshRuntimePlanets();

        runtimeRefreshRequested = false;
    }


    private void Update()
    {
        if (Application.isPlaying)
        {
            // Inspector/script changes made during Play mode are queued by
            // OnValidate and performed safely here, after Unity's validation pass.
            if (runtimeStarted &&
                runtimeRefreshRequested)
            {
                ApplyRendererSettings(runtimeTilemap);
                RefreshRuntimePlanets();
                runtimeRefreshRequested = false;
            }

            UpdatePlanetAnimation();
            return;
        }

        // ExecuteAlways lets the placement preview react to Scene/Inspector edits.
        // Rebuilds happen from Update, never directly from OnValidate.
        int previewHash =
            CalculatePreviewHash();

        if (editorPreviewRefreshRequested ||
            previewHash != lastPreviewHash)
        {
            editorPreviewRefreshRequested = false;
            RebuildEditorPreview();
        }
    }


    private void OnDisable()
    {
        // Do not destroy/create hierarchy objects here. OnDisable can be invoked
        // as part of script reload and other editor validation paths.
        runtimeStarted = false;
        runtimeRefreshRequested = true;
        editorPreviewRefreshRequested = true;
    }


    private void OnDestroy()
    {
        DestroyGeneratedTilemap(runtimeTilemap);
        runtimeTilemap = null;

        DestroyEditorPreview();
    }


    private void OnValidate()
    {
        // OnValidate must be DATA ONLY.
        // Do not create GameObjects, add components, destroy objects, or rebuild
        // Tilemaps from this callback.

        editorPreviewOpacity =
            Mathf.Clamp(editorPreviewOpacity, 0.1f, 1f);

        wiggleAmount =
            Mathf.Max(0f, wiggleAmount);

        wiggleSpeed =
            Mathf.Max(0f, wiggleSpeed);

        skewAmount =
            Mathf.Clamp(skewAmount, 0f, 0.25f);

        skewSpeed =
            Mathf.Max(0f, skewSpeed);

        rotationWiggle =
            Mathf.Max(0f, rotationWiggle);

        ClampPlanetTileIndices();

        // Defer actual hierarchy/render work to Update.
        runtimeRefreshRequested = true;
        editorPreviewRefreshRequested = true;
        lastPreviewHash = int.MinValue;
    }


    /// <summary>
    /// Safely requests a refresh without mutating the hierarchy immediately.
    ///
    /// The custom Inspector uses this instead of rebuilding Tilemaps from
    /// OnInspectorGUI/OnValidate-adjacent code.
    /// </summary>
    public void RequestRefresh()
    {
        if (Application.isPlaying)
        {
            runtimeRefreshRequested = true;
        }
        else
        {
            editorPreviewRefreshRequested = true;
            lastPreviewHash = int.MinValue;
        }
    }


    // =====================================================================
    // Runtime creation
    // =====================================================================

    private bool ValidateGridParent()
    {
        if (GetComponentInParent<Grid>() != null)
        {
            missingGridErrorLogged = false;
            return true;
        }

        if (!missingGridErrorLogged)
        {
            Debug.LogError(
                $"{name}: PlanetManager must be parented underneath a GameObject with a Grid component.",
                this);

            missingGridErrorLogged = true;
        }

        return false;
    }


    private void EnsureRuntimeTilemap()
    {
        if (runtimeTilemap != null)
            return;

        if (!ValidateGridParent())
            return;

        runtimeTilemap =
            CreateGeneratedTilemap(
                $"{name} - Runtime Planets",
                saveInScene: false
            );

        ApplyRendererSettings(runtimeTilemap);
    }


    private Tilemap CreateGeneratedTilemap(
        string objectName,
        bool saveInScene)
    {
        GameObject tilemapObject =
            new GameObject(objectName);

        tilemapObject.transform.SetParent(
            transform,
            worldPositionStays: false
        );

        tilemapObject.transform.localPosition =
            Vector3.zero;

        tilemapObject.transform.localRotation =
            Quaternion.identity;

        tilemapObject.transform.localScale =
            Vector3.one;

        // Runtime-generated and editor-preview objects should never become
        // permanent scene children.
        if (!saveInScene)
            tilemapObject.hideFlags |= HideFlags.DontSave;

        Tilemap tilemap =
            tilemapObject.AddComponent<Tilemap>();

        // Planet animation is designed around the centre of each grid cell.
        tilemap.tileAnchor =
            new Vector3(0.5f, 0.5f, 0f);

        tilemapObject.AddComponent<TilemapRenderer>();

        return tilemap;
    }


    private void DestroyGeneratedTilemap(Tilemap tilemap)
    {
        if (tilemap == null)
            return;

        GameObject target =
            tilemap.gameObject;

        if (Application.isPlaying)
            Destroy(target);
        else
            DestroyImmediate(target);
    }


    private void ApplyRendererSettings(Tilemap tilemap)
    {
        if (tilemap == null)
            return;

        TilemapRenderer renderer =
            tilemap.GetComponent<TilemapRenderer>();

        if (renderer == null)
            renderer =
                tilemap.gameObject.AddComponent<TilemapRenderer>();

        renderer.sortingLayerName =
            ResolveSortingLayerName(sortingLayerName);

        renderer.sortingOrder =
            sortingOrder;
    }


    private string ResolveSortingLayerName(
        string configuredName)
    {
        foreach (SortingLayer sortingLayer in SortingLayer.layers)
        {
            if (sortingLayer.name == configuredName)
                return configuredName;
        }

        return "Default";
    }


    /// <summary>
    /// Keeps serialised planet sub-objects valid if an entry was created by an
    /// older scene version or was otherwise left with a null data object.
    /// </summary>
    private void EnsurePlanetData(Planet planet)
    {
        if (planet == null)
            return;

        if (planet.visibility == null)
            planet.visibility = new PlanetVisibilityState();

        if (planet.eventState == null)
            planet.eventState = new PlanetEventState();
    }


    // =====================================================================
    // Runtime planet rendering
    // =====================================================================

    /// <summary>
    /// Rebuilds the runtime planet Tilemap from current placement/tile data.
    ///
    /// Every configured planet is rendered regardless of discovery/visibility state.
    /// FogOfWar sits above this Tilemap and provides the actual visual hiding/reveal.
    ///
    /// Call this after changing a planet's cell or selected tile from code.
    /// </summary>
    public void RefreshRuntimePlanets()
    {
        if (!Application.isPlaying)
            return;

        EnsureRuntimeTilemap();

        if (runtimeTilemap == null)
            return;

        runtimeTilemap.ClearAllTiles();

        HashSet<Vector3Int> occupiedCells =
            new HashSet<Vector3Int>();

        for (int i = 0; i < planets.Count; i++)
        {
            Planet planet =
                planets[i];

            if (planet == null)
                continue;

            EnsurePlanetData(planet);

            // Planets always exist underneath the fog. Do not remove them when
            // they leave player vision; FogOfWar is responsible for obscuring them.
            TileBase tile =
                GetPlanetTile(planet);

            if (tile == null)
                continue;

            if (!occupiedCells.Add(planet.cell))
            {
                Debug.LogWarning(
                    $"{name}: more than one planet uses cell {planet.cell}. " +
                    "Only one tile can be rendered in a Tilemap cell.",
                    this);
            }

            runtimeTilemap.SetTile(
                planet.cell,
                tile
            );

            runtimeTilemap.SetTileFlags(
                planet.cell,
                TileFlags.None
            );

            runtimeTilemap.SetTransformMatrix(
                planet.cell,
                Matrix4x4.identity
            );
        }
    }


    private TileBase GetPlanetTile(
        Planet planet)
    {
        if (planet == null)
            return null;

        if (planet.tileIndex < 0 ||
            planet.tileIndex >= planetTiles.Count)
        {
            return null;
        }

        return planetTiles[planet.tileIndex];
    }


    // =====================================================================
    // Per-planet animation
    // =====================================================================

    private void InitialiseAllPlanetAnimationState()
    {
        for (int i = 0; i < planets.Count; i++)
            InitialisePlanetAnimationState(planets[i], i);
    }


    private void InitialisePlanetAnimationState(
        Planet planet,
        int index)
    {
        if (planet == null)
            return;

        // Stable enough for scene-authored planets and deterministic for the
        // same id/cell/index combination.
        int seed = 17;

        seed =
            seed * 31 +
            (planet.id != null
                ? planet.id.GetHashCode()
                : 0);

        seed =
            seed * 31 +
            planet.cell.GetHashCode();

        seed =
            seed * 31 +
            index;

        System.Random random =
            new System.Random(seed);

        planet.phaseX =
            (float)random.NextDouble() * Mathf.PI * 2f;

        planet.phaseY =
            (float)random.NextDouble() * Mathf.PI * 2f;

        planet.phaseSkewX =
            (float)random.NextDouble() * Mathf.PI * 2f;

        planet.phaseSkewY =
            (float)random.NextDouble() * Mathf.PI * 2f;

        planet.phaseRotation =
            (float)random.NextDouble() * Mathf.PI * 2f;

        planet.animationStateInitialised =
            true;
    }


    private void UpdatePlanetAnimation()
    {
        if (runtimeTilemap == null)
            return;

        float time =
            Time.time;

        for (int i = 0; i < planets.Count; i++)
        {
            Planet planet =
                planets[i];

            if (planet == null)
                continue;

            EnsurePlanetData(planet);

            // Hidden planets continue animating underneath the fog. This prevents
            // discovery state from changing rendering/animation ownership.
            if (!runtimeTilemap.HasTile(planet.cell))
                continue;

            if (!planet.animationStateInitialised)
                InitialisePlanetAnimationState(planet, i);

            if (!animatePlanets ||
                !planet.animate ||
                planet.animationStrength <= 0f)
            {
                runtimeTilemap.SetTransformMatrix(
                    planet.cell,
                    Matrix4x4.identity
                );

                continue;
            }

            float strength =
                planet.animationStrength;

            float x =
                Mathf.Sin(
                    time * wiggleSpeed +
                    planet.phaseX
                ) *
                wiggleAmount *
                strength;

            float y =
                Mathf.Sin(
                    time * wiggleSpeed * 0.83f +
                    planet.phaseY
                ) *
                wiggleAmount *
                strength;

            float shearX =
                Mathf.Sin(
                    time * skewSpeed +
                    planet.phaseSkewX
                ) *
                skewAmount *
                strength;

            float shearY =
                Mathf.Sin(
                    time * skewSpeed * 0.79f +
                    planet.phaseSkewY
                ) *
                skewAmount *
                strength;

            float angle =
                Mathf.Sin(
                    time * wiggleSpeed * 0.67f +
                    planet.phaseRotation
                ) *
                rotationWiggle *
                strength;

            Matrix4x4 wiggle =
                Matrix4x4.TRS(
                    new Vector3(x, y, 0f),
                    Quaternion.Euler(0f, 0f, angle),
                    Vector3.one
                );

            Matrix4x4 skew =
                Matrix4x4.identity;

            // Local XY shear. Because the matrix is applied to this cell only,
            // every planet animates around its own tile/pivot rather than around
            // a shared world origin.
            skew.m01 = shearX;
            skew.m10 = shearY;

            runtimeTilemap.SetTransformMatrix(
                planet.cell,
                wiggle * skew
            );
        }
    }


    // =====================================================================
    // Visibility API for future VisionManager use
    // =====================================================================

    public bool SetPlanetCurrentlyVisible(
        string planetId,
        bool visible)
    {
        Planet planet =
            FindPlanet(planetId);

        return SetPlanetCurrentlyVisible(
            planet,
            visible,
            refreshRuntimePlanets: true
        );
    }


    /// <summary>
    /// Direct Planet overload used by VisionManager.
    ///
    /// Visibility is metadata only. Planet tiles stay rendered underneath FogOfWar,
    /// so changing this flag does not normally require a Tilemap rebuild.
    ///
    /// refreshRuntimePlanets is retained for API compatibility, but should generally
    /// remain false for visibility-only changes.
    /// </summary>
    public bool SetPlanetCurrentlyVisible(
        Planet planet,
        bool visible,
        bool refreshRuntimePlanets = true)
    {
        if (planet == null)
            return false;

        EnsurePlanetData(planet);

        planet.visibility.SetCurrentlyVisible(
            visible
        );

        // Do not add/remove the planet tile here. FogOfWar is the visual mask.
        // The optional refresh is retained only for callers that explicitly want
        // to force a complete placement rebuild for some other reason.
        if (refreshRuntimePlanets)
            RefreshRuntimePlanets();

        return true;
    }


    public bool SetPlanetDiscovered(
        string planetId,
        bool discovered)
    {
        Planet planet =
            FindPlanet(planetId);

        if (planet == null)
            return false;

        EnsurePlanetData(planet);

        planet.visibility.SetDiscovered(
            discovered
        );

        RefreshRuntimePlanets();
        return true;
    }


    public bool SetPlanetRememberLocation(
        string planetId,
        bool remember)
    {
        Planet planet =
            FindPlanet(planetId);

        if (planet == null)
            return false;

        EnsurePlanetData(planet);

        planet.visibility.SetRememberLocation(
            remember
        );

        RefreshRuntimePlanets();
        return true;
    }


    public Planet FindPlanet(
        string planetId)
    {
        if (string.IsNullOrWhiteSpace(planetId))
            return null;

        return planets.Find(
            planet =>
                planet != null &&
                planet.id == planetId
        );
    }


    public bool TryGetPlanet(
        string planetId,
        out Planet planet)
    {
        planet =
            FindPlanet(planetId);

        return planet != null;
    }


    /// <summary>
    /// Finds the planet occupying one grid cell. Returns null when the
    /// cell contains no authored planet.
    ///
    /// This is the preferred integration point for event/interaction code:
    /// callers should not duplicate their own loop over Planets simply to
    /// answer "did the player enter a planet cell?".
    /// </summary>
    public Planet FindPlanetAtCell(Vector3Int cell)
    {
        return planets.Find(
            planet =>
                planet != null &&
                planet.cell == cell
        );
    }


    /// <summary>
    /// Try-pattern equivalent of FindPlanetAtCell.
    /// </summary>
    public bool TryGetPlanetAtCell(
        Vector3Int cell,
        out Planet planet)
    {
        planet = FindPlanetAtCell(cell);
        return planet != null;
    }


    /// <summary>
    /// Returns all authored planets carrying the requested event tag.
    /// The returned list is a snapshot; changing it does not modify the
    /// manager's planet collection.
    /// </summary>
    public List<Planet> GetPlanetsWithTag(string tag)
    {
        List<Planet> result = new List<Planet>();

        if (string.IsNullOrWhiteSpace(tag))
        {
            return result;
        }

        foreach (Planet planet in planets)
        {
            if (planet != null &&
                planet.eventState != null &&
                planet.eventState.HasTag(tag))
            {
                result.Add(planet);
            }
        }

        return result;
    }


    public Vector3 GetPlanetWorldPosition(
        Planet planet)
    {
        if (planet == null)
            return transform.position;

        Tilemap tilemap =
            runtimeTilemap != null
                ? runtimeTilemap
                : editorPreviewTilemap;

        if (tilemap != null)
            return tilemap.GetCellCenterWorld(planet.cell);

        Grid grid =
            GetComponentInParent<Grid>();

        if (grid != null)
            return grid.GetCellCenterWorld(planet.cell);

        return transform.position;
    }


    // =====================================================================
    // Editor preview
    // =====================================================================

    /// <summary>
    /// Creates/refreshes the edit-mode placement preview.
    ///
    /// The preview intentionally ignores runtime visibility. Its job is only to
    /// make scene-authored planet positions obvious while building the map.
    /// </summary>
    public void RebuildEditorPreview()
    {
        if (Application.isPlaying)
            return;

        editorPreviewRefreshRequested = false;

        lastPreviewHash =
            CalculatePreviewHash();

        if (!showEditorPreview)
        {
            DestroyEditorPreview();
            return;
        }

        if (!ValidateGridParent())
        {
            DestroyEditorPreview();
            return;
        }

        if (editorPreviewTilemap == null)
        {
            editorPreviewTilemap =
                CreateGeneratedTilemap(
                    $"{name} - Planet Placement Preview",
                    saveInScene: false
                );
        }

        ApplyRendererSettings(
            editorPreviewTilemap
        );

        Color previewTint =
            editorPreviewTilemap.color;

        previewTint.a =
            editorPreviewOpacity;

        editorPreviewTilemap.color =
            previewTint;

        editorPreviewTilemap.ClearAllTiles();

        for (int i = 0; i < planets.Count; i++)
        {
            Planet planet =
                planets[i];

            if (planet == null)
                continue;

            EnsurePlanetData(planet);

            TileBase tile =
                GetPlanetTile(planet);

            if (tile == null)
                continue;

            editorPreviewTilemap.SetTile(
                planet.cell,
                tile
            );

            editorPreviewTilemap.SetTileFlags(
                planet.cell,
                TileFlags.None
            );

            editorPreviewTilemap.SetTransformMatrix(
                planet.cell,
                Matrix4x4.identity
            );
        }
    }


    private void DestroyEditorPreview()
    {
        if (editorPreviewTilemap == null)
            return;

        DestroyGeneratedTilemap(
            editorPreviewTilemap
        );

        editorPreviewTilemap =
            null;
    }


    private int CalculatePreviewHash()
    {
        unchecked
        {
            int hash = 17;

            hash =
                hash * 31 +
                showEditorPreview.GetHashCode();

            hash =
                hash * 31 +
                editorPreviewOpacity.GetHashCode();

            hash =
                hash * 31 +
                sortingLayerName.GetHashCode();

            hash =
                hash * 31 +
                sortingOrder;

            hash =
                hash * 31 +
                planetTiles.Count;

            for (int i = 0; i < planetTiles.Count; i++)
            {
                TileBase tile =
                    planetTiles[i];

                hash =
                    hash * 31 +
                    (tile != null
                        ? tile.GetInstanceID()
                        : 0);
            }

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
                    planet.cell.GetHashCode();

                hash =
                    hash * 31 +
                    planet.tileIndex;

                hash =
                    hash * 31 +
                    (planet.id != null
                        ? planet.id.GetHashCode()
                        : 0);
            }

            return hash;
        }
    }


    private void ClampPlanetTileIndices()
    {
        if (planets == null)
            return;

        int maximumIndex =
            Mathf.Max(0, planetTiles.Count - 1);

        foreach (Planet planet in planets)
        {
            if (planet == null)
                continue;

            EnsurePlanetData(planet);

            planet.tileIndex =
                Mathf.Clamp(
                    planet.tileIndex,
                    0,
                    maximumIndex
                );
        }
    }
}
