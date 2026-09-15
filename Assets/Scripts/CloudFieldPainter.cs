using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Paints and animates a field containing multiple fluid-like pixel clouds.
///
/// The important separation is:
///
///     hidden skeleton graphs (rings of joints connected by structural edges)
///             -> capsule "skin" distance surface along each edge
///             -> exact pixel masks
///             -> 8x8 Tilemap slices
///
/// Each cloud is a small closed ring of joints (like a soft, inflated polygon).
/// Structural edges connect adjacent joints with a spring plus a hard min/max
/// distance clamp, so the ring can bulge, pinch and rotate but never collapses
/// to a point or flies apart. The visible pixel mask is built from distance to
/// the NEAREST EDGE (a capsule/segment distance), not from a sum of per-blob
/// density kernels - that's what stops the result from settling into a filled
/// disc. Whole-cloud translation (the old "fluid drift") is tracked completely
/// separately from the ring's own internal churn, so a cloud can wander slowly
/// while its skeleton spins and deforms quickly in place.
///
/// This file assumes CloudMass and CloudTileRenderer (defined elsewhere) are
/// unchanged - they only care about the final pixel mask, not how it was made.
///
/// The configured field is a spawn/render area, not a hard physics container.
/// Clouds never bounce off its edges. They either wrap cleanly to the opposite
/// side or are allowed to keep travelling out of the visible field.
/// </summary>
[ExecuteAlways]
public sealed class CloudFieldPainter : MonoBehaviour
{
    public const int TilePixelSize = 8;

    public enum FieldBoundaryMode
    {
        /// <summary>
        /// The field behaves like a torus. A cloud leaving one side continues from
        /// the opposite side without losing momentum or hitting a wall.
        /// </summary>
        Wrap,

        /// <summary>
        /// There is no field boundary at all. Cloud physics continues outside the
        /// configured area, but only the part currently inside the field is rendered.
        /// </summary>
        FreeExit
    }

    [Header("Target")]
    [SerializeField] private Tilemap cloudTilemap;

    [Header("Cloud Field")]
    [Tooltip("Bottom-left Tilemap cell of the spawn/render field.")]
    [SerializeField] private Vector3Int origin = Vector3Int.zero;

    [Tooltip("Size of the whole cloud field in Unity tiles. For example 50x50 tiles.")]
    [SerializeField] private Vector2Int size = new Vector2Int(50, 50);

    [Tooltip("Wrap lets clouds continuously cross field edges. Free Exit lets them leave the visible field completely. Neither mode bounces or clamps clouds at the border.")]
    [SerializeField] private FieldBoundaryMode boundaryMode = FieldBoundaryMode.Wrap;

    [Header("Cloud Population")]
    [Tooltip("Number of separate skeleton-ring cloud bodies distributed across the field.")]
    [Range(1, 64)]
    [SerializeField] private int cloudCount = 12;

    [Tooltip("Minimum generated cloud footprint in Unity tiles. Width and height are randomised independently.")]
    [SerializeField] private Vector2Int minimumCloudSize = new Vector2Int(2, 2);

    [Tooltip("Maximum generated cloud footprint in Unity tiles. Width and height are randomised independently.")]
    [SerializeField] private Vector2Int maximumCloudSize = new Vector2Int(8, 8);

    [Tooltip("Extra preferred spacing between newly spawned cloud centres, in Unity tiles. This is a placement preference, not a runtime wall.")]
    [Range(0f, 8f)]
    [SerializeField] private float spawnSeparationTiles = 1.0f;

    [Tooltip("Keeps initial cloud centres this far from the field edge. Has no effect after generation.")]
    [Range(0f, 10f)]
    [SerializeField] private float spawnPaddingTiles = 1.0f;

    [Tooltip("How strongly separate cloud bodies repel one another when they become very close. Set to zero if you want independent clouds to pass through one another freely.")]
    [Range(0f, 4f)]
    [SerializeField] private float interCloudRepulsion = 0.30f;

    [Header("Cloud Material")]
    [Tooltip("Average fraction of each cloud's nominal footprint that becomes visible cloud material.")]
    [Range(0.15f, 0.75f)]
    [SerializeField] private float cloudFill = 0.46f;

    [Tooltip("Per-cloud random variation added above or below Cloud Fill.")]
    [Range(0f, 0.25f)]
    [SerializeField] private float cloudFillVariation = 0.07f;

    [Header("Skeleton Shape")]
    [Tooltip("Number of joints forming the closed ring skeleton of each cloud. This is the main knob for how lumpy/organic a single cloud can look - very low counts look like triangles/pentagons, very high counts look like a smooth circle again.")]
    [Range(4, 10)]
    [SerializeField] private int jointCount = 7;

    [Tooltip("Capsule radius contributed by each joint, as a fraction of the ring's initial radius. Structural: baked in at generation time.")]
    [Range(0.15f, 0.9f)]
    [SerializeField] private float jointRadiusScale = 0.45f;

    [Tooltip("Per-joint random variation on joint radius. Structural.")]
    [Range(0f, 0.5f)]
    [SerializeField] private float jointRadiusVariation = 0.18f;

    [Tooltip("Initial ring radius as a fraction of the cloud's nominal half-size. Structural.")]
    [Range(0.25f, 0.9f)]
    [SerializeField] private float ringRestRadius = 0.55f;

    [Tooltip("Minimum edge length as a fraction of that edge's rest length. This is the hard floor that stops the ring collapsing to a point - your 'restrictions on distance'. Structural.")]
    [Range(0.2f, 0.95f)]
    [SerializeField] private float edgeMinLengthFactor = 0.55f;

    [Tooltip("Maximum edge length as a multiple of that edge's rest length. Stops a single joint flying off and stretching the skin into a spike. Structural.")]
    [Range(1.05f, 3f)]
    [SerializeField] private float edgeMaxLengthFactor = 1.8f;

    [Tooltip("Spring strength pulling each edge back toward its rest length. Live - takes effect immediately.")]
    [Range(0f, 20f)]
    [SerializeField] private float edgeStiffness = 6.0f;

    [Tooltip("How hard the Min/Max Length Factor clamp pushes back once violated. Higher values make the distance limits feel closer to a hard wall than a soft spring.")]
    [Range(0f, 80f)]
    [SerializeField] private float edgeConstraintStrength = 30f;

    [Tooltip("Outward push from the ring's own centroid, like an inflated balloon. This is what keeps the ring from caving in on itself once it starts deforming.")]
    [Range(0f, 6f)]
    [SerializeField] private float internalPressure = 1.6f;

    [Tooltip("Tangential force rotating joints around the ring's own centroid. This is the 'faster internal movement' - it can run fast even while the whole cloud drifts slowly.")]
    [Range(0f, 6f)]
    [SerializeField] private float spinStrength = 1.1f;

    [Header("Surface Noise")]
    [Tooltip("Large-scale surface noise frequency.")]
    [Min(0.01f)]
    [SerializeField] private float largeNoiseScale = 1.8f;

    [Tooltip("Large-scale surface noise strength.")]
    [Range(0f, 0.5f)]
    [SerializeField] private float largeNoiseStrength = 0.12f;

    [Tooltip("Medium-scale surface noise frequency.")]
    [Min(0.01f)]
    [SerializeField] private float mediumNoiseScale = 5.0f;

    [Tooltip("Medium-scale surface noise strength.")]
    [Range(0f, 0.25f)]
    [SerializeField] private float mediumNoiseStrength = 0.035f;

    [Tooltip("Frequency of the animated domain warp used by the surface and turbulence fields.")]
    [Min(0.01f)]
    [SerializeField] private float warpScale = 2.2f;

    [Tooltip("Amount of domain warping applied to the sampled cloud surface.")]
    [Range(0f, 0.30f)]
    [SerializeField] private float warpStrength = 0.05f;

    [Tooltip("Multiplies every joint's capsule radius at raster time only. Live - lets you fatten or thin the skin without rebuilding the skeleton.")]
    [Range(0.8f, 3f)]
    [SerializeField] private float skinThickness = 1.5f;

    [Tooltip("Small preference for pixels that belonged to the same cloud last frame. Reduces single-pixel shimmer.")]
    [Range(0f, 0.25f)]
    [SerializeField] private float surfaceMemory = 0.055f;

    [Header("Animation")]
    [SerializeField] private bool animateCloud = true;

    [Tooltip("How often the hidden skeleton physics is stepped and rasterised into visible pixels.")]
    [Range(0.01f, 0.5f)]
    [SerializeField] private float animationUpdateInterval = 0.10f;

    [Tooltip("Overall time multiplier. This scales both hidden physics and animated surface motion.")]
    [Range(0f, 12f)]
    [SerializeField] private float animationSpeed = 1f;

    [Tooltip("How quickly the visible surface-noise pattern drifts over each fluid body.")]
    [Range(0f, 0.25f)]
    [SerializeField] private float noiseDriftSpeed = 0.045f;

    [Tooltip("How quickly animated domain warping changes.")]
    [Range(0f, 0.25f)]
    [SerializeField] private float warpAnimationSpeed = 0.035f;

    [Tooltip("Strength of the curl-like flow field perturbing joint velocity. This adds organic imperfection on top of the spin/pressure forces.")]
    [Range(0f, 1f)]
    [SerializeField] private float flowStrength = 0.18f;

    [Header("Whole-Cloud Translation")]
    [Tooltip("Preferred bulk velocity in simulation pixels per second. All clouds are driven generally in this direction. This is completely separate from the skeleton's own internal churn.")]
    [SerializeField] private Vector2 setVelocity = new Vector2(0.45f, 0.08f);

    [Tooltip("Random per-cloud velocity offset baked in at generation time. This stops every cloud moving in perfect formation.")]
    [Range(0f, 2f)]
    [SerializeField] private float cloudVelocityVariation = 0.28f;

    [Tooltip("How strongly each cloud's bulk translation accelerates toward its preferred velocity.")]
    [Range(0f, 4f)]
    [SerializeField] private float velocityDrive = 0.85f;

    [Tooltip("Maximum bulk translation speed for the cloud as a whole, independent of how fast the skeleton is churning internally.")]
    [Range(0.25f, 10f)]
    [SerializeField] private float maxTranslationSpeed = 2.5f;

    [Header("Joint Dynamics")]
    [Tooltip("How much local (internal) joint velocity survives between physics updates. This only damps the churn - it never touches whole-cloud translation.")]
    [Range(0f, 0.995f)]
    [SerializeField] private float jointDamping = 0.82f;

    [Tooltip("Maximum local joint speed, relative to the cloud's own translation.")]
    [Range(0.25f, 20f)]
    [SerializeField] private float maxJointSpeed = 3.5f;

    [Tooltip("Physics substeps performed for every visible update.")]
    [Range(1, 6)]
    [SerializeField] private int skeletonSubsteps = 2;

    [Header("Ship Stirring")]
    [Tooltip("Default radius around the ship affected by StirAtWorldPosition, measured in Unity tiles.")]
    [Range(0.25f, 8f)]
    [SerializeField] private float stirRadiusTiles = 1.75f;

    [Tooltip("Default forward impulse applied in the ship's average direction of travel.")]
    [Range(0f, 20f)]
    [SerializeField] private float stirStrength = 5.0f;

    [Tooltip("Sideways force that pushes material away from the ship's travel line. This is what lets a pass cut a temporary channel through a cloud.")]
    [Range(0f, 12f)]
    [SerializeField] private float stirCutStrength = 3.25f;

    [Tooltip("Rotational component mixed into the wake so the two sides roll instead of simply separating and snapping straight back.")]
    [Range(0f, 12f)]
    [SerializeField] private float stirMixStrength = 1.65f;

    [Tooltip("Maximum extra speed one stir call may add to a joint before the normal Max Joint Speed clamp takes over on the next physics step.")]
    [Range(0.25f, 20f)]
    [SerializeField] private float stirImpulseLimit = 7.0f;

    [Header("Rendering")]
    [SerializeField] private Color cloudColour = Color.white;

    [Min(1)]
    [SerializeField] private int pixelsPerUnit = 8;

    [Header("Editor")]
    [SerializeField] private int seed = 12345;
    [SerializeField] private bool liveRegenerate = true;

    [Header("Live Tuning")]
    [Tooltip("While the game is running in the Unity Editor, Inspector changes are applied automatically instead of waiting for a manual refresh.")]
    [SerializeField] private bool applyInspectorChangesImmediately = true;

    [Tooltip("Some settings define the hidden skeleton itself and cannot be changed correctly in-place. When one of those settings changes, restart the cloud field from simulation step 1 automatically.")]
    [SerializeField] private bool restartSimulationWhenRequired = true;

    [Header("Simulation Debug")]
    [Tooltip("Draw the hidden skeleton simulation over the visible cloud. This uses a real runtime mesh overlay, so it does not depend on Unity Gizmos being enabled.")]
    [SerializeField] private bool drawSimulationDebug = false;

    [Tooltip("Only draw the simulation overlay while this CloudFieldPainter GameObject is selected.")]
    [SerializeField] private bool debugOnlyWhenSelected = false;

    [Tooltip("Draw each skeleton joint and its capsule radius.")]
    [SerializeField] private bool debugDrawJoints = true;

    [Tooltip("Draw the structural edges connecting joints (the 'skin').")]
    [SerializeField] private bool debugDrawEdges = true;

    [Tooltip("Draw the current local velocity of every joint.")]
    [SerializeField] private bool debugDrawJointVelocities = true;

    [Tooltip("Draw each cloud centroid, its translation velocity, and a spin indicator.")]
    [SerializeField] private bool debugDrawCloudCentres = true;

    [Tooltip("Multiplier used only for debug velocity arrows.")]
    [Range(0.05f, 3f)]
    [SerializeField] private float debugVelocityScale = 0.45f;

    [Tooltip("Scales debug points and rings only. Increase this if the hidden simulation markers are difficult to see.")]
    [Range(0.5f, 6f)]
    [SerializeField] private float debugMarkerScale = 2.0f;

    [Tooltip("Moves the debug overlay slightly in front of the Tilemap plane so sprites cannot hide the Gizmos in the Game view.")]
    [Range(0f, 1f)]
    [SerializeField] private float debugOverlayDepth = 0.08f;

    [Tooltip("Draw the configured field outline and a large cross at its origin. Useful for confirming that debug drawing is active at all.")]
    [SerializeField] private bool debugDrawFieldGuide = true;

    private CloudMass mass;
    private CloudTileRenderer renderer;

