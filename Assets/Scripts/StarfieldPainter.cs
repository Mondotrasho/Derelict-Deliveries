using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Coordinator script for a multi-layer parallax starfield.
///
/// Revision v6:
/// - Star depth layers are a growable list containing only Density and Parallax Factor.
/// - Every star layer shares one Origin, Size, Sorting Layer and starting Sorting Order.
/// - Runtime sorting order is derived automatically from the layer's list index.
/// - Black holes render on their own runtime Tilemap so repainting stars cannot erase them.
///
/// This component owns all of its runtime Tilemaps. Each configured depth layer creates
/// its own child GameObject with a Tilemap and TilemapRenderer when play begins.
/// Put this manager underneath the same Unity Grid used by the rest of the map.
/// The generated Tilemaps are children of this manager, so the hierarchy stays
/// grouped without requiring manual Tilemap objects in the scene.
///
/// Each layer gets its own cluster generation pass and its own parallax
/// speed. At runtime, LateUpdate offsets each layer's Transform based on how
/// far the reference (camera) has moved since last frame, scaled by that
/// layer's parallaxFactor - this is what gives the "float behind the world at
/// different speeds" effect, without needing to regenerate any tiles.
///
/// Star clusters are no longer "every cell independently rolls a random
/// brightness". Instead each cluster gets exactly ONE centrepiece star at the
/// brightest level, then a handful of stars from EACH of the other
/// (lesser) brightness levels scattered around it - see starsPerLesserLevelRange.
/// </summary>
public class StarfieldPainter : MonoBehaviour
{
    [System.Serializable]
    public class BrightnessLevel
    {
        public Tile tile;
        [Tooltip("Unused by cluster composition directly, kept in case you want it for your own weighting later.")]
        [Min(0f)] public float weight = 1f;
    }

    [System.Serializable]
    public class BlackHoleTiles
    {
        public Tile topLeft;
        public Tile topRight;
        public Tile bottomLeft;
        public Tile bottomRight;
    }

    [System.Serializable]
    public class StarLayer
    {
        [Range(0f, 1f)]
        [Tooltip("Chance a cell that passes the cluster-noise check actually becomes a cluster centre on this depth layer.")]
        public float density = 0.06f;

        [Range(0f, 1f)]
        [Tooltip("How closely this layer tracks reference movement. Near 1 feels farther away. Near 0 feels closer.")]
        public float parallaxFactor = 0.8f;

        // Runtime-only. The manager creates and owns this Tilemap.
        [System.NonSerialized] public Tilemap tilemap;
    }

    private class BlackHoleInstance
    {
        public Vector3Int bottomLeftCell;
    }

    /// <summary>
    /// Runtime-only information for one placed star.
    /// The tile stays in its original cell; this data only adds a tiny local
    /// transform so individual stars can gently wobble without repainting.
    /// </summary>
    private class StarInstance
    {
        public Tilemap tilemap;
        public Vector3Int cell;
        public float baseAngle;
        public float phaseX;
        public float phaseY;
        public float phaseRotation;
        public float motionVariation;
        public float speedVariation;
    }

    /// <summary>
    /// Runtime-only motion state for one whole parallax layer.
    /// The original Tilemap position is kept so drift/wiggle can be calculated
    /// absolutely instead of accumulating floating-point error every frame.
    /// </summary>
    private class LayerMotionState
    {
        public Tilemap tilemap;
        public Vector3 baseWorldPosition;
        public float phaseX;
        public float phaseY;
        public float motionVariation;
    }

    [Header("Starfield Area")]
    [Tooltip("Bottom-left cell shared by every generated star layer and by the black-hole placement area.")]
    public Vector3Int origin = new Vector3Int(-25, -25, 0);

    [Tooltip("Width/height in cells shared by every generated star layer and by the black-hole placement area.")]
    public Vector2Int size = new Vector2Int(50, 50);

    [Header("Star Depth Layers")]
    [Tooltip("Growable depth list. Each entry only controls density and parallax. " +
             "List order also controls rendering order: index 0 uses Starting Sorting Order, " +
             "index 1 uses Starting Sorting Order + 1, and so on.")]
    public List<StarLayer> layers = new List<StarLayer>();

    [Header("Starfield Rendering")]
    [Tooltip("Sorting Layer shared by every generated star layer and the separate black-hole Tilemap.")]
    [SortingLayerName]
    public string sortingLayerName = "Default";

    [Tooltip("Sorting order used by layer index 0. Every following list entry automatically uses the next order.")]
    public int startingSortingOrder = -30;

    [Header("Parallax Movement")]
    [Tooltip("Usually your main camera or player. Leave empty to auto-use Camera.main.")]
    public Transform referenceTransform;

    [Min(0f)]
    [Tooltip("Smooths/averages the reference position before it drives parallax. This is especially useful when the reference moves on a grid. 0 = follow the raw Transform exactly. Around 0.10-0.25 seconds gives a gentle continuous response.")]
    public float referenceAverageTime = 0.18f;

    [Header("Ambient Star Motion")]
    [Tooltip("Adds slow movement independent of the parallax reference. Layer drift moves the whole field, while individual star wiggle moves stars slightly inside their own cells.")]
    public bool animateStarMotion = true;

    [Tooltip("Very slow world-space drift applied to each complete star layer. Each layer gets a small deterministic variation so they do not move as one rigid sheet.")]
    public Vector2 layerDriftVelocity = new Vector2(0.006f, 0.002f);

    [Min(0f)]
    [Tooltip("Small world-space side-to-side movement of each complete layer.")]
    public float layerWiggleAmount = 0.035f;

    [Min(0f)]
    [Tooltip("Speed of the whole-layer wiggle. Kept deliberately slow by default.")]
    public float layerWiggleSpeed = 0.18f;

