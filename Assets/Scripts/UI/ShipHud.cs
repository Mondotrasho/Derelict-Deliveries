using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The in-game HUD, styled like the dialogue terminal. Builds itself at runtime
/// under its own RectTransform (stretch it over the Canvas):
///
///   top-centre   THREAT bar - fills as detection / the next hunter wave gets
///                closer, shifting amber to red, pulsing faster, glitching and
///                shaking as it closes in. Turns into HUNTED once detected.
///   top-right    SHIP STATUS - hull, shields and fuel as filling bars, crew,
///                supplies, officers and the turn number.
///   bottom-right MOVEMENT - one pip per movement point (the planned route's
///                cost blinks on the pips it would spend), the plot-mode toggle
///                (THIS TURN / MULTI-TURN) and the GO / CANCEL / END TURN buttons.
///
/// Every sprite slot is optional: temporary art ships with it (Sprites/HUD) and
/// anything left empty falls back to plain tinted boxes, so replacing art never
/// breaks the layout. Colours and fonts come from the TerminalStyle asset.
/// Objects in Hide On Start (the old temp buttons and readouts) are switched off
/// when the HUD starts, so it can be removed again without losing them.
/// </summary>
[DisallowMultipleComponent]
public sealed class ShipHud : MonoBehaviour
{
    [Header("References (found automatically if empty)")]
    [SerializeField] private TerminalStyle style;
    [SerializeField] private PlayerShipState player;
    [SerializeField] private MovementPlanController planController;
    [SerializeField] private RoutePlanner routePlanner;
    [SerializeField] private TurnManager turnManager;
    [SerializeField] private EnemySpawnController enemySpawner;
    [SerializeField] private OfficerRoster roster;

    [Header("Art (temporary - replace with your own)")]
    [SerializeField] private Sprite barFrame;
    [SerializeField] private Sprite barFill;
    [SerializeField] private Sprite buttonSprite;
    [SerializeField] private Sprite pipSprite;
    [SerializeField] private Sprite threatFrame;
    [SerializeField] private Sprite threatIcon;
    [Tooltip("Tiling texture scrolled inside the threat bar (wrap mode Repeat).")]
    [SerializeField] private Texture2D threatStripes;

    [Header("Layout")]
    [Min(0.25f)] [SerializeField] private float scale = 1f;
    [SerializeField] private Vector2 screenMargin = new Vector2(16f, 16f);

    [Header("Colours")]
    [SerializeField] private Color hullColour = new Color(0.95f, 0.3f, 0.3f, 1f);
    [SerializeField] private Color shieldColour = new Color(0.35f, 0.85f, 1f, 1f);
    [SerializeField] private Color fuelColour = new Color(1f, 0.72f, 0.1f, 1f);
    [SerializeField] private Color moveColour = new Color(0.4f, 1f, 0.55f, 1f);
    [SerializeField] private Color threatCalmColour = new Color(1f, 0.62f, 0.1f, 1f);
    [SerializeField] private Color threatHotColour = new Color(1f, 0.12f, 0.1f, 1f);

    [Header("Threat")]
    [SerializeField] private string detectionLabel = "THREAT  //  DETECTION IN";
    [SerializeField] private string huntedLabel = "HUNTED  //  NEXT WAVE IN";
    [Tooltip("At or below this many turns the bar goes into full panic mode.")]
    [Min(0)] [SerializeField] private int dangerTurns = 2;

    [Header("Labels")]
    [SerializeField] private string plotCaption = "PLOT";
    [SerializeField] private string oneTurnShort = "1 TURN";
    [SerializeField] private string multiTurnShort = "MULTI";
    [SerializeField] private string goLabel = "GO";
    [SerializeField] private string cancelLabel = "CANCEL";
    [SerializeField] private string endTurnLabel = "END TURN";

    [Header("Buttons")]
    [Tooltip("Largest button text size; labels shrink automatically to fit their button.")]
    [Min(6f)] [SerializeField] private float buttonTextSize = 16f;
    [Min(4f)] [SerializeField] private float buttonTextMinSize = 10f;
    [Tooltip("How visible a disabled button's art stays (0 = invisible).")]
    [Range(0f, 1f)] [SerializeField] private float disabledButtonAlpha = 0.35f;

    [Header("Old UI")]
    [Tooltip("Switched off when the HUD starts (the temp buttons and text readouts it replaces).")]
    [SerializeField] private List<GameObject> hideOnStart = new List<GameObject>();

