using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Shared look for the NEW terminal-style UIs (event UI, planet picker, combat
/// buttons) so they match the dialogue UI without touching DialoguePanelController.
///
/// The values are a hand copy of the dialogue panel's Inspector settings and its
/// built-in colour presets. If the dialogue panel's look changes, update this
/// asset to match - nothing reads the dialogue panel at runtime.
/// </summary>
[CreateAssetMenu(menuName = "Derelict Deliveries/UI/Terminal Style", fileName = "TerminalStyle")]
public sealed class TerminalStyle : ScriptableObject
{
    /// <summary>Resolved colours for one preset.</summary>
    public struct Palette
    {
        public Color background;
        public Color primary;
        public Color dim;
    }

    [Header("Colour Presets (same names and colours as the dialogue panel)")]
    [Tooltip("Normal events, the planet picker and trade. The scene's dialogue panel uses GreenPhosphor.")]
    [SerializeField] private DialoguePanelController.TerminalColourPreset normalPreset =
        DialoguePanelController.TerminalColourPreset.GreenPhosphor;

    [Tooltip("Random events such as hazards.")]
    [SerializeField] private DialoguePanelController.TerminalColourPreset alertPreset =
        DialoguePanelController.TerminalColourPreset.RedAlert;

    [SerializeField] private Color customBackgroundColour = new Color32(0x10, 0x09, 0x00, 0xFF);
    [SerializeField] private Color customPrimaryColour = new Color32(0xFF, 0xB0, 0x00, 0xFF);
    [SerializeField] private Color customDimColour = new Color32(0x9C, 0x6B, 0x00, 0xFF);

    [Header("Panel Background (copy from the dialogue panel)")]
    [Tooltip("Same sprite as the dialogue panel's Panel Background Sprite. Empty = flat preset background colour.")]
    [SerializeField] private Sprite panelBackgroundSprite;
    [SerializeField] private bool useSlicedPanelBackground = true;
    [SerializeField] private bool tintPanelBackgroundWithTerminalColour;
    [SerializeField] private Color panelBackgroundTint = Color.white;

    [Header("Scanlines (scene dialogue panel: on, 8, 8, 0.313)")]
    [SerializeField] private bool scanlines = true;
    [Min(0.5f)] [SerializeField] private float scanlineThickness = 8f;
    [Min(0f)] [SerializeField] private float scanlineGap = 8f;
    [Range(0f, 1f)] [SerializeField] private float scanlineOpacity = 0.313f;

    [Header("Fonts (scene dialogue panel: system 20, buttons 42)")]
    [Tooltip("Empty = TextMeshPro's default font asset, which is what the dialogue panel falls back to.")]
    [SerializeField] private TMP_FontAsset systemFont;
    [Min(1f)] [SerializeField] private float headerFontSize = 20f;
    [Min(1f)] [SerializeField] private float bodyFontSize = 28f;
    [Min(1f)] [SerializeField] private float buttonFontSize = 42f;

    [Header("Buttons (same values the dialogue panel uses)")]
    [Range(0f, 1f)] [SerializeField] private float buttonBackgroundAlpha = 0.22f;
    [Range(0f, 1f)] [SerializeField] private float lockedLabelAlpha = 0.45f;

    public bool Scanlines => scanlines;
    public float ScanlineThickness => scanlineThickness;
    public float ScanlineGap => scanlineGap;
    public float ScanlineOpacity => scanlineOpacity;
    public float HeaderFontSize => headerFontSize;
    public float BodyFontSize => bodyFontSize;
    public float ButtonFontSize => buttonFontSize;
    public TMP_FontAsset Font => systemFont != null ? systemFont : TMP_Settings.defaultFontAsset;


    public Palette GetPalette(bool alert)
    {
        return GetPalette(alert ? alertPreset : normalPreset);
    }


    public Palette GetPalette(DialoguePanelController.TerminalColourPreset preset)
    {
        switch (preset)
        {
            case DialoguePanelController.TerminalColourPreset.GreenPhosphor:
                return Make("#001008", "#42FF7B", "#187A3D");
            case DialoguePanelController.TerminalColourPreset.IceBlue:
                return Make("#031018", "#67D8FF", "#2B718C");
            case DialoguePanelController.TerminalColourPreset.PaperWhite:
                return Make("#111111", "#E8E8DE", "#85857F");
            case DialoguePanelController.TerminalColourPreset.RedAlert:
                return Make("#160203", "#FF4A4A", "#8C2424");
            case DialoguePanelController.TerminalColourPreset.Violet:
                return Make("#0E0616", "#D48CFF", "#71458A");
            case DialoguePanelController.TerminalColourPreset.Custom:
                return new Palette
                {
                    background = customBackgroundColour,
                    primary = customPrimaryColour,
                    dim = customDimColour
                };
            default: // Amber
                return Make("#100900", "#FFB000", "#9C6B00");
        }
    }


    /// <summary>Panel background, the same way the dialogue panel applies it.</summary>
    public void ApplyPanel(Image panel, Palette palette)
    {
        if (panel == null) return;

        if (panelBackgroundSprite != null)
        {
            panel.sprite = panelBackgroundSprite;
            panel.type = useSlicedPanelBackground ? Image.Type.Sliced : Image.Type.Simple;
            panel.fillCenter = true;
            panel.color = tintPanelBackgroundWithTerminalColour
                ? panelBackgroundTint * palette.background
                : panelBackgroundTint;
        }
        else
        {
            panel.sprite = null;
            panel.type = Image.Type.Simple;
            panel.color = palette.background;
        }
    }


    /// <summary>
    /// Button look copied from DialoguePanelController.ConfigureButtonVisual:
    /// dim colour at 22% alpha behind a primary-coloured label.
    /// </summary>
    public void ApplyButton(Button button, TMP_Text label, Palette palette, bool interactable = true)
    {
        if (button == null) return;

        Image image = button.GetComponent<Image>();
        if (image != null)
        {
            Color baseColour = palette.dim;
            baseColour.a = buttonBackgroundAlpha;
            image.color = baseColour;
        }

        ColorBlock colours = button.colors;
        colours.normalColor = Color.white;
        colours.highlightedColor = new Color(1f, 1f, 1f, 1.35f);
        colours.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        colours.selectedColor = Color.white;
        colours.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
        colours.colorMultiplier = 1f;
        button.colors = colours;
        button.interactable = interactable;

        if (label != null)
        {
            label.font = Font;
            label.fontSize = buttonFontSize;
            Color text = palette.primary;
            if (!interactable) text.a = lockedLabelAlpha;
            label.color = text;
        }
    }


    public void ApplyText(TMP_Text text, Color colour, float size)
    {
        if (text == null) return;
        text.font = Font;
        text.fontSize = size;
        text.color = colour;
    }


    private static Palette Make(string background, string primary, string dim)
    {
        return new Palette
        {
            background = Html(background),
            primary = Html(primary),
            dim = Html(dim)
        };
    }


    private static Color Html(string value)
    {
        return ColorUtility.TryParseHtmlString(value, out Color colour) ? colour : Color.white;
    }
}
