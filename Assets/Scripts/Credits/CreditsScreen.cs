using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Scrolling credits over a background image. Builds its own overlay canvas
/// from a CreditsRoll asset every time it opens, so edits to the roll or the
/// people assets show up next time without touching the scene.
///
/// Open() / Close() / PlayAndWait() - the pause menu uses PlayAndWait.
/// Uses unscaled time, so it runs while the game is paused.
/// Mouse wheel scrolls manually; links are clickable; BACK closes.
/// </summary>
[DisallowMultipleComponent]
public sealed class CreditsScreen : MonoBehaviour
{
    [SerializeField] private CreditsRoll roll;
    [Tooltip("Draw order of the credits canvas. Must be above the game UI and the pause window.")]
    [SerializeField] private int sortingOrder = 1000;
    [Tooltip("Open on Start - for a standalone credits scene.")]
    [SerializeField] private bool openOnStart = false;
    [SerializeField] private string backLabel = "BACK";

    public bool IsOpen { get; private set; }
    public event Action Closed;

    private GameObject root;
    private RectTransform canvasRect;
    private RectTransform content;
    private float scrollY;
    private float contentHeight;
    private float viewportHeight;
    private float delayLeft;
    private bool stopped;
    private float speedMultiplier = 1f;


    private void Start()
    {
        if (openOnStart) Open();
    }