    [Range(0f, 1f)]
    [Tooltip("How differently the separate depth layers drift/wiggle. 0 = identical motion, 1 = strong variation.")]
    public float layerMotionVariation = 0.30f;

    [Min(0f)]
    [Tooltip("Tiny local movement of each individual star inside its Tilemap cell. This is in cell-local units, so values around 0.01-0.05 are subtle.")]
    public float individualStarWiggleAmount = 0.025f;

    [Min(0f)]
    [Tooltip("Speed of the individual star wiggle.")]
    public float individualStarWiggleSpeed = 0.28f;

    [Min(0f)]
    [Tooltip("Small extra rotation in degrees applied while a star wiggles. The star still keeps its original random 0/90/180/270 degree base rotation.")]
    public float individualStarRotationWiggle = 1.5f;

    [Range(0f, 1f)]
    [Tooltip("Random per-star variation in wiggle amount and speed.")]
    public float individualStarMotionVariation = 0.35f;

    [Header("Stars")]
    [Tooltip("Brightness levels, index 0 = the single centrepiece star used per cluster. The rest are the 'lesser' levels scattered around it.")]
    public BrightnessLevel[] brightnessLevels = new BrightnessLevel[5];
    [Tooltip("Index into brightnessLevels used as each cluster's single centrepiece star.")]
    [Min(0)] public int brightestLevelIndex = 0;
    [Tooltip("How many of EACH remaining (non-centrepiece) brightness level to scatter around a cluster's centre - rolled separately per level.")]
    public Vector2Int starsPerLesserLevelRange = new Vector2Int(2, 4);
    [Tooltip("How far from a cluster's centre (in cells) its lesser stars can land.")]
    [Min(1)] public int clusterRadius = 3;
    [Tooltip("Minimum spacing (in cells) enforced between cluster centres on the same layer, so clusters don't stack on top of each other.")]
    [Min(1)] public int minClusterSpacing = 10;

    [Header("Cluster Shape (noise - shared across layers)")]
    [Tooltip("Bigger = bigger, smoother cluster regions. Smaller = tighter, noisier clumping.")]
    public float clusterNoiseScale = 0.08f;
    [Tooltip("Cells below this noise value are never eligible to become a cluster centre.")]
    [Range(0f, 1f)] public float clusterThreshold = 0.45f;
    [Tooltip("How strongly the noise field biases cluster-centre placement above the threshold.")]
    public float clusterInfluence = 2f;

    [Header("Appearance")]
    [Range(0f, 1f)]
    [Tooltip("Global opacity applied to every configured starfield Tilemap layer. 1 = fully visible, 0 = invisible.")]
    public float starfieldOpacity = 1f;

    [Header("Rotation")]
    [Tooltip("Allowed rotation steps in degrees for placed star tiles, or leave just {0} to disable.")]
    public float[] allowedRotations = { 0f, 90f, 180f, 270f };

    [Header("Black Holes")]
    [Tooltip("Black holes use a separate runtime Tilemap, so regenerating a star layer cannot erase them.")]
    public BlackHoleTiles blackHoleTiles;

    [Tooltip("How many black holes to place inside the shared Origin/Size area.")]
    [Min(0)] public int blackHoleCount = 1;

    [Tooltip("Minimum spacing in cells between black-hole bottom-left positions.")]
    [Min(2)] public int blackHoleMinSpacing = 15;

    [Tooltip("Which star depth layer the separate black-hole Tilemap follows for parallax/drift. " +
             "The index is clamped automatically if the layer list changes.")]
    [Min(0)] public int blackHoleParallaxLayerIndex = 0;

    [Tooltip("Generate the separate black-hole Tilemap and place black holes.")]
    public bool placeBlackHoles = true;

    [Header("Seeding")]
    public int seed = 12345;
    [Tooltip("If true, randomises the seed on Awake instead of using the value above.")]
    public bool randomizeSeedOnAwake = false;

    [Header("Debug")]
    [Tooltip("If true, automatically calls Paint() when you press Play - no need to use the context menu.")]
    public bool paintOnStart = true;

    private System.Random _rng;
    private float _noiseOffsetX;
    private float _noiseOffsetY;
    private readonly List<BlackHoleInstance> _blackHoles = new List<BlackHoleInstance>();
    private readonly List<StarInstance> _stars = new List<StarInstance>();
    private readonly List<LayerMotionState> _layerMotionStates = new List<LayerMotionState>();

    // Tracks generated star Tilemaps so list entries can be added/removed safely at runtime.
    private readonly List<Tilemap> _generatedStarTilemaps = new List<Tilemap>();

    // Black holes deliberately have their own Tilemap. In the older layout they were painted onto
    // one star layer and were then erased when GenerateLayer() cleared that same Tilemap.
    private Tilemap _blackHoleTilemap;

    private bool _missingGridErrorLogged;
    private bool _hasReference;
    private Vector3 _smoothedReferencePosition;
    private Vector3 _referenceStartPosition;
    private Vector3 _referenceSmoothVelocity;
    private float _motionStartTime;

    private void Awake()
    {
        if (randomizeSeedOnAwake)
            seed = System.Guid.NewGuid().GetHashCode();

        // Data/reference setup is safe in Awake.
        // Runtime Tilemap GameObjects are deliberately NOT created here because
        // AddComponent can trigger Unity's OnDidAddComponent SendMessage while
        // Unity is still inside Awake/consistency checks.
        InitialiseReferenceTracking();
        _motionStartTime = Time.time;
    }

    private void Start()
    {
        // Start is the safe point for creating the generated runtime hierarchy.
        EnsureRuntimeTilemaps();
        InitialiseLayerMotionStates();
        ApplyStarfieldOpacity();

        if (paintOnStart)
            Paint();
    }

