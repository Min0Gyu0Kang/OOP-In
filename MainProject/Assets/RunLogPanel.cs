using System.Text;
using UnityEngine;

/// <summary>
/// Shows the current run's results in a webview hosting StreamingAssets/results.html. Rows
/// logged before the page has loaded are replayed from <see cref="OOPIn.RunLog.Entries"/>.
/// </summary>
[RequireComponent(typeof(WebViewController))]
public class RunLogPanel : MonoBehaviour
{
    [SerializeField] private WebViewController webViewController;

    private void Awake()
    {
        if (webViewController == null)
        {
            webViewController = GetComponent<WebViewController>();
        }
    }

    private void OnEnable()
    {
        webViewController.PageLoaded += OnPageLoaded;
        OOPIn.RunLog.Logged += OnLogged;
        OOPIn.RunLog.Cleared += OnCleared;
    }

    private void OnDisable()
    {
        webViewController.PageLoaded -= OnPageLoaded;
        OOPIn.RunLog.Logged -= OnLogged;
        OOPIn.RunLog.Cleared -= OnCleared;
    }

    private void OnPageLoaded()
    {
        var js = new StringBuilder("window.clearLog && window.clearLog();");
        foreach (var entry in OOPIn.RunLog.Entries)
        {
            js.Append(AppendJs(entry));
        }

        var farm = OOPIn.FarmBridgeManager.Instance;
        if (farm != null)
        {
            js.Append("window.setPlantNames && window.setPlantNames([");
            bool first = true;
            foreach (var plant in farm.PlantNames)
            {
                if (!first) js.Append(',');
                js.Append(WebViewController.ToJsString(plant));
                first = false;
            }
            js.Append("]);");
        }

        webViewController.EvaluateJS(js.ToString());
    }

    private void OnLogged(OOPIn.RunLogEntry entry)
    {
        webViewController.EvaluateJS(AppendJs(entry));
    }

    private void OnCleared()
    {
        webViewController.EvaluateJS("window.clearLog && window.clearLog();");
    }

    private static string AppendJs(OOPIn.RunLogEntry entry)
    {
        return "window.appendLog && window.appendLog(" + (entry.ok ? "true" : "false") + ", " +
               entry.line + ", " + WebViewController.ToJsString(entry.text) + ");";
    }
}
