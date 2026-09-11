using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Resize grip for <see cref="WebViewWindow"/>. Added along the window border at runtime;
/// dragging it moves the window edges named by <see cref="edges"/>.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class WebViewWindowResizeHandle : MonoBehaviour, IInitializePotentialDragHandler, IBeginDragHandler, IDragHandler
{
    /// <summary>The window this grip resizes.</summary>
    public WebViewWindow owner;

    /// <summary>Which edges this grip moves (two for a corner).</summary>
    public WebViewWindow.ResizeEdges edges;

    /// <summary>Canvas the window lives on, used to undo the canvas scale factor.</summary>
    public Canvas canvas;

    Vector2 pressPosition;

    public void OnInitializePotentialDrag(PointerEventData eventData)
    {
        // The grips are only a few units wide; waiting out the drag threshold lets the
        // pointer slide off the grip before the resize even starts.
        eventData.useDragThreshold = false;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        pressPosition = eventData.pressPosition;
        if (owner != null)
        {
            owner.BeginResize();
        }
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (owner == null)
        {
            return;
        }

        // Resize from the total offset since the press rather than accumulating per-frame
        // deltas, so hitting a size limit and dragging back doesn't make the edge drift
        // away from the pointer.
        float scale = (canvas != null && canvas.scaleFactor > 0f) ? canvas.scaleFactor : 1f;
        owner.Resize(edges, (eventData.position - pressPosition) / scale);
    }
}
