using UnityEngine;

/// <summary>
/// Look/layout settings for a HoverCallout. Field names and defaults match
/// PlanetLabelManager's "Callout Layout" group so the two can be set identically.
/// </summary>
[System.Serializable]
public sealed class HoverCalloutStyle
{
    [Tooltip("World distance from the target to the label's connection corner. The quadrant is chosen automatically.")]
    public Vector2 labelDiagonalOffset = new Vector2(1.15f, 0.75f);

    [Tooltip("Minimum gap between the label and the camera edge.")]
    public float screenPaddingPixels = 8f;

    [Tooltip("Pixel density of the generated reticle, connector and snapping grid.")]
    public int calloutPixelsPerUnit = 8;

    public int connectorThicknessPixels = 1;

    [Tooltip("Connector order. Reticle draws at +1, text at +2.")]
    public int calloutSortingOrder = 100;

    public Color connectorColor = Color.white;
    public Color reticleColor = Color.white;
    public float reticleScale = 1f;
}


/// <summary>
/// One hover callout: an 8x8 pixel reticle on the target tile, a one-pixel
/// connector line, and a PlanetLabel whose facing corner is locked to a fixed
/// anchor diagonally away from the target.
///
/// This is the same layout PlanetLabelManager uses for planets, pulled into a
/// reusable plain class (PlanetLabelManager still has its own copy):
///   - the preferred diagonal points toward the larger amount of screen space;
///     all four diagonals are tried and the one with the least overflow wins;
///   - the text corner facing the target stays on the anchor, so a garbled
///     name turning into a longer real name grows away without moving the line;
///   - if the text still does not fit, the whole anchored callout shifts inward.
///
/// Create the parent under an object scaled like the map (planet callouts live
/// under VisualTilemaps, scaled 0.5) to get identical sizes.
/// </summary>
public sealed class HoverCallout
{
    private readonly HoverCalloutStyle style;
    private readonly PlanetLabel label;
    private readonly Renderer labelRenderer;
    private readonly SpriteRenderer reticle;
    private readonly SpriteRenderer connector;

    private static Sprite reticleSprite;
    private static int reticleSpritePpu;
    private static Sprite connectorSprite;

    public PlanetLabel Label => label;


    public HoverCallout(
        Transform parent,
        string name,
        HoverCalloutStyle style,
        Font font,
        int fontSize,
        float labelScale,
        string sortingLayerName)
    {
        this.style = style;

        // Text gets its own root so GetComponentInChildren finds only the TextMesh.
        GameObject textRoot = new GameObject(name);
        textRoot.transform.SetParent(parent, false);

        label = PlanetLabel.Create(
            textRoot.transform, font, fontSize, labelScale,
            sortingLayerName, style.calloutSortingOrder + 2);

        labelRenderer = textRoot.GetComponentInChildren<Renderer>(true);

        reticle = CreateRenderer(parent, name + "_Reticle", GetReticleSprite(style.calloutPixelsPerUnit),
            sortingLayerName, style.calloutSortingOrder + 1);
        connector = CreateRenderer(parent, name + "_Connector", GetConnectorSprite(),
            sortingLayerName, style.calloutSortingOrder);
    }


    public void SetText(string text, Color colour) => label.SetText(text, colour);


    public void Hide()
    {
        label.SetActive(false);
        reticle.gameObject.SetActive(false);
        connector.gameObject.SetActive(false);
    }


