using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Generates the frame sprites at runtime (anti-aliased circle / pointy-top
/// hexagon, filled or as a ring), so there are no mask textures to author.
/// Cached, so each shape is only made once.
/// </summary>
public static class CreditsShapes
{
    private const int Size = 256;
    private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        cache.Clear();
    }


    /// <summary>Filled shape, shrunk inwards by <paramref name="inset"/> (fraction of radius).</summary>
    public static Sprite Fill(AvatarFrameShape shape, float inset) => Get(shape, inset, 0f);

    /// <summary>Border ring of the given thickness (fraction of radius).</summary>
    public static Sprite Ring(AvatarFrameShape shape, float thickness) => Get(shape, 0f, thickness);


    private static Sprite Get(AvatarFrameShape shape, float inset, float ring)
    {
        string key = shape + ":" + Mathf.RoundToInt(inset * 1000f) + ":" + Mathf.RoundToInt(ring * 1000f);
        if (cache.TryGetValue(key, out Sprite cached) && cached != null) return cached;

        Texture2D tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
        {
            name = "CreditsFrame_" + key,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        const float margin = 1.02f;              // keep the edge off the texture border
        float pixel = 2f * margin / Size;         // one pixel in shape units
        Color32[] pixels = new Color32[Size * Size];

        for (int py = 0; py < Size; py++)
        {
            for (int px = 0; px < Size; px++)
            {
                float x = ((px + 0.5f) / Size * 2f - 1f) * margin;
                float y = ((py + 0.5f) / Size * 2f - 1f) * margin;
                float d = Distance(shape, x, y) + inset;

                float outer = Mathf.Clamp01(0.5f - d / pixel);
                float alpha = outer;
                if (ring > 0f)
                {
                    float inner = Mathf.Clamp01(0.5f - (d + ring) / pixel);
                    alpha = outer * (1f - inner);
                }

                pixels[py * Size + px] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha * 255f));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, true);

        Sprite sprite = Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f);
        sprite.name = tex.name;
        cache[key] = sprite;
        return sprite;
    }


    /// <summary>Signed distance to the shape edge (radius 1): negative inside.</summary>
    private static float Distance(AvatarFrameShape shape, float x, float y)
    {
        if (shape == AvatarFrameShape.Circle) return Mathf.Sqrt(x * x + y * y) - 1f;

        // Pointy-top hexagon, circumradius 1, apothem sqrt(3)/2.
        const float apothem = 0.8660254f;
        float ax = Mathf.Abs(x);
        float ay = Mathf.Abs(y);
        float sides = ax - apothem;
        float slants = ax * 0.5f + ay * apothem - apothem;
        return Mathf.Max(sides, slants);
    }
}
