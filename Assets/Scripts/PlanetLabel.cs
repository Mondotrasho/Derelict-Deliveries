using UnityEngine;

/// <summary>
/// Owns one visual planet label - a small world-space TextMesh positioned
/// above a planet.
///
/// This is presentation only: it has no idea what a Planet is, what fog
/// or discovery mean, or when it should be showing garbled vs identified
/// text. PlanetLabelManager decides all of that and just tells this
/// object what to display.
///
/// Deliberately a plain class rather than a MonoBehaviour - it creates
/// and owns its own GameObject/TextMesh internally, so PlanetLabelManager
/// can pool these the same way RoutePathRenderer pools its dash dots,
/// with zero manual scene setup required.
/// </summary>
public class PlanetLabel
{
    private readonly GameObject labelObject;
    private readonly TextMesh textMesh;

    private string currentText;


    /// <summary>
    /// True once this label has ever been given text - lets the manager
    /// tell "never generated a garble yet" apart from "due for a refresh".
    /// </summary>
    public bool HasText
    {
        get
        {
            return !string.IsNullOrEmpty(currentText);
        }
    }


    private PlanetLabel(GameObject labelObject, TextMesh textMesh)
    {
        this.labelObject = labelObject;
        this.textMesh = textMesh;
    }


    /// <summary>
    /// Builds one label as a child of parent, starting inactive.
    ///
    /// font can be left null to use TextMesh's built-in default font -
    /// note that will look like ordinary smooth vector text, not the
    /// crunchy pixel-art look the rest of this project generates
    /// procedurally. For a genuinely pixel-perfect label, assign a
    /// bitmap Font asset here.
    /// </summary>
    public static PlanetLabel Create(
        Transform parent,
        Font font,
        int fontSize,
        float scale,
        string sortingLayerName,
        int sortingOrder)
    {
        GameObject labelObject = new GameObject("PlanetLabel");

        labelObject.transform.SetParent(
            parent,
            false
        );

        labelObject.transform.localScale =
            Vector3.one * scale;


        TextMesh textMesh =
            labelObject.AddComponent<TextMesh>();

        textMesh.alignment = TextAlignment.Center;
        textMesh.anchor = TextAnchor.LowerCenter;
        textMesh.fontSize = fontSize;
        textMesh.characterSize = 1.0f;

        if (font != null)
        {
            textMesh.font = font;

            labelObject.GetComponent<MeshRenderer>().material =
                font.material;
        }


        MeshRenderer meshRenderer =
            labelObject.GetComponent<MeshRenderer>();

        meshRenderer.sortingLayerName = sortingLayerName;
        meshRenderer.sortingOrder = sortingOrder;


        labelObject.SetActive(false);

        return new PlanetLabel(labelObject, textMesh);
    }


    public void SetWorldPosition(Vector3 worldPosition)
    {
        labelObject.transform.position = worldPosition;
    }


    public void SetActive(bool active)
    {
        labelObject.SetActive(active);
    }


    public void SetText(string text, Color color)
    {
        currentText = text;

        textMesh.text = text;
        textMesh.color = color;
    }
}