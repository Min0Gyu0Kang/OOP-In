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

    [Tooltip("Strip ad / sidebar elements from the page once it finishes loading.")]
    public bool removeAds = true;

    [Tooltip("Load the page as soon as the scene starts. Leave OFF when something else " +
             "opens the webview on demand (e.g. WebViewTrigger on the guide cube) - the " +
             "native webview is then not created and no network request is made until " +
             "Load() is called.")]
    public bool loadOnStart = false;

    private Coroutine _loadCoroutine;

    // Guards Load() so repeated triggers don't Init()/LoadURL() the webview twice.
    private bool _loadStarted;

    // Below this, treat the panel's on-screen rect as degenerate rather than asking the
    // native plugin for a near-zero bitmap (mirrors the guard in WebViewObject.Update()).
    private const int MinBoundsSize = 16;

    private bool marginsValid;
    private int lastLeft, lastTop, lastRight, lastBottom;

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
        if (loadOnStart)
        {
            Load();
        }
    }

    /// <summary>True once <see cref="Load"/> has run.</summary>
    public bool IsLoaded { get { return _loadStarted; } }

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
        _loadStarted = true;

        _loadCoroutine = StartCoroutine(LoadWebView(Url));
        if (!hasOwnWindow)
        {
            // With a WebViewWindow present, it owns visibility instead.
            SetVisibility(true);
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
    /// Computes screen-space margins from <see cref="boundsPanel"/>'s current rect and
    /// applies them via SetMargins(), matching the math WebViewObject itself uses (a
    /// bottom-left screen origin). Does nothing if boundsPanel isn't assigned, or if a
    /// WebViewWindow on this GameObject already owns margins/visibility.
    /// </summary>
    private void UpdateMarginsFromBounds()
    {
        if (hasOwnWindow || boundsPanel == null || webViewObject == null)
        {
            return;
        }

        Vector3[] corners = new Vector3[4];
        boundsPanel.GetWorldCorners(corners);

        Canvas canvas = boundsPanel.GetComponentInParent<Canvas>();
        Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            ? canvas.worldCamera
            : null;

        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
        Vector2 topRight = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);

        int left = Mathf.Max(0, Mathf.RoundToInt(bottomLeft.x));
        int bottom = Mathf.Max(0, Mathf.RoundToInt(bottomLeft.y));
        int right = Mathf.Max(0, Mathf.RoundToInt(Screen.width - topRight.x));
        int top = Mathf.Max(0, Mathf.RoundToInt(Screen.height - topRight.y));

        int width = Screen.width - (left + right);
        int height = Screen.height - (top + bottom);
        if (width < MinBoundsSize || height < MinBoundsSize)
        {
            // Degenerate rect (mid-layout, or the panel collapsed) - a 0-sized bitmap
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
                // Debug.Log(string.Format("CallFromJS[{0}]", msg));
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
                var js = "";
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

        if (hasOwnWindow)
        {
            // WebViewWindow drives margins/visibility itself, every LateUpdate, from its
            // own content area - setting them here too would just cause a one-frame
            // full-screen flash before it corrects them on the next frame.
        }
        else if (boundsPanel != null)
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
            var exts = new string[]{
                ".jpg",
                ".js",
                ".html"  // should be last
            };
            foreach (var ext in exts) {
                var url = Url.Replace(".html", ext);
                var src = System.IO.Path.Combine(Application.streamingAssetsPath, url);
                var dst = System.IO.Path.Combine(Application.temporaryCachePath, url);
                byte[] result = null;
                if (src.Contains("://")) {  // for Android
#if UNITY_2018_4_OR_NEWER
                    // NOTE: a more complete code that utilizes UnityWebRequest can be found in https://github.com/gree/unity-webview/commit/2a07e82f760a8495aa3a77a23453f384869caba7#diff-4379160fa4c2a287f414c07eb10ee36d
                    var unityWebRequest = UnityWebRequest.Get(src);
                    yield return unityWebRequest.SendWebRequest();
                    result = unityWebRequest.downloadHandler.data;
#else
                    var www = new WWW(src);
                    yield return www;
                    result = www.bytes;
#endif
                } else {
                    result = System.IO.File.ReadAllBytes(src);
                }
                System.IO.File.WriteAllBytes(dst, result);
                if (ext == ".html") {
                    LoadHome("file://" + dst.Replace(" ", "%20"));
                    break;
                }
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