    // ------------------------------------------------------------------ state
    private sealed class Bar
    {
        public RectTransform fill;
        public TMP_Text value;
        public float shown = -1f;
    }

    private TerminalStyle.Palette palette;
    private Bar hullBar, shieldBar, fuelBar;
    private TMP_Text headerText, crewText, officerText, moveText;
    private RectTransform pipRow;
    private readonly List<Image> pips = new List<Image>();
    private Button modeButton, goButton, cancelButton, endButton;
    private TMP_Text modeLabel, goText, cancelText, endText;
    private readonly Dictionary<Button, bool> buttonState = new Dictionary<Button, bool>();

    private RectTransform threatRoot, threatFill;
    private Image threatFillImage, threatFrameImage, threatIconImage;
    private RawImage threatStripeImage;
    private TMP_Text threatLabel, threatCount;
    private Vector2 threatHome;
    private float threatShown = -1f, shakeUntil, glitchUntil, stripeOffset;
    private int lastThreatTurns = int.MinValue;

    private MovementAllowance Allowance => planController != null ? planController.Allowance : null;


    // -------------------------------------------------------------- lifecycle
    private void Awake()
    {
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (planController == null) planController = FindFirstObjectByType<MovementPlanController>();
        if (routePlanner == null) routePlanner = FindFirstObjectByType<RoutePlanner>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();
        if (enemySpawner == null) enemySpawner = FindFirstObjectByType<EnemySpawnController>();
        if (roster == null) roster = FindFirstObjectByType<OfficerRoster>();

        palette = style != null ? style.GetPalette(false) : new TerminalStyle.Palette
        {
            background = new Color(0.06f, 0.035f, 0f, 1f),
            primary = new Color(1f, 0.69f, 0f, 1f),
            dim = new Color(0.61f, 0.42f, 0f, 1f)
        };

        foreach (GameObject go in hideOnStart)
        {
            if (go != null) go.SetActive(false);
        }

        Build();
    }


    private void Update()
    {
        UpdateStatus();
        UpdateMovement();
        UpdateThreat();

        if (Keyboard.current != null && Keyboard.current.mKey.wasPressedThisFrame)
        {
            ToggleMode();
        }
    }


