/*
 * Copyright (C) 2012 GREE, Inc.
 * 
 * This software is provided 'as-is', without any express or implied
 * warranty.  In no event will the authors be held liable for any damages
 * arising from the use of this software.
 * 
 * Permission is granted to anyone to use this software for any purpose,
 * including commercial applications, and to alter it and redistribute it
 * freely, subject to the following restrictions:
 * 
 * 1. The origin of this software must not be misrepresented; you must not
 *    claim that you wrote the original software. If you use this software
 *    in a product, an acknowledgment in the product documentation would be
 *    appreciated but is not required.
 * 2. Altered source versions must be plainly marked as such, and must not be
 *    misrepresented as being the original software.
 * 3. This notice may not be removed or altered from any source distribution.
 */

using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class WebViewController : MonoBehaviour
{
    public string Url;
    public int LeftMargin, RightMargin, TopMargin, BottomMargin;

    [SerializeField]
    private WebViewObject webViewObject;

    [Tooltip("Optional. When assigned, the webview's margins are computed from this " +
             "panel's on-screen rect every frame instead of the static Left/Right/Top/" +
             "BottomMargin fields above - a native webview ignores Transforms entirely, " +
             "so this is what actually confines it to part of the screen.")]
    [SerializeField]
    private RectTransform boundsPanel;

    [Tooltip("Optional, and takes precedence over Bounds Panel. Pins the webview to a 3D " +
             "object by projecting its renderer bounds into screen space - use this to sit " +
             "the page on a world-space surface (e.g. the 'Top Editor Input' cube), which a " +
             "native webview cannot be parented to.\n" +
             "The result is a flat, axis-aligned screen rectangle: it is not " +
             "perspective-warped onto the face, and nothing drawn in front of the object " +
             "will occlude it.")]
    [SerializeField]
    private Transform worldAnchor;

    [Tooltip("Camera used to project World Anchor. Defaults to Camera.main when empty.")]
    [SerializeField]
    private Camera anchorCamera;

    [Tooltip("Strip ad / sidebar elements from the page once it finishes loading.")]
    public bool removeAds = true;

    [Tooltip("ON  - a constantly-live webview: the page loads AND the window opens as soon " +
             "as the scene starts, with no click needed (e.g. the Monaco IDE).\n" +
             "OFF - opened on demand: nothing is fetched and no native webview exists until " +
             "something calls Load(), e.g. WebViewTrigger on the guide cube (e.g. a " +
             "reference page).\n" +
             "When ON, this also overrides WebViewWindow's Start Hidden.")]
    public bool loadOnStart = false;

    /// <summary>
    /// Raised for every <c>window.Unity.call(msg)</c> the page makes. The string arrives
    /// already URL-unescaped, so a payload sent as
    /// <c>Unity.call('run-python?code=' + encodeURIComponent(code))</c> shows up here as
    /// <c>run-python?code=&lt;the real code&gt;</c>. Because that decode happens before this
    /// point, a parser must take everything after <c>code=</c> rather than splitting on
    /// '&amp;' - decoded code can legitimately contain both '&amp;' and '?'.
    /// </summary>
    public event System.Action<string> MessageReceived;

    private Coroutine _loadCoroutine;

    // Guards Load() so repeated triggers don't Init()/LoadURL() the webview twice.
    private bool _loadStarted;

    // Below this, treat the panel's on-screen rect as degenerate rather than asking the
    // native plugin for a near-zero bitmap (mirrors the guard in WebViewObject.Update()).
    private const int MinBoundsSize = 16;

    private bool marginsValid;
    private int lastLeft, lastTop, lastRight, lastBottom;

    // World-anchor latch. Flexalon positions objects during its own update, so the anchor's
    // bounds are not final at Start() - projecting there would pin the page to a stale rect.
    // Instead the projection re-runs every LateUpdate until two consecutive frames agree,
    // which is how "project once, then stay fixed" is reached without guessing a frame
    // count. Unlatched again on a resolution change so the page re-fits.
    private bool anchorLatched;
    private bool anchorHasPrevious;
    private int prevAnchorLeft, prevAnchorTop, prevAnchorRight, prevAnchorBottom;
    private int anchorScreenW, anchorScreenH;

    // Resolved once, then reused - the fallback search is not something to repeat per frame.
    private Camera resolvedAnchorCamera;
    private bool cameraWarningLogged;
    private bool anchorBoundsErrorLogged;

    // With a positioning source assigned, the page is kept hidden until the first margins
    // are actually applied - otherwise it would show at whatever rect it had beforehand
    // (full screen) for the frames the anchor latch takes to converge.
    private bool pendingShow;

    // When a WebViewWindow chrome sits on this GameObject, it becomes the sole owner of
    // margins/visibility (it drives both every LateUpdate to track its content area) -
    // otherwise this component's own static-margin/boundsPanel logic would race it and
    // cause a one-frame full-screen flash before the window's chrome corrects it.
    private bool hasOwnWindow;

    // The window on this GameObject, if any - closing it resets the page to Url.
    private WebViewWindow ownWindow;

    // The URL actually handed to LoadURL for Url (http as-is, or the file:// copy of a
    // StreamingAssets page), kept so a reset can navigate straight back to it.
    private string _homeUrl;

    // Injected on every page load. Ad slots are filled in asynchronously by the ad
    // network well after onLoaded fires, so a one-shot pass would miss most of them -
    // the MutationObserver keeps stripping them as they appear.
    private const string RemoveAdsJS = @"
(function () {
    var selectorsToRemove = [
        '#right',                       // right sidebar container
        '#footer-skyscraper',           // skyscraper ads
        '#stickypos',                   // sticky ad container
        '#vidpos',                      // video ad area
        '[data-hbdbrk-parent=""true""]',  // ad network containers
        '[data-hbdbrk]',                // ad units
        '.remove-ads-container',        // 'REMOVE ADS' buttons
        '.sharethis'                    // social links block
    ];

    function strip() {
        selectorsToRemove.forEach(function (selector) {
            var nodes = document.querySelectorAll(selector);
            for (var i = 0; i < nodes.length; i++) {
                nodes[i].remove();
            }
        });
    }

    strip();

    if (window.MutationObserver && document.body) {
        var pending = false;
        new MutationObserver(function () {
            if (pending) { return; }
            pending = true;
            // Coalesce bursts of DOM mutations into a single pass.
            window.setTimeout(function () { pending = false; strip(); }, 50);
        }).observe(document.body, { childList: true, subtree: true });
    }
})();
";

    private void Awake()
    {
        ownWindow = GetComponent<WebViewWindow>();
        hasOwnWindow = ownWindow != null;
        if (hasOwnWindow)
        {
            // Closing the window is a hard restart: the next open starts on Url again, not
            // on whatever page was last browsed to. Minimizing keeps the current page.
            ownWindow.Closed += ResetToHome;
        }
    }

    private void OnDestroy()
    {
        if (ownWindow != null)
        {
            ownWindow.Closed -= ResetToHome;
        }
    }

    private void Start() {
        if (!loadOnStart)
        {
            return;
        }

        Load();

        // Loading alone isn't enough when there's window chrome: a closed WebViewWindow
        // drives SetVisibility(false) from its own LateUpdate, so the page would load and
        // then sit invisible behind a hidden window. Opening here (Start runs after every
        // Awake) reliably overrides the window's Start Hidden without depending on script
        // execution order.
        //
        // Gated on the load actually starting, so a controller with no Url logs its error
        // instead of also popping an empty window.
        if (_loadStarted && ownWindow != null)
        {
            ownWindow.Open();
        }
    }

    /// <summary>True once <see cref="Load"/> has run.</summary>
    public bool IsLoaded { get { return _loadStarted; } }

    /// <summary>
    /// True when something other than the static margins decides the webview's rect, so the
    /// rect isn't known until that source has been evaluated at least once.
    /// </summary>
    private bool HasPositioningSource
    {
        get { return worldAnchor != null || boundsPanel != null; }
    }

    /// <summary>
    /// Creates the native webview and loads <see cref="Url"/>. Idempotent - only the
    /// first call does any work, so a trigger can call it on every click. Until this
    /// runs, no native webview exists and the page is never fetched.
    /// </summary>
    public void Load()
    {
        if (_loadStarted)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Url))
        {
            // Bail before Init() so a misconfigured component doesn't spin up a native
            // webview for nothing. Note an empty Url used to resolve to the StreamingAssets
            // folder itself, and reading a directory throws UnauthorizedAccessException.
            // _loadStarted stays false, so assigning Url later and calling Load() works.
            Debug.LogError("[WebViewController] No Url set on '" + name + "' - nothing to load. " +
                           "Use an http(s) URL, or a path relative to StreamingAssets " +
                           "(e.g. \"MonacoEditor/index.html\").", this);
            return;
        }

        _loadStarted = true;

        _loadCoroutine = StartCoroutine(LoadWebView(Url));
        if (!hasOwnWindow)
        {
            // With a WebViewWindow present, it owns visibility instead.
            if (HasPositioningSource)
            {
                // Stay hidden until the first margins land, so the page doesn't flash at
                // its previous (full-screen) rect while the anchor projection converges.
                pendingShow = true;
            }
            else
            {
                SetVisibility(true);
            }
        }
    }

    /// <summary>
    /// Navigates back to <see cref="Url"/>, discarding wherever the user browsed to. Does
    /// nothing until the first load has resolved the home URL - that load is already
    /// heading there.
    /// </summary>
    public void ResetToHome()
    {
        if (webViewObject == null || string.IsNullOrEmpty(_homeUrl))
        {
            return;
        }
        webViewObject.LoadURL(_homeUrl);
    }

    private void LoadHome(string url)
    {
        _homeUrl = url;
        webViewObject.LoadURL(url);
    }

    /// <summary>
    /// Absolute filesystem path to a file:// URL. Uses System.Uri rather than string
    /// concatenation so Windows drive paths get the three-slash form WebView2 expects
    /// ("file:///C:/..."), backslashes become forward slashes, and spaces are escaped.
    /// </summary>
    private static string ToFileUrl(string path)
    {
        System.Uri uri;
        if (System.Uri.TryCreate(path, System.UriKind.Absolute, out uri) && uri.IsFile)
        {
            return uri.AbsoluteUri;
        }

        // Fallback: a POSIX path on a runtime whose Uri parser insists on a drive letter.
        // Not reachable in normal play (the POSIX branch only runs on Android), but a
        // thrown UriFormatException here would be a confusing way to fail.
        var normalised = path.Replace('\\', '/');
        if (!normalised.StartsWith("/"))
        {
            normalised = "/" + normalised;
        }
        return "file://" + normalised.Replace(" ", "%20");
    }

    private void OnDisable()
    {
        if (_loadCoroutine != null)
        {
            StopCoroutine(_loadCoroutine);
        }
    }

    private void LateUpdate()
    {
        // Keep the webview tracking the panel if it's dragged, resized, or the canvas
        // rescales. No-op until boundsPanel is assigned, and no-op entirely when a
        // WebViewWindow owns margins/visibility instead.
        UpdateMarginsFromBounds();
    }

    /// <summary>
    /// Drives the webview's screen rect, in precedence order: a WebViewWindow on this
    /// GameObject owns it outright; otherwise <see cref="worldAnchor"/> (a 3D object
    /// projected into screen space), then <see cref="boundsPanel"/> (a UI rect); with
    /// neither, the static Left/Right/Top/BottomMargin fields set in LoadWebView stand.
    /// </summary>
    private void UpdateMarginsFromBounds()
    {
        if (hasOwnWindow || webViewObject == null)
        {
            return;
        }

        if (worldAnchor != null)
        {
            UpdateMarginsFromWorldAnchor();
        }
        else if (boundsPanel != null)
        {
            UpdateMarginsFromPanel();
        }
    }

    /// <summary>Screen rect of <see cref="boundsPanel"/>, tracked every frame.</summary>
    private void UpdateMarginsFromPanel()
    {
        Vector3[] corners = new Vector3[4];
        boundsPanel.GetWorldCorners(corners);

        Canvas canvas = boundsPanel.GetComponentInParent<Canvas>();
        Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            ? canvas.worldCamera
            : null;

        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 topRight = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

        ApplyMargins(Mathf.RoundToInt(bottomLeft.x),
                     Mathf.RoundToInt(Screen.height - topRight.y),
                     Mathf.RoundToInt(Screen.width - topRight.x),
                     Mathf.RoundToInt(bottomLeft.y));
    }

    /// <summary>
    /// Projects <see cref="worldAnchor"/>'s renderer bounds into screen space and pins the
    /// webview to that rectangle. Re-runs until two consecutive frames agree (letting a
    /// layout system such as Flexalon settle), then latches.
    /// </summary>
    private void UpdateMarginsFromWorldAnchor()
    {
        if (Screen.width != anchorScreenW || Screen.height != anchorScreenH)
        {
            anchorScreenW = Screen.width;
            anchorScreenH = Screen.height;
            RefreshAnchor();
        }

        if (anchorLatched)
        {
            return;
        }

        Camera cam = ResolveAnchorCamera();
        if (cam == null)
        {
            return;
        }

        Bounds bounds;
        if (!TryGetAnchorBounds(out bounds))
        {
            return;
        }

        // Screen-space AABB of the 8 corners. A corner behind the camera makes
        // WorldToScreenPoint's result meaningless, so abandon the frame rather than pin the
        // page to a garbage rect.
        Vector3 c = bounds.center;
        Vector3 e = bounds.extents;
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                c.x + ((i & 1) == 0 ? -e.x : e.x),
                c.y + ((i & 2) == 0 ? -e.y : e.y),
                c.z + ((i & 4) == 0 ? -e.z : e.z));

            Vector3 sp = cam.WorldToScreenPoint(corner);
            if (sp.z <= 0f)
            {
                return;
            }

            minX = Mathf.Min(minX, sp.x);
            minY = Mathf.Min(minY, sp.y);
            maxX = Mathf.Max(maxX, sp.x);
            maxY = Mathf.Max(maxY, sp.y);
        }

        int left = Mathf.RoundToInt(minX);
        int bottom = Mathf.RoundToInt(minY);
        int right = Mathf.RoundToInt(Screen.width - maxX);
        int top = Mathf.RoundToInt(Screen.height - maxY);

        // Latch only once the layout has stopped moving the anchor.
        if (anchorHasPrevious && left == prevAnchorLeft && top == prevAnchorTop
            && right == prevAnchorRight && bottom == prevAnchorBottom)
        {
            anchorLatched = true;
        }
        prevAnchorLeft = left;
        prevAnchorTop = top;
        prevAnchorRight = right;
        prevAnchorBottom = bottom;
        anchorHasPrevious = true;

        ApplyMargins(left, top, right, bottom);
    }

    /// <summary>
    /// Camera used to project the anchor: the serialized one, else Camera.main, else any
    /// camera in the scene.
    ///
    /// The scene-wide fallback matters because Camera.main only ever returns a camera tagged
    /// "MainCamera" - an untagged camera makes it null, which would otherwise leave the
    /// projection silently dead and the page stuck at whatever rect it last had.
    ///
    /// Must be the camera that actually renders the anchor: this measures where the object
    /// appears on screen, so a camera with a different transform or FOV gives a wrong rect.
    /// </summary>
    private Camera ResolveAnchorCamera()
    {
        if (anchorCamera != null)
        {
            return anchorCamera;
        }
        if (resolvedAnchorCamera != null)
        {
            return resolvedAnchorCamera;
        }

        resolvedAnchorCamera = Camera.main;
        if (resolvedAnchorCamera != null)
        {
            return resolvedAnchorCamera;
        }

#if UNITY_2023_1_OR_NEWER
        resolvedAnchorCamera = FindFirstObjectByType<Camera>();
#else
        resolvedAnchorCamera = FindObjectOfType<Camera>();
#endif

        if (!cameraWarningLogged)
        {
            cameraWarningLogged = true;
            if (resolvedAnchorCamera != null)
            {
                Debug.LogWarning("[WebViewController] '" + name + "': Camera.main is null (no camera " +
                                 "tagged MainCamera), falling back to '" + resolvedAnchorCamera.name +
                                 "' to position World Anchor. Tag that camera MainCamera, or assign " +
                                 "Anchor Camera explicitly.", this);
            }
            else
            {
                Debug.LogError("[WebViewController] '" + name + "': no camera found, so World Anchor " +
                               "cannot be projected and the webview will stay hidden. Assign " +
                               "Anchor Camera.", this);
            }
        }

        return resolvedAnchorCamera;
    }

    /// <summary>
    /// World bounds to project. Uses renderers under <see cref="worldAnchor"/>, falling back
    /// to the nearest ancestor that has one - a layout marker (e.g. a Flexalon node) often
    /// has no renderer of its own, and the visible surface is its parent.
    /// </summary>
    private bool TryGetAnchorBounds(out Bounds bounds)
    {
        bounds = new Bounds();

        var renderers = worldAnchor.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }
            return true;
        }

        var parentRenderer = worldAnchor.GetComponentInParent<Renderer>();
        if (parentRenderer != null)
        {
            bounds = parentRenderer.bounds;
            return true;
        }

        if (!anchorBoundsErrorLogged)
        {
            anchorBoundsErrorLogged = true;
            Debug.LogError("[WebViewController] '" + name + "': World Anchor '" + worldAnchor.name +
                           "' has no Renderer on itself, its children or any ancestor, so it has no " +
                           "bounds to project and the webview will stay hidden. Point World Anchor at " +
                           "the object that actually renders the surface.", this);
        }
        return false;
    }

    /// <summary>
    /// Re-runs the <see cref="worldAnchor"/> projection. Call after moving the anchor or
    /// changing the layout around it.
    /// </summary>
    public void RefreshAnchor()
    {
        anchorLatched = false;
        anchorHasPrevious = false;
    }

    /// <summary>
    /// Clamps, de-duplicates and forwards a margin set to the plugin. Shared by every
    /// positioning source so the degenerate-rect guard and change detection live in one place.
    /// </summary>
    private void ApplyMargins(int left, int top, int right, int bottom)
    {
        left = Mathf.Max(0, left);
        top = Mathf.Max(0, top);
        right = Mathf.Max(0, right);
        bottom = Mathf.Max(0, bottom);

        int width = Screen.width - (left + right);
        int height = Screen.height - (top + bottom);
        if (width < MinBoundsSize || height < MinBoundsSize)
        {
            // Degenerate rect (mid-layout, or the target collapsed) - a 0-sized bitmap
            // throws inside Texture2D's constructor, so skip this frame instead.
            return;
        }

        if (marginsValid && left == lastLeft && top == lastTop && right == lastRight && bottom == lastBottom)
        {
            return;
        }

        webViewObject.SetMargins(left, top, right, bottom);
        lastLeft = left;
        lastTop = top;
        lastRight = right;
        lastBottom = bottom;
        marginsValid = true;

        // The rect is now real, so it's safe to reveal the page.
        if (pendingShow)
        {
            pendingShow = false;
            SetVisibility(true);
        }
    }

    public void SetVisibility(bool visibility)
    {
        webViewObject.SetVisibility(visibility);
    }

    public bool GetVisibility()
    {
        return webViewObject.GetVisibility();
    }

    // Note: Load web view loads the page but wont make it visible.
    // to do this, you must run SetVisibility(true);
    private IEnumerator LoadWebView(string Url)
    {
        webViewObject.Init(
            cb: (msg) =>
            {
                // Every window.Unity.call(...) from the page lands here. WebViewObject has
                // already URL-unescaped it once (see CallFromJS), so listeners get the
                // decoded string.
                if (MessageReceived != null)
                {
                    MessageReceived(msg);
                }
            },
            err: (msg) =>
            {
                // Debug.Log(string.Format("CallOnError[{0}]", msg));
            },
            httpErr: (msg) =>
            {
                // Debug.Log(string.Format("CallOnHttpError[{0}]", msg));
            },
            started: (msg) =>
            {
                // Debug.Log(string.Format("CallOnStarted[{0}]", msg));
            },
            hooked: (msg) =>
            {
                // Debug.Log(string.Format("CallOnHooked[{0}]", msg));
            },
            cookies: (msg) =>
            {
                // Debug.Log(string.Format("CallOnCookies[{0}]", msg));
            },
            ld: (msg) =>
            {
                // Debug.Log(string.Format("CallOnLoaded[{0}]", msg));
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
                // NOTE: the following js definition is required only for UIWebView; if
                // enabledWKWebView is true and runtime has WKWebView, Unity.call is defined
                // directly by the native plugin.
#if true
                var js = @"
                    if (!(window.webkit && window.webkit.messageHandlers)) {
                        window.Unity = {
                            call: function(msg) {
                                window.location = 'unity:' + msg;
                            }
                        };
                    }
                ";
#else
                // NOTE: depending on the situation, you might prefer this 'iframe' approach.
                // cf. https://github.com/gree/unity-webview/issues/189
                var js = @"
                    if (!(window.webkit && window.webkit.messageHandlers)) {
                        window.Unity = {
                            call: function(msg) {
                                var iframe = document.createElement('IFRAME');
                                iframe.setAttribute('src', 'unity:' + msg);
                                document.documentElement.appendChild(iframe);
                                iframe.parentNode.removeChild(iframe);
                                iframe = null;
                            }
                        };
                    }
                ";
#endif
#elif UNITY_WEBPLAYER || UNITY_WEBGL
                var js = @"
                    window.Unity = {
                        call:function(msg) {
                            parent.unityWebView.sendMessage('WebViewObject', msg);
                        }
                    };
                ";
#else
                // Windows (WebView2) and Android.
                //
                // Android's native plugin already exposes window.Unity through
                // addJavascriptInterface. The Windows plugin does NOT - it only listens for
                // WebView2's WebMessageReceived - so without this shim `Unity.call(...)`
                // below is a ReferenceError and every JS -> C# message is silently lost.
                // Guarded on window.Unity being absent so a native implementation wins.
                var js = @"
                    if (!window.Unity || typeof window.Unity.call !== 'function') {
                        window.Unity = {
                            call: function (msg) {
                                if (window.chrome && window.chrome.webview
                                    && window.chrome.webview.postMessage) {
                                    window.chrome.webview.postMessage(msg);
                                } else {
                                    window.location = 'unity:' + msg;
                                }
                            }
                        };
                    }
                ";
#endif
                webViewObject.EvaluateJS(js + @"Unity.call('ua=' + navigator.userAgent)");

                if (removeAds)
                {
                    webViewObject.EvaluateJS(RemoveAdsJS);
                }
            }
            //transparent: false,
            //zoom: true,
            //ua: "custom user agent string",
            //radius: 0,  // rounded corner radius in pixel
            //// android
            //androidForceDarkMode: 0,  // 0: follow system setting, 1: force dark off, 2: force dark on
            //// ios
            //enableWKWebView: true,
            //wkContentMode: 0,  // 0: recommended, 1: mobile, 2: desktop
            //wkAllowsLinkPreview: true,
            //// editor
            //separated: false
            );
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        webViewObject.bitmapRefreshCycle = 1;
        webViewObject.devicePixelRatio = 1;  // 1 or 2
#endif
        // cf. https://github.com/gree/unity-webview/pull/512
        // Added alertDialogEnabled flag to enable/disable alert/confirm/prompt dialogs. by KojiNakamaru · Pull Request #512 · gree/unity-webview
        //webViewObject.SetAlertDialogEnabled(false);

        // cf. https://github.com/gree/unity-webview/pull/728
        //webViewObject.SetCameraAccess(true);
        //webViewObject.SetMicrophoneAccess(true);

        // cf. https://github.com/gree/unity-webview/pull/550
        // introduced SetURLPattern(..., hookPattern). by KojiNakamaru · Pull Request #550 · gree/unity-webview
        //webViewObject.SetURLPattern("", "^https://.*youtube.com", "^https://.*google.com");

        // cf. https://github.com/gree/unity-webview/pull/570
        // Add BASIC authentication feature (Android and iOS with WKWebView only) by takeh1k0 · Pull Request #570 · gree/unity-webview
        //webViewObject.SetBasicAuthInfo("id", "password");

        //webViewObject.SetScrollbarsVisibility(true);

        // Must mirror UpdateMarginsFromBounds()'s precedence exactly. Testing only
        // boundsPanel here was what pinned a worldAnchor-positioned webview to the static
        // margins - all zeros, i.e. full screen - before the anchor ever got a say.
        if (hasOwnWindow)
        {
            // WebViewWindow drives margins/visibility itself, every LateUpdate, from its
            // own content area - setting them here too would just cause a one-frame
            // full-screen flash before it corrects them on the next frame.
        }
        else if (HasPositioningSource)
        {
            UpdateMarginsFromBounds();
        }
        else
        {
            webViewObject.SetMargins(LeftMargin, TopMargin, RightMargin, BottomMargin);
        }
        webViewObject.SetTextZoom(100);  // android only. cf. https://stackoverflow.com/questions/21647641/android-webview-set-font-size-system-default/47017410#47017410

#if !UNITY_WEBPLAYER && !UNITY_WEBGL
        if (Url.StartsWith("http")) {
            LoadHome(Url.Replace(" ", "%20"));
        } else {
            // Url is a path relative to StreamingAssets, e.g. "MonacoEditor/index.html".
            //
            // NOTE: this replaces gree's sample loop, which copied Url with each of
            // .jpg/.js/.html substituted in. That only ever suited their 3-file sample -
            // for any other page the first substitution names a file that doesn't exist
            // and File.ReadAllBytes throws.
            var src = System.IO.Path.Combine(Application.streamingAssetsPath, Url);

            if (src.Contains("://")) {
                // Android: StreamingAssets lives inside the APK, so the page has to be
                // copied out to a real file before the webview can open it.
                var dst = System.IO.Path.Combine(Application.temporaryCachePath, Url);
                byte[] result = null;
#if UNITY_2018_4_OR_NEWER
                var unityWebRequest = UnityWebRequest.Get(src);
                yield return unityWebRequest.SendWebRequest();
                result = unityWebRequest.downloadHandler.data;
#else
                var www = new WWW(src);
                yield return www;
                result = www.bytes;
#endif
                if (result == null) {
                    Debug.LogError("[WebViewController] Could not read '" + src + "' from StreamingAssets.", this);
                    yield break;
                }
                var dstDir = System.IO.Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) {
                    System.IO.Directory.CreateDirectory(dstDir);
                }
                System.IO.File.WriteAllBytes(dst, result);
                LoadHome(ToFileUrl(dst));
            } else {
                // Editor / desktop: StreamingAssets is a real folder, so open the page
                // where it already lives - copying just the .html elsewhere would break
                // any sibling files it references relatively.
                if (!System.IO.File.Exists(src)) {
                    Debug.LogError("[WebViewController] '" + Url + "' not found under StreamingAssets " +
                                   "(looked for '" + src + "').", this);
                    yield break;
                }
                LoadHome(ToFileUrl(src));
            }
        }
#else
        if (Url.StartsWith("http")) {
            LoadHome(Url.Replace(" ", "%20"));
        } else {
            LoadHome("StreamingAssets/" + Url.Replace(" ", "%20"));
        }
#endif
        yield break;
    }
}