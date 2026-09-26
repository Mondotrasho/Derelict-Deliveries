using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>What a combat bar shows. Any bar can show any stat.</summary>
public enum CombatBarStat
{
    PlayerHull,
    PlayerShields,
    EnemyHull,
    EnemyShields
}


/// <summary>
/// A combat bar made of a fill (drawn first) and a frame (drawn on top). Which
/// stat it shows is chosen here, so any frame + matching fill pair can be used
/// for any stat. The fill gets a colour filter (Fill Tint).
///
/// With a sprite on the fill it uses Image.Type.Filled (fill amount); with no
/// sprite (placeholder layout) it shrinks the fill's rect instead, so it still
/// reads correctly before the art is in.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public sealed class SpriteBar : MonoBehaviour
{
    [SerializeField] private CombatBarStat stat = CombatBarStat.PlayerHull;

    [Tooltip("Empty = child named 'Fill'.")]
    [SerializeField] private Image fill;

    [Tooltip("Colour filter multiplied into the fill sprite.")]
    [SerializeField] private Color fillTint = Color.white;

    [SerializeField] private Image.FillMethod fillMethod = Image.FillMethod.Vertical;
    [Tooltip("For Vertical: 0 = fills from the bottom, 1 = from the top.")]
    [SerializeField] private int fillOrigin = 0;

    [Min(0f)] [SerializeField] private float tweenSeconds = 0.35f;
    [SerializeField] private Color hitFlashColour = Color.white;
    [Min(0f)] [SerializeField] private float hitFlashSeconds = 0.12f;

    public CombatBarStat Stat => stat;
    public float Fraction { get; private set; } = 1f;

    private RectTransform fillRect;
    private Coroutine tween;


    private void Awake()
    {
        if (fill == null)
        {
            Transform child = transform.Find("Fill");
            if (child != null) fill = child.GetComponent<Image>();
        }
        if (fill == null) return;

        fillRect = fill.rectTransform;
        ApplyFillSettings();
        Apply(Fraction);
    }


    private void OnValidate()
    {
        if (fill != null) fill.color = fillTint;
    }


    /// <summary>Jump straight to a value (encounter start).</summary>
    public void SetInstant(float fraction)
    {
        if (tween != null) StopCoroutine(tween);
        tween = null;
        Apply(Mathf.Clamp01(fraction));
    }


    /// <summary>Slide to a value, with a short flash when it goes down.</summary>
    public void SetTarget(float fraction)
    {
        fraction = Mathf.Clamp01(fraction);
        if (fill == null || !isActiveAndEnabled)
        {
            Apply(fraction);
            return;
        }

        if (tween != null) StopCoroutine(tween);
        tween = StartCoroutine(Tween(Fraction, fraction));
    }


    private IEnumerator Tween(float from, float to)
    {
        bool dropping = to < from;
        float t = 0f;
        while (t < tweenSeconds)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / Mathf.Max(0.0001f, tweenSeconds));
            Apply(Mathf.Lerp(from, to, 1f - (1f - k) * (1f - k)));
            fill.color = dropping && t < hitFlashSeconds ? hitFlashColour : fillTint;
            yield return null;
        }

        Apply(to);
        fill.color = fillTint;
        tween = null;
    }


    private void Apply(float fraction)
    {
        Fraction = fraction;
        if (fill == null) return;

        if (fill.sprite != null)
        {
            fill.fillAmount = fraction;
        }
        else if (fillRect != null)
        {
            // Placeholder mode: shrink the rect along the fill direction.
            bool vertical = fillMethod == Image.FillMethod.Vertical;
            bool fromStart = fillOrigin == 0;
            Vector2 min = Vector2.zero, max = Vector2.one;
            if (vertical)
            {
                if (fromStart) max.y = fraction; else min.y = 1f - fraction;
            }
            else
            {
                if (fromStart) max.x = fraction; else min.x = 1f - fraction;
            }
            fillRect.anchorMin = min;
            fillRect.anchorMax = max;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
        }
    }


    [ContextMenu("Test: set to 50%")]
    private void TestHalf() => SetTarget(0.5f);

    [ContextMenu("Test: set to 100%")]
    private void TestFull() => SetTarget(1f);


    private void ApplyFillSettings()
    {
        fill.color = fillTint;
        if (fill.sprite != null)
        {
            fill.type = Image.Type.Filled;
            fill.fillMethod = fillMethod;
            fill.fillOrigin = fillOrigin;
        }
    }
}
