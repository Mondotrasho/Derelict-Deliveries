using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Reusable terminal window: header (title + status), a ~3:1 banner, body text,
/// a result line and a column of choice buttons. It builds its own children at
/// runtime (like the dialogue panel does), so it only needs to sit on a
/// RectTransform under a Canvas with an EventSystem in the scene.
///
/// Used by EventPanel (event UI) now and by the planet picker later. It only
/// presents: it never pauses movement or writes game state.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public sealed class BannerChoiceView : MonoBehaviour
{
    /// <summary>One button row.</summary>
    public struct Choice
    {
        public string id;
        public string label;
        public bool enabled;

        public Choice(string id, string label, bool enabled = true)
        {
            this.id = id;
            this.label = label;
            this.enabled = enabled;
        }
    }

    [Header("Style")]
    [SerializeField] private TerminalStyle style;

    [Header("Placement")]
    [Tooltip("On: size and centre this window from its parent using the fractions below. Off: keep the RectTransform as you placed it.")]
    [SerializeField] private bool sizeFromParent = true;
    [Range(0.2f, 1f)] [SerializeField] private float widthFraction = 0.62f;
    [Range(0.2f, 1f)] [SerializeField] private float heightFraction = 0.82f;
    [SerializeField] private bool bringToFrontOnOpen = true;

    [Header("Layout")]
    [Tooltip("Banner width / height. The event banners are about 1916 x 648, i.e. ~2.96.")]
    [Min(0.5f)] [SerializeField] private float bannerAspect = 2.96f;
    [Tooltip("The banner never takes more than this share of the window height.")]
    [Range(0.1f, 0.7f)] [SerializeField] private float maxBannerHeightFraction = 0.42f;
    [Range(0f, 0.1f)] [SerializeField] private float innerInsetFraction = 0.025f;
    [Min(1f)] [SerializeField] private float buttonHeightScale = 1.5f;
    [Min(0f)] [SerializeField] private float spacing = 10f;

    [Header("Many Choices")]
    [Tooltip("More buttons than this switch to two columns.")]
    [Min(1)] [SerializeField] private int twoColumnsAbove = 4;
    [Tooltip("Buttons shrink to fit, but never below this height (pixels).")]
    [Min(8f)] [SerializeField] private float minButtonHeight = 30f;
    [Tooltip("Button labels shrink to fit, down to this size.")]
    [Min(6f)] [SerializeField] private float minButtonFontSize = 14f;
    [Tooltip("The banner shrinks before the buttons do, down to this share of its normal height.")]
    [Range(0f, 1f)] [SerializeField] private float minBannerFraction = 0.35f;
    [Tooltip("Body text always keeps at least this many lines.")]
    [Min(0)] [SerializeField] private int minBodyLines = 2;

    [Header("Open Animation (same feel as the dialogue panel)")]
    [SerializeField] private bool animateOpen = true;
    [Min(0.05f)] [SerializeField] private float openDuration = 0.24f;
    [SerializeField] private Vector2 openStartScale = new Vector2(0.94f, 0.06f);

    public bool IsOpen { get; private set; }

    private RectTransform rect;
    private RectTransform generatedRoot;
    private CanvasGroup canvasGroup;
    private Image panelImage;
    private Image topBorder, bottomBorder, leftBorder, rightBorder, divider;
    private RawImage scanlineImage;
    private Texture2D scanlineTexture;
    private RectTransform contentRect;
    private RectTransform headerRect;
    private TextMeshProUGUI titleText, statusText, bodyText, resultText;
    private Image bannerImage;
    private LayoutElement bannerLayout, headerLayout, dividerLayout, bodyLayout;
    private GridLayoutGroup choiceGrid;
    private RectTransform choiceArea;

    private readonly List<Button> buttons = new List<Button>();
    private readonly List<TextMeshProUGUI> buttonLabels = new List<TextMeshProUGUI>();
    private readonly List<string> buttonIds = new List<string>();
    private readonly List<bool> buttonEnabled = new List<bool>();

    private TerminalStyle.Palette palette;
    private bool built;
    private bool choiceMade;
    private string chosenId;
    private Vector2 lastSize = new Vector2(-1f, -1f);
    private Coroutine openRoutine;


    private void Awake()
    {
        EnsureBuilt();
        SetVisible(false);
    }


    private void OnDestroy()
    {
        if (scanlineTexture != null) Destroy(scanlineTexture);
    }


    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>Open (or refresh) the window. Null banner collapses the banner area.</summary>
    public void Show(Sprite banner, string title, string status, string body, bool alert)
    {
        EnsureBuilt();
        palette = style != null ? style.GetPalette(alert) : DefaultPalette();

        bannerImage.sprite = banner;
        bannerImage.gameObject.SetActive(banner != null);
        titleText.text = title ?? "";
        statusText.text = status ?? "";
        bodyText.text = body ?? "";
        bodyText.gameObject.SetActive(!string.IsNullOrEmpty(body));
        ShowResult(null);
        ClearChoices();

        ApplyStyle();

        bool wasOpen = IsOpen;
        SetVisible(true);
        if (bringToFrontOnOpen) transform.SetAsLastSibling();
        lastSize = new Vector2(-1f, -1f);   // force a layout pass
        if (!wasOpen) PlayOpen();
    }


    /// <summary>Show the window again after Hide (e.g. back from a dialogue) without changing its content.</summary>
    public void ShowAgain()
    {
        if (!built) return;
        SetVisible(true);
        if (bringToFrontOnOpen) transform.SetAsLastSibling();
        PlayOpen();
    }


    public void SetChoices(IReadOnlyList<Choice> choices)
    {
        EnsureBuilt();
        ClearChoices();
        if (choices == null) return;

        for (int i = 0; i < choices.Count; i++)
        {
            Button button = GetButton(i);
            buttonIds[i] = choices[i].id;
            buttonEnabled[i] = choices[i].enabled;
            buttonLabels[i].text = choices[i].label ?? "";
            button.gameObject.SetActive(true);
            if (style != null) style.ApplyButton(button, buttonLabels[i], palette, choices[i].enabled);
            else button.interactable = choices[i].enabled;
            FitLabel(buttonLabels[i]);
        }

        choiceMade = false;
        chosenId = null;
        lastSize = new Vector2(-1f, -1f);
    }


    /// <summary>Result line under the body. Null or empty hides it.</summary>
    public void ShowResult(string text)
    {
        EnsureBuilt();
        resultText.text = text ?? "";
        resultText.gameObject.SetActive(!string.IsNullOrEmpty(text));
        lastSize = new Vector2(-1f, -1f);
    }


    /// <summary>Yields until a button is clicked, then reports its id. Hide() or ForceClose() report null.</summary>
    public IEnumerator WaitForChoice(Action<string> picked)
    {
        choiceMade = false;
        chosenId = null;

        while (!choiceMade)
        {
            if (!IsOpen)
            {
                picked?.Invoke(null);
                yield break;
            }
            yield return null;
        }

        picked?.Invoke(chosenId);
    }


    public void Hide()
    {
        if (!built) return;
        StopOpen();
        SetVisible(false);
    }


    // ------------------------------------------------------------------
    // Build
    // ------------------------------------------------------------------

    private void EnsureBuilt()
    {
        if (built) return;
        built = true;

        rect = (RectTransform)transform;
        if (sizeFromParent)
        {
            rect.anchorMin = new Vector2(0.5f - widthFraction * 0.5f, 0.5f - heightFraction * 0.5f);
            rect.anchorMax = new Vector2(0.5f + widthFraction * 0.5f, 0.5f + heightFraction * 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        Transform old = transform.Find("__EventWindowGenerated");
        if (old != null) Destroy(old.gameObject);

        generatedRoot = CreateRect("__EventWindowGenerated", transform);
        Stretch(generatedRoot);
        canvasGroup = generatedRoot.gameObject.AddComponent<CanvasGroup>();

        panelImage = generatedRoot.gameObject.AddComponent<Image>();
        panelImage.raycastTarget = true;   // blocks clicks through to the map

        contentRect = CreateRect("Content", generatedRoot);
        Stretch(contentRect);
        VerticalLayoutGroup column = contentRect.gameObject.AddComponent<VerticalLayoutGroup>();
        column.childControlWidth = true;
        column.childControlHeight = true;
        column.childForceExpandWidth = true;
        column.childForceExpandHeight = false;
        column.spacing = spacing;

        headerRect = CreateRect("Header", contentRect);
        headerLayout = headerRect.gameObject.AddComponent<LayoutElement>();
        titleText = CreateText("Title", headerRect);
        titleText.alignment = TextAlignmentOptions.MidlineLeft;
        titleText.rectTransform.anchorMin = new Vector2(0f, 0f);
        titleText.rectTransform.anchorMax = new Vector2(0.68f, 1f);
        titleText.rectTransform.offsetMin = Vector2.zero;
        titleText.rectTransform.offsetMax = Vector2.zero;
        titleText.textWrappingMode = TextWrappingModes.NoWrap;
        titleText.overflowMode = TextOverflowModes.Ellipsis;
        statusText = CreateText("Status", headerRect);
        statusText.alignment = TextAlignmentOptions.MidlineRight;
        statusText.rectTransform.anchorMin = new Vector2(0.68f, 0f);
        statusText.rectTransform.anchorMax = new Vector2(1f, 1f);
        statusText.rectTransform.offsetMin = Vector2.zero;
        statusText.rectTransform.offsetMax = Vector2.zero;
        statusText.textWrappingMode = TextWrappingModes.NoWrap;
        statusText.overflowMode = TextOverflowModes.Ellipsis;

        divider = CreateImage("HeaderDivider", contentRect);
        dividerLayout = divider.gameObject.AddComponent<LayoutElement>();

        bannerImage = CreateImage("Banner", contentRect);
        bannerImage.preserveAspect = true;
        bannerLayout = bannerImage.gameObject.AddComponent<LayoutElement>();

        bodyText = CreateText("Body", contentRect);
        bodyText.alignment = TextAlignmentOptions.TopLeft;
        bodyText.textWrappingMode = TextWrappingModes.Normal;
        bodyText.overflowMode = TextOverflowModes.Ellipsis;
        bodyLayout = bodyText.gameObject.AddComponent<LayoutElement>();
        bodyLayout.flexibleHeight = 1f;          // body takes whatever is left, so the choices never get pushed off
        bodyLayout.minHeight = 20f;
        bodyLayout.preferredHeight = 0f;         // overrides TMP's own preferred height (long text ellipsises)

        resultText = CreateText("Result", contentRect);
        resultText.alignment = TextAlignmentOptions.TopLeft;
        resultText.textWrappingMode = TextWrappingModes.Normal;

        choiceArea = CreateRect("Choices", contentRect);
        // A grid, so many choices can go into two columns. Its preferred height
        // is reported to the column; Relayout sizes the cells to fit.
        choiceGrid = choiceArea.gameObject.AddComponent<GridLayoutGroup>();
        choiceGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        choiceGrid.constraintCount = 1;
        choiceGrid.childAlignment = TextAnchor.UpperCenter;
        choiceGrid.spacing = new Vector2(spacing * 0.6f, spacing * 0.6f);

        topBorder = CreateImage("TopBorder", generatedRoot);
        bottomBorder = CreateImage("BottomBorder", generatedRoot);
        leftBorder = CreateImage("LeftBorder", generatedRoot);
        rightBorder = CreateImage("RightBorder", generatedRoot);

        scanlineImage = new GameObject("Scanlines", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        scanlineImage.transform.SetParent(generatedRoot, false);
        scanlineImage.raycastTarget = false;
        Stretch(scanlineImage.rectTransform);
        RebuildScanlineTexture();

        palette = style != null ? style.GetPalette(false) : DefaultPalette();
        ApplyStyle();
    }


    private void ApplyStyle()
    {
        if (style != null) style.ApplyPanel(panelImage, palette);
        else panelImage.color = palette.background;

        float header = style != null ? style.HeaderFontSize : 20f;
        float body = style != null ? style.BodyFontSize : 28f;
        ApplyText(titleText, palette.primary, header);
        ApplyText(statusText, palette.dim, header);
        ApplyText(bodyText, palette.primary, body);
        ApplyText(resultText, palette.primary, body);

        topBorder.color = palette.primary;
        bottomBorder.color = palette.primary;
        leftBorder.color = palette.primary;
        rightBorder.color = palette.primary;
        divider.color = palette.dim;

        scanlineImage.gameObject.SetActive(style == null || style.Scanlines);
        scanlineImage.transform.SetAsLastSibling();

        for (int i = 0; i < buttons.Count; i++)
        {
            if (!buttons[i].gameObject.activeSelf) continue;
            if (style != null) style.ApplyButton(buttons[i], buttonLabels[i], palette, buttonEnabled[i]);
            FitLabel(buttonLabels[i]);
        }
    }


    /// <summary>Labels shrink to fit smaller buttons, up to the style's button size.</summary>
    private void FitLabel(TextMeshProUGUI label)
    {
        label.enableAutoSizing = true;
        label.fontSizeMax = style != null ? style.ButtonFontSize : 42f;
        label.fontSizeMin = Mathf.Min(minButtonFontSize, label.fontSizeMax);
        label.margin = new Vector4(8f, 2f, 8f, 2f);
    }


    private void ApplyText(TextMeshProUGUI text, Color colour, float size)
    {
        if (style != null) style.ApplyText(text, colour, size);
        else
        {
            text.color = colour;
            text.fontSize = size;
        }
    }


    // ------------------------------------------------------------------
    // Layout (sizes depend on the window size, so recalculated when it changes)
    // ------------------------------------------------------------------

    private void LateUpdate()
    {
        if (!IsOpen || rect == null) return;
        Vector2 size = rect.rect.size;
        if (size == lastSize) return;
        lastSize = size;
        Relayout(size);
    }


    private void Relayout(Vector2 size)
    {
        float border = Mathf.Max(1f, size.y * 0.004f);
        float inset = Mathf.Max(border * 4f, size.x * innerInsetFraction);
        float headerSize = style != null ? style.HeaderFontSize : 20f;
        float buttonSize = style != null ? style.ButtonFontSize : 42f;

        SetEdge(topBorder.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), border, true);
        SetEdge(bottomBorder.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), border, true);
        SetEdge(leftBorder.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), border, false);
        SetEdge(rightBorder.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 1f), border, false);

        VerticalLayoutGroup column = contentRect.GetComponent<VerticalLayoutGroup>();
        int pad = Mathf.RoundToInt(inset);
        column.padding = new RectOffset(pad, pad, Mathf.RoundToInt(border), pad);

        float headerHeight = Mathf.Max(headerSize * 1.8f, size.y * 0.075f);
        headerLayout.minHeight = headerHeight;
        headerLayout.preferredHeight = headerHeight;
        dividerLayout.minHeight = border;
        dividerLayout.preferredHeight = border;

        float contentWidth = Mathf.Max(1f, size.x - inset * 2f);

        // --- fit: choices first, then the banner gives way, then buttons shrink ---
        int count = 0;
        foreach (Button b in buttons) if (b.gameObject.activeSelf) count++;
        int columns = count > twoColumnsAbove ? 2 : 1;
        int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)columns));
        float gap = spacing * 0.6f;

        float bodySize = style != null ? style.BodyFontSize : 28f;
        float minBody = bodyText.gameObject.activeSelf ? bodySize * 1.25f * minBodyLines : 0f;
        float resultHeight = resultText.gameObject.activeSelf ? resultText.preferredHeight : 0f;
        int visibleChildren = 0;
        foreach (Transform child in contentRect) if (child.gameObject.activeSelf) visibleChildren++;
        float fixedHeight = column.padding.top + column.padding.bottom + headerHeight + border
                            + spacing * Mathf.Max(0, visibleChildren - 1) + resultHeight + minBody;
        float available = Mathf.Max(0f, size.y - fixedHeight);

        float idealButton = buttonSize * buttonHeightScale;
        float idealChoices = count > 0 ? rows * idealButton + (rows - 1) * gap : 0f;
        float idealBanner = bannerImage.gameObject.activeSelf
            ? Mathf.Min(contentWidth / Mathf.Max(0.5f, bannerAspect), size.y * maxBannerHeightFraction)
            : 0f;

        float bannerHeight = Mathf.Clamp(available - idealChoices, idealBanner * minBannerFraction, idealBanner);
        float buttonHeight = count > 0
            ? Mathf.Clamp((available - bannerHeight - (rows - 1) * gap) / rows, minButtonHeight, idealButton)
            : idealButton;

        bannerLayout.minHeight = bannerHeight;
        bannerLayout.preferredHeight = bannerHeight;
        bodyLayout.minHeight = Mathf.Max(20f, minBody);

        choiceGrid.constraintCount = columns;
        choiceGrid.spacing = new Vector2(gap, gap);
        choiceGrid.cellSize = new Vector2(Mathf.Max(1f, (contentWidth - (columns - 1) * gap) / columns), buttonHeight);

        if (scanlineImage != null && style != null)
        {
            float period = Mathf.Max(0.5f, style.ScanlineThickness + style.ScanlineGap);
            scanlineImage.uvRect = new Rect(0f, 0f, 1f, size.y / period);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
    }


    private static void SetEdge(RectTransform edge, Vector2 anchorMin, Vector2 anchorMax, float thickness, bool horizontal)
    {
        edge.anchorMin = anchorMin;
        edge.anchorMax = anchorMax;
        edge.pivot = new Vector2(anchorMin.x, anchorMin.y);
        edge.anchoredPosition = Vector2.zero;
        edge.sizeDelta = horizontal ? new Vector2(0f, thickness) : new Vector2(thickness, 0f);
    }


    // ------------------------------------------------------------------
    // Buttons
    // ------------------------------------------------------------------

    private Button GetButton(int index)
    {
        while (buttons.Count <= index)
        {
            int slot = buttons.Count;
            GameObject obj = new GameObject("Choice " + slot, typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            obj.transform.SetParent(choiceArea, false);

            Image image = obj.GetComponent<Image>();
            image.raycastTarget = true;
            Button button = obj.GetComponent<Button>();
            button.targetGraphic = image;

            TextMeshProUGUI label = CreateText("Label", obj.transform);
            Stretch(label.rectTransform);
            label.alignment = TextAlignmentOptions.Center;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Truncate;

            button.onClick.AddListener(() => OnButton(slot));

            buttons.Add(button);
            buttonLabels.Add(label);
            buttonIds.Add(null);
            buttonEnabled.Add(true);
        }

        return buttons[index];
    }


    private void OnButton(int slot)
    {
        if (!IsOpen || choiceMade || slot >= buttonIds.Count || !buttonEnabled[slot]) return;
        chosenId = buttonIds[slot];
        choiceMade = true;
    }


    private void ClearChoices()
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            buttons[i].gameObject.SetActive(false);
            buttonIds[i] = null;
            buttonEnabled[i] = false;
        }
    }


    // ------------------------------------------------------------------
    // Visibility and open animation
    // ------------------------------------------------------------------

    private void SetVisible(bool visible)
    {
        IsOpen = visible;
        if (generatedRoot != null) generatedRoot.gameObject.SetActive(visible);
        if (canvasGroup != null)
        {
            canvasGroup.alpha = visible ? 1f : 0f;
            canvasGroup.interactable = visible;
            canvasGroup.blocksRaycasts = visible;
        }
    }


    private void PlayOpen()
    {
        StopOpen();
        if (!animateOpen || !isActiveAndEnabled) return;
        openRoutine = StartCoroutine(OpenAnimation());
    }


    private void StopOpen()
    {
        if (openRoutine != null) StopCoroutine(openRoutine);
        openRoutine = null;
        if (generatedRoot != null) generatedRoot.localScale = Vector3.one;
        if (canvasGroup != null && IsOpen)
        {
            canvasGroup.alpha = 1f;
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;
        }
    }


    private IEnumerator OpenAnimation()
    {
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;

        float elapsed = 0f;
        while (elapsed < openDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / openDuration);
            float eased = 1f - (1f - t) * (1f - t);
            generatedRoot.localScale = new Vector3(
                Mathf.Lerp(openStartScale.x, 1f, eased),
                Mathf.Lerp(openStartScale.y, 1f, eased),
                1f);
            canvasGroup.alpha = eased;
            yield return null;
        }

        openRoutine = null;
        StopOpen();
    }


    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private void RebuildScanlineTexture()
    {
        if (scanlineTexture != null) Destroy(scanlineTexture);

        float thickness = style != null ? style.ScanlineThickness : 8f;
        float gap = style != null ? style.ScanlineGap : 8f;
        float opacity = style != null ? style.ScanlineOpacity : 0.313f;

        const int height = 64;
        scanlineTexture = new Texture2D(1, height, TextureFormat.RGBA32, false);
        scanlineTexture.wrapMode = TextureWrapMode.Repeat;
        scanlineTexture.filterMode = FilterMode.Bilinear;

        float period = Mathf.Max(0.5f, thickness + gap);
        float darkFraction = Mathf.Clamp01(thickness / period);
        for (int y = 0; y < height; y++)
        {
            float normalized = (y + 0.5f) / height;
            scanlineTexture.SetPixel(0, y, new Color(0f, 0f, 0f, normalized <= darkFraction ? opacity : 0f));
        }

        scanlineTexture.Apply();
        scanlineImage.texture = scanlineTexture;
        scanlineImage.color = Color.white;
    }


    private static TerminalStyle.Palette DefaultPalette()
    {
        return new TerminalStyle.Palette
        {
            background = new Color32(0x00, 0x10, 0x08, 0xFF),
            primary = new Color32(0x42, 0xFF, 0x7B, 0xFF),
            dim = new Color32(0x18, 0x7A, 0x3D, 0xFF)
        };
    }


    private static RectTransform CreateRect(string name, Transform parent)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        return (RectTransform)obj.transform;
    }


    private static TextMeshProUGUI CreateText(string name, Transform parent)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        obj.transform.SetParent(parent, false);
        TextMeshProUGUI text = obj.GetComponent<TextMeshProUGUI>();
        text.raycastTarget = false;
        return text;
    }


    private static Image CreateImage(string name, Transform parent)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform), typeof(Image));
        obj.transform.SetParent(parent, false);
        Image image = obj.GetComponent<Image>();
        image.raycastTarget = false;
        return image;
    }


    private static void Stretch(RectTransform target)
    {
        target.anchorMin = Vector2.zero;
        target.anchorMax = Vector2.one;
        target.offsetMin = Vector2.zero;
        target.offsetMax = Vector2.zero;
    }
}
