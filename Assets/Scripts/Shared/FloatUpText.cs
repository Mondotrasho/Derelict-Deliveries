using TMPro;
using UnityEngine;

/// <summary>
/// Small world-space text that rises and fades, then destroys itself, e.g.
/// "FUEL +10" when a pickup is collected. Uses TextMeshPro, so it can use the
/// same TMP font asset as the UI. No setup: call Spawn.
/// </summary>
public sealed class FloatUpText : MonoBehaviour
{
    private TextMeshPro text;
    private Color colour;
    private Vector3 start;
    private float rise;
    private float seconds;
    private float t;


    /// <param name="heightWorld">Line height in world units (e.g. 0.6 of a tile).</param>
    /// <param name="font">TMP font asset. Null = TextMeshPro's default font.</param>
    public static FloatUpText Spawn(Vector3 worldPosition, string message, Color colour, float heightWorld,
                                    TMP_FontAsset font = null, string sortingLayer = "Default", int sortingOrder = 200,
                                    float riseWorld = 1f, float seconds = 1.2f)
    {
        GameObject obj = new GameObject("FloatUpText");
        obj.transform.position = worldPosition;

        TextMeshPro tmp = obj.AddComponent<TextMeshPro>();
        if (font != null) tmp.font = font;
        tmp.text = message;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.fontSize = 10f;
        tmp.color = colour;
        tmp.sortingLayerID = SortingLayer.NameToID(sortingLayer);
        tmp.sortingOrder = sortingOrder;

        // Scale so one line is heightWorld tall, whatever the font's metrics.
        tmp.ForceMeshUpdate();
        float lineHeight = Mathf.Max(0.0001f, tmp.preferredHeight);
        obj.transform.localScale = Vector3.one * (Mathf.Max(0.001f, heightWorld) / lineHeight);

        FloatUpText floater = obj.AddComponent<FloatUpText>();
        floater.text = tmp;
        floater.colour = colour;
        floater.start = worldPosition;
        floater.rise = riseWorld;
        floater.seconds = Mathf.Max(0.05f, seconds);
        return floater;
    }


    private void Update()
    {
        t += Time.deltaTime;
        float k = Mathf.Clamp01(t / seconds);
        transform.position = start + Vector3.up * (rise * (1f - (1f - k) * (1f - k)));
        Color c = colour;
        c.a = colour.a * (k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f);
        text.color = c;
        if (k >= 1f) Destroy(gameObject);
    }
}