    private void OnDestroy()
    {
        // In Play mode the manager hierarchy is destroyed by Unity automatically.
        // This explicit cleanup mainly covers editor context-menu previews.
        if (Application.isPlaying)
            return;

        for (int i = _generatedStarTilemaps.Count - 1; i >= 0; i--)
        {
            Tilemap tilemap = _generatedStarTilemaps[i];

            if (tilemap != null)
                DestroyGeneratedLayerObject(tilemap.gameObject);
        }

        _generatedStarTilemaps.Clear();

        if (_blackHoleTilemap != null)
        {
            DestroyGeneratedLayerObject(_blackHoleTilemap.gameObject);
            _blackHoleTilemap = null;
        }
    }

    private void LateUpdate()
    {
        if (layers == null)
            return;

        EnsureRuntimeTilemaps();
        EnsureRuntimeMotionState();

        float deltaTime = Time.deltaTime;
        float motionTime = Time.time - _motionStartTime;

        Vector3 referenceTravel = UpdateSmoothedReference(deltaTime);
        UpdateLayerMotion(referenceTravel, motionTime);
        UpdateIndividualStarMotion(motionTime);
    }

    // ==================== Runtime Tilemaps ====================

    /// <summary>
    /// Confirms that the manager lives somewhere beneath a Unity Grid.
    /// Generated Tilemaps are children of this manager, while the Grid remains
    /// the common layout ancestor for the complete hierarchy.
    /// </summary>
    private bool ValidateGridParent()
    {
        if (GetComponentInParent<Grid>() != null)
        {
            _missingGridErrorLogged = false;
            return true;
        }

        if (!_missingGridErrorLogged)
        {
            Debug.LogError(
                $"{name}: StarfieldPainter must be parented underneath a GameObject with a Grid component " +
                "before it can create runtime Tilemaps.",
                this);

            _missingGridErrorLogged = true;
        }

        return false;
    }

    /// <summary>
    /// Creates missing star depth Tilemaps, removes Tilemaps belonging to deleted list entries,
    /// and maintains the optional separate black-hole Tilemap.
    /// </summary>
    private void EnsureRuntimeTilemaps()
    {
        if (layers == null || !ValidateGridParent())
            return;

        var activeStarTilemaps = new HashSet<Tilemap>();

        for (int i = 0; i < layers.Count; i++)
        {
            StarLayer layer = layers[i];

            if (layer == null)
                continue;

            if (layer.tilemap == null)
            {
                layer.tilemap = CreateRuntimeStarTilemap(i);

                if (layer.tilemap != null)
                    _generatedStarTilemaps.Add(layer.tilemap);
            }

            if (layer.tilemap == null)
                continue;

            activeStarTilemaps.Add(layer.tilemap);
            UpdateRuntimeStarLayerName(layer, i);
            ApplyStarLayerRendererSettings(layer, i);
        }

        // A removed list entry leaves behind a generated Tilemap reference in our ownership list.
        // Anything no longer referenced by a current StarLayer is safe to destroy.
        for (int i = _generatedStarTilemaps.Count - 1; i >= 0; i--)
        {
            Tilemap generated = _generatedStarTilemaps[i];

            if (generated == null)
            {
                _generatedStarTilemaps.RemoveAt(i);
                continue;
            }

            if (activeStarTilemaps.Contains(generated))
                continue;

            DestroyGeneratedLayerObject(generated.gameObject);
            _generatedStarTilemaps.RemoveAt(i);
        }

        if (placeBlackHoles)
        {
            EnsureBlackHoleTilemap();
            ApplyBlackHoleRendererSettings();
        }
        else if (_blackHoleTilemap != null)
        {
            // Keep the hierarchy honest when black holes are disabled at runtime.
            DestroyGeneratedLayerObject(_blackHoleTilemap.gameObject);
            _blackHoleTilemap = null;
            _blackHoles.Clear();
        }
    }

    /// <summary>
    /// Creates one runtime star depth Tilemap underneath this manager.
    /// </summary>
    private Tilemap CreateRuntimeStarTilemap(int layerIndex)
    {
        return CreateRuntimeTilemapObject(
            $"{name} - Star Layer {layerIndex + 1}"
        );
    }

    /// <summary>
    /// Creates the dedicated black-hole Tilemap if it does not exist yet.
    /// It is intentionally independent of every star depth Tilemap.
    /// </summary>
    private void EnsureBlackHoleTilemap()
    {
        if (_blackHoleTilemap != null)
            return;

        _blackHoleTilemap =
            CreateRuntimeTilemapObject($"{name} - Black Holes");
    }

    /// <summary>
    /// Shared runtime Tilemap constructor used by stars and black holes.
    /// </summary>
    private Tilemap CreateRuntimeTilemapObject(string objectName)
    {
        GameObject layerObject = new GameObject(objectName);

        layerObject.transform.SetParent(transform, worldPositionStays: false);
        layerObject.transform.localPosition = Vector3.zero;
        layerObject.transform.localRotation = Quaternion.identity;
        layerObject.transform.localScale = Vector3.one;

        // Context-menu painting is still useful outside Play mode. These preview objects must
        // never become persistent scene children simply because Paint Stars was clicked.
        if (!Application.isPlaying)
            layerObject.hideFlags |= HideFlags.DontSaveInEditor;

        Tilemap tilemap = layerObject.AddComponent<Tilemap>();
        layerObject.AddComponent<TilemapRenderer>();

        return tilemap;
    }