    // ------------------------------------------------------------------ build
    private void Build()
    {
        var root = (RectTransform)transform;

        // SHIP STATUS (top right)
        RectTransform status = Panel(root, "ShipStatus", new Vector2(1f, 1f), new Vector2(-screenMargin.x, -screenMargin.y),
                                     new Vector2(380f, 262f));
        var col = Column(status, 14f, 6f);
        headerText = Label(col, "SHIP STATUS", style != null ? style.HeaderFontSize : 20f, palette.primary, 26f);
        hullBar = MakeBar(col, "HULL", hullColour);
        shieldBar = MakeBar(col, "SHIELDS", shieldColour);
        fuelBar = MakeBar(col, "FUEL", fuelColour);
        crewText = Label(col, "", 20f, palette.primary, 26f);
        officerText = Label(col, "", 18f, palette.dim, 24f);

        // MOVEMENT + actions (bottom right)
        //   MOVEMENT 5/8  > > > > > > > >
        //   PLOT [1 TURN]        [GO] [CANCEL] [END TURN]
        RectTransform move = Panel(root, "Movement", new Vector2(1f, 0f), new Vector2(-screenMargin.x, screenMargin.y),
                                   new Vector2(540f, 118f));
        var mcol = Column(move, 12f, 10f);
        RectTransform moveRow = Row(mcol, 28f, 8f);
        moveText = Label(moveRow, "MOVEMENT", 16f, palette.primary, 28f, 140f);
        pipRow = Row(moveRow, 24f, 4f);
        Flex(pipRow);

        RectTransform buttons = Row(mcol, 42f, 6f);
        Label(buttons, plotCaption, 13f, palette.dim, 42f, 40f);
        modeButton = MakeButton(buttons, oneTurnShort, ToggleMode, 96f, out modeLabel, flexible: false);
        Flex(Rect("Gap", buttons));                                        // pushes the actions right
        goButton = MakeButton(buttons, goLabel, () => planController?.CommitSegment(), 64f, out goText, flexible: false);
        cancelButton = MakeButton(buttons, cancelLabel, () => planController?.Cancel(), 96f, out cancelText, flexible: false);
        endButton = MakeButton(buttons, endTurnLabel, EndTurn, 116f, out endText, flexible: false);

        // THREAT (top centre)
        threatRoot = Panel(root, "Threat", new Vector2(0.5f, 1f), new Vector2(0f, -screenMargin.y), new Vector2(640f, 92f));
        threatHome = threatRoot.anchoredPosition;
        RectTransform trow = Row(Column(threatRoot, 12f, 0f), 68f, 12f);

        threatIconImage = Img(trow, "Icon", threatIcon, threatCalmColour, false);
        Size(threatIconImage.rectTransform, 56f, 56f);

        RectTransform tmid = Column(Flex(Rect("Middle", trow)), 0f, 4f);
        threatLabel = Label(tmid, detectionLabel, 18f, threatCalmColour, 22f);
        RectTransform barRoot = Rect("Bar", tmid);
        Size(barRoot, 0f, 34f);
        Flex(barRoot, horizontal: true);
        Image back = Img(barRoot, "Back", null, new Color(0f, 0f, 0f, 0.55f), false);
        Stretch(back.rectTransform);
        threatFill = Rect("Fill", barRoot);
        threatFill.anchorMin = Vector2.zero;
        threatFill.anchorMax = new Vector2(0f, 1f);
        threatFill.offsetMin = threatFill.offsetMax = Vector2.zero;
        threatFillImage = Img(threatFill, "Colour", barFill, threatCalmColour, true);
        Stretch(threatFillImage.rectTransform);
        if (threatStripes != null)
        {
            var go = new GameObject("Stripes", typeof(RectTransform), typeof(RawImage));
            go.transform.SetParent(threatFill, false);
            threatStripeImage = go.GetComponent<RawImage>();
            threatStripeImage.texture = threatStripes;
            threatStripeImage.color = new Color(0f, 0f, 0f, 0.35f);
            threatStripeImage.raycastTarget = false;
            Stretch(threatStripeImage.rectTransform);
        }
        threatFrameImage = Img(barRoot, "Frame", threatFrame != null ? threatFrame : barFrame, threatCalmColour, true);
        Stretch(threatFrameImage.rectTransform);

        threatCount = Label(trow, "T-0", 40f, threatCalmColour, 60f, 110f);
        threatCount.alignment = TextAlignmentOptions.Center;
    }


    private RectTransform Panel(RectTransform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        RectTransform rt = Rect(name, parent);
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos * scale;
        rt.sizeDelta = size * scale;
        Image bg = rt.gameObject.AddComponent<Image>();
        bg.raycastTarget = true;
        if (style != null) style.ApplyPanel(bg, palette);
        else bg.color = new Color(palette.background.r, palette.background.g, palette.background.b, 0.85f);
        AddScanlines(rt);
        return rt;
    }


    private Bar MakeBar(RectTransform parent, string title, Color colour)
    {
        RectTransform row = Row(parent, 34f, 8f);
        Label(row, title, 18f, palette.primary, 34f, 96f);

        RectTransform barRoot = Rect(title + "Bar", row);
        Flex(barRoot);
        Image back = Img(barRoot, "Back", null, new Color(0f, 0f, 0f, 0.5f), false);
        Stretch(back.rectTransform);
        var bar = new Bar { fill = Rect("Fill", barRoot) };
        bar.fill.anchorMin = Vector2.zero;
        bar.fill.anchorMax = Vector2.one;
        bar.fill.offsetMin = new Vector2(2f, 2f) * scale;
        bar.fill.offsetMax = new Vector2(-2f, -2f) * scale;
        Image fill = Img(bar.fill, "Colour", barFill, colour, true);
        Stretch(fill.rectTransform);
        Image frame = Img(barRoot, "Frame", barFrame, palette.primary, true);
        Stretch(frame.rectTransform);

        bar.value = Label(row, "", 18f, palette.primary, 34f, 90f);
        bar.value.alignment = TextAlignmentOptions.Right;
        return bar;
    }


