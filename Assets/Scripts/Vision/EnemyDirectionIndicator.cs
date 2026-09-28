using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pixel-art "damage indicator" ring around the player showing where enemy
/// ships are. Attach to the player ship.
///
/// Every enemy gets a small pixel chevron on a fixed radius R, pointing along
///   theta = atan2(enemy - player)
/// with 1-3 short arcs behind it for distance (3 = close, 2 = medium, 1 = far).
/// New contacts pop in with an ease-out-back and throw a fading "spawn echo"
/// outwards; after that the indicator tracks the enemy smoothly.
///
/// All drawing is rasterised into a small point-filtered texture at the same
/// pixels-per-unit as the cloud field, and the texture is snapped to a pixel
/// grid aligned to the camera - so the motion is smooth (angles and radii are
/// continuous) but what you see is always whole, hard-edged pixels. Opacity is
/// stored on the rendered pixels themselves, so fading never removes parts of a shape.
/// </summary>
[DisallowMultipleComponent]
public sealed class EnemyDirectionIndicator : MonoBehaviour
{
    [Header("References (found automatically if empty)")]
    [Tooltip("What the ring is centred on. Assign the ship's SPRITE child if the player object's pivot isn't at the visual centre of the ship. Empty = this object.")]
    [SerializeField] private Transform centre;
    [SerializeField] private EnemyShipRegistry enemyRegistry;
    [SerializeField] private Camera targetCamera;

    [Header("Pixel Grid")]
    [Tooltip("World pixels per unit. Match the clouds / sprites (clouds use 8).")]
    [Min(1)][SerializeField] private int pixelsPerUnit = 8;
    [Tooltip("Sorting layer the indicator renders on.")]
    [SortingLayerPicker][SerializeField] private int sortingLayerId = 0;
    [SerializeField] private int sortingOrder = 50;
    [Tooltip("GameObject layer for the indicator view (camera culling masks etc.).")]
    [LayerPicker][SerializeField] private int renderLayer = 0;

    [Header("Geometry (world units - independent of Pixels Per Unit)")]
    [Tooltip("Radius R of the outer arc, measured from the centre. This alone sets both the distance from the ship and the curvature.")]
    [Min(0.05f)][SerializeField] private float indicatorRadius = 1.75f;
    [Tooltip("How much of the full circle each arc covers, in percent (25% = a 90 degree arc).")]
    [Range(1f, 100f)][SerializeField] private float arcPercent = 6f;
    [Tooltip("Arc line thickness in world units (never thinner than one pixel).")]
    [Min(0f)][SerializeField] private float arcThickness = 0.125f;
    [Tooltip("Distance between stacked distance arcs (each inner arc sits this much closer to the centre, same centre, so they stay concentric).")]
    [Min(0f)][SerializeField] private float arcSpacing = 0.25f;
    [Tooltip("Size of the chevron just outside the outer arc. 0 = no chevron.")]
    [Min(0f)][SerializeField] private float chevronSize = 0.3f;
    [Tooltip("Gap between the outer arc and the chevron.")]
    [Min(0f)][SerializeField] private float chevronGap = 0f;

    [Header("Distance Fill (world units)")]
    [Tooltip("The first arc starts growing when the enemy comes inside this distance (beyond it the first arc sits at its minimum).")]
    [SerializeField] private float farDistance = 9.0f;
    [Tooltip("First arc is full here; the second starts growing.")]
    [SerializeField] private float mediumDistance = 6.0f;
    [Tooltip("Second arc is full here; the third starts growing.")]
    [SerializeField] private float closeDistance = 3.5f;
    [Tooltip("Third arc is full here.")]
    [SerializeField] private float nearDistance = 1.5f;
    [Tooltip("Sweep of the first arc while the enemy is beyond Far Distance (fraction of a full arc), so every contact always shows something.")]
    [Range(0f, 1f)][SerializeField] private float minimumFirstArc = 0.3f;
    [Tooltip("Enemies further than this get no indicator (0 = always show).")]
    [Min(0f)][SerializeField] private float maxDistance = 0f;
    [Tooltip("Hide an enemy's indicator while that enemy is on screen.")]
    [SerializeField] private bool hideWhenOnScreen = false;