    /// <summary>
    /// Generated star layer names are derived from list order, so labels are not required.
    /// </summary>
    private void UpdateRuntimeStarLayerName(StarLayer layer, int layerIndex)
    {
        if (layer?.tilemap == null)
            return;

        layer.tilemap.gameObject.name =
            $"{name} - Star Layer {layerIndex + 1}";
    }

    /// <summary>
    /// Destroys a generated child correctly in both Play mode and editor context-menu use.
    /// </summary>
    private void DestroyGeneratedLayerObject(GameObject layerObject)
    {
        if (layerObject == null)
            return;

        if (Application.isPlaying)
            Destroy(layerObject);
        else
            DestroyImmediate(layerObject);
    }

    /// <summary>
    /// Applies shared Sorting Layer plus an automatically-derived sorting order.
    ///
    /// List index 0 = startingSortingOrder
    /// List index 1 = startingSortingOrder + 1
    /// etc.
    /// </summary>
    private void ApplyStarLayerRendererSettings(StarLayer layer, int layerIndex)
    {
        if (layer?.tilemap == null)
            return;

        TilemapRenderer renderer =
            layer.tilemap.GetComponent<TilemapRenderer>();

        if (renderer == null)
            renderer = layer.tilemap.gameObject.AddComponent<TilemapRenderer>();

        renderer.sortingLayerName =
            ResolveSortingLayerName(sortingLayerName);

        renderer.sortingOrder =
            startingSortingOrder + layerIndex;
    }

    /// <summary>
    /// Black holes render one order above the highest configured star depth layer.
    /// They still follow the motion of the selected parallax layer.
    /// </summary>
    private void ApplyBlackHoleRendererSettings()
    {
        if (_blackHoleTilemap == null)
            return;

        TilemapRenderer renderer =
            _blackHoleTilemap.GetComponent<TilemapRenderer>();

        if (renderer == null)
            renderer = _blackHoleTilemap.gameObject.AddComponent<TilemapRenderer>();

        renderer.sortingLayerName =
            ResolveSortingLayerName(sortingLayerName);

        renderer.sortingOrder =
            startingSortingOrder + (layers?.Count ?? 0);
    }

    /// <summary>
    /// Falls back to Default if a configured Sorting Layer name does not exist.
    /// </summary>
    private string ResolveSortingLayerName(string configuredSortingLayer)
    {
        foreach (SortingLayer sortingLayer in SortingLayer.layers)
        {
            if (sortingLayer.name == configuredSortingLayer)
                return configuredSortingLayer;
        }

        Debug.LogWarning(
            $"{name}: Sorting Layer \"{configuredSortingLayer}\" does not exist. Falling back to \"Default\".",
            this);

        return "Default";
    }

    // ==================== Runtime movement ====================

    /// <summary>
    /// Captures the current reference as the parallax origin. The smoothed
    /// reference starts at the real position so there is no startup jump.
    /// </summary>
    private void InitialiseReferenceTracking()
    {
        if (referenceTransform == null && Camera.main != null)
            referenceTransform = Camera.main.transform;

        _hasReference = referenceTransform != null;
        _referenceSmoothVelocity = Vector3.zero;

        if (_hasReference)
        {
            _smoothedReferencePosition = referenceTransform.position;
            _referenceStartPosition = _smoothedReferencePosition;
        }
        else
        {
            _smoothedReferencePosition = Vector3.zero;
            _referenceStartPosition = Vector3.zero;
        }
    }

    /// <summary>
    /// Saves the original world-space transform of every layer and gives each
    /// layer deterministic phases/variation for the ambient drift.
    /// </summary>
    private void InitialiseLayerMotionStates()
    {
        _layerMotionStates.Clear();

        if (layers == null)
            return;

        var motionRng = new System.Random(seed ^ 0x3465A17);

        for (int i = 0; i < layers.Count; i++)
        {
            StarLayer layer = layers[i];
            Tilemap tilemap = layer?.tilemap;

            _layerMotionStates.Add(new LayerMotionState
            {
                tilemap = tilemap,
                baseWorldPosition = tilemap != null ? tilemap.transform.position : Vector3.zero,
                phaseX = (float)motionRng.NextDouble() * Mathf.PI * 2f,
                phaseY = (float)motionRng.NextDouble() * Mathf.PI * 2f,
                motionVariation = ((float)motionRng.NextDouble() * 2f) - 1f
            });
        }
    }