    /// <summary>Shows and lays out the callout around a world-space target (tile centre).</summary>
    public void Show(Vector3 targetWorld, Camera camera)
    {
        label.SetActive(true);

        if (labelRenderer == null || camera == null)
        {
            label.SetWorldPosition(targetWorld);
            return;
        }

        // Reticle centred on the target.
        reticle.transform.position = Snap(targetWorld);
        float rs = Mathf.Max(0.05f, style.reticleScale);
        reticle.transform.localScale = new Vector3(rs, rs, 1f);
        reticle.color = style.reticleColor;
        reticle.gameObject.SetActive(true);

        Vector2 preferred = PreferredDirection(targetWorld, camera);
        Vector2[] candidates =
        {
            preferred,
            new Vector2(-preferred.x, preferred.y),
            new Vector2(preferred.x, -preferred.y),
            new Vector2(-preferred.x, -preferred.y)
        };

        float hx = Mathf.Abs(style.labelDiagonalOffset.x);
        float hy = Mathf.Abs(style.labelDiagonalOffset.y);

        Vector3 bestLabel = targetWorld;
        Vector3 bestAnchor = targetWorld;
        Vector2 bestDir = preferred;
        float bestPenalty = float.PositiveInfinity;

        foreach (Vector2 dir in candidates)
        {
            Vector3 anchor = Snap(targetWorld + new Vector3(dir.x * hx, dir.y * hy, 0f));
            label.SetWorldPosition(anchor);
            Vector3 pos = AlignCorner(anchor, dir);
            float penalty = OverflowPenalty(camera);

            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                bestLabel = pos;
                bestAnchor = anchor;
                bestDir = dir;
            }
        }

        label.SetWorldPosition(bestLabel);
        bestLabel = AlignCorner(bestAnchor, bestDir);

        Vector3 shift = ScreenClampShift(camera, bestLabel);
        if (shift.sqrMagnitude > 0.000001f)
        {
            bestAnchor += shift;
            bestLabel += shift;
            label.SetWorldPosition(bestLabel);
        }

