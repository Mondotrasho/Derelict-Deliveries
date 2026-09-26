using TMPro;
using UnityEngine;

/// <summary>
/// Small on-screen readout of the ship's resources: hull, fuel, crew and
/// supplies (the player counter EventKeys.Supplies). Builds its own text at
/// runtime and uses TerminalStyle, so it only needs a RectTransform under a Canvas.
/// Polls a few times a second because supplies have no change event yet.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public sealed class ResourceReadout : MonoBehaviour
{
    [SerializeField] private TerminalStyle style;
    [Tooltip("Found automatically if empty.")]
    [SerializeField] private PlayerShipState player;

    [Min(1f)] [SerializeField] private float fontSize = 22f;
    [Min(0.05f)] [SerializeField] private float refreshSeconds = 0.25f;
    [SerializeField] private TextAlignmentOptions alignment = TextAlignmentOptions.TopRight;

    [SerializeField] private bool showHull = true;
    [SerializeField] private bool showFuel = true;
    [SerializeField] private bool showCrew = true;
    [SerializeField] private bool showSupplies = true;

    private TextMeshProUGUI text;
    private float timer;


    private void Awake()
    {
        if (player == null) player = FindFirstObjectByType<PlayerShipState>();

        GameObject obj = new GameObject("__ReadoutText", typeof(RectTransform), typeof(TextMeshProUGUI));
        obj.transform.SetParent(transform, false);
        RectTransform rect = (RectTransform)obj.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        text = obj.GetComponent<TextMeshProUGUI>();
        text.raycastTarget = false;
        text.alignment = alignment;
        text.textWrappingMode = TextWrappingModes.NoWrap;

        Color colour = style != null ? style.GetPalette(false).primary : new Color32(0x42, 0xFF, 0x7B, 0xFF);
        if (style != null) style.ApplyText(text, colour, fontSize);
        else
        {
            text.color = colour;
            text.fontSize = fontSize;
        }

        Refresh();
    }


    private void Update()
    {
        timer += Time.unscaledDeltaTime;
        if (timer < refreshSeconds) return;
        timer = 0f;
        Refresh();
    }


    private void Refresh()
    {
        if (text == null) return;
        ShipResources r = player != null ? player.Resources : null;
        var sb = new System.Text.StringBuilder();

        if (r != null)
        {
            if (showHull) sb.Append($"HULL {r.HullIntegrity:0}/{r.MaxHullIntegrity:0}\n");
            if (showFuel) sb.Append($"FUEL {r.Fuel:0}/{r.MaxFuel:0}\n");
            if (showCrew) sb.Append($"CREW {r.Crew}/{r.MaxCrew}\n");
        }
        if (showSupplies && player != null && player.EventState != null)
        {
            sb.Append($"SUPPLIES {player.EventState.GetCounter(EventKeys.Supplies)}\n");
        }

        text.text = sb.ToString().TrimEnd('\n');
    }
}
