using System.Collections;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

public enum CombatFaceMood
{
    Default,
    Angry,
    Confused
}


/// <summary>
/// The ship AI's face in the combat screen: one Image that swaps between three
/// expression sprites (Default, Angry, Confused). Changes and idle moments are
/// shown as screen glitches - stepped horizontal tears, flicker and a frame or
/// two of the previous expression - rather than smooth motion.
/// Sits between the face panel's background and frame.
/// </summary>
[RequireComponent(typeof(Image))]
public sealed class CombatFace : MonoBehaviour
{
    [FormerlySerializedAs("neutral")] [SerializeField] private Sprite defaultFace;
    [FormerlySerializedAs("worried")] [SerializeField] private Sprite angry;
    [FormerlySerializedAs("pleased")] [SerializeField] private Sprite confused;

    [Header("Glitch on change")]
    [Min(1)] [SerializeField] private int changeGlitchFrames = 7;
    [Min(0.01f)] [SerializeField] private float glitchFrameSeconds = 0.04f;
    [Tooltip("Largest sideways tear, in pixels (stepped, not smooth).")]
    [Min(0f)] [SerializeField] private float glitchOffsetPixels = 6f;
    [Tooltip("Largest horizontal stretch during a tear (1 = none).")]
    [Min(1f)] [SerializeField] private float glitchStretch = 1.15f;
    [Range(0f, 1f)] [SerializeField] private float flickerChance = 0.35f;

    [Header("Idle glitches")]
    [SerializeField] private bool idleGlitches = true;
    [Min(0.1f)] [SerializeField] private float idleGlitchMinSeconds = 2.5f;
    [Min(0.1f)] [SerializeField] private float idleGlitchMaxSeconds = 6f;
    [Min(1)] [SerializeField] private int idleGlitchFrames = 3;

    public CombatFaceMood Mood { get; private set; } = CombatFaceMood.Default;

    private Image image;
    private RectTransform rect;
    private Vector2 home;
    private Color baseColour;
    private Coroutine glitch;
    private float nextIdleGlitch;


    private void Awake()
    {
        image = GetComponent<Image>();
        rect = (RectTransform)transform;
        home = rect.anchoredPosition;
        baseColour = image.color;
        Apply(CombatFaceMood.Default);
        ScheduleIdle();
    }


    private void OnDisable()
    {
        glitch = null;
        Settle();
    }


    private void Update()
    {
        if (!idleGlitches || glitch != null) return;
        if (Time.unscaledTime < nextIdleGlitch) return;
        glitch = StartCoroutine(Glitch(idleGlitchFrames, null));
        ScheduleIdle();
    }


    public void SetMood(CombatFaceMood mood)
    {
        if (mood == Mood) return;
        Sprite previous = image != null ? image.sprite : null;
        Apply(mood);
        if (!isActiveAndEnabled) return;
        if (glitch != null) StopCoroutine(glitch);
        glitch = StartCoroutine(Glitch(changeGlitchFrames, previous));
        ScheduleIdle();
    }


    private void Apply(CombatFaceMood mood)
    {
        Mood = mood;
        Sprite sprite = SpriteFor(mood);
        if (sprite != null && image != null) image.sprite = sprite;
    }


    private Sprite SpriteFor(CombatFaceMood mood)
    {
        switch (mood)
        {
            case CombatFaceMood.Angry: return angry;
            case CombatFaceMood.Confused: return confused;
            default: return defaultFace;
        }
    }


    /// <summary>A few stepped frames of tearing and flicker. With a previous
    /// sprite, some frames show it, so the change looks like a bad signal.</summary>
    private IEnumerator Glitch(int frames, Sprite previous)
    {
        Sprite current = image.sprite;
        for (int i = 0; i < frames; i++)
        {
            bool last = i == frames - 1;
            float tear = Mathf.Round(Random.Range(-1f, 1f) * glitchOffsetPixels);
            rect.anchoredPosition = home + new Vector2(last ? 0f : tear, 0f);
            rect.localScale = new Vector3(last ? 1f : Random.Range(1f, glitchStretch), 1f, 1f);

            if (previous != null && !last && Random.value < 0.4f) image.sprite = previous;
            else image.sprite = current;

            Color c = baseColour;
            if (!last && Random.value < flickerChance) c.a *= Random.value < 0.5f ? 0.15f : 0.6f;
            image.color = c;

            float t = 0f;
            while (t < glitchFrameSeconds)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        image.sprite = current;
        Settle();
        glitch = null;
    }


    private void Settle()
    {
        if (rect == null) return;
        rect.anchoredPosition = home;
        rect.localScale = Vector3.one;
        if (image != null) image.color = baseColour;
    }


    private void ScheduleIdle()
    {
        nextIdleGlitch = Time.unscaledTime + Random.Range(idleGlitchMinSeconds, Mathf.Max(idleGlitchMinSeconds, idleGlitchMaxSeconds));
    }
}
