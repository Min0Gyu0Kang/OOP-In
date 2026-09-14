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

    [Tooltip("Runs once at startup to set up the persistent Python session.")]
    [TextArea(6, 20)]
    public string bootstrapScript =
        "import UnityEngine\n" +
        "\n" +
        "class FarmerWrapper:\n" +
        "    def move(self):\n" +
        "        UnityEngine.Debug.Log('Farmer Move Called from Python!')\n" +
        "\n" +
        "farmer = FarmerWrapper()\n";

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
        InitGlobalPythonContext();
    }

    /// <summary>Seeds the global Python scope the editor's submissions run against.</summary>
    private void InitGlobalPythonContext()
    {
        if (string.IsNullOrEmpty(bootstrapScript))
        {
            return;
        }
        Run(bootstrapScript, "bootstrap");
    }

    /// <summary>
    /// Runs user code in the same session as everything before it, so state carries over
    /// between submissions.
    /// </summary>
    public void ExecuteSessionCode(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return;
        }
        Run(code, "submitted code");
    }

    private void Run(string code, string what)
    {
#if UNITY_EDITOR
        try
        {
            PythonRunner.RunString(code);
        }
        catch (System.Exception e)
        {
            // Surface syntax/runtime errors from the page instead of letting the exception
            // escape into the webview message pump.
            Debug.LogError("[InteractivePythonManager] Python error running " + what + ":\n" + e.Message, this);
        }
#else
        Debug.LogWarning("[InteractivePythonManager] Python scripting is Editor-only; " +
                         "skipped " + what + ".", this);
#endif
    }
}
