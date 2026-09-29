using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public enum AvatarFrameShape
{
    Circle,
    /// <summary>Pointy-top hexagon.</summary>
    Hexagon
}


/// <summary>A heading plus the people under it. Add a section for new audio, testers, etc.</summary>
[Serializable]
public sealed class CreditsSection
{
    public string heading = "SECTION";
    [TextArea(1, 4)] public string intro = "";
    public List<CreditsPerson> people = new List<CreditsPerson>();
    [TextArea(1, 4)] public string outro = "";
}


/// <summary>
/// The whole credits roll: content (sections of CreditsPerson assets) and the
/// look (fonts, colours, frame shape, scroll speed). Edit this asset to change
/// the credits; nothing in the scene needs touching.
/// </summary>
[CreateAssetMenu(menuName = "Derelict Deliveries/Credits/Credits Roll", fileName = "CreditsRoll")]
public sealed class CreditsRoll : ScriptableObject
{
    [Header("Content")]
    public string title = "DERELICT DELIVERIES";
    [TextArea(1, 3)] public string tagline = "";
    public List<CreditsSection> sections = new List<CreditsSection>();
    [TextArea(1, 3)] public string closingLine = "Thanks for playing.";

    [Header("Fonts (empty = TMP default)")]
    public TMP_FontAsset titleFont;
    public TMP_FontAsset headingFont;
    public TMP_FontAsset nameFont;
    public TMP_FontAsset numberFont;
    public TMP_FontAsset bodyFont;
    public TMP_FontAsset linkFont;

    [Header("Font Sizes")]
    public float titleSize = 84f;
    public float taglineSize = 28f;
    public float headingSize = 44f;
    public float nameSize = 40f;
    public float numberSize = 28f;
    public float bodySize = 28f;
    public float citationSize = 20f;
    public float linkSize = 26f;

    [Header("Colours")]
    public Color titleColour = new Color32(0x42, 0xFF, 0x7B, 0xFF);
    public Color headingColour = new Color32(0x42, 0xFF, 0x7B, 0xFF);
    [Tooltip("Colour 1: names.")]
    public Color nameColour = new Color32(0xE8, 0xE6, 0xFF, 0xFF);
    [Tooltip("Colour 2: student numbers / handles.")]
    public Color numberColour = new Color32(0xFF, 0xB0, 0x00, 0xFF);
    [Tooltip("Colour 3: contributions.")]
    public Color contributionColour = new Color32(0xB3, 0xAD, 0xDB, 0xFF);
    public Color noteColour = new Color32(0x8F, 0x8A, 0xB8, 0xFF);
    public Color citationColour = new Color32(0x7A, 0x75, 0x9E, 0xFF);
    public Color linkColour = new Color32(0x67, 0xD8, 0xFF, 0xFF);
    public Color linkHoverColour = new Color32(0xFF, 0xFF, 0xFF, 0xFF);

    [Header("Avatar Frames")]
    public AvatarFrameShape frameShape = AvatarFrameShape.Hexagon;
    [Tooltip("Optional: take the border colour from the game's TerminalStyle (and style the BACK button with it).")]
    public TerminalStyle terminalStyle;
    [Tooltip("Used when Terminal Style is empty.")]
    public Color borderColour = new Color32(0x42, 0xFF, 0x7B, 0xFF);
    [Tooltip("Border width as a fraction of the frame radius.")]
    [Range(0.02f, 0.25f)] public float borderThickness = 0.07f;
    [Tooltip("Behind initials when someone has no avatar yet.")]
    public Color initialsBackground = new Color32(0x1A, 0x15, 0x33, 0xFF);
    [Min(16f)] public float avatarSize = 170f;
    [Min(8f)] public float linkIconSize = 40f;

    [Header("Background")]
    public Sprite background;
    public Color backgroundTint = Color.white;
    [Tooltip("Shown behind everything (and instead of the background if none is set).")]
    public Color backdropColour = new Color32(0x0D, 0x0A, 0x1F, 0xFF);

    [Header("Layout")]
    [Min(200f)] public float textColumnWidth = 900f;
    [Min(0f)] public float sectionSpacing = 140f;
    [Min(0f)] public float entrySpacing = 70f;

    [Header("Scrolling")]
    [Tooltip("Pixels per second (at 1080p reference).")]
    [Min(0f)] public float scrollSpeed = 70f;
    [Tooltip("Scroll speed is multiplied by this while a key, right/middle mouse button or gamepad button is held. Left click is left alone so links can be clicked. 1 = off.")]
    [Min(1f)] public float holdSpeedMultiplier = 4f;
    [Tooltip("Seconds to ease between normal and held speed (0 = instant).")]
    [Min(0f)] public float holdEaseSeconds = 0.2f;
    [Tooltip("Mouse wheel multiplier. Wheel scrolls back and forth manually.")]
    [Min(0f)] public float wheelSpeed = 1.5f;
    [Tooltip("After using the mouse wheel, auto-scroll waits this long before starting again.")]
    [Min(0f)] public float manualScrollPause = 2.5f;
    [Tooltip("Seconds for auto-scroll to ease back up to speed after a manual scroll.")]
    [Min(0f)] public float resumeEaseSeconds = 0.8f;
    [Min(0f)] public float startDelay = 0.4f;
    [Tooltip("On: start again from the bottom. Off: stop with the closing line on screen.")]
    public bool loop = true;

    public Color BorderColour => terminalStyle != null ? terminalStyle.GetPalette(false).primary : borderColour;
}
