using UnityEngine;
#if UNITY_EDITOR
using UnityEditor.Scripting.Python;
#endif

/// <summary>
/// Holds one long-lived Python session so successive submissions from the Monaco editor see
/// each other's variables, and executes code handed over by <see cref="MonacoBridge"/>.
///
/// NOTE: com.unity.scripting.python's PythonRunner lives in UnityEditor and only runs inside
/// the Editor, so every call into it is guarded. In a player build this class compiles and
/// runs but reports that execution is unavailable, rather than breaking the build.
/// </summary>
public class InteractivePythonManager : MonoBehaviour
{
    public static InteractivePythonManager Instance { get; private set; }

    private const string SessionScope = "__main__";

    [Tooltip("Runs once at startup, before the built-in Bridge wrapper. Use it for your own " +
             "imports or helpers.")]
    [TextArea(6, 20)]
    public string bootstrapScript =
        "import clr\n" +
        "clr.AddReference('Assembly-CSharp')\n" +
        "import UnityEngine\n";

    // Always installed after bootstrapScript, so it can't go stale in a serialized scene.
    // Shadows the C# Bridge with a wrapper that passes each call's editor line number.
    private const string BridgeWrapper =
        "import clr as _clr, sys as _sys\n" +
        "_clr.AddReference('Assembly-CSharp')\n" +
        "from OOPIn import Bridge as _CsBridge\n" +
        "class _Bridge(object):\n" +
        "    def Plow(self, x, z): return _CsBridge.Plow(x, z, _sys._getframe(1).f_lineno)\n" +
        "    def Remove(self, x, z): return _CsBridge.Remove(x, z, _sys._getframe(1).f_lineno)\n" +
        "    def Plant(self, x, z, name): return _CsBridge.Plant(x, z, name, _sys._getframe(1).f_lineno)\n" +
        "    def Harvest(self, x, z): return _CsBridge.Harvest(x, z, _sys._getframe(1).f_lineno)\n" +
        "Bridge = _Bridge()\n";

    // Submitted code is compiled under the filename "<editor>" so the error's line can be
    // told apart from lines inside the wrapper, and reported instead of thrown.
    private const string RunnerTemplate =
        "import base64 as _b64, traceback as _tb\n" +
        "try:\n" +
        "    exec(compile(_b64.b64decode('{0}').decode('utf-8'), '<editor>', 'exec'))\n" +
        "except SyntaxError as _e:\n" +
        "    _CsBridge.ReportError(_e.lineno or 0, 'SyntaxError', str(_e.msg))\n" +
        "except Exception as _e:\n" +
        "    _ln = 0\n" +
        "    for _f in _tb.extract_tb(_e.__traceback__):\n" +
        "        if _f.filename == '<editor>': _ln = _f.lineno\n" +
        "    _CsBridge.ReportError(_ln, type(_e).__name__, str(_e))\n";

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Start()
    {
        if (!string.IsNullOrEmpty(bootstrapScript))
        {
            Run(bootstrapScript, "bootstrap");
        }
        Run(BridgeWrapper, "Bridge wrapper");
    }

    /// <summary>
    /// Resets the farm and the run log, then runs user code in the same session as everything
    /// before it. Python errors are reported to the run log with their editor line.
    /// </summary>
    public void ExecuteSessionCode(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return;
        }
        if (OOPIn.FarmBridgeManager.Instance != null)
        {
            OOPIn.FarmBridgeManager.Instance.ResetForRun();
        }
        OOPIn.RunLog.Clear();

        var encoded = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(code));
        Run(string.Format(RunnerTemplate, encoded), "submitted code");

        // RunString is synchronous, so the whole script has been checked by now: play it,
        // or report the first error and play nothing.
        if (OOPIn.FarmBridgeManager.Instance != null)
        {
            OOPIn.FarmBridgeManager.Instance.CommitRun();
        }
    }

    private void Run(string code, string what)
    {
#if UNITY_EDITOR
        try
        {
            // Without a scope name RunString execs in a fresh globals dict every call, so
            // the bootstrap's imports and earlier submissions' variables would vanish.
            PythonRunner.RunString(code, SessionScope);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[InteractivePythonManager] Python error running " + what + ":\n" + e.Message, this);
            OOPIn.Bridge.ReportError(0, "Python error in " + what, e.Message);
        }
#else
        Debug.LogWarning("[InteractivePythonManager] Python scripting is Editor-only; " +
                         "skipped " + what + ".", this);
        OOPIn.RunLog.Add(new OOPIn.RunLogEntry { ok = false, text = "Python runs only in the Unity Editor." });
#endif
    }
}
