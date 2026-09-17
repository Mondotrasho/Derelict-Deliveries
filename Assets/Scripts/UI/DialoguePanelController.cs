using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class DialoguePanelController : MonoBehaviour
{
    public enum TerminalColourPreset
    {
        Amber,
        GreenPhosphor,
        IceBlue,
        PaperWhite,
        RedAlert,
        Violet,
        Custom
    }

    public enum PortraitVerticalAlignment
    {
        Top,
        Centre,
        Bottom
    }

    private enum LineKind
    {
        Left,
        Right,
        System
    }

    private enum PortraitSide
    {
        Left,
        Right
    }

    [Serializable]
    private class DialogueFile
    {
        public TerminalJson terminal;
        public CharacterPairJson characters;
        public TextColoursJson textColours;
        public TextColoursJson textColors;
        public List<CharacterColourJson> characterColours;
        public List<CharacterColourJson> characterColors;
        public TimingJson timing;
        public List<DialogueJsonLine> lines;
        public List<ChoiceSetJson> choices;
    }

    [Serializable]
    private class TerminalJson
    {
        public string title;
        public string status;
        public string systemFont;
        public float systemFontSize;
        public string backgroundColor;
        public string primaryColor;
        public string dimColor;
        public string scanlines;
    }

    [Serializable]
    private class CharacterPairJson
    {
        public string left;
        public string right;
    }

    [Serializable]
    private class TextColoursJson
    {
        public string left;
        public string right;
    }

    [Serializable]
    private class CharacterColourJson
    {
        public string id;
        public string colour;
        public string color;
    }

    [Serializable]
    private class TimingJson
    {
        public float charactersPerSecond;
        public float lineInterval;
        public string automaticMode;
    }

    [Serializable]
    private class DialogueJsonLine
    {
        public int tick;
        public string speaker;
        public string text;

        // Optional flow override. 0 keeps the old behaviour: continue to the next numeric tick.
        public int nextTick;
    }

    [Serializable]
    private class ChoiceSetJson
    {
        public int tick;
        public List<ChoiceOptionJson> options;
    }

    [Serializable]
    private class ChoiceOptionJson
    {
        // Button label. Also used as the spoken player line when line is empty.
        public string text;

        // Optional longer line written into the terminal as the left/player character.
        public string line;

        // 0 means use the normal next tick. Any other existing tick can be targeted.
        public int nextTick;
    }

    [Serializable]
    public class CharacterDefinition
    {
        public string characterId;
        public string displayName;
        public Sprite sprite;
        public string fontName;

        [Min(0f)]
        public float fontSize;

        public PortraitVerticalAlignment verticalAlignment = PortraitVerticalAlignment.Bottom;
        public bool flipWhenLeft;
        public bool flipWhenRight;

        [Range(0.1f, 1f)]
        public float portraitScale = 1f;

        public bool overrideTextColour;
        public Color textColour = Color.white;
    }

    [Serializable]
    public class CharacterSpriteMapping
    {
        public string characterId;
        public Sprite sprite;
    }

    [Serializable]
    public class FontSizeModifier
    {
        public TMP_FontAsset font;
        public float modifier;
    }

    private class DialogueLine
    {
        public int tick;
        public int order;
        public string speakerId;
        public string text;
        public int nextTick;
    }

    private class GeneratedLine
    {
        public LineKind kind;
        public string fullText;
        public RectTransform rect;
        public TextMeshProUGUI text;
        public float height;
        public TMP_FontAsset customFont;
        public float customFontSize;
        public bool useCustomStyle;
        public bool useBootStyle;
    }

    private class PortraitRuntime
    {
        public PortraitSide side;
        public RectTransform slotRect;
        public RectTransform imageRect;
        public Image image;
        public CanvasGroup canvasGroup;
        public CharacterDefinition definition;
        public Vector2 basePosition;
        public Vector3 baseScale = Vector3.one;
        public Coroutine appearCoroutine;
        public float instantPulseUntil;
    }

    [Header("Dialogue Source")]
    [SerializeField] private TextAsset dialogueJson;

    [Header("Character Definitions")]
    [Tooltip("Character presentation lives here rather than in each dialogue JSON.")]
    [SerializeField] private List<CharacterDefinition> characterDefinitions = new List<CharacterDefinition>
    {
        new CharacterDefinition { characterId = "charlie", displayName = "CHARLIE", fontName = "Kenney Future", fontSize = 30f, overrideTextColour = true, textColour = new Color32(0x67, 0xD8, 0xFF, 0xFF) },
        new CharacterDefinition { characterId = "pilot", displayName = "PILOT", fontName = "Kenney Future Narrow", fontSize = 27f, overrideTextColour = true, textColour = new Color32(0xFF, 0xB0, 0x00, 0xFF) },
        new CharacterDefinition { characterId = "engineer", displayName = "ENGINEER", fontName = "Kenney Rocket", fontSize = 24f, overrideTextColour = true, textColour = new Color32(0x42, 0xFF, 0x7B, 0xFF) },
        new CharacterDefinition { characterId = "cop", displayName = "COP", fontName = "Kenney Pixel Square", fontSize = 25f, overrideTextColour = true, textColour = new Color32(0x8E, 0xDB, 0xFF, 0xFF) },
        new CharacterDefinition { characterId = "bosscop", displayName = "BOSSCOP", fontName = "Kenney High Square", fontSize = 29f, overrideTextColour = true, textColour = new Color32(0xFF, 0x6B, 0x6B, 0xFF) },
        new CharacterDefinition { characterId = "clerk", displayName = "CLERK", fontName = "Kenney Mini Square Mono", fontSize = 25f, overrideTextColour = true, textColour = new Color32(0xFF, 0xD7, 0x6A, 0xFF) },
        new CharacterDefinition { characterId = "cultist", displayName = "CULTIST", fontName = "Kenney Blocks", fontSize = 28f, overrideTextColour = true, textColour = new Color32(0xD4, 0x8C, 0xFF, 0xFF) }
    };

    [SerializeField] private bool hidePortraitWhenNoSprite = true;

    [Header("Layout")]
    [Range(0.12f, 0.30f)]
    [SerializeField] private float sideAreaFraction = 0.20f;

    [Range(0.08f, 0.25f)]
    [SerializeField] private float controlsHeightFraction = 0.15f;

    [Range(0f, 0.08f)]
    [SerializeField] private float outerInsetFraction = 0.018f;

    [Range(0f, 0.08f)]
    [SerializeField] private float portraitPaddingFraction = 0.018f;

    [Range(0f, 0.10f)]
    [SerializeField] private float topInsetFraction = 0.025f;

    [Header("Panel Background")]
    [SerializeField] private Sprite panelBackgroundSprite;
    [SerializeField] private bool useSlicedPanelBackground = true;
    [SerializeField] private bool tintPanelBackgroundWithTerminalColour;
    [SerializeField] private Color panelBackgroundTint = Color.white;

    [Header("Scanlines")]
    [SerializeField] private bool defaultScanlines = true;

    [Min(0.25f)]
    [SerializeField] private float scanlineThickness = 2f;

    [Min(0.25f)]
    [SerializeField] private float scanlineGap = 3f;

    [Range(0f, 1f)]
    [SerializeField] private float scanlineOpacity = 0.14f;

    [Header("Portrait Effects")]
    [SerializeField] private bool playPortraitAppearEffect = true;

    [Min(0.05f)]
    [SerializeField] private float portraitAppearDuration = 0.55f;

    [Min(0f)]
    [SerializeField] private float portraitAppearJitter = 5f;

    [SerializeField] private bool animateSpeakingPortrait = true;

    [Range(0f, 0.20f)]
    [SerializeField] private float speakingScaleAmount = 0.045f;

    [Min(0.1f)]
    [SerializeField] private float speakingPulseSpeed = 7.5f;

    [Min(0f)]
    [SerializeField] private float instantSpeechPulseDuration = 0.22f;

    [Header("Terminal Colours")]
    [SerializeField] private TerminalColourPreset colourPreset = TerminalColourPreset.Amber;
    [SerializeField] private Color customBackgroundColour = new Color32(0x10, 0x09, 0x00, 0xFF);
    [SerializeField] private Color customPrimaryColour = new Color32(0xFF, 0xB0, 0x00, 0xFF);
    [SerializeField] private Color customDimColour = new Color32(0x9C, 0x6B, 0x00, 0xFF);

    [Header("Default Terminal Text")]
    [SerializeField] private string defaultTitle = "SYS://REMOTE_LINK";
    [SerializeField] private string defaultStatus = "CHANNEL OPEN";

    [Header("Default Fonts")]
    [SerializeField] private TMP_FontAsset fallbackSystemFont;
    [SerializeField] private TMP_FontAsset fallbackCharacterFont;

    [Header("Default Font Sizes")]
    [Min(1f)][SerializeField] private float fallbackSystemFontSize = 20f;
    [Min(1f)][SerializeField] private float fallbackCharacterFontSize = 28f;
    [Min(1f)][SerializeField] private float bootFontSize = 18f;
    [Min(1f)][SerializeField] private float buttonFontSize = 18f;

    [Header("Font Test")]
    [Min(1f)]
    [SerializeField] private float fontTestBaseSize = 28f;

    [SerializeField] private string fontTestSample = "ABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789 - The quick brown fox.";

    [Header("Typing")]
    [SerializeField] private bool typewriterEffect = true;
    [Min(1f)][SerializeField] private float defaultCharactersPerSecond = 35f;
    [Min(0f)][SerializeField] private float defaultLineInterval = 1f;
    [SerializeField] private bool automaticMode;

    [Header("Boot")]
    [SerializeField] private bool runBootOnStart;
    [SerializeField] private bool keepBootLogAfterBoot;
    [SerializeField] private bool hideCharacterImagesDuringBoot = true;

    [SerializeField]
    private List<string> bootLines = new List<string>
    {
        ":: TERMINAL COLD START",
        "POWER BUS............... OK",
        "MEMORY TEST............. OK",
        "I/O BUS................. OK",
        "CLOCK SYNC.............. OK"
    };

    [Min(1f)][SerializeField] private float defaultBootCharactersPerSecond = 70f;
    [Min(0.1f)][SerializeField] private float defaultConnectDuration = 2.5f;
    [Min(0f)][SerializeField] private float defaultReadyHold = 0.45f;
    [Min(0)][SerializeField] private int defaultBlankLinesBeforeDialogue = 2;

    [Header("Behaviour")]
    [SerializeField] private bool startEmpty = true;
    [SerializeField] private bool autoScrollToNewest = true;

    [Header("Dialogue Controls")]
    [Tooltip("Shows RESET in the fixed left control slot. Intended for testing/debug builds.")]
    [SerializeField] private bool showDebugResetButton;

    [Tooltip("Text on the wide button along the bottom of the window.")]
    [SerializeField] private string continueButtonText = "CONTINUE";

    [Range(0.35f, 0.75f)]
    [Tooltip("How much of the controls area is reserved for choice buttons above CONTINUE.")]
    [SerializeField] private float choiceAreaFraction = 0.58f;

    // Filled by the custom inspector so runtime builds can resolve a font by name.
    [HideInInspector][SerializeField] private List<TMP_FontAsset> fontLibrary = new List<TMP_FontAsset>();

    // The editor keeps one additive size modifier per discovered TMP font.
    [HideInInspector][SerializeField] private List<FontSizeModifier> fontSizeModifiers = new List<FontSizeModifier>();

    // Old prefab references are intentionally retained so replacing the script does not leave
    // the existing manually wired UI active on top of the generated layout.
    [FormerlySerializedAs("nextButton")]
    [HideInInspector][SerializeField] private Button legacyNextButton;

    [FormerlySerializedAs("resetButton")]
    [HideInInspector][SerializeField] private Button legacyResetButton;

    [FormerlySerializedAs("bootButton")]
    [HideInInspector][SerializeField] private Button legacyBootButton;

    [FormerlySerializedAs("leftCharacterImage")]
    [HideInInspector][SerializeField] private Image legacyLeftCharacterImage;

    [FormerlySerializedAs("rightCharacterImage")]
    [HideInInspector][SerializeField] private Image legacyRightCharacterImage;

    // Keep the original field name so existing character sprite assignments survive the refactor.
    [HideInInspector][SerializeField] private List<CharacterSpriteMapping> characterSprites = new List<CharacterSpriteMapping>();

    private DialogueFile loadedDefinition;
    private readonly List<DialogueLine> dialogue = new List<DialogueLine>();
    private readonly List<GeneratedLine> generatedLines = new List<GeneratedLine>();
    private readonly List<int> visitedTicks = new List<int>();
    private readonly Dictionary<int, string> selectedChoiceLines = new Dictionary<int, string>();

    private RectTransform panelRect;
    private RectTransform generatedRoot;
    private RectTransform leftPortraitSlot;
    private RectTransform rightPortraitSlot;
    private RectTransform frameRect;
    private RectTransform viewportRect;
    private RectTransform contentRect;
    private RectTransform controlsRect;
    private RectTransform choiceAreaRect;
    private RectTransform bottomBarRect;

    private Image panelImage;
    private ScrollRect scrollRect;
    private RawImage scanlineImage;
    private Texture2D scanlineTexture;

    private Image topBorder;
    private Image bottomBorder;
    private Image leftBorder;
    private Image rightBorder;
    private Image headerDivider;

    private TextMeshProUGUI titleText;
    private TextMeshProUGUI statusText;

    private Button generatedResetButton;
    private Button generatedNextButton;
    private Button generatedAutoButton;
    private TextMeshProUGUI generatedResetLabel;
    private TextMeshProUGUI generatedNextLabel;
    private TextMeshProUGUI generatedAutoLabel;
    private readonly List<Button> generatedChoiceButtons = new List<Button>();

    private PortraitRuntime leftPortrait;
    private PortraitRuntime rightPortrait;

    private string leftCharacterId = "left";
    private string rightCharacterId = "right";
    private string leftDisplayName = "";
    private string rightDisplayName = "";

    private CharacterDefinition leftDefinition;
    private CharacterDefinition rightDefinition;

    private string resolvedTitle;
    private string resolvedStatus;

    private TMP_FontAsset resolvedSystemFont;
    private TMP_FontAsset resolvedLeftFont;
    private TMP_FontAsset resolvedRightFont;

    private float resolvedSystemFontSize;
    private float resolvedLeftFontSize;
    private float resolvedRightFontSize;
    private float resolvedBootFontSize;
    private float resolvedButtonFontSize;

    private Color resolvedBackgroundColour;
    private Color resolvedPrimaryColour;
    private Color resolvedDimColour;
    private Color resolvedLeftColour;
    private Color resolvedRightColour;

    private bool resolvedScanlines;
    private bool activeAutomaticMode;

    private float activeCharactersPerSecond;
    private float activeLineInterval;

    private int currentTick = -1;
    private float automaticTimer;
    private Vector2 lastPanelSize;

    private bool ready;
    private bool presentationDirty;
    private bool layoutDirty;
    private bool scanlineDirty;
    private bool bootRequested;
    private bool fontTestMode;
    private bool waitingForChoice;
    private ChoiceSetJson activeChoiceSet;

    private Coroutine typingCoroutine;
    private bool isTyping;
    private List<DialogueLine> activeTickLines;
    private int activeTickLineIndex = -1;
    private GeneratedLine activeGeneratedLine;
    private LineKind? activeSpeakingSide;

    private Coroutine bootCoroutine;
    private bool isBooting;

    private void Awake()
    {
        panelRect = GetComponent<RectTransform>();
        if (panelRect == null)
        {
            Debug.LogError("DialoguePanelController requires a RectTransform.", this);
            enabled = false;
            return;
        }

        DisableLegacyUi();
        BuildGeneratedUi();
        SetupGeneratedButtons();

        if (dialogueJson != null)
        {
            ParseDialogue(dialogueJson.text);
        }
        else
        {
            ResolveSceneSettings();
            ApplyPresentation();
        }
    }

    private IEnumerator Start()
    {
        yield return null;

        Canvas.ForceUpdateCanvases();
        LayoutRebuilder.ForceRebuildLayoutImmediate(panelRect);
        Canvas.ForceUpdateCanvases();

        lastPanelSize = panelRect.rect.size;
        ready = true;

        LayoutAll();
        ApplyPresentation();
        ApplyCharacterPortraits(true);

        if (runBootOnStart || bootRequested)
        {
            RunBootSequence();
            yield break;
        }

        StartConversationState();
    }

    private void Update()
    {
        if (!ready)
        {
            return;
        }

        if (presentationDirty)
        {
            presentationDirty = false;
            ResolveSceneSettings();
            ApplyPresentation();
            ApplyCharacterPortraits(false);
            layoutDirty = true;
        }

        if (scanlineDirty)
        {
            scanlineDirty = false;
            RebuildScanlineTexture();
            layoutDirty = true;
        }

        if (layoutDirty)
        {
            layoutDirty = false;
            LayoutAll();
        }

        UpdateKeyboardControls();
        UpdateAutomaticDialogue();
        UpdatePortraitSpeakingAnimation();
    }

    private void LateUpdate()
    {
        if (!ready)
        {
            return;
        }

        CheckPanelSize();
    }

    private void OnValidate()
    {
        fallbackSystemFontSize = Mathf.Max(1f, fallbackSystemFontSize);
        fallbackCharacterFontSize = Mathf.Max(1f, fallbackCharacterFontSize);
        bootFontSize = Mathf.Max(1f, bootFontSize);
        buttonFontSize = Mathf.Max(1f, buttonFontSize);
        fontTestBaseSize = Mathf.Max(1f, fontTestBaseSize);
        defaultCharactersPerSecond = Mathf.Max(1f, defaultCharactersPerSecond);
        defaultLineInterval = Mathf.Max(0f, defaultLineInterval);
        defaultBootCharactersPerSecond = Mathf.Max(1f, defaultBootCharactersPerSecond);
        defaultConnectDuration = Mathf.Max(0.1f, defaultConnectDuration);
        defaultReadyHold = Mathf.Max(0f, defaultReadyHold);
        defaultBlankLinesBeforeDialogue = Mathf.Max(0, defaultBlankLinesBeforeDialogue);

        sideAreaFraction = Mathf.Clamp(sideAreaFraction, 0.12f, 0.30f);
        controlsHeightFraction = Mathf.Clamp(controlsHeightFraction, 0.08f, 0.25f);
        outerInsetFraction = Mathf.Clamp(outerInsetFraction, 0f, 0.08f);
        portraitPaddingFraction = Mathf.Clamp(portraitPaddingFraction, 0f, 0.08f);
        topInsetFraction = Mathf.Clamp(topInsetFraction, 0f, 0.10f);
        choiceAreaFraction = Mathf.Clamp(choiceAreaFraction, 0.35f, 0.75f);

        scanlineThickness = Mathf.Max(0.25f, scanlineThickness);
        scanlineGap = Mathf.Max(0.25f, scanlineGap);
        scanlineOpacity = Mathf.Clamp01(scanlineOpacity);

        portraitAppearDuration = Mathf.Max(0.05f, portraitAppearDuration);
        portraitAppearJitter = Mathf.Max(0f, portraitAppearJitter);
        speakingScaleAmount = Mathf.Clamp(speakingScaleAmount, 0f, 0.20f);
        speakingPulseSpeed = Mathf.Max(0.1f, speakingPulseSpeed);
        instantSpeechPulseDuration = Mathf.Max(0f, instantSpeechPulseDuration);

        if (characterDefinitions != null)
        {
            foreach (CharacterDefinition definition in characterDefinitions)
            {
                if (definition == null)
                {
                    continue;
                }

                definition.fontSize = Mathf.Max(0f, definition.fontSize);
                definition.portraitScale = Mathf.Clamp(definition.portraitScale, 0.1f, 1f);
            }
        }

        if (Application.isPlaying)
        {
            presentationDirty = true;
            scanlineDirty = true;
            layoutDirty = true;
        }
    }

    private void OnDestroy()
    {
        RemoveGeneratedButtonListeners();
        StopTyping();
        StopBootSequence();
        StopPortraitEffect(leftPortrait);
        StopPortraitEffect(rightPortrait);

        if (scanlineTexture != null)
        {
            Destroy(scanlineTexture);
        }
    }

    // ---------------------------------------------------------------------
    // Loading
    // ---------------------------------------------------------------------

    public void LoadDialogue(TextAsset jsonFile)
    {
        if (jsonFile == null)
        {
            return;
        }

        dialogueJson = jsonFile;
        ParseDialogue(jsonFile.text);

        if (ready)
        {
            StartConversationState();
        }
    }

    public void LoadDialogueAndBoot(TextAsset jsonFile)
    {
        if (jsonFile == null)
        {
            return;
        }

        dialogueJson = jsonFile;
        ParseDialogue(jsonFile.text);

        if (ready)
        {
            RunBootSequence();
        }
        else
        {
            bootRequested = true;
        }
    }

    public void LoadDialogueFromJson(string json)
    {
        ParseDialogue(json);

        if (ready)
        {
            StartConversationState();
        }
    }

    public void LoadDialogueFromJsonAndBoot(string json)
    {
        ParseDialogue(json);

        if (ready)
        {
            RunBootSequence();
        }
        else
        {
            bootRequested = true;
        }
    }

    private void ParseDialogue(string json)
    {
        dialogue.Clear();
        loadedDefinition = null;

        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                loadedDefinition = JsonUtility.FromJson<DialogueFile>(json);
            }
            catch (Exception exception)
            {
                Debug.LogError("Could not read dialogue JSON:\n" + exception.Message, this);
            }
        }

        if (loadedDefinition != null && loadedDefinition.lines != null)
        {
            int order = 0;

            foreach (DialogueJsonLine jsonLine in loadedDefinition.lines)
            {
                if (jsonLine == null)
                {
                    continue;
                }

                dialogue.Add(new DialogueLine
                {
                    tick = jsonLine.tick,
                    order = order++,
                    speakerId = jsonLine.speaker ?? "",
                    text = jsonLine.text ?? "",
                    nextTick = jsonLine.nextTick
                });
            }

            dialogue.Sort((a, b) =>
            {
                int tickCompare = a.tick.CompareTo(b.tick);
                return tickCompare != 0 ? tickCompare : a.order.CompareTo(b.order);
            });
        }

        ResolveSceneSettings();
        ApplyPresentation();
        ApplyCharacterPortraits(true);
        layoutDirty = true;
    }

    private void StartConversationState()
    {
        StopTyping();
        StopBootSequence();

        fontTestMode = false;
        automaticTimer = 0f;
        currentTick = -1;
        activeSpeakingSide = null;
        visitedTicks.Clear();
        selectedChoiceLines.Clear();
        HideChoices();

        if (statusText != null)
        {
            statusText.text = resolvedStatus ?? "";
        }

        ClearGeneratedLines();
        CalculateDialogueLayout();
        ScrollToBottom();
        SetControlsInteractable(true);

        if (startEmpty)
        {
            return;
        }

        int firstTick = FindNextDialogueTick(-1);
        if (firstTick >= 0)
        {
            currentTick = firstTick;
            RecordVisitedTick(currentTick);
            StartCurrentTick();
        }
    }

    // ---------------------------------------------------------------------
    // Scene settings
    // ---------------------------------------------------------------------

    private void ResolveSceneSettings()
    {
        GetPresetColours(colourPreset, out Color presetBackground, out Color presetPrimary, out Color presetDim);

        TerminalJson terminal = loadedDefinition != null ? loadedDefinition.terminal : null;
        CharacterPairJson characters = loadedDefinition != null ? loadedDefinition.characters : null;
        TimingJson timing = loadedDefinition != null ? loadedDefinition.timing : null;
        TextColoursJson textColours = loadedDefinition != null
            ? (loadedDefinition.textColours ?? loadedDefinition.textColors)
            : null;

        List<CharacterColourJson> characterColours = loadedDefinition != null
            ? (loadedDefinition.characterColours ?? loadedDefinition.characterColors)
            : null;

        leftCharacterId = !string.IsNullOrWhiteSpace(characters != null ? characters.left : null)
            ? characters.left
            : "left";

        rightCharacterId = !string.IsNullOrWhiteSpace(characters != null ? characters.right : null)
            ? characters.right
            : "right";

        leftDefinition = FindCharacterDefinition(leftCharacterId);
        rightDefinition = FindCharacterDefinition(rightCharacterId);

        leftDisplayName = leftDefinition != null && !string.IsNullOrWhiteSpace(leftDefinition.displayName)
            ? leftDefinition.displayName
            : leftCharacterId.ToUpperInvariant();

        rightDisplayName = rightDefinition != null && !string.IsNullOrWhiteSpace(rightDefinition.displayName)
            ? rightDefinition.displayName
            : rightCharacterId.ToUpperInvariant();

        resolvedTitle = terminal != null && terminal.title != null ? terminal.title : defaultTitle;
        resolvedStatus = terminal != null && terminal.status != null ? terminal.status : defaultStatus;

        resolvedSystemFont = ResolveFont(terminal != null ? terminal.systemFont : null, fallbackSystemFont);
        resolvedLeftFont = ResolveCharacterFont(leftDefinition, fallbackCharacterFont);
        resolvedRightFont = ResolveCharacterFont(rightDefinition, fallbackCharacterFont);

        float requestedSystemSize = terminal != null && terminal.systemFontSize > 0f
            ? terminal.systemFontSize
            : fallbackSystemFontSize;

        float requestedLeftSize = leftDefinition != null && leftDefinition.fontSize > 0f
            ? leftDefinition.fontSize
            : fallbackCharacterFontSize;

        float requestedRightSize = rightDefinition != null && rightDefinition.fontSize > 0f
            ? rightDefinition.fontSize
            : fallbackCharacterFontSize;

        resolvedSystemFontSize = ResolveFontSize(resolvedSystemFont, requestedSystemSize);
        resolvedLeftFontSize = ResolveFontSize(resolvedLeftFont, requestedLeftSize);
        resolvedRightFontSize = ResolveFontSize(resolvedRightFont, requestedRightSize);
        resolvedBootFontSize = ResolveFontSize(resolvedSystemFont, bootFontSize);
        resolvedButtonFontSize = ResolveFontSize(resolvedSystemFont, buttonFontSize);

        resolvedBackgroundColour = ParseColour(terminal != null ? terminal.backgroundColor : null, presetBackground);
        resolvedPrimaryColour = ParseColour(terminal != null ? terminal.primaryColor : null, presetPrimary);
        resolvedDimColour = ParseColour(terminal != null ? terminal.dimColor : null, presetDim);

        resolvedLeftColour = ResolveCharacterTextColour(
            textColours != null ? textColours.left : null,
            FindDialogueCharacterColour(characterColours, leftCharacterId),
            leftDefinition,
            resolvedPrimaryColour);

        resolvedRightColour = ResolveCharacterTextColour(
            textColours != null ? textColours.right : null,
            FindDialogueCharacterColour(characterColours, rightCharacterId),
            rightDefinition,
            resolvedPrimaryColour);

        resolvedScanlines = defaultScanlines;
        if (TryParseOptionalBool(terminal != null ? terminal.scanlines : null, out bool scanlinesValue))
        {
            resolvedScanlines = scanlinesValue;
        }

        activeCharactersPerSecond = timing != null && timing.charactersPerSecond > 0f
            ? timing.charactersPerSecond
            : defaultCharactersPerSecond;

        activeLineInterval = timing != null && timing.lineInterval > 0f
            ? timing.lineInterval
            : defaultLineInterval;

        activeAutomaticMode = automaticMode;
        if (TryParseOptionalBool(timing != null ? timing.automaticMode : null, out bool autoValue))
        {
            activeAutomaticMode = autoValue;
        }

        UpdateAutoButtonLabel();
    }

    private CharacterDefinition FindCharacterDefinition(string characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId) || characterDefinitions == null)
        {
            return null;
        }

        foreach (CharacterDefinition definition in characterDefinitions)
        {
            if (definition != null &&
                string.Equals(definition.characterId, characterId, StringComparison.OrdinalIgnoreCase))
            {
                return definition;
            }
        }

        return null;
    }

    private Sprite ResolveCharacterSprite(CharacterDefinition definition, string characterId)
    {
        if (definition != null && definition.sprite != null)
        {
            return definition.sprite;
        }

        return FindLegacyCharacterSprite(characterId);
    }

    private Sprite FindLegacyCharacterSprite(string characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId) || characterSprites == null)
        {
            return null;
        }

        foreach (CharacterSpriteMapping mapping in characterSprites)
        {
            if (mapping != null &&
                mapping.sprite != null &&
                string.Equals(mapping.characterId, characterId, StringComparison.OrdinalIgnoreCase))
            {
                return mapping.sprite;
            }
        }

        return null;
    }

    private TMP_FontAsset ResolveCharacterFont(CharacterDefinition definition, TMP_FontAsset fallback)
    {
        if (definition == null)
        {
            return fallback;
        }

        return ResolveFont(definition.fontName, fallback);
    }

    private string FindDialogueCharacterColour(List<CharacterColourJson> colours, string characterId)
    {
        if (colours == null || string.IsNullOrWhiteSpace(characterId))
        {
            return null;
        }

        foreach (CharacterColourJson entry in colours)
        {
            if (entry == null || !string.Equals(entry.id, characterId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return !string.IsNullOrWhiteSpace(entry.colour) ? entry.colour : entry.color;
        }

        return null;
    }

    private Color ResolveCharacterTextColour(
        string sideOverride,
        string characterOverride,
        CharacterDefinition definition,
        Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(sideOverride) &&
            ColorUtility.TryParseHtmlString(sideOverride, out Color sideColour))
        {
            return sideColour;
        }

        if (!string.IsNullOrWhiteSpace(characterOverride) &&
            ColorUtility.TryParseHtmlString(characterOverride, out Color characterColour))
        {
            return characterColour;
        }

        if (definition != null && definition.overrideTextColour)
        {
            return definition.textColour;
        }

        return fallback;
    }

    private void GetPresetColours(
        TerminalColourPreset preset,
        out Color background,
        out Color primary,
        out Color dim)
    {
        switch (preset)
        {
            case TerminalColourPreset.GreenPhosphor:
                background = HtmlColour("#001008");
                primary = HtmlColour("#42FF7B");
                dim = HtmlColour("#187A3D");
                break;

            case TerminalColourPreset.IceBlue:
                background = HtmlColour("#031018");
                primary = HtmlColour("#67D8FF");
                dim = HtmlColour("#2B718C");
                break;

            case TerminalColourPreset.PaperWhite:
                background = HtmlColour("#111111");
                primary = HtmlColour("#E8E8DE");
                dim = HtmlColour("#85857F");
                break;

            case TerminalColourPreset.RedAlert:
                background = HtmlColour("#160203");
                primary = HtmlColour("#FF4A4A");
                dim = HtmlColour("#8C2424");
                break;

            case TerminalColourPreset.Violet:
                background = HtmlColour("#0E0616");
                primary = HtmlColour("#D48CFF");
                dim = HtmlColour("#71458A");
                break;

            case TerminalColourPreset.Custom:
                background = customBackgroundColour;
                primary = customPrimaryColour;
                dim = customDimColour;
                break;

            default:
                background = HtmlColour("#100900");
                primary = HtmlColour("#FFB000");
                dim = HtmlColour("#9C6B00");
                break;
        }
    }

    private Color HtmlColour(string value)
    {
        return ColorUtility.TryParseHtmlString(value, out Color colour) ? colour : Color.white;
    }

    private Color ParseColour(string value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return ColorUtility.TryParseHtmlString(value, out Color colour) ? colour : fallback;
    }

    private bool TryParseOptionalBool(string value, out bool result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = false;
            return false;
        }

        return bool.TryParse(value, out result);
    }

    private TMP_FontAsset ResolveFont(string requestedName, TMP_FontAsset fallback)
    {
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            string requestedKey = NormaliseFontName(requestedName);

            foreach (TMP_FontAsset font in fontLibrary)
            {
                if (font == null)
                {
                    continue;
                }

                if (NormaliseFontName(font.name) == requestedKey)
                {
                    return font;
                }
            }

            Debug.LogWarning(
                $"Dialogue requested TMP font '{requestedName}', but no matching TMP font was found under Assets/Fonts. Using the fallback.",
                this);
        }

        return fallback;
    }

    private float ResolveFontSize(TMP_FontAsset font, float requestedSize)
    {
        return Mathf.Max(1f, requestedSize + FindFontSizeModifier(font));
    }

    private float FindFontSizeModifier(TMP_FontAsset font)
    {
        if (font == null || fontSizeModifiers == null)
        {
            return 0f;
        }

        foreach (FontSizeModifier fontSizeModifier in fontSizeModifiers)
        {
            if (fontSizeModifier != null && fontSizeModifier.font == font)
            {
                return fontSizeModifier.modifier;
            }
        }

        return 0f;
    }

    private string NormaliseFontName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalised = value.Trim();

        if (normalised.EndsWith(" SDF", StringComparison.OrdinalIgnoreCase))
        {
            normalised = normalised.Substring(0, normalised.Length - 4);
        }
        else if (normalised.EndsWith("_SDF", StringComparison.OrdinalIgnoreCase) ||
                 normalised.EndsWith("-SDF", StringComparison.OrdinalIgnoreCase))
        {
            normalised = normalised.Substring(0, normalised.Length - 4);
        }

        char[] buffer = new char[normalised.Length];
        int count = 0;

        foreach (char character in normalised)
        {
            if (char.IsWhiteSpace(character) || character == '_' || character == '-')
            {
                continue;
            }

            buffer[count++] = char.ToLowerInvariant(character);
        }

        return new string(buffer, 0, count);
    }

    // ---------------------------------------------------------------------
    // Generated UI
    // ---------------------------------------------------------------------

    private void DisableLegacyUi()
    {
        DisableLegacyObject(legacyNextButton != null ? legacyNextButton.gameObject : null);
        DisableLegacyObject(legacyResetButton != null ? legacyResetButton.gameObject : null);
        DisableLegacyObject(legacyBootButton != null ? legacyBootButton.gameObject : null);
        DisableLegacyObject(legacyLeftCharacterImage != null ? legacyLeftCharacterImage.gameObject : null);
        DisableLegacyObject(legacyRightCharacterImage != null ? legacyRightCharacterImage.gameObject : null);
    }

    private void DisableLegacyObject(GameObject target)
    {
        if (target != null && target != gameObject)
        {
            target.SetActive(false);
        }
    }

    private void BuildGeneratedUi()
    {
        panelImage = GetComponent<Image>();
        if (panelImage == null)
        {
            panelImage = gameObject.AddComponent<Image>();
        }
        panelImage.raycastTarget = true;

        ScrollRect legacyRootScrollRect = GetComponent<ScrollRect>();
        if (legacyRootScrollRect != null)
        {
            legacyRootScrollRect.enabled = false;
        }

        Transform oldGenerated = transform.Find("__DialogueGenerated");
        if (oldGenerated != null)
        {
            oldGenerated.gameObject.SetActive(false);
            Destroy(oldGenerated.gameObject);
        }

        generatedRoot = CreateRect("__DialogueGenerated", transform);
        Stretch(generatedRoot);

        leftPortraitSlot = CreateRect("LeftPortraitArea", generatedRoot);
        rightPortraitSlot = CreateRect("RightPortraitArea", generatedRoot);
        frameRect = CreateRect("TerminalFrame", generatedRoot);
        controlsRect = CreateRect("ControlsArea", generatedRoot);
        choiceAreaRect = CreateRect("ChoiceArea", controlsRect);
        bottomBarRect = CreateRect("BottomBar", controlsRect);

        Image frameRaycastImage = frameRect.gameObject.AddComponent<Image>();
        frameRaycastImage.color = new Color(0f, 0f, 0f, 0f);
        frameRaycastImage.raycastTarget = true;
        frameRect.gameObject.AddComponent<RectMask2D>();

        viewportRect = CreateRect("Viewport", frameRect);
        viewportRect.gameObject.AddComponent<RectMask2D>();

        contentRect = CreateRect("DialogueContent", viewportRect);
        contentRect.anchorMin = new Vector2(0f, 1f);
        contentRect.anchorMax = new Vector2(1f, 1f);
        contentRect.pivot = new Vector2(0.5f, 1f);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;

        titleText = CreateText("TerminalTitle", frameRect);
        statusText = CreateText("TerminalStatus", frameRect);
        titleText.alignment = TextAlignmentOptions.Left;
        statusText.alignment = TextAlignmentOptions.Right;
        titleText.textWrappingMode = TextWrappingModes.NoWrap;
        statusText.textWrappingMode = TextWrappingModes.NoWrap;
        titleText.overflowMode = TextOverflowModes.Truncate;
        statusText.overflowMode = TextOverflowModes.Truncate;

        scanlineImage = CreateRawImage("Scanlines", frameRect);
        Stretch(scanlineImage.rectTransform);
        scanlineImage.raycastTarget = false;

        topBorder = CreateImage("TopBorder", frameRect);
        bottomBorder = CreateImage("BottomBorder", frameRect);
        leftBorder = CreateImage("LeftBorder", frameRect);
        rightBorder = CreateImage("RightBorder", frameRect);
        headerDivider = CreateImage("HeaderDivider", frameRect);

        // Scanlines are deliberately above viewport/title/status so the dark bands also pass over text.
        scanlineImage.transform.SetAsLastSibling();
        topBorder.transform.SetAsLastSibling();
        bottomBorder.transform.SetAsLastSibling();
        leftBorder.transform.SetAsLastSibling();
        rightBorder.transform.SetAsLastSibling();
        headerDivider.transform.SetAsLastSibling();

        scrollRect = frameRect.gameObject.AddComponent<ScrollRect>();
        scrollRect.viewport = viewportRect;
        scrollRect.content = contentRect;
        scrollRect.horizontal = false;
        scrollRect.vertical = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.inertia = true;

        leftPortrait = CreatePortraitRuntime("LeftPortrait", leftPortraitSlot, PortraitSide.Left);
        rightPortrait = CreatePortraitRuntime("RightPortrait", rightPortraitSlot, PortraitSide.Right);

        generatedResetButton = CreateGeneratedButton("ResetButton", bottomBarRect, "RESET", out generatedResetLabel);
        generatedNextButton = CreateGeneratedButton("NextButton", bottomBarRect, continueButtonText, out generatedNextLabel);
        generatedAutoButton = CreateGeneratedButton("AutoButton", bottomBarRect, "AUTO: OFF", out generatedAutoLabel);

        RebuildScanlineTexture();
    }

    private PortraitRuntime CreatePortraitRuntime(string objectName, RectTransform parent, PortraitSide side)
    {
        GameObject obj = new GameObject(objectName, typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
        obj.transform.SetParent(parent, false);

        Image image = obj.GetComponent<Image>();
        image.raycastTarget = false;
        image.preserveAspect = true;
        image.type = Image.Type.Simple;

        CanvasGroup canvasGroup = obj.GetComponent<CanvasGroup>();
        canvasGroup.blocksRaycasts = false;
        canvasGroup.interactable = false;

        return new PortraitRuntime
        {
            side = side,
            slotRect = parent,
            imageRect = obj.GetComponent<RectTransform>(),
            image = image,
            canvasGroup = canvasGroup
        };
    }

    private RectTransform CreateRect(string objectName, Transform parent)
    {
        GameObject obj = new GameObject(objectName, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        return obj.GetComponent<RectTransform>();
    }

    private TextMeshProUGUI CreateText(string objectName, Transform parent)
    {
        GameObject obj = new GameObject(objectName, typeof(RectTransform), typeof(TextMeshProUGUI));
        obj.transform.SetParent(parent, false);

        TextMeshProUGUI text = obj.GetComponent<TextMeshProUGUI>();
        text.raycastTarget = false;
        return text;
    }

    private Image CreateImage(string objectName, Transform parent)
    {
        GameObject obj = new GameObject(objectName, typeof(RectTransform), typeof(Image));
        obj.transform.SetParent(parent, false);

        Image image = obj.GetComponent<Image>();
        image.raycastTarget = false;
        return image;
    }

    private RawImage CreateRawImage(string objectName, Transform parent)
    {
        GameObject obj = new GameObject(objectName, typeof(RectTransform), typeof(RawImage));
        obj.transform.SetParent(parent, false);
        return obj.GetComponent<RawImage>();
    }

    private Button CreateGeneratedButton(
        string objectName,
        Transform parent,
        string label,
        out TextMeshProUGUI labelText)
    {
        GameObject obj = new GameObject(objectName, typeof(RectTransform), typeof(Image), typeof(Button));
        obj.transform.SetParent(parent, false);

        Image image = obj.GetComponent<Image>();
        image.raycastTarget = true;

        Button button = obj.GetComponent<Button>();
        button.targetGraphic = image;

        labelText = CreateText("Label", obj.transform);
        Stretch(labelText.rectTransform);
        labelText.text = label;
        labelText.alignment = TextAlignmentOptions.Center;
        labelText.textWrappingMode = TextWrappingModes.NoWrap;
        labelText.overflowMode = TextOverflowModes.Truncate;

        return button;
    }

    private void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private void RebuildScanlineTexture()
    {
        if (scanlineImage == null)
        {
            return;
        }

        if (scanlineTexture != null)
        {
            Destroy(scanlineTexture);
        }

        // The actual spacing is controlled by UV repetition in LayoutTerminal.
        // A taller texture keeps the thickness ratio smooth even when the user uses fractional values.
        const int textureHeight = 64;
        scanlineTexture = new Texture2D(1, textureHeight, TextureFormat.RGBA32, false);
        scanlineTexture.wrapMode = TextureWrapMode.Repeat;
        scanlineTexture.filterMode = FilterMode.Bilinear;

        float period = Mathf.Max(0.5f, scanlineThickness + scanlineGap);
        float darkFraction = Mathf.Clamp01(scanlineThickness / period);

        for (int y = 0; y < textureHeight; y++)
        {
            float normalized = (y + 0.5f) / textureHeight;
            float alpha = normalized <= darkFraction ? scanlineOpacity : 0f;
            scanlineTexture.SetPixel(0, y, new Color(0f, 0f, 0f, alpha));
        }

        scanlineTexture.Apply();
        scanlineImage.texture = scanlineTexture;
        scanlineImage.color = Color.white;
    }

    private void ApplyPresentation()
    {
        if (panelImage == null)
        {
            return;
        }

        if (panelBackgroundSprite != null)
        {
            panelImage.sprite = panelBackgroundSprite;
            panelImage.type = useSlicedPanelBackground ? Image.Type.Sliced : Image.Type.Simple;
            panelImage.fillCenter = true;
            panelImage.color = tintPanelBackgroundWithTerminalColour
                ? panelBackgroundTint * resolvedBackgroundColour
                : panelBackgroundTint;
        }
        else
        {
            panelImage.sprite = null;
            panelImage.type = Image.Type.Simple;
            panelImage.color = resolvedBackgroundColour;
        }

        titleText.text = resolvedTitle ?? "";
        if (!fontTestMode)
        {
            statusText.text = resolvedStatus ?? "";
        }

        titleText.font = resolvedSystemFont;
        statusText.font = resolvedSystemFont;
        titleText.fontSize = resolvedSystemFontSize;
        statusText.fontSize = resolvedSystemFontSize;
        titleText.color = resolvedPrimaryColour;
        statusText.color = resolvedDimColour;

        topBorder.color = resolvedPrimaryColour;
        bottomBorder.color = resolvedPrimaryColour;
        leftBorder.color = resolvedPrimaryColour;
        rightBorder.color = resolvedPrimaryColour;
        headerDivider.color = resolvedDimColour;

        scanlineImage.gameObject.SetActive(resolvedScanlines);
        scrollRect.scrollSensitivity = Mathf.Max(resolvedLeftFontSize, resolvedRightFontSize) * 1.25f;

        ConfigureButtonVisual(generatedResetButton, generatedResetLabel);
        ConfigureButtonVisual(generatedNextButton, generatedNextLabel);
        ConfigureButtonVisual(generatedAutoButton, generatedAutoLabel);
        UpdateAutoButtonLabel();
        UpdateContinueButtonLabel();
        if (generatedResetButton != null) generatedResetButton.gameObject.SetActive(showDebugResetButton);

        foreach (GeneratedLine line in generatedLines)
        {
            ConfigureGeneratedLine(line);
        }

        if (ready)
        {
            CalculateDialogueLayout();
            if (autoScrollToNewest)
            {
                ScrollToBottom();
            }
        }
    }

    private void ConfigureButtonVisual(Button button, TextMeshProUGUI label)
    {
        if (button == null || label == null)
        {
            return;
        }

        Image image = button.GetComponent<Image>();
        Color baseColour = resolvedDimColour;
        baseColour.a = 0.22f;
        image.color = baseColour;

        ColorBlock colours = button.colors;
        colours.normalColor = Color.white;
        colours.highlightedColor = new Color(1f, 1f, 1f, 1.35f);
        colours.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        colours.selectedColor = Color.white;
        colours.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
        colours.colorMultiplier = 1f;
        button.colors = colours;

        label.font = resolvedSystemFont;
        label.fontSize = resolvedButtonFontSize;
        label.color = resolvedPrimaryColour;
    }

    // ---------------------------------------------------------------------
    // Layout
    // ---------------------------------------------------------------------

    private void LayoutAll()
    {
        Canvas.ForceUpdateCanvases();

        float panelWidth = panelRect.rect.width;
        float panelHeight = panelRect.rect.height;
        if (panelWidth <= 1f || panelHeight <= 1f)
        {
            return;
        }

        float safeSideFraction = Mathf.Clamp(sideAreaFraction, 0.12f, 0.30f);
        float safeControlsFraction = Mathf.Clamp(controlsHeightFraction, 0.08f, 0.25f);
        float topInset = panelHeight * Mathf.Clamp(topInsetFraction, 0f, 0.10f);
        float centreMin = safeSideFraction;
        float centreMax = 1f - safeSideFraction;

        leftPortraitSlot.anchorMin = new Vector2(0f, safeControlsFraction);
        leftPortraitSlot.anchorMax = new Vector2(centreMin, 1f);
        leftPortraitSlot.offsetMin = Vector2.zero;
        leftPortraitSlot.offsetMax = new Vector2(0f, -topInset);

        frameRect.anchorMin = new Vector2(centreMin, safeControlsFraction);
        frameRect.anchorMax = new Vector2(centreMax, 1f);

        float outerInset = Mathf.Max(3f, Mathf.Min(panelWidth, panelHeight) * outerInsetFraction);
        frameRect.offsetMin = new Vector2(outerInset, outerInset);
        frameRect.offsetMax = new Vector2(-outerInset, -(outerInset + topInset));

        rightPortraitSlot.anchorMin = new Vector2(centreMax, safeControlsFraction);
        rightPortraitSlot.anchorMax = new Vector2(1f, 1f);
        rightPortraitSlot.offsetMin = Vector2.zero;
        rightPortraitSlot.offsetMax = new Vector2(0f, -topInset);

        controlsRect.anchorMin = Vector2.zero;
        controlsRect.anchorMax = new Vector2(1f, safeControlsFraction);
        controlsRect.offsetMin = Vector2.zero;
        controlsRect.offsetMax = Vector2.zero;

        LayoutTerminal();
        LayoutPortrait(leftPortrait);
        LayoutPortrait(rightPortrait);
        LayoutControls();
        CalculateDialogueLayout();

        Canvas.ForceUpdateCanvases();
    }

    private void LayoutTerminal()
    {
        float frameWidth = frameRect.rect.width;
        float frameHeight = frameRect.rect.height;
        if (frameWidth <= 1f || frameHeight <= 1f)
        {
            return;
        }

        float borderThickness = Mathf.Max(1f, frameHeight * 0.004f);
        float innerInset = Mathf.Max(borderThickness * 4f, frameWidth * 0.018f);
        float headerHeight = Mathf.Max(resolvedSystemFontSize * 1.8f, frameHeight * 0.095f);

        SetTopEdge(topBorder.rectTransform, borderThickness);
        SetBottomEdge(bottomBorder.rectTransform, borderThickness);
        SetLeftEdge(leftBorder.rectTransform, borderThickness);
        SetRightEdge(rightBorder.rectTransform, borderThickness);

        RectTransform titleRect = titleText.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(0.68f, 1f);
        titleRect.pivot = new Vector2(0f, 1f);
        titleRect.offsetMin = new Vector2(innerInset, -headerHeight);
        titleRect.offsetMax = new Vector2(-innerInset, -borderThickness);

        RectTransform statusRect = statusText.rectTransform;
        statusRect.anchorMin = new Vector2(0.55f, 1f);
        statusRect.anchorMax = new Vector2(1f, 1f);
        statusRect.pivot = new Vector2(1f, 1f);
        statusRect.offsetMin = new Vector2(innerInset, -headerHeight);
        statusRect.offsetMax = new Vector2(-innerInset, -borderThickness);

        RectTransform dividerRect = headerDivider.rectTransform;
        dividerRect.anchorMin = new Vector2(0f, 1f);
        dividerRect.anchorMax = new Vector2(1f, 1f);
        dividerRect.pivot = new Vector2(0.5f, 1f);
        dividerRect.anchoredPosition = new Vector2(0f, -headerHeight);
        dividerRect.sizeDelta = new Vector2(0f, borderThickness);

        viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
        viewportRect.offsetMin = new Vector2(innerInset, innerInset);
        viewportRect.offsetMax = new Vector2(-innerInset, -(headerHeight + innerInset));

        if (scanlineImage != null)
        {
            float period = Mathf.Max(0.5f, scanlineThickness + scanlineGap);
            scanlineImage.uvRect = new Rect(0f, 0f, 1f, frameHeight / period);
        }
    }

    private void LayoutPortrait(PortraitRuntime portrait)
    {
        if (portrait == null || portrait.imageRect == null || portrait.slotRect == null)
        {
            return;
        }

        CharacterDefinition definition = portrait.definition;
        Sprite sprite = portrait.image != null ? portrait.image.sprite : null;

        float slotWidth = portrait.slotRect.rect.width;
        float slotHeight = portrait.slotRect.rect.height;
        if (slotWidth <= 1f || slotHeight <= 1f)
        {
            return;
        }

        float padding = Mathf.Min(panelRect.rect.width, panelRect.rect.height) * portraitPaddingFraction;
        float availableWidth = Mathf.Max(1f, slotWidth - padding * 2f);
        float availableHeight = Mathf.Max(1f, slotHeight - padding * 2f);

        PortraitVerticalAlignment alignment = definition != null
            ? definition.verticalAlignment
            : PortraitVerticalAlignment.Bottom;

        float anchorY;
        float pivotY;
        float yOffset;

        switch (alignment)
        {
            case PortraitVerticalAlignment.Top:
                anchorY = 1f;
                pivotY = 1f;
                yOffset = -padding;
                break;

            case PortraitVerticalAlignment.Centre:
                anchorY = 0.5f;
                pivotY = 0.5f;
                yOffset = 0f;
                break;

            default:
                anchorY = 0f;
                pivotY = 0f;
                yOffset = padding;
                break;
        }

        portrait.imageRect.anchorMin = new Vector2(0.5f, anchorY);
        portrait.imageRect.anchorMax = new Vector2(0.5f, anchorY);
        portrait.imageRect.pivot = new Vector2(0.5f, pivotY);

        float portraitScale = definition != null ? Mathf.Clamp(definition.portraitScale, 0.1f, 1f) : 1f;
        float fittedWidth = availableWidth;
        float fittedHeight = availableHeight;

        if (sprite != null && sprite.rect.height > 0f)
        {
            float aspect = sprite.rect.width / sprite.rect.height;
            fittedWidth = availableWidth;
            fittedHeight = fittedWidth / Mathf.Max(0.0001f, aspect);

            if (fittedHeight > availableHeight)
            {
                fittedHeight = availableHeight;
                fittedWidth = fittedHeight * aspect;
            }
        }

        fittedWidth *= portraitScale;
        fittedHeight *= portraitScale;

        portrait.imageRect.sizeDelta = new Vector2(fittedWidth, fittedHeight);
        portrait.basePosition = new Vector2(0f, yOffset);
        portrait.imageRect.anchoredPosition = portrait.basePosition;

        bool flip = false;
        if (definition != null)
        {
            flip = portrait.side == PortraitSide.Left
                ? definition.flipWhenLeft
                : definition.flipWhenRight;
        }

        portrait.baseScale = new Vector3(flip ? -1f : 1f, 1f, 1f);

        if (!IsPortraitSpeaking(portrait))
        {
            portrait.imageRect.localScale = portrait.baseScale;
        }
    }

    private void LayoutControls()
    {
        float width = controlsRect.rect.width;
        float height = controlsRect.rect.height;
        if (width <= 1f || height <= 1f)
        {
            return;
        }

        float choiceFraction = Mathf.Clamp(choiceAreaFraction, 0.35f, 0.75f);

        choiceAreaRect.anchorMin = new Vector2(0f, 1f - choiceFraction);
        choiceAreaRect.anchorMax = Vector2.one;
        choiceAreaRect.offsetMin = Vector2.zero;
        choiceAreaRect.offsetMax = Vector2.zero;

        bottomBarRect.anchorMin = Vector2.zero;
        bottomBarRect.anchorMax = new Vector2(1f, 1f - choiceFraction);
        bottomBarRect.offsetMin = Vector2.zero;
        bottomBarRect.offsetMax = Vector2.zero;

        LayoutBottomBar();
        LayoutChoiceButtons();
    }

    private void LayoutBottomBar()
    {
        float width = bottomBarRect.rect.width;
        float height = bottomBarRect.rect.height;
        if (width <= 1f || height <= 1f)
        {
            return;
        }

        float edgeInset = Mathf.Max(5f, height * 0.14f);
        float gap = Mathf.Max(5f, width * 0.010f);
        float sideWidth = Mathf.Clamp(width * 0.16f, 90f, width * 0.22f);
        float buttonHeight = Mathf.Max(1f, height - edgeInset * 2f);

        if (generatedResetButton != null)
        {
            generatedResetButton.gameObject.SetActive(showDebugResetButton);
            if (showDebugResetButton)
            {
                PlaceBottomButton(generatedResetButton, edgeInset, sideWidth, buttonHeight, false);
            }
        }

        if (generatedAutoButton != null)
        {
            PlaceBottomButton(generatedAutoButton, edgeInset, sideWidth, buttonHeight, true);
        }

        if (generatedNextButton != null)
        {
            float left = edgeInset + (showDebugResetButton ? sideWidth + gap : 0f);
            float right = edgeInset + sideWidth + gap;
            RectTransform rect = generatedNextButton.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(left, -buttonHeight * 0.5f);
            rect.offsetMax = new Vector2(-right, buttonHeight * 0.5f);
        }
    }

    private void PlaceBottomButton(Button button, float edgeInset, float width, float height, bool right)
    {
        RectTransform rect = button.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(right ? 1f : 0f, 0.5f);
        rect.anchorMax = rect.anchorMin;
        rect.pivot = new Vector2(right ? 1f : 0f, 0.5f);
        rect.sizeDelta = new Vector2(width, height);
        rect.anchoredPosition = new Vector2(right ? -edgeInset : edgeInset, 0f);
    }

    private void LayoutChoiceButtons()
    {
        int count = generatedChoiceButtons.Count;
        if (count == 0 || choiceAreaRect == null)
        {
            return;
        }

        float width = choiceAreaRect.rect.width;
        float height = choiceAreaRect.rect.height;
        if (width <= 1f || height <= 1f)
        {
            return;
        }

        int rows = count <= 3 ? 1 : 2;
        int columns = Mathf.CeilToInt(count / (float)rows);
        float edgeInset = Mathf.Max(5f, height * 0.10f);
        float gap = Mathf.Max(5f, width * 0.010f);
        float usableWidth = Mathf.Max(1f, width - edgeInset * 2f - gap * (columns - 1));
        float usableHeight = Mathf.Max(1f, height - edgeInset * 2f - gap * (rows - 1));
        float buttonWidth = usableWidth / columns;
        float buttonHeight = usableHeight / rows;

        for (int i = 0; i < count; i++)
        {
            Button button = generatedChoiceButtons[i];
            int row = i / columns;
            int column = i % columns;
            RectTransform rect = button.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.sizeDelta = new Vector2(buttonWidth, buttonHeight);
            rect.anchoredPosition = new Vector2(
                edgeInset + column * (buttonWidth + gap),
                -(edgeInset + row * (buttonHeight + gap)));
        }
    }

    private void SetTopEdge(RectTransform rect, float thickness)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(0f, thickness);
    }

    private void SetBottomEdge(RectTransform rect, float thickness)
    {
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(0f, thickness);
    }

    private void SetLeftEdge(RectTransform rect, float thickness)
    {
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(thickness, 0f);
    }

    private void SetRightEdge(RectTransform rect, float thickness)
    {
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(thickness, 0f);
    }

    // ---------------------------------------------------------------------
    // Portraits
    // ---------------------------------------------------------------------

    private void ApplyCharacterPortraits(bool playAppearEffect)
    {
        ApplyCharacterPortrait(leftPortrait, leftDefinition, leftCharacterId, playAppearEffect);
        ApplyCharacterPortrait(rightPortrait, rightDefinition, rightCharacterId, playAppearEffect);

        if (isBooting && hideCharacterImagesDuringBoot)
        {
            SetCharacterImagesVisible(false);
        }
    }

    private void ApplyCharacterPortrait(
        PortraitRuntime portrait,
        CharacterDefinition definition,
        string characterId,
        bool playAppearEffect)
    {
        if (portrait == null || portrait.image == null)
        {
            return;
        }

        Sprite newSprite = ResolveCharacterSprite(definition, characterId);

        portrait.definition = definition;
        portrait.image.sprite = newSprite;
        portrait.image.preserveAspect = true;
        portrait.image.enabled = !hidePortraitWhenNoSprite || newSprite != null;

        LayoutPortrait(portrait);

        if (playAppearEffect && newSprite != null && playPortraitAppearEffect && ready)
        {
            StartPortraitAppearEffect(portrait);
        }
        else if (portrait.canvasGroup != null)
        {
            portrait.canvasGroup.alpha = 1f;
        }
    }

    private void StartPortraitAppearEffect(PortraitRuntime portrait)
    {
        StopPortraitEffect(portrait);
        portrait.appearCoroutine = StartCoroutine(PortraitAppearEffect(portrait));
    }

    private IEnumerator PortraitAppearEffect(PortraitRuntime portrait)
    {
        if (portrait == null || portrait.image == null || portrait.canvasGroup == null)
        {
            yield break;
        }

        float duration = Mathf.Max(0.05f, portraitAppearDuration);
        float elapsed = 0f;
        Vector2 basePosition = portrait.basePosition;

        portrait.canvasGroup.alpha = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            // A few hard alpha steps makes it read more like a broken hologram than a clean fade.
            float flicker = UnityEngine.Random.value > 0.32f ? 1f : UnityEngine.Random.Range(0.08f, 0.45f);
            portrait.canvasGroup.alpha = Mathf.Clamp01(t * 1.25f) * flicker;

            float jitter = portraitAppearJitter * (1f - t);
            portrait.imageRect.anchoredPosition = basePosition + new Vector2(
                UnityEngine.Random.Range(-jitter, jitter),
                0f);

            yield return new WaitForSecondsRealtime(UnityEngine.Random.Range(0.018f, 0.055f));
        }

        portrait.canvasGroup.alpha = 1f;
        portrait.imageRect.anchoredPosition = portrait.basePosition;
        portrait.appearCoroutine = null;
    }

    private void StopPortraitEffect(PortraitRuntime portrait)
    {
        if (portrait == null)
        {
            return;
        }

        if (portrait.appearCoroutine != null)
        {
            StopCoroutine(portrait.appearCoroutine);
            portrait.appearCoroutine = null;
        }

        if (portrait.canvasGroup != null)
        {
            portrait.canvasGroup.alpha = 1f;
        }

        if (portrait.imageRect != null)
        {
            portrait.imageRect.anchoredPosition = portrait.basePosition;
            portrait.imageRect.localScale = portrait.baseScale;
        }
    }

    private void UpdatePortraitSpeakingAnimation()
    {
        UpdatePortraitSpeakingAnimation(leftPortrait);
        UpdatePortraitSpeakingAnimation(rightPortrait);
    }

    private void UpdatePortraitSpeakingAnimation(PortraitRuntime portrait)
    {
        if (portrait == null || portrait.imageRect == null)
        {
            return;
        }

        bool speaking = animateSpeakingPortrait && IsPortraitSpeaking(portrait);
        float scale = 1f;

        if (speaking)
        {
            float pulse = (Mathf.Sin(Time.unscaledTime * speakingPulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f;
            scale += pulse * speakingScaleAmount;
        }

        Vector3 targetScale = new Vector3(
            portrait.baseScale.x * scale,
            portrait.baseScale.y * scale,
            1f);

        portrait.imageRect.localScale = Vector3.Lerp(
            portrait.imageRect.localScale,
            targetScale,
            Mathf.Clamp01(Time.unscaledDeltaTime * 16f));
    }

    private bool IsPortraitSpeaking(PortraitRuntime portrait)
    {
        if (portrait == null)
        {
            return false;
        }

        bool activeSideMatches = activeSpeakingSide.HasValue &&
            ((portrait.side == PortraitSide.Left && activeSpeakingSide.Value == LineKind.Left) ||
             (portrait.side == PortraitSide.Right && activeSpeakingSide.Value == LineKind.Right));

        return activeSideMatches || Time.unscaledTime < portrait.instantPulseUntil;
    }

    private void PulsePortraitInstantly(LineKind kind)
    {
        PortraitRuntime portrait = kind == LineKind.Left ? leftPortrait : rightPortrait;
        if (portrait != null)
        {
            portrait.instantPulseUntil = Time.unscaledTime + instantSpeechPulseDuration;
        }
    }

    private void SetCharacterImagesVisible(bool visible)
    {
        SetCharacterImageVisible(leftPortrait, visible);
        SetCharacterImageVisible(rightPortrait, visible);
    }

    private void SetCharacterImageVisible(PortraitRuntime portrait, bool visible)
    {
        if (portrait == null || portrait.image == null)
        {
            return;
        }

        if (!visible)
        {
            portrait.image.enabled = false;
            return;
        }

        portrait.image.enabled = !hidePortraitWhenNoSprite || portrait.image.sprite != null;
    }

    public string GetLeftCharacterId() => leftCharacterId;
    public string GetRightCharacterId() => rightCharacterId;
    public string GetLeftDisplayName() => leftDisplayName;
    public string GetRightDisplayName() => rightDisplayName;

    // ---------------------------------------------------------------------
    // Buttons / controls
    // ---------------------------------------------------------------------

    private void SetupGeneratedButtons()
    {
        if (generatedNextButton != null) generatedNextButton.onClick.AddListener(NextTick);
        if (generatedResetButton != null) generatedResetButton.onClick.AddListener(ResetDialogue);
        if (generatedAutoButton != null) generatedAutoButton.onClick.AddListener(ToggleAutomaticMode);
    }

    private void RemoveGeneratedButtonListeners()
    {
        if (generatedNextButton != null) generatedNextButton.onClick.RemoveListener(NextTick);
        if (generatedResetButton != null) generatedResetButton.onClick.RemoveListener(ResetDialogue);
        if (generatedAutoButton != null) generatedAutoButton.onClick.RemoveListener(ToggleAutomaticMode);
    }

    public void NextTick()
    {
        if (!ready || isBooting || fontTestMode)
        {
            return;
        }

        // A choice must be made deliberately. Space/Continue never auto-picks an option.
        if (waitingForChoice)
        {
            return;
        }

        if (isTyping)
        {
            CompleteCurrentTyping();
            return;
        }

        int nextTick = FindNextDialogueTick(currentTick);
        if (nextTick < 0)
        {
            return;
        }

        currentTick = nextTick;
        RecordVisitedTick(currentTick);
        automaticTimer = 0f;
        StartCurrentTick();
    }

    public void PreviousTick()
    {
        if (!ready || isBooting || fontTestMode)
        {
            return;
        }

        StopTyping();
        HideChoices();

        if (visitedTicks.Count == 0)
        {
            currentTick = -1;
        }
        else
        {
            if (visitedTicks.Count > 1)
            {
                visitedTicks.RemoveAt(visitedTicks.Count - 1);
            }

            currentTick = visitedTicks.Count > 0 ? visitedTicks[visitedTicks.Count - 1] : -1;

            // If we stepped back onto a decision point, make that decision available again.
            if (currentTick >= 0)
            {
                selectedChoiceLines.Remove(currentTick);
            }
        }

        automaticTimer = 0f;
        RebuildVisitedPathInstantly();
        ShowChoicesForCurrentTick();
    }

    public void ResetDialogue()
    {
        StopTyping();
        StopBootSequence();
        fontTestMode = false;
        currentTick = -1;
        automaticTimer = 0f;
        activeSpeakingSide = null;
        visitedTicks.Clear();
        selectedChoiceLines.Clear();
        HideChoices();

        if (statusText != null)
        {
            statusText.text = resolvedStatus ?? "";
        }

        ClearGeneratedLines();
        CalculateDialogueLayout();
        ScrollToBottom();
    }

    public void SetTick(int tick)
    {
        if (!ready || isBooting || fontTestMode)
        {
            return;
        }

        StopTyping();
        HideChoices();
        currentTick = tick;
        visitedTicks.Clear();
        selectedChoiceLines.Clear();
        if (TickExists(tick))
        {
            visitedTicks.Add(tick);
        }
        automaticTimer = 0f;
        RebuildVisitedPathInstantly();
        ShowChoicesForCurrentTick();
    }

    public int GetTick() => currentTick;

    public void ToggleAutomaticMode()
    {
        SetAutomaticMode(!activeAutomaticMode);
    }

    public void SetAutomaticMode(bool enabled)
    {
        activeAutomaticMode = enabled;
        automaticTimer = 0f;
        UpdateAutoButtonLabel();
    }

    private void UpdateAutoButtonLabel()
    {
        if (generatedAutoLabel != null)
        {
            generatedAutoLabel.text = activeAutomaticMode ? "AUTO: ON" : "AUTO: OFF";
        }
    }

    public void SetCharactersPerSecond(float value)
    {
        activeCharactersPerSecond = Mathf.Max(1f, value);
    }

    public void SetLineInterval(float value)
    {
        activeLineInterval = Mathf.Max(0f, value);
    }

    private void SetControlsInteractable(bool interactable)
    {
        if (generatedNextButton != null) generatedNextButton.interactable = interactable && !waitingForChoice;
        if (generatedResetButton != null) generatedResetButton.interactable = interactable;
        if (generatedAutoButton != null) generatedAutoButton.interactable = interactable;

        foreach (Button button in generatedChoiceButtons)
        {
            if (button != null) button.interactable = interactable;
        }
    }

    private int FindNextDialogueTick(int afterTick)
    {
        if (afterTick >= 0)
        {
            foreach (DialogueLine line in dialogue)
            {
                if (line.tick == afterTick && line.nextTick != 0)
                {
                    if (TickExists(line.nextTick))
                    {
                        return line.nextTick;
                    }

                    Debug.LogWarning($"Dialogue tick {afterTick} points to missing nextTick {line.nextTick}. Falling back to numeric order.", this);
                    break;
                }
            }
        }

        foreach (DialogueLine line in dialogue)
        {
            if (line.tick > afterTick)
            {
                return line.tick;
            }
        }

        return -1;
    }

    private bool TickExists(int tick)
    {
        foreach (DialogueLine line in dialogue)
        {
            if (line.tick == tick)
            {
                return true;
            }
        }

        return false;
    }

    private int FindPreviousDialogueTick(int beforeTick)
    {
        int result = -1;

        foreach (DialogueLine line in dialogue)
        {
            if (line.tick >= beforeTick)
            {
                break;
            }

            result = line.tick;
        }

        return result;
    }

    private void UpdateAutomaticDialogue()
    {
        if (!activeAutomaticMode || isTyping || isBooting || fontTestMode || waitingForChoice)
        {
            automaticTimer = 0f;
            return;
        }

        if (FindNextDialogueTick(currentTick) < 0)
        {
            return;
        }

        automaticTimer += Time.unscaledDeltaTime;
        if (automaticTimer < activeLineInterval)
        {
            return;
        }

        automaticTimer = 0f;
        NextTick();
    }

    // ---------------------------------------------------------------------
    // Dialogue typing
    // ---------------------------------------------------------------------

    private void StartCurrentTick()
    {
        StopTyping();
        activeTickLines = new List<DialogueLine>();

        foreach (DialogueLine line in dialogue)
        {
            if (line.tick == currentTick)
            {
                activeTickLines.Add(line);
            }
        }

        if (activeTickLines.Count == 0)
        {
            ShowChoicesForCurrentTick();
            return;
        }

        HideChoices();
        typingCoroutine = StartCoroutine(TypeCurrentTick());
    }

    private IEnumerator TypeCurrentTick()
    {
        isTyping = true;

        for (int i = 0; i < activeTickLines.Count; i++)
        {
            activeTickLineIndex = i;
            DialogueLine dialogueLine = activeTickLines[i];

            if (!TryResolveSpeaker(dialogueLine.speakerId, out LineKind kind))
            {
                Debug.LogWarning($"Unknown dialogue speaker '{dialogueLine.speakerId}'.", this);
                continue;
            }

            activeSpeakingSide = kind;
            activeGeneratedLine = CreateGeneratedLine(kind, dialogueLine.text, !typewriterEffect);
            CalculateDialogueLayout();
            ScrollToBottom();

            if (typewriterEffect)
            {
                yield return RevealLine(activeGeneratedLine, activeCharactersPerSecond, false);
            }
            else
            {
                PulsePortraitInstantly(kind);
            }
        }

        activeSpeakingSide = null;
        isTyping = false;
        typingCoroutine = null;
        activeGeneratedLine = null;
        activeTickLines = null;
        activeTickLineIndex = -1;
        automaticTimer = 0f;
        ShowChoicesForCurrentTick();
    }

    private bool TryResolveSpeaker(string speakerId, out LineKind kind)
    {
        if (string.Equals(speakerId, leftCharacterId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(speakerId, "left", StringComparison.OrdinalIgnoreCase))
        {
            kind = LineKind.Left;
            return true;
        }

        if (string.Equals(speakerId, rightCharacterId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(speakerId, "right", StringComparison.OrdinalIgnoreCase))
        {
            kind = LineKind.Right;
            return true;
        }

        kind = LineKind.Left;
        return false;
    }

    private IEnumerator RevealLine(GeneratedLine line, float speed, bool jitter)
    {
        TextMeshProUGUI text = line.text;
        text.maxVisibleCharacters = 0;
        text.ForceMeshUpdate();

        int characterCount = text.textInfo.characterCount;
        float secondsPerCharacter = 1f / Mathf.Max(1f, speed);
        float timer = 0f;
        int visibleCharacters = 0;

        while (visibleCharacters < characterCount)
        {
            timer += Time.unscaledDeltaTime;

            while (timer >= secondsPerCharacter && visibleCharacters < characterCount)
            {
                timer -= secondsPerCharacter;
                visibleCharacters++;
                text.maxVisibleCharacters = visibleCharacters;

                if (jitter && visibleCharacters % 11 == 0 && UnityEngine.Random.value < 0.28f)
                {
                    yield return new WaitForSecondsRealtime(UnityEngine.Random.Range(0.015f, 0.06f));
                }
            }

            yield return null;
        }

        text.maxVisibleCharacters = int.MaxValue;
    }

    private void CompleteCurrentTyping()
    {
        if (!isTyping)
        {
            return;
        }

        if (typingCoroutine != null)
        {
            StopCoroutine(typingCoroutine);
            typingCoroutine = null;
        }

        if (activeGeneratedLine != null)
        {
            activeGeneratedLine.text.maxVisibleCharacters = int.MaxValue;
        }

        if (activeTickLines != null)
        {
            for (int i = activeTickLineIndex + 1; i < activeTickLines.Count; i++)
            {
                DialogueLine dialogueLine = activeTickLines[i];
                if (TryResolveSpeaker(dialogueLine.speakerId, out LineKind kind))
                {
                    CreateGeneratedLine(kind, dialogueLine.text, true);
                    PulsePortraitInstantly(kind);
                }
            }
        }

        activeSpeakingSide = null;
        isTyping = false;
        activeGeneratedLine = null;
        activeTickLines = null;
        activeTickLineIndex = -1;
        automaticTimer = 0f;

        CalculateDialogueLayout();
        ScrollToBottom();
        ShowChoicesForCurrentTick();
    }

    private void StopTyping()
    {
        if (typingCoroutine != null)
        {
            StopCoroutine(typingCoroutine);
            typingCoroutine = null;
        }

        activeSpeakingSide = null;
        isTyping = false;
        activeGeneratedLine = null;
        activeTickLines = null;
        activeTickLineIndex = -1;
    }

    // ---------------------------------------------------------------------
    // Boot animation
    // ---------------------------------------------------------------------

    public void RunBootSequence()
    {
        if (!ready)
        {
            bootRequested = true;
            return;
        }

        StopTyping();
        StopBootSequence();

        fontTestMode = false;
        currentTick = -1;
        visitedTicks.Clear();
        selectedChoiceLines.Clear();
        HideChoices();
        automaticTimer = 0f;

        ClearGeneratedLines();
        CalculateDialogueLayout();
        ScrollToBottom();

        bootCoroutine = StartCoroutine(BootSequence());
    }

    private IEnumerator BootSequence()
    {
        isBooting = true;
        bootRequested = false;

        SetControlsInteractable(false);

        if (hideCharacterImagesDuringBoot)
        {
            SetCharacterImagesVisible(false);
        }

        string normalStatus = resolvedStatus;
        statusText.text = "BOOTING";

        string[] bootLineSnapshot = bootLines != null ? bootLines.ToArray() : Array.Empty<string>();
        float bootSpeed = Mathf.Max(1f, defaultBootCharactersPerSecond);
        float connectDuration = Mathf.Max(0.1f, defaultConnectDuration);
        float readyHold = Mathf.Max(0f, defaultReadyHold);
        int blankLinesBeforeDialogue = Mathf.Max(0, defaultBlankLinesBeforeDialogue);
        bool keepBootLog = keepBootLogAfterBoot;

        foreach (string bootLine in bootLineSnapshot)
        {
            yield return BootLine(bootLine ?? "", bootSpeed);
            yield return BootPause(0.035f, 0.085f);
        }

        yield return BootPause(0.16f, 0.26f);
        yield return AnimateConnectionSequence(bootSpeed, connectDuration);

        statusText.text = "ONLINE";
        yield return BootLine("> TERMINAL READY", bootSpeed * 0.82f);

        if (readyHold > 0f)
        {
            yield return new WaitForSecondsRealtime(readyHold);
        }

        if (!keepBootLog)
        {
            ClearGeneratedLines();
            CalculateDialogueLayout();
            ScrollToBottom();
        }

        AddBlankLinesBeforeDialogue(blankLinesBeforeDialogue);
        statusText.text = normalStatus;

        isBooting = false;
        bootCoroutine = null;
        automaticTimer = 0f;

        ApplyCharacterPortraits(true);
        SetCharacterImagesVisible(true);
        SetControlsInteractable(true);

        if (!startEmpty)
        {
            int firstTick = FindNextDialogueTick(-1);
            if (firstTick >= 0)
            {
                currentTick = firstTick;
                RecordVisitedTick(currentTick);
                StartCurrentTick();
            }
        }
    }

    private IEnumerator AnimateConnectionSequence(float bootSpeed, float connectDuration)
    {
        statusText.text = "SEARCHING";

        GeneratedLine connectionLine = CreateGeneratedLine(
            LineKind.System,
            "CONNECT [............]   0% |",
            true,
            true);

        CalculateDialogueLayout();
        ScrollToBottom();

        float duration = Mathf.Max(0.25f, connectDuration);
        float elapsed = 0f;
        bool performedRetry = false;
        char[] spinner = { '|', '/', '-', '\\' };

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;

            float t = Mathf.Clamp01(elapsed / duration);
            int percent = Mathf.FloorToInt(t * 100f);
            int filled = Mathf.Clamp(Mathf.FloorToInt(t * 12f), 0, 12);

            string bar = new string('=', filled) + new string('.', 12 - filled);
            char spin = spinner[Mathf.Abs(Mathf.FloorToInt(elapsed * 12f)) % spinner.Length];

            SetGeneratedLineText(connectionLine, $"CONNECT [{bar}] {percent,3}% {spin}", false);

            if (!performedRetry && t >= 0.42f)
            {
                performedRetry = true;
                statusText.text = "NO CARRIER";
                SetGeneratedLineText(connectionLine, $"CONNECT [{bar}] {percent,3}% !", false);
                yield return new WaitForSecondsRealtime(0.28f);
                statusText.text = "RETRYING";
                yield return new WaitForSecondsRealtime(0.24f);
                statusText.text = "SEARCHING";
            }

            yield return null;
        }

        SetGeneratedLineText(connectionLine, "CONNECT [============] 100% OK", true);
        statusText.text = "HANDSHAKE";

        yield return BootPause(0.08f, 0.14f);
        yield return BootLine("REMOTE HANDSHAKE......... ACCEPTED", bootSpeed);
        yield return BootLine("TTY LINK................. OK", bootSpeed);
        yield return BootLine("NOISE FILTER............. OK", bootSpeed);
        yield return BootPause(0.10f, 0.18f);
    }

    private IEnumerator BootLine(string value, float speed)
    {
        GeneratedLine line = CreateGeneratedLine(LineKind.System, value, false, true);
        CalculateDialogueLayout();
        ScrollToBottom();
        yield return RevealLine(line, speed, true);
    }

    private IEnumerator BootPause(float minimum, float maximum)
    {
        yield return new WaitForSecondsRealtime(UnityEngine.Random.Range(minimum, maximum));
    }

    private void AddBlankLinesBeforeDialogue(int count)
    {
        int safeCount = Mathf.Max(0, count);

        for (int i = 0; i < safeCount; i++)
        {
            CreateGeneratedLine(LineKind.System, " ", true, true);
        }

        if (safeCount > 0)
        {
            CalculateDialogueLayout();
            ScrollToBottom();
        }
    }

    private void SetGeneratedLineText(GeneratedLine line, string value, bool rebuildLayout = true)
    {
        if (line == null || line.text == null)
        {
            return;
        }

        line.fullText = value ?? "";
        line.text.text = line.fullText;
        line.text.maxVisibleCharacters = int.MaxValue;

        if (rebuildLayout)
        {
            CalculateDialogueLayout();
            ScrollToBottom();
        }
    }

    private void StopBootSequence()
    {
        if (bootCoroutine != null)
        {
            StopCoroutine(bootCoroutine);
            bootCoroutine = null;
        }

        isBooting = false;

        if (ready)
        {
            ApplyCharacterPortraits(false);
            SetCharacterImagesVisible(true);
            SetControlsInteractable(true);
        }
    }

    // ---------------------------------------------------------------------
    // Font test
    // ---------------------------------------------------------------------

    [ContextMenu("Show Font Test")]
    public void ShowFontTest()
    {
        HideChoices();
        if (!Application.isPlaying || !ready)
        {
            Debug.LogWarning("Font test is available in Play Mode after the dialogue panel has initialised.", this);
            return;
        }

        StopTyping();
        StopBootSequence();

        fontTestMode = true;
        currentTick = -1;
        automaticTimer = 0f;
        activeSpeakingSide = null;

        ClearGeneratedLines();
        statusText.text = "FONT TEST";

        if (fontLibrary == null || fontLibrary.Count == 0)
        {
            CreateGeneratedLine(LineKind.System, "NO TMP FONTS FOUND UNDER Assets/Fonts", true);
        }
        else
        {
            foreach (TMP_FontAsset font in fontLibrary)
            {
                if (font == null)
                {
                    continue;
                }

                float modifier = FindFontSizeModifier(font);
                float testSize = Mathf.Max(1f, fontTestBaseSize + modifier);

                string cleanName = CleanFontDisplayName(font.name);
                string modifierText = modifier > 0f ? $"+{modifier:0.#}" : modifier.ToString("0.#");
                string lineText = $"{cleanName} | base {fontTestBaseSize:0.#} {modifierText} = {testSize:0.#} px | {fontTestSample}";
                CreateCustomFontTestLine(lineText, font, testSize);
            }
        }

        CalculateDialogueLayout();
        ScrollToTop();
    }

    private string CleanFontDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unnamed Font";
        }

        string result = name.Trim();
        if (result.EndsWith(" SDF", StringComparison.OrdinalIgnoreCase))
        {
            result = result.Substring(0, result.Length - 4);
        }

        return result;
    }

    private GeneratedLine CreateCustomFontTestLine(string value, TMP_FontAsset font, float size)
    {
        GeneratedLine line = CreateGeneratedLine(LineKind.System, value, true);
        line.useCustomStyle = true;
        line.customFont = font;
        line.customFontSize = size;
        ConfigureGeneratedLine(line);
        return line;
    }

    // ---------------------------------------------------------------------
    // Generated dialogue lines
    // ---------------------------------------------------------------------

    private GeneratedLine CreateGeneratedLine(LineKind kind, string value, bool immediatelyVisible, bool useBootStyle = false)
    {
        GameObject obj = new GameObject(
            $"TerminalLine_{generatedLines.Count}",
            typeof(RectTransform),
            typeof(TextMeshProUGUI));

        obj.transform.SetParent(contentRect, false);

        GeneratedLine line = new GeneratedLine
        {
            kind = kind,
            fullText = value ?? "",
            rect = obj.GetComponent<RectTransform>(),
            text = obj.GetComponent<TextMeshProUGUI>(),
            useBootStyle = useBootStyle
        };

        line.text.text = line.fullText;
        line.text.raycastTarget = false;
        line.text.textWrappingMode = TextWrappingModes.Normal;
        line.text.overflowMode = TextOverflowModes.Truncate;

        ConfigureGeneratedLine(line);
        line.text.maxVisibleCharacters = immediatelyVisible ? int.MaxValue : 0;

        generatedLines.Add(line);
        return line;
    }

    private void ConfigureGeneratedLine(GeneratedLine line)
    {
        if (line == null || line.text == null)
        {
            return;
        }

        if (line.useCustomStyle && line.customFont != null)
        {
            line.text.font = line.customFont;
            line.text.fontSize = Mathf.Max(1f, line.customFontSize);
            line.text.color = resolvedPrimaryColour;
            line.text.alignment = TextAlignmentOptions.TopLeft;
            return;
        }

        if (line.useBootStyle)
        {
            line.text.font = resolvedSystemFont;
            line.text.fontSize = resolvedBootFontSize;
            line.text.color = resolvedPrimaryColour;
            line.text.alignment = TextAlignmentOptions.TopLeft;
            return;
        }

        switch (line.kind)
        {
            case LineKind.Left:
                line.text.font = resolvedLeftFont;
                line.text.fontSize = resolvedLeftFontSize;
                line.text.color = resolvedLeftColour;
                line.text.alignment = TextAlignmentOptions.TopLeft;
                break;

            case LineKind.Right:
                line.text.font = resolvedRightFont;
                line.text.fontSize = resolvedRightFontSize;
                line.text.color = resolvedRightColour;
                line.text.alignment = TextAlignmentOptions.TopRight;
                break;

            default:
                line.text.font = resolvedSystemFont;
                line.text.fontSize = resolvedSystemFontSize;
                line.text.color = resolvedPrimaryColour;
                line.text.alignment = TextAlignmentOptions.TopLeft;
                break;
        }
    }

    private void ClearGeneratedLines()
    {
        generatedLines.Clear();

        if (contentRect == null)
        {
            return;
        }

        for (int i = contentRect.childCount - 1; i >= 0; i--)
        {
            GameObject child = contentRect.GetChild(i).gameObject;
            child.SetActive(false);
            Destroy(child);
        }
    }

    private void RebuildToCurrentTickInstantly()
    {
        RebuildVisitedPathInstantly();
    }

    private void RebuildVisitedPathInstantly()
    {
        ClearGeneratedLines();

        foreach (int tick in visitedTicks)
        {
            foreach (DialogueLine dialogueLine in dialogue)
            {
                if (dialogueLine.tick != tick)
                {
                    continue;
                }

                if (TryResolveSpeaker(dialogueLine.speakerId, out LineKind kind))
                {
                    CreateGeneratedLine(kind, dialogueLine.text, true);
                }
            }

            if (selectedChoiceLines.TryGetValue(tick, out string choiceLine) && !string.IsNullOrWhiteSpace(choiceLine))
            {
                CreateGeneratedLine(LineKind.Left, choiceLine, true);
            }
        }

        CalculateDialogueLayout();
        ScrollToBottom();
    }

    private void RecordVisitedTick(int tick)
    {
        if (tick < 0)
        {
            return;
        }

        if (visitedTicks.Count == 0 || visitedTicks[visitedTicks.Count - 1] != tick)
        {
            visitedTicks.Add(tick);
        }
    }

    private ChoiceSetJson FindChoiceSet(int tick)
    {
        if (loadedDefinition == null || loadedDefinition.choices == null)
        {
            return null;
        }

        foreach (ChoiceSetJson choiceSet in loadedDefinition.choices)
        {
            if (choiceSet != null && choiceSet.tick == tick && choiceSet.options != null && choiceSet.options.Count > 0)
            {
                return choiceSet;
            }
        }

        return null;
    }

    private void ShowChoicesForCurrentTick()
    {
        if (isBooting || fontTestMode || isTyping || currentTick < 0 || selectedChoiceLines.ContainsKey(currentTick))
        {
            return;
        }

        ChoiceSetJson choiceSet = FindChoiceSet(currentTick);
        if (choiceSet == null)
        {
            waitingForChoice = false;
            activeChoiceSet = null;
            UpdateContinueInteractivity();
            return;
        }

        HideChoices();
        waitingForChoice = true;
        activeChoiceSet = choiceSet;
        automaticTimer = 0f;

        for (int i = 0; i < choiceSet.options.Count; i++)
        {
            ChoiceOptionJson option = choiceSet.options[i];
            if (option == null || string.IsNullOrWhiteSpace(option.text))
            {
                continue;
            }

            int capturedIndex = i;
            Button button = CreateGeneratedButton(
                $"ChoiceButton_{i}",
                choiceAreaRect,
                option.text,
                out TextMeshProUGUI label);

            ConfigureButtonVisual(button, label);
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Truncate;
            button.onClick.AddListener(() => SelectChoice(capturedIndex));
            generatedChoiceButtons.Add(button);
        }

        UpdateContinueInteractivity();
        LayoutControls();
    }

    private void HideChoices()
    {
        waitingForChoice = false;
        activeChoiceSet = null;

        foreach (Button button in generatedChoiceButtons)
        {
            if (button != null)
            {
                button.gameObject.SetActive(false);
                Destroy(button.gameObject);
            }
        }

        generatedChoiceButtons.Clear();
        UpdateContinueInteractivity();
    }

    private void SelectChoice(int optionIndex)
    {
        if (!waitingForChoice || activeChoiceSet == null || activeChoiceSet.options == null ||
            optionIndex < 0 || optionIndex >= activeChoiceSet.options.Count)
        {
            return;
        }

        ChoiceOptionJson option = activeChoiceSet.options[optionIndex];
        if (option == null)
        {
            return;
        }

        int sourceTick = currentTick;
        string spokenLine = !string.IsNullOrWhiteSpace(option.line) ? option.line : option.text;
        int requestedTarget = option.nextTick;

        HideChoices();

        if (!string.IsNullOrWhiteSpace(spokenLine))
        {
            selectedChoiceLines[sourceTick] = spokenLine;
            CreateGeneratedLine(LineKind.Left, spokenLine, true);
            PulsePortraitInstantly(LineKind.Left);
            CalculateDialogueLayout();
            ScrollToBottom();
        }

        int targetTick = requestedTarget != 0 ? requestedTarget : FindNextDialogueTick(sourceTick);
        if (requestedTarget != 0 && !TickExists(requestedTarget))
        {
            Debug.LogWarning($"Dialogue choice at tick {sourceTick} points to missing tick {requestedTarget}. Falling back to normal flow.", this);
            targetTick = FindNextDialogueTick(sourceTick);
        }

        if (targetTick < 0)
        {
            UpdateContinueInteractivity();
            return;
        }

        currentTick = targetTick;
        RecordVisitedTick(currentTick);
        automaticTimer = 0f;
        StartCurrentTick();
    }

    private void UpdateContinueInteractivity()
    {
        if (generatedNextButton != null)
        {
            generatedNextButton.interactable = ready && !isBooting && !fontTestMode && !waitingForChoice;
        }
    }

    private void UpdateContinueButtonLabel()
    {
        if (generatedNextLabel != null)
        {
            generatedNextLabel.text = string.IsNullOrWhiteSpace(continueButtonText) ? "CONTINUE" : continueButtonText;
        }
    }

    private void UpdateKeyboardControls()
    {
        if (waitingForChoice || isBooting || fontTestMode)
        {
            return;
        }

        GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selected != null &&
            (selected.GetComponent<TMP_InputField>() != null || selected.GetComponent<InputField>() != null))
        {
            return;
        }

        bool pressed = false;

#if ENABLE_INPUT_SYSTEM
        pressed = Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame;
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        if (!pressed)
        {
            pressed = Input.GetKeyDown(KeyCode.Space);
        }
#endif

        if (pressed)
        {
            NextTick();
        }
    }

    // ---------------------------------------------------------------------
    // Chat log layout
    // ---------------------------------------------------------------------

    private void CalculateDialogueLayout()
    {
        if (viewportRect == null || contentRect == null)
        {
            return;
        }

        Canvas.ForceUpdateCanvases();

        float width = viewportRect.rect.width;
        float height = viewportRect.rect.height;
        if (width <= 1f || height <= 1f)
        {
            return;
        }

        float largestFont = Mathf.Max(resolvedSystemFontSize, resolvedLeftFontSize, resolvedRightFontSize);
        foreach (GeneratedLine line in generatedLines)
        {
            if (line != null && line.text != null)
            {
                largestFont = Mathf.Max(largestFont, line.text.fontSize);
            }
        }

        float edgeInset = Mathf.Max(width * 0.025f, largestFont * 0.30f);
        float usableWidth = Mathf.Max(1f, width - edgeInset * 2f);
        float dialogueWidth = usableWidth * 0.47f;
        float systemWidth = usableWidth;
        float spacing = largestFont * 0.28f;
        float verticalInset = largestFont * 0.35f;

        float totalHeight = 0f;

        for (int i = 0; i < generatedLines.Count; i++)
        {
            GeneratedLine line = generatedLines[i];
            float lineWidth = line.kind == LineKind.System ? systemWidth : dialogueWidth;

            Vector2 preferred = line.text.GetPreferredValues(line.fullText, lineWidth, Mathf.Infinity);
            float minimumHeight = line.text.fontSize * 1.2f;
            line.height = Mathf.Max(preferred.y, minimumHeight);

            totalHeight += line.height;
            if (i < generatedLines.Count - 1)
            {
                totalHeight += spacing;
            }
        }

        float requiredHeight = totalHeight + verticalInset * 2f;
        float contentHeight = Mathf.Max(height, requiredHeight);
        contentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, contentHeight);

        float y = requiredHeight <= height
            ? -(height - verticalInset - totalHeight)
            : -verticalInset;

        foreach (GeneratedLine line in generatedLines)
        {
            switch (line.kind)
            {
                case LineKind.Left:
                    PlaceLeftLine(line.rect, dialogueWidth, edgeInset, y, line.height);
                    break;

                case LineKind.Right:
                    PlaceRightLine(line.rect, dialogueWidth, edgeInset, y, line.height);
                    break;

                default:
                    PlaceSystemLine(line.rect, systemWidth, edgeInset, y, line.height);
                    break;
            }

            y -= line.height + spacing;
        }

        Canvas.ForceUpdateCanvases();
    }

    private void PlaceLeftLine(RectTransform rect, float width, float inset, float y, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        rect.anchoredPosition = new Vector2(inset, y);
    }

    private void PlaceRightLine(RectTransform rect, float width, float inset, float y, float height)
    {
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        rect.anchoredPosition = new Vector2(-inset, y);
    }

    private void PlaceSystemLine(RectTransform rect, float width, float inset, float y, float height)
    {
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
        rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        rect.anchoredPosition = new Vector2(inset, y);
    }

    private void CheckPanelSize()
    {
        Vector2 currentSize = panelRect.rect.size;
        if (Vector2.SqrMagnitude(currentSize - lastPanelSize) < 0.01f)
        {
            return;
        }

        lastPanelSize = currentSize;
        LayoutAll();

        if (autoScrollToNewest && !fontTestMode)
        {
            ScrollToBottom();
        }
    }

    private void ScrollToBottom()
    {
        if (scrollRect == null)
        {
            return;
        }

        Canvas.ForceUpdateCanvases();
        scrollRect.verticalNormalizedPosition = 0f;
        Canvas.ForceUpdateCanvases();
    }

    private void ScrollToTop()
    {
        if (scrollRect == null)
        {
            return;
        }

        Canvas.ForceUpdateCanvases();
        scrollRect.verticalNormalizedPosition = 1f;
        Canvas.ForceUpdateCanvases();
    }

    // ---------------------------------------------------------------------
    // Runtime helpers
    // ---------------------------------------------------------------------

    public void SetColourPreset(TerminalColourPreset preset)
    {
        colourPreset = preset;
        presentationDirty = true;
    }

    public void SetScanlineSettings(float thickness, float gap, float opacity)
    {
        scanlineThickness = Mathf.Max(0.25f, thickness);
        scanlineGap = Mathf.Max(0.25f, gap);
        scanlineOpacity = Mathf.Clamp01(opacity);
        scanlineDirty = true;
    }
}