    private Button MakeButton(RectTransform parent, string text, UnityEngine.Events.UnityAction onClick, float width, out TMP_Text label, bool flexible = true)
    {
        RectTransform rt = Rect(text, parent);
        Size(rt, width, 0f);
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = width * scale;
        le.flexibleWidth = flexible ? 1f : 0f;
        Image bg = rt.gameObject.AddComponent<Image>();
        if (buttonSprite != null)
        {
            bg.sprite = buttonSprite;
            bg.type = Image.Type.Sliced;
        }
        Button button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = bg;
        button.onClick.AddListener(onClick);

        label = Label(rt, text, 20f, palette.primary, 0f);
        label.alignment = TextAlignmentOptions.Center;
        Stretch(label.rectTransform);
        SetButton(button, label, true, force: true);
        return button;
    }


    // ---------------------------------------------------------------- update
    private void UpdateStatus()
    {
        ShipResources r = player != null ? player.Resources : null;
        if (r != null)
        {
            SetBar(hullBar, r.HullIntegrity, r.MaxHullIntegrity);
            SetBar(shieldBar, r.Shields, r.MaxShields);
            SetBar(fuelBar, r.Fuel, r.MaxFuel);
            int supplies = player.EventState != null ? player.EventState.GetCounter(EventKeys.Supplies) : 0;
            crewText.text = $"CREW {r.Crew}/{r.MaxCrew}     SUPPLIES {supplies}";
        }

        headerText.text = turnManager != null ? $"SHIP STATUS  //  TURN {turnManager.CurrentTurn}" : "SHIP STATUS";

        if (roster != null && roster.Aboard.Count > 0)
        {
            var names = new List<string>();
            foreach (OfficerDefinition o in roster.Aboard) names.Add(o.DisplayName.ToUpperInvariant());
            officerText.text = "OFFICERS  " + string.Join(", ", names);
        }
        else
        {
            officerText.text = "OFFICERS  NONE ABOARD";
        }
    }


    private void SetBar(Bar bar, float value, float max)
    {
        float target = max > 0f ? Mathf.Clamp01(value / max) : 0f;
        bar.shown = bar.shown < 0f ? target : Mathf.MoveTowards(bar.shown, target, Time.unscaledDeltaTime * 1.5f);
        bar.fill.anchorMax = new Vector2(bar.shown, 1f);
        bar.value.text = $"{Mathf.RoundToInt(value)}/{Mathf.RoundToInt(max)}";
    }


    private void UpdateMovement()
    {
        MovementAllowance a = Allowance;
        int max = a != null ? a.MaxMovementPoints : 0;
        int current = a != null ? a.CurrentMovementPoints : 0;

        if (pips.Count != max)
        {
            foreach (Image p in pips) Destroy(p.gameObject);
            pips.Clear();
            for (int i = 0; i < max; i++)
            {
                Image pip = Img(pipRow, "Pip" + i, pipSprite, moveColour, false);
                Size(pip.rectTransform, 18f, 26f);
                pips.Add(pip);
            }
        }

        // cost of the route being previewed, capped at what is left
        int cost = 0;
        if (a != null && routePlanner != null && routePlanner.HasPlannedRoute)
        {
            cost = Mathf.Min(current, a.CalculatePathCost(routePlanner.PlannedPath));
        }
        bool blinkOn = Mathf.Repeat(Time.unscaledTime * 3f, 1f) < 0.6f;
        for (int i = 0; i < pips.Count; i++)
        {
            Color c;
            if (i >= current) c = new Color(moveColour.r, moveColour.g, moveColour.b, 0.18f);      // spent
            else if (i >= current - cost) c = blinkOn ? Color.white : moveColour;                   // this route
            else c = moveColour;                                                                     // left after it
            pips[i].color = c;
        }
        moveText.text = $"MOVEMENT {current}/{max}";

        bool oneTurn = planController == null || planController.Mode == MovementPlanController.PlanningMode.OneTurn;
        modeLabel.text = oneTurn ? oneTurnShort : multiTurnShort;

        bool canAct = planController != null && planController.CanAcceptPlayerInput;
        bool moving = player != null && player.IsMoving;
        SetButton(modeButton, modeLabel, true);
        SetButton(goButton, goText, canAct && planController.CanCommit);
        SetButton(cancelButton, cancelText, canAct &&
                  ((routePlanner != null && routePlanner.HasPlannedRoute) || planController.HasQueuedRemainder));
        SetButton(endButton, endText, canAct && !moving && turnManager != null && turnManager.IsPlayerPhase);
    }


