using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Floating, draggable, resizable window chrome for <see cref="WebViewObject"/>.
///
/// NOTE on layering: on desktop the webview is not a uGUI element - WebViewObject.OnGUI()
/// blits its offscreen texture with Graphics.DrawTexture(), which paints over a
/// Screen Space Overlay canvas and takes the mouse wherever it is drawn. The header strip
/// and the resize border are therefore kept strictly OUTSIDE the rect handed to
/// SetMargins(), so the title bar, its buttons and the resize grips are never covered.
///
/// The whole UI is built in code, so this works in a scene with no Canvas. Assign the
/// icon slots below to replace the built-in text glyphs on the buttons.
/// </summary>
public class WebViewWindow : MonoBehaviour
{
    public enum WindowState { Normal, Maximized, Minimized, Closed }

    /// <summary>Which window edges a resize grip moves.</summary>
    [System.Flags]
    public enum ResizeEdges { None = 0, Left = 1, Right = 2, Bottom = 4, Top = 8 }

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

    [Tooltip("Stay completely hidden (no window, no restore button) until something calls " +
             "Open() - e.g. WebViewTrigger on the guide cube. Uncheck to open immediately " +
             "when the scene starts.")]
    public bool startHidden = true;

    [Header("Sizing")]
    [Tooltip("Largest the windowed (non-maximized) window may be, as a fraction of the canvas. " +
             "Keeps windowed visibly smaller than maximized: with a Scale With Screen Size " +
             "canvas, Windowed Size easily exceeds the whole canvas, which made Maximize " +
             "look like it did nothing.")]
    [Range(0.3f, 1f)]
    public float maxWindowedFraction = 0.85f;

    [Tooltip("Smallest size the window can be resized to, in canvas units (header included).")]
    public Vector2 minWindowSize = new Vector2(320f, 200f);

    [Tooltip("Width of the resize border around the page, in canvas units. The page is inset " +
             "by this much so the native webview never draws over the resize grips.")]
    public float resizeBorder = 6f;

    [Header("Style")]
    public Color windowColor = new Color(0.10f, 0.11f, 0.13f, 1f);
    public Color headerColor = new Color(0.16f, 0.17f, 0.20f, 1f);
    public Color contentColor = Color.white;
    public Color buttonColor = new Color(1f, 1f, 1f, 0.10f);
    public Color closeHoverColor = new Color(0.90f, 0.25f, 0.25f, 1f);

    /// <summary>
    /// Raised when the window is closed (X button, or Toggle() while open). Not raised by
    /// the initial hidden state at startup, nor by minimizing.
    /// </summary>
    public event System.Action Closed;

    // Keep the webview rect strictly positive. A zero-sized rect makes the native plugin
    // report a 0x0 bitmap, which throws inside the Texture2D constructor.
    const int MinWebViewSize = 16;

    // Length of each arm of the L-shaped corner grips, in canvas units.
    const float CornerGripLength = 16f;

    RectTransform canvasRect;
    RectTransform windowRoot;
    RectTransform contentArea;
    GameObject resizeGrips;
    GameObject restoreRoot;
    Text maximizeGlyph;

    // Starts Closed so Awake's first Open()/SetState() is a real transition - starting as
    // Normal would make Open() treat the window as already showing and skip setting it up.
    WindowState state = WindowState.Closed;
    Vector2 savedPosition;
    Vector2 savedSize;
    Vector2 resizeStartPosition;
    Vector2 resizeStartSize;
    bool webViewVisible;
    bool hasBeenOpened;
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

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        // This window already paints an opaque backdrop behind the page, so WebViewObject's
        // optional background Image is redundant - and harmful here: it re-parents itself to
        // the front of its canvas every frame, drawing over the header. Clearing the field
        // before WebViewObject.Start() runs stops it from ever being created.
        if (webViewObject != null)
        {
            webViewObject.canvas = null;
        }
#endif

        EnsureEventSystem();
        EnsureCanvas();
        BuildWindow();
        BuildRestoreButton();

        savedPosition = windowedPosition;
        savedSize = windowedSize;
        lastScreenW = Screen.width;
        lastScreenH = Screen.height;

