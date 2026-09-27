using System;
using TMPro;
using UnityEngine;

/// <summary>
/// Inclusive min/max pair. Each glyph picks its own value inside the range.
/// </summary>
[Serializable]
public struct FloatRange
{
    public float min;
    public float max;

    public FloatRange(float min, float max)
    {
        this.min = min;
        this.max = max;
    }

    public float Sample(System.Random rng)
    {
        return Mathf.Lerp(min, max, (float)rng.NextDouble());
    }
}


/// <summary>
/// Parameters for the font-based event marker. The tile acts as an emitter:
/// every glyph is born at the tile centre, flies OUT from it a random distance
/// inside Travel Distance, grows from a random size inside Start Size to a random
/// size inside End Size, turns from a random angle inside Start Rotation to a
/// random angle inside End Rotation, then fades and is re-emitted with fresh
/// random values. Sizes and distances are in tiles (1 = one cell).
/// </summary>
[Serializable]
public sealed class QuestionMarkBurstSettings
{
    [Header("Glyphs")]
    [Tooltip("TextMeshPro font asset. Empty = the TMP default font.")]
    public TMP_FontAsset font;

    [Tooltip("Characters to draw. Each glyph picks one at random every rebirth: \"?\" for question marks, \"!\" for exclamation marks, \"?!\" to mix.")]
    public string glyphCharacters = "?";

    [Tooltip("Glyphs on the tile at once, staggered through their lifetimes so the tile is never empty.")]
    [Min(1)] public int particleCount = 3;

    [Tooltip("TMP font size used for every glyph. Visible size comes from Start/End Size, so this mainly affects text quality.")]
    [Min(0.1f)] public float fontSize = 10f;

    [Tooltip("Font units per tile: how tall (in tiles) a glyph is at scale 1 with the font size above. Tune once per font so Start/End Size read as tile fractions.")]
    [Min(0.01f)] public float glyphHeightAtScaleOne = 1f;

    [Header("Growth (tiles)")]
    public FloatRange startSize = new FloatRange(0.25f, 0.4f);
    public FloatRange endSize = new FloatRange(0.6f, 0.85f);

    [Header("Rotation (degrees)")]
    public FloatRange startRotation = new FloatRange(-30f, 30f);
    public FloatRange endRotation = new FloatRange(-8f, 8f);

    [Header("Timing (seconds)")]
    public FloatRange lifetime = new FloatRange(1.2f, 1.8f);

    [Range(0f, 1f)] public float fadeInPortion = 0.15f;
    [Range(0f, 1f)] public float fadeOutStart = 0.7f;

    [Header("Emission (tiles / degrees)")]
    [Tooltip("How far from the tile centre a glyph travels over its life. Values above 0.5 carry it past the tile edge.")]
    public FloatRange travelDistance = new FloatRange(0.6f, 1.2f);

    [Tooltip("Emission direction, in degrees from straight up. (-180, 180) = any direction; (-45, 45) = an upward plume.")]
    public FloatRange emitAngle = new FloatRange(-180f, 180f);

    [Tooltip("Random offset of the birth point around the tile centre, in tiles.")]
    [Min(0f)] public float spawnJitter = 0.08f;

    [Tooltip("1 = constant speed; higher = fast burst out of the tile that slows as it travels.")]
    [Min(1f)] public float travelEaseOut = 2f;

    [Tooltip("Snap glyph positions to the pixel grid (keeps motion in step with pixel-art tiles).")]
    public bool snapToPixelGrid = true;

    [Min(1)] public int pixelsPerUnit = 8;
}
