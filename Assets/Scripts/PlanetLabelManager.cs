using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Creates and updates one PlanetLabel per planet, following world
/// knowledge rather than raw rendering:
///
/// hidden and never remembered  -> no label
/// knowledgeState Unknown       -> fixed placeholder text, no real data leaked
/// knowledgeState Detected      -> garbled name; low reveal chance at Partial
///                                  vision, high reveal chance at Full
/// knowledgeState Identified    -> real name
///
/// Location-known (Discovered/RememberLocation) and identity-known
/// (knowledgeState) are deliberately separate: a remembered-but-only-
/// Detected planet still shows a garbled label even while comfortably
/// inside remembered fog. Fog-lock participation
/// (Planet.revealFogWhenDiscovered) is a further separate concern this
/// ignores entirely - a moon with fog reveal disabled still gets a label
/// exactly like any other planet.
/// </summary>
public class PlanetLabelManager : MonoBehaviour
{
    [Header("References")]

    [SerializeField]
    private PlanetManager planetManager;

    [SerializeField]
    private VisionManager visionManager;


    [Header("Label Look")]

    [Tooltip("Leave empty to use TextMesh's built-in default font (smooth vector text). Assign a bitmap Font asset for a pixel-accurate look matching the rest of the project.")]
    [SerializeField]
    private Font labelFont;

    [SerializeField]
    private int fontSize = 12;

    [Tooltip("TextMesh characters are large by default - this scales the whole label down to fit a small-scale pixel-art world.")]
    [SerializeField]
    private float labelScale = 0.02f;

    [SerializeField]
    private Vector3 labelOffset = new Vector3(0.0f, 0.6f, 0.0f);

    [SerializeField]
    private Color identifiedColor = Color.white;

    [SerializeField]
    private Color garbledColor = new Color(0.6f, 0.85f, 1.0f);

    [SortingLayerName]
    [SerializeField]
    private string sortingLayerName = "Default";

    [SerializeField]
    private int sortingOrder = 20;


    [Header("Garble")]

    [Tooltip("Chance each character shows correctly at knowledgeState Detected while the planet is only in Partial vision.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float partialRevealChance = 0.35f;

    [Tooltip("Chance each character shows correctly at knowledgeState Detected while the planet is in Full vision.")]
    [Range(0f, 1f)]
    [SerializeField]
    private float fullRevealChance = 0.85f;

    [Tooltip("How often garbled labels regenerate their static, in seconds. Too fast reads as unreadable noise rather than an intentional flicker.")]
    [SerializeField]
    private float garbleRefreshInterval = 0.35f;

    [SerializeField]
    private string garbleCharacters = "!@#$%^&*-_=+?<>01";

    [Tooltip("Shown for knowledgeState Unknown - a planet whose location is remembered but whose identity was never actually detected. Should rarely if ever be seen in normal play, since remembering a location almost always implies at least Detected.")]
    [SerializeField]
    private string unknownLabelText = "?????";


    private readonly Dictionary<string, PlanetLabel> labelsByPlanetId =
        new Dictionary<string, PlanetLabel>();

    private float garbleTimer;


    private void Update()
    {
        if (planetManager == null || visionManager == null)
        {
            return;
        }


        garbleTimer += Time.deltaTime;

        bool refreshGarble =
            garbleTimer >= garbleRefreshInterval;

        if (refreshGarble)
        {
            garbleTimer = 0.0f;
        }


        foreach (Planet planet in planetManager.Planets)
        {
            UpdateLabelForPlanet(planet, refreshGarble);
        }
    }


    private void UpdateLabelForPlanet(Planet planet, bool refreshGarble)
    {
        FogOfWar.VisibilityTier liveTier =
            visionManager.GetLiveVisibility(planet.cell);

        bool isKnown =
            planet.visibility.knowledgeState !=
            PlanetKnowledgeState.Unknown;

        bool isRemembered =
            isKnown &&
            planet.visibility.rememberLocation;

        if (liveTier == FogOfWar.VisibilityTier.Hidden &&
            !isRemembered)
        {
            HideLabel(planet.id);
            return;
        }


        PlanetLabel label = GetOrCreateLabel(planet.id);

        label.SetWorldPosition(
            planetManager.GetPlanetWorldPosition(planet) +
            labelOffset
        );

        label.SetActive(true);


        switch (planet.visibility.knowledgeState)
        {
            case PlanetKnowledgeState.Identified:

                label.SetText(
                    GetDisplayName(planet),
                    identifiedColor
                );

                return;


            case PlanetKnowledgeState.Unknown:

                // Fixed text, nothing to regenerate - this only changes
                // when knowledgeState itself changes, not on the garble timer.
                if (!label.HasText)
                {
                    label.SetText(unknownLabelText, garbledColor);
                }

                return;


            case PlanetKnowledgeState.Detected:
            default:

                if (!refreshGarble && label.HasText)
                {
                    // Not due for a refresh yet - leave the current
                    // garbled text as-is rather than regenerating it
                    // every frame.
                    return;
                }

                float revealChance =
                    liveTier == FogOfWar.VisibilityTier.Full
                        ? fullRevealChance
                        : partialRevealChance;

                label.SetText(
                    GenerateGarbledText(
                        GetDisplayName(planet),
                        revealChance
                    ),
                    garbledColor
                );

                return;
        }
    }


    private string GetDisplayName(Planet planet)
    {
        return string.IsNullOrWhiteSpace(planet.displayName)
            ? planet.id
            : planet.displayName;
    }


    /// <summary>
    /// Replaces each non-space character with a random garble character,
    /// with revealChance probability of keeping the real one instead.
    /// Never alters the planet's actual name/id - purely a display string.
    /// </summary>
    private string GenerateGarbledText(string realName, float revealChance)
    {
        StringBuilder builder = new StringBuilder(realName.Length);

        foreach (char character in realName)
        {
            if (character == ' ')
            {
                builder.Append(' ');
                continue;
            }

            if (Random.value < revealChance)
            {
                builder.Append(character);
            }
            else
            {
                builder.Append(
                    garbleCharacters[
                        Random.Range(0, garbleCharacters.Length)
                    ]
                );
            }
        }

        return builder.ToString();
    }


    private PlanetLabel GetOrCreateLabel(string planetId)
    {
        if (labelsByPlanetId.TryGetValue(planetId, out PlanetLabel existing))
        {
            return existing;
        }

        PlanetLabel label =
            PlanetLabel.Create(
                transform,
                labelFont,
                fontSize,
                labelScale,
                ResolveSortingLayerName(sortingLayerName),
                sortingOrder
            );

        labelsByPlanetId.Add(planetId, label);

        return label;
    }


    private void HideLabel(string planetId)
    {
        if (labelsByPlanetId.TryGetValue(planetId, out PlanetLabel label))
        {
            label.SetActive(false);
        }
    }


    /// <summary>
    /// Falls back to Default if the configured sorting layer no longer
    /// exists, matching the convention used by FogOfWar/PlanetManager/
    /// StarfieldPainter/RoutePathRenderer.
    /// </summary>
    private string ResolveSortingLayerName(string requestedName)
    {
        foreach (SortingLayer layer in SortingLayer.layers)
        {
            if (layer.name == requestedName)
            {
                return requestedName;
            }
        }

        Debug.LogWarning(
            $"{name}: Sorting Layer \"{requestedName}\" does not exist. Falling back to Default.",
            this
        );

        return "Default";
    }
}