    [Header("Animation")]
    [Min(0.01f)][SerializeField] private float popInSeconds = 0.35f;
    [Min(0.01f)][SerializeField] private float popOutSeconds = 0.2f;
    [Tooltip("How quickly the indicator follows the enemy's bearing (higher = snappier).")]
    [Min(0.1f)][SerializeField] private float trackingSharpness = 12f;
    [Tooltip("How quickly the arcs catch up with the enemy's distance (higher = snappier).")]
    [Min(0.1f)][SerializeField] private float fillSharpness = 8f;
    [SerializeField] private bool spawnEcho = true;
    [Min(0.05f)][SerializeField] private float echoSeconds = 0.6f;
    [Tooltip("How far past R the echo travels, in world units.")]
    [Min(0f)][SerializeField] private float echoTravel = 0.9f;

    [Header("Opacity")]
    [Tooltip("Overall opacity multiplier for the persistent enemy arrows and distance arcs.")]
    [Range(0f, 1f)][SerializeField] private float arrowOpacity = 1f;
    [Tooltip("Overall opacity multiplier for enemy spawn echoes and external direction pulses.")]
    [Range(0f, 1f)][SerializeField] private float pulseOpacity = 1f;
    [Tooltip("Persistent arrow / arc opacity at or beyond Far Distance, before Arrow Opacity is applied.")]
    [Range(0f, 1f)][SerializeField] private float farOpacity = 0.35f;
    [Tooltip("Persistent arrow / arc opacity at or inside Near Distance, before Arrow Opacity is applied.")]
    [Range(0f, 1f)][SerializeField] private float nearOpacity = 0.9f;
    [Tooltip("Shape of the far-to-near opacity ramp. X: 0 = Far Distance, 1 = Near Distance. Y: blend from Far Opacity to Near Opacity.")]
    [SerializeField] private AnimationCurve distanceOpacityCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
    [Tooltip("How a newly spawned enemy settles from full Arrow Opacity to its distance-based opacity. X: 0 = just spawned, 1 = spawn animation complete. Y: 0 = spawn opacity, 1 = distance opacity.")]
    [SerializeField] private AnimationCurve spawnOpacityCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Colour")]
    [SerializeField] private Color colour = new Color(1f, 0.12f, 0.1f, 1f);
    [Tooltip("Colour for far contacts (blended by band). Leave equal to Colour for one colour.")]
    [SerializeField] private Color farColour = new Color(1f, 0.62f, 0.1f, 1f);
    [Tooltip("How strongly close contacts influence colour when indicators merge. 1 = equal weighting; higher values let closer contacts contribute more strongly.")]
    [Range(1f, 4f)][SerializeField] private float closeMergeWeight = 1.5f;

    // ---------------------------------------------------------------- state
    private sealed class Contact
    {
        public EnemyShip enemy;
        public float angle;           // smoothed bearing, degrees
        public float appear;          // 0..1 pop-in progress
        public float disappear;       // 0..1 pop-out progress (0 = alive)
        public float bands;           // smoothed fill 0..3: arc1 grows 0->1, then arc2 1->2, then arc3 2->3
        public float echo = -1f;      // 0..1 spawn echo progress, <0 = none
        public bool leaving;
    }

    private sealed class PulseEcho
    {
        public Vector2 worldPosition;
        public Color colour;
        public float strength;
        public float progress;
    }

    private struct ArcStroke
    {
        public float angle;
        public float halfAngle;
        public float alpha;
    }

    private struct ChevronStroke
    {
        public Vector2 tip;
        public Vector2 left;
        public Vector2 right;
        public float alpha;
    }

    private struct ColourSource
    {
        public float angle;
        public Color colour;
        public float alpha;
        public float mergeWeight;
    }

    private readonly List<int> touched = new List<int>();
    private readonly List<Contact> contacts = new List<Contact>();
    private readonly List<PulseEcho> pulses = new List<PulseEcho>();
    private readonly Dictionary<EnemyShip, Contact> byEnemy = new Dictionary<EnemyShip, Contact>();
    private readonly List<ArcStroke>[] arcBands =
    {
        new List<ArcStroke>(),
        new List<ArcStroke>(),
        new List<ArcStroke>()
    };
    private readonly List<ChevronStroke> chevrons = new List<ChevronStroke>();
    private readonly List<ColourSource> colourSources = new List<ColourSource>();