        if (!startHidden)
        {
            Open();
        }
        else
        {
            SetState(WindowState.Closed);
        }
    }

    void LateUpdate()
    {
        // Re-fit every frame instead of only when Screen.width/height changes: a Scale With
        // Screen Size canvas resizes its rect a frame after the screen does, so a one-shot
        // fit used the stale size and could leave the window taller than the new canvas,
        // pushing the header off the top of the screen. Both calls are idempotent.
        if (state == WindowState.Maximized)
        {
            ApplyMaximizedRect();
        }
        else if (state == WindowState.Normal)
        {
            ClampWindow();
        }

        if (Screen.width != lastScreenW || Screen.height != lastScreenH)
        {
            lastScreenW = Screen.width;
            lastScreenH = Screen.height;
            marginsValid = false;
        }

        SyncWebViewToContent();
    }

    // ---------------------------------------------------------------- window state

    public void Open()
    {
        hasBeenOpened = true;

        // Already on screen: just bring it forward. Re-entering Normal here would snap a
        // dragged/resized window back to its last saved rect, and un-maximize a
        // maximized one, every time the guide cube is clicked.
        if (state == WindowState.Normal || state == WindowState.Maximized)
        {
            BringToFront();
            return;
        }

        SetState(WindowState.Normal);
    }

    /// <summary>Open if hidden/closed/minimized, otherwise close. Handy for a toggle button.</summary>
    public void Toggle()
    {
        if (state == WindowState.Normal || state == WindowState.Maximized)
        {
            Close();
        }
        else
        {
            Open();
        }
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
        bool wasOpen = state != WindowState.Closed;
        SetState(WindowState.Closed);

        if (wasOpen && Closed != null)
        {
            Closed();
        }
    }

    public void BringToFront()
    {
        if (windowRoot != null)
        {
            windowRoot.SetAsLastSibling();
        }
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
        if (resizeGrips != null)
        {
            // A maximized window is pinned to the canvas, so there is nothing to resize.
            resizeGrips.SetActive(state == WindowState.Normal);
        }
        if (restoreRoot != null)
        {
            // Before the first Open() nothing should be on screen at all - the guide cube
            // is the only way in. After that, closing/minimizing shows it as before.
            restoreRoot.SetActive(!windowShown && hasBeenOpened);
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

    /// <summary>
    /// Canvas size in canvas units, or false while the canvas hasn't been laid out yet
    /// (fitting the window to a zero-sized canvas would collapse it).
    /// </summary>
    bool TryGetCanvasSize(out Vector2 canvasSize)
    {
        canvasSize = (canvasRect != null) ? canvasRect.rect.size : Vector2.zero;
        return canvasSize.x >= 1f && canvasSize.y >= 1f;
    }

    void ApplyMaximizedRect()
    {
        Vector2 canvasSize;
        if (windowRoot == null || !TryGetCanvasSize(out canvasSize))
        {
            return;
        }
        windowRoot.anchoredPosition = Vector2.zero;
        windowRoot.sizeDelta = canvasSize;
    }

    void GetWindowedSizeLimits(Vector2 canvasSize, out Vector2 minSize, out Vector2 maxSize)
    {
        maxSize = canvasSize * maxWindowedFraction;
        minSize = Vector2.Min(minWindowSize, maxSize);
    }

    void ClampWindow()
    {
        Vector2 canvasSize;
        if (windowRoot == null || state != WindowState.Normal || !TryGetCanvasSize(out canvasSize))
        {
            return;
        }

        Vector2 minSize, maxSize;
        GetWindowedSizeLimits(canvasSize, out minSize, out maxSize);

        Vector2 size = windowRoot.sizeDelta;
        size.x = Mathf.Clamp(size.x, minSize.x, maxSize.x);
        size.y = Mathf.Clamp(size.y, minSize.y, maxSize.y);
        windowRoot.sizeDelta = size;

        Vector2 pos = windowRoot.anchoredPosition;
        pos.x = Mathf.Clamp(pos.x, 0f, canvasSize.x - size.x);
        pos.y = Mathf.Clamp(pos.y, 0f, canvasSize.y - size.y);
        windowRoot.anchoredPosition = pos;
    }

    /// <summary>Snapshot the window rect a resize is measured from. Called by the grips.</summary>
    public void BeginResize()
    {
        BringToFront();
        if (windowRoot != null)
        {
            resizeStartPosition = windowRoot.anchoredPosition;
            resizeStartSize = windowRoot.sizeDelta;
        }
    }

    /// <summary>
    /// Moves <paramref name="edges"/> by <paramref name="offset"/> canvas units from where
    /// they were at <see cref="BeginResize"/>, keeping the opposite edges fixed and the
    /// window inside the canvas and its size limits. Only applies to a normal window.
    /// </summary>
    public void Resize(ResizeEdges edges, Vector2 offset)
    {
        Vector2 canvasSize;
        if (windowRoot == null || state != WindowState.Normal || !TryGetCanvasSize(out canvasSize))
        {
            return;
        }

        Vector2 minSize, maxSize;
        GetWindowedSizeLimits(canvasSize, out minSize, out maxSize);

        Vector2 pos = resizeStartPosition;
        Vector2 size = resizeStartSize;

        // The window pivot is bottom-left, so the right/top edges move by growing the size,
        // while the left/bottom edges also shift the position to keep the far edge fixed.
        if ((edges & ResizeEdges.Right) != 0)
        {
            size.x = Mathf.Clamp(size.x + offset.x, minSize.x, Mathf.Min(maxSize.x, canvasSize.x - pos.x));
        }
        else if ((edges & ResizeEdges.Left) != 0)
        {
            float right = pos.x + size.x;
            float width = Mathf.Clamp(size.x - offset.x, minSize.x, Mathf.Min(maxSize.x, right));
            pos.x = right - width;
            size.x = width;
        }

        if ((edges & ResizeEdges.Top) != 0)
        {
            size.y = Mathf.Clamp(size.y + offset.y, minSize.y, Mathf.Min(maxSize.y, canvasSize.y - pos.y));
        }
        else if ((edges & ResizeEdges.Bottom) != 0)
        {
            float top = pos.y + size.y;
            float height = Mathf.Clamp(size.y - offset.y, minSize.y, Mathf.Min(maxSize.y, top));
            pos.y = top - height;
            size.y = height;
        }

        windowRoot.anchoredPosition = pos;
        windowRoot.sizeDelta = size;
    }

    // ---------------------------------------------------------------- webview sync

    /// <summary>
    /// Converts the content area's screen rect into WebViewObject margins. Runs every
    /// LateUpdate so dragging, resizing, maximizing and resolution changes stay in sync.
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

    float HeaderButtonSize()
    {
        return Mathf.Max(16f, headerHeight - 10f);
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
        float size = HeaderButtonSize();
        MakeHeaderButton(header, "CloseButton", closeIcon, "✕", -6f, size, Close, closeHoverColor);
        maximizeGlyph = MakeHeaderButton(header, "MaximizeButton", maximizeIcon, "□", -(6f + size + 4f), size, ToggleMaximize, buttonColor);
        MakeHeaderButton(header, "MinimizeButton", minimizeIcon, "–", -(6f + (size + 4f) * 2f), size, Minimize, buttonColor);

        // Content area: everything under the header, inset by the resize border so the
        // native webview (drawn over this rect) never covers the grips. Opaque so pages
        // with transparent backgrounds do not show the scene through them.
        contentArea = NewRect("Content", windowRoot);
        contentArea.anchorMin = Vector2.zero;
        contentArea.anchorMax = Vector2.one;
        contentArea.offsetMin = new Vector2(resizeBorder, resizeBorder);
        contentArea.offsetMax = new Vector2(-resizeBorder, -headerHeight);
        AddImage(contentArea, contentColor);

        // Built last so the grips sit on top of the header and content for raycasts.
        BuildResizeGrips();
    }

    void BuildResizeGrips()
    {
        RectTransform root = NewRect("ResizeGrips", windowRoot);
        root.anchorMin = Vector2.zero;
        root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero;
        root.offsetMax = Vector2.zero;
        resizeGrips = root.gameObject;

        float b = Mathf.Max(2f, resizeBorder);
        // The top grip must fit in the gap above the header buttons, or it steals their clicks.
        float t = Mathf.Clamp((headerHeight - HeaderButtonSize()) * 0.5f, 2f, b);
        float c = Mathf.Max(CornerGripLength, b);

        // Edges stop short of the corners, where the corner grips take over.
        AddGrip(root, "Left", ResizeEdges.Left, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, c), new Vector2(b, -c));
        AddGrip(root, "Right", ResizeEdges.Right, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-b, c), new Vector2(0f, -c));
        AddGrip(root, "Bottom", ResizeEdges.Bottom, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(c, 0f), new Vector2(-c, b));
        AddGrip(root, "Top", ResizeEdges.Top, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(c, -t), new Vector2(-c, 0f));

        // Corners are L-shaped - one arm along each adjoining edge - so they stay on the
        // border and never overlap the page.
        ResizeEdges bottomLeft = ResizeEdges.Bottom | ResizeEdges.Left;
        ResizeEdges bottomRight = ResizeEdges.Bottom | ResizeEdges.Right;
        ResizeEdges topLeft = ResizeEdges.Top | ResizeEdges.Left;
        ResizeEdges topRight = ResizeEdges.Top | ResizeEdges.Right;

        AddGrip(root, "BottomLeftH", bottomLeft, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(c, b));
        AddGrip(root, "BottomLeftV", bottomLeft, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(b, c));
        AddGrip(root, "BottomRightH", bottomRight, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-c, 0f), new Vector2(0f, b));
        AddGrip(root, "BottomRightV", bottomRight, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-b, 0f), new Vector2(0f, c));
        AddGrip(root, "TopLeftH", topLeft, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -t), new Vector2(c, 0f));
        AddGrip(root, "TopLeftV", topLeft, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -c), new Vector2(b, 0f));
        AddGrip(root, "TopRightH", topRight, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-c, -t), new Vector2(0f, 0f));
        AddGrip(root, "TopRightV", topRight, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-b, -c), new Vector2(0f, 0f));
    }

    void AddGrip(RectTransform parent, string name, ResizeEdges edges, Vector2 anchorMin, Vector2 anchorMax,
                 Vector2 offsetMin, Vector2 offsetMax)
    {
        RectTransform rect = NewRect(name, parent);
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;

        // Invisible but still a raycast target - graphic raycasts ignore alpha.
        AddImage(rect, Color.clear);

        var grip = rect.gameObject.AddComponent<WebViewWindowResizeHandle>();
        grip.owner = this;
        grip.edges = edges;
        grip.canvas = targetCanvas;
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
