using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Keeps the game at a fixed shape (16:9 by default) in any window or monitor,
/// with black bars filling the rest, and frames the whole map in it.
///
/// Put it on the Main Camera. Every time the window size changes it:
///   1. works out the largest 16:9 rectangle that fits, centred - bars
///      left/right on wide screens, top/bottom on tall ones - and covers the
///      rest with solid bars on a top-most overlay canvas (they also block
///      clicks, so no routes get plotted into the black). The camera itself
///      stays full-screen - URP's 2D renderer mis-draws some sprites (the
///      starfield) with a reduced camera viewport - and its zoom is adjusted so
///      the 16:9 area always shows the same slice of the world.
///   2. (Fit Map) centres the camera on the ground tilemap and zooms so the
///      whole map fits, plus Padding Cells.
///   3. keeps the UI inside the picture: at start everything under the Canvas
///      moves into a "GameFrame" rectangle matching the camera viewport, and the
///      Canvas scale is set so that frame is exactly Reference Height (1080) UI
///      units tall - the 1920x1080 layouts fit the picture at any size.
///      Anything a script adds to the Canvas later is moved in too.
///
/// Mouse clicks are unaffected: Unity's ScreenToWorldPoint already accounts for
/// the camera's viewport.
/// </summary>
[RequireComponent(typeof(Camera))]
[DisallowMultipleComponent]
public sealed class GameViewFrame : MonoBehaviour
{
    [Header("Shape")]
    [SerializeField] private Vector2 targetAspect = new Vector2(16f, 9f);
    [SerializeField] private Color barColour = Color.black;

    [Header("Map")]
    [Tooltip("Zoom and centre the camera so the whole ground tilemap is visible.")]
    [SerializeField] private bool fitMap = true;
    [SerializeField] private GridMap gridMap;
    [Tooltip("Empty space around the map, in cells.")]
    [Min(0f)] [SerializeField] private float paddingCells = 1f;

    [Header("UI")]
    [Tooltip("The Screen Space Overlay canvas whose UI should stay inside the picture.")]
    [SerializeField] private Canvas uiCanvas;
    [Tooltip("UI layouts were designed for this height (1920x1080 -> 1080).")]
    [Min(1f)] [SerializeField] private float referenceHeight = 1080f;

    private Camera cam;
    private float baseOrthoSize;
    private RectTransform barLeft, barRight, barTop, barBottom;
    private RectTransform frame;
    private CanvasScaler scaler;
    private int lastWidth = -1, lastHeight = -1;


    private void Awake()
    {
        cam = GetComponent<Camera>();
        if (gridMap == null) gridMap = FindFirstObjectByType<GridMap>();
        if (uiCanvas == null)
        {
            foreach (Canvas c in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (c.isRootCanvas && c.renderMode == RenderMode.ScreenSpaceOverlay) { uiCanvas = c; break; }
            }
        }

        baseOrthoSize = cam.orthographicSize;      // the 16:9 view as set up in the scene
        cam.rect = new Rect(0f, 0f, 1f, 1f);
        CreateBars();
        CreateUiFrame();
        Apply();
    }


    private void LateUpdate()
    {
        if (Screen.width != lastWidth || Screen.height != lastHeight) Apply();
        SweepCanvasIntoFrame();
    }


    // ------------------------------------------------------------------ bars
    private void CreateBars()
    {
        var go = new GameObject("Letterbox Bars (Generated)", typeof(RectTransform), typeof(Canvas));
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;                   // above every other UI
        go.AddComponent<GraphicRaycaster>();           // so the bars swallow clicks
        barLeft = Bar(go.transform, "Left");
        barRight = Bar(go.transform, "Right");
        barTop = Bar(go.transform, "Top");
        barBottom = Bar(go.transform, "Bottom");
    }


    private RectTransform Bar(Transform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.color = barColour;
        img.raycastTarget = true;
        var rt = (RectTransform)go.transform;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        return rt;
    }