    private Texture2D texture;
    private Color32[] pixels;
    private float[] mergeAlpha;
    private int size;
    private SpriteRenderer view;
    private Transform viewTransform;


    // ------------------------------------------------------------ lifecycle
    private Transform Centre => centre != null ? centre : transform;


    private void Awake()
    {
        if (enemyRegistry == null) enemyRegistry = FindFirstObjectByType<EnemyShipRegistry>();
        if (targetCamera == null) targetCamera = Camera.main;
        BuildView();
    }


    private void OnEnable()
    {
        if (enemyRegistry == null) return;
        enemyRegistry.EnemyRegistered += HandleRegistered;
        enemyRegistry.EnemyUnregistered += HandleUnregistered;
        foreach (EnemyShip e in enemyRegistry.Enemies) AddContact(e, popIn: false);
    }


    private void OnDisable()
    {
        if (enemyRegistry != null)
        {
            enemyRegistry.EnemyRegistered -= HandleRegistered;
            enemyRegistry.EnemyUnregistered -= HandleUnregistered;
        }
        contacts.Clear();
        pulses.Clear();
        byEnemy.Clear();
        if (view != null) view.enabled = false;
    }


    private void OnDestroy()
    {
        if (viewTransform != null) Destroy(viewTransform.gameObject);
        if (texture != null) Destroy(texture);
    }


    private void HandleRegistered(EnemyShip enemy) => AddContact(enemy, popIn: true);


    private void HandleUnregistered(EnemyShip enemy)
    {
        if (enemy != null && byEnemy.TryGetValue(enemy, out Contact c)) c.leaving = true;
    }


    private void AddContact(EnemyShip enemy, bool popIn)
    {
        if (enemy == null || byEnemy.ContainsKey(enemy)) return;

        Vector2 v = enemy.transform.position - Centre.position;
        var c = new Contact
        {
            enemy = enemy,
            angle = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg,
            appear = popIn ? 0f : 1f,
            bands = BandFor(v.magnitude),
            echo = popIn && spawnEcho ? 0f : -1f
        };
        contacts.Add(c);
        byEnemy.Add(enemy, c);
    }

    /// <summary>
    /// Plays an echo-only pulse toward a world position. The pulse shares this
    /// indicator's texture, pixel grid, arc geometry, true-alpha fading and echo timing,
    /// but does not create a persistent arc or chevron.
    /// </summary>
    public void Pulse(Vector2 worldPosition, Color pulseColour, float strength = 1f)
    {
        float clampedStrength = Mathf.Max(0f, strength);
        if (clampedStrength <= 0f) return;

        pulses.Add(new PulseEcho
        {
            worldPosition = worldPosition,
            colour = pulseColour,
            strength = clampedStrength,
            progress = 0f
        });
    }


    // --------------------------------------------------------------- update
    private void LateUpdate()
    {
        if (view == null) return;

        float dt = Time.deltaTime;
        Vector2 player = Centre.position;

        for (int i = contacts.Count - 1; i >= 0; i--)
        {
            Contact c = contacts[i];

            if (c.enemy == null) c.leaving = true;

            if (c.leaving)
            {
                c.disappear += dt / popOutSeconds;
                if (c.disappear >= 1f)
                {
                    if (c.enemy != null) byEnemy.Remove(c.enemy);
                    else RemoveDeadKeys();
                    contacts.RemoveAt(i);
                }
                continue;
            }

            Vector2 v = (Vector2)c.enemy.transform.position - player;
            float target = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
            // frame-rate independent smoothing toward the live bearing
            c.angle = Mathf.LerpAngle(c.angle, target, 1f - Mathf.Exp(-trackingSharpness * dt));
            c.bands = Mathf.Lerp(c.bands, BandFor(v.magnitude), 1f - Mathf.Exp(-fillSharpness * dt));
            c.appear = Mathf.Min(1f, c.appear + dt / popInSeconds);
            if (c.echo >= 0f)
            {
                c.echo += dt / echoSeconds;
                if (c.echo >= 1f) c.echo = -1f;
            }
        }

        for (int i = pulses.Count - 1; i >= 0; i--)
        {
            PulseEcho pulse = pulses[i];
            pulse.progress += dt / echoSeconds;
            if (pulse.progress >= 1f) pulses.RemoveAt(i);
        }

        Redraw();
    }