    private readonly List<SkeletonCloud> clouds = new List<SkeletonCloud>();
    private readonly HashSet<int> rasterPixels = new HashSet<int>();

    // Real rendered debug overlay. This intentionally does not rely on OnDrawGizmos,
    // because Gizmos can be disabled globally or per-component in Unity.
    private GameObject debugOverlayObject;
    private MeshFilter debugOverlayFilter;
    private MeshRenderer debugOverlayRenderer;
    private Mesh debugOverlayMesh;
    private Material debugOverlayMaterial;
    private readonly List<Vector3> debugOverlayVertices = new List<Vector3>(8192);
    private readonly List<Color> debugOverlayColours = new List<Color>(8192);
    private readonly List<int> debugOverlayTriangles = new List<int>(16384);

    private float animationTime;
    private float animationAccumulator;
    private bool blockedSpacePresent;

    // Inspector/live-tuning state. The structural signature records the settings
    // that were used to build the current hidden skeleton. Live physics values
    // are intentionally not included because the solver reads them every step.
    private int appliedStructuralSignature;
    private int appliedRenderingSignature;
    private bool appliedSignaturesValid;
    private bool structuralChangesPending;

#if UNITY_EDITOR
    private bool editorRegenerationQueued;
    private bool runtimeInspectorApplyQueued;
#endif

    public Tilemap CloudTilemap => cloudTilemap;
    public Vector3Int Origin => origin;
    public Vector2Int Size => size;
    public Vector2Int FieldSize => size;
    public int CloudCount => clouds.Count;
    public int ConfiguredCloudCount => cloudCount;
    public int Seed => seed;
    public float CloudFill => cloudFill;
    public float AnimationTime => animationTime;
    public Vector2 FluidSetVelocity => setVelocity;
    public FieldBoundaryMode BoundaryMode => boundaryMode;
    public int TargetPixelCount => mass != null ? mass.TargetPixelCount : EstimateTotalTargetPixels();
    public int CurrentPixelCount => mass != null ? mass.CountCloudPixels() : 0;
    public CloudMass Mass => mass;

    private void Reset()
    {
        cloudTilemap = GetComponent<Tilemap>();
    }

    private void OnEnable()
    {
        if (cloudTilemap == null)
            cloudTilemap = GetComponent<Tilemap>();

        if (Application.isPlaying)
        {
            if (mass == null)
                GenerateCloud();
        }
#if UNITY_EDITOR
        else if (liveRegenerate)
        {
            QueueEditorRegeneration();
        }
#endif

        UpdateDebugOverlayRenderer();
    }

    private void Update()
    {
        if (Application.isPlaying && animateCloud && animationSpeed > 0f && EnsureGenerated())
        {
            animationAccumulator += Time.deltaTime;

            int safety = 0;
            while (animationAccumulator >= animationUpdateInterval && safety++ < 4)
            {
                animationAccumulator -= animationUpdateInterval;
                animationTime += animationUpdateInterval * animationSpeed;
                ApplyAnimationStep(animationUpdateInterval * animationSpeed);
            }
        }

        // Keep the debug visualisation alive even when the cloud animation is paused.
        UpdateDebugOverlayRenderer();
    }

    private void OnDisable()
    {
        DisposeDebugOverlayRenderer();
    }

    private void OnDestroy()
    {
        DisposeDebugOverlayRenderer();
        DisposeRenderer();
    }

    private void OnValidate()
    {
        size.x = Mathf.Clamp(size.x, 1, 128);
        size.y = Mathf.Clamp(size.y, 1, 128);

        cloudCount = Mathf.Clamp(cloudCount, 1, 64);

        minimumCloudSize.x = Mathf.Clamp(minimumCloudSize.x, 1, 32);
        minimumCloudSize.y = Mathf.Clamp(minimumCloudSize.y, 1, 32);
        maximumCloudSize.x = Mathf.Clamp(maximumCloudSize.x, minimumCloudSize.x, 32);
        maximumCloudSize.y = Mathf.Clamp(maximumCloudSize.y, minimumCloudSize.y, 32);

        minimumCloudSize.x = Mathf.Min(minimumCloudSize.x, size.x);
        minimumCloudSize.y = Mathf.Min(minimumCloudSize.y, size.y);
        maximumCloudSize.x = Mathf.Clamp(maximumCloudSize.x, minimumCloudSize.x, Mathf.Max(1, size.x));
        maximumCloudSize.y = Mathf.Clamp(maximumCloudSize.y, minimumCloudSize.y, Mathf.Max(1, size.y));

        spawnSeparationTiles = Mathf.Clamp(spawnSeparationTiles, 0f, 8f);
        spawnPaddingTiles = Mathf.Clamp(spawnPaddingTiles, 0f, 10f);
        interCloudRepulsion = Mathf.Clamp(interCloudRepulsion, 0f, 4f);

        cloudFill = Mathf.Clamp(cloudFill, 0.15f, 0.75f);
        cloudFillVariation = Mathf.Clamp(cloudFillVariation, 0f, 0.25f);

        jointCount = Mathf.Clamp(jointCount, 4, 10);
        jointRadiusScale = Mathf.Clamp(jointRadiusScale, 0.15f, 0.9f);
        jointRadiusVariation = Mathf.Clamp(jointRadiusVariation, 0f, 0.5f);
        ringRestRadius = Mathf.Clamp(ringRestRadius, 0.25f, 0.9f);
        edgeMinLengthFactor = Mathf.Clamp(edgeMinLengthFactor, 0.2f, 0.95f);
        edgeMaxLengthFactor = Mathf.Clamp(edgeMaxLengthFactor, 1.05f, 3f);
        edgeStiffness = Mathf.Clamp(edgeStiffness, 0f, 20f);
        edgeConstraintStrength = Mathf.Clamp(edgeConstraintStrength, 0f, 80f);
        internalPressure = Mathf.Clamp(internalPressure, 0f, 6f);
        spinStrength = Mathf.Clamp(spinStrength, 0f, 6f);

        largeNoiseScale = Mathf.Max(0.01f, largeNoiseScale);
        largeNoiseStrength = Mathf.Clamp(largeNoiseStrength, 0f, 0.5f);
        mediumNoiseScale = Mathf.Max(0.01f, mediumNoiseScale);
        mediumNoiseStrength = Mathf.Clamp(mediumNoiseStrength, 0f, 0.25f);
        warpScale = Mathf.Max(0.01f, warpScale);
        warpStrength = Mathf.Clamp(warpStrength, 0f, 0.30f);
        skinThickness = Mathf.Clamp(skinThickness, 0.8f, 3f);
        surfaceMemory = Mathf.Clamp(surfaceMemory, 0f, 0.25f);

        animationUpdateInterval = Mathf.Clamp(animationUpdateInterval, 0.01f, 0.5f);
        animationSpeed = Mathf.Clamp(animationSpeed, 0f, 12f);
        noiseDriftSpeed = Mathf.Clamp(noiseDriftSpeed, 0f, 0.25f);
        warpAnimationSpeed = Mathf.Clamp(warpAnimationSpeed, 0f, 0.25f);
        flowStrength = Mathf.Clamp01(flowStrength);

        cloudVelocityVariation = Mathf.Clamp(cloudVelocityVariation, 0f, 2f);
        velocityDrive = Mathf.Clamp(velocityDrive, 0f, 4f);
        maxTranslationSpeed = Mathf.Clamp(maxTranslationSpeed, 0.25f, 10f);

        jointDamping = Mathf.Clamp(jointDamping, 0f, 0.995f);
        maxJointSpeed = Mathf.Clamp(maxJointSpeed, 0.25f, 20f);
        skeletonSubsteps = Mathf.Clamp(skeletonSubsteps, 1, 6);

        stirRadiusTiles = Mathf.Clamp(stirRadiusTiles, 0.25f, 8f);
        stirStrength = Mathf.Clamp(stirStrength, 0f, 20f);
        stirCutStrength = Mathf.Clamp(stirCutStrength, 0f, 12f);
        stirMixStrength = Mathf.Clamp(stirMixStrength, 0f, 12f);
        stirImpulseLimit = Mathf.Clamp(stirImpulseLimit, 0.25f, 20f);

        pixelsPerUnit = Mathf.Max(1, pixelsPerUnit);
        debugVelocityScale = Mathf.Clamp(debugVelocityScale, 0.05f, 3f);
        debugMarkerScale = Mathf.Clamp(debugMarkerScale, 0.5f, 6f);
        debugOverlayDepth = Mathf.Clamp(debugOverlayDepth, 0f, 1f);

#if UNITY_EDITOR
        if (Application.isPlaying)
        {
            if (applyInspectorChangesImmediately)
                QueueRuntimeInspectorApply();
            else if (appliedSignaturesValid)
                structuralChangesPending = CalculateStructuralSignature() != appliedStructuralSignature;
        }
        else if (liveRegenerate)
        {
            QueueEditorRegeneration();
        }
#endif
    }

    // -------------------------------------------------------------------------
    // Shuffle helpers
    // -------------------------------------------------------------------------

    [ContextMenu("Shuffle/Population")]
    public void ShufflePopulation()
    {
        System.Random random = CreateShuffleRandom();

        int maxReasonableClouds = Mathf.Clamp((size.x * size.y) / 80, 4, 28);
        cloudCount = random.Next(4, maxReasonableClouds + 1);

        int maxWidth = Mathf.Clamp(Mathf.Min(10, size.x), 2, 32);
        int maxHeight = Mathf.Clamp(Mathf.Min(10, size.y), 2, 32);

        minimumCloudSize = new Vector2Int(
            random.Next(2, Mathf.Min(4, maxWidth) + 1),
            random.Next(2, Mathf.Min(4, maxHeight) + 1));

        maximumCloudSize = new Vector2Int(
            random.Next(Mathf.Max(minimumCloudSize.x, 4), maxWidth + 1),
            random.Next(Mathf.Max(minimumCloudSize.y, 4), maxHeight + 1));

        spawnSeparationTiles = RandomRange(random, 0.25f, 2.50f);
        spawnPaddingTiles = RandomRange(random, 0f, 2.0f);
        interCloudRepulsion = RandomRange(random, 0.05f, 0.75f);
        cloudFillVariation = RandomRange(random, 0.02f, 0.12f);

        RegenerateAfterShuffle();
    }

    [ContextMenu("Shuffle/Skeleton")]
    public void ShuffleSkeleton()
    {
        System.Random random = CreateShuffleRandom();

        jointCount = random.Next(5, 10);
        jointRadiusScale = RandomRange(random, 0.32f, 0.62f);
        jointRadiusVariation = RandomRange(random, 0.08f, 0.30f);
        ringRestRadius = RandomRange(random, 0.42f, 0.72f);
        edgeMinLengthFactor = RandomRange(random, 0.40f, 0.70f);
        edgeMaxLengthFactor = RandomRange(random, 1.40f, 2.40f);
        edgeStiffness = RandomRange(random, 3.0f, 11.0f);
        internalPressure = RandomRange(random, 0.8f, 2.8f);
        spinStrength = RandomRange(random, 0.4f, 2.2f);

        RegenerateAfterShuffle();
    }

    [ContextMenu("Shuffle/Noise")]
    public void ShuffleNoise()
    {
        System.Random random = CreateShuffleRandom();

        largeNoiseScale = RandomRange(random, 1.20f, 3.20f);
        largeNoiseStrength = RandomRange(random, 0.05f, 0.20f);
        mediumNoiseScale = RandomRange(random, 3.50f, 8.00f);
        mediumNoiseStrength = RandomRange(random, 0.01f, 0.085f);
        warpScale = RandomRange(random, 1.40f, 4.00f);
        warpStrength = RandomRange(random, 0.015f, 0.11f);
        skinThickness = RandomRange(random, 1.1f, 2.2f);

        RefreshAfterAnimationShuffle();
    }

    [ContextMenu("Shuffle/Animation")]
    public void ShuffleAnimation()
    {
        System.Random random = CreateShuffleRandom();

        animateCloud = true;
        animationUpdateInterval = RandomRange(random, 0.025f, 0.13f);
        animationSpeed = RandomRange(random, 0.75f, 8.50f);
        noiseDriftSpeed = RandomRange(random, 0.02f, 0.18f);
        warpAnimationSpeed = RandomRange(random, 0.015f, 0.16f);
        flowStrength = RandomRange(random, 0.08f, 0.65f);

        RefreshAfterAnimationShuffle();
    }

    [ContextMenu("Shuffle/Motion")]
    public void ShuffleMotion()
    {
        System.Random random = CreateShuffleRandom();

        float direction = RandomRange(random, -Mathf.PI, Mathf.PI);
        float speed = RandomRange(random, 0.10f, 1.20f);
        setVelocity = new Vector2(Mathf.Cos(direction), Mathf.Sin(direction)) * speed;

        cloudVelocityVariation = RandomRange(random, 0.05f, 0.65f);
        velocityDrive = RandomRange(random, 0.35f, 1.60f);
        maxTranslationSpeed = RandomRange(random, 1.2f, 4.5f);

        jointDamping = RandomRange(random, 0.65f, 0.92f);
        maxJointSpeed = RandomRange(random, 2.0f, 6.0f);
        skeletonSubsteps = random.Next(1, 4);

        RegenerateAfterShuffle();
    }

    public void SetFluidVelocity(Vector2 velocity)
    {
        setVelocity = velocity;
    }

    /// <summary>
    /// Pushes and mixes cloud fluid around a world-space point using one average
    /// direction of travel. This is intended for a ship wake.
    ///
    /// Call this while the ship overlaps the cloud field. The forward component
    /// carries fluid with the ship, the cut component pushes the two sides away
    /// from the travel line, and the mix component adds a small rolling wake.
    /// </summary>
    public void StirAtWorldPosition(Vector3 worldPosition, Vector2 averageTravelDirection)
    {
        StirAtWorldPosition(
            worldPosition,
            averageTravelDirection,
            stirRadiusTiles,
            stirStrength,
            stirCutStrength,
            stirMixStrength);
    }

