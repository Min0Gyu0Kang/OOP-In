using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Drag behaviour for the window header. Added to the header strip at runtime by
/// <see cref="WebViewWindow"/>; moves the window root and reports the move so the
/// webview's margins can be re-synced.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class WebViewWindowDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    /// <summary>The window root this handle moves.</summary>
    public RectTransform target;

    /// <summary>Canvas the window lives on, used to undo the canvas scale factor.</summary>
    public Canvas canvas;

    /// <summary>Raised while dragging so the owner can clamp and re-apply margins.</summary>
    public System.Action onMoved;

    public void OnBeginDrag(PointerEventData eventData)
    {
        // Bring the window in front of any other UI on the same canvas.
        if (target != null)
        {
            target.SetAsLastSibling();
        }
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (target == null)
        {
            return;
        }

        // eventData.delta is in screen pixels; anchoredPosition is in canvas units.
        float scale = (canvas != null && canvas.scaleFactor > 0f) ? canvas.scaleFactor : 1f;
        target.anchoredPosition += eventData.delta / scale;

        if (onMoved != null)
        {
            onMoved();
        }
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (onMoved != null)
        {
            onMoved();
        }
    }
}
