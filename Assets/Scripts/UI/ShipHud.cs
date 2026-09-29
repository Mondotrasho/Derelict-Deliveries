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
///                shaking as it closes in. Turns into HUNTED once detected:
///                the bar then stays full (still animated) and the countdown to
///                the next hunter wave moves into a segmented pie clock to the
///                right, one slice per turn, greyed out while the enemy cap is
///                full (a wave then would bring nobody).
///   top-right    STATUS - hull, shields and fuel bars (icon + value over the
///                bar), crew and supplies counts and an INVENTORY button (it
///                raises On Inventory Pressed / InventoryPressed - wire it up).
///                Changes float off as +10 / -15; low hull or fuel pulses red.
///   bottom-right MOVEMENT - one pip per movement point (the planned route's
///                cost blinks on the pips it would spend), the turn number, the
///                route's cost readout, the plot-mode toggle and GO / CANCEL /
///                END TURN. Keys: SPACE = GO, E = END TURN, M = plot mode.
///
/// Every sprite slot is optional: temporary art ships with it (Sprites/HUD, and
/// the icons in Resources/HUDIcons, loaded automatically when a slot is empty)
/// and anything still missing falls back to plain tinted boxes, so replacing art
/// never breaks the layout. Colours and fonts come from the TerminalStyle asset.
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

    [Header("Art (temporary - replace with your own)")]
    [SerializeField] private Sprite barFrame;
    [SerializeField] private Sprite barFill;
    [SerializeField] private Sprite buttonSprite;
    [SerializeField] private Sprite pipSprite;
    [SerializeField] private Sprite threatFrame;
    [SerializeField] private Sprite threatIcon;
    [Tooltip("Tiling texture scrolled inside the threat bar (wrap mode Repeat).")]
    [SerializeField] private Texture2D threatStripes;

    [Header("Icons (empty = load the temp ones from Resources/HUDIcons)")]
    [SerializeField] private Sprite hullIcon;
    [SerializeField] private Sprite shieldIcon;
    [SerializeField] private Sprite fuelIcon;
    [SerializeField] private Sprite crewIcon;
    [SerializeField] private Sprite suppliesIcon;
    [SerializeField] private Sprite inventoryIcon;

    [Header("Inventory")]
    [Tooltip("Raised when the INVENTORY button is pressed. Nothing is wired yet.")]
    [SerializeField] private UnityEngine.Events.UnityEvent onInventoryPressed = new UnityEngine.Events.UnityEvent();

    /// <summary>Raised when the INVENTORY button is pressed (for code; the inspector event fires too).</summary>
    public event System.Action InventoryPressed;

    [Header("Feedback")]
    [Tooltip("Hull or fuel below this fraction pulses red.")]
    [Range(0f, 1f)] [SerializeField] private float lowFraction = 0.25f;
    [SerializeField] private Color gainColour = new Color(0.45f, 1f, 0.55f, 1f);
    [SerializeField] private Color lossColour = new Color(1f, 0.35f, 0.3f, 1f);
    [SerializeField] private bool showKeyHints = true;

    [Header("Layout")]
    [Min(0.25f)] [SerializeField] private float scale = 1f;
    [Tooltip("Gap between the HUD panels and the screen edges. 0 = panels sit flush against the edges.")]
    [SerializeField] private Vector2 screenMargin = Vector2.zero;

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
    [Tooltip("Diameter of the wave pie clock.")]
    [Min(20f)] [SerializeField] private float waveClockSize = 64f;
    [Tooltip("Gap between pie slices, in degrees.")]
    [Range(0f, 30f)] [SerializeField] private float waveClockGapDegrees = 8f;
    [Tooltip("Ring thickness (0 = thin line, 1 = solid pie).")]
    [Range(0.1f, 1f)] [SerializeField] private float waveClockThickness = 0.38f;
    [SerializeField] private Color waveClockCapColour = new Color(0.45f, 0.45f, 0.45f, 1f);
    [SerializeField] private string waveClockCapText = "MAX";

    [Header("Labels")]
    [HideInInspector] [SerializeField] private string plotCaption = "PLOT";   // caption removed from the layout
    [SerializeField] private string oneTurnShort = "1 TURN";
    [SerializeField] private string multiTurnShort = "MULTI";
    [SerializeField] private string goLabel = "GO";
    [SerializeField] private string cancelLabel = "CANCEL";
    [SerializeField] private string endTurnLabel = "END TURN";

    [Header("Menu Button (top left, opens PrototypePauseMenuController)")]
    [SerializeField] private bool showMenuButton = true;
    [SerializeField] private string menuLabel = "MENU";
    [Min(40f)] [SerializeField] private float menuButtonWidth = 96f;

    [Header("Buttons")]
    [Tooltip("Largest button text size; labels shrink automatically to fit their button.")]
    [Min(6f)] [SerializeField] private float buttonTextSize = 16f;
    [Min(4f)] [SerializeField] private float buttonTextMinSize = 10f;
    [Tooltip("How visible a disabled button's art stays (0 = invisible).")]
    [Range(0f, 1f)] [SerializeField] private float disabledButtonAlpha = 0.35f;

    [Header("Hide while events / combat are open")]
    [Tooltip("The movement / turn panel (bottom right).")]
    [SerializeField] private bool hideMovementPanel = true;
    [SerializeField] private bool hideStatusPanel = false;
    [SerializeField] private bool hideThreatPanel = false;
    [Min(0.01f)] [SerializeField] private float hideFadeSeconds = 0.15f;
    [Tooltip("Also hide whenever the game blocks player input (any event, dialogue or hold that pauses movement) - catches windows the HUD doesn't know by name.")]
    [SerializeField] private bool hideWhenInputBlocked = true;
    [Tooltip("Print to the console what made the panels hide / show (for tracking down a window that isn't caught).")]
    [SerializeField] private bool logVisibility = false;
    [Tooltip("Waits this long after the last window closes before coming back, so chained windows (planet -> event -> dialogue) don't make it flicker.")]
    [Min(0f)] [SerializeField] private float reshowDelaySeconds = 0.25f;

    [Header("Old UI")]
    [Tooltip("Switched off when the HUD starts (the temp buttons and text readouts it replaces).")]
    [SerializeField] private List<GameObject> hideOnStart = new List<GameObject>();

    // ------------------------------------------------------------------ state
    private sealed class Bar
    {
        public RectTransform root, fill;
        public Image fillImage, icon;
        public Color colour;
        public TMP_Text value, valueShadow;
        public bool warnLow;
        public float shown = -1f, last = float.NaN, flashUntil;
    }

    private sealed class Floater
    {
        public TMP_Text text;
        public Vector2 start;
        public float born;
    }

    private TerminalStyle.Palette palette;
    private Bar hullBar, shieldBar, fuelBar;
    private TMP_Text crewText, suppliesText, moveText, turnText, routeText;
    private RectTransform routeTip;
    private float moveWidth, moveHeight;
    private Canvas hudCanvas;
    private Image crewIconImage, suppliesIconImage;
    private RectTransform statusRoot;
    private int lastCrew = int.MinValue, lastSupplies = int.MinValue;
    private readonly List<Floater> floaters = new List<Floater>();

    // windows that hide the HUD panels while open
    private RectTransform movePanel;
    private CanvasGroup moveGroup, statusGroup, threatGroup;
    private EventPanel[] eventPanels;
    private PlanetPicker[] planetPickers;
    private DialoguePanelController[] dialoguePanels;
    private CombatScreenController[] combatScreens;
    private CombatEncounterController[] combatControllers;
    private WarpExitController[] warpScreens;
    private float lastModalTime = float.NegativeInfinity;
    private bool panelsHidden;
    private RectTransform pipRow;
    private readonly List<Image> pips = new List<Image>();
    private Button modeButton, goButton, cancelButton, endButton, inventoryButton;
    private TMP_Text modeLabel, goText, cancelText, endText;
    private readonly Dictionary<Button, bool> buttonState = new Dictionary<Button, bool>();

    private RectTransform threatRoot, threatFill;
    private Image threatFillImage, threatFrameImage, threatIconImage;
    private RawImage threatStripeImage;
    private TMP_Text threatLabel, threatCount;
    private Vector2 threatHome;
    private float threatShown = -1f, shakeUntil, glitchUntil, stripeOffset;
    private int lastThreatTurns = int.MinValue;

    // wave pie clock (visible once hunted)
    private RectTransform waveClockRoot;
    private Image waveClockBack;
    private readonly List<Image> waveSlices = new List<Image>();
    private TMP_Text waveClockCount;
    private Sprite waveRingSprite;
    private float waveFlashUntil, clockPunchUntil;
    private int lastWaveTurns = int.MinValue;
    private bool wasHunted;

    private MovementAllowance Allowance => planController != null ? planController.Allowance : null;


    // -------------------------------------------------------------- lifecycle
    private void Awake()
    {
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();
        if (planController == null) planController = FindFirstObjectByType<MovementPlanController>();
        if (routePlanner == null) routePlanner = FindFirstObjectByType<RoutePlanner>();
        if (turnManager == null) turnManager = FindFirstObjectByType<TurnManager>();
        if (enemySpawner == null) enemySpawner = FindFirstObjectByType<EnemySpawnController>();
        if (hullIcon == null) hullIcon = Resources.Load<Sprite>("HUDIcons/hud_icon_hull");
        if (shieldIcon == null) shieldIcon = Resources.Load<Sprite>("HUDIcons/hud_icon_shield");
        if (fuelIcon == null) fuelIcon = Resources.Load<Sprite>("HUDIcons/hud_icon_fuel");
        if (crewIcon == null) crewIcon = Resources.Load<Sprite>("HUDIcons/hud_icon_crew");
        if (suppliesIcon == null) suppliesIcon = Resources.Load<Sprite>("HUDIcons/hud_icon_supplies");
        if (inventoryIcon == null) inventoryIcon = Resources.Load<Sprite>("HUDIcons/hud_icon_inventory");

        palette = style != null ? style.GetPalette(false) : new TerminalStyle.Palette
        {
            background = new Color(0.06f, 0.035f, 0f, 1f),
            primary = new Color(1f, 0.69f, 0f, 1f),
            dim = new Color(0.61f, 0.42f, 0f, 1f)
        };

        eventPanels = FindObjectsByType<EventPanel>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        planetPickers = FindObjectsByType<PlanetPicker>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        dialoguePanels = FindObjectsByType<DialoguePanelController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        combatScreens = FindObjectsByType<CombatScreenController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        combatControllers = FindObjectsByType<CombatEncounterController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        warpScreens = FindObjectsByType<WarpExitController>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (GameObject go in hideOnStart)
        {
            if (go != null) go.SetActive(false);
        }

        Build();
        moveGroup = movePanel.gameObject.AddComponent<CanvasGroup>();
        statusGroup = statusRoot.gameObject.AddComponent<CanvasGroup>();
        threatGroup = threatRoot.gameObject.AddComponent<CanvasGroup>();
    }


    private void Update()
    {
        UpdateStatus();
        UpdateMovement();
        UpdateThreat();

        UpdateFloaters();
        UpdatePanelVisibility();

        Keyboard k = Keyboard.current;
        if (k != null && !(panelsHidden && hideMovementPanel) && !PrototypePauseMenuController.BlocksGameplayInput)
        {
            if (k.mKey.wasPressedThisFrame) ToggleMode();
            if (k.spaceKey.wasPressedThisFrame && goButton.interactable) planController?.CommitSegment();
            if (k.eKey.wasPressedThisFrame && endButton.interactable) EndTurn();
        }
    }


    // ------------------------------------------------------------------ build
    private void Build()
    {
        var root = (RectTransform)transform;

        // STATUS (top right):  [icon][bar 64/100] x3,  crew / supplies / inventory
        statusRoot = Panel(root, "ShipStatus", new Vector2(1f, 1f), new Vector2(-screenMargin.x, -screenMargin.y),
                           new Vector2(300f, 164f));
        var col = Column(statusRoot, 12f, 6f);
        hullBar = MakeBar(col, "Hull", hullIcon, hullColour, true);
        shieldBar = MakeBar(col, "Shields", shieldIcon, shieldColour, false);
        fuelBar = MakeBar(col, "Fuel", fuelIcon, fuelColour, true);

        RectTransform counts = Row(col, 36f, 6f);
        crewIconImage = Icon(counts, "CrewIcon", crewIcon, palette.primary, 26f);
        crewText = Label(counts, "", 18f, palette.primary, 36f, 64f);
        suppliesIconImage = Icon(counts, "SuppliesIcon", suppliesIcon, palette.primary, 26f);
        suppliesText = Label(counts, "", 18f, palette.primary, 36f, 56f);
        Flex(Rect("Gap", counts));
        inventoryButton = MakeButton(counts, "", PressInventory, 48f, out TMP_Text invLabel, flexible: false);
        Image invIcon = Img(inventoryButton.transform, "Icon", inventoryIcon, palette.primary, false);
        invIcon.preserveAspect = true;
        Stretch(invIcon.rectTransform);
        invIcon.rectTransform.offsetMin = new Vector2(10f, 7f) * scale;
        invIcon.rectTransform.offsetMax = new Vector2(-10f, -7f) * scale;

        // MOVEMENT + actions (bottom right, frame flipped so its heavy edge is at the bottom)
        //   > > > > > > > > > > > >   TURN 3
        //   [1 TURN] [GO] [CANCEL] [END TURN]
        // Exactly as wide as the button row, tight padding.
        // No MOVE / PLOT captions: the pips fill the top row up to TURN, and the key
        // hints and the route readout only appear while hovering (see UpdateMovement).
        const float movePad = 8f, moveGap = 4f;
        const float modeW = 96f, goW = 60f, cancelW = 88f, endW = 104f;
        moveWidth = movePad * 2f + modeW + goW + cancelW + endW + moveGap * 3f;
        moveHeight = movePad * 2f + 26f + 40f + moveGap;
        RectTransform move = movePanel = Panel(root, "Movement", new Vector2(1f, 0f), new Vector2(-screenMargin.x, screenMargin.y),
                                   new Vector2(moveWidth, moveHeight), flipFrame: true);
        var mcol = Column(move, movePad, moveGap);
        RectTransform moveRow = Row(mcol, 26f, 6f);
        pipRow = Row(moveRow, 22f, 2f);
        Flex(pipRow);
        turnText = Label(moveRow, "TURN 1", 16f, palette.primary, 26f, 72f);
        turnText.alignment = TextAlignmentOptions.Right;

        // route readout: a small tag sitting on top of the panel, shown on hover
        routeTip = Panel(root, "RouteTip", new Vector2(1f, 0f),
                         new Vector2(-screenMargin.x, screenMargin.y + moveHeight), new Vector2(moveWidth, 22f));
        routeText = Label(routeTip, "", 13f, palette.dim, 0f);
        routeText.alignment = TextAlignmentOptions.Center;
        Stretch(routeText.rectTransform);
        routeTip.gameObject.SetActive(false);

        RectTransform buttons = Row(mcol, 40f, moveGap);
        modeButton = MakeButton(buttons, oneTurnShort, ToggleMode, modeW, out modeLabel, flexible: false);
        goButton = MakeButton(buttons, goLabel, () => planController?.CommitSegment(), goW, out goText, flexible: false);
        cancelButton = MakeButton(buttons, cancelLabel, () => planController?.Cancel(), cancelW, out cancelText, flexible: false);
        endButton = MakeButton(buttons, endTurnLabel, EndTurn, endW, out endText, flexible: false);

        // MENU (top left): opens the pause menu. Same frame and buttons as the rest of the HUD.
        if (showMenuButton)
        {
            const float menuPad = 8f, menuButtonHeight = 40f;
            RectTransform menuPanel = Panel(root, "Menu", new Vector2(0f, 1f), new Vector2(screenMargin.x, -screenMargin.y),
                                            new Vector2(menuButtonWidth + menuPad * 2f, menuButtonHeight + menuPad * 2f));
            RectTransform menuRow = Row(Column(menuPanel, menuPad, 0f), menuButtonHeight, 0f);
            MakeButton(menuRow, menuLabel, PrototypePauseMenuController.ToggleMenu, menuButtonWidth, out _, flexible: false);
        }

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

        // same 110-wide slot as before: T-n text until detected, then the pie
        RectTransform countSlot = Rect("Count", trow);
        Size(countSlot, 110f, 60f);
        threatCount = Label(countSlot, "T-0", 40f, threatCalmColour, 0f);
        threatCount.alignment = TextAlignmentOptions.Center;
        Stretch((RectTransform)threatCount.transform);

        waveClockRoot = Rect("WaveClock", countSlot);
        waveClockRoot.anchorMin = waveClockRoot.anchorMax = waveClockRoot.pivot = new Vector2(0.5f, 0.5f);
        waveClockRoot.sizeDelta = Vector2.one * waveClockSize * scale;
        waveRingSprite = MakeRingSprite(waveClockThickness);
        waveClockBack = Img(waveClockRoot, "Back", waveRingSprite, new Color(0f, 0f, 0f, 0.55f), false);
        Stretch(waveClockBack.rectTransform);
        var countGo = Rect("Slices", waveClockRoot);
        Stretch(countGo);
        waveClockCount = Label(waveClockRoot, "0", 22f, threatHotColour, 0f);
        waveClockCount.alignment = TextAlignmentOptions.Center;
        waveClockCount.overflowMode = TextOverflowModes.Overflow;
        Stretch((RectTransform)waveClockCount.transform);
        waveClockRoot.gameObject.SetActive(false);

        if (enemySpawner != null)
        {
            enemySpawner.ReinforcementWaveSpawned += HandleWaveSpawned;
        }
    }


    private void OnDestroy()
    {
        if (enemySpawner != null)
        {
            enemySpawner.ReinforcementWaveSpawned -= HandleWaveSpawned;
        }
    }


    private void HandleWaveSpawned(int waveIndex, int spawned)
    {
        // the spawner resets the countdown in the same call, so the clock would
        // never be seen full - show it full for a moment and kick the bar
        float t = Time.unscaledTime;
        waveFlashUntil = t + 0.7f;
        clockPunchUntil = t + 0.35f;
        shakeUntil = t + 0.6f;
    }


    /// <summary>Rebuilds the pie slices when the turns-per-wave changes.</summary>
    private void EnsureWaveSlices(int count)
    {
        count = Mathf.Clamp(count, 1, 24);
        if (waveSlices.Count == count) return;

        Transform parent = waveClockRoot.Find("Slices");
        foreach (Image img in waveSlices) if (img != null) Destroy(img.gameObject);
        waveSlices.Clear();

        float gap = count > 1 ? waveClockGapDegrees : 0f;
        float sliceFill = Mathf.Max(0.01f, 1f / count - gap / 360f);

        for (int i = 0; i < count; i++)
        {
            Image s = Img(parent, $"Slice{i}", waveRingSprite, threatHotColour, false);
            Stretch(s.rectTransform);
            s.type = Image.Type.Filled;
            s.fillMethod = Image.FillMethod.Radial360;
            s.fillOrigin = (int)Image.Origin360.Top;
            s.fillClockwise = true;
            s.fillAmount = sliceFill;
            // slice i starts i/count of the way round (clockwise), centred on its gap
            s.rectTransform.localEulerAngles = new Vector3(0f, 0f, -(360f * i / count) - gap * 0.5f);
            waveSlices.Add(s);
        }
    }


    /// <summary>A white ring (or disc) texture so the clock needs no art.</summary>
    private static Sprite MakeRingSprite(float thickness)
    {
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float outer = size * 0.5f - 1f;
        float inner = outer * (1f - thickness);
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(size * 0.5f, size * 0.5f));
            float a = Mathf.Clamp01(outer - d + 0.5f) * Mathf.Clamp01(d - inner + 0.5f);
            px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
        }
        tex.SetPixels32(px);
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }


    private RectTransform Panel(RectTransform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, bool flipFrame = false)
    {
        RectTransform rt = Rect(name, parent);
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = pos * scale;
        rt.sizeDelta = size * scale;
        Image bg;
        if (flipFrame)
        {
            // Frame upside down (its "top" edge at the bottom), on its own child so the
            // content isn't flipped. Transparent root image keeps the panel clickable.
            Image hit = rt.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            hit.raycastTarget = true;
            RectTransform frame = Rect("Frame", rt);
            Stretch(frame);
            frame.localScale = new Vector3(1f, -1f, 1f);
            frame.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            bg = frame.gameObject.AddComponent<Image>();
        }
        else
        {
            bg = rt.gameObject.AddComponent<Image>();
        }
        bg.raycastTarget = true;
        if (style != null) style.ApplyPanel(bg, palette);
        else bg.color = new Color(palette.background.r, palette.background.g, palette.background.b, 0.85f);
        AddScanlines(rt);
        return rt;
    }


    private Bar MakeBar(RectTransform parent, string title, Sprite iconSprite, Color colour, bool warnLow)
    {
        RectTransform row = Row(parent, 30f, 8f);
        var bar = new Bar { colour = colour, warnLow = warnLow };
        bar.icon = Icon(row, title + "Icon", iconSprite, colour, 26f);

        bar.root = Rect(title + "Bar", row);
        Flex(bar.root);
        Image back = Img(bar.root, "Back", null, new Color(0f, 0f, 0f, 0.5f), false);
        Stretch(back.rectTransform);
        bar.fill = Rect("Fill", bar.root);
        bar.fill.anchorMin = Vector2.zero;
        bar.fill.anchorMax = Vector2.one;
        bar.fill.offsetMin = new Vector2(2f, 2f) * scale;
        bar.fill.offsetMax = new Vector2(-2f, -2f) * scale;
        bar.fillImage = Img(bar.fill, "Colour", barFill, colour, true);
        Stretch(bar.fillImage.rectTransform);
        Image frame = Img(bar.root, "Frame", barFrame, palette.primary, true);
        Stretch(frame.rectTransform);

        // value written over the bar, with a dark copy behind it so it reads on any fill colour
        bar.valueShadow = Label(bar.root, "", 15f, new Color(0f, 0f, 0f, 0.85f), 0f);
        Stretch(bar.valueShadow.rectTransform);
        bar.valueShadow.rectTransform.offsetMin = new Vector2(1.5f, -1.5f) * scale;
        bar.valueShadow.rectTransform.offsetMax = new Vector2(1.5f, -1.5f) * scale;
        bar.valueShadow.alignment = TextAlignmentOptions.Center;
        bar.value = Label(bar.root, "", 15f, Color.white, 0f);
        Stretch(bar.value.rectTransform);
        bar.value.alignment = TextAlignmentOptions.Center;
        return bar;
    }


    private Image Icon(RectTransform parent, string name, Sprite sprite, Color colour, float size)
    {
        Image icon = Img(parent, name, sprite, colour, false);
        icon.preserveAspect = true;
        Size(icon.rectTransform, size, size);
        return icon;
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
        if (r == null) return;

        SetBar(hullBar, r.HullIntegrity, r.MaxHullIntegrity);
        SetBar(shieldBar, r.Shields, r.MaxShields);
        SetBar(fuelBar, r.Fuel, r.MaxFuel);

        int supplies = player.EventState != null ? player.EventState.GetCounter(EventKeys.Supplies) : 0;
        crewText.text = $"{r.Crew}/{r.MaxCrew}";
        suppliesText.text = supplies.ToString();
        if (lastCrew != int.MinValue && r.Crew != lastCrew) Float(crewIconImage.rectTransform, r.Crew - lastCrew);
        if (lastSupplies != int.MinValue && supplies != lastSupplies) Float(suppliesIconImage.rectTransform, supplies - lastSupplies);
        lastCrew = r.Crew;
        lastSupplies = supplies;
    }


    private void SetBar(Bar bar, float value, float max)
    {
        float t = Time.unscaledTime;
        float target = max > 0f ? Mathf.Clamp01(value / max) : 0f;
        bar.shown = bar.shown < 0f ? target : Mathf.MoveTowards(bar.shown, target, Time.unscaledDeltaTime * 1.5f);
        bar.fill.anchorMax = new Vector2(bar.shown, 1f);

        string text = $"{Mathf.RoundToInt(value)}/{Mathf.RoundToInt(max)}";
        bar.value.text = text;
        bar.valueShadow.text = text;

        if (!float.IsNaN(bar.last) && Mathf.RoundToInt(value) != Mathf.RoundToInt(bar.last))
        {
            Float(bar.root, Mathf.RoundToInt(value) - Mathf.RoundToInt(bar.last));
            bar.flashUntil = t + 0.25f;
        }
        bar.last = value;

        bool low = bar.warnLow && max > 0f && value / max < lowFraction;
        float pulse = 0.5f + 0.5f * Mathf.Sin(t * 7f);
        Color c = low ? Color.Lerp(bar.colour, lossColour, pulse) : bar.colour;
        if (t < bar.flashUntil) c = Color.Lerp(c, Color.white, 0.7f);
        bar.fillImage.color = c;
        bar.icon.color = low && pulse > 0.5f ? lossColour : bar.colour;
    }


    // ------------------------------------------------------ change floaters
    private void Float(RectTransform anchor, int delta)
    {
        if (delta == 0 || statusRoot == null) return;
        TMP_Text t = Label(statusRoot, delta > 0 ? $"+{delta}" : delta.ToString(), 16f,
                           delta > 0 ? gainColour : lossColour, 0f);
        t.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
        RectTransform rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(80f, 24f) * scale;
        rt.position = anchor.position;
        t.alignment = TextAlignmentOptions.Center;
        floaters.Add(new Floater { text = t, start = rt.anchoredPosition, born = Time.unscaledTime });
    }


    private void UpdateFloaters()
    {
        for (int i = floaters.Count - 1; i >= 0; i--)
        {
            Floater f = floaters[i];
            float age = Time.unscaledTime - f.born;
            if (age > 1.1f)
            {
                Destroy(f.text.gameObject);
                floaters.RemoveAt(i);
                continue;
            }
            f.text.rectTransform.anchoredPosition = f.start + new Vector2(0f, 34f * age) * scale;
            Color c = f.text.color;
            c.a = Mathf.Clamp01(1.4f - age * 1.3f);
            f.text.color = c;
        }
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
                Size(pip.rectTransform, 6f, 26f);
                // equal flexible slots across the whole row; the chevron keeps its shape
                Layout(pip.rectTransform).flexibleWidth = 1f;
                pip.preserveAspect = true;
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
        if (moveText != null) moveText.text = $"MOVE {current}/{max}";
        turnText.text = turnManager != null ? $"TURN {turnManager.CurrentTurn}" : "";

        bool oneTurn = planController == null || planController.Mode == MovementPlanController.PlanningMode.OneTurn;
        modeLabel.text = HoverHint(modeButton, oneTurn ? oneTurnShort : multiTurnShort, "M");
        goText.text = HoverHint(goButton, goLabel, "SPC");
        endText.text = HoverHint(endButton, endTurnLabel, "E");

        string route = RouteReadout(a, current, oneTurn);
        routeText.text = route;
        bool showTip = !string.IsNullOrEmpty(route) && (PointerOver(movePanel) || PointerOver(routeTip));
        if (routeTip.gameObject.activeSelf != showTip) routeTip.gameObject.SetActive(showTip);

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
        float t = Time.unscaledTime;

        // the bar is detection only: once hunted it stays full
        int turns = hunted ? enemySpawner.TurnsUntilNextWave : enemySpawner.TurnsUntilDetection;
        int total = Mathf.Max(1, enemySpawner.DetectionDelayTurns);
        float target = hunted ? 1f : 1f - Mathf.Clamp01((float)enemySpawner.TurnsUntilDetection / total);

        if (turns != lastThreatTurns || hunted != wasHunted)
        {
            if (lastThreatTurns != int.MinValue)
            {
                shakeUntil = t + 0.45f;                    // it just got closer
                if (hunted) clockPunchUntil = t + 0.3f;    // and a slice ticked in
            }
            lastThreatTurns = turns;
            wasHunted = hunted;
        }

        threatShown = threatShown < 0f ? target : Mathf.MoveTowards(threatShown, target, Time.unscaledDeltaTime * 0.8f);
        threatFill.anchorMax = new Vector2(Mathf.Max(0.02f, threatShown), 1f);

        // hunted: bar stays full and red but settles to a slow throb; panic
        // mode only kicks in again as the next wave gets close
        bool danger = turns <= dangerTurns;
        float heat = hunted ? 1f : threatShown;
        float pulseSpeed = hunted && !danger ? 3.5f : 2f + 8f * heat;
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
        threatCount.gameObject.SetActive(!hunted);
        if (!hunted)
        {
            threatCount.text = glitching ? Garble($"T-{turns}") : $"T-{turns}";
        }
        UpdateWaveClock(hunted, glow, pulse, glitching, t);

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


    // ------------------------------------------ hide while windows are open
    /// <summary>Why the panels should be hidden right now, or null if nothing is open.</summary>
    private string OpenWindowReason()
    {
        foreach (EventPanel p in eventPanels) if (p != null && p.IsOpen) return "event panel " + p.name;
        foreach (PlanetPicker p in planetPickers) if (p != null && p.IsOpen) return "planet picker " + p.name;
        foreach (DialoguePanelController p in dialoguePanels) if (p != null && p.IsDialogueOpen) return "dialogue " + p.name;
        foreach (CombatScreenController p in combatScreens) if (p != null && p.IsOpen) return "combat screen " + p.name;
        foreach (CombatEncounterController c in combatControllers) if (c != null && c.IsEncounterActive) return "combat encounter";
        foreach (WarpExitController w in warpScreens) if (w != null && w.IsBusy) return "warp / run-end screen";
        if (turnManager != null && turnManager.CurrentPhase == TurnPhase.Combat) return "combat phase";
        if (hideWhenInputBlocked && planController != null && turnManager != null &&
            turnManager.IsPlayerPhase && !planController.CanAcceptPlayerInput)
        {
            return "player input blocked (event / dialogue / hold)";
        }
        return null;
    }


    private void UpdatePanelVisibility()
    {
        float t = Time.unscaledTime;
        string reason = OpenWindowReason();
        if (reason != null) lastModalTime = t;
        bool wasHidden = panelsHidden;
        panelsHidden = t - lastModalTime < reshowDelaySeconds;
        if (logVisibility && panelsHidden != wasHidden)
        {
            Debug.Log(panelsHidden ? $"ShipHud: hiding panels - {reason}" : "ShipHud: showing panels - nothing open", this);
        }

        Fade(moveGroup, hideMovementPanel && panelsHidden);
        Fade(statusGroup, hideStatusPanel && panelsHidden);
        Fade(threatGroup, hideThreatPanel && panelsHidden);
    }


    private void Fade(CanvasGroup group, bool hidden)
    {
        if (group == null) return;
        float step = Time.unscaledDeltaTime / hideFadeSeconds;
        group.alpha = Mathf.MoveTowards(group.alpha, hidden ? 0f : 1f, step);
        bool usable = !hidden && group.alpha > 0.5f;
        group.interactable = usable;
        group.blocksRaycasts = usable;
    }


    /// <summary>Key hint only while the pointer is over that button.</summary>
    private string HoverHint(Button button, string label, string key)
    {
        return button != null && PointerOver((RectTransform)button.transform) ? Hint(label, key) : label;
    }


    private bool PointerOver(RectTransform rt)
    {
        if (rt == null || !rt.gameObject.activeInHierarchy || Mouse.current == null) return false;
        if (hudCanvas == null) hudCanvas = GetComponentInParent<Canvas>();
        Camera cam = hudCanvas != null && hudCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? hudCanvas.worldCamera : null;
        return RectTransformUtility.RectangleContainsScreenPoint(rt, Mouse.current.position.ReadValue(), cam);
    }


    private string Hint(string label, string key)
    {
        return showKeyHints ? $"{label} <size=60%><alpha=#88>{key}</alpha></size>" : label;
    }


    private string RouteReadout(MovementAllowance a, int current, bool oneTurn)
    {
        if (a == null || routePlanner == null) return "";
        if (routePlanner.HasPlannedRoute)
        {
            int cost = a.CalculatePathCost(routePlanner.PlannedPath);
            if (cost <= current) return $"ROUTE COST {cost}  //  {current - cost} LEFT AFTER";
            int turns = Mathf.Max(1, a.GetTurnSegmentBreakpoints(routePlanner.PlannedPath).Count);
            return oneTurn ? $"ROUTE COST {cost}  //  OUT OF REACH THIS TURN" : $"ROUTE COST {cost}  //  {turns} TURNS";
        }
        if (planController != null && planController.HasQueuedRemainder) return "ROUTE CONTINUES NEXT TURN";
        return "";
    }


    private void PressInventory()
    {
        onInventoryPressed?.Invoke();
        InventoryPressed?.Invoke();
    }


    private void UpdateWaveClock(bool hunted, Color glow, float pulse, bool glitching, float t)
    {
        waveClockRoot.gameObject.SetActive(hunted);
        if (!hunted) return;

        int slices = Mathf.Max(1, enemySpawner.TurnsBetweenWaves);
        EnsureWaveSlices(slices);
        slices = waveSlices.Count;

        int left = enemySpawner.TurnsUntilNextWave;
        bool capped = enemySpawner.AtEnemyCap;
        bool flashing = t < waveFlashUntil;
        int filled = flashing ? slices : Mathf.Clamp(slices - left, 0, slices);

        Color on = capped ? waveClockCapColour : glow;
        Color off = new Color(on.r, on.g, on.b, 0.18f);
        for (int i = 0; i < slices; i++)
        {
            Color c = i < filled ? on : off;
            // the next slice to fill breathes so you can see where it's up to
            if (!capped && !flashing && i == filled) c.a = Mathf.Lerp(0.18f, 0.55f, pulse);
            waveSlices[i].color = c;
        }

        string text = capped ? waveClockCapText : left.ToString();
        waveClockCount.text = glitching ? Garble(text) : text;
        waveClockCount.color = capped ? waveClockCapColour : glow;
        waveClockCount.fontSize = (capped ? 16f : 24f) * scale;

        float punch = t < clockPunchUntil ? 1f + 0.15f * ((clockPunchUntil - t) / 0.35f) : 1f;
        waveClockRoot.localScale = Vector3.one * punch;
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
