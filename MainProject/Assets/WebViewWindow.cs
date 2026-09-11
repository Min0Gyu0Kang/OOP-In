using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Floating, draggable window chrome for <see cref="WebViewObject"/>.
///
/// NOTE on layering: on desktop the webview is not a uGUI element - WebViewObject.OnGUI()
/// blits its offscreen texture with Graphics.DrawTexture(), which paints over a
/// Screen Space Overlay canvas. The header strip is therefore kept strictly OUTSIDE the
/// rect handed to SetMargins(), so the title bar and its buttons are never covered.
///
/// The whole UI is built in code, so this works in a scene with no Canvas. Assign the
/// icon slots below to replace the built-in text glyphs on the buttons.
/// </summary>
public class WebViewWindow : MonoBehaviour
{
    public enum WindowState { Normal, Maximized, Minimized, Closed }

    [Header("References (found or created at runtime when left empty)")]
    public WebViewObject webViewObject;
    public Canvas targetCanvas;

    [Header("Button icons (optional - falls back to text glyphs)")]
    public Sprite minimizeIcon;
    public Sprite maximizeIcon;
    public Sprite closeIcon;
    public Sprite restoreIcon;

    [Header("Window")]
    public string title = "WebView";
    public Vector2 windowedSize = new Vector2(960f, 640f);
    public Vector2 windowedPosition = new Vector2(60f, 60f);
    public float headerHeight = 34f;
    public bool openOnStart = true;

    [Header("Style")]
    public Color windowColor = new Color(0.10f, 0.11f, 0.13f, 1f);
    public Color headerColor = new Color(0.16f, 0.17f, 0.20f, 1f);
    public Color contentColor = Color.white;
    public Color buttonColor = new Color(1f, 1f, 1f, 0.10f);
    public Color closeHoverColor = new Color(0.90f, 0.25f, 0.25f, 1f);

    // Keep the webview rect strictly positive. A zero-sized rect makes the native plugin
    // report a 0x0 bitmap, which throws inside the Texture2D constructor.
    const int MinWebViewSize = 16;

    RectTransform canvasRect;
    RectTransform windowRoot;
    RectTransform contentArea;
    GameObject restoreRoot;
    Text maximizeGlyph;

    WindowState state = WindowState.Normal;
    Vector2 savedPosition;
    Vector2 savedSize;
    bool webViewVisible;
    bool marginsValid;
    int lastLeft, lastTop, lastRight, lastBottom;
    int lastScreenW, lastScreenH;

    public WindowState State { get { return state; } }

    void Awake()
    {
        if (webViewObject == null)
        {
#if UNITY_2023_1_OR_NEWER
            webViewObject = FindFirstObjectByType<WebViewObject>();
#else
            webViewObject = FindObjectOfType<WebViewObject>();
#endif
        }

        EnsureEventSystem();
        EnsureCanvas();
        BuildWindow();
        BuildRestoreButton();

        savedPosition = windowedPosition;
        savedSize = windowedSize;
        lastScreenW = Screen.width;
        lastScreenH = Screen.height;

        SetState(openOnStart ? WindowState.Normal : WindowState.Closed);
    }

    void LateUpdate()
    {
        // Re-apply on resolution changes so the window stays on screen.
        if (Screen.width != lastScreenW || Screen.height != lastScreenH)
        {
            lastScreenW = Screen.width;
            lastScreenH = Screen.height;
            if (state == WindowState.Maximized)
            {
                ApplyMaximizedRect();
            }
            ClampWindow();
            marginsValid = false;
        }

        SyncWebViewToContent();
    }

    // ---------------------------------------------------------------- window state

    public void Open()
    {
        SetState(WindowState.Normal);
    }

    public void Minimize()
    {
        SetState(state == WindowState.Minimized ? WindowState.Normal : WindowState.Minimized);
    }

