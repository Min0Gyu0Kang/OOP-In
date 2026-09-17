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
    // told apart from lines inside the wrapper, and reported instead of thrown. A line-step
    // cap turns an endless loop into an error instead of freezing the Editor.
    private const string RunnerTemplate =
        "import base64 as _b64, traceback as _tb, sys as _rsys\n" +
        "class StepLimitError(Exception): pass\n" +
        "_steps = [0]\n" +
        "def _line_trace(_f, _ev, _a):\n" +
        "    if _ev == 'line':\n" +
        "        _steps[0] += 1\n" +
        "        if _steps[0] > {1}: raise StepLimitError('stopped after {1} steps - endless loop?')\n" +
        "    return _line_trace\n" +
        "def _run_trace(_f, _ev, _a):\n" +
        "    return _line_trace if _f.f_code.co_filename == '<editor>' else None\n" +
        "_rsys.settrace(_run_trace)\n" +
        "try:\n" +
        "    exec(compile(_b64.b64decode('{0}').decode('utf-8'), '<editor>', 'exec'))\n" +
        "except SyntaxError as _e:\n" +
        "    _CsBridge.ReportError(_e.lineno or 0, 'SyntaxError', str(_e.msg))\n" +
        "except Exception as _e:\n" +
        "    _ln = 0\n" +
        "    for _f in _tb.extract_tb(_e.__traceback__):\n" +
        "        if _f.filename == '<editor>': _ln = _f.lineno\n" +
        "    _CsBridge.ReportError(_ln, type(_e).__name__, str(_e))\n" +
        "finally:\n" +
        "    _rsys.settrace(None)\n";

    private const int RunStepLimit = 2000000;

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
        var stageUI = StageUIController.Instance;
        // Before the farm reset: that reset ends any playback, which must not release a
        // result left over from the previous run.
        if (stageUI != null)
        {
            stageUI.OnRunStarted();
        }
        var farm = OOPIn.FarmBridgeManager.Instance;
        if (farm != null)
        {
            farm.ResetForRun();
        }
        OOPIn.RunLog.Clear();
        OOPIn.Bridge.RunHadError = false;

        // The stage's input variables (e.g. bags, target) exist before the code runs.
        var condition = stageUI != null ? stageUI.CurrentCondition : null;
        var inputScript = OOPIn.PythonASTEvaluator.BuildInputScript(condition);
        if (inputScript != null)
        {
            Run(inputScript, "stage inputs");
        }

        var encoded = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(code));
        if (!OOPIn.Bridge.RunHadError)
        {
            Run(string.Format(RunnerTemplate, encoded, RunStepLimit), "submitted code");
        }

        // RunString is synchronous, so the whole script has been checked by now: play it,
        // or report the first error and play nothing.
        if (farm != null)
        {
            farm.CommitRun();
        }

        // Grade the stage. The result is shown by StageUIController once the farm animation
        // above has finished.
        if (condition != null)
        {
            string detail;
            if (OOPIn.Bridge.RunHadError || (farm != null && farm.RunFailed))
            {
                OOPIn.PythonASTEvaluator.ReportNotCleared("the run stopped on an error");
            }
            else if (!OOPIn.PythonASTEvaluator.CheckFarmGoal(condition, farm, out detail))
            {
                OOPIn.PythonASTEvaluator.ReportNotCleared(detail);
            }
            else
            {
                Run(OOPIn.PythonASTEvaluator.BuildScript(stageUI.CurrentConditionJson, code, detail), "stage evaluation");
            }
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
