using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Builds and caches point-filtered sprites from tiny text bitmaps
/// ('#' = on, anything else = off; row 0 is the TOP row). White pixels,
/// so SpriteRenderer.color tints them. Same technique RoutePathRenderer uses
/// for its dots. Event markers now use a font (QuestionMarkBurst); this is
/// kept for the generated derelict placeholder sprite.
/// </summary>
public static class PixelGlyphFactory
{
    /// <summary>Small generic hull shape used when a derelict definition has no sprite.</summary>
    public static readonly string[] WreckPlaceholder =
    {
        "..##....",
        ".####.#.",
        "######..",
        ".#.####.",
        "..###.##",
        ".#..##..",
    };

    /// <summary>8x8 diagonal hatch used as the placeholder route-hazard tile.</summary>
    public static readonly string[] HazardHatch =
    {
        "#...#...",
        ".#...#..",
        "..#...#.",
        "...#...#",
        "#...#...",
        ".#...#..",
        "..#...#.",
        "...#...#",
    };

    private static readonly Dictionary<string, Sprite> cache = new Dictionary<string, Sprite>();


    public static Sprite GetWreckPlaceholder(int pixelsPerUnit = 8)
    {
        return GetSprite("wreck", WreckPlaceholder, pixelsPerUnit, new Color(0.62f, 0.66f, 0.72f, 1f));
    }


    public static Sprite GetHazardPlaceholder(int pixelsPerUnit = 8)
    {
        return GetSprite("hazard", HazardHatch, pixelsPerUnit);
    }


    public static Sprite GetSprite(string key, string[] rows, int pixelsPerUnit)
    {
        return GetSprite(key, rows, pixelsPerUnit, Color.white);
    }


    public static Sprite GetSprite(string key, string[] rows, int pixelsPerUnit, Color onColour)
    {
        pixelsPerUnit = Mathf.Max(1, pixelsPerUnit);
        string cacheKey = key + "@" + pixelsPerUnit;

        if (cache.TryGetValue(cacheKey, out Sprite cached) && cached != null)
            return cached;

        int height = rows.Length;
        int width = 0;
        foreach (string r in rows) width = Mathf.Max(width, r.Length);

        Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            name = "PixelGlyph_" + key
        };

        Color clear = new Color(0f, 0f, 0f, 0f);
        for (int y = 0; y < height; y++)
        {
            string row = rows[height - 1 - y];   // texture y=0 is the bottom
            for (int x = 0; x < width; x++)
            {
                bool on = x < row.Length && row[x] == '#';
                tex.SetPixel(x, y, on ? onColour : clear);
            }
        }
        tex.Apply(false, true);

        Sprite sprite = Sprite.Create(
            tex,
            new Rect(0, 0, width, height),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit);
        sprite.name = "PixelGlyph_" + key;

        cache[cacheKey] = sprite;
        return sprite;
    }
}