    /// <summary>
    /// Handles runtime Inspector changes to the layer list without requiring
    /// the component to be restarted. Existing layer positions become the new
    /// motion origins when the list itself changes.
    /// </summary>
    private void EnsureRuntimeMotionState()
    {
        bool layerStateInvalid = _layerMotionStates.Count != (layers?.Count ?? 0);

        if (!layerStateInvalid && layers != null)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                Tilemap current = layers[i]?.tilemap;
                if (_layerMotionStates[i].tilemap != current)
                {
                    layerStateInvalid = true;
                    break;
                }
            }
        }

        if (layerStateInvalid)
            InitialiseLayerMotionStates();

        bool referenceAvailable = referenceTransform != null;
        if (referenceAvailable != _hasReference)
            InitialiseReferenceTracking();
    }

    /// <summary>
    /// Low-pass filters the reference position before parallax is calculated.
    /// A grid-moving reference can therefore jump cell-to-cell while the
    /// starfield receives a smooth continuous trajectory.
    /// </summary>
    private Vector3 UpdateSmoothedReference(float deltaTime)
    {
        if (!_hasReference || referenceTransform == null)
            return Vector3.zero;

        Vector3 target = referenceTransform.position;

        if (referenceAverageTime <= 0f || deltaTime <= 0f)
        {
            _smoothedReferencePosition = target;
            _referenceSmoothVelocity = Vector3.zero;
        }
        else
        {
            _smoothedReferencePosition = Vector3.SmoothDamp(
                _smoothedReferencePosition,
                target,
                ref _referenceSmoothVelocity,
                referenceAverageTime,
                Mathf.Infinity,
                deltaTime);
        }

        return _smoothedReferencePosition - _referenceStartPosition;
    }

    /// <summary>
    /// Updates complete Tilemap layers using absolute offsets. This avoids
    /// accumulating numerical error and means changing wiggle settings does
    /// not gradually push a layer away from its intended position.
    /// </summary>
    private void UpdateLayerMotion(Vector3 referenceTravel, float motionTime)
    {
        if (layers == null)
            return;

        int count = Mathf.Min(layers.Count, _layerMotionStates.Count);

        for (int i = 0; i < count; i++)
        {
            StarLayer layer = layers[i];
            LayerMotionState state = _layerMotionStates[i];

            if (layer?.tilemap == null || state.tilemap == null)
                continue;

            Vector3 parallaxOffset = _hasReference
                ? -referenceTravel * layer.parallaxFactor
                : Vector3.zero;

            Vector3 ambientOffset = Vector3.zero;

            if (animateStarMotion)
            {
                float variationScale = Mathf.Max(0.05f, 1f + state.motionVariation * layerMotionVariation);

                Vector2 drift = layerDriftVelocity * (motionTime * variationScale);

                // Subtract the phase's t=0 value so enabling the component does
                // not cause an immediate positional jump.
                float wiggleX = 0.5f * (
                    Mathf.Sin(motionTime * layerWiggleSpeed * variationScale + state.phaseX) -
                    Mathf.Sin(state.phaseX));

                float wiggleY = 0.5f * (
                    Mathf.Sin(motionTime * layerWiggleSpeed * 0.83f * variationScale + state.phaseY) -
                    Mathf.Sin(state.phaseY));

                ambientOffset = new Vector3(
                    drift.x + wiggleX * layerWiggleAmount * variationScale,
                    drift.y + wiggleY * layerWiggleAmount * variationScale,
                    0f);
            }

            layer.tilemap.transform.position =
                state.baseWorldPosition + parallaxOffset + ambientOffset;
        }

        UpdateBlackHoleMotion();
    }

    /// <summary>
    /// Keeps the dedicated black-hole Tilemap aligned with one selected depth layer.
    ///
    /// This preserves the old behaviour where black holes lived on a chosen star layer,
    /// but without sharing that layer's tile storage.
    /// </summary>
    private void UpdateBlackHoleMotion()
    {
        if (_blackHoleTilemap == null)
            return;

        if (layers == null || layers.Count == 0)
        {
            _blackHoleTilemap.transform.localPosition = Vector3.zero;
            return;
        }

        int layerIndex =
            Mathf.Clamp(blackHoleParallaxLayerIndex, 0, layers.Count - 1);

        Tilemap sourceTilemap =
            layers[layerIndex]?.tilemap;

        if (sourceTilemap == null)
        {
            _blackHoleTilemap.transform.localPosition = Vector3.zero;
            return;
        }

        _blackHoleTilemap.transform.position =
            sourceTilemap.transform.position;
    }

    /// <summary>
    /// Gives every star a tiny independent wobble inside its existing Tilemap
    /// cell. This changes only the tile transform matrix; no tiles are removed
    /// or regenerated and cluster layout remains unchanged.
    /// </summary>
    private void UpdateIndividualStarMotion(float motionTime)
    {
        for (int i = 0; i < _stars.Count; i++)
        {
            StarInstance star = _stars[i];
            if (star.tilemap == null || !star.tilemap.HasTile(star.cell))
                continue;

            float amountScale = Mathf.Max(0.05f, 1f + star.motionVariation * individualStarMotionVariation);
            float speedScale = Mathf.Max(0.05f, 1f + star.speedVariation * individualStarMotionVariation);

            float x = 0f;
            float y = 0f;
            float angleOffset = 0f;

            if (animateStarMotion)
            {
                float speed = individualStarWiggleSpeed * speedScale;

                x = 0.5f * (
                    Mathf.Sin(motionTime * speed + star.phaseX) -
                    Mathf.Sin(star.phaseX));

                y = 0.5f * (
                    Mathf.Sin(motionTime * speed * 0.79f + star.phaseY) -
                    Mathf.Sin(star.phaseY));

                angleOffset = 0.5f * (
                    Mathf.Sin(motionTime * speed * 0.63f + star.phaseRotation) -
                    Mathf.Sin(star.phaseRotation));

                x *= individualStarWiggleAmount * amountScale;
                y *= individualStarWiggleAmount * amountScale;
                angleOffset *= individualStarRotationWiggle * amountScale;
            }

            Matrix4x4 transformMatrix = Matrix4x4.TRS(
                new Vector3(x, y, 0f),
                Quaternion.Euler(0f, 0f, star.baseAngle + angleOffset),
                Vector3.one);

            star.tilemap.SetTransformMatrix(star.cell, transformMatrix);
        }
    }

    // ==================== Appearance ====================

    /// <summary>
    /// Sets the opacity of every configured starfield layer.
    /// This changes the Tilemap tint alpha, so every tile on those layers is affected together.
    /// </summary>
    public void SetStarfieldOpacity(float opacity)
    {
        starfieldOpacity = Mathf.Clamp01(opacity);
        ApplyStarfieldOpacity();
    }

    /// <summary>Applies the current global opacity value to every configured starfield Tilemap.</summary>
    [ContextMenu("Apply Starfield Opacity")]
    public void ApplyStarfieldOpacity()
    {
        starfieldOpacity = Mathf.Clamp01(starfieldOpacity);

        if (layers != null)
        {
            foreach (StarLayer layer in layers)
            {
                if (layer?.tilemap == null)
                    continue;

                Color tint = layer.tilemap.color;
                tint.a = starfieldOpacity;
                layer.tilemap.color = tint;
            }
        }

        if (_blackHoleTilemap != null)
        {
            Color blackHoleTint = _blackHoleTilemap.color;
            blackHoleTint.a = starfieldOpacity;
            _blackHoleTilemap.color = blackHoleTint;
        }
    }

    private void OnValidate()
    {
        starfieldOpacity = Mathf.Clamp01(starfieldOpacity);
        referenceAverageTime = Mathf.Max(0f, referenceAverageTime);
        layerWiggleAmount = Mathf.Max(0f, layerWiggleAmount);
        layerWiggleSpeed = Mathf.Max(0f, layerWiggleSpeed);
        individualStarWiggleAmount = Mathf.Max(0f, individualStarWiggleAmount);
        individualStarWiggleSpeed = Mathf.Max(0f, individualStarWiggleSpeed);
        individualStarRotationWiggle = Mathf.Max(0f, individualStarRotationWiggle);

        size.x = Mathf.Max(1, size.x);
        size.y = Mathf.Max(1, size.y);

        if (layers != null)
        {
            for (int i = 0; i < layers.Count; i++)
                ApplyStarLayerRendererSettings(layers[i], i);
        }

        ApplyBlackHoleRendererSettings();
        ApplyStarfieldOpacity();
    }

    // ==================== Generation ====================

    /// <summary>Clears and regenerates every layer plus the black holes, using the current seed/settings.</summary>
    [ContextMenu("Paint Stars")]
    public void Paint()
    {
        EnsureRuntimeTilemaps();

        Debug.Log($"{name}: Paint() called across {(layers?.Count ?? 0)} star depth layers.", this);

        // A repaint creates a new deterministic set of star instances, so the
        // runtime wiggle registry must be rebuilt at the same time.
        _stars.Clear();

        // Place black holes first so their occupied cells are known before star generation.
        // They now live on a separate Tilemap, so the following star-layer clears cannot erase them.
        PlaceBlackHoles();

        if (layers == null || layers.Count == 0)
        {
            Debug.LogWarning($"{name}: no star depth layers configured. Black holes can still be generated independently.", this);
            return;
        }

        if (brightnessLevels == null || brightnessLevels.Length == 0 ||
            brightestLevelIndex < 0 ||
            brightestLevelIndex >= brightnessLevels.Length ||
            brightnessLevels[brightestLevelIndex]?.tile == null)
        {
            Debug.LogWarning($"{name}: brightnessLevels[{brightestLevelIndex}] (the centrepiece level) has no tile assigned.", this);
            return;
        }

        for (int i = 0; i < layers.Count; i++)
        {
            int layerSeed = seed + i * 7919; // large prime offset so each layer's pattern diverges
            GenerateLayer(layers[i], i, layerSeed);
        }
    }

    [ContextMenu("Clear All Layers")]
    public void ClearAllLayers()
    {
        if (layers != null)
        {
            foreach (StarLayer layer in layers)
                ClearLayer(layer);
        }

        ClearBlackHoleTilemap();
        _blackHoles.Clear();
    }

    private void ClearLayer(StarLayer layer)
    {
        if (layer?.tilemap == null)
            return;

        // ClearAllTiles is appropriate here because this Tilemap belongs only to this manager.
        layer.tilemap.ClearAllTiles();
    }

    private void ClearBlackHoleTilemap()
    {
        if (_blackHoleTilemap != null)
            _blackHoleTilemap.ClearAllTiles();
    }

    private void GenerateLayer(StarLayer layer, int layerIndex, int layerSeed)
    {
        if (layer == null || layer.tilemap == null)
        {
            Debug.LogWarning($"{name}: runtime Tilemap for layer index {layerIndex} could not be created - skipping.", this);
            return;
        }

        _rng = new System.Random(layerSeed);
        _noiseOffsetX = (float)_rng.NextDouble() * 10000f;
        _noiseOffsetY = (float)_rng.NextDouble() * 10000f;

        ClearLayer(layer);

        var centers = new List<Vector3Int>();

        for (int x = 0; x < size.x; x++)
        {
            for (int y = 0; y < size.y; y++)
            {
                Vector3Int cell = origin + new Vector3Int(x, y, 0);
                if (IsCellBlockedByBlackHole(cell))
                    continue;

                float cluster = ClusterNoise(cell.x, cell.y);
                if (cluster < clusterThreshold)
                    continue;

                float clusterStrength = Mathf.InverseLerp(clusterThreshold, 1f, cluster);
                float chance = layer.density * Mathf.Pow(clusterStrength, clusterInfluence == 0 ? 1f : 1f / Mathf.Max(0.01f, clusterInfluence));

                if (RandomFloat() > chance)
                    continue;

                bool tooClose = false;
                foreach (var existing in centers)
                {
                    int dx = existing.x - cell.x;
                    int dy = existing.y - cell.y;
                    if (dx * dx + dy * dy < minClusterSpacing * minClusterSpacing)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose)
                    continue;

                centers.Add(cell);
            }
        }

        foreach (var center in centers)
            PlaceCluster(center, layer, layerIndex);

        Debug.Log($"{name}: star layer {layerIndex} placed {centers.Count} clusters.", this);
    }

    private void PlaceCluster(Vector3Int centerCell, StarLayer layer, int layerIndex)
    {
        var brightest = brightnessLevels[brightestLevelIndex];
        if (brightest != null && brightest.tile != null && !IsCellBlockedByBlackHole(centerCell))
            PlaceStar(centerCell, brightest.tile, layer.tilemap);

        for (int levelIndex = 0; levelIndex < brightnessLevels.Length; levelIndex++)
        {
            if (levelIndex == brightestLevelIndex) continue;

            var level = brightnessLevels[levelIndex];
            if (level == null || level.tile == null) continue;

            int countThisLevel = _rng.Next(starsPerLesserLevelRange.x, starsPerLesserLevelRange.y + 1);

            for (int i = 0; i < countThisLevel; i++)
            {
                Vector3Int cell = centerCell + RandomOffsetWithinRadius(clusterRadius);

                if (IsCellBlockedByBlackHole(cell)) continue;
                if (layer.tilemap.GetTile(cell) != null) continue; // don't stomp a tile already placed by this cluster

                PlaceStar(cell, level.tile, layer.tilemap);
            }
        }
    }

    private Vector3Int RandomOffsetWithinRadius(int radius)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int dx = _rng.Next(-radius, radius + 1);
            int dy = _rng.Next(-radius, radius + 1);
            if (dx * dx + dy * dy <= radius * radius)
                return new Vector3Int(dx, dy, 0);
        }
        return Vector3Int.zero;
    }

    /// <summary>
    /// Places one star with its normal random base rotation and registers the
    /// extra deterministic phases used by the slow runtime wobble.
    /// </summary>
    private void PlaceStar(Vector3Int cell, Tile tile, Tilemap tilemap)
    {
        float angle = RandomRotationAngle();

        tilemap.SetTile(cell, tile);
        tilemap.SetTransformMatrix(
            cell,
            Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(0f, 0f, angle), Vector3.one));

        // _rng is seeded per layer, so repainting with the same seed produces
        // the same stars, rotations and motion phases.
        _stars.Add(new StarInstance
        {
            tilemap = tilemap,
            cell = cell,
            baseAngle = angle,
            phaseX = (float)_rng.NextDouble() * Mathf.PI * 2f,
            phaseY = (float)_rng.NextDouble() * Mathf.PI * 2f,
            phaseRotation = (float)_rng.NextDouble() * Mathf.PI * 2f,
            motionVariation = ((float)_rng.NextDouble() * 2f) - 1f,
            speedVariation = ((float)_rng.NextDouble() * 2f) - 1f
        });
    }

    private float RandomRotationAngle()
    {
        if (allowedRotations == null || allowedRotations.Length == 0)
            return 0f;

        return allowedRotations[_rng.Next(0, allowedRotations.Length)];
    }

    private float ClusterNoise(int cellX, int cellY)
    {
        float sampleX = (cellX * clusterNoiseScale) + _noiseOffsetX;
        float sampleY = (cellY * clusterNoiseScale) + _noiseOffsetY;
        return Mathf.PerlinNoise(sampleX, sampleY);
    }

    private float RandomFloat()
    {
        return (float)_rng.NextDouble();
    }

    // ==================== Black Holes ====================

    /// <summary>
    /// Rebuilds black-hole placement on the dedicated runtime Tilemap.
    ///
    /// Black holes used to be drawn directly onto one star Tilemap. Paint() then called
    /// GenerateLayer(), which cleared that Tilemap and could erase the black holes immediately.
    /// Keeping them separate fixes that lifecycle problem completely.
    /// </summary>
    [ContextMenu("Place Black Holes")]
    public void PlaceBlackHoles()
    {
        _blackHoles.Clear();

        if (!placeBlackHoles || blackHoleCount <= 0)
        {
            ClearBlackHoleTilemap();
            return;
        }

        EnsureRuntimeTilemaps();
        EnsureBlackHoleTilemap();
        ApplyBlackHoleRendererSettings();
        ClearBlackHoleTilemap();

        if (_blackHoleTilemap == null)
        {
            Debug.LogWarning($"{name}: black-hole Tilemap could not be created.", this);
            return;
        }

        if (blackHoleTiles == null ||
            blackHoleTiles.topLeft == null ||
            blackHoleTiles.topRight == null ||
            blackHoleTiles.bottomLeft == null ||
            blackHoleTiles.bottomRight == null)
        {
            Debug.LogWarning(
                $"{name}: PlaceBlackHoles requires all four black-hole tiles.",
                this);

            return;
        }

        if (size.x < 2 || size.y < 2)
        {
            Debug.LogWarning(
                $"{name}: shared starfield Size must be at least 2 x 2 to place a 2 x 2 black hole.",
                this);

            return;
        }

        var bhRng =
            new System.Random(seed ^ 0x5A5A5A5A);

        int attempts = 0;
        int maxAttempts = Mathf.Max(50, blackHoleCount * 100);

        // For a 2x2 object, the bottom-left cell can range from 0 through size-2.
        // System.Random's upper bound is exclusive, hence Next(0, size - 1).
        while (_blackHoles.Count < blackHoleCount &&
               attempts < maxAttempts)
        {
            attempts++;

            int x =
                bhRng.Next(0, size.x - 1);

            int y =
                bhRng.Next(0, size.y - 1);

            Vector3Int candidate =
                origin + new Vector3Int(x, y, 0);

            if (IsTooCloseToExistingBlackHole(candidate))
                continue;

            _blackHoles.Add(
                new BlackHoleInstance
                {
                    bottomLeftCell = candidate
                });
        }

        foreach (BlackHoleInstance blackHole in _blackHoles)
            DrawBlackHoleInstance(blackHole);

        UpdateBlackHoleMotion();

        Debug.Log(
            $"{name}: placed {_blackHoles.Count}/{blackHoleCount} black holes on the dedicated black-hole Tilemap.",
            this);

        if (_blackHoles.Count < blackHoleCount)
        {
            Debug.LogWarning(
                $"{name}: only managed to place {_blackHoles.Count}/{blackHoleCount} black holes. " +
                "Try a smaller Black Hole Min Spacing or a larger shared Size.",
                this);
        }
    }

    /// <summary>
    /// Checks spacing using black-hole bottom-left cells.
    /// </summary>
    private bool IsTooCloseToExistingBlackHole(Vector3Int candidate)
    {
        int minimumSpacing =
            Mathf.Max(2, blackHoleMinSpacing);

        int minimumSpacingSqr =
            minimumSpacing * minimumSpacing;

        foreach (BlackHoleInstance existing in _blackHoles)
        {
            int dx =
                existing.bottomLeftCell.x - candidate.x;

            int dy =
                existing.bottomLeftCell.y - candidate.y;

            if (dx * dx + dy * dy < minimumSpacingSqr)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Draws one 2x2 black hole onto the dedicated black-hole Tilemap.
    /// </summary>
    private void DrawBlackHoleInstance(BlackHoleInstance blackHole)
    {
        if (_blackHoleTilemap == null)
            return;

        _blackHoleTilemap.SetTile(
            blackHole.bottomLeftCell,
            blackHoleTiles.bottomLeft);

        _blackHoleTilemap.SetTile(
            blackHole.bottomLeftCell + new Vector3Int(1, 0, 0),
            blackHoleTiles.bottomRight);

        _blackHoleTilemap.SetTile(
            blackHole.bottomLeftCell + new Vector3Int(0, 1, 0),
            blackHoleTiles.topLeft);

        _blackHoleTilemap.SetTile(
            blackHole.bottomLeftCell + new Vector3Int(1, 1, 0),
            blackHoleTiles.topRight);

        foreach (Vector3Int cell in GetBlackHoleCellsInstance(blackHole))
        {
            _blackHoleTilemap.SetTransformMatrix(
                cell,
                Matrix4x4.identity);
        }
    }

    private Vector3Int[] GetBlackHoleCellsInstance(
        BlackHoleInstance blackHole)
    {
        return new[]
        {
            blackHole.bottomLeftCell,
            blackHole.bottomLeftCell + new Vector3Int(1, 0, 0),
            blackHole.bottomLeftCell + new Vector3Int(0, 1, 0),
            blackHole.bottomLeftCell + new Vector3Int(1, 1, 0)
        };
    }

    private bool IsCellInBlackHoleInstance(
        BlackHoleInstance blackHole,
        Vector3Int cell)
    {
        return
            cell.x >= blackHole.bottomLeftCell.x &&
            cell.x <= blackHole.bottomLeftCell.x + 1 &&
            cell.y >= blackHole.bottomLeftCell.y &&
            cell.y <= blackHole.bottomLeftCell.y + 1;
    }

    /// <summary>
    /// Stars on every depth layer avoid black-hole cells.
    ///
    /// Previously only the layer that physically contained the black-hole tiles was blocked.
    /// Since black holes now render independently above the star stack, keeping all four cells
    /// clear on every depth prevents stars showing through transparent parts of the artwork.
    /// </summary>
    private bool IsCellBlockedByBlackHole(Vector3Int cell)
    {
        foreach (BlackHoleInstance blackHole in _blackHoles)
        {
            if (IsCellInBlackHoleInstance(blackHole, cell))
                return true;
        }

        return false;
    }

    /// <summary>How many black holes are currently placed.</summary>
    public int BlackHoleCount =>
        _blackHoles.Count;

    /// <summary>
    /// The four cells occupied by a black hole:
    /// bottom-left, bottom-right, top-left, top-right.
    /// </summary>
    public Vector3Int[] GetBlackHoleCells(int index)
    {
        if (index < 0 || index >= _blackHoles.Count)
            return System.Array.Empty<Vector3Int>();

        return GetBlackHoleCellsInstance(
            _blackHoles[index]);
    }

    /// <summary>True if the given cell belongs to any placed black hole.</summary>
    public bool IsCellInAnyBlackHole(Vector3Int cell)
    {
        return IsCellBlockedByBlackHole(cell);
    }

    /// <summary>Finds which black hole a cell belongs to, if any.</summary>
    public bool TryGetBlackHoleIndexAt(
        Vector3Int cell,
        out int index)
    {
        for (int i = 0; i < _blackHoles.Count; i++)
        {
            if (!IsCellInBlackHoleInstance(_blackHoles[i], cell))
                continue;

            index = i;
            return true;
        }

        index = -1;
        return false;
    }

    /// <summary>
    /// Finds which black hole a world-space position falls inside, if any.
    /// Uses the dedicated Tilemap so the query follows black-hole parallax correctly.
    /// </summary>
    public bool IsWorldPositionInAnyBlackHole(
        Vector3 worldPosition,
        out int index)
    {
        index = -1;

        if (_blackHoleTilemap == null)
            return false;

        Vector3Int cell =
            _blackHoleTilemap.WorldToCell(worldPosition);

        return TryGetBlackHoleIndexAt(
            cell,
            out index);
    }

    /// <summary>
    /// Geometric centre of a black hole's 2x2 footprint in world space.
    /// </summary>
    public Vector3 GetBlackHoleWorldCenter(int index)
    {
        if (index < 0 ||
            index >= _blackHoles.Count ||
            _blackHoleTilemap == null)
        {
            return transform.position;
        }

        BlackHoleInstance blackHole =
            _blackHoles[index];

        Vector3 bottomLeftCentre =
            _blackHoleTilemap.GetCellCenterWorld(
                blackHole.bottomLeftCell);

        Vector3 topRightCentre =
            _blackHoleTilemap.GetCellCenterWorld(
                blackHole.bottomLeftCell +
                new Vector3Int(1, 1, 0));

        return
            (bottomLeftCentre + topRightCentre) * 0.5f;
    }

}