    private static void Place(RectTransform rt, float xMin, float yMin, float xMax, float yMax)
    {
        rt.anchorMin = new Vector2(xMin, yMin);
        rt.anchorMax = new Vector2(xMax, yMax);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        rt.gameObject.SetActive(xMax > xMin && yMax > yMin);
    }


    // -------------------------------------------------------------------- UI
    private void CreateUiFrame()
    {
        if (uiCanvas == null) return;
        scaler = uiCanvas.GetComponent<CanvasScaler>();

        var go = new GameObject("GameFrame", typeof(RectTransform));
        frame = (RectTransform)go.transform;
        frame.SetParent(uiCanvas.transform, false);
        frame.pivot = new Vector2(0.5f, 0.5f);
        SweepCanvasIntoFrame();
    }


    private void SweepCanvasIntoFrame()
    {
        if (frame == null) return;
        Transform root = uiCanvas.transform;
        for (int i = 0; i < root.childCount; )
        {
            Transform child = root.GetChild(i);
            if (child == frame) { i++; continue; }
            child.SetParent(frame, false);       // keeps its anchors, now relative to the frame
        }
    }


    // ------------------------------------------------------------------ apply
    private void Apply()
    {
        lastWidth = Screen.width;
        lastHeight = Screen.height;
        if (lastWidth <= 0 || lastHeight <= 0) return;

        float target = targetAspect.x / Mathf.Max(0.01f, targetAspect.y);
        float screen = (float)lastWidth / lastHeight;
        Rect r;
        if (screen > target)
        {
            float w = target / screen;                   // bars left and right
            r = new Rect((1f - w) * 0.5f, 0f, w, 1f);
        }
        else
        {
            float h = screen / target;                   // bars top and bottom
            r = new Rect(0f, (1f - h) * 0.5f, 1f, h);
        }
        if (fitMap) FitMap(target);

        // Full-screen camera; on screens taller than 16:9 zoom out so the 16:9
        // band still spans the same world width. Wider screens keep the zoom -
        // the bars simply cover the extra width.
        cam.rect = new Rect(0f, 0f, 1f, 1f);
        cam.orthographicSize = screen < target ? baseOrthoSize * target / screen : baseOrthoSize;

        Place(barLeft, 0f, 0f, r.xMin, 1f);
        Place(barRight, r.xMax, 0f, 1f, 1f);
        Place(barBottom, r.xMin, 0f, r.xMax, r.yMin);
        Place(barTop, r.xMin, r.yMax, r.xMax, 1f);

        if (frame != null)
        {
            frame.anchorMin = new Vector2(r.xMin, r.yMin);
            frame.anchorMax = new Vector2(r.xMax, r.yMax);
            frame.offsetMin = frame.offsetMax = Vector2.zero;

            // UI scale from the picture's height, not the window's
            if (scaler != null)
            {
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = r.height * lastHeight / referenceHeight;
            }
        }
    }


    private void FitMap(float aspect)
    {
        if (gridMap == null) return;
        BoundsInt cells = gridMap.GroundBounds;
        if (cells.size.x <= 0 || cells.size.y <= 0) return;

        Vector3 a = gridMap.CellToWorld(new Vector3Int(cells.xMin, cells.yMin, 0));
        Vector3 b = gridMap.CellToWorld(new Vector3Int(cells.xMax - 1, cells.yMax - 1, 0));
        float cell = Vector3.Distance(gridMap.CellToWorld(Vector3Int.zero), gridMap.CellToWorld(Vector3Int.right));
        if (cell <= 0f) cell = 1f;

        float pad = paddingCells * cell + cell * 0.5f;      // half a cell: CellToWorld gives centres
        float minX = Mathf.Min(a.x, b.x) - pad, maxX = Mathf.Max(a.x, b.x) + pad;
        float minY = Mathf.Min(a.y, b.y) - pad, maxY = Mathf.Max(a.y, b.y) + pad;

        float halfHeight = (maxY - minY) * 0.5f;
        float halfWidth = (maxX - minX) * 0.5f;
        baseOrthoSize = Mathf.Max(halfHeight, halfWidth / aspect);

        Vector3 p = transform.position;
        transform.position = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, p.z);
    }
}
