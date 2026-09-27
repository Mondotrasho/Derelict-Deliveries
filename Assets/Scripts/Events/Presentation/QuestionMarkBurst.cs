using TMPro;
using UnityEngine;

/// <summary>
/// One tile's event marker, drawn with a font. Runtime generated and pooled by
/// EventMarkerPresenter, which drives it via Tick() - do not add it manually.
///
/// The component sits on the site's tile and acts as an emitter. Each child
/// glyph ("?" / "!") is born near the tile centre and flies OUT from it in a
/// random direction for a random distance, growing from a random start size to
/// a random end size and rotating from a random start angle to a random end
/// angle (each picked inside the configured ranges). It fades out and is
/// re-emitted with new random values. Glyphs are staggered so the tile always
/// has something leaving it.
/// </summary>
public class QuestionMarkBurst : MonoBehaviour
{
    private sealed class Glyph
    {
        public TextMeshPro text;
        public MeshRenderer renderer;
        public float bornAt;
        public float life;
        public float startSize;
        public float endSize;
        public float startAngle;
        public float endAngle;
        public Vector2 spawn;
        public Vector2 direction;
        public float distance;
    }

    private Glyph[] glyphs = new Glyph[0];
    private QuestionMarkBurstSettings settings;
    private System.Random rng = new System.Random();
    private Color colour = Color.white;
    private float alpha = 1f;
    private float tileWorldSize = 1f;
    private bool needsFirstBirth = true;

    public EventSite Site { get; private set; }


    /// <summary>(Re)creates the glyph objects for these settings and sorting.</summary>
    public void Build(
        QuestionMarkBurstSettings burstSettings,
        string sortingLayerName,
        int sortingOrder)
    {
        settings = burstSettings;
        int count = Mathf.Max(1, settings.particleCount);

        if (glyphs.Length != count)
        {
            foreach (Glyph g in glyphs)
            {
                if (g != null && g.text != null) Destroy(g.text.gameObject);
            }

            glyphs = new Glyph[count];
            for (int i = 0; i < count; i++) glyphs[i] = CreateGlyph(i);
        }

        TMP_FontAsset font = settings.font != null ? settings.font : TMP_Settings.defaultFontAsset;
        foreach (Glyph g in glyphs)
        {
            if (font != null) g.text.font = font;
            g.text.fontSize = settings.fontSize;
        }

        SetSorting(sortingLayerName, sortingOrder);
    }


    public void Bind(EventSite site, Vector3 worldCentre, float tileSize, int randomSeed)
    {
        Site = site;
        transform.position = worldCentre;
        tileWorldSize = Mathf.Max(0.0001f, tileSize);
        rng = new System.Random(randomSeed);
        needsFirstBirth = true;
        gameObject.SetActive(true);
    }


    public void Unbind()
    {
        Site = null;
        gameObject.SetActive(false);
    }


    public void SetColour(Color c)
    {
        colour = c;
    }


    /// <summary>Overall opacity (fog, knowledge, crossfades).</summary>
    public void SetAlpha(float a)
    {
        alpha = Mathf.Clamp01(a);
    }


    public void SetSorting(string layer, int order)
    {
        foreach (Glyph g in glyphs)
        {
            if (g == null || g.renderer == null) continue;
            g.renderer.sortingLayerName = layer;
            g.renderer.sortingOrder = order;
        }
    }


    public void Tick(float time)
    {
        if (settings == null || glyphs.Length == 0) return;

        if (needsFirstBirth)
        {
            // Stagger: pretend each glyph was born part-way through a life.
            for (int i = 0; i < glyphs.Length; i++)
            {
                Rebirth(glyphs[i], time);
                glyphs[i].bornAt = time - glyphs[i].life * (i / (float)glyphs.Length);
            }
            needsFirstBirth = false;
        }

        bool visible = alpha > 0.001f;
        Vector3 origin = transform.position;

        foreach (Glyph g in glyphs)
        {
            if (time - g.bornAt >= g.life) Rebirth(g, time);

            g.renderer.enabled = visible;
            if (!visible) continue;

            float t = Mathf.Clamp01((time - g.bornAt) / g.life);
            float eased = Mathf.SmoothStep(0f, 1f, t);

            float sizeTiles = Mathf.Lerp(g.startSize, g.endSize, eased);
            float scale = sizeTiles * tileWorldSize / Mathf.Max(0.01f, settings.glyphHeightAtScaleOne);
            g.text.transform.localScale = new Vector3(scale, scale, 1f);
            g.text.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(g.startAngle, g.endAngle, eased));

            // Ease-out travel: bursts out of the tile, then slows.
            float travelT = 1f - Mathf.Pow(1f - t, Mathf.Max(1f, settings.travelEaseOut));
            Vector2 offset = g.spawn + g.direction * (g.distance * travelT);
            Vector3 pos = origin + new Vector3(offset.x, offset.y, 0f) * tileWorldSize;
            if (settings.snapToPixelGrid) pos = Snap(pos, settings.pixelsPerUnit);
            g.text.transform.position = pos;

            float fadeIn = settings.fadeInPortion > 0f ? Mathf.Clamp01(t / settings.fadeInPortion) : 1f;
            float fadeOut = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(settings.fadeOutStart, 1f, t));
            Color c = colour;
            c.a *= alpha * fadeIn * fadeOut;
            g.text.color = c;
        }
    }


    private void Rebirth(Glyph g, float time)
    {
        g.bornAt = time;
        g.life = Mathf.Max(0.05f, settings.lifetime.Sample(rng));
        g.startSize = Mathf.Max(0f, settings.startSize.Sample(rng));
        g.endSize = Mathf.Max(0f, settings.endSize.Sample(rng));
        g.startAngle = settings.startRotation.Sample(rng);
        g.endAngle = settings.endRotation.Sample(rng);

        float jitterRadius = settings.spawnJitter * Mathf.Sqrt((float)rng.NextDouble());
        float jitterAngle = (float)rng.NextDouble() * Mathf.PI * 2f;
        g.spawn = new Vector2(Mathf.Cos(jitterAngle), Mathf.Sin(jitterAngle)) * jitterRadius;

        // Emit angle is measured from straight up, clockwise-positive like a compass.
        float emit = settings.emitAngle.Sample(rng) * Mathf.Deg2Rad;
        g.direction = new Vector2(Mathf.Sin(emit), Mathf.Cos(emit));
        g.distance = Mathf.Max(0f, settings.travelDistance.Sample(rng));

        string chars = string.IsNullOrEmpty(settings.glyphCharacters) ? "?" : settings.glyphCharacters;
        g.text.text = chars[rng.Next(chars.Length)].ToString();
    }


    private Glyph CreateGlyph(int index)
    {
        GameObject go = new GameObject("Glyph " + index);
        go.transform.SetParent(transform, false);

        TextMeshPro text = go.AddComponent<TextMeshPro>();
        text.alignment = TextAlignmentOptions.Center;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        text.rectTransform.sizeDelta = new Vector2(2f, 2f);
        text.text = "?";

        return new Glyph
        {
            text = text,
            renderer = go.GetComponent<MeshRenderer>()
        };
    }


    private static Vector3 Snap(Vector3 p, int pixelsPerUnit)
    {
        float ppu = Mathf.Max(1, pixelsPerUnit);
        return new Vector3(Mathf.Round(p.x * ppu) / ppu, Mathf.Round(p.y * ppu) / ppu, p.z);
    }
}