    /// <summary>
    /// Continuous fill 0..3 from distance: far -> medium grows arc 1,
    /// medium -> close grows arc 2, close -> near grows arc 3.
    /// </summary>
    private float BandFor(float distance)
    {
        return Mathf.InverseLerp(farDistance, mediumDistance, distance)
             + Mathf.InverseLerp(mediumDistance, closeDistance, distance)
             + Mathf.InverseLerp(closeDistance, nearDistance, distance);
    }


    /// <summary>
    /// Distance-based opacity for the persistent arrow / arc. Band 0 is far and
    /// band 3 is near. The AnimationCurve is visible in the Inspector and shapes
    /// how the value travels between the two configured opacity endpoints.
    /// </summary>
    private float DistanceOpacityFor(float bands)
    {
        float distanceT = Mathf.Clamp01(bands / 3f);
        float curveT = distanceOpacityCurve != null
            ? Mathf.Clamp01(distanceOpacityCurve.Evaluate(distanceT))
            : distanceT;
        return Mathf.Lerp(farOpacity, nearOpacity, curveT);
    }


    /// <summary>
    /// New contacts begin at the full Arrow Opacity and settle toward their
    /// distance-based opacity while the spawn animation completes. This keeps a
    /// new contact noticeable even when it first appears at long range.
    /// </summary>
    private float StableOpacityFor(Contact c)
    {
        float distanceOpacity = DistanceOpacityFor(c.bands) * arrowOpacity;

        if (c.leaving)
            return distanceOpacity * (1f - Mathf.Clamp01(c.disappear));

        float settle = spawnOpacityCurve != null
            ? Mathf.Clamp01(spawnOpacityCurve.Evaluate(Mathf.Clamp01(c.appear)))
            : Mathf.Clamp01(c.appear);

        return Mathf.Lerp(arrowOpacity, distanceOpacity, settle);
    }


    private bool IsHidden(Contact c)
    {
        if (c.enemy == null) return false;
        Vector2 v = c.enemy.transform.position - Centre.position;
        if (maxDistance > 0f && v.magnitude > maxDistance) return true;
        if (!hideWhenOnScreen || targetCamera == null) return false;
        Vector3 vp = targetCamera.WorldToViewportPoint(c.enemy.transform.position);
        return vp.x > 0f && vp.x < 1f && vp.y > 0f && vp.y < 1f;
    }


    // -------------------------------------------------------------- drawing
    private void BuildView()
    {
        float reach = indicatorRadius + echoTravel + chevronGap + chevronSize * 1.3f + arcThickness + 4f / pixelsPerUnit;
        size = Mathf.CeilToInt(reach * pixelsPerUnit) * 2 + 4;

        texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "EnemyIndicator"
        };
        pixels = new Color32[size * size];
        mergeAlpha = new float[size * size];