    /// <summary>
    /// Same as StirAtWorldPosition but with per-call tuning. Radius is measured in
    /// Unity tiles. Strength values are velocity impulses, not persistent settings.
    /// </summary>
    public void StirAtWorldPosition(
        Vector3 worldPosition,
        Vector2 averageTravelDirection,
        float radiusTiles,
        float forwardStrength,
        float cutStrength,
        float mixStrength)
    {
        if (!EnsureGenerated() || cloudTilemap == null)
            return;

        Vector2 direction = averageTravelDirection;
        if (direction.sqrMagnitude < 0.0001f)
            return;

        direction.Normalize();

        GridLayout grid = cloudTilemap.layoutGrid;
        Vector3 localPoint = grid.transform.InverseTransformPoint(worldPosition);
        Vector3 interpolatedCell = grid.LocalToCellInterpolated(localPoint);
        Vector2 localPixelPosition = new Vector2(
            (interpolatedCell.x - origin.x) * TilePixelSize,
            (interpolatedCell.y - origin.y) * TilePixelSize);

        StirAtSimulationPosition(
            localPixelPosition,
            direction,
            Mathf.Max(0.1f, radiusTiles) * TilePixelSize,
            forwardStrength,
            cutStrength,
            mixStrength);
    }

    /// <summary>
    /// Lowest-level stirring API. Position and radius are in the hidden simulation's
    /// pixel coordinates, where one Unity tile is eight simulation pixels. Impulses
    /// are applied to skeleton joints, which then pull the rest of their ring along
    /// through the normal edge springs.
    /// </summary>
    public void StirAtSimulationPosition(
        Vector2 simulationPosition,
        Vector2 averageTravelDirection,
        float radiusPixels,
        float forwardStrength,
        float cutStrength,
        float mixStrength)
    {
        if (mass == null || clouds.Count == 0)
            return;

        Vector2 direction = averageTravelDirection;
        if (direction.sqrMagnitude < 0.0001f)
            return;

        direction.Normalize();
        Vector2 perpendicular = new Vector2(-direction.y, direction.x);
        float radius = Mathf.Max(0.1f, radiusPixels);
        float radiusSquared = radius * radius;

        for (int cloudIndex = 0; cloudIndex < clouds.Count; cloudIndex++)
        {
            SkeletonCloud cloud = clouds[cloudIndex];

            for (int i = 0; i < cloud.Joints.Count; i++)
            {
                SkeletonJoint joint = cloud.Joints[i];
                Vector2 offset = FieldDelta(
                    simulationPosition,
                    joint.Position,
                    mass.Width,
                    mass.Height);

                float distanceSquared = offset.sqrMagnitude;
                if (distanceSquared >= radiusSquared)
                    continue;

                float distance = Mathf.Sqrt(distanceSquared);
                float t = 1f - distance / radius;
                float falloff = t * t * (3f - 2f * t);

                float side = Vector2.Dot(offset, perpendicular);
                float sideSign = Mathf.Abs(side) < 0.05f
                    ? (i % 2 == 0 ? 1f : -1f)
                    : Mathf.Sign(side);

                Vector2 impulse = direction * forwardStrength;
                impulse += perpendicular * sideSign * cutStrength;

                if (distance > 0.001f)
                {
                    Vector2 tangent = new Vector2(-offset.y, offset.x) / distance;
                    float swirlSign = Mathf.Sign(Vector2.Dot(tangent, direction));
                    if (Mathf.Abs(swirlSign) < 0.5f)
                        swirlSign = 1f;

                    impulse += tangent * swirlSign * mixStrength;
                }

                impulse *= falloff;
                impulse = Vector2.ClampMagnitude(impulse, stirImpulseLimit);

                joint.Velocity += impulse;
                joint.Velocity = Vector2.ClampMagnitude(
                    joint.Velocity,
                    Mathf.Max(maxJointSpeed, stirImpulseLimit));
            }
        }
    }

    [ContextMenu("Shuffle/All")]
    public void ShuffleAll()
    {
        System.Random random = CreateShuffleRandom();

        seed = random.Next(1, int.MaxValue);

        int maxReasonableClouds = Mathf.Clamp((size.x * size.y) / 80, 4, 28);
        cloudCount = random.Next(4, maxReasonableClouds + 1);
        spawnSeparationTiles = RandomRange(random, 0.25f, 2.50f);
        spawnPaddingTiles = RandomRange(random, 0f, 2.0f);
        interCloudRepulsion = RandomRange(random, 0.05f, 0.75f);

        jointCount = random.Next(5, 10);
        jointRadiusScale = RandomRange(random, 0.32f, 0.62f);
        jointRadiusVariation = RandomRange(random, 0.08f, 0.30f);
        ringRestRadius = RandomRange(random, 0.42f, 0.72f);
        edgeMinLengthFactor = RandomRange(random, 0.40f, 0.70f);
        edgeMaxLengthFactor = RandomRange(random, 1.40f, 2.40f);
        edgeStiffness = RandomRange(random, 3.0f, 11.0f);
        internalPressure = RandomRange(random, 0.8f, 2.8f);
        spinStrength = RandomRange(random, 0.4f, 2.2f);

        largeNoiseScale = RandomRange(random, 1.20f, 3.20f);
        largeNoiseStrength = RandomRange(random, 0.05f, 0.20f);
        mediumNoiseScale = RandomRange(random, 3.50f, 8.00f);
        mediumNoiseStrength = RandomRange(random, 0.01f, 0.085f);
        warpScale = RandomRange(random, 1.40f, 4.00f);
        warpStrength = RandomRange(random, 0.015f, 0.11f);
        skinThickness = RandomRange(random, 1.1f, 2.2f);

        animateCloud = true;
        animationUpdateInterval = RandomRange(random, 0.025f, 0.13f);
        animationSpeed = RandomRange(random, 0.75f, 8.50f);
        noiseDriftSpeed = RandomRange(random, 0.02f, 0.18f);
        warpAnimationSpeed = RandomRange(random, 0.015f, 0.16f);
        flowStrength = RandomRange(random, 0.08f, 0.65f);

        float direction = RandomRange(random, -Mathf.PI, Mathf.PI);
        float speed = RandomRange(random, 0.10f, 1.20f);
        setVelocity = new Vector2(Mathf.Cos(direction), Mathf.Sin(direction)) * speed;
        cloudVelocityVariation = RandomRange(random, 0.05f, 0.65f);
        velocityDrive = RandomRange(random, 0.35f, 1.60f);
        maxTranslationSpeed = RandomRange(random, 1.2f, 4.5f);

        jointDamping = RandomRange(random, 0.65f, 0.92f);
        maxJointSpeed = RandomRange(random, 2.0f, 6.0f);
        skeletonSubsteps = random.Next(1, 4);

        RegenerateAfterShuffle();
    }

    [ContextMenu("Shuffle/Seed Only")]
    public void ShuffleSeed()
    {
        seed = CreateShuffleRandom().Next(1, int.MaxValue);
        RegenerateAfterShuffle();
    }

    public void SetAnimationPace(int preset)
    {
        animateCloud = true;

        switch (preset)
        {
            default:
            case 0:
                animationSpeed = 1.0f;
                animationUpdateInterval = 0.10f;
                skeletonSubsteps = 2;
                velocityDrive = 0.55f;
                jointDamping = 0.88f;
                break;

            case 1:
                animationSpeed = 2.5f;
                animationUpdateInterval = 0.075f;
                skeletonSubsteps = 2;
                velocityDrive = 0.85f;
                jointDamping = 0.82f;
                break;

            case 2:
                animationSpeed = 5.0f;
                animationUpdateInterval = 0.050f;
                skeletonSubsteps = 3;
                velocityDrive = 1.15f;
                jointDamping = 0.75f;
                break;

            case 3:
                animationSpeed = 8.0f;
                animationUpdateInterval = 0.035f;
                skeletonSubsteps = 4;
                velocityDrive = 1.55f;
                jointDamping = 0.68f;
                break;
        }

        RefreshAfterAnimationShuffle();
    }

    private static System.Random CreateShuffleRandom()
    {
        return new System.Random(unchecked((int)DateTime.UtcNow.Ticks));
    }

    private static float RandomRange(System.Random random, float minimum, float maximum)
    {
        return minimum + (float)random.NextDouble() * (maximum - minimum);
    }

    private void RegenerateAfterShuffle()
    {
#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
#endif

        if (isActiveAndEnabled && cloudTilemap != null)
            GenerateCloud();
    }

    private void RefreshAfterAnimationShuffle()
    {
        animationAccumulator = 0f;

#if UNITY_EDITOR
        EditorUtility.SetDirty(this);
        SceneView.RepaintAll();
#endif
    }

    // -------------------------------------------------------------------------
    // Live Inspector tuning
    // -------------------------------------------------------------------------

    /// <summary>
    /// Applies the current Inspector values to the running simulation.
    ///
    /// Most physics values are already read directly every simulation step, so they
    /// need no reset. Structural values (ring layout, joint radii, edge limits) change
    /// the shape/topology of the hidden skeleton; those require rebuilding the field
    /// from simulation step 1.
    /// </summary>
    public void ApplyInspectorChangesNow()
    {
        if (!isActiveAndEnabled || cloudTilemap == null)
            return;

        if (mass == null || clouds.Count == 0)
        {
            GenerateCloud();
            return;
        }

        int structuralSignature = CalculateStructuralSignature();
        bool requiresRestart = !appliedSignaturesValid || structuralSignature != appliedStructuralSignature;

        if (requiresRestart)
        {
            structuralChangesPending = true;

            if (restartSimulationWhenRequired)
            {
                GenerateCloud();
                return;
            }
        }
        else
        {
            structuralChangesPending = false;
        }

        int renderingSignature = CalculateRenderingSignature();
        if (!appliedSignaturesValid || renderingSignature != appliedRenderingSignature)
        {
            RebuildRendererWithoutRestart();
            appliedRenderingSignature = renderingSignature;
        }
        else
        {
            // Noise, skin thickness and several other non-structural visual values
            // can affect the raster immediately, even before another physics step.
            if (RasteriseSkeletonField() && renderer != null)
                renderer.UpdateChangedTiles(mass, origin, size);
        }

#if UNITY_EDITOR
        SceneView.RepaintAll();
#endif
    }

    /// <summary>
    /// Explicit testing helper: discard all hidden simulation state and rebuild the
    /// field from time/step zero using the values currently visible in the Inspector.
    /// </summary>
    [ContextMenu("Restart Simulation From Step 1")]
    public void RestartSimulationFromStepOne()
    {
        GenerateCloud();
    }

    public bool StructuralChangesPending => structuralChangesPending;

    private void RebuildRendererWithoutRestart()
    {
        if (mass == null || cloudTilemap == null)
            return;

        DisposeRenderer();
        renderer = new CloudTileRenderer(
            cloudTilemap,
            TilePixelSize,
            pixelsPerUnit,
            cloudColour);

        renderer.RenderAll(mass, origin, size);
    }

    private void CaptureAppliedSignatures()
    {
        appliedStructuralSignature = CalculateStructuralSignature();
        appliedRenderingSignature = CalculateRenderingSignature();
        appliedSignaturesValid = true;
        structuralChangesPending = false;
    }

    private int CalculateStructuralSignature()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + seed;
            hash = hash * 31 + size.GetHashCode();
            hash = hash * 31 + cloudCount;
            hash = hash * 31 + minimumCloudSize.GetHashCode();
            hash = hash * 31 + maximumCloudSize.GetHashCode();
            hash = hash * 31 + spawnSeparationTiles.GetHashCode();
            hash = hash * 31 + spawnPaddingTiles.GetHashCode();
            hash = hash * 31 + cloudFill.GetHashCode();
            hash = hash * 31 + cloudFillVariation.GetHashCode();

            // These values decide the initial ring layout, joint radii and edge
            // limits, so changing them properly means rebuilding step zero.
            hash = hash * 31 + jointCount;
            hash = hash * 31 + jointRadiusScale.GetHashCode();
            hash = hash * 31 + jointRadiusVariation.GetHashCode();
            hash = hash * 31 + ringRestRadius.GetHashCode();
            hash = hash * 31 + edgeMinLengthFactor.GetHashCode();
            hash = hash * 31 + edgeMaxLengthFactor.GetHashCode();

            // Per-cloud velocity variation is baked into each cloud's generated
            // VelocityOffset. The common Set Velocity itself remains live.
            hash = hash * 31 + cloudVelocityVariation.GetHashCode();

