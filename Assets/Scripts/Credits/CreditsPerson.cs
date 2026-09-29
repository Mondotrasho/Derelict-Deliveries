using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>How an image sits inside its round/hex frame.</summary>
public enum AvatarFit
{
    /// <summary>Fill the frame; whatever sticks out past the border is cropped.</summary>
    Cover,

    /// <summary>Show the whole image inside the frame (good for logos with text).</summary>
    Contain
}


/// <summary>One clickable link under a person, with an optional little avatar badge.</summary>
[Serializable]
public sealed class CreditLink
{
    [Tooltip("Text shown. Empty = the URL without https://")]
    public string label = "";
    public string url = "";

    [Tooltip("Optional small framed icon before the link (e.g. a different avatar used on that site).")]
    public Sprite icon;
    public AvatarFit iconFit = AvatarFit.Cover;
    [Tooltip("Colour behind the icon. Transparent = none. Use white for black-on-transparent logos.")]
    public Color iconBackground = Color.clear;

    public string DisplayLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(label)) return label;
            string u = url ?? "";
            if (u.StartsWith("https://")) u = u.Substring(8);
            else if (u.StartsWith("http://")) u = u.Substring(7);
            return u.TrimEnd('/');
        }
    }
}


/// <summary>
/// One person (or studio) in the credits. Reusable: the same asset can sit in
/// several sections or several rolls. Everything here is plain data - the
/// CreditsScreen builds the UI from it at runtime.
/// </summary>
[CreateAssetMenu(menuName = "Derelict Deliveries/Credits/Person", fileName = "CreditPerson")]
public sealed class CreditsPerson : ScriptableObject
{
    [Header("Text")]
    public string displayName = "Name";

    [Tooltip("Shown next to the name in the number colour: student number, username or @handle.")]
    public string idLine = "";

    [Tooltip("What they did. Contribution colour.")]
    [TextArea(2, 5)] public string contribution = "";

    [Tooltip("Optional extra line, e.g. usage terms. Note colour.")]
    [TextArea(1, 4)] public string note = "";

    [Tooltip("Optional reference-style citation, shown small underneath.")]
    [TextArea(2, 5)] public string citation = "";

    [Header("Avatar (any size / file type Unity imports; set Texture Type = Sprite)")]
    [Tooltip("Empty = the frame shows initials instead.")]
    public Sprite avatar;
    public AvatarFit avatarFit = AvatarFit.Cover;
    [Tooltip("Colour behind the avatar. Transparent = none (the space background shows through).")]
    public Color avatarBackground = Color.clear;
    [Tooltip("1 = normal. Bigger zooms in (crops more).")]
    [Range(0.25f, 3f)] public float avatarZoom = 1f;
    [Tooltip("Nudge the image inside the frame, as a fraction of the frame size.")]
    public Vector2 avatarOffset = Vector2.zero;
    [Tooltip("Initials shown when there is no avatar. Empty = worked out from the name.")]
    public string initialsOverride = "";

    [Header("Links (clickable)")]
    public List<CreditLink> links = new List<CreditLink>();

    public string Initials
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(initialsOverride)) return initialsOverride.Trim();
            if (string.IsNullOrWhiteSpace(displayName)) return "?";

            string result = "";
            foreach (string part in displayName.Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (char.IsLetter(part[0])) result += char.ToUpperInvariant(part[0]);
                if (result.Length == 2) break;
            }
            return result.Length > 0 ? result : "?";
        }
    }
}
