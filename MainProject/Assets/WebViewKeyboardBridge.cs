using UnityEngine;

/// <summary>
/// Forwards navigation keys and editing shortcuts to a page that defines
/// <c>window.unityKey(name, shift)</c>, <c>window.unityCopy(cut)</c> and
/// <c>window.unityPaste(text)</c> (see StreamingAssets/index.html).
///
/// WebViewObject only forwards printable text (Input.inputString) to the native plugin, and
/// the plugin's handling of non-character key codes is undocumented. Driving the page's own
/// commands through JavaScript is deterministic instead. Clipboard goes through
/// GUIUtility.systemCopyBuffer, since the offscreen page can't reach the OS clipboard.
/// </summary>
[RequireComponent(typeof(WebViewController))]
public class WebViewKeyboardBridge : MonoBehaviour
{
    [SerializeField] private WebViewController webViewController;

    private const string ClipboardMessage = "clipboard?text=";

    // Unity may deliver one shortcut as both KeyDown and ExecuteCommand in the same frame.
    private int lastActionFrame = -1;
    private string lastAction;

    private void Awake()
    {
        if (webViewController == null)
        {
            webViewController = GetComponent<WebViewController>();
        }
    }

    private void OnEnable()
    {
        webViewController.MessageReceived += OnMessageFromPage;
    }

    private void OnDisable()
    {
        webViewController.MessageReceived -= OnMessageFromPage;
    }

    private void OnMessageFromPage(string message)
    {
        if (message != null && message.StartsWith(ClipboardMessage))
        {
            GUIUtility.systemCopyBuffer = message.Substring(ClipboardMessage.Length);
        }
    }

    private void OnGUI()
    {
        if (!webViewController.HasFocus)
        {
            return;
        }

        var e = Event.current;
        switch (e.type)
        {
            case EventType.KeyDown:
                HandleKey(e);
                break;

            case EventType.ValidateCommand:
                if (CommandAction(e.commandName) != null)
                {
                    e.Use();
                }
                break;

            case EventType.ExecuteCommand:
                var action = CommandAction(e.commandName);
                if (action != null)
                {
                    Perform(action, false);
                    e.Use();
                }
                break;
        }
    }

    private void HandleKey(Event e)
    {
        bool ctrl = e.control || e.command;
        string action = null;

        if (ctrl)
        {
            switch (e.keyCode)
            {
                case KeyCode.A: action = "selectAll"; break;
                case KeyCode.C: action = "copy"; break;
                case KeyCode.X: action = "cut"; break;
                case KeyCode.V: action = "paste"; break;
                case KeyCode.Z: action = e.shift ? "redo" : "undo"; break;
                case KeyCode.Y: action = "redo"; break;
                case KeyCode.Slash: action = "comment"; break;
                case KeyCode.Return:
                case KeyCode.KeypadEnter: action = "run"; break;
                case KeyCode.LeftArrow: action = "wordLeft"; break;
                case KeyCode.RightArrow: action = "wordRight"; break;
                case KeyCode.Home: action = "top"; break;
                case KeyCode.End: action = "bottom"; break;
            }
        }
        else
        {
            switch (e.keyCode)
            {
                case KeyCode.LeftArrow: action = "left"; break;
                case KeyCode.RightArrow: action = "right"; break;
                case KeyCode.UpArrow: action = "up"; break;
                case KeyCode.DownArrow: action = "down"; break;
                case KeyCode.Home: action = "home"; break;
                case KeyCode.End: action = "end"; break;
                case KeyCode.PageUp: action = "pageUp"; break;
                case KeyCode.PageDown: action = "pageDown"; break;
                case KeyCode.Delete: action = "delete"; break;
                case KeyCode.Escape: action = "escape"; break;
            }
        }

        if (action != null)
        {
            Perform(action, e.shift);
            e.Use();
        }
    }

    private static string CommandAction(string commandName)
    {
        switch (commandName)
        {
            case "Copy": return "copy";
            case "Cut": return "cut";
            case "Paste": return "paste";
            case "SelectAll": return "selectAll";
            case "Undo": return "undo";
            case "Redo": return "redo";
            default: return null;
        }
    }

    private void Perform(string action, bool shift)
    {
        if (Time.frameCount == lastActionFrame && action == lastAction)
        {
            return;
        }
        lastActionFrame = Time.frameCount;
        lastAction = action;

        string js;
        switch (action)
        {
            case "copy": js = "window.unityCopy && window.unityCopy(false);"; break;
            case "cut": js = "window.unityCopy && window.unityCopy(true);"; break;
            case "paste":
                js = "window.unityPaste && window.unityPaste(" +
                     WebViewController.ToJsString(GUIUtility.systemCopyBuffer ?? "") + ");";
                break;
            default:
                js = "window.unityKey && window.unityKey('" + action + "', " + (shift ? "true" : "false") + ");";
                break;
        }
        webViewController.EvaluateJS(js);
    }
}
