using UnityEngine;

/// <summary>
/// Centered "stage result" popup (StreamingAssets/Result/stage_result.html). The page is
/// preloaded hidden, kept centered on screen, drawn above the other webviews, and shown
/// when <see cref="StageUIController"/> has a result.
///
/// Unlike WebViewWindow there is no draggable chrome: a modal has a fixed place.
/// </summary>
[RequireComponent(typeof(WebViewController))]
public class StageResultModal : MonoBehaviour
{
    [SerializeField] private WebViewController webViewController;

    [Header("Layout")]
    [Tooltip("Modal width as a fraction of the screen width.")]
    [Range(0.2f, 1f)] public float widthFraction = 0.42f;
    [Tooltip("Width / height of the modal (the mockup is 760x424).")]
    public float aspect = 760f / 424f;
    [Tooltip("Never taller than this fraction of the screen height.")]
    [Range(0.2f, 1f)] public float maxHeightFraction = 0.8f;
    [Tooltip("Desktop draw order: must be lower than every other webview's (they default to 0).")]
    public int drawOrder = -100;

    /// <summary>Raised when the player presses Next on the popup.</summary>
    public event System.Action NextPressed;

    public bool IsOpen { get; private set; }

    private bool star1, star2, star3;

    private void Awake()
    {
        if (webViewController == null)
        {
            webViewController = GetComponent<WebViewController>();
        }
        if (!webViewController.startHidden)
        {
            Debug.LogWarning("[StageResultModal] '" + name + "': turn on Start Hidden on the WebViewController, " +
                             "otherwise the popup shows as soon as the scene starts.", this);
        }
    }

    private void OnEnable()
    {
        webViewController.PageLoaded += PushStars;
        webViewController.MessageReceived += OnMessage;
    }

    private void OnDisable()
    {
        webViewController.PageLoaded -= PushStars;
        webViewController.MessageReceived -= OnMessage;
    }

    private void LateUpdate()
    {
        webViewController.SetDrawOrder(drawOrder);

        int w = Mathf.RoundToInt(Screen.width * widthFraction);
        int h = Mathf.RoundToInt(w / Mathf.Max(aspect, 0.1f));
        int maxH = Mathf.RoundToInt(Screen.height * maxHeightFraction);
        if (h > maxH)
        {
            h = maxH;
            w = Mathf.RoundToInt(h * aspect);
        }
        int left = (Screen.width - w) / 2;
        int top = (Screen.height - h) / 2;
        webViewController.SetScreenMargins(left, top, Screen.width - w - left, Screen.height - h - top);
    }

    /// <summary>Shows the popup with the given stars.</summary>
    public void Show(bool cleared, bool oop, bool optimal)
    {
        star1 = cleared;
        star2 = oop;
        star3 = optimal;
        PushStars();
        webViewController.SetVisibility(true);
        IsOpen = true;
    }

    public void Hide()
    {
        if (webViewController.IsLoaded)
        {
            webViewController.SetVisibility(false);
        }
        IsOpen = false;
    }

    private void PushStars()
    {
        webViewController.EvaluateJS("window.setStageResult && window.setStageResult(" +
                                     (star1 ? "true" : "false") + "," +
                                     (star2 ? "true" : "false") + "," +
                                     (star3 ? "true" : "false") + ");");
    }

    private void OnMessage(string message)
    {
        if (message != "next-stage") return;
        Hide();
        if (NextPressed != null) NextPressed();
    }
}