            return hash;
        }
    }

    private int CalculateRenderingSignature()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + cloudColour.GetHashCode();
            hash = hash * 31 + pixelsPerUnit;
            hash = hash * 31 + origin.GetHashCode();
            hash = hash * 31 + (cloudTilemap != null ? cloudTilemap.GetInstanceID() : 0);
            return hash;
        }
    }

    // -------------------------------------------------------------------------
    // Generation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the entire field and distributes separate skeleton-ring cloud bodies
    /// across it.
    /// </summary>
    [ContextMenu("Generate Cloud Field")]
    public void GenerateCloud()
    {
        if (!ValidateTarget())
            return;

        DisposeRenderer();
        clouds.Clear();
        rasterPixels.Clear();

        int pixelWidth = size.x * TilePixelSize;
        int pixelHeight = size.y * TilePixelSize;

        System.Random random = new System.Random(seed);
        List<SpawnInfo> spawnInfos = BuildSpawnInfos(random, pixelWidth, pixelHeight);

        int totalTargetPixels = 0;
        for (int i = 0; i < spawnInfos.Count; i++)
            totalTargetPixels += spawnInfos[i].TargetPixels;

        int capacity = pixelWidth * pixelHeight;
        if (totalTargetPixels > capacity)
        {
            float scale = capacity * 0.98f / totalTargetPixels;
            totalTargetPixels = 0;

            for (int i = 0; i < spawnInfos.Count; i++)
            {
                SpawnInfo info = spawnInfos[i];
                info.TargetPixels = Mathf.Max(1, Mathf.RoundToInt(info.TargetPixels * scale));
                spawnInfos[i] = info;
                totalTargetPixels += info.TargetPixels;
            }
        }

        mass = new CloudMass(pixelWidth, pixelHeight, totalTargetPixels, seed);

        for (int i = 0; i < spawnInfos.Count; i++)
            clouds.Add(CreateSkeletonCloud(i, spawnInfos[i], random));

        animationTime = 0f;
        animationAccumulator = 0f;
        blockedSpacePresent = false;

        RasteriseSkeletonField();

        renderer = new CloudTileRenderer(
            cloudTilemap,
            TilePixelSize,
            pixelsPerUnit,
            cloudColour);

        renderer.RenderAll(mass, origin, size);
        CaptureAppliedSignatures();
        ReportMassWarningIfNeeded();
        UpdateDebugOverlayRenderer();

#if UNITY_EDITOR
        SceneView.RepaintAll();
#endif
    }

    private List<SpawnInfo> BuildSpawnInfos(System.Random random, int pixelWidth, int pixelHeight)
    {
        List<SpawnInfo> infos = new List<SpawnInfo>(cloudCount);

        int minWidth = Mathf.Clamp(minimumCloudSize.x, 1, Mathf.Max(1, size.x));
        int minHeight = Mathf.Clamp(minimumCloudSize.y, 1, Mathf.Max(1, size.y));
        int maxWidth = Mathf.Clamp(maximumCloudSize.x, minWidth, Mathf.Max(minWidth, size.x));
        int maxHeight = Mathf.Clamp(maximumCloudSize.y, minHeight, Mathf.Max(minHeight, size.y));

        float paddingPixels = spawnPaddingTiles * TilePixelSize;
        float separationPixels = spawnSeparationTiles * TilePixelSize;

        for (int cloudIndex = 0; cloudIndex < cloudCount; cloudIndex++)
        {
            int widthTiles = random.Next(minWidth, maxWidth + 1);
            int heightTiles = random.Next(minHeight, maxHeight + 1);

            Vector2Int nominalPixels = new Vector2Int(
                widthTiles * TilePixelSize,
                heightTiles * TilePixelSize);

            float fill = Mathf.Clamp(
                cloudFill + RandomRange(random, -cloudFillVariation, cloudFillVariation),
                0.10f,
                0.85f);

            int targetPixels = Mathf.Max(
                1,
                Mathf.RoundToInt(nominalPixels.x * nominalPixels.y * fill));

            float approximateRadius = Mathf.Max(nominalPixels.x, nominalPixels.y) * 0.42f;
            Vector2 centre = ChooseSpawnCentre(
                random,
                infos,
                approximateRadius,
                paddingPixels,
                separationPixels,
                pixelWidth,
                pixelHeight);

            infos.Add(new SpawnInfo
            {
                Centre = centre,
                NominalSizePixels = nominalPixels,
                TargetPixels = targetPixels,
                ApproximateRadius = approximateRadius
            });
        }

        return infos;
    }

    private Vector2 ChooseSpawnCentre(
        System.Random random,
        List<SpawnInfo> existing,
        float approximateRadius,
        float paddingPixels,
        float separationPixels,
        int pixelWidth,
        int pixelHeight)
    {
        Vector2 best = RandomPointInField(random, paddingPixels, pixelWidth, pixelHeight);
        float bestClearance = float.NegativeInfinity;

        const int attempts = 48;
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            Vector2 candidate = RandomPointInField(random, paddingPixels, pixelWidth, pixelHeight);
            float nearestClearance = float.MaxValue;

            for (int i = 0; i < existing.Count; i++)
            {
                Vector2 delta = FieldDelta(candidate, existing[i].Centre, pixelWidth, pixelHeight);
                float required =
                    (approximateRadius + existing[i].ApproximateRadius) * 0.45f +
                    separationPixels;

                nearestClearance = Mathf.Min(nearestClearance, delta.magnitude - required);
            }

            if (existing.Count == 0)
                return candidate;

            if (nearestClearance > bestClearance)
            {
                bestClearance = nearestClearance;
                best = candidate;
            }

            if (nearestClearance >= 0f)
                return candidate;
        }

        return best;
    }

    private Vector2 RandomPointInField(System.Random random, float paddingPixels, int pixelWidth, int pixelHeight)
    {
        float minX = Mathf.Min(paddingPixels, pixelWidth * 0.45f);
        float minY = Mathf.Min(paddingPixels, pixelHeight * 0.45f);
        float maxX = Mathf.Max(minX, pixelWidth - minX);
        float maxY = Mathf.Max(minY, pixelHeight - minY);

        return new Vector2(
            RandomRange(random, minX, maxX),
            RandomRange(random, minY, maxY));
    }

    /// <summary>
    /// Builds one closed ring skeleton: joints spaced evenly (with a little angular
    /// and radial jitter) around the spawn centre, connected to their two neighbours
    /// by structural edges whose rest length is measured directly from that initial
    /// layout. No diagonal chords are added on purpose - a bare ring is what lets the
    /// shape fold and bulge instead of behaving like a stiff polygon.
    /// </summary>
    private SkeletonCloud CreateSkeletonCloud(int cloudIndex, SpawnInfo info, System.Random random)
    {
        SkeletonCloud cloud = new SkeletonCloud
        {
            Id = cloudIndex,
            Seed = unchecked(seed + cloudIndex * 104729),
            NominalSizePixels = info.NominalSizePixels,
            TargetPixelCount = info.TargetPixels,
            NoiseOffset = new Vector2(
                RandomRange(random, 100f, 10000f),
                RandomRange(random, 100f, 10000f)),
            VelocityOffset = RandomInsideUnitCircle(random) * cloudVelocityVariation,
            SpeedScale = RandomRange(random, 0.82f, 1.18f)
        };

        cloud.TranslationVelocity = setVelocity * cloud.SpeedScale + cloud.VelocityOffset;

        int count = Mathf.Clamp(jointCount + random.Next(-1, 2), 4, 10);
        float halfExtent = Mathf.Max(4f, Mathf.Min(info.NominalSizePixels.x, info.NominalSizePixels.y) * 0.5f);
        float ringRadius = Mathf.Max(3f, halfExtent * ringRestRadius);

        for (int i = 0; i < count; i++)
        {
            float baseAngle = (Mathf.PI * 2f * i) / count;
            float angle = baseAngle + RandomRange(random, -0.14f, 0.14f);
            float radialJitter = RandomRange(random, 0.82f, 1.18f);

            Vector2 localPosition = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * ringRadius * radialJitter;
            float jointRadius = Mathf.Max(
                1f,
                ringRadius * jointRadiusScale * RandomRange(random, 1f - jointRadiusVariation, 1f + jointRadiusVariation));

            bool counterSpin = random.NextDouble() < 0.12;

            cloud.Joints.Add(new SkeletonJoint
            {
                Position = info.Centre + localPosition,
                Velocity = Vector2.zero,
                Radius = jointRadius,
                PressureScale = RandomRange(random, 0.7f, 1.3f),
                SpinScale = RandomRange(random, 0.6f, 1.4f) * (counterSpin ? -1f : 1f)
            });
        }

        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            float restLength = Vector2.Distance(cloud.Joints[i].Position, cloud.Joints[next].Position);

            cloud.Edges.Add(new SkeletonEdge
            {
                JointA = i,
                JointB = next,
                RestLength = restLength,
                MinLength = restLength * edgeMinLengthFactor,
                MaxLength = restLength * edgeMaxLengthFactor
            });
        }

        return cloud;
    }

    private static Vector2 RandomInsideUnitCircle(System.Random random)
    {
        float angle = RandomRange(random, 0f, Mathf.PI * 2f);
        float radius = Mathf.Sqrt((float)random.NextDouble());
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
    }

    // -------------------------------------------------------------------------
    // Runtime editing / damage
    // -------------------------------------------------------------------------

    public int RemoveTile(Vector3Int tilePosition)
    {
        return RemoveArea(new BoundsInt(tilePosition, Vector3Int.one));
    }

    public int RemoveArea(BoundsInt tileArea)
    {
        if (!EnsureGenerated())
            return 0;

        RectInt pixelArea = TileBoundsToPixelRect(tileArea);
        if (pixelArea.width <= 0 || pixelArea.height <= 0)
            return 0;

        int removed = mass.BlockAndClear(pixelArea);
        blockedSpacePresent = true;

        ExpelJointsFromBlockedSpace();
        RasteriseSkeletonField();

        renderer.UpdateChangedTiles(mass, origin, size);
        ReportMassWarningIfNeeded();
        return removed;
    }

    public int ClearArea(BoundsInt tileArea)
    {
        return RemoveArea(tileArea);
    }

    [ContextMenu("Reflow Cloud Field")]
    public void ReflowCloud()
    {
        if (!EnsureGenerated())
            return;

        RasteriseSkeletonField();
        renderer.UpdateChangedTiles(mass, origin, size);
        ReportMassWarningIfNeeded();
    }

    [ContextMenu("Step Cloud Animation")]
    public void StepCloudAnimation()
    {
        if (!EnsureGenerated())
            return;

        float dt = animationUpdateInterval * Mathf.Max(0.01f, animationSpeed);
        animationTime += dt;
        ApplyAnimationStep(dt);
    }

    public void SetAnimationTime(float time, bool reshapeImmediately = true)
    {
        if (!EnsureGenerated())
            return;

        float previous = animationTime;
        animationTime = time;
        animationAccumulator = 0f;

        if (!reshapeImmediately)
            return;

        float dt = Mathf.Abs(animationTime - previous);
        if (dt <= 0.0001f)
            dt = animationUpdateInterval * Mathf.Max(0.01f, animationSpeed);

        StepSkeletonSimulation(Mathf.Min(dt, 0.35f));

        if (RasteriseSkeletonField())
            renderer.UpdateChangedTiles(mass, origin, size);
    }

    [ContextMenu("Render Cloud Field")]
    public void RenderCloud()
    {
        if (!EnsureGenerated())
            return;

        EnsureRenderer();
        renderer.RenderAll(mass, origin, size);
    }

    public bool ContainsCloud(Vector3Int tilePosition)
    {
        if (mass == null || !TryTileToLocalTile(tilePosition, out int localTileX, out int localTileY))
            return false;

        int startX = localTileX * TilePixelSize;
        int startY = localTileY * TilePixelSize;

        for (int y = 0; y < TilePixelSize; y++)
        {
            for (int x = 0; x < TilePixelSize; x++)
            {
                if (mass.HasCloud(startX + x, startY + y))
                    return true;
            }
        }

        return false;
    }

    public bool ContainsCloud(BoundsInt tileArea)
    {
        if (mass == null)
            return false;

        foreach (Vector3Int tile in tileArea.allPositionsWithin)
        {
            if (ContainsCloud(tile))
                return true;
        }

        return false;
    }

    public float GetTileFill01(Vector3Int tilePosition)
    {
        if (mass == null || !TryTileToLocalTile(tilePosition, out int localTileX, out int localTileY))
            return 0f;

        int filled = 0;
        int startX = localTileX * TilePixelSize;
        int startY = localTileY * TilePixelSize;

        for (int y = 0; y < TilePixelSize; y++)
        {
            for (int x = 0; x < TilePixelSize; x++)
            {
                if (mass.HasCloud(startX + x, startY + y))
                    filled++;
            }
        }

        return filled / 64f;
    }

    public bool IsTileBlocked(Vector3Int tilePosition)
    {
        if (mass == null || !TryTileToLocalTile(tilePosition, out int localTileX, out int localTileY))
            return false;

        int startX = localTileX * TilePixelSize;
        int startY = localTileY * TilePixelSize;

        for (int y = 0; y < TilePixelSize; y++)
        {
            for (int x = 0; x < TilePixelSize; x++)
            {
                if (mass.IsBlocked(startX + x, startY + y))
                    return true;
            }
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // Hidden skeleton physics
    // -------------------------------------------------------------------------

    private void ApplyAnimationStep(float deltaTime)
    {
        StepSkeletonSimulation(Mathf.Max(0.001f, deltaTime));

        if (RasteriseSkeletonField())
            renderer.UpdateChangedTiles(mass, origin, size);
    }

    /// <summary>
    /// Advances every cloud's skeleton by one animation step. Two things are kept
    /// deliberately separate here: each cloud's TranslationVelocity (the whole-body
    /// drift, updated once per cloud) and each joint's local Velocity (the internal
    /// churn). Only local velocity is damped/clamped by the joint dynamics settings;
    /// translation has its own drive/clamp so it can stay slow while the skeleton
    /// spins quickly in place.
    /// </summary>
    private void StepSkeletonSimulation(float deltaTime)
    {
        if (mass == null || clouds.Count == 0)
            return;

        int substeps = Mathf.Clamp(skeletonSubsteps, 1, 6);
        float step = Mathf.Clamp(deltaTime / substeps, 0.0005f, 0.08f);

        for (int substep = 0; substep < substeps; substep++)
        {
            Vector2[] centroids = new Vector2[clouds.Count];
            for (int i = 0; i < clouds.Count; i++)
                centroids[i] = CalculateJointCentroid(clouds[i]);

            for (int cloudIndex = 0; cloudIndex < clouds.Count; cloudIndex++)
            {
                SkeletonCloud cloud = clouds[cloudIndex];
                if (cloud.Joints.Count == 0)
                    continue;

                Vector2 centroid = centroids[cloudIndex];

                Vector2 preferredVelocity = setVelocity * cloud.SpeedScale + cloud.VelocityOffset;
                cloud.TranslationVelocity += (preferredVelocity - cloud.TranslationVelocity) * velocityDrive * step;
                cloud.TranslationVelocity = Vector2.ClampMagnitude(cloud.TranslationVelocity, maxTranslationSpeed);

                Vector2 interCloudPush = CalculateInterCloudRepulsion(cloudIndex, centroids);

                Vector2[] localForces = new Vector2[cloud.Joints.Count];

                foreach (SkeletonEdge edge in cloud.Edges)
                {
                    SkeletonJoint a = cloud.Joints[edge.JointA];
                    SkeletonJoint b = cloud.Joints[edge.JointB];

                    Vector2 delta = b.Position - a.Position;
                    float distance = Mathf.Max(0.0001f, delta.magnitude);
                    Vector2 direction = delta / distance;

                    float stretch = distance - edge.RestLength;
                    Vector2 force = direction * stretch * edgeStiffness;

                    if (distance < edge.MinLength)
                        force -= direction * (edge.MinLength - distance) * edgeConstraintStrength;
                    else if (distance > edge.MaxLength)
                        force += direction * (distance - edge.MaxLength) * edgeConstraintStrength;

                    localForces[edge.JointA] += force;
                    localForces[edge.JointB] -= force;
                }

                for (int i = 0; i < cloud.Joints.Count; i++)
                {
                    SkeletonJoint joint = cloud.Joints[i];
                    Vector2 fromCentre = joint.Position - centroid;
                    float distanceFromCentre = fromCentre.magnitude;

                    if (distanceFromCentre > 0.0001f)
                    {
                        Vector2 outward = fromCentre / distanceFromCentre;

                        // Inflated-balloon pressure keeps the ring from caving in.
                        localForces[i] += outward * internalPressure * joint.PressureScale;

                        // Tangential spin is the "faster internal movement" - it can
                        // run hard even while the cloud's translation stays gentle.
                        Vector2 tangent = new Vector2(-outward.y, outward.x);
                        localForces[i] += tangent * spinStrength * joint.SpinScale;
                    }

                    localForces[i] += SampleCurlNoise(cloud, joint.Position - centroid) * flowStrength;
                    localForces[i] += interCloudPush;
                    localForces[i] += SampleBlockedRepulsion(joint.Position, joint.Radius);
                }

                float retainedLocalVelocity = Mathf.Pow(Mathf.Clamp01(jointDamping), step * 10f);

                for (int i = 0; i < cloud.Joints.Count; i++)
                {
                    SkeletonJoint joint = cloud.Joints[i];
                    joint.Velocity *= retainedLocalVelocity;
                    joint.Velocity += localForces[i] * step;
                    joint.Velocity = Vector2.ClampMagnitude(joint.Velocity, maxJointSpeed);

                    Vector2 oldPosition = joint.Position;
                    Vector2 newPosition = oldPosition + (joint.Velocity + cloud.TranslationVelocity) * step;

                    ResolveBlockedCollision(oldPosition, ref newPosition, ref joint.Velocity, joint.Radius);
                    joint.Position = newPosition;
                }

                ApplyFieldBoundary(cloud);
            }
        }
    }

    private Vector2 CalculateInterCloudRepulsion(int cloudIndex, Vector2[] centres)
    {
        if (interCloudRepulsion <= 0f)
            return Vector2.zero;

        SkeletonCloud cloud = clouds[cloudIndex];
        Vector2 force = Vector2.zero;
        float ownRadius = Mathf.Max(cloud.NominalSizePixels.x, cloud.NominalSizePixels.y) * 0.35f;

        for (int otherIndex = 0; otherIndex < clouds.Count; otherIndex++)
        {
            if (otherIndex == cloudIndex)
                continue;

            SkeletonCloud other = clouds[otherIndex];
            float otherRadius = Mathf.Max(other.NominalSizePixels.x, other.NominalSizePixels.y) * 0.35f;
            float interactionDistance = ownRadius + otherRadius + spawnSeparationTiles * TilePixelSize * 0.25f;

            Vector2 delta = FieldDelta(
                centres[otherIndex],
                centres[cloudIndex],
                mass.Width,
                mass.Height);

            float distance = delta.magnitude;
            if (distance < 0.001f || distance >= interactionDistance)
                continue;

            float strength = 1f - distance / interactionDistance;
            force += (delta / distance) * strength * interCloudRepulsion;
        }

        return force;
    }

    private Vector2 SampleCurlNoise(SkeletonCloud cloud, Vector2 local)
    {
        float frequency = 0.025f * warpScale;
        float time = animationTime * warpAnimationSpeed;
        float epsilon = 0.75f;

        float left = SamplePerlin(cloud, local + Vector2.left * epsilon, frequency, time);
        float right = SamplePerlin(cloud, local + Vector2.right * epsilon, frequency, time);
        float down = SamplePerlin(cloud, local + Vector2.down * epsilon, frequency, time);
        float up = SamplePerlin(cloud, local + Vector2.up * epsilon, frequency, time);

        Vector2 gradient = new Vector2(right - left, up - down);
        if (gradient.sqrMagnitude < 0.000001f)
            return Vector2.zero;

        return Vector2.ClampMagnitude(new Vector2(gradient.y, -gradient.x) * 4f, 1f);
    }

    private float SamplePerlin(SkeletonCloud cloud, Vector2 local, float frequency, float time)
    {
        return Mathf.PerlinNoise(
            cloud.NoiseOffset.x + local.x * frequency + time,
            cloud.NoiseOffset.y + local.y * frequency - time * 0.73f);
    }

    private void ApplyFieldBoundary(SkeletonCloud cloud)
    {
        if (boundaryMode != FieldBoundaryMode.Wrap || cloud.Joints.Count == 0)
            return;

        Vector2 centre = CalculateJointCentroid(cloud);
        Vector2 shift = Vector2.zero;

        while (centre.x < 0f)
        {
            shift.x += mass.Width;
            centre.x += mass.Width;
        }

        while (centre.x >= mass.Width)
        {
            shift.x -= mass.Width;
            centre.x -= mass.Width;
        }

        while (centre.y < 0f)
        {
            shift.y += mass.Height;
            centre.y += mass.Height;
        }

        while (centre.y >= mass.Height)
        {
            shift.y -= mass.Height;
            centre.y -= mass.Height;
        }

        if (shift == Vector2.zero)
            return;

        for (int i = 0; i < cloud.Joints.Count; i++)
            cloud.Joints[i].Position += shift;
    }

    private Vector2 CalculateJointCentroid(SkeletonCloud cloud)
    {
        if (cloud.Joints.Count == 0)
            return Vector2.zero;

        Vector2 sum = Vector2.zero;
        for (int i = 0; i < cloud.Joints.Count; i++)
            sum += cloud.Joints[i].Position;

        return sum / cloud.Joints.Count;
    }

    // -------------------------------------------------------------------------
    // Skeleton surface -> pixel field
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts every hidden cloud skeleton into hard pixel material.
    ///
    /// Each cloud receives its own target pixel count, so a large cloud cannot steal
    /// all the material from a smaller one. Selected pixels are then combined into
    /// one global CloudMass for the Tilemap renderer.
    /// </summary>
    private bool RasteriseSkeletonField()
    {
        if (mass == null)
            return false;

        HashSet<int> nextPixels = new HashSet<int>();

        // Keep ownership order stable. Inter-cloud repulsion normally prevents heavy
        // overlap, and a stable order avoids pixels suddenly changing owner mid-animation.
        for (int cloudIndex = 0; cloudIndex < clouds.Count; cloudIndex++)
        {
            SkeletonCloud cloud = clouds[cloudIndex];
            RasteriseOneCloud(cloud, nextPixels);
        }

        bool changed = !rasterPixels.SetEquals(nextPixels);

        mass.ClearCloud();
        foreach (int index in nextPixels)
        {
            int x = index % mass.Width;
            int y = index / mass.Width;
            mass.SetCloud(x, y, true);
        }

        rasterPixels.Clear();
        foreach (int index in nextPixels)
            rasterPixels.Add(index);

        return changed;
    }

    private void RasteriseOneCloud(SkeletonCloud cloud, HashSet<int> occupiedByOtherClouds)
    {
        if (cloud.Edges.Count == 0 || cloud.TargetPixelCount <= 0)
        {
            cloud.PreviousPixels.Clear();
            return;
        }

        List<DensityPixel> candidates = BuildSkeletonCandidates(cloud, 1f);
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        HashSet<int> selected = new HashSet<int>();
        int desired = cloud.TargetPixelCount;

        for (int i = 0; i < candidates.Count && selected.Count < desired; i++)
        {
            DensityPixel candidate = candidates[i];

            if (occupiedByOtherClouds.Contains(candidate.Index))
                continue;

            if (mass.IsBlocked(candidate.X, candidate.Y))
                continue;

            if (selected.Add(candidate.Index))
                occupiedByOtherClouds.Add(candidate.Index);
        }

        // In wrap mode every cloud should retain its exact visible material count.
        // If another cloud or a blocked area consumed most local candidates, widen
        // the capsule support in stages and keep choosing the strongest remaining
        // skeleton-shaped candidates before ever falling back to a raw square scan.
        if (boundaryMode == FieldBoundaryMode.Wrap && selected.Count < desired)
        {
            FillMissingSkeletonPixels(cloud, desired, selected, occupiedByOtherClouds);
        }

        cloud.PreviousPixels.Clear();
        foreach (int index in selected)
            cloud.PreviousPixels.Add(index);
    }

    /// <summary>
    /// Builds density candidates from distance to the NEAREST EDGE, not distance to
    /// the nearest joint and not a sum over every joint's kernel. This is the change
    /// that stops the rendered result from being a filled disc - empty space between
    /// two joints on opposite sides of the ring stays empty unless an edge actually
    /// runs through it.
    /// </summary>
    private List<DensityPixel> BuildSkeletonCandidates(SkeletonCloud cloud, float supportExpansion)
    {
        Dictionary<int, DensityPixel> unique = new Dictionary<int, DensityPixel>();

        if (cloud.Edges.Count == 0)
            return new List<DensityPixel>();

        float maxSupport = 0f;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        for (int i = 0; i < cloud.Joints.Count; i++)
        {
            SkeletonJoint joint = cloud.Joints[i];
            float support = joint.Radius * skinThickness * supportExpansion;
            maxSupport = Mathf.Max(maxSupport, support);

            minX = Mathf.Min(minX, joint.Position.x - support);
            minY = Mathf.Min(minY, joint.Position.y - support);
            maxX = Mathf.Max(maxX, joint.Position.x + support);
            maxY = Mathf.Max(maxY, joint.Position.y + support);
        }

        int rawMinX = Mathf.FloorToInt(minX - 2f);
        int rawMinY = Mathf.FloorToInt(minY - 2f);
        int rawMaxX = Mathf.CeilToInt(maxX + 2f);
        int rawMaxY = Mathf.CeilToInt(maxY + 2f);

        if (boundaryMode == FieldBoundaryMode.FreeExit)
        {
            rawMinX = Mathf.Max(0, rawMinX);
            rawMinY = Mathf.Max(0, rawMinY);
            rawMaxX = Mathf.Min(mass.Width - 1, rawMaxX);
            rawMaxY = Mathf.Min(mass.Height - 1, rawMaxY);
        }
        else
        {
            Vector2 centre = CalculateJointCentroid(cloud);

            if (rawMaxX - rawMinX + 1 > mass.Width)
            {
                rawMinX = Mathf.FloorToInt(centre.x - mass.Width * 0.5f);
                rawMaxX = rawMinX + mass.Width - 1;
            }

            if (rawMaxY - rawMinY + 1 > mass.Height)
            {
                rawMinY = Mathf.FloorToInt(centre.y - mass.Height * 0.5f);
                rawMaxY = rawMinY + mass.Height - 1;
            }
        }

        if (rawMaxX < rawMinX || rawMaxY < rawMinY)
            return new List<DensityPixel>();

        Vector2 cloudCentre = CalculateJointCentroid(cloud);
        float falloffWidth = Mathf.Max(1f, maxSupport * 0.4f);

        for (int rawY = rawMinY; rawY <= rawMaxY; rawY++)
        {
            for (int rawX = rawMinX; rawX <= rawMaxX; rawX++)
            {
                if (!TryMapRawPixel(rawX, rawY, out int x, out int y))
                    continue;

                if (mass.IsBlocked(x, y))
                    continue;

                Vector2 sample = new Vector2(rawX + 0.5f, rawY + 0.5f);
                float signedDistance = DistanceToSkeleton(sample, cloud, supportExpansion);

                if (signedDistance > falloffWidth)
                    continue;

                float density = Mathf.Clamp01(1f - signedDistance / falloffWidth);
                density = density * density * (3f - 2f * density);

                if (density <= 0.0001f)
                    continue;

                float score = density;
                score -= Mathf.Max(0f, signedDistance) * 0.01f;
                score += SampleSurfaceNoise(cloud, sample, cloudCentre);

                int index = x + y * mass.Width;
                if (cloud.PreviousPixels.Contains(index))
                    score += surfaceMemory;

                DensityPixel pixelCandidate = new DensityPixel(x, y, index, score);

                if (!unique.TryGetValue(index, out DensityPixel existing) || pixelCandidate.Score > existing.Score)
                    unique[index] = pixelCandidate;
            }
        }

        return new List<DensityPixel>(unique.Values);
    }

    /// <summary>
    /// Signed distance from a sample point to the nearest capsule formed by any edge
    /// in the skeleton. Negative/zero means inside the skin.
    /// </summary>
    private float DistanceToSkeleton(Vector2 sample, SkeletonCloud cloud, float supportExpansion)
    {
        float best = float.MaxValue;

        for (int i = 0; i < cloud.Edges.Count; i++)
        {
            SkeletonEdge edge = cloud.Edges[i];
            Vector2 a = cloud.Joints[edge.JointA].Position;
            Vector2 b = cloud.Joints[edge.JointB].Position;

            Vector2 ab = b - a;
            float lengthSquared = Mathf.Max(0.0001f, ab.sqrMagnitude);
            float t = Mathf.Clamp01(Vector2.Dot(sample - a, ab) / lengthSquared);
            Vector2 closest = a + ab * t;

            float radiusAtT = Mathf.Lerp(
                cloud.Joints[edge.JointA].Radius,
                cloud.Joints[edge.JointB].Radius,
                t) * skinThickness * supportExpansion;

            float distance = Vector2.Distance(sample, closest) - radiusAtT;
            if (distance < best)
                best = distance;
        }

        return best;
    }

    private float SampleSurfaceNoise(SkeletonCloud cloud, Vector2 sample, Vector2 centre)
    {
        Vector2 local = sample - centre;
        float reference = Mathf.Max(8f, Mathf.Max(cloud.NominalSizePixels.x, cloud.NominalSizePixels.y));

        float timeLarge = animationTime * noiseDriftSpeed;
        float timeWarp = animationTime * warpAnimationSpeed;

        float warpFrequency = warpScale / reference;
        float wx = Mathf.PerlinNoise(
            cloud.NoiseOffset.x + local.x * warpFrequency + timeWarp,
            cloud.NoiseOffset.y + local.y * warpFrequency - timeWarp * 0.71f) - 0.5f;

        float wy = Mathf.PerlinNoise(
            cloud.NoiseOffset.x + 317.13f + local.x * warpFrequency - timeWarp * 0.63f,
            cloud.NoiseOffset.y + 911.77f + local.y * warpFrequency + timeWarp) - 0.5f;

        Vector2 warped = local + new Vector2(wx, wy) * warpStrength * reference;

        float largeFrequency = largeNoiseScale / reference;
        float mediumFrequency = mediumNoiseScale / reference;

        float large = Mathf.PerlinNoise(
            cloud.NoiseOffset.x + warped.x * largeFrequency + timeLarge,
            cloud.NoiseOffset.y + warped.y * largeFrequency + timeLarge * 0.39f) - 0.5f;

        float medium = Mathf.PerlinNoise(
            cloud.NoiseOffset.x + 173.9f + warped.x * mediumFrequency - timeLarge * 0.51f,
            cloud.NoiseOffset.y + 541.2f + warped.y * mediumFrequency + timeLarge * 0.83f) - 0.5f;

        return large * largeNoiseStrength + medium * mediumNoiseStrength;
    }

    /// <summary>
    /// Restores any material that did not fit inside the normal capsule support.
    /// Widens the skin thickness gradually in stages, following the actual skeleton
    /// shape at each stage, and only falls back to a plain nearest-skeleton-distance
    /// scan (never a square ring) if even a heavily widened skin still isn't enough.
    /// </summary>
    private void FillMissingSkeletonPixels(
        SkeletonCloud cloud,
        int desired,
        HashSet<int> selected,
        HashSet<int> occupiedByOtherClouds)
    {
        if (selected.Count >= desired)
            return;

        float[] expansionSteps = { 1.20f, 1.40f, 1.70f, 2.05f, 2.50f, 3.10f };

        for (int step = 0; step < expansionSteps.Length && selected.Count < desired; step++)
        {
            List<DensityPixel> expanded = BuildSkeletonCandidates(cloud, expansionSteps[step]);
            expanded.Sort((a, b) => b.Score.CompareTo(a.Score));

            for (int i = 0; i < expanded.Count && selected.Count < desired; i++)
            {
                DensityPixel candidate = expanded[i];

                if (selected.Contains(candidate.Index))
                    continue;

                if (occupiedByOtherClouds.Contains(candidate.Index))
                    continue;

                if (mass.IsBlocked(candidate.X, candidate.Y))
                    continue;

                selected.Add(candidate.Index);
                occupiedByOtherClouds.Add(candidate.Index);
            }
        }

        if (selected.Count < desired)
        {
            FillMissingByNearestSkeleton(cloud, desired, selected, occupiedByOtherClouds);
        }
    }

    private void FillMissingByNearestSkeleton(
        SkeletonCloud cloud,
        int desired,
        HashSet<int> selected,
        HashSet<int> occupiedByOtherClouds)
    {
        List<DensityPixel> candidates = new List<DensityPixel>();
        Vector2 centre = CalculateJointCentroid(cloud);

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                int index = x + y * mass.Width;

                if (selected.Contains(index) || occupiedByOtherClouds.Contains(index))
                    continue;

                if (mass.IsBlocked(x, y))
                    continue;

                Vector2 sample = new Vector2(x + 0.5f, y + 0.5f);
                float nearest = DistanceToSkeletonWrapped(cloud, sample);

                float score = -nearest;
                score += SampleSurfaceNoise(cloud, sample, centre) * 0.30f;

                candidates.Add(new DensityPixel(x, y, index, score));
            }
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        for (int i = 0; i < candidates.Count && selected.Count < desired; i++)
        {
            DensityPixel candidate = candidates[i];
            selected.Add(candidate.Index);
            occupiedByOtherClouds.Add(candidate.Index);
        }
    }

    private float DistanceToSkeletonWrapped(SkeletonCloud cloud, Vector2 sample)
    {
        float best = float.MaxValue;

        for (int i = 0; i < cloud.Edges.Count; i++)
        {
            SkeletonEdge edge = cloud.Edges[i];
            Vector2 a = cloud.Joints[edge.JointA].Position;
            Vector2 b = cloud.Joints[edge.JointB].Position;

            // Edges are always short compared to the field, so only the sample-to-a
            // offset needs wrap correction - a and b are never far enough apart to
            // straddle the seam independently.
            Vector2 sampleRelative = boundaryMode == FieldBoundaryMode.Wrap
                ? FieldDelta(a, sample, mass.Width, mass.Height)
                : sample - a;

            Vector2 ab = b - a;
            float lengthSquared = Mathf.Max(0.0001f, ab.sqrMagnitude);
            float t = Mathf.Clamp01(Vector2.Dot(sampleRelative, ab) / lengthSquared);
            Vector2 closestRelative = ab * t;

            float distance = (sampleRelative - closestRelative).magnitude;
            if (distance < best)
                best = distance;
        }

        return best;
    }

    // -------------------------------------------------------------------------
    // Blocked-space collision
    // -------------------------------------------------------------------------

    private Vector2 SampleBlockedRepulsion(Vector2 position, float radius)
    {
        if (mass == null || !blockedSpacePresent)
            return Vector2.zero;

        Vector2 force = Vector2.zero;
        int range = Mathf.Clamp(Mathf.CeilToInt(radius * 0.75f), 1, 6);
        int cx = Mathf.FloorToInt(position.x);
        int cy = Mathf.FloorToInt(position.y);

        for (int rawY = cy - range; rawY <= cy + range; rawY++)
        {
            for (int rawX = cx - range; rawX <= cx + range; rawX++)
            {
                if (!TryMapRawPixel(rawX, rawY, out int x, out int y))
                    continue;

                if (!mass.IsBlocked(x, y))
                    continue;

                Vector2 blockedCentre = new Vector2(rawX + 0.5f, rawY + 0.5f);
                Vector2 away = position - blockedCentre;
                float distance = away.magnitude;
                float influence = radius * 1.25f;

                if (distance < 0.0001f || distance >= influence)
                    continue;

                force +=
                    (away / distance) *
                    (1f - distance / influence) *
                    Mathf.Max(2f, edgeStiffness * 0.6f + 1.5f);
            }
        }

        return force;
    }

    private void ResolveBlockedCollision(
        Vector2 oldPosition,
        ref Vector2 newPosition,
        ref Vector2 velocity,
        float radius)
    {
        if (!TryMapWorldPosition(newPosition, out int x, out int y))
            return;

        if (!mass.IsBlocked(x, y))
            return;

        Vector2 legal = FindNearestUnblockedPosition(
            oldPosition,
            newPosition,
            Mathf.CeilToInt(radius + 6f));

        Vector2 normal = legal - newPosition;
        newPosition = legal;

        if (normal.sqrMagnitude > 0.0001f)
        {
            normal.Normalize();
            velocity = Vector2.Reflect(velocity, normal) * 0.55f;
        }
        else
        {
            velocity *= -0.35f;
        }
    }

    private Vector2 FindNearestUnblockedPosition(Vector2 fallback, Vector2 around, int searchRadius)
    {
        int cx = Mathf.FloorToInt(around.x);
        int cy = Mathf.FloorToInt(around.y);

        Vector2 best = fallback;
        float bestDistance = float.MaxValue;

        for (int radius = 0; radius <= searchRadius; radius++)
        {
            for (int rawY = cy - radius; rawY <= cy + radius; rawY++)
            {
                for (int rawX = cx - radius; rawX <= cx + radius; rawX++)
                {
                    if (!TryMapRawPixel(rawX, rawY, out int x, out int y))
                        continue;

                    if (mass.IsBlocked(x, y))
                        continue;

                    Vector2 candidate = new Vector2(rawX + 0.5f, rawY + 0.5f);
                    float distance = (candidate - around).sqrMagnitude;
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
            }

            if (bestDistance < float.MaxValue)
                break;
        }

        return best;
    }

    private void ExpelJointsFromBlockedSpace()
    {
        for (int cloudIndex = 0; cloudIndex < clouds.Count; cloudIndex++)
        {
            SkeletonCloud cloud = clouds[cloudIndex];

            for (int i = 0; i < cloud.Joints.Count; i++)
            {
                SkeletonJoint joint = cloud.Joints[i];

                if (!TryMapWorldPosition(joint.Position, out int x, out int y))
                    continue;

                if (!mass.IsBlocked(x, y))
                    continue;

                Vector2 old = joint.Position;
                joint.Position = FindNearestUnblockedPosition(
                    old,
                    old,
                    Mathf.CeilToInt(joint.Radius + 10f));

                Vector2 escape = joint.Position - old;
                if (escape.sqrMagnitude > 0.0001f)
                {
                    joint.Velocity +=
                        escape.normalized *
                        Mathf.Min(maxJointSpeed, 1.5f + internalPressure * 0.5f);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Coordinate helpers
    // -------------------------------------------------------------------------

    private bool TryMapWorldPosition(Vector2 position, out int x, out int y)
    {
        return TryMapRawPixel(
            Mathf.FloorToInt(position.x),
            Mathf.FloorToInt(position.y),
            out x,
            out y);
    }

    private bool TryMapRawPixel(int rawX, int rawY, out int x, out int y)
    {
        if (boundaryMode == FieldBoundaryMode.Wrap)
        {
            x = PositiveModulo(rawX, mass.Width);
            y = PositiveModulo(rawY, mass.Height);
            return true;
        }

        x = rawX;
        y = rawY;
        return x >= 0 && y >= 0 && x < mass.Width && y < mass.Height;
    }

    private static int PositiveModulo(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private Vector2 FieldDelta(Vector2 from, Vector2 to, int width, int height)
    {
        Vector2 delta = to - from;

        if (boundaryMode != FieldBoundaryMode.Wrap)
            return delta;

        float halfWidth = width * 0.5f;
        float halfHeight = height * 0.5f;

        if (delta.x > halfWidth)
            delta.x -= width;
        else if (delta.x < -halfWidth)
            delta.x += width;

        if (delta.y > halfHeight)
            delta.y -= height;
        else if (delta.y < -halfHeight)
            delta.y += height;

        return delta;
    }

    // -------------------------------------------------------------------------
    // General component helpers
    // -------------------------------------------------------------------------

    private int EstimateTotalTargetPixels()
    {
        float averageWidth = (minimumCloudSize.x + maximumCloudSize.x) * 0.5f * TilePixelSize;
        float averageHeight = (minimumCloudSize.y + maximumCloudSize.y) * 0.5f * TilePixelSize;
        return Mathf.Max(1, Mathf.RoundToInt(cloudCount * averageWidth * averageHeight * cloudFill));
    }

    private bool EnsureGenerated()
    {
        if (mass != null && clouds.Count > 0)
        {
            EnsureRenderer();
            return renderer != null;
        }

        GenerateCloud();
        return mass != null && clouds.Count > 0 && renderer != null;
    }

    private void EnsureRenderer()
    {
        if (renderer == null && cloudTilemap != null)
        {
            renderer = new CloudTileRenderer(
                cloudTilemap,
                TilePixelSize,
                pixelsPerUnit,
                cloudColour);
        }
    }

    private bool ValidateTarget()
    {
        if (cloudTilemap == null)
        {
            Debug.LogWarning(
                $"{nameof(CloudFieldPainter)} on '{name}' needs a target Tilemap.",
                this);
            return false;
        }

        return true;
    }

    private RectInt TileBoundsToPixelRect(BoundsInt tileArea)
    {
        int tileMinX = Mathf.Max(tileArea.xMin, origin.x);
        int tileMinY = Mathf.Max(tileArea.yMin, origin.y);
        int tileMaxX = Mathf.Min(tileArea.xMax, origin.x + size.x);
        int tileMaxY = Mathf.Min(tileArea.yMax, origin.y + size.y);

        if (tileMaxX <= tileMinX || tileMaxY <= tileMinY)
            return new RectInt();

        int localTileX = tileMinX - origin.x;
        int localTileY = tileMinY - origin.y;
        int widthTiles = tileMaxX - tileMinX;
        int heightTiles = tileMaxY - tileMinY;

        return new RectInt(
            localTileX * TilePixelSize,
            localTileY * TilePixelSize,
            widthTiles * TilePixelSize,
            heightTiles * TilePixelSize);
    }

    private bool TryTileToLocalTile(Vector3Int tilePosition, out int localTileX, out int localTileY)
    {
        localTileX = tilePosition.x - origin.x;
        localTileY = tilePosition.y - origin.y;

        return
            tilePosition.z == origin.z &&
            localTileX >= 0 &&
            localTileY >= 0 &&
            localTileX < size.x &&
            localTileY < size.y;
    }

    private void ReportMassWarningIfNeeded()
    {
        if (mass == null)
            return;

        // Free Exit intentionally allows visible mass to fall as clouds travel outside
        // the render field, so a target-count warning would be misleading in that mode.
        if (boundaryMode == FieldBoundaryMode.FreeExit)
            return;

        int current = mass.CountCloudPixels();
        if (current == mass.TargetPixelCount)
            return;

        if (mass.AvailableCapacity < mass.TargetPixelCount)
        {
            Debug.LogWarning(
                $"{nameof(CloudFieldPainter)} '{name}' cannot preserve {mass.TargetPixelCount} visible cloud pixels because only " +
                $"{mass.AvailableCapacity} unblocked field pixels remain. Current visible mass: {current}.",
                this);
        }
        else
        {
            Debug.LogWarning(
                $"{nameof(CloudFieldPainter)} '{name}' currently placed {current}/{mass.TargetPixelCount} visible cloud pixels. " +
                "Heavy cloud overlap or blocked space may be consuming the remaining local surface area.",
                this);
        }
    }

    // -------------------------------------------------------------------------
    // Runtime debug overlay
    // -------------------------------------------------------------------------

    /// <summary>
    /// Updates a real MeshRenderer overlay for the hidden skeleton simulation.
    /// Unlike Gizmos, this is ordinary rendered geometry and therefore remains
    /// visible even when Unity's Scene/Game Gizmos switches are disabled.
    /// </summary>
    private void UpdateDebugOverlayRenderer()
    {
        if (!ShouldShowDebugOverlay())
        {
            if (debugOverlayRenderer != null)
                debugOverlayRenderer.enabled = false;

            return;
        }

        EnsureDebugOverlayRenderer();
        if (debugOverlayRenderer == null || debugOverlayMesh == null)
            return;

        debugOverlayRenderer.enabled = true;
        BuildDebugOverlayMesh();
    }

    private bool ShouldShowDebugOverlay()
    {
        if (!drawSimulationDebug || !isActiveAndEnabled || cloudTilemap == null)
            return false;

#if UNITY_EDITOR
        if (debugOnlyWhenSelected)
        {
            GameObject selected = Selection.activeGameObject;
            if (selected == null)
                return false;

            if (selected != gameObject && !selected.transform.IsChildOf(transform))
                return false;
        }
#endif

        return true;
    }

    private void EnsureDebugOverlayRenderer()
    {
        if (debugOverlayObject != null &&
            debugOverlayFilter != null &&
            debugOverlayRenderer != null &&
            debugOverlayMesh != null &&
            debugOverlayMaterial != null)
        {
            SyncDebugOverlaySorting();
            return;
        }

        DisposeDebugOverlayRenderer();

        debugOverlayObject = new GameObject($"{name}_CloudSimulationDebug");
        debugOverlayObject.hideFlags = HideFlags.HideAndDontSave;
        debugOverlayObject.transform.position = Vector3.zero;
        debugOverlayObject.transform.rotation = Quaternion.identity;
        debugOverlayObject.transform.localScale = Vector3.one;

        debugOverlayFilter = debugOverlayObject.AddComponent<MeshFilter>();
        debugOverlayRenderer = debugOverlayObject.AddComponent<MeshRenderer>();

        debugOverlayMesh = new Mesh
        {
            name = $"{name}_CloudSimulationDebugMesh",
            hideFlags = HideFlags.HideAndDontSave
        };
        debugOverlayMesh.MarkDynamic();
        debugOverlayFilter.sharedMesh = debugOverlayMesh;

        Shader debugShader = Shader.Find("Sprites/Default");
        if (debugShader == null)
            debugShader = Shader.Find("Unlit/Transparent");

        if (debugShader == null)
        {
            Debug.LogWarning(
                $"{nameof(CloudFieldPainter)} '{name}' could not find a shader for the simulation debug overlay.",
                this);
            return;
        }

        debugOverlayMaterial = new Material(debugShader)
        {
            name = $"{name}_CloudSimulationDebugMaterial",
            hideFlags = HideFlags.HideAndDontSave,
            renderQueue = 5000
        };

        debugOverlayRenderer.sharedMaterial = debugOverlayMaterial;
        debugOverlayRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        debugOverlayRenderer.receiveShadows = false;
        debugOverlayRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        debugOverlayRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        SyncDebugOverlaySorting();
    }

    private void SyncDebugOverlaySorting()
    {
        if (debugOverlayRenderer == null || cloudTilemap == null)
            return;

        Renderer sourceRenderer = cloudTilemap.GetComponent<Renderer>();
        if (sourceRenderer != null)
        {
            debugOverlayRenderer.sortingLayerID = sourceRenderer.sortingLayerID;
            debugOverlayRenderer.sortingOrder = sourceRenderer.sortingOrder + 1000;
        }
    }

    private void BuildDebugOverlayMesh()
    {
        debugOverlayVertices.Clear();
        debugOverlayColours.Clear();
        debugOverlayTriangles.Clear();

        float thinLine = Mathf.Max(0.0025f, SimulationDistanceToWorld(0.14f * debugMarkerScale));
        float normalLine = Mathf.Max(0.004f, SimulationDistanceToWorld(0.22f * debugMarkerScale));
        float strongLine = Mathf.Max(0.006f, SimulationDistanceToWorld(0.32f * debugMarkerScale));

        if (debugDrawFieldGuide)
        {
            Vector2 fieldPixels = new Vector2(size.x * TilePixelSize, size.y * TilePixelSize);
            Vector3 a = SimulationToDebugWorld(Vector2.zero);
            Vector3 b = SimulationToDebugWorld(new Vector2(fieldPixels.x, 0f));
            Vector3 c = SimulationToDebugWorld(fieldPixels);
            Vector3 d = SimulationToDebugWorld(new Vector2(0f, fieldPixels.y));

            Color fieldColour = new Color(1f, 0.02f, 0.48f, 1f);
            AddDebugLine(a, b, strongLine, fieldColour);
            AddDebugLine(b, c, strongLine, fieldColour);
            AddDebugLine(c, d, strongLine, fieldColour);
            AddDebugLine(d, a, strongLine, fieldColour);

            float cross = SimulationDistanceToWorld(2.5f * debugMarkerScale);
            Vector3 right = cloudTilemap.layoutGrid.transform.right.normalized * cross;
            Vector3 up = cloudTilemap.layoutGrid.transform.up.normalized * cross;
            AddDebugLine(a - right, a + right, strongLine, fieldColour);
            AddDebugLine(a - up, a + up, strongLine, fieldColour);
            AddDebugDisc(a, SimulationDistanceToWorld(0.75f * debugMarkerScale), fieldColour, 16);
        }

        if (mass != null && clouds.Count > 0)
        {
            for (int cloudIndex = 0; cloudIndex < clouds.Count; cloudIndex++)
            {
                SkeletonCloud cloud = clouds[cloudIndex];
                if (cloud.Joints.Count == 0)
                    continue;

                Vector2 centroid = CalculateJointCentroid(cloud);
                Vector3 centroidWorld = SimulationToDebugWorld(centroid);

                if (debugDrawEdges)
                {
                    Color skinColour = new Color(0.11f, 0.78f, 0.62f, 0.9f);
                    for (int i = 0; i < cloud.Edges.Count; i++)
                    {
                        SkeletonEdge edge = cloud.Edges[i];
                        Vector3 a = SimulationToDebugWorld(cloud.Joints[edge.JointA].Position);
                        Vector3 b = SimulationToDebugWorld(cloud.Joints[edge.JointB].Position);
                        float thickness = SimulationDistanceToWorld(
                            Mathf.Lerp(cloud.Joints[edge.JointA].Radius, cloud.Joints[edge.JointB].Radius, 0.5f) * skinThickness);
                        AddDebugLine(a, b, Mathf.Max(strongLine, thickness), skinColour);
                    }
                }

                if (debugDrawJoints)
                {
                    Color jointColour = new Color(0.02f, 1f, 1f, 0.98f);
                    for (int i = 0; i < cloud.Joints.Count; i++)
                    {
                        SkeletonJoint joint = cloud.Joints[i];
                        Vector3 jointWorld = SimulationToDebugWorld(joint.Position);
                        AddDebugRing(jointWorld, SimulationDistanceToWorld(joint.Radius), thinLine, jointColour, 20);
                        AddDebugDisc(jointWorld, SimulationDistanceToWorld(0.45f * debugMarkerScale), jointColour, 12);

                        if (debugDrawJointVelocities)
                        {
                            Vector2 apparentVelocity = joint.Velocity + cloud.TranslationVelocity;
                            AddDebugLine(
                                jointWorld,
                                SimulationToDebugWorld(joint.Position + apparentVelocity * debugVelocityScale),
                                normalLine,
                                new Color(0.15f, 1f, 0.2f, 1f));
                        }
                    }
                }

                if (debugDrawCloudCentres)
                {
                    Color centreColour = new Color(1f, 0.08f, 0.88f, 1f);
                    float centreRadius = SimulationDistanceToWorld(1.35f * debugMarkerScale);
                    AddDebugRing(centroidWorld, centreRadius, normalLine, centreColour, 28);
                    AddDebugDisc(centroidWorld, SimulationDistanceToWorld(0.34f * debugMarkerScale), centreColour, 14);

                    Vector3 translationEnd = SimulationToDebugWorld(centroid + cloud.TranslationVelocity * debugVelocityScale);
                    AddDebugLine(centroidWorld, translationEnd, strongLine, centreColour);

                    // Small spin indicator: an arc around the centroid, orientation
                    // sampled from the average sign of joint SpinScale.
                    float spinSign = 0f;
                    for (int i = 0; i < cloud.Joints.Count; i++)
                        spinSign += Mathf.Sign(cloud.Joints[i].SpinScale);

                    Color spinColour = new Color(1f, 0.6f, 0.02f, 0.9f);
                    float spinRadius = SimulationDistanceToWorld(0.7f * debugMarkerScale);
                    int arcSegments = 10;
                    float arcSpan = spinSign >= 0f ? 4.6f : -4.6f;

                    Vector3 previous = centroidWorld +
                        cloudTilemap.layoutGrid.transform.right.normalized * spinRadius;

                    for (int i = 1; i <= arcSegments; i++)
                    {
                        float angle = (i / (float)arcSegments) * arcSpan;
                        Vector3 offset =
                            cloudTilemap.layoutGrid.transform.right.normalized * Mathf.Cos(angle) * spinRadius +
                            cloudTilemap.layoutGrid.transform.up.normalized * Mathf.Sin(angle) * spinRadius;
                        Vector3 next = centroidWorld + offset;
                        AddDebugLine(previous, next, thinLine, spinColour);
                        previous = next;
                    }
                }
            }
        }

        debugOverlayMesh.Clear();

        if (debugOverlayVertices.Count == 0)
            return;

        debugOverlayMesh.SetVertices(debugOverlayVertices);
        debugOverlayMesh.SetColors(debugOverlayColours);
        debugOverlayMesh.SetTriangles(debugOverlayTriangles, 0, true);
        debugOverlayMesh.RecalculateBounds();
    }

    private void AddDebugLine(Vector3 start, Vector3 end, float thickness, Color colour)
    {
        Vector3 delta = end - start;
        if (delta.sqrMagnitude < 0.0000001f)
            return;

        Vector3 planeNormal = cloudTilemap != null
            ? cloudTilemap.layoutGrid.transform.forward.normalized
            : Vector3.forward;

        Vector3 perpendicular = Vector3.Cross(planeNormal, delta.normalized);
        if (perpendicular.sqrMagnitude < 0.000001f)
            perpendicular = Vector3.up;

        perpendicular.Normalize();
        perpendicular *= thickness * 0.5f;

        int baseIndex = debugOverlayVertices.Count;
        debugOverlayVertices.Add(start - perpendicular);
        debugOverlayVertices.Add(start + perpendicular);
        debugOverlayVertices.Add(end + perpendicular);
        debugOverlayVertices.Add(end - perpendicular);

        debugOverlayColours.Add(colour);
        debugOverlayColours.Add(colour);
        debugOverlayColours.Add(colour);
        debugOverlayColours.Add(colour);

        debugOverlayTriangles.Add(baseIndex + 0);
        debugOverlayTriangles.Add(baseIndex + 1);
        debugOverlayTriangles.Add(baseIndex + 2);
        debugOverlayTriangles.Add(baseIndex + 0);
        debugOverlayTriangles.Add(baseIndex + 2);
        debugOverlayTriangles.Add(baseIndex + 3);
    }

    private void AddDebugRing(Vector3 centre, float radius, float thickness, Color colour, int segments)
    {
        if (radius <= 0f)
            return;

        segments = Mathf.Clamp(segments, 8, 64);
        Transform gridTransform = cloudTilemap != null ? cloudTilemap.layoutGrid.transform : transform;
        Vector3 right = gridTransform.right.normalized;
        Vector3 up = gridTransform.up.normalized;

        Vector3 previous = centre + right * radius;
        for (int i = 1; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = centre + (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * radius;
            AddDebugLine(previous, next, thickness, colour);
            previous = next;
        }
    }

    private void AddDebugDisc(Vector3 centre, float radius, Color colour, int segments)
    {
        if (radius <= 0f)
            return;

        segments = Mathf.Clamp(segments, 6, 32);
        Transform gridTransform = cloudTilemap != null ? cloudTilemap.layoutGrid.transform : transform;
        Vector3 right = gridTransform.right.normalized;
        Vector3 up = gridTransform.up.normalized;

        int centreIndex = debugOverlayVertices.Count;
        debugOverlayVertices.Add(centre);
        debugOverlayColours.Add(colour);

        for (int i = 0; i <= segments; i++)
        {
            float angle = (i / (float)segments) * Mathf.PI * 2f;
            debugOverlayVertices.Add(centre + (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)) * radius);
            debugOverlayColours.Add(colour);
        }

        for (int i = 0; i < segments; i++)
        {
            debugOverlayTriangles.Add(centreIndex);
            debugOverlayTriangles.Add(centreIndex + i + 1);
            debugOverlayTriangles.Add(centreIndex + i + 2);
        }
    }

    private void DisposeDebugOverlayRenderer()
    {
        if (debugOverlayMesh != null)
        {
            if (Application.isPlaying)
                Destroy(debugOverlayMesh);
            else
                DestroyImmediate(debugOverlayMesh);
        }

        if (debugOverlayMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(debugOverlayMaterial);
            else
                DestroyImmediate(debugOverlayMaterial);
        }

        if (debugOverlayObject != null)
        {
            if (Application.isPlaying)
                Destroy(debugOverlayObject);
            else
                DestroyImmediate(debugOverlayObject);
        }

        debugOverlayObject = null;
        debugOverlayFilter = null;
        debugOverlayRenderer = null;
        debugOverlayMesh = null;
        debugOverlayMaterial = null;
    }

    // -------------------------------------------------------------------------
    // Simulation debug drawing (Gizmo fallback)
    // -------------------------------------------------------------------------

    private void OnDrawGizmos()
    {
        if (!drawSimulationDebug || debugOnlyWhenSelected)
            return;

        DrawSimulationDebugGizmos();
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawSimulationDebug || !debugOnlyWhenSelected)
            return;

        DrawSimulationDebugGizmos();
    }

    private void DrawSimulationDebugGizmos()
    {
        if (cloudTilemap == null)
            return;

        DrawDebugFieldGuideGizmos();

        if (mass == null || clouds.Count == 0)
            return;

        for (int cloudIndex = 0; cloudIndex < clouds.Count; cloudIndex++)
        {
            SkeletonCloud cloud = clouds[cloudIndex];
            if (cloud.Joints.Count == 0)
                continue;

            Vector2 centroid = CalculateJointCentroid(cloud);
            Vector3 centroidWorld = SimulationToDebugWorld(centroid);

            if (debugDrawEdges)
            {
                Gizmos.color = new Color(0.11f, 0.78f, 0.62f, 0.9f);
                for (int i = 0; i < cloud.Edges.Count; i++)
                {
                    SkeletonEdge edge = cloud.Edges[i];
                    Gizmos.DrawLine(
                        SimulationToDebugWorld(cloud.Joints[edge.JointA].Position),
                        SimulationToDebugWorld(cloud.Joints[edge.JointB].Position));
                }
            }

            if (debugDrawJoints)
            {
                for (int i = 0; i < cloud.Joints.Count; i++)
                {
                    SkeletonJoint joint = cloud.Joints[i];
                    Vector3 jointWorld = SimulationToDebugWorld(joint.Position);

                    Gizmos.color = new Color(0.02f, 1f, 1f, 0.95f);
                    Gizmos.DrawWireSphere(jointWorld, SimulationDistanceToWorld(joint.Radius));
                    Gizmos.DrawSphere(jointWorld, SimulationDistanceToWorld(0.48f * debugMarkerScale));

                    if (debugDrawJointVelocities)
                    {
                        Gizmos.color = new Color(0.15f, 1f, 0.2f, 1f);
                        Vector2 apparentVelocity = joint.Velocity + cloud.TranslationVelocity;
                        Gizmos.DrawLine(
                            jointWorld,
                            SimulationToDebugWorld(joint.Position + apparentVelocity * debugVelocityScale));
                    }
                }
            }

            if (debugDrawCloudCentres)
            {
                Gizmos.color = new Color(1f, 0.15f, 0.9f, 1f);
                float centreRadius = SimulationDistanceToWorld(1.35f * debugMarkerScale);
                Gizmos.DrawWireSphere(centroidWorld, centreRadius);
                Gizmos.DrawSphere(centroidWorld, SimulationDistanceToWorld(0.28f * debugMarkerScale));
                Gizmos.DrawLine(
                    centroidWorld,
                    SimulationToDebugWorld(centroid + cloud.TranslationVelocity * debugVelocityScale));
            }
        }

#if UNITY_EDITOR
        DrawSimulationDebugHandles();
#endif
    }

    private void DrawDebugFieldGuideGizmos()
    {
        if (!debugDrawFieldGuide || cloudTilemap == null)
            return;

        Vector2 fieldPixels = new Vector2(size.x * TilePixelSize, size.y * TilePixelSize);
        Vector3 a = SimulationToDebugWorld(Vector2.zero);
        Vector3 b = SimulationToDebugWorld(new Vector2(fieldPixels.x, 0f));
        Vector3 c = SimulationToDebugWorld(fieldPixels);
        Vector3 d = SimulationToDebugWorld(new Vector2(0f, fieldPixels.y));

        Gizmos.color = new Color(1f, 0.05f, 0.55f, 1f);
        Gizmos.DrawLine(a, b);
        Gizmos.DrawLine(b, c);
        Gizmos.DrawLine(c, d);
        Gizmos.DrawLine(d, a);

        float cross = SimulationDistanceToWorld(2.0f * debugMarkerScale);
        Vector3 right = cloudTilemap.layoutGrid.transform.right * cross;
        Vector3 up = cloudTilemap.layoutGrid.transform.up * cross;
        Gizmos.DrawLine(a - right, a + right);
        Gizmos.DrawLine(a - up, a + up);
        Gizmos.DrawSphere(a, SimulationDistanceToWorld(0.7f * debugMarkerScale));
    }

#if UNITY_EDITOR
    private void DrawSimulationDebugHandles()
    {
        // Handles with zTest=Always are intentionally drawn through Tilemap sprites.
        // This is the most reliable Scene-view representation of the hidden physics.
        UnityEngine.Rendering.CompareFunction previousZTest = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;

        try
        {
            Transform gridTransform = cloudTilemap.layoutGrid.transform;
            Vector3 normal = gridTransform.forward;

            if (debugDrawFieldGuide)
            {
                Vector2 fieldPixels = new Vector2(size.x * TilePixelSize, size.y * TilePixelSize);
                Vector3 a = SimulationToDebugWorld(Vector2.zero);
                Vector3 b = SimulationToDebugWorld(new Vector2(fieldPixels.x, 0f));
                Vector3 c = SimulationToDebugWorld(fieldPixels);
                Vector3 d = SimulationToDebugWorld(new Vector2(0f, fieldPixels.y));

                Handles.color = new Color(1f, 0.05f, 0.55f, 1f);
                Handles.DrawAAPolyLine(4f, a, b, c, d, a);
                Handles.Label(a, "  CLOUD DEBUG");
            }

            if (mass == null || clouds.Count == 0)
                return;

            for (int cloudIndex = 0; cloudIndex < clouds.Count; cloudIndex++)
            {
                SkeletonCloud cloud = clouds[cloudIndex];
                if (cloud.Joints.Count == 0)
                    continue;

                Vector2 centroid = CalculateJointCentroid(cloud);
                Vector3 centroidWorld = SimulationToDebugWorld(centroid);

                if (debugDrawEdges)
                {
                    Handles.color = new Color(0.11f, 0.78f, 0.62f, 1f);
                    for (int i = 0; i < cloud.Edges.Count; i++)
                    {
                        SkeletonEdge edge = cloud.Edges[i];
                        Handles.DrawAAPolyLine(
                            3f,
                            SimulationToDebugWorld(cloud.Joints[edge.JointA].Position),
                            SimulationToDebugWorld(cloud.Joints[edge.JointB].Position));
                    }
                }

                if (debugDrawJoints)
                {
                    for (int i = 0; i < cloud.Joints.Count; i++)
                    {
                        SkeletonJoint joint = cloud.Joints[i];
                        Vector3 jointWorld = SimulationToDebugWorld(joint.Position);

                        Handles.color = new Color(0.02f, 1f, 1f, 1f);
                        Handles.DrawWireDisc(jointWorld, normal, SimulationDistanceToWorld(joint.Radius));

                        float centreSize = SimulationDistanceToWorld(0.48f * debugMarkerScale);
                        Handles.SphereHandleCap(0, jointWorld, Quaternion.identity, centreSize * 2f, EventType.Repaint);

                        if (debugDrawJointVelocities)
                        {
                            Handles.color = new Color(0.15f, 1f, 0.2f, 1f);
                            Vector2 apparentVelocity = joint.Velocity + cloud.TranslationVelocity;
                            Handles.DrawAAPolyLine(
                                3f,
                                jointWorld,
                                SimulationToDebugWorld(joint.Position + apparentVelocity * debugVelocityScale));
                        }
                    }
                }

                if (debugDrawCloudCentres)
                {
                    Handles.color = new Color(1f, 0.15f, 0.9f, 1f);
                    float radius = SimulationDistanceToWorld(1.35f * debugMarkerScale);
                    Handles.DrawWireDisc(centroidWorld, normal, radius);
                    Handles.SphereHandleCap(0, centroidWorld, Quaternion.identity, radius * 0.45f, EventType.Repaint);
                }
            }
        }
        finally
        {
            Handles.zTest = previousZTest;
        }
    }
#endif

    private Vector3 SimulationToWorld(Vector2 simulationPosition)
    {
        if (cloudTilemap == null)
            return transform.position;

        GridLayout grid = cloudTilemap.layoutGrid;
        Vector3 cellPosition = new Vector3(
            origin.x + simulationPosition.x / TilePixelSize,
            origin.y + simulationPosition.y / TilePixelSize,
            origin.z);

        Vector3 local = grid.CellToLocalInterpolated(cellPosition);
        return grid.transform.TransformPoint(local);
    }

    private Vector3 SimulationToDebugWorld(Vector2 simulationPosition)
    {
        Vector3 world = SimulationToWorld(simulationPosition);

        if (cloudTilemap == null || debugOverlayDepth <= 0f)
            return world;

        return world - cloudTilemap.layoutGrid.transform.forward * debugOverlayDepth;
    }

    private float SimulationDistanceToWorld(float simulationPixels)
    {
        if (cloudTilemap == null)
            return simulationPixels / TilePixelSize;

        Vector3 originWorld = SimulationToWorld(Vector2.zero);
        Vector3 xWorld = SimulationToWorld(new Vector2(simulationPixels, 0f));
        Vector3 yWorld = SimulationToWorld(new Vector2(0f, simulationPixels));

        float xDistance = Vector3.Distance(originWorld, xWorld);
        float yDistance = Vector3.Distance(originWorld, yWorld);
        return Mathf.Max(0.0001f, (xDistance + yDistance) * 0.5f);
    }

    private void DisposeRenderer()
    {
        if (renderer == null)
            return;

        renderer.Dispose();
        renderer = null;
    }

#if UNITY_EDITOR
    private void QueueRuntimeInspectorApply()
    {
        if (runtimeInspectorApplyQueued)
            return;

        runtimeInspectorApplyQueued = true;
        EditorApplication.delayCall += ApplyRuntimeInspectorChangesFromDelay;
    }

    private void ApplyRuntimeInspectorChangesFromDelay()
    {
        runtimeInspectorApplyQueued = false;

        if (this == null || !Application.isPlaying || !isActiveAndEnabled)
            return;

        ApplyInspectorChangesNow();
        EditorUtility.SetDirty(this);
        SceneView.RepaintAll();
    }

    private void QueueEditorRegeneration()
    {
        if (editorRegenerationQueued)
            return;

        editorRegenerationQueued = true;
        EditorApplication.delayCall += RegenerateFromEditorDelay;
    }

    private void RegenerateFromEditorDelay()
    {
        editorRegenerationQueued = false;

        if (this == null || Application.isPlaying || !liveRegenerate || !isActiveAndEnabled)
            return;

        GenerateCloud();
        UpdateDebugOverlayRenderer();
        SceneView.RepaintAll();
    }
#endif

    // -------------------------------------------------------------------------
    // Small runtime data objects
    // -------------------------------------------------------------------------

    private sealed class SkeletonJoint
    {
        public Vector2 Position;
        public Vector2 Velocity;     // local to the cloud - never includes translation
        public float Radius;
        public float PressureScale;
        public float SpinScale;      // sign controls spin direction; a few counter-spun
                                     // joints per ring add a nice kink instead of a
                                     // perfectly uniform rotation
    }

    private sealed class SkeletonEdge
    {
        public int JointA;
        public int JointB;
        public float RestLength;
        public float MinLength;
        public float MaxLength;
    }

    private sealed class SkeletonCloud
    {
        public int Id;
        public int Seed;
        public Vector2Int NominalSizePixels;
        public int TargetPixelCount;
        public Vector2 NoiseOffset;
        public Vector2 VelocityOffset;
        public float SpeedScale;
        public Vector2 TranslationVelocity; // whole-cloud drift, separate from joint churn
        public readonly List<SkeletonJoint> Joints = new List<SkeletonJoint>();
        public readonly List<SkeletonEdge> Edges = new List<SkeletonEdge>();
        public readonly HashSet<int> PreviousPixels = new HashSet<int>();
    }

    private struct SpawnInfo
    {
        public Vector2 Centre;
        public Vector2Int NominalSizePixels;
        public int TargetPixels;
        public float ApproximateRadius;
    }

    private readonly struct DensityPixel
    {
        public readonly int X;
        public readonly int Y;
        public readonly int Index;
        public readonly float Score;

        public DensityPixel(int x, int y, int index, float score)
        {
            X = x;
            Y = y;
            Index = index;
            Score = score;
        }
    }
}

#if UNITY_EDITOR
/// <summary>
/// Built-in Inspector helpers. Kept in this same file so the field/population update
/// still requires replacing only CloudFieldPainter.cs.
/// </summary>
[CustomEditor(typeof(CloudFieldPainter))]
internal sealed class CloudFieldPainterEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        CloudFieldPainter painter = (CloudFieldPainter)target;

        EditorGUILayout.Space(12f);
        EditorGUILayout.LabelField("Cloud Field Shuffle", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Population changes cloud count, per-cloud size range and spacing. The other buttons only shuffle their own group. Field size, Tilemap and colour are kept intact.",
            MessageType.None);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Population"))
                RunAction(painter, "Shuffle Cloud Population", painter.ShufflePopulation);

            if (GUILayout.Button("Skeleton"))
                RunAction(painter, "Shuffle Cloud Skeleton", painter.ShuffleSkeleton);

            if (GUILayout.Button("Noise"))
                RunAction(painter, "Shuffle Cloud Noise", painter.ShuffleNoise);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Animation"))
                RunAction(painter, "Shuffle Cloud Animation", painter.ShuffleAnimation);

            if (GUILayout.Button("Motion"))
                RunAction(painter, "Shuffle Cloud Motion", painter.ShuffleMotion);

            if (GUILayout.Button("Seed Only"))
                RunAction(painter, "Shuffle Cloud Seed", painter.ShuffleSeed);
        }

        if (GUILayout.Button("SHUFFLE ALL"))
            RunAction(painter, "Shuffle Entire Cloud Field", painter.ShuffleAll);

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Animation Pace", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "These change field speed, hidden-skeleton update rate, substeps and joint damping together.",
            MessageType.None);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Gentle"))
                RunPace(painter, 0, "Gentle Cloud Animation");

            if (GUILayout.Button("Medium"))
                RunPace(painter, 1, "Medium Cloud Animation");

            if (GUILayout.Button("Fast"))
                RunPace(painter, 2, "Fast Cloud Animation");

            if (GUILayout.Button("Very Fast"))
                RunPace(painter, 3, "Very Fast Cloud Animation");
        }

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Live Tuning", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Live physics values (spring stiffness, pressure, spin, noise, translation) take effect without resetting the simulation. Structural settings rebuild from step 1 when Auto Restart Required Changes is enabled. Structural settings are: population/size/fill/seed, joint count/radius, ring rest radius, and edge min/max length factors.",
            MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Apply Inspector Changes"))
                RunAction(painter, "Apply Cloud Inspector Changes", painter.ApplyInspectorChangesNow);

            if (GUILayout.Button("Restart From Step 1"))
                RunAction(painter, "Restart Cloud Simulation", painter.RestartSimulationFromStepOne);
        }

        if (painter.StructuralChangesPending)
        {
            EditorGUILayout.HelpBox(
                "Structural changes are pending. Enable Restart Simulation When Required or press Restart From Step 1 to fully apply them.",
                MessageType.Warning);
        }

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Debug Overlay", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Enable Draw Simulation Debug above. The overlay is a real rendered mesh and does NOT depend on Unity Gizmos. Bright pink is the field guide, cyan is the skeleton joints, teal is the capsule skin between them, green is joint velocity, and magenta is each cloud centroid/translation with a small orange spin arc.",
            MessageType.None);

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("Generated Clouds", painter.CloudCount.ToString());
        EditorGUILayout.LabelField("Visible Pixels", painter.CurrentPixelCount.ToString());
        EditorGUILayout.LabelField("Target Pixels", painter.TargetPixelCount.ToString());

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Ship Stir API", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Call StirAtWorldPosition(shipPosition, averageTravelDirection) while the ship overlaps cloud. The impulse is applied to skeleton joints directly, which then pull the rest of their ring along through the normal edge springs.",
            MessageType.Info);
    }

    private void RunAction(CloudFieldPainter painter, string undoName, Action action)
    {
        Undo.RecordObject(painter, undoName);
        action();
        EditorUtility.SetDirty(painter);
        serializedObject.Update();
        Repaint();
        SceneView.RepaintAll();
    }

    private void RunPace(CloudFieldPainter painter, int preset, string undoName)
    {
        Undo.RecordObject(painter, undoName);
        painter.SetAnimationPace(preset);
        EditorUtility.SetDirty(painter);
        serializedObject.Update();
        Repaint();
        SceneView.RepaintAll();
    }
}
#endif