    public void ToggleMaximize()
    {
        SetState(state == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
    }

    public void Close()
    {
        SetState(WindowState.Closed);
    }

    void SetState(WindowState next)
    {
        // Remember the windowed placement before leaving the normal state.
        if (state == WindowState.Normal && next != WindowState.Normal && windowRoot != null)
        {
            savedPosition = windowRoot.anchoredPosition;
            savedSize = windowRoot.sizeDelta;
        }

        state = next;

        bool windowShown = (state == WindowState.Normal || state == WindowState.Maximized);
        if (windowRoot != null)
        {
            windowRoot.gameObject.SetActive(windowShown);
        }
        if (restoreRoot != null)
        {
            restoreRoot.SetActive(!windowShown);
        }

        if (state == WindowState.Normal && windowRoot != null)
        {
            windowRoot.anchoredPosition = savedPosition;
            windowRoot.sizeDelta = savedSize;
            ClampWindow();
        }
        else if (state == WindowState.Maximized)
        {
            ApplyMaximizedRect();
        }

        if (maximizeGlyph != null)
        {
            maximizeGlyph.text = (state == WindowState.Maximized) ? "❐" : "□";
        }

        // Hide the native webview immediately when the window goes away, rather than
        // waiting for the margin sync, so it cannot paint over the restore button.
        if (!windowShown && webViewObject != null && webViewVisible)
        {
            webViewObject.SetVisibility(false);
            webViewVisible = false;
        }

        marginsValid = false;
    }

    void ApplyMaximizedRect()
    {
        if (windowRoot == null || canvasRect == null)
        {
            return;
        }
        windowRoot.anchoredPosition = Vector2.zero;
        windowRoot.sizeDelta = canvasRect.rect.size;
    }

    void ClampWindow()
    {
        if (windowRoot == null || canvasRect == null || state != WindowState.Normal)
        {
            return;
        }

        Vector2 canvasSize = canvasRect.rect.size;
        Vector2 size = windowRoot.sizeDelta;
        size.x = Mathf.Min(size.x, canvasSize.x);
        size.y = Mathf.Min(size.y, canvasSize.y);
        windowRoot.sizeDelta = size;

        Vector2 pos = windowRoot.anchoredPosition;
        pos.x = Mathf.Clamp(pos.x, 0f, Mathf.Max(0f, canvasSize.x - size.x));
        pos.y = Mathf.Clamp(pos.y, 0f, Mathf.Max(0f, canvasSize.y - size.y));
        windowRoot.anchoredPosition = pos;
    }

    // ---------------------------------------------------------------- webview sync

    /// <summary>
    /// Converts the content area's screen rect into WebViewObject margins. Runs every
    /// LateUpdate so dragging, maximizing and resolution changes all stay in sync.
    /// </summary>
    void SyncWebViewToContent()
    {
        if (webViewObject == null || contentArea == null)
        {
            return;
        }

        bool shouldShow = (state == WindowState.Normal || state == WindowState.Maximized);
        int left, top, right, bottom;

        // A degenerate rect (mid-layout, or collapsed) would ask the plugin for a
        // zero-sized bitmap, so treat it the same as a hidden window.
        if (!shouldShow || !TryGetMargins(out left, out top, out right, out bottom))
        {
            if (webViewVisible)
            {
                webViewObject.SetVisibility(false);
                webViewVisible = false;
            }
            return;
        }

        if (!marginsValid || left != lastLeft || top != lastTop || right != lastRight || bottom != lastBottom)
        {
            webViewObject.SetMargins(left, top, right, bottom);
            lastLeft = left;
            lastTop = top;
            lastRight = right;
            lastBottom = bottom;
            marginsValid = true;
        }

        if (!webViewVisible)
        {
            webViewObject.SetVisibility(true);
            webViewVisible = true;
        }
    }

    /// <summary>
    /// Screen rect of the content area expressed as margins from each screen edge.
    /// Returns false when the resulting rect would be too small to render.
    /// </summary>
    bool TryGetMargins(out int left, out int top, out int right, out int bottom)
    {
        left = top = right = bottom = 0;

        Vector3[] corners = new Vector3[4];
        contentArea.GetWorldCorners(corners);

        Camera cam = (targetCanvas != null && targetCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            ? targetCanvas.worldCamera
            : null;

        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 topRight = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

        // WebViewObject's desktop rect uses a bottom-left origin.
        left = Mathf.Max(0, Mathf.RoundToInt(bottomLeft.x));
        bottom = Mathf.Max(0, Mathf.RoundToInt(bottomLeft.y));
        right = Mathf.Max(0, Mathf.RoundToInt(Screen.width - topRight.x));
        top = Mathf.Max(0, Mathf.RoundToInt(Screen.height - topRight.y));

        int width = Screen.width - (left + right);
        int height = Screen.height - (top + bottom);
        return width >= MinWebViewSize && height >= MinWebViewSize;
    }

    // ---------------------------------------------------------------- ui construction

    void EnsureEventSystem()
    {
#if UNITY_2023_1_OR_NEWER
        if (FindFirstObjectByType<EventSystem>() != null)
#else
        if (FindObjectOfType<EventSystem>() != null)
#endif
        {
            return;
        }
        // Created at the scene root: this component may live on a scaled/rotated
        // GameObject, and inheriting that transform would skew the UI.
        new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
    }

    void EnsureCanvas()
    {
        if (targetCanvas == null)
        {
            targetCanvas = GetComponentInChildren<Canvas>();
        }
        if (targetCanvas == null)
        {
            var go = new GameObject("WebViewCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            targetCanvas = go.GetComponent<Canvas>();
            targetCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            targetCanvas.sortingOrder = 100;

            // Constant pixel size keeps canvas units == screen pixels, which keeps the
            // margin maths exact.
            go.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        }
        canvasRect = targetCanvas.GetComponent<RectTransform>();
    }

    void BuildWindow()
    {
        windowRoot = NewRect("WebViewWindow", canvasRect);
        windowRoot.anchorMin = Vector2.zero;
        windowRoot.anchorMax = Vector2.zero;
        windowRoot.pivot = Vector2.zero;
        windowRoot.anchoredPosition = windowedPosition;
        windowRoot.sizeDelta = windowedSize;
        AddImage(windowRoot, windowColor);

        // Header strip, pinned to the top of the window and kept outside the webview rect.
        RectTransform header = NewRect("Header", windowRoot);
        header.anchorMin = new Vector2(0f, 1f);
        header.anchorMax = new Vector2(1f, 1f);
        header.pivot = new Vector2(0.5f, 1f);
        header.offsetMin = new Vector2(0f, -headerHeight);
        header.offsetMax = Vector2.zero;
        AddImage(header, headerColor);

        var drag = header.gameObject.AddComponent<WebViewWindowDragHandle>();
        drag.target = windowRoot;
        drag.canvas = targetCanvas;
        drag.onMoved = OnWindowDragged;

        RectTransform titleRect = NewRect("Title", header);
        titleRect.anchorMin = Vector2.zero;
        titleRect.anchorMax = Vector2.one;
        titleRect.offsetMin = new Vector2(12f, 0f);
        titleRect.offsetMax = new Vector2(-120f, 0f);
        AddText(titleRect, title, 14, TextAnchor.MiddleLeft);

        // Control buttons, right-aligned inside the header.
        float size = Mathf.Max(16f, headerHeight - 10f);
        MakeHeaderButton(header, "CloseButton", closeIcon, "✕", -6f, size, Close, closeHoverColor);
        maximizeGlyph = MakeHeaderButton(header, "MaximizeButton", maximizeIcon, "□", -(6f + size + 4f), size, ToggleMaximize, buttonColor);
        MakeHeaderButton(header, "MinimizeButton", minimizeIcon, "–", -(6f + (size + 4f) * 2f), size, Minimize, buttonColor);

        // Content area: everything under the header. Opaque so pages with transparent
        // backgrounds do not show the scene through them.
        contentArea = NewRect("Content", windowRoot);
        contentArea.anchorMin = Vector2.zero;
        contentArea.anchorMax = Vector2.one;
        contentArea.offsetMin = Vector2.zero;
        contentArea.offsetMax = new Vector2(0f, -headerHeight);
        AddImage(contentArea, contentColor);
    }

    void BuildRestoreButton()
    {
        RectTransform root = NewRect("RestoreWebViewButton", canvasRect);
        root.anchorMin = new Vector2(0f, 1f);
        root.anchorMax = new Vector2(0f, 1f);
        root.pivot = new Vector2(0f, 1f);
        root.anchoredPosition = new Vector2(20f, -20f);
        root.sizeDelta = new Vector2(restoreIcon != null ? 56f : 150f, 44f);

        Image bg = AddImage(root, new Color(0.16f, 0.17f, 0.20f, 0.95f));
        Button button = root.gameObject.AddComponent<Button>();
        button.targetGraphic = bg;
        button.onClick.AddListener(Open);

        RectTransform label = NewRect("Label", root);
        label.anchorMin = Vector2.zero;
        label.anchorMax = Vector2.one;
        label.offsetMin = Vector2.zero;
        label.offsetMax = Vector2.zero;

        if (restoreIcon != null)
        {
            Image icon = label.gameObject.AddComponent<Image>();
            icon.sprite = restoreIcon;
            icon.preserveAspect = true;
        }
        else
        {
            AddText(label, "Open WebView", 14, TextAnchor.MiddleCenter);
        }

        restoreRoot = root.gameObject;
        restoreRoot.SetActive(false);
    }

    void OnWindowDragged()
    {
        // Dragging is only meaningful in the normal state; a maximized window fills the
        // screen, so snap it back to windowed and let the user move it.
        if (state == WindowState.Maximized)
        {
            SetState(WindowState.Normal);
        }
        ClampWindow();
    }

    Text MakeHeaderButton(RectTransform parent, string name, Sprite icon, string glyph,
                          float rightOffset, float size, UnityEngine.Events.UnityAction action,
                          Color highlight)
    {
        RectTransform rect = NewRect(name, parent);
        rect.anchorMin = new Vector2(1f, 0.5f);
        rect.anchorMax = new Vector2(1f, 0.5f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.anchoredPosition = new Vector2(rightOffset, 0f);
        rect.sizeDelta = new Vector2(size, size);

        Image bg = AddImage(rect, buttonColor);
        Button button = rect.gameObject.AddComponent<Button>();
        button.targetGraphic = bg;
        button.onClick.AddListener(action);

        ColorBlock colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = highlight;
        colors.pressedColor = highlight * 0.8f;
        button.colors = colors;

        RectTransform inner = NewRect("Icon", rect);
        inner.anchorMin = Vector2.zero;
        inner.anchorMax = Vector2.one;
        inner.offsetMin = Vector2.zero;
        inner.offsetMax = Vector2.zero;

        if (icon != null)
        {
            Image image = inner.gameObject.AddComponent<Image>();
            image.sprite = icon;
            image.preserveAspect = true;
            return null;
        }

        return AddText(inner, glyph, Mathf.RoundToInt(size * 0.6f), TextAnchor.MiddleCenter);
    }

    static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    static Image AddImage(RectTransform rect, Color color)
    {
        Image image = rect.gameObject.AddComponent<Image>();
        image.color = color;
        return image;
    }

    static Text AddText(RectTransform rect, string content, int fontSize, TextAnchor anchor)
    {
        Text text = rect.gameObject.AddComponent<Text>();
        text.text = content;
        text.fontSize = fontSize;
        text.alignment = anchor;
        text.color = new Color(0.92f, 0.92f, 0.94f, 1f);
        text.raycastTarget = false;
        // Arial.ttf was renamed to LegacyRuntime.ttf in Unity 2022.2+.
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (text.font == null)
        {
            text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }
        return text;
    }
}
