using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Makes &lt;link="url"&gt; tags in a TextMeshPro text clickable (opens the URL
/// in the browser) and highlights the text while hovered. Added by
/// CreditsScreen; can also go on any TMP text by hand.
/// </summary>
[RequireComponent(typeof(TMP_Text))]
public sealed class CreditsLinkText : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
{
    [SerializeField] private Color normalColour = Color.cyan;
    [SerializeField] private Color hoverColour = Color.white;

    private TMP_Text text;

    public void Setup(Color normal, Color hover)
    {
        normalColour = normal;
        hoverColour = hover;
        if (text == null) text = GetComponent<TMP_Text>();
        text.color = normalColour;
    }


    private void Awake()
    {
        text = GetComponent<TMP_Text>();
        text.raycastTarget = true;
    }


    public void OnPointerClick(PointerEventData eventData)
    {
        int index = TMP_TextUtilities.FindIntersectingLink(text, eventData.position, eventData.pressEventCamera);
        if (index < 0 && text.textInfo.linkCount == 1) index = 0;   // clicked the padding around a single link
        if (index < 0) return;

        string url = text.textInfo.linkInfo[index].GetLinkID();
        if (url.StartsWith("http://") || url.StartsWith("https://")) Application.OpenURL(url);
    }


    public void OnPointerEnter(PointerEventData eventData) => text.color = hoverColour;

    public void OnPointerExit(PointerEventData eventData) => text.color = normalColour;

    private void OnDisable()
    {
        if (text != null) text.color = normalColour;
    }
}
