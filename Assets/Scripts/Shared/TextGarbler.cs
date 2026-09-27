using System.Text;
using UnityEngine;

/// <summary>
/// The corrupted-name effect used by hover labels, as a reusable helper.
/// Same rules as PlanetLabelManager's private GenerateGarbledText /
/// GenerateUnknownText, so planet and event labels read the same way.
/// (PlanetLabelManager still has its own copy; it can switch to this later.)
/// </summary>
public static class TextGarbler
{
    public const string DefaultCharacters = "!@#$%^&*-_=+?<>01";


    /// <summary>
    /// Detected: spaces stay, every other character survives with revealChance,
    /// otherwise it is replaced by a random corruption character.
    /// </summary>
    public static string Garble(string realName, float revealChance, string garbleCharacters, int unknownFallbackLength = 5)
    {
        if (string.IsNullOrEmpty(realName)) return Unknown(string.Empty, garbleCharacters, unknownFallbackLength);
        if (string.IsNullOrEmpty(garbleCharacters)) return realName;

        StringBuilder builder = new StringBuilder(realName.Length);
        foreach (char character in realName)
        {
            if (character == ' ')
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(Random.value < revealChance
                ? character
                : garbleCharacters[Random.Range(0, garbleCharacters.Length)]);
        }
        return builder.ToString();
    }


    /// <summary>
    /// Unknown: no real letters, only corruption characters, keeping spaces and
    /// length so the label has the same footprint as the real name.
    /// </summary>
    public static string Unknown(string realName, string garbleCharacters, int unknownFallbackLength = 5)
    {
        int fallbackLength = Mathf.Max(1, unknownFallbackLength);

        if (string.IsNullOrEmpty(garbleCharacters))
            return new string('?', string.IsNullOrEmpty(realName) ? fallbackLength : realName.Length);

        StringBuilder builder = new StringBuilder(Mathf.Max(fallbackLength, realName?.Length ?? 0));

        if (string.IsNullOrEmpty(realName))
        {
            for (int i = 0; i < fallbackLength; i++)
                builder.Append(garbleCharacters[Random.Range(0, garbleCharacters.Length)]);
            return builder.ToString();
        }

        foreach (char character in realName)
        {
            builder.Append(character == ' '
                ? ' '
                : garbleCharacters[Random.Range(0, garbleCharacters.Length)]);
        }
        return builder.ToString();
    }
}
