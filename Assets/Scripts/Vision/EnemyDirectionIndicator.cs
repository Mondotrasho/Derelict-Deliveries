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
/// continuous) but what you see is always whole, hard-edged pixels. Fades use
/// an ordered (Bayer) dither instead of alpha so they stay pixel-y too.
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

    [Header("Colour")]
    [SerializeField] private Color colour = new Color(1f, 0.12f, 0.1f, 1f);
    [Tooltip("Colour for far contacts (blended by band). Leave equal to Colour for one colour.")]
    [SerializeField] private Color farColour = new Color(1f, 0.62f, 0.1f, 1f);

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

    private struct ArcStroke
    {
        public float angle;
        public float halfAngle;
        public Color colour;
        public float alpha;
    }

    private readonly List<int> touched = new List<int>();
    private readonly List<Contact> contacts = new List<Contact>();
    private readonly Dictionary<EnemyShip, Contact> byEnemy = new Dictionary<EnemyShip, Contact>();
    private readonly List<ArcStroke>[] arcBands =
    {
        new List<ArcStroke>(),
        new List<ArcStroke>(),
        new List<ArcStroke>()
    };

    private Texture2D texture;
    private Color32[] pixels;
    private int size;
    private SpriteRenderer view;
    private Transform viewTransform;

    private static readonly float[] Bayer4 =
    {
         0f / 16f,  8f / 16f,  2f / 16f, 10f / 16f,
        12f / 16f,  4f / 16f, 14f / 16f,  6f / 16f,
         3f / 16f, 11f / 16f,  1f / 16f,  9f / 16f,
        15f / 16f,  7f / 16f, 13f / 16f,  5f / 16f
    };


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

        foreach (Contact c in contacts)
        {
            if (IsHidden(c)) continue;
            any = true;

            // The radius never animates - the ring stays exactly R from the centre.
            // Pop in = arcs sweep open with ease-out-back; pop out = sweep closed + dither.
            float open = c.leaving ? 1f - EaseInQuad(c.disappear) : EaseOutBack(c.appear);
            float alpha = c.leaving ? 1f - c.disappear : Mathf.Clamp01(c.appear * 2f);
            if (open <= 0.01f || alpha <= 0.01f) continue;

            float bandT = Mathf.Clamp01(c.bands / 3f);
            Color col = Color.Lerp(farColour, colour, bandT);
            float half = fullHalf * open;

            if (chevronSize > 0f)
            {
                DrawChevron(c0, c.angle, radiusPx + thickPx * 0.5f + chevronGap * ppu,
                            chevronSize * ppu * Mathf.Clamp(open, 0f, 1.2f), col, alpha);
            }

            // Collect the normal distance arcs first. Arcs on the same concentric
            // band are rasterised together below so nearby contacts can become one
            // continuous shape instead of drawing over each other in contact order.
            for (int i = 0; i < 3; i++)
            {
                float amount = Mathf.Clamp01(c.bands - i);
                if (i == 0) amount = Mathf.Max(amount, minimumFirstArc);
                if (amount <= 0.01f) break;

                arcBands[i].Add(new ArcStroke
                {
                    angle = c.angle,
                    halfAngle = half * amount,
                    colour = col,
                    alpha = alpha
                });
            }

            // Spawn echoes keep their independent radii. They are temporary and
            // deliberately remain separate from the stable distance-band merge.
            if (c.echo >= 0f)
            {
                float e = EaseOutCubic(c.echo);
                for (int k = 0; k < 3; k++)
                {
                    float er = radiusPx + (echoTravel * ppu) * e + (k + 1) * spacingPx;
                    float ea = (1f - c.echo) * (0.8f - k * 0.25f);
                    DrawArc(c0, c.angle, er, thickPx, fullHalf * (1f + 0.3f * e), col, ea);
                }
            }
        }

        for (int i = 0; i < arcBands.Length; i++)
        {
            if (arcBands[i].Count == 0) continue;
            DrawMergedArcBand(c0, radiusPx - i * spacingPx, thickPx, arcBands[i]);
        }

        view.enabled = any;
        if (!any) return;

        texture.SetPixels32(pixels);
        texture.Apply(false);
    }


    /// <summary>
    /// Rasterises one concentric distance band as a shared shape. Overlapping arcs
    /// blend their colours, and tiny angular gaps between neighbouring arcs are
    /// bridged so close contacts flow into one continuous pixel-art segment.
    /// </summary>
    private void DrawMergedArcBand(Vector2 centre, float radius, float thickness, List<ArcStroke> strokes)
    {
        if (radius < 1f || strokes.Count == 0) return;

        float halfThick = thickness * 0.5f;
        int r0 = Mathf.FloorToInt(radius - halfThick - 1f), r1 = Mathf.CeilToInt(radius + halfThick + 1f);
        int xMin = Mathf.Max(0, Mathf.FloorToInt(centre.x - r1)), xMax = Mathf.Min(size - 1, Mathf.CeilToInt(centre.x + r1));
        int yMin = Mathf.Max(0, Mathf.FloorToInt(centre.y - r1)), yMax = Mathf.Min(size - 1, Mathf.CeilToInt(centre.y + r1));

        // Merge by roughly 1-2 screen pixels. This is derived from the existing
        // geometry rather than exposed as another setting, so the Inspector stays
        // unchanged while the behaviour scales naturally with radius/thickness.
        float mergePixels = Mathf.Clamp(thickness * 0.75f + 0.75f, 1f, 2.5f);
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
                Color colourSum = default;
                float colourWeight = 0f;

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

                    // Let nearby arcs influence the colour slightly before their
                    // silhouettes actually overlap. That removes the hard colour
                    // seam as two contacts move together, without softening pixels.
                    if (edgeDistance <= mergeAngle)
                    {
                        float edgeInfluence = edgeDistance <= 0f
                            ? 1f
                            : 1f - Mathf.Clamp01(edgeDistance / mergeAngle);
                        float centreInfluence = 1f - Mathf.Clamp01(
                            Mathf.Abs(delta) / Mathf.Max(halfAngle + mergeAngle, 0.001f));
                        float weight = stroke.alpha * edgeInfluence * (0.35f + 0.65f * centreInfluence);

                        colourSum += stroke.colour * weight;
                        colourWeight += weight;
                    }
                }

                // A small gap is filled only when there is an arc on both sides of
                // it. Isolated arc ends therefore keep their original length.
                if (!covered && positiveGap < float.PositiveInfinity && negativeGap < float.PositiveInfinity
                    && positiveGap + negativeGap <= mergeAngle)
                {
                    covered = true;
                    float totalGap = positiveGap + negativeGap;
                    float t = totalGap > 0.0001f ? positiveGap / totalGap : 0.5f;
                    coverageAlpha = Mathf.Lerp(positiveStroke.alpha, negativeStroke.alpha, t);

                    if (colourWeight <= 0.0001f)
                    {
                        colourSum = Color.Lerp(positiveStroke.colour, negativeStroke.colour, t);
                        colourWeight = 1f;
                    }
                }

                if (!covered) continue;

                Color mergedColour = colourWeight > 0.0001f
                    ? colourSum / colourWeight
                    : Color.white;
                mergedColour.a = 1f;
                Plot(x, y, mergedColour, coverageAlpha);
            }
    }


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


    /// <summary>Small filled pixel chevron (triangle) pointing outwards at radius r.</summary>
    private void DrawChevron(Vector2 centre, float angleDeg, float r, float s, Color col, float alpha)
    {
        if (s < 0.5f) return;
        float rad = angleDeg * Mathf.Deg2Rad;
        Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
        Vector2 side = new Vector2(-dir.y, dir.x);

        Vector2 tip = centre + dir * (r + s);
        Vector2 left = centre + dir * r + side * s;
        Vector2 right = centre + dir * r - side * s;

        int xMin = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(tip.x, Mathf.Min(left.x, right.x))));
        int xMax = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(tip.x, Mathf.Max(left.x, right.x))));
        int yMin = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(tip.y, Mathf.Min(left.y, right.y))));
        int yMax = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(tip.y, Mathf.Max(left.y, right.y))));

        for (int y = yMin; y <= yMax; y++)
            for (int x = xMin; x <= xMax; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                if (InTriangle(p, tip, left, right)) Plot(x, y, col, alpha);
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


    /// <summary>Hard pixel, faded with an ordered dither so there's no soft alpha.</summary>
    private void Plot(int x, int y, Color col, float alpha)
    {
        if (alpha < 1f && alpha <= Bayer4[(y & 3) * 4 + (x & 3)]) return;
        int idx = y * size + x;
        if (pixels[idx].a == 0) touched.Add(idx);
        pixels[idx] = (Color32)new Color(col.r, col.g, col.b, 1f);
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