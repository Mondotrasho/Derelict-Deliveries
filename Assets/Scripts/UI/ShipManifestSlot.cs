using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// One slot of the ship manifest panel. Built at runtime by ShipManifestPanel;
/// it only forwards pointer / drag events back to the panel.
/// </summary>
public sealed class ShipManifestSlot : MonoBehaviour,
    IPointerEnterHandler, IPointerExitHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    public ShipManifestPanel panel;
    public int index;

    public void OnPointerEnter(PointerEventData e) => panel?.SlotHover(this, true);
    public void OnPointerExit(PointerEventData e) => panel?.SlotHover(this, false);
    public void OnBeginDrag(PointerEventData e) => panel?.BeginDrag(this, e);
    public void OnDrag(PointerEventData e) => panel?.Drag(e);
    public void OnEndDrag(PointerEventData e) => panel?.EndDrag(e);
}