        DrawConnectorToAnchor(bestAnchor);
    }


    // ==================== Layout ====================

    private Vector3 AlignCorner(Vector3 anchor, Vector2 dir)
    {
        Bounds b = labelRenderer.bounds;
        Vector3 corner = new Vector3(
            dir.x >= 0f ? b.min.x : b.max.x,
            dir.y >= 0f ? b.min.y : b.max.y,
            b.center.z);

        Vector3 newPos = labelRenderer.transform.position + (anchor - corner);
        label.SetWorldPosition(newPos);
        return newPos;
    }


    private static Vector2 PreferredDirection(Vector3 world, Camera camera)
    {
        Vector3 screen = camera.WorldToScreenPoint(world);
        Rect r = camera.pixelRect;
        return new Vector2(
            screen.x < r.xMin + r.width * 0.5f ? 1f : -1f,
            screen.y < r.yMin + r.height * 0.5f ? 1f : -1f);
    }


    private float OverflowPenalty(Camera camera)
    {
        Rect s = ScreenBounds(camera);
        Rect c = camera.pixelRect;
        float p = style.screenPaddingPixels;
        float overflow = 0f;
        if (s.xMin < c.xMin + p) overflow += c.xMin + p - s.xMin;
        if (s.xMax > c.xMax - p) overflow += s.xMax - (c.xMax - p);
        if (s.yMin < c.yMin + p) overflow += c.yMin + p - s.yMin;
        if (s.yMax > c.yMax - p) overflow += s.yMax - (c.yMax - p);
        return overflow;
    }


    private Vector3 ScreenClampShift(Camera camera, Vector3 currentWorld)
    {
        Rect s = ScreenBounds(camera);
        Rect c = camera.pixelRect;
        float p = style.screenPaddingPixels;

        float dx = 0f, dy = 0f;
        if (s.xMin < c.xMin + p) dx = c.xMin + p - s.xMin;
        else if (s.xMax > c.xMax - p) dx = c.xMax - p - s.xMax;
        if (s.yMin < c.yMin + p) dy = c.yMin + p - s.yMin;
        else if (s.yMax > c.yMax - p) dy = c.yMax - p - s.yMax;

        if (Mathf.Abs(dx) < 0.01f && Mathf.Abs(dy) < 0.01f) return Vector3.zero;

        Vector3 screen = camera.WorldToScreenPoint(currentWorld);
        Vector3 shifted = camera.ScreenToWorldPoint(screen + new Vector3(dx, dy, 0f));
        shifted.z = currentWorld.z;
        return shifted - currentWorld;
    }


    private Rect ScreenBounds(Camera camera)
    {
        Bounds b = labelRenderer.bounds;
        Vector3 min = b.min, max = b.max;
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;

        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3(
                (i & 1) == 0 ? min.x : max.x,
                (i & 2) == 0 ? min.y : max.y,
                (i & 4) == 0 ? min.z : max.z);
            Vector3 sp = camera.WorldToScreenPoint(corner);
            minX = Mathf.Min(minX, sp.x);
            maxX = Mathf.Max(maxX, sp.x);
            minY = Mathf.Min(minY, sp.y);
            maxY = Mathf.Max(maxY, sp.y);
        }

        return Rect.MinMaxRect(minX, minY, maxX, maxY);
    }


    // ==================== Connector ====================

    private void DrawConnectorToAnchor(Vector3 anchor)
    {
        Bounds rb = reticle.bounds;
        float z = rb.center.z;
        Vector3[] corners =
        {
            new Vector3(rb.min.x, rb.min.y, z),
            new Vector3(rb.min.x, rb.max.y, z),
            new Vector3(rb.max.x, rb.min.y, z),
            new Vector3(rb.max.x, rb.max.y, z)
        };

        Vector3 start = corners[0];
        float best = float.PositiveInfinity;
        foreach (Vector3 c in corners)
        {
            float d = (anchor - c).sqrMagnitude;
            if (d < best)
            {
                best = d;
                start = c;
            }
        }

        start = Snap(start);
        Vector3 end = Snap(anchor);
        Vector3 delta = end - start;
        float length = delta.magnitude;
        if (length < 0.0001f)
        {
            connector.gameObject.SetActive(false);
            return;
        }

        connector.transform.position = (start + end) * 0.5f;
        connector.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);

        float thickness = Mathf.Max(1, style.connectorThicknessPixels) / (float)Mathf.Max(1, style.calloutPixelsPerUnit);
        connector.transform.localScale = new Vector3(length, thickness, 1f);
        connector.color = style.connectorColor;
        connector.gameObject.SetActive(true);
    }


    private Vector3 Snap(Vector3 world)
    {
        float unit = 1f / Mathf.Max(1, style.calloutPixelsPerUnit);
        return new Vector3(Mathf.Round(world.x / unit) * unit, Mathf.Round(world.y / unit) * unit, world.z);
    }


    // ==================== Generated sprites ====================

    private static SpriteRenderer CreateRenderer(Transform parent, string name, Sprite sprite, string layer, int order)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingLayerName = layer;
        sr.sortingOrder = order;
        go.SetActive(false);
        return sr;
    }


    /// <summary>Same 8x8 broken-bracket reticle with edge ticks as the planet callout.</summary>
    private static Sprite GetReticleSprite(int pixelsPerUnit)
    {
        pixelsPerUnit = Mathf.Max(1, pixelsPerUnit);
        if (reticleSprite != null && reticleSpritePpu == pixelsPerUnit) return reticleSprite;

        const int size = 8;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        Color[] clear = new Color[size * size];
        for (int i = 0; i < clear.Length; i++) clear[i] = new Color(0f, 0f, 0f, 0f);
        tex.SetPixels(clear);

        int[,] on =
        {
            {0,0},{1,0},{0,1},  {6,0},{7,0},{7,1},
            {0,6},{0,7},{1,7},  {7,6},{6,7},{7,7},
            {3,0},{4,7},{0,4},{7,3}
        };
        for (int i = 0; i < on.GetLength(0); i++) tex.SetPixel(on[i, 0], on[i, 1], Color.white);
        tex.Apply();

        reticleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), pixelsPerUnit);
        reticleSpritePpu = pixelsPerUnit;
        return reticleSprite;
    }


    private static Sprite GetConnectorSprite()
    {
        if (connectorSprite != null) return connectorSprite;

        Texture2D tex = new Texture2D(1, 1)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        tex.SetPixel(0, 0, Color.white);
        tex.Apply();

        connectorSprite = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        return connectorSprite;
    }
}
