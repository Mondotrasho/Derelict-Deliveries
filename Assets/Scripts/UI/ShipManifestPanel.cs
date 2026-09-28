using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The ship panel opened by the HUD's INVENTORY button: ShipUI.psd with the
/// crew and passengers from ShipCrewManifest in its 12 slots.
///
///   - Opens / closes with the INVENTORY button; closes on Escape or a click
///     outside the ship (a full-screen blocker also stops that click reaching
///     the map).
///   - Drag a portrait to another slot: officers to any free slot, passengers
///     only to free berths. Valid slots light up; an invalid drop snaps back.
///   - Hover a portrait for name and role.
///   - Sized to Max Screen Fraction (or whole-pixel steps / a forced scale, see Look).
/// Put it on the ShipHud object; it builds its UI on first open.
/// </summary>
[DisallowMultipleComponent]
public sealed class ShipManifestPanel : MonoBehaviour
{
    [Header("References (found automatically if empty)")]
    [SerializeField] private ShipCrewManifest manifest;
    [SerializeField] private ShipHud hud;
    [SerializeField] private TerminalStyle style;

    [Header("Art")]
    [Tooltip("ShipUI.psd")]
    [SerializeField] private Sprite shipSprite;
    [Tooltip("Largest share of the screen the ship may use (it then snaps down to a whole pixel scale).")]
    [Range(0.3f, 1f)] [SerializeField] private float maxScreenFraction = 0.85f;
    [Tooltip("On: scale in whole steps (1x, 2x, 3x) so every pixel is the same size - at 1080p that means 1x, because 2x (1146px tall) doesn't fit. Off: fill Max Screen Fraction exactly (a little uneven pixel-wise, but bigger).")]
    [SerializeField] private bool wholePixelScale = false;
    [Tooltip("Force a size: sprite pixels are drawn this many screen pixels big (e.g. 1.5, 2). 0 = automatic.")]
    [Min(0f)] [SerializeField] private float scaleOverride = 0f;
    [Tooltip("Size portraits are drawn at, in sprite pixels (the cropped portraits are 60).")]
    [Min(8)] [SerializeField] private int portraitPixels = 60;

    [Header("Look")]
    [SerializeField] private Color blockerColour = new Color(0f, 0f, 0f, 0.55f);
    [SerializeField] private Color validSlotColour = new Color(0.4f, 1f, 0.6f, 0.28f);
    [SerializeField] private Color targetSlotColour = new Color(0.4f, 1f, 0.6f, 0.6f);

    // Slot rectangles inside ShipUI.psd, in pixels from its top-left (measured
    // from the art: the dark fill inside each frame). Order = ShipCrewManifest slots.
    private static readonly RectInt[] SlotRects =
    {
        new RectInt(184, 32, 66, 66),   // 0 helm
        new RectInt(13, 337, 66, 66),   // 1 gunnery (left pod)
        new RectInt(357, 337, 66, 66),  // 2 navigation (right pod)
        new RectInt(184, 455, 66, 66),  // 3 engineering
        new RectInt(149, 119, 66, 66), new RectInt(221, 119, 66, 66),   // berths
        new RectInt(149, 190, 66, 66), new RectInt(221, 190, 66, 66),
        new RectInt(149, 263, 66, 66), new RectInt(221, 263, 66, 66),
        new RectInt(149, 335, 66, 66), new RectInt(221, 335, 66, 66),
    };

    public bool IsOpen => root != null && root.gameObject.activeSelf;

    private RectTransform root, ship, tooltip, ghost;
    private TMP_Text tooltipText;
    private Canvas canvas;
    private readonly List<ShipManifestSlot> slots = new List<ShipManifestSlot>();
    private readonly List<Image> portraits = new List<Image>();
    private readonly List<Image> highlights = new List<Image>();
    private int dragFrom = -1, hoverSlot = -1;
    private float unit = 1f;   // canvas units per sprite pixel


    private void Awake()
    {
        if (hud == null) hud = GetComponent<ShipHud>();
        if (hud == null) hud = FindFirstObjectByType<ShipHud>();
        if (manifest == null) manifest = FindFirstObjectByType<ShipCrewManifest>();
        canvas = GetComponentInParent<Canvas>();
        if (canvas != null) canvas = canvas.rootCanvas;
    }


    private void OnEnable()
    {
        if (hud != null) hud.InventoryPressed += Toggle;
        if (manifest != null) manifest.Changed += Refresh;
    }


    private void OnDisable()
    {
        if (hud != null) hud.InventoryPressed -= Toggle;
        if (manifest != null) manifest.Changed -= Refresh;
        Close();
    }


