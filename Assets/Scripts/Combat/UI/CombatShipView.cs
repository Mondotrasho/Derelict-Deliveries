using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One ship in the combat view: the ship sprite and its shield drawn over it
/// move together (this RectTransform is what moves). Indicator children - the
/// ship's damage lines - stay where they were placed while the ship moves, flash
/// when the ship is hit, flash red while its hull is low, and disappear with the
/// ship (explosion or escape).
///
/// Every animation is a coroutine the combat screen yields on, so a round plays
/// out step by step. Children are found by name: Ship, Shield, and every child
/// whose name starts with Indicator.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public sealed class CombatShipView : MonoBehaviour
{
    [Header("Parts (found by child name if empty)")]
    [SerializeField] private Image ship;
    [SerializeField] private Image shield;
    [Tooltip("Extra damage-line images. Children whose names start with 'Indicator' are added automatically.")]
    [SerializeField] private List<Image> indicators = new List<Image>();

    [Header("Shield")]
    [Range(0f, 1f)] [SerializeField] private float shieldAlpha = 0.55f;
    [Range(0f, 1f)] [SerializeField] private float shieldRaisedAlpha = 0.9f;

    [Header("Motion (pixels / seconds)")]
    [SerializeField] private float lungeDistance = 40f;
    [SerializeField] private float lungeSeconds = 0.28f;
    [SerializeField] private float shakePixels = 8f;
    [SerializeField] private float shakeSeconds = 0.3f;
    [SerializeField] private float evadeDistance = 45f;
    [SerializeField] private float evadeSeconds = 0.45f;
    [SerializeField] private float fleeDistance = 400f;
    [SerializeField] private float fleeSeconds = 0.7f;

    [Header("Indicators (damage lines)")]
    [Tooltip("Gentle drift in place. 0 = still.")]
    [SerializeField] private float indicatorBobPixels = 0f;
    [SerializeField] private float indicatorBobSpeed = 2.2f;
    [SerializeField] private Color indicatorColour = Color.white;
    [SerializeField] private Color lowHullColour = new Color(1f, 0.25f, 0.25f, 1f);
    [SerializeField] private float lowHullFlashSpeed = 6f;
    [SerializeField] private float indicatorJitterPixels = 3f;

    public RectTransform Rect { get; private set; }
    public bool ShieldVisible => shield != null && shield.gameObject.activeSelf;

    private Vector2 home;
    private Vector2 placedHome;          // where it sits in the scene
    private float baseScale = 1f;
    private readonly List<Vector3> indicatorHomes = new List<Vector3>();   // local to the combat view, so they ignore ship motion
    private bool lowHull;
    private float indicatorJitter;
    private float flashTimer;
    private Color flashColour = Color.red;
    private float indicatorAlpha = 1f;
    private bool indicatorsVisible = true;
    private Color shipColour = Color.white;
    private bool awoken;


    private void Awake()
    {
        if (awoken) return;
        awoken = true;
        Rect = (RectTransform)transform;
        placedHome = Rect.anchoredPosition;
        home = placedHome;
        if (ship == null) ship = FindImage("Ship");
        if (shield == null) shield = FindImage("Shield");
        if (ship != null) shipColour = ship.color;

        foreach (Transform child in transform)
        {
            Image img = child.GetComponent<Image>();
            if (img != null && child.name.StartsWith("Indicator") && !indicators.Contains(img)) indicators.Add(img);
        }

        Transform view = Rect.parent;
        indicatorHomes.Clear();
        foreach (Image img in indicators)
        {
            indicatorHomes.Add(img != null && view != null ? view.InverseTransformPoint(img.rectTransform.position) : Vector3.zero);
        }
    }


    private void LateUpdate()
    {
        if (indicators.Count == 0) return;

        float t = Time.unscaledTime;
        Transform view = Rect.parent;
        Vector3 bob = new Vector3(Mathf.Cos(t * indicatorBobSpeed * 0.7f), Mathf.Sin(t * indicatorBobSpeed), 0f) * indicatorBobPixels;
        if (indicatorJitter > 0f)
        {
            Vector2 j = Random.insideUnitCircle * indicatorJitterPixels * indicatorJitter;
            bob += new Vector3(Mathf.Round(j.x), Mathf.Round(j.y), 0f);
            indicatorJitter = Mathf.Max(0f, indicatorJitter - Time.unscaledDeltaTime * 4f);
        }
        if (flashTimer > 0f) flashTimer -= Time.unscaledDeltaTime;

        Color c = flashTimer > 0f ? flashColour
            : lowHull && Mathf.Sin(t * lowHullFlashSpeed) > 0f ? lowHullColour
            : indicatorColour;
        c.a *= indicatorAlpha;

        for (int i = 0; i < indicators.Count; i++)
        {
            Image img = indicators[i];
            if (img == null) continue;
            img.gameObject.SetActive(indicatorsVisible);
            if (view != null) img.rectTransform.position = view.TransformPoint(indicatorHomes[i] + bob);   // stay put while the ship moves
            img.color = c;
        }
    }


    // ------------------------------------------------------------------ setup

    /// <summary>Per-enemy placement: offset (pixels) from where it was placed, and size.</summary>
    public void SetPlacement(Vector2 offset, float scale)
    {
        Awake();
        home = placedHome + offset;
        baseScale = Mathf.Max(0.05f, scale);
    }


    public void SetSprites(Sprite shipSprite, Sprite shieldSprite)
    {
        if (ship != null && shipSprite != null) ship.sprite = shipSprite;
        if (shield != null && shieldSprite != null) shield.sprite = shieldSprite;
    }


    /// <summary>Back to the start pose: in place, visible, shields as given.</summary>
    public void ResetVisual(bool shieldsUp)
    {
        StopAllCoroutines();
        Awake();
        Rect.anchoredPosition = home;
        Rect.localScale = Vector3.one * baseScale;
        Rect.localRotation = Quaternion.identity;
        SetAlpha(ship, shipColour, 1f);
        if (ship != null) ship.gameObject.SetActive(true);
        SetShieldVisible(shieldsUp);
        lowHull = false;
        indicatorJitter = 0f;
        flashTimer = 0f;
        indicatorAlpha = 1f;
        indicatorsVisible = true;
    }


    public void SetShieldVisible(bool visible)
    {
        if (shield == null) return;
        shield.gameObject.SetActive(visible);
        shield.rectTransform.localScale = Vector3.one;
        SetAlpha(shield, shield.color, shieldAlpha);
    }


    public void SetLowHull(bool low) => lowHull = low;
    public void Jitter() => indicatorJitter = 1f;


    /// <summary>Damage lines flash a colour (and twitch) when this ship is hit.</summary>
    public void FlashDamage(Color colour, float seconds)
    {
        flashColour = colour;
        flashTimer = seconds;
        Jitter();
    }


    // ------------------------------------------------------------- animations

    /// <summary>Quick move toward a target and back (attack).</summary>
    public IEnumerator Lunge(Vector2 towards)
    {
        Vector2 dir = towards.sqrMagnitude > 0.0001f ? towards.normalized : Vector2.up;
        yield return MoveOutAndBack(dir * lungeDistance, lungeSeconds);
    }


    /// <summary>Side to side (evasive).</summary>
    public IEnumerator Evade()
    {
        float half = evadeSeconds * 0.5f;
        yield return MoveOutAndBack(Vector2.right * evadeDistance, half);
        yield return MoveOutAndBack(Vector2.left * evadeDistance, half);
    }


    /// <summary>Took hull damage: shake in place.</summary>
    public IEnumerator Shake(float strength = 1f)
    {
        float t = 0f;
        while (t < shakeSeconds)
        {
            t += Time.unscaledDeltaTime;
            float fade = 1f - Mathf.Clamp01(t / shakeSeconds);
            Rect.anchoredPosition = home + Random.insideUnitCircle * shakePixels * strength * fade;
            yield return null;
        }
        Rect.anchoredPosition = home;
    }


    /// <summary>Defensive: shield fades up to full and pulses once.</summary>
    public IEnumerator RaiseShield()
    {
        if (shield == null) yield break;
        shield.gameObject.SetActive(true);
        yield return Fade(shield, 0f, shieldRaisedAlpha, 0.2f);
        yield return Pulse(shield.rectTransform, 1.08f, 0.2f);
        yield return Fade(shield, shieldRaisedAlpha, shieldAlpha, 0.2f);
    }


    /// <summary>A hit absorbed by the shield: quick flicker.</summary>
    public IEnumerator FlickerShield()
    {
        if (shield == null || !shield.gameObject.activeSelf) yield break;
        for (int i = 0; i < 4; i++)
        {
            SetAlpha(shield, shield.color, i % 2 == 0 ? 1f : 0.15f);
            yield return Wait(0.05f);
        }
        SetAlpha(shield, shield.color, shieldAlpha);
    }


    /// <summary>Shields reached 0: swell and fade out, then hidden.</summary>
    public IEnumerator PopShield()
    {
        if (shield == null || !shield.gameObject.activeSelf) yield break;
        RectTransform r = shield.rectTransform;
        float t = 0f, d = 0.3f;
        while (t < d)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / d);
            r.localScale = Vector3.one * Mathf.Lerp(1f, 1.35f, k);
            SetAlpha(shield, shield.color, Mathf.Lerp(1f, 0f, k));
            yield return null;
        }
        SetShieldVisible(false);
    }


    /// <summary>Destroyed: white flash, hard shake, then shrink and fade.</summary>
    public IEnumerator Explode()
    {
        SetShieldVisible(false);
        if (ship != null) ship.color = Color.white;
        yield return Shake(2f);
        float t = 0f, d = 0.45f;
        while (t < d)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / d);
            Rect.localScale = Vector3.one * (baseScale * Mathf.Lerp(1f, 0.2f, k));
            Rect.localRotation = Quaternion.Euler(0f, 0f, k * 90f);
            SetAlpha(ship, Color.white, 1f - k);
            indicatorAlpha = 1f - k;
            yield return null;
        }
        indicatorsVisible = false;
    }


    /// <summary>Flee: slide down. Success leaves the view; failure jolts back.</summary>
    public IEnumerator FleeDown(bool success)
    {
        if (success)
        {
            yield return MoveTo(home, home + Vector2.down * fleeDistance, fleeSeconds);
            indicatorsVisible = false;   // gone with the ship
        }
        else
        {
            yield return MoveOutAndBack(Vector2.down * fleeDistance * 0.15f, fleeSeconds * 0.5f);
            yield return Shake(0.6f);
        }
    }


    // ---------------------------------------------------------------- helpers

    private IEnumerator MoveOutAndBack(Vector2 offset, float seconds)
    {
        float half = Mathf.Max(0.01f, seconds * 0.5f);
        yield return MoveTo(home, home + offset, half);
        yield return MoveTo(home + offset, home, half);
    }


    private IEnumerator MoveTo(Vector2 from, Vector2 to, float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Clamp01(t / seconds);
            Rect.anchoredPosition = Vector2.Lerp(from, to, k * k * (3f - 2f * k));
            yield return null;
        }
        Rect.anchoredPosition = to;
    }


    private static IEnumerator Fade(Image image, float from, float to, float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            SetAlpha(image, image.color, Mathf.Lerp(from, to, Mathf.Clamp01(t / seconds)));
            yield return null;
        }
        SetAlpha(image, image.color, to);
    }


    private static IEnumerator Pulse(RectTransform r, float scale, float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            float k = Mathf.Sin(Mathf.Clamp01(t / seconds) * Mathf.PI);
            r.localScale = Vector3.one * Mathf.Lerp(1f, scale, k);
            yield return null;
        }
        r.localScale = Vector3.one;
    }


    private static IEnumerator Wait(float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            yield return null;
        }
    }


    private static void SetAlpha(Image image, Color baseColour, float alpha)
    {
        if (image == null) return;
        baseColour.a = alpha;
        image.color = baseColour;
    }


    private Image FindImage(string childName)
    {
        Transform t = transform.Find(childName);
        return t != null ? t.GetComponent<Image>() : null;
    }
}
