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
    [Tooltip("Fallback banner width / height used only when the assigned sprite has no usable dimensions.")]
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
    [Tooltip("On result screens, protect more of the banner height before shrinking it. This keeps the artwork stable when result text appears.")]
    [Range(0f, 1f)] [SerializeField] private float minResultBannerFraction = 0.78f;
    [Tooltip("Body text always keeps at least this many lines.")]
    [Min(0)] [SerializeField] private int minBodyLines = 2;

    [Header("Open Animation (same feel as the dialogue panel)")]
    [SerializeField] private bool animateOpen = true;
    [Min(0.05f)] [SerializeField] private float openDuration = 0.24f;
    [SerializeField] private Vector2 openStartScale = new Vector2(0.94f, 0.06f);

    [Header("Banner CRT Swap")]
    [Tooltip("Total time for the banner flicker, redraw and settle effect.")]
    [Min(0.05f)] [SerializeField] private float bannerSwapDuration = 0.42f;
    [Tooltip("Number of irregular old-image flickers before the redraw starts. Set to 0 to skip the flicker stage.")]
    [Min(0)] [SerializeField] private int bannerSwapFlickerCount = 4;
    [Tooltip("Reveal the new banner from top to bottom behind a CRT scan bar. Off switches after the flicker and only plays the settle stage.")]
    [SerializeField] private bool bannerSwapWipe = true;
    [Tooltip("Multiplied by the terminal palette primary colour for the redraw scan bar.")]
    [SerializeField] private Color bannerSwapScanBarColour = new Color(1f, 1f, 1f, 0.85f);
    [Tooltip("Maximum horizontal CRT glitch offset, in UI pixels, during a few flicker frames.")]
    [Min(0f)] [SerializeField] private float bannerSwapJitter = 2f;

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
    private RectTransform bannerSwapRoot;
    private Image bannerSwapOldImage, bannerSwapNewImage, bannerSwapScanBar;
    private RawImage bannerSwapScanlines;
    private Texture2D bannerSwapScanlineTexture;
    private LayoutElement bannerLayout, headerLayout, dividerLayout, bodyLayout;
    private GridLayoutGroup choiceGrid;
    private RectTransform choiceArea;

    // Optional custom rows (SetChoiceRows). Null = the normal one/two-column grid.
    private LayoutElement choiceAreaLayout;
    private int[] rowSizes;
    private string[] rowLabels;
    private TextAlignmentOptions rowLabelAlignment = TextAlignmentOptions.Center;
    private bool spreadRows;
    private readonly List<TextMeshProUGUI> rowLabelTexts = new List<TextMeshProUGUI>();
    private LayoutElement resultLayout;

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
    private Coroutine bannerSwapRoutine;
    private Sprite bannerSwapTarget;


    private void Awake()
    {
        EnsureBuilt();
        SetVisible(false);
    }


    private void OnDestroy()
    {
        if (bannerSwapRoutine != null) StopCoroutine(bannerSwapRoutine);
        if (scanlineTexture != null) Destroy(scanlineTexture);
        if (bannerSwapScanlineTexture != null) Destroy(bannerSwapScanlineTexture);
    }


    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>Open (or refresh) the window. Null banner collapses the banner area.</summary>
    public void Show(Sprite banner, string title, string status, string body, bool alert)
    {
        EnsureBuilt();
        StopBannerSwap(true);
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


    /// <summary>Swap just the banner image, leaving everything else as it is.</summary>
    public void SetBanner(Sprite banner)
    {
        if (!built || banner == null) return;

        // A second result can arrive while the first redraw is still running. Commit that
        // redraw first so transitions never stack and the previous target becomes the old image.
        StopBannerSwap(true);

        Sprite oldBanner = bannerImage.sprite;
        if (oldBanner == null || oldBanner == banner || !IsOpen || !isActiveAndEnabled)
        {
            SetBannerImmediate(banner);
            return;
        }

        bannerSwapTarget = banner;

        // The layout should size itself for the final sprite immediately, even though the base
        // Image stays hidden until the transition finishes. The two temporary Images do the draw.
        bannerImage.sprite = banner;
        bannerImage.enabled = false;
        bannerImage.gameObject.SetActive(true);

        bannerSwapOldImage.sprite = oldBanner;
        bannerSwapOldImage.color = Color.white;
        bannerSwapOldImage.gameObject.SetActive(true);

        bannerSwapNewImage.sprite = banner;
        bannerSwapNewImage.color = Color.white;
        bannerSwapNewImage.fillAmount = bannerSwapWipe ? 0f : 1f;
        bannerSwapNewImage.gameObject.SetActive(true);

        bannerSwapRoot.localPosition = Vector3.zero;
        bannerSwapRoot.gameObject.SetActive(true);
        bannerSwapScanlines.gameObject.SetActive(true);
        bannerSwapScanBar.gameObject.SetActive(false);
        UpdateBannerSwapScanlineUV();

        lastSize = new Vector2(-1f, -1f);
        bannerSwapRoutine = StartCoroutine(BannerSwapAnimation());
    }


    /// <summary>Show the window again after Hide (e.g. back from a dialogue) without changing its content.</summary>
    public void ShowAgain()
    {
        if (!built) return;
        StopBannerSwap(true);
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


    /// <summary>
    /// Optional: lay the current buttons out in custom rows instead of the grid.
    /// Call AFTER SetChoices (SetChoices / Show reset it back to the grid).
    /// buttonsPerRow {1, 2, 1} = one full-width button, then two side by side,
    /// then one. labelAboveRow (optional, same length) puts a line of text above
    /// a row - null or empty for none. Buttons fill the rows in SetChoices order.
    /// </summary>
    /// labelAlignment = how the row labels line up (MidlineLeft for left-aligned).
    /// spread = true pushes the rows apart to use the window's full height.
    public void SetChoiceRows(IReadOnlyList<int> buttonsPerRow, IReadOnlyList<string> labelAboveRow = null,
                              TextAlignmentOptions labelAlignment = TextAlignmentOptions.Center, bool spread = false)
    {
        EnsureBuilt();
        rowLabelAlignment = labelAlignment;
        spreadRows = spread;
        if (buttonsPerRow == null || buttonsPerRow.Count == 0)
        {
            rowSizes = null;
            rowLabels = null;
        }
        else
        {
            rowSizes = new int[buttonsPerRow.Count];
            rowLabels = new string[buttonsPerRow.Count];
            for (int i = 0; i < rowSizes.Length; i++)
            {
                rowSizes[i] = Mathf.Max(1, buttonsPerRow[i]);
                rowLabels[i] = labelAboveRow != null && i < labelAboveRow.Count ? labelAboveRow[i] : null;
            }
        }
        lastSize = new Vector2(-1f, -1f);   // relayout next frame
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
        StopBannerSwap(true);
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
        BuildBannerSwapLayers();

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
        resultLayout = resultText.gameObject.AddComponent<LayoutElement>();
        resultLayout.flexibleHeight = 0f;

        choiceArea = CreateRect("Choices", contentRect);
        // A grid, so many choices can go into two columns. Its preferred height
        // is reported to the column; Relayout sizes the cells to fit.
        choiceGrid = choiceArea.gameObject.AddComponent<GridLayoutGroup>();
        choiceGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        choiceGrid.constraintCount = 1;
        choiceGrid.childAlignment = TextAnchor.UpperCenter;
        choiceGrid.spacing = new Vector2(spacing * 0.6f, spacing * 0.6f);
        choiceAreaLayout = choiceArea.gameObject.AddComponent<LayoutElement>();   // unset (-1) = the grid reports its own size

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

        if (bannerSwapScanBar != null)
        {
            bannerSwapScanBar.color = new Color(
                palette.primary.r * bannerSwapScanBarColour.r,
                palette.primary.g * bannerSwapScanBarColour.g,
                palette.primary.b * bannerSwapScanBarColour.b,
                palette.primary.a * bannerSwapScanBarColour.a);
        }

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

        // --- fit: normal screens may give banner space to choices; result screens protect it ---
        int count = 0;
        foreach (Button b in buttons) if (b.gameObject.activeSelf) count++;
        int columns = count > twoColumnsAbove ? 2 : 1;
        int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)columns));
        float gap = spacing * 0.6f;

        float bodySize = style != null ? style.BodyFontSize : 28f;

        // Custom rows (SetChoiceRows): row count comes from the layout, and any
        // row labels take fixed height before the buttons are sized.
        bool customRows = rowSizes != null && count > 0;
        float rowLabelHeight = bodySize * 1.4f;
        float rowLabelsTotal = 0f;
        if (customRows)
        {
            rows = rowSizes.Length;
            foreach (string label in rowLabels)
                if (!string.IsNullOrEmpty(label)) rowLabelsTotal += rowLabelHeight + gap;
        }
        float minBody = bodyText.gameObject.activeSelf ? bodySize * 1.25f * minBodyLines : 0f;
        bool showingResult = resultText.gameObject.activeSelf;
        // Do not use resultText.preferredHeight here. On the first result shown in a
        // freshly-opened panel, the Result RectTransform can still have its old/inactive
        // width for this frame. TMP then measures against that stale width and can report
        // an enormous height, which makes the banner collapse until another interaction
        // happens to force a second layout pass. Measure against the width we are about to
        // give the result instead, so first-result and later-result layouts are identical.
        float resultHeight = 0f;
        if (showingResult)
        {
            resultHeight = resultText.GetPreferredValues(resultText.text, contentWidth, Mathf.Infinity).y;
            resultHeight = Mathf.Max(0f, resultHeight);
        }

        if (resultLayout != null)
        {
            resultLayout.minHeight = resultHeight;
            resultLayout.preferredHeight = resultHeight;
        }

        int visibleChildren = 0;
        foreach (Transform child in contentRect) if (child.gameObject.activeSelf) visibleChildren++;
        float fixedHeight = column.padding.top + column.padding.bottom + headerHeight + border
                            + spacing * Mathf.Max(0, visibleChildren - 1) + resultHeight + minBody;
        fixedHeight += rowLabelsTotal;
        float available = Mathf.Max(0f, size.y - fixedHeight);

        float idealButton = buttonSize * buttonHeightScale;
        float idealChoices = count > 0 ? rows * idealButton + (rows - 1) * gap : 0f;
        float actualBannerAspect = GetBannerAspect();
        float idealBanner = bannerImage.gameObject.activeSelf
            ? Mathf.Min(contentWidth / actualBannerAspect, size.y * maxBannerHeightFraction)
            : 0f;

        float bannerHeight;
        float buttonHeight;

        if (showingResult && idealBanner > 0f)
        {
            // Results normally replace many choices with one Continue button. Shrink that
            // button first and keep most of the artwork at the same height as the event card.
            float protectedFraction = Mathf.Max(minBannerFraction, minResultBannerFraction);
            float protectedBanner = idealBanner * protectedFraction;
            float choiceGaps = count > 0 ? (rows - 1) * gap : 0f;
            float roomForButtons = available - protectedBanner - choiceGaps;

            if (count == 0)
            {
                bannerHeight = Mathf.Clamp(available, 0f, idealBanner);
                buttonHeight = idealButton;
            }
            else if (roomForButtons >= rows * minButtonHeight)
            {
                buttonHeight = Mathf.Clamp(roomForButtons / rows, minButtonHeight, idealButton);
                float usedChoices = rows * buttonHeight + choiceGaps;
                bannerHeight = Mathf.Clamp(available - usedChoices, protectedBanner, idealBanner);
            }
            else
            {
                // Extremely small windows still have to fit. Keep buttons usable and only
                // let the banner fall below the protected result height when there is no room.
                buttonHeight = minButtonHeight;
                float usedChoices = rows * buttonHeight + choiceGaps;
                bannerHeight = Mathf.Clamp(available - usedChoices, 0f, idealBanner);
            }
        }
        else
        {
            bannerHeight = Mathf.Clamp(available - idealChoices, idealBanner * minBannerFraction, idealBanner);
            buttonHeight = count > 0
                ? Mathf.Clamp((available - bannerHeight - (rows - 1) * gap) / rows, minButtonHeight, idealButton)
                : idealButton;
        }

        bannerLayout.minHeight = bannerHeight;
        bannerLayout.preferredHeight = bannerHeight;
        bodyLayout.minHeight = Mathf.Max(20f, minBody);

        if (customRows)
        {
            choiceGrid.enabled = false;
            // Spread: the space the grid would otherwise leave empty goes between the rows.
            float natural = rowLabelsTotal + rows * buttonHeight + (rows - 1) * gap;
            float room = available + rowLabelsTotal - bannerLayout.preferredHeight;
            float rowGap = gap;
            if (spreadRows && rows > 1 && room > natural)
                rowGap = gap + (room - natural) / (rows - 1);

            float rowsHeight = LayoutCustomRows(contentWidth, buttonHeight, gap, rowGap, rowLabelHeight);
            choiceAreaLayout.minHeight = rowsHeight;
            choiceAreaLayout.preferredHeight = rowsHeight;
        }
        else
        {
            choiceGrid.enabled = true;
            choiceAreaLayout.minHeight = -1f;
            choiceAreaLayout.preferredHeight = -1f;
            foreach (TextMeshProUGUI t in rowLabelTexts) t.gameObject.SetActive(false);

            choiceGrid.constraintCount = columns;
            choiceGrid.spacing = new Vector2(gap, gap);
            choiceGrid.cellSize = new Vector2(Mathf.Max(1f, (contentWidth - (columns - 1) * gap) / columns), buttonHeight);
        }

        if (scanlineImage != null && style != null)
        {
            float period = Mathf.Max(0.5f, style.ScanlineThickness + style.ScanlineGap);
            scanlineImage.uvRect = new Rect(0f, 0f, 1f, size.y / period);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
        UpdateBannerSwapScanlineUV();
    }


    /// <summary>Places the active buttons (and row labels) by hand. Returns the total height.</summary>
    private float LayoutCustomRows(float width, float buttonHeight, float gap, float rowGap, float labelHeight)
    {
        float body = style != null ? style.BodyFontSize : 28f;
        float y = 0f;
        int buttonIndex = 0;
        int labelIndex = 0;

        for (int r = 0; r < rowSizes.Length; r++)
        {
            if (!string.IsNullOrEmpty(rowLabels[r]))
            {
                TextMeshProUGUI text = GetRowLabel(labelIndex++);
                text.gameObject.SetActive(true);
                text.text = rowLabels[r];
                text.alignment = rowLabelAlignment;
                ApplyText(text, palette.primary, body);
                PlaceTopLeft(text.rectTransform, 0f, y, width, labelHeight);
                y += labelHeight + gap;
            }

            int perRow = rowSizes[r];
            float cellWidth = Mathf.Max(1f, (width - (perRow - 1) * gap) / perRow);
            for (int k = 0; k < perRow; k++)
            {
                Button button = NextActiveButton(ref buttonIndex);
                if (button == null) break;
                PlaceTopLeft((RectTransform)button.transform, k * (cellWidth + gap), y, cellWidth, buttonHeight);
            }
            y += buttonHeight + (r < rowSizes.Length - 1 ? rowGap : gap);
        }

        // Any buttons beyond the described rows get a full-width row each.
        Button extra;
        while ((extra = NextActiveButton(ref buttonIndex)) != null)
        {
            PlaceTopLeft((RectTransform)extra.transform, 0f, y, width, buttonHeight);
            y += buttonHeight + gap;
        }

        for (int i = labelIndex; i < rowLabelTexts.Count; i++) rowLabelTexts[i].gameObject.SetActive(false);
        return Mathf.Max(0f, y - gap);
    }


    private Button NextActiveButton(ref int index)
    {
        while (index < buttons.Count)
        {
            Button b = buttons[index++];
            if (b.gameObject.activeSelf) return b;
        }
        return null;
    }


    private TextMeshProUGUI GetRowLabel(int index)
    {
        while (rowLabelTexts.Count <= index)
        {
            TextMeshProUGUI text = CreateText("Row Label " + rowLabelTexts.Count, choiceArea);
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.raycastTarget = false;
            text.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            rowLabelTexts.Add(text);
        }
        return rowLabelTexts[index];
    }


    private static void PlaceTopLeft(RectTransform rt, float x, float y, float width, float height)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(width, height);
        rt.anchoredPosition = new Vector2(x, -y);
    }


    /// <summary>Use the assigned sprite's real aspect ratio so differently-shaped banners do not get an incorrectly-sized slot.</summary>
    private float GetBannerAspect()
    {
        Sprite sprite = bannerImage != null ? bannerImage.sprite : null;
        if (sprite != null && sprite.rect.height > 0.01f)
        {
            return Mathf.Max(0.5f, sprite.rect.width / sprite.rect.height);
        }

        return Mathf.Max(0.5f, bannerAspect);
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

        rowSizes = null;    // back to the normal grid until SetChoiceRows is called again
        rowLabels = null;
        rowLabelAlignment = TextAlignmentOptions.Center;
        spreadRows = false;
    }


    // ------------------------------------------------------------------
    // Banner CRT swap
    // ------------------------------------------------------------------

    private void BuildBannerSwapLayers()
    {
        // bannerImage remains the layout-owned final image. During a transition it is disabled
        // while these children draw snapshots of the old and new banners over the same rect.
        bannerImage.gameObject.AddComponent<RectMask2D>();

        bannerSwapRoot = CreateRect("BannerSwap", bannerImage.transform);
        Stretch(bannerSwapRoot);
        bannerSwapRoot.gameObject.SetActive(false);

        bannerSwapOldImage = CreateImage("OldBanner", bannerSwapRoot);
        Stretch(bannerSwapOldImage.rectTransform);
        bannerSwapOldImage.preserveAspect = true;

        bannerSwapNewImage = CreateImage("NewBanner", bannerSwapRoot);
        Stretch(bannerSwapNewImage.rectTransform);
        bannerSwapNewImage.preserveAspect = true;
        bannerSwapNewImage.type = Image.Type.Filled;
        bannerSwapNewImage.fillMethod = Image.FillMethod.Vertical;
        bannerSwapNewImage.fillOrigin = (int)Image.OriginVertical.Top;
        bannerSwapNewImage.fillAmount = 0f;

        bannerSwapScanlines = new GameObject("SwapScanlines", typeof(RectTransform), typeof(RawImage)).GetComponent<RawImage>();
        bannerSwapScanlines.transform.SetParent(bannerSwapRoot, false);
        bannerSwapScanlines.raycastTarget = false;
        Stretch(bannerSwapScanlines.rectTransform);
        RebuildBannerSwapScanlineTexture();

        bannerSwapScanBar = CreateImage("RedrawScanBar", bannerSwapRoot);
        RectTransform bar = bannerSwapScanBar.rectTransform;
        bar.anchorMin = new Vector2(0f, 1f);
        bar.anchorMax = new Vector2(1f, 1f);
        bar.pivot = new Vector2(0.5f, 0.5f);
        bar.anchoredPosition = Vector2.zero;
        bar.sizeDelta = new Vector2(0f, 3f);
        bannerSwapScanBar.gameObject.SetActive(false);
    }


    private void SetBannerImmediate(Sprite banner)
    {
        bannerSwapTarget = null;
        bannerImage.sprite = banner;
        bannerImage.enabled = true;
        bannerImage.color = Color.white;
        bannerImage.gameObject.SetActive(banner != null);
        if (bannerSwapRoot != null) bannerSwapRoot.gameObject.SetActive(false);
        lastSize = new Vector2(-1f, -1f);
    }


    /// <summary>Stop an in-flight swap. Complete=true jumps directly to its intended final banner.</summary>
    private void StopBannerSwap(bool complete)
    {
        if (bannerSwapRoutine != null)
        {
            StopCoroutine(bannerSwapRoutine);
            bannerSwapRoutine = null;
        }

        if (complete && bannerSwapTarget != null)
            bannerImage.sprite = bannerSwapTarget;

        bannerSwapTarget = null;

        if (bannerImage != null)
        {
            bannerImage.enabled = true;
            bannerImage.color = Color.white;
        }

        if (bannerSwapOldImage != null)
        {
            bannerSwapOldImage.color = Color.white;
            bannerSwapOldImage.gameObject.SetActive(false);
        }

        if (bannerSwapNewImage != null)
        {
            bannerSwapNewImage.color = Color.white;
            bannerSwapNewImage.fillAmount = 1f;
            bannerSwapNewImage.gameObject.SetActive(false);
        }

        if (bannerSwapScanBar != null) bannerSwapScanBar.gameObject.SetActive(false);
        if (bannerSwapScanlines != null) bannerSwapScanlines.gameObject.SetActive(false);
        if (bannerSwapRoot != null)
        {
            bannerSwapRoot.localPosition = Vector3.zero;
            bannerSwapRoot.gameObject.SetActive(false);
        }
    }


    private IEnumerator BannerSwapAnimation()
    {
        float duration = Mathf.Max(0.05f, bannerSwapDuration);
        float flickerFraction = bannerSwapFlickerCount > 0 ? 0.30f : 0f;
        float wipeFraction = bannerSwapWipe ? 0.52f : 0f;
        float settleFraction = Mathf.Max(0.18f, 1f - flickerFraction - wipeFraction);
        float fractionTotal = flickerFraction + wipeFraction + settleFraction;

        float flickerDuration = duration * flickerFraction / fractionTotal;
        float wipeDuration = duration * wipeFraction / fractionTotal;
        float settleDuration = duration * settleFraction / fractionTotal;

        if (bannerSwapFlickerCount > 0)
        {
            float stepSeconds = flickerDuration / bannerSwapFlickerCount;
            for (int i = 0; i < bannerSwapFlickerCount; i++)
            {
                float a = FlickerAlpha(i);
                bannerSwapOldImage.color = new Color(1f, 1f, 1f, a);
                SetBannerSwapJitter(i % 2 == 0 ? UnityEngine.Random.Range(-bannerSwapJitter, bannerSwapJitter) : 0f);
                yield return WaitUnscaled(stepSeconds);
            }
        }

        bannerSwapOldImage.color = Color.white;
        SetBannerSwapJitter(0f);

        if (bannerSwapWipe)
        {
            bannerSwapNewImage.fillAmount = 0f;
            bannerSwapScanBar.gameObject.SetActive(true);

            float elapsed = 0f;
            while (elapsed < wipeDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / Mathf.Max(0.001f, wipeDuration));
                float eased = t * t * (3f - 2f * t);
                bannerSwapNewImage.fillAmount = eased;
                PositionBannerSwapScanBar(eased);
                yield return null;
            }

            bannerSwapNewImage.fillAmount = 1f;
            bannerSwapScanBar.gameObject.SetActive(false);
        }
        else
        {
            bannerSwapOldImage.gameObject.SetActive(false);
            bannerSwapNewImage.fillAmount = 1f;
        }

        // The redraw has finished. A pair of short new-image blips makes it feel like the
        // terminal locks onto the refreshed frame instead of simply stopping the wipe.
        bannerSwapOldImage.gameObject.SetActive(false);
        float settleStep = settleDuration / 3f;
        bannerSwapNewImage.color = new Color(1f, 1f, 1f, 0.58f);
        SetBannerSwapJitter(bannerSwapJitter > 0f ? -bannerSwapJitter * 0.55f : 0f);
        yield return WaitUnscaled(settleStep);

        bannerSwapNewImage.color = Color.white;
        SetBannerSwapJitter(0f);
        yield return WaitUnscaled(settleStep);

        bannerSwapNewImage.color = new Color(1f, 1f, 1f, 0.84f);
        yield return WaitUnscaled(settleStep);

        if (bannerSwapTarget != null) bannerImage.sprite = bannerSwapTarget;
        bannerSwapTarget = null;
        bannerSwapRoutine = null;
        bannerImage.enabled = true;
        bannerImage.color = Color.white;
        bannerSwapRoot.localPosition = Vector3.zero;
        bannerSwapRoot.gameObject.SetActive(false);
        lastSize = new Vector2(-1f, -1f);
    }


    private static float FlickerAlpha(int index)
    {
        // Deliberately uneven rather than a regular pulse: 1 -> .3 -> .9 -> .1 ...
        switch (index % 6)
        {
            case 0: return 0.30f;
            case 1: return 0.90f;
            case 2: return 0.10f;
            case 3: return 0.72f;
            case 4: return 0.18f;
            default: return 0.94f;
        }
    }


    private void SetBannerSwapJitter(float x)
    {
        if (bannerSwapRoot == null) return;
        bannerSwapRoot.localPosition = new Vector3(x, 0f, 0f);
    }


    private void PositionBannerSwapScanBar(float progress)
    {
        if (bannerSwapScanBar == null || bannerSwapRoot == null) return;
        float height = Mathf.Max(1f, bannerSwapRoot.rect.height);
        bannerSwapScanBar.rectTransform.anchoredPosition = new Vector2(0f, -height * Mathf.Clamp01(progress));
    }


    private IEnumerator WaitUnscaled(float seconds)
    {
        float elapsed = 0f;
        while (elapsed < seconds)
        {
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }
    }


    private void RebuildBannerSwapScanlineTexture()
    {
        if (bannerSwapScanlineTexture != null) Destroy(bannerSwapScanlineTexture);

        bannerSwapScanlineTexture = new Texture2D(1, 4, TextureFormat.RGBA32, false);
        bannerSwapScanlineTexture.wrapMode = TextureWrapMode.Repeat;
        bannerSwapScanlineTexture.filterMode = FilterMode.Point;
        bannerSwapScanlineTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0f));
        bannerSwapScanlineTexture.SetPixel(0, 1, new Color(0f, 0f, 0f, 0.18f));
        bannerSwapScanlineTexture.SetPixel(0, 2, new Color(0f, 0f, 0f, 0f));
        bannerSwapScanlineTexture.SetPixel(0, 3, new Color(0f, 0f, 0f, 0.07f));
        bannerSwapScanlineTexture.Apply();

        bannerSwapScanlines.texture = bannerSwapScanlineTexture;
        bannerSwapScanlines.color = Color.white;
    }


    private void UpdateBannerSwapScanlineUV()
    {
        if (bannerSwapScanlines == null || bannerSwapRoot == null) return;
        float height = Mathf.Max(1f, bannerSwapRoot.rect.height);
        bannerSwapScanlines.uvRect = new Rect(0f, 0f, 1f, height / 4f);
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