    private void Update()
    {
        if (IsOpen && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) Close();
    }


    // ------------------------------------------------------------ open / close
    public void Toggle()
    {
        if (IsOpen) Close(); else Open();
    }


    public void Open()
    {
        if (shipSprite == null)
        {
            Debug.LogWarning("ShipManifestPanel: no Ship Sprite assigned.", this);
            return;
        }
        if (root == null) Build();
        root.gameObject.SetActive(true);
        root.SetAsLastSibling();
        Layout();
        Refresh();
    }


    public void Close()
    {
        CancelDrag();
        if (root != null) root.gameObject.SetActive(false);
        hoverSlot = -1;
        if (tooltip != null) tooltip.gameObject.SetActive(false);
    }


    // ------------------------------------------------------------------ build
    private void Build()
    {
        TerminalStyle.Palette palette = style != null ? style.GetPalette(false) : default;

        root = NewRect("ShipManifest", transform);
        Stretch(root);

        Image blocker = root.gameObject.AddComponent<Image>();
        blocker.color = blockerColour;
        Button closer = root.gameObject.AddComponent<Button>();
        closer.transition = Selectable.Transition.None;
        closer.onClick.AddListener(Close);

        ship = NewRect("Ship", root);
        ship.anchorMin = ship.anchorMax = ship.pivot = new Vector2(0.5f, 0.5f);
        Image shipImage = ship.gameObject.AddComponent<Image>();
        shipImage.sprite = shipSprite;
        shipImage.raycastTarget = true;   // clicks on the ship don't close it

        for (int i = 0; i < SlotRects.Length; i++)
        {
            RectTransform s = NewRect("Slot" + i, ship);
            s.anchorMin = s.anchorMax = s.pivot = new Vector2(0f, 1f);
            Image hit = s.gameObject.AddComponent<Image>();
            hit.color = Color.clear;

            Image hl = NewRect("Highlight", s).gameObject.AddComponent<Image>();
            Stretch(hl.rectTransform);
            hl.raycastTarget = false;
            hl.enabled = false;
            highlights.Add(hl);

            RectTransform pr = NewRect("Portrait", s);
            pr.anchorMin = pr.anchorMax = pr.pivot = new Vector2(0.5f, 0.5f);
            Image p = pr.gameObject.AddComponent<Image>();
            p.preserveAspect = true;
            p.raycastTarget = false;
            portraits.Add(p);

            ShipManifestSlot slot = s.gameObject.AddComponent<ShipManifestSlot>();
            slot.panel = this;
            slot.index = i;
            slots.Add(slot);
        }

        tooltip = NewRect("Tooltip", root);
        tooltip.pivot = new Vector2(0.5f, 0f);
        tooltip.anchorMin = tooltip.anchorMax = new Vector2(0.5f, 0.5f);
        Image tipBg = tooltip.gameObject.AddComponent<Image>();
        if (style != null) style.ApplyPanel(tipBg, palette); else tipBg.color = new Color(0f, 0f, 0f, 0.85f);
        tipBg.raycastTarget = false;
        tooltipText = NewRect("Text", tooltip).gameObject.AddComponent<TextMeshProUGUI>();
        Stretch(tooltipText.rectTransform);
        tooltipText.margin = new Vector4(8f, 3f, 8f, 3f);
        tooltipText.alignment = TextAlignmentOptions.Center;
        tooltipText.fontSize = 18f;
        tooltipText.raycastTarget = false;
        if (style != null)
        {
            tooltipText.font = style.Font;
            tooltipText.color = palette.primary;
        }
        tooltip.gameObject.SetActive(false);

        root.gameObject.SetActive(false);
    }


    /// <summary>Whole-number pixel scale that fits the screen, then place the slots.</summary>
    private void Layout()
    {
        float sf = canvas != null ? Mathf.Max(0.0001f, canvas.scaleFactor) : 1f;
        Vector2 px = shipSprite.rect.size;
        float fit = Mathf.Min(Screen.width * maxScreenFraction / px.x, Screen.height * maxScreenFraction / px.y);
        float k = scaleOverride > 0f ? scaleOverride
                : wholePixelScale ? Mathf.Max(1, Mathf.FloorToInt(fit))
                : Mathf.Max(0.25f, fit);
        unit = k / sf;                       // canvas units per sprite pixel
        ship.sizeDelta = px * unit;
        ship.anchoredPosition = Vector2.zero;

        for (int i = 0; i < slots.Count; i++)
        {
            RectInt r = SlotRects[i];
            var rt = (RectTransform)slots[i].transform;
            rt.anchoredPosition = new Vector2(r.x, -r.y) * unit;
            rt.sizeDelta = new Vector2(r.width, r.height) * unit;
            portraits[i].rectTransform.sizeDelta = Vector2.one * portraitPixels * unit;
        }
    }