    private void OnDestroy()
    {
        if (root != null) Destroy(root);
    }


    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    public void Open()
    {
        if (roll == null)
        {
            Debug.LogWarning($"{name}: CreditsScreen has no Credits Roll assigned.", this);
            return;
        }

        Build();
        root.SetActive(true);
        IsOpen = true;
        stopped = false;
        speedMultiplier = 1f;
        delayLeft = roll.startDelay;

        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        viewportHeight = canvasRect.rect.height;
        contentHeight = content.rect.height;
        scrollY = -viewportHeight;   // content starts just below the screen
        Apply();
    }


    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (root != null) root.SetActive(false);
        Closed?.Invoke();
    }


    /// <summary>Opens the credits and waits until they are closed.</summary>
    public IEnumerator PlayAndWait()
    {
        Open();
        while (IsOpen) yield return null;
    }


    public void SetRoll(CreditsRoll newRoll)
    {
        roll = newRoll;
    }


    // ------------------------------------------------------------------
    // Scrolling
    // ------------------------------------------------------------------

    private void Update()
    {
        if (!IsOpen) return;

        contentHeight = content.rect.height;   // layout can settle a frame late
        float dt = Time.unscaledDeltaTime;
        float wheel = Mouse.current != null ? Mouse.current.scroll.ReadValue().y : 0f;
        if (Mathf.Abs(wheel) > 0.01f)
        {
            scrollY -= wheel * roll.wheelSpeed;
            stopped = false;
        }

        // Hold any key / mouse button / touch to fast-forward (eased so it doesn't jerk).
        float targetMultiplier = AnyInputHeld() ? roll.holdSpeedMultiplier : 1f;
        speedMultiplier = roll.holdEaseSeconds > 0f
            ? Mathf.MoveTowards(speedMultiplier, targetMultiplier,
                                Mathf.Abs(roll.holdSpeedMultiplier - 1f) * dt / roll.holdEaseSeconds)
            : targetMultiplier;

        if (delayLeft > 0f) delayLeft -= dt;
        else if (!stopped) scrollY += roll.scrollSpeed * speedMultiplier * dt;

        float stopAt = contentHeight - viewportHeight * 0.6f;
        if (roll.loop)
        {
            if (scrollY > contentHeight) scrollY = -viewportHeight;
        }
        else if (scrollY >= stopAt)
        {
            scrollY = stopAt;
            stopped = true;
        }

        scrollY = Mathf.Clamp(scrollY, -viewportHeight, contentHeight);
        Apply();
    }


    private static bool AnyInputHeld()
    {
        if (Keyboard.current != null && Keyboard.current.anyKey.isPressed) return true;
        Mouse mouse = Mouse.current;
        if (mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed)) return true;
        Gamepad pad = Gamepad.current;
        if (pad != null && (pad.buttonSouth.isPressed || pad.rightTrigger.isPressed)) return true;
        Touchscreen touch = Touchscreen.current;
        return touch != null && touch.primaryTouch.press.isPressed;
    }


    private void Apply()
    {
        if (content != null) content.anchoredPosition = new Vector2(0f, scrollY);
    }


    // ------------------------------------------------------------------
    // Build
    // ------------------------------------------------------------------

    private void Build()
    {
        if (root != null) Destroy(root);

        root = new GameObject("Credits Canvas", typeof(RectTransform));
        root.transform.SetParent(transform, false);

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = sortingOrder;

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        root.AddComponent<GraphicRaycaster>();
        canvasRect = (RectTransform)root.transform;

        // Backdrop (also blocks clicks to anything underneath).
        Image backdrop = NewImage("Backdrop", root.transform, null, roll.backdropColour);
        Stretch(backdrop.rectTransform);
        backdrop.raycastTarget = true;

        if (roll.background != null)
        {
            Image bg = NewImage("Background", root.transform, roll.background, roll.backgroundTint);
            bg.raycastTarget = false;
            AspectRatioFitter fit = bg.gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fit.aspectRatio = roll.background.rect.width / Mathf.Max(1f, roll.background.rect.height);
        }

        // Scrolling content, clipped to the screen.
        RectTransform viewport = NewRect("Viewport", root.transform);
        Stretch(viewport);
        viewport.gameObject.AddComponent<RectMask2D>();

        content = NewRect("Content", viewport);
        content.anchorMin = new Vector2(0.5f, 1f);
        content.anchorMax = new Vector2(0.5f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.sizeDelta = new Vector2(ContentWidth(), 0f);

        VerticalLayoutGroup layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.spacing = roll.entrySpacing;
        layout.padding = new RectOffset(0, 0, 0, 0);
        ContentSizeFitter fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        BuildContent();
        BuildBackButton();

        root.SetActive(false);
    }


    private float ContentWidth() => roll.avatarSize + 48f + roll.textColumnWidth;


    private void BuildContent()
    {
        float width = ContentWidth();

        Text(content, roll.title, roll.titleFont, roll.titleSize, roll.titleColour, TextAlignmentOptions.Center, width);
        if (!string.IsNullOrWhiteSpace(roll.tagline))
            Text(content, roll.tagline, roll.bodyFont, roll.taglineSize, roll.contributionColour, TextAlignmentOptions.Center, width);

        foreach (CreditsSection section in roll.sections)
        {
            if (section == null) continue;

            Spacer(content, roll.sectionSpacing - roll.entrySpacing);
            Text(content, section.heading, roll.headingFont, roll.headingSize, roll.headingColour, TextAlignmentOptions.Center, width);

            if (!string.IsNullOrWhiteSpace(section.intro))
                Text(content, section.intro, roll.bodyFont, roll.bodySize, roll.noteColour, TextAlignmentOptions.Center, width);

            foreach (CreditsPerson person in section.people)
            {
                if (person != null) BuildEntry(person);
            }

            if (!string.IsNullOrWhiteSpace(section.outro))
                Text(content, section.outro, roll.bodyFont, roll.bodySize, roll.noteColour, TextAlignmentOptions.Center, width);
        }

        if (!string.IsNullOrWhiteSpace(roll.closingLine))
        {
            Spacer(content, roll.sectionSpacing);
            Text(content, roll.closingLine, roll.headingFont, roll.headingSize, roll.titleColour, TextAlignmentOptions.Center, width);
        }
    }


    private void BuildEntry(CreditsPerson person)
    {
        RectTransform row = NewRect("Entry: " + person.displayName, content);
        HorizontalLayoutGroup rowLayout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        rowLayout.childAlignment = TextAnchor.MiddleLeft;
        rowLayout.spacing = 48f;
        rowLayout.childControlWidth = true;
        rowLayout.childControlHeight = true;
        rowLayout.childForceExpandWidth = false;
        rowLayout.childForceExpandHeight = false;

        BuildFrame(row, roll.avatarSize, person.avatar, person.avatarFit, person.avatarBackground,
                   person.avatarZoom, person.avatarOffset, person.Initials);

        RectTransform column = NewRect("Text", row);
        LayoutElement columnSize = column.gameObject.AddComponent<LayoutElement>();
        columnSize.preferredWidth = roll.textColumnWidth;
        columnSize.minWidth = roll.textColumnWidth;
        VerticalLayoutGroup columnLayout = column.gameObject.AddComponent<VerticalLayoutGroup>();
        columnLayout.childAlignment = TextAnchor.UpperLeft;
        columnLayout.spacing = 6f;
        columnLayout.childControlWidth = true;
        columnLayout.childControlHeight = true;
        columnLayout.childForceExpandWidth = true;
        columnLayout.childForceExpandHeight = false;

        // Name + number on one line, each in its own font and colour.
        RectTransform nameRow = NewRect("Name Row", column);
        HorizontalLayoutGroup nameLayout = nameRow.gameObject.AddComponent<HorizontalLayoutGroup>();
        nameLayout.childAlignment = TextAnchor.LowerLeft;
        nameLayout.spacing = 20f;
        nameLayout.childControlWidth = true;
        nameLayout.childControlHeight = true;
        nameLayout.childForceExpandWidth = false;
        nameLayout.childForceExpandHeight = false;
        Text(nameRow, person.displayName, roll.nameFont, roll.nameSize, roll.nameColour, TextAlignmentOptions.BottomLeft, 0f);
        if (!string.IsNullOrWhiteSpace(person.idLine))
            Text(nameRow, person.idLine, roll.numberFont, roll.numberSize, roll.numberColour, TextAlignmentOptions.BottomLeft, 0f);

        float col = roll.textColumnWidth;
        if (!string.IsNullOrWhiteSpace(person.contribution))
            Text(column, person.contribution, roll.bodyFont, roll.bodySize, roll.contributionColour, TextAlignmentOptions.TopLeft, col);
        if (!string.IsNullOrWhiteSpace(person.note))
            Text(column, person.note, roll.bodyFont, roll.citationSize + 2f, roll.noteColour, TextAlignmentOptions.TopLeft, col);
        if (!string.IsNullOrWhiteSpace(person.citation))
            Text(column, person.citation, roll.bodyFont, roll.citationSize, roll.citationColour, TextAlignmentOptions.TopLeft, col);

        foreach (CreditLink link in person.links)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.url)) continue;
            BuildLink(column, link);
        }
    }


    private void BuildLink(RectTransform parent, CreditLink link)
    {
        RectTransform row = NewRect("Link", parent);
        HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.spacing = 12f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        if (link.icon != null)
            BuildFrame(row, roll.linkIconSize, link.icon, link.iconFit, link.iconBackground, 1f, Vector2.zero, "");

        string url = link.url.Trim().Replace("\"", "");
        TextMeshProUGUI text = Text(row, $"<link=\"{url}\"><u>{link.DisplayLabel}</u></link>",
                                    roll.linkFont != null ? roll.linkFont : roll.bodyFont,
                                    roll.linkSize, roll.linkColour, TextAlignmentOptions.MidlineLeft, 0f);
        text.raycastTarget = true;
        text.gameObject.AddComponent<CreditsLinkText>().Setup(roll.linkColour, roll.linkHoverColour);
    }


    /// <summary>Avatar inside a masked circle/hex with a border ring on top.</summary>
    private void BuildFrame(RectTransform parent, float size, Sprite sprite, AvatarFit fit,
                            Color background, float zoom, Vector2 offset, string initials)
    {
        RectTransform frame = NewRect("Frame", parent);
        LayoutElement le = frame.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = le.minWidth = size;
        le.preferredHeight = le.minHeight = size;

        float border = roll.borderThickness;

        // Mask: the filled shape, slightly inset so the ring covers its hard edge.
        // The mask Image must stay fully opaque: a transparent graphic is culled and
        // writes no stencil, which hides everything inside it. So the mask itself is
        // never shown, and the background colour is its own Image inside the mask.
        Sprite fill = CreditsShapes.Fill(roll.frameShape, border * 0.6f);
        Image maskImage = NewImage("Mask", frame, fill, Color.white);
        Stretch(maskImage.rectTransform);
        Mask mask = maskImage.gameObject.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        Color backColour = sprite == null && background.a <= 0.001f ? roll.initialsBackground : background;
        if (backColour.a > 0.001f)
        {
            Image back = NewImage("Background", maskImage.rectTransform, fill, backColour);
            Stretch(back.rectTransform);
        }

        if (sprite != null)
        {
            Image avatar = NewImage("Avatar", maskImage.rectTransform, sprite, Color.white);
            avatar.raycastTarget = false;
            RectTransform ar = avatar.rectTransform;
            ar.anchorMin = ar.anchorMax = new Vector2(0.5f, 0.5f);
            ar.pivot = new Vector2(0.5f, 0.5f);

            float w = Mathf.Max(1f, sprite.rect.width);
            float h = Mathf.Max(1f, sprite.rect.height);
            float scale = fit == AvatarFit.Cover ? Mathf.Max(size / w, size / h) : Mathf.Min(size / w, size / h);
            ar.sizeDelta = new Vector2(w * scale, h * scale) * zoom;
            ar.anchoredPosition = offset * size;
        }
        else if (!string.IsNullOrEmpty(initials))
        {
            TextMeshProUGUI t = Text(maskImage.rectTransform, initials, roll.nameFont, size * 0.34f,
                                     roll.BorderColour, TextAlignmentOptions.Center, 0f);
            Destroy(t.GetComponent<LayoutElement>());
            Stretch(t.rectTransform);
        }

        Image ring = NewImage("Border", frame, CreditsShapes.Ring(roll.frameShape, border), roll.BorderColour);
        Stretch(ring.rectTransform);
        ring.raycastTarget = false;
    }


    private void BuildBackButton()
    {
        RectTransform rect = NewRect("Back Button", root.transform);
        rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-40f, -40f);
        rect.sizeDelta = new Vector2(220f, 72f);

        Image image = rect.gameObject.AddComponent<Image>();
        Color border = roll.BorderColour;
        image.color = new Color(border.r, border.g, border.b, 0.22f);
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(Close);

        TextMeshProUGUI label = Text(rect, backLabel, roll.headingFont, 36f, border, TextAlignmentOptions.Center, 0f);
        Destroy(label.GetComponent<LayoutElement>());
        Stretch(label.rectTransform);

        if (roll.terminalStyle != null)
            roll.terminalStyle.ApplyButton(button, label, roll.terminalStyle.GetPalette(false), true);
    }


    // ------------------------------------------------------------------
    // Small UI helpers
    // ------------------------------------------------------------------

    private static RectTransform NewRect(string objectName, Transform parent)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }


    private static Image NewImage(string objectName, Transform parent, Sprite sprite, Color colour)
    {
        RectTransform rect = NewRect(objectName, parent);
        Image image = rect.gameObject.AddComponent<Image>();
        image.sprite = sprite;
        image.color = colour;
        image.raycastTarget = false;
        return image;
    }


    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }


    private static void Spacer(RectTransform parent, float height)
    {
        if (height <= 0f) return;
        RectTransform rect = NewRect("Spacer", parent);
        LayoutElement le = rect.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = le.minHeight = height;
    }


    private static TextMeshProUGUI Text(Transform parent, string value, TMP_FontAsset font, float size,
                                        Color colour, TextAlignmentOptions alignment, float width)
    {
        RectTransform rect = NewRect("Text", parent);
        TextMeshProUGUI text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) text.font = font;
        text.fontSize = size;
        text.color = colour;
        text.alignment = alignment;
        text.richText = true;
        text.raycastTarget = false;
        text.text = value ?? "";

        LayoutElement le = rect.gameObject.AddComponent<LayoutElement>();
        if (width > 0f) le.preferredWidth = width;
        return text;
    }
}
