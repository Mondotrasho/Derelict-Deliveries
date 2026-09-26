using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The planet UI (U1): the planet's banner (by tag, from BannerLibrary), its
/// name, and one dialogue-style button per option, plus Leave. Implements
/// IPoiPicker, so PointOfInterestController drives it exactly like the debug
/// picker: it only shows the list and reports the pick.
///
/// The window hides while the picked option runs (dialogue UI, event UI or
/// combat); the controller calls Pick again afterwards to show the list again.
/// </summary>
public sealed class PlanetPicker : MonoBehaviour, IPoiPicker
{
    private const string LeaveId = "__leave";

    [Header("References")]
    [Tooltip("The window this picker drives. Empty = a BannerChoiceView on this object or its children.")]
    [SerializeField] private BannerChoiceView view;
    [Tooltip("Planet banners by tag. Empty = no banner.")]
    [SerializeField] private BannerLibrary banners;

    [Header("Text")]
    [SerializeField] private string titlePrefix = "SYS://";
    [SerializeField] private bool upperCaseTitle = true;
    [SerializeField] private string status = "IN ORBIT";
    [TextArea(1, 3)] [SerializeField] private string prompt = "What do you want to do here?";
    [Tooltip("List each option's description under the prompt.")]
    [SerializeField] private bool listDescriptions = true;
    [SerializeField] private string leaveLabel = "LEAVE";

    public bool IsOpen { get; private set; }

    private readonly List<BannerChoiceView.Choice> buttons = new List<BannerChoiceView.Choice>();


    private void Awake()
    {
        if (view == null) view = GetComponentInChildren<BannerChoiceView>(true);
        if (view == null) Debug.LogWarning("PlanetPicker has no BannerChoiceView; planets will close straight away.", this);
    }


    public IEnumerator Pick(Planet planet, IReadOnlyList<PoiChoice> choices, Action<PoiChoice> picked)
    {
        if (view == null || planet == null || choices == null)
        {
            picked?.Invoke(null);
            yield break;
        }

        IsOpen = true;

        view.Show(
            banners != null ? banners.ResolvePlanet(planet) : null,
            titlePrefix + PlanetName(planet),
            status,
            Body(choices),
            false);

        buttons.Clear();
        for (int i = 0; i < choices.Count; i++)
        {
            if (choices[i] == null) continue;
            buttons.Add(new BannerChoiceView.Choice(i.ToString(), choices[i].Title));
        }
        buttons.Add(new BannerChoiceView.Choice(LeaveId, leaveLabel));
        view.SetChoices(buttons);

        string id = null;
        yield return view.WaitForChoice(c => id = c);

        view.Hide();
        IsOpen = false;

        PoiChoice result = null;
        if (id != null && id != LeaveId && int.TryParse(id, out int index) && index >= 0 && index < choices.Count)
        {
            result = choices[index];
        }
        picked?.Invoke(result);
    }


    public void Cancel()
    {
        if (!IsOpen || view == null) return;
        view.Hide();   // WaitForChoice then reports null = Leave
    }


    private string PlanetName(Planet planet)
    {
        string name = !string.IsNullOrWhiteSpace(planet.displayName) ? planet.displayName : planet.id.Replace('_', ' ');
        return upperCaseTitle ? name.ToUpperInvariant() : name;
    }


    private string Body(IReadOnlyList<PoiChoice> choices)
    {
        if (!listDescriptions) return prompt;

        var sb = new System.Text.StringBuilder(prompt);
        foreach (PoiChoice c in choices)
        {
            if (c == null || string.IsNullOrWhiteSpace(c.Description)) continue;
            sb.Append("\n- ").Append(c.Title).Append(": ").Append(c.Description.Trim());
        }
        return sb.ToString();
    }
}