    private void Refresh()
    {
        if (!IsOpen || manifest == null) return;
        for (int i = 0; i < portraits.Count; i++)
        {
            ShipCrewManifest.Occupant o = manifest.Get(i);
            Sprite s = manifest.PortraitOf(o);
            portraits[i].sprite = s;
            portraits[i].enabled = s != null;
            portraits[i].color = i == dragFrom ? new Color(1f, 1f, 1f, 0.3f) : Color.white;
        }
        UpdateHighlights();
        if (hoverSlot >= 0) ShowTooltip(hoverSlot);
    }


    private void UpdateHighlights()
    {
        for (int i = 0; i < highlights.Count; i++)
        {
            bool valid = dragFrom >= 0 && manifest != null && manifest.CanMove(dragFrom, i);
            highlights[i].enabled = valid;
            if (valid) highlights[i].color = i == hoverSlot ? targetSlotColour : validSlotColour;
        }
    }


    // ---------------------------------------------------------------- hover
    public void SlotHover(ShipManifestSlot slot, bool entered)
    {
        if (entered) hoverSlot = slot.index;
        else if (hoverSlot == slot.index) hoverSlot = -1;

        if (dragFrom >= 0) { UpdateHighlights(); return; }
        if (hoverSlot >= 0) ShowTooltip(hoverSlot);
        else if (tooltip != null) tooltip.gameObject.SetActive(false);
    }


    private void ShowTooltip(int index)
    {
        ShipCrewManifest.Occupant o = manifest != null ? manifest.Get(index) : null;
        if (o == null || dragFrom >= 0)
        {
            tooltip.gameObject.SetActive(false);
            return;
        }
        tooltipText.text = $"{manifest.NameOf(o)}\n<size=75%><alpha=#AA>{manifest.RoleOf(o)}</size>";
        tooltip.gameObject.SetActive(true);
        tooltip.SetAsLastSibling();

        RectInt r = SlotRects[index];
        Vector2 shipTopLeft = ship.anchoredPosition + new Vector2(-ship.sizeDelta.x, ship.sizeDelta.y) * 0.5f;
        Vector2 slotTopCentre = shipTopLeft + new Vector2(r.x + r.width * 0.5f, -r.y) * unit;
        tooltip.anchoredPosition = slotTopCentre + new Vector2(0f, 4f);
        Vector2 size = tooltipText.GetPreferredValues(tooltipText.text) + new Vector2(16f, 6f);
        tooltip.sizeDelta = new Vector2(Mathf.Max(120f, size.x), size.y);
    }


    // ----------------------------------------------------------------- drag
    public void BeginDrag(ShipManifestSlot slot, PointerEventData e)
    {
        if (manifest == null || manifest.Get(slot.index) == null) return;
        dragFrom = slot.index;
        tooltip.gameObject.SetActive(false);

        ghost = NewRect("DragGhost", root);
        ghost.anchorMin = ghost.anchorMax = ghost.pivot = new Vector2(0.5f, 0.5f);
        ghost.sizeDelta = portraits[slot.index].rectTransform.sizeDelta;
        Image g = ghost.gameObject.AddComponent<Image>();
        g.sprite = portraits[slot.index].sprite;
        g.preserveAspect = true;
        g.raycastTarget = false;
        Drag(e);
        Refresh();
    }


    public void Drag(PointerEventData e)
    {
        if (ghost == null) return;
        Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(root, e.position, cam, out Vector2 local))
            ghost.anchoredPosition = local;
    }


    public void EndDrag(PointerEventData e)
    {
        if (dragFrom < 0) return;
        GameObject over = e.pointerCurrentRaycast.gameObject;
        ShipManifestSlot target = over != null ? over.GetComponentInParent<ShipManifestSlot>() : null;
        int from = dragFrom;
        CancelDrag();
        if (target != null) manifest.Move(from, target.index);   // invalid = snaps back
        Refresh();
    }


    private void CancelDrag()
    {
        dragFrom = -1;
        if (ghost != null) Destroy(ghost.gameObject);
        ghost = null;
        foreach (Image h in highlights) if (h != null) h.enabled = false;
    }


    // ---------------------------------------------------------------- helpers
    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }


    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