    private void UpdateThreat()
    {
        if (enemySpawner == null)
        {
            threatRoot.gameObject.SetActive(false);
            return;
        }

        bool hunted = enemySpawner.IsDetectionActive;
        int turns = hunted ? enemySpawner.TurnsUntilNextWave : enemySpawner.TurnsUntilDetection;
        int total = Mathf.Max(1, hunted ? enemySpawner.TurnsBetweenWaves : enemySpawner.DetectionDelayTurns);
        float target = 1f - Mathf.Clamp01((float)turns / total);
        float t = Time.unscaledTime;

        if (turns != lastThreatTurns)
        {
            if (lastThreatTurns != int.MinValue) shakeUntil = t + 0.45f;   // it just got closer
            lastThreatTurns = turns;
        }

        threatShown = threatShown < 0f ? target : Mathf.MoveTowards(threatShown, target, Time.unscaledDeltaTime * 0.8f);
        threatFill.anchorMax = new Vector2(Mathf.Max(0.02f, threatShown), 1f);

        bool danger = hunted || turns <= dangerTurns;
        float heat = hunted ? 1f : threatShown;
        float pulseSpeed = 2f + 8f * heat;
        float pulse = 0.5f + 0.5f * Mathf.Sin(t * pulseSpeed);
        Color c = Color.Lerp(threatCalmColour, threatHotColour, heat);
        Color glow = Color.Lerp(c, Color.white, danger ? pulse * 0.45f : pulse * 0.12f);

        threatFillImage.color = glow;
        threatFrameImage.color = c;
        threatLabel.color = c;
        threatCount.color = glow;
        if (threatIconImage != null)
        {
            threatIconImage.color = danger && pulse < 0.25f ? new Color(c.r, c.g, c.b, 0.2f) : glow;   // the eye blinks
        }

        stripeOffset += Time.unscaledDeltaTime * (0.2f + 1.8f * heat);
        if (threatStripeImage != null)
        {
            RectTransform fr = threatStripeImage.rectTransform;
            float aspect = fr.rect.height > 0f ? fr.rect.width / fr.rect.height : 1f;
            threatStripeImage.uvRect = new Rect(-stripeOffset, 0f, Mathf.Max(1f, aspect), 1f);
        }

        // glitches: rare when calm, constant nagging when close
        float glitchChance = (danger ? 3f : 0.25f) * Time.unscaledDeltaTime;
        if (Random.value < glitchChance) glitchUntil = t + Random.Range(0.05f, 0.14f);
        bool glitching = t < glitchUntil;

        string label = hunted ? huntedLabel : detectionLabel;
        threatLabel.text = glitching ? Garble(label) : label;
        threatCount.text = glitching ? Garble($"T-{turns}") : $"T-{turns}";

        Vector2 offset = Vector2.zero;
        if (t < shakeUntil) offset += Random.insideUnitCircle * 6f * scale;
        if (glitching) offset.x += Random.Range(-5f, 5f) * scale;
        threatRoot.anchoredPosition = threatHome + offset;
    }


    // --------------------------------------------------------------- actions
    private void ToggleMode()
    {
        if (planController == null) return;
        planController.Mode = planController.Mode == MovementPlanController.PlanningMode.OneTurn
            ? MovementPlanController.PlanningMode.MultiTurn
            : MovementPlanController.PlanningMode.OneTurn;
        if (routePlanner != null) routePlanner.ClearRoute();    // re-preview under the new rule
    }


    private void EndTurn()
    {
        if (turnManager == null || !turnManager.IsPlayerPhase) return;
        if (player != null && player.IsMoving) return;
        routePlanner?.ClearRoute();
        turnManager.EndTurn();
    }


    // --------------------------------------------------------------- helpers
    private void SetButton(Button button, TMP_Text label, bool interactable, bool force = false)
    {
        if (!force && buttonState.TryGetValue(button, out bool was) && was == interactable) return;
        buttonState[button] = interactable;
        if (style != null) style.ApplyButton(button, label, palette, interactable);
        else
        {
            button.interactable = interactable;
            label.color = interactable ? palette.primary : palette.dim;
        }
        if (buttonSprite != null)
        {
            var img = (Image)button.targetGraphic;
            img.sprite = buttonSprite;
            img.type = Image.Type.Sliced;
        }

        // The terminal style sizes button text for the big dialogue buttons -
        // far too large here. Shrink-to-fit within the HUD's own limits.
        label.enableAutoSizing = true;
        label.fontSizeMax = buttonTextSize * scale;
        label.fontSizeMin = buttonTextMinSize * scale;
        label.fontSize = buttonTextSize * scale;
        label.margin = new Vector4(6f, 2f, 6f, 2f) * scale;

        // Keep a disabled button's art visible (dimmed) instead of fading it out.
        ColorBlock colours = button.colors;
        Color d = colours.disabledColor;
        colours.disabledColor = new Color(d.r, d.g, d.b, Mathf.Max(d.a, disabledButtonAlpha));
        button.colors = colours;
    }