        var go = new GameObject("EnemyIndicatorView");
        viewTransform = go.transform;          // not parented: the player's rotation must not spin it
        view = go.AddComponent<SpriteRenderer>();
        view.sprite = Sprite.Create(texture, new Rect(0, 0, size, size), Vector2.zero, pixelsPerUnit);
        ApplyLayers();
    }


    private void ApplyLayers()
    {
        if (view == null) return;
        view.sortingLayerID = sortingLayerId;
        view.sortingOrder = sortingOrder;
        view.gameObject.layer = Mathf.Clamp(renderLayer, 0, 31);
    }


    private void OnValidate()
    {
        ApplyLayers();   // lets you change layers live in play mode
    }


    private void Redraw()
    {
        // clear only what was drawn last frame (the texture can be big at high PPU)
        foreach (int idx in touched) pixels[idx] = default;
        touched.Clear();
        System.Array.Clear(mergeAlpha, 0, mergeAlpha.Length);

        // Snap the texture's corner to a pixel grid aligned to the camera, then
        // draw everything around the centre's exact (sub-pixel) position inside it.
        Vector2 player = Centre.position;
        Vector2 camPos = targetCamera != null ? (Vector2)targetCamera.transform.position : Vector2.zero;
        float unit = 1f / pixelsPerUnit;
        Vector2 corner = player - Vector2.one * (size * 0.5f * unit);
        corner = camPos + new Vector2(
            Mathf.Round((corner.x - camPos.x) * pixelsPerUnit) * unit,
            Mathf.Round((corner.y - camPos.y) * pixelsPerUnit) * unit);
        viewTransform.position = new Vector3(corner.x, corner.y, Centre.position.z);
        Vector2 c0 = (player - corner) * pixelsPerUnit;   // centre, in texture pixels

        // everything below is world units * ppu, so PPU only changes resolution
        float ppu = pixelsPerUnit;
        float radiusPx = indicatorRadius * ppu;
        float thickPx = Mathf.Max(1f, arcThickness * ppu);
        float spacingPx = arcSpacing * ppu;
        float fullHalf = arcPercent * 3.6f * 0.5f;          // percent of 360, halved

        bool any = false;
        for (int i = 0; i < arcBands.Length; i++) arcBands[i].Clear();
        chevrons.Clear();
        colourSources.Clear();

        foreach (Contact c in contacts)
        {
            if (IsHidden(c)) continue;
            any = true;

            // The radius never animates - the ring stays exactly R from the centre.
            // Pop in = arcs sweep open at high opacity then settle toward the distance ramp;
            // pop out = sweep closed + true pixel alpha fade.
            float open = c.leaving ? 1f - EaseInQuad(c.disappear) : EaseOutBack(c.appear);
            float alpha = StableOpacityFor(c);
            if (open <= 0.01f || alpha <= 0.01f) continue;

            float bandT = Mathf.Clamp01(c.bands / 3f);
            Color col = Color.Lerp(farColour, colour, bandT);
            float half = fullHalf * open;
            float mergeWeight = Mathf.Lerp(1f, closeMergeWeight, bandT);

            // Colour is collected once per contact, independently from the shapes it
            // contributes. The final colour pass can therefore form one continuous
            // gradient across nearby arcs and chevrons without changing their geometry.
            colourSources.Add(new ColourSource
            {
                angle = c.angle,
                colour = col,
                alpha = alpha,
                mergeWeight = mergeWeight
            });

            if (chevronSize > 0f)
            {
                float chevronRadius = radiusPx + thickPx * 0.5f + chevronGap * ppu;
                float chevronPixels = chevronSize * ppu * Mathf.Clamp(open, 0f, 1.2f);
                AddChevronStroke(c0, c.angle, chevronRadius, chevronPixels, alpha);
            }

            // Collect the normal distance arcs as geometry only. Arcs on the same
            // concentric band can share a silhouette, but their colour is applied later.
            for (int i = 0; i < 3; i++)
            {
                float amount = Mathf.Clamp01(c.bands - i);
                if (i == 0) amount = Mathf.Max(amount, minimumFirstArc);
                if (amount <= 0.01f) break;

                arcBands[i].Add(new ArcStroke
                {
                    angle = c.angle,
                    halfAngle = half * amount,
                    alpha = alpha
                });
            }

            // Spawn echoes keep their independent radii. They are temporary and
            // deliberately remain separate from the stable indicator merge. Their
            // fade is true texture alpha, not dithered pixel coverage.
            if (c.echo >= 0f)
            {
                float e = EaseOutCubic(c.echo);
                for (int k = 0; k < 3; k++)
                {
                    float er = radiusPx + (echoTravel * ppu) * e + (k + 1) * spacingPx;
                    float ea = (1f - c.echo) * (0.8f - k * 0.25f) * pulseOpacity;
                    DrawArc(c0, c.angle, er, thickPx, fullHalf * (1f + 0.3f * e), col, ea);
                }
            }
        }

        // External pulses are echo-only contacts. They reuse the same ring geometry
        // and true pixel alpha, but never add stable arcs, chevrons or colour sources.
        for (int i = 0; i < pulses.Count; i++)
        {
            PulseEcho pulse = pulses[i];
            Vector2 toPulse = pulse.worldPosition - player;
            if (toPulse.sqrMagnitude <= 0.000001f) continue;

            any = true;
            float pulseAngle = Mathf.Atan2(toPulse.y, toPulse.x) * Mathf.Rad2Deg;
            float t = Mathf.Clamp01(pulse.progress);
            float e = EaseOutCubic(t);
            float colourAlpha = Mathf.Clamp01(pulse.colour.a);

            for (int k = 0; k < 3; k++)
            {
                float er = radiusPx + (echoTravel * ppu) * e + (k + 1) * spacingPx;
                float ea = (1f - t) * (0.8f - k * 0.25f) * pulse.strength * pulseOpacity * colourAlpha;
                DrawArc(c0, pulseAngle, er, thickPx, fullHalf * (1f + 0.3f * e), pulse.colour, ea);
            }
        }

        // First finish the stable geometry. Arc gaps are bridged band-by-band and
        // chevrons are rasterised at their exact shape. No colour decisions happen here.
        for (int i = 0; i < arcBands.Length; i++)
        {
            if (arcBands[i].Count == 0) continue;
            DrawMergedArcBandCoverage(c0, radiusPx - i * spacingPx, thickPx, arcBands[i]);
        }
        AddChevronCoverage();

        // Only after the complete shape exists do we shade it. Every covered pixel
        // samples the same angular colour field, so nearby contacts create a gradient
        // across whole arcs and chevrons instead of blending only where shapes overlap.
        float chevronPixelsForBlend = chevronSize * ppu;
        ShadeMergedIndicator(c0, radiusPx, fullHalf, chevronPixelsForBlend, thickPx);

        view.enabled = any;
        if (!any) return;

        texture.SetPixels32(pixels);
        texture.Apply(false);
    }


    private void AddChevronStroke(Vector2 centre, float angleDeg, float r, float s, float alpha)
    {
        if (s < 0.5f) return;

        float rad = angleDeg * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        Vector2 side = new Vector2(-dir.y, dir.x);

        chevrons.Add(new ChevronStroke
        {
            tip = centre + dir * (r + s),
            left = centre + dir * r + side * s,
            right = centre + dir * r - side * s,
            alpha = alpha
        });
    }


    /// <summary>
    /// Builds one concentric distance band's shared silhouette. Tiny angular gaps
    /// between neighbouring arcs are bridged, but isolated ends keep their length.
    /// </summary>
    private void DrawMergedArcBandCoverage(Vector2 centre, float radius, float thickness, List<ArcStroke> strokes)
    {
        if (radius < 1f || strokes.Count == 0) return;

        float halfThick = thickness * 0.5f;
        int r0 = Mathf.FloorToInt(radius - halfThick - 1f), r1 = Mathf.CeilToInt(radius + halfThick + 1f);
        int xMin = Mathf.Max(0, Mathf.FloorToInt(centre.x - r1)), xMax = Mathf.Min(size - 1, Mathf.CeilToInt(centre.x + r1));
        int yMin = Mathf.Max(0, Mathf.FloorToInt(centre.y - r1)), yMax = Mathf.Min(size - 1, Mathf.CeilToInt(centre.y + r1));

        float mergePixels = MergeDistancePixels(thickness);
        float mergeAngle = Mathf.Atan2(mergePixels, Mathf.Max(radius, 1f)) * Mathf.Rad2Deg;

        for (int y = yMin; y <= yMax; y++)
            for (int x = xMin; x <= xMax; x++)
            {
                float dx = x + 0.5f - centre.x, dy = y + 0.5f - centre.y;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d < r0 || d > r1 || Mathf.Abs(d - radius) > halfThick) continue;

                float pixelAngle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                bool covered = false;
                float coverageAlpha = 0f;
                float positiveGap = float.PositiveInfinity;
                float negativeGap = float.PositiveInfinity;
                ArcStroke positiveStroke = default;
                ArcStroke negativeStroke = default;

                for (int i = 0; i < strokes.Count; i++)
                {
                    ArcStroke stroke = strokes[i];
                    float halfAngle = Mathf.Min(180f, Mathf.Max(0f, stroke.halfAngle));
                    float delta = Mathf.DeltaAngle(stroke.angle, pixelAngle);
                    float edgeDistance = Mathf.Abs(delta) - halfAngle;

                    if (edgeDistance <= 0f)
                    {
                        covered = true;
                        coverageAlpha = Mathf.Max(coverageAlpha, stroke.alpha);
                    }
                    else if (delta > 0f && edgeDistance < positiveGap)
                    {
                        positiveGap = edgeDistance;
                        positiveStroke = stroke;
                    }
                    else if (delta < 0f && edgeDistance < negativeGap)
                    {
                        negativeGap = edgeDistance;
                        negativeStroke = stroke;
                    }
                }

                if (!covered && positiveGap < float.PositiveInfinity && negativeGap < float.PositiveInfinity
                    && positiveGap + negativeGap <= mergeAngle)
                {
                    covered = true;
                    float totalGap = positiveGap + negativeGap;
                    float t = totalGap > 0.0001f ? positiveGap / totalGap : 0.5f;
                    coverageAlpha = Mathf.Lerp(positiveStroke.alpha, negativeStroke.alpha, t);
                }

                if (!covered) continue;
                int idx = y * size + x;
                mergeAlpha[idx] = Mathf.Max(mergeAlpha[idx], coverageAlpha);
            }
    }


    private void AddChevronCoverage()
    {
        for (int i = 0; i < chevrons.Count; i++)
        {
            ChevronStroke stroke = chevrons[i];
            int xMin = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(stroke.tip.x, Mathf.Min(stroke.left.x, stroke.right.x))));
            int xMax = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(stroke.tip.x, Mathf.Max(stroke.left.x, stroke.right.x))));
            int yMin = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(stroke.tip.y, Mathf.Min(stroke.left.y, stroke.right.y))));
            int yMax = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(stroke.tip.y, Mathf.Max(stroke.left.y, stroke.right.y))));

            for (int y = yMin; y <= yMax; y++)
                for (int x = xMin; x <= xMax; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    if (!InTriangle(p, stroke.tip, stroke.left, stroke.right)) continue;
                    int idx = y * size + x;
                    mergeAlpha[idx] = Mathf.Max(mergeAlpha[idx], stroke.alpha);
                }
        }
    }


    private void ShadeMergedIndicator(Vector2 centre, float outerRadius, float fullHalfAngle, float chevronPixels, float thickness)
    {
        if (colourSources.Count == 0) return;

        // The colour field reaches beyond the physical overlap distance on purpose.
        // Its scale comes from the current geometry, so the Inspector stays compact:
        // wider arcs and larger chevrons naturally produce a broader transition.
        float arcHalfLength = outerRadius * fullHalfAngle * Mathf.Deg2Rad;
        float blendScale = Mathf.Max(
            MergeDistancePixels(thickness) * 2.5f,
            Mathf.Max(arcHalfLength, chevronPixels * 1.25f));
        blendScale = Mathf.Max(1f, blendScale);
        float blendReach = blendScale * 3f;

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int idx = y * size + x;
                float alpha = mergeAlpha[idx];
                if (alpha <= 0f) continue;

                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float pixelAngle = Mathf.Atan2(p.y - centre.y, p.x - centre.x) * Mathf.Rad2Deg;
                Color colourSum = default;
                float colourWeight = 0f;

                for (int i = 0; i < colourSources.Count; i++)
                {
                    ColourSource source = colourSources[i];
                    float angleDelta = Mathf.Abs(Mathf.DeltaAngle(pixelAngle, source.angle));
                    float distance = angleDelta * Mathf.Deg2Rad * Mathf.Max(outerRadius, 1f);
                    if (distance > blendReach) continue;

                    // A soft inverse-square field gives each contact influence beyond its
                    // own pixels. The smooth cutoff prevents distant contacts elsewhere on
                    // the ring from tinting an otherwise unrelated indicator cluster.
                    float scaled = distance / blendScale;
                    float influence = 1f / (1f + scaled * scaled);
                    float cutoffT = Mathf.Clamp01(distance / blendReach);
                    float cutoff = 1f - cutoffT * cutoffT * (3f - 2f * cutoffT);
                    float weight = source.alpha * source.mergeWeight * influence * cutoff;

                    colourSum += source.colour * weight;
                    colourWeight += weight;
                }

                if (colourWeight <= 0.0001f) continue;
                Color mergedColour = colourSum / colourWeight;
                mergedColour.a = 1f;
                Plot(x, y, mergedColour, alpha);
            }
    }


    private static float MergeDistancePixels(float thickness)
        => Mathf.Clamp(thickness * 0.75f + 0.75f, 1f, 2.5f);


    /// <summary>An arc of the given pixel thickness centred on the ring centre, rasterised by testing every pixel in its bounding ring.</summary>
    private void DrawArc(Vector2 centre, float angleDeg, float radius, float thickness, float halfAngleDeg, Color col, float alpha)
    {
        if (radius < 1f || alpha <= 0f) return;

        float halfThick = thickness * 0.5f;
        int r0 = Mathf.FloorToInt(radius - halfThick - 1f), r1 = Mathf.CeilToInt(radius + halfThick + 1f);
        int xMin = Mathf.Max(0, Mathf.FloorToInt(centre.x - r1)), xMax = Mathf.Min(size - 1, Mathf.CeilToInt(centre.x + r1));
        int yMin = Mathf.Max(0, Mathf.FloorToInt(centre.y - r1)), yMax = Mathf.Min(size - 1, Mathf.CeilToInt(centre.y + r1));

        for (int y = yMin; y <= yMax; y++)
            for (int x = xMin; x <= xMax; x++)
            {
                float dx = x + 0.5f - centre.x, dy = y + 0.5f - centre.y;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                if (d < r0 || d > r1 || Mathf.Abs(d - radius) > halfThick) continue;

                float a = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                if (Mathf.Abs(Mathf.DeltaAngle(a, angleDeg)) > halfAngleDeg) continue;

                Plot(x, y, col, alpha);
            }
    }


    private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
        bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(neg && pos);
    }


    private static float Cross(Vector2 p, Vector2 a, Vector2 b)
        => (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);


    /// <summary>
    /// Writes a complete hard-edged pixel with real alpha. Opacity changes the
    /// transparency of the rendered pixel itself; it never changes shape coverage.
    /// Multiple strokes use ordinary source-over alpha compositing.
    /// </summary>
    private void Plot(int x, int y, Color col, float alpha)
    {
        float srcA = Mathf.Clamp01(alpha);
        if (srcA <= 0f) return;

        int idx = y * size + x;
        Color32 dst32 = pixels[idx];
        if (dst32.a == 0) touched.Add(idx);

        float dstA = dst32.a / 255f;
        if (dstA <= 0f)
        {
            pixels[idx] = (Color32)new Color(col.r, col.g, col.b, srcA);
            return;
        }

        float outA = srcA + dstA * (1f - srcA);
        float dstScale = dstA * (1f - srcA);
        float invOutA = outA > 0.000001f ? 1f / outA : 0f;

        float dstR = dst32.r / 255f;
        float dstG = dst32.g / 255f;
        float dstB = dst32.b / 255f;

        Color result = new Color(
            (col.r * srcA + dstR * dstScale) * invOutA,
            (col.g * srcA + dstG * dstScale) * invOutA,
            (col.b * srcA + dstB * dstScale) * invOutA,
            outA);

        pixels[idx] = (Color32)result;
    }


    private void RemoveDeadKeys()
    {
        var dead = new List<EnemyShip>();
        foreach (EnemyShip e in byEnemy.Keys) if (e == null) dead.Add(e);
        foreach (EnemyShip e in dead) byEnemy.Remove(e);
    }


    // -------------------------------------------------------------- easings
    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        t = Mathf.Clamp01(t) - 1f;
        return 1f + c3 * t * t * t + c1 * t * t;
    }

    private static float EaseOutCubic(float t)
    {
        t = 1f - Mathf.Clamp01(t);
        return 1f - t * t * t;
    }

    private static float EaseInQuad(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t;
    }
}


/// <summary>Shows an int field as a sorting-layer dropdown (stores the layer's unique id).</summary>
public sealed class SortingLayerPickerAttribute : PropertyAttribute { }

/// <summary>Shows an int field as a GameObject layer dropdown.</summary>
public sealed class LayerPickerAttribute : PropertyAttribute { }