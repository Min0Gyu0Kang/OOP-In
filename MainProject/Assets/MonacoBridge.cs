using UnityEngine;

/// <summary>
/// Bridges the Monaco editor page to Unity, on top of the existing
/// <see cref="WebViewObject"/> / <see cref="WebViewController"/> pair.
///
/// Replaces the earlier MonacoWebViewManager, which targeted UniWebView (a separate asset
/// that isn't in this project) and spawned a second webview of its own. This instead rides
/// the webview already configured on this GameObject - so the Inspector's Url, Bounds Panel
/// and WebViewWindow chrome all keep working - and listens for JS messages rather than
/// UniWebView's OnMessageReceived.
///
/// Page side: <c>Unity.call('run-python?code=' + encodeURIComponent(code))</c>.
/// </summary>
[RequireComponent(typeof(WebViewController))]
public class MonacoBridge : MonoBehaviour
{
    [Tooltip("The webview hosting the Monaco page. Found on this GameObject when empty.")]
    [SerializeField] private WebViewController webViewController;

    [Tooltip("Optional. Receives code submitted from the editor. Found in the scene when " +
             "empty; without one, submitted code is just logged.")]
    [SerializeField] private InteractivePythonManager pythonManager;

    [Tooltip("Log every message arriving from the page - useful when wiring up new " +
             "commands in index.html.")]
    public bool logMessages;

    /// <summary>Raised with the code the user submitted from the editor.</summary>
    public event System.Action<string> CodeSubmitted;

    // Message format agreed with index.html: "<command>?code=<payload>".
    private const string RunPythonCommand = "run-python";
    private const string CodeParameter = "code=";

    private void Awake()
    {
        if (webViewController == null)
        {
            webViewController = GetComponent<WebViewController>();
        }
        if (pythonManager == null)
        {
#if UNITY_2023_1_OR_NEWER
            pythonManager = FindFirstObjectByType<InteractivePythonManager>();
#else
            pythonManager = FindObjectOfType<InteractivePythonManager>();
#endif
        }
    }

    private void OnEnable()
    {
        if (webViewController != null)
        {
            webViewController.MessageReceived += OnMessageFromPage;
        }
    }

    private void OnDisable()
    {
        if (webViewController != null)
        {
            webViewController.MessageReceived -= OnMessageFromPage;
        }
    }

    private void OnMessageFromPage(string message)
    {
        if (logMessages)
        {
            Debug.Log("[MonacoBridge] " + message, this);
        }

        if (string.IsNullOrEmpty(message) || !message.StartsWith(RunPythonCommand))
        {
            // Not ours - WebViewController also sends a "ua=..." handshake on every load.
            return;
        }

        var marker = message.IndexOf(CodeParameter, System.StringComparison.Ordinal);
        if (marker < 0)
        {
            Debug.LogWarning("[MonacoBridge] '" + RunPythonCommand + "' message had no '" +
                             CodeParameter + "' payload: " + message, this);
            return;
        }

        // Take the whole remainder rather than splitting on '&'. WebViewObject.CallFromJS
        // already URL-unescaped the message, so by now the code is real source text and may
        // legitimately contain '&', '?' and newlines - splitting would truncate it.
        var code = message.Substring(marker + CodeParameter.Length);
        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        if (CodeSubmitted != null)
        {
            CodeSubmitted(code);
        }

        if (pythonManager != null)
        {
            pythonManager.ExecuteSessionCode(code);
        }
        else
        {
            Debug.Log("[MonacoBridge] No InteractivePythonManager in the scene; code was:\n" + code, this);
        }
    }
}