    private static string Garble(string s)
    {
        const string junk = "#%&@!?/\\<>01";
        char[] c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            if (c[i] != ' ' && Random.value < 0.35f) c[i] = junk[Random.Range(0, junk.Length)];
        }
        return new string(c);
    }


    private void AddScanlines(RectTransform parent)
    {
        if (style == null || !style.Scanlines) return;
        var go = new GameObject("Scanlines", typeof(RectTransform), typeof(RawImage));
        go.transform.SetParent(parent, false);
        var raw = go.GetComponent<RawImage>();
        raw.raycastTarget = false;
        go.AddComponent<LayoutElement>().ignoreLayout = true;     // an overlay, not a row
        int on = Mathf.Max(1, Mathf.RoundToInt(style.ScanlineThickness / 4f));
        int off = Mathf.Max(1, Mathf.RoundToInt(style.ScanlineGap / 4f));
        var tex = new Texture2D(1, on + off, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
        for (int y = 0; y < on + off; y++) tex.SetPixel(0, y, y < on ? new Color(0f, 0f, 0f, style.ScanlineOpacity * 0.6f) : Color.clear);
        tex.Apply();
        raw.texture = tex;
        Stretch(raw.rectTransform);
        raw.uvRect = new Rect(0f, 0f, 1f, parent.sizeDelta.y / Mathf.Max(1f, (on + off) * 2f));
        go.transform.SetAsLastSibling();
    }


    private RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }


    private RectTransform Column(RectTransform parent, float padding, float spacing)
    {
        var v = parent.gameObject.AddComponent<VerticalLayoutGroup>();
        v.padding = new RectOffset((int)(padding * scale), (int)(padding * scale), (int)(padding * scale), (int)(padding * scale));
        v.spacing = spacing * scale;
        v.childControlWidth = v.childControlHeight = true;
        v.childForceExpandWidth = true;
        v.childForceExpandHeight = false;
        return parent;
    }


    private RectTransform Row(RectTransform parent, float height, float spacing)
    {
        RectTransform rt = Rect("Row", parent);
        var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.spacing = spacing * scale;
        h.childControlWidth = h.childControlHeight = true;
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        h.childAlignment = TextAnchor.MiddleLeft;
        var le = rt.gameObject.AddComponent<LayoutElement>();
        le.preferredHeight = height * scale;
        le.minHeight = height * scale;
        return rt;
    }


    private RectTransform Flex(RectTransform rt, bool horizontal = true)
    {
        LayoutElement le = Layout(rt);
        le.flexibleWidth = 1f;
        return rt;
    }


    private void Size(RectTransform rt, float w, float h)
    {
        LayoutElement le = Layout(rt);
        if (w > 0f) { le.preferredWidth = w * scale; le.minWidth = w * scale; }
        if (h > 0f) { le.preferredHeight = h * scale; le.minHeight = h * scale; }
    }


    private static LayoutElement Layout(RectTransform rt)
    {
        // TryGetComponent, not "GetComponent ?? Add": in the editor a missing
        // component comes back as a fake-null object that ?? does not catch.
        return rt.TryGetComponent(out LayoutElement le) ? le : rt.gameObject.AddComponent<LayoutElement>();
    }


    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }


    private Image Img(Transform parent, string name, Sprite sprite, Color colour, bool sliced)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.color = colour;
        img.raycastTarget = false;
        if (sprite != null && sliced) img.type = Image.Type.Sliced;
        return img;
    }


    private TMP_Text Label(RectTransform parent, string text, float size, Color colour, float height, float width = 0f)
    {
        var go = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<TextMeshProUGUI>();
        if (style != null) style.ApplyText(t, colour, size * scale);
        else { t.color = colour; t.fontSize = size * scale; }
        t.text = text;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Ellipsis;
        t.alignment = TextAlignmentOptions.MidlineLeft;
        t.raycastTarget = false;
        if (height > 0f || width > 0f) Size((RectTransform)go.transform, width, height);
        return t;
    }
}
