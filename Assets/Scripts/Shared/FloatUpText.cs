using UnityEngine;

/// <summary>
/// Small world-space text that rises and fades, then destroys itself, e.g.
/// "FUEL +10" when a pickup is collected. No setup: call Spawn.
/// </summary>
public sealed class FloatUpText : MonoBehaviour
{
    private TextMesh textMesh;
    private Color colour;
    private Vector3 start;
    private float rise;
    private float seconds;
    private float t;


    /// <param name="heightWorld">Rough line height in world units (e.g. 0.6 of a tile).</param>
    public static FloatUpText Spawn(Vector3 worldPosition, string text, Color colour, float heightWorld,
                                    Font font = null, string sortingLayer = "Default", int sortingOrder = 200,
                                    float riseWorld = 1f, float seconds = 1.2f)
    {
        GameObject obj = new GameObject("FloatUpText");
        obj.transform.position = worldPosition;

        TextMesh mesh = obj.AddComponent<TextMesh>();
        mesh.text = text;
        mesh.anchor = TextAnchor.MiddleCenter;
        mesh.alignment = TextAlignment.Center;
        mesh.fontSize = 48;
        mesh.characterSize = Mathf.Max(0.001f, heightWorld / (mesh.fontSize * 0.1f));
        mesh.color = colour;
        if (font != null)
        {
            mesh.font = font;
            obj.GetComponent<MeshRenderer>().material = font.material;
        }

        MeshRenderer renderer = obj.GetComponent<MeshRenderer>();
        renderer.sortingLayerName = sortingLayer;
        renderer.sortingOrder = sortingOrder;

        FloatUpText floater = obj.AddComponent<FloatUpText>();
        floater.textMesh = mesh;
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
        textMesh.color = c;
        if (k >= 1f) Destroy(gameObject);
    }
}
