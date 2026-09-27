using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Keeps the game at a fixed shape (16:9 by default) in any window or monitor,
/// with black bars filling the rest, and frames the whole map in it.
///
/// Put it on the Main Camera. Every time the window size changes it:
///   1. sets the camera's viewport to the largest 16:9 rectangle that fits,
///      centred - bars left/right on wide screens, top/bottom on tall ones.
///      A background camera clears the bars to Bar Colour.
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
    private Camera barCamera;
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

        CreateBarCamera();
        CreateUiFrame();
        Apply();
    }


    private void LateUpdate()
    {
        if (Screen.width != lastWidth || Screen.height != lastHeight) Apply();
        SweepCanvasIntoFrame();
    }


    // ------------------------------------------------------------------ bars
    private void CreateBarCamera()
    {
        var go = new GameObject("Letterbox Bars (Generated)");
        go.transform.SetParent(transform, false);
        barCamera = go.AddComponent<Camera>();
        barCamera.clearFlags = CameraClearFlags.SolidColor;
        barCamera.backgroundColor = barColour;
        barCamera.cullingMask = 0;                   // draws nothing, only clears
        barCamera.orthographic = true;
        barCamera.depth = cam.depth - 100f;          // renders before the main camera
        barCamera.rect = new Rect(0f, 0f, 1f, 1f);
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
        cam.rect = r;

        if (fitMap) FitMap(target);

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
        cam.orthographicSize = Mathf.Max(halfHeight, halfWidth / aspect);

        Vector3 p = transform.position;
        transform.position = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, p.z);
    }
}
