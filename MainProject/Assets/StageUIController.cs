using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Drives the stage webviews: Canvas/Problem (question.html) and Canvas/Condition
/// (condition.html). Stages are authored as JSON in the Inspector. Star results from
/// <see cref="OOPIn.PythonASTEvaluator"/> are shown only after the farm animation finishes.
/// </summary>
public class StageUIController : MonoBehaviour
{
    public static StageUIController Instance { get; private set; }

    [Header("Webviews")]
    [Tooltip("Canvas/Problem - WebViewController with Url 'Question/question.html'.")]
    [SerializeField] private WebViewController problemView;
    [Tooltip("Canvas/Condition - WebViewController with Url 'Condition/condition.html'.")]
    [SerializeField] private WebViewController conditionView;

    [Header("Stages")]
    public List<StageEntry> stages = DefaultStages();
    [Min(0)] public int startStage;

    private static readonly string[] StarRules =
    {
        "Your entry function returns the expected value for every test case.",
        "Define a class with a method using self, store data on self, create an instance, and call a method on it.",
        "Pass Star 1, and keep the number of executed steps growing logarithmically as the input grows (e.g. halve the search range each step)."
    };

    private int current;
    private QuestionData question;
    private ConditionData condition;
    private string questionError;
    private string conditionError;
    private string lastLoggedError;

    private readonly bool[] stars = new bool[3];
    private readonly string[] details = { "", "", "" };

    private bool hasPendingResult;
    private OOPIn.StageResult pendingResult;

    /// <summary>The stage's conditionJson, or null when there is no valid stage to grade.</summary>
    public string CurrentConditionJson
    {
        get { return condition != null && conditionError == null ? stages[current].conditionJson : null; }
    }

    public string CurrentStageName
    {
        get { return stages != null && current < stages.Count ? stages[current].name : ""; }
    }

    private void Reset()
    {
        stages = DefaultStages();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[StageUIController] Duplicate on '" + name + "' ignored.", this);
            enabled = false;
            return;
        }
        Instance = this;

        if (problemView == null)
            Debug.LogError("[StageUIController] Problem View is not assigned - drag Canvas/Problem in.", this);
        if (conditionView == null)
            Debug.LogError("[StageUIController] Condition View is not assigned - drag Canvas/Condition in.", this);

        current = (stages == null || stages.Count == 0) ? 0 : Mathf.Clamp(startStage, 0, stages.Count - 1);
        ParseCurrent();
    }

    private void OnEnable()
    {
        if (problemView != null) problemView.PageLoaded += PushQuestion;
        if (conditionView != null)
        {
            conditionView.PageLoaded += PushCondition;
            conditionView.MessageReceived += OnConditionMessage;
        }
        OOPIn.FarmBridgeManager.PlaybackFinished += OnPlaybackFinished;
    }

    private void OnDisable()
    {
        if (problemView != null) problemView.PageLoaded -= PushQuestion;
        if (conditionView != null)
        {
            conditionView.PageLoaded -= PushCondition;
            conditionView.MessageReceived -= OnConditionMessage;
        }
        OOPIn.FarmBridgeManager.PlaybackFinished -= OnPlaybackFinished;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // Lets JSON edited in the Inspector during Play show up immediately.
    private void OnValidate()
    {
        if (!Application.isPlaying || Instance != this) return;
        ParseCurrent();
        PushQuestion();
        PushCondition();
    }

    // ---- Runs ---------------------------------------------------------------------------

    /// <summary>Called when new code is submitted: clears stars and any result still waiting.</summary>
    public void OnRunStarted()
    {
        hasPendingResult = false;
        SetStars(false, false, false, "", "", "");
        PushCondition();
    }

    /// <summary>
    /// Shows an evaluation. While the farm's tool/plant animation is still playing, the result
    /// is held and applied when it finishes.
    /// </summary>
    public void ShowResult(OOPIn.StageResult result)
    {
        var farm = OOPIn.FarmBridgeManager.Instance;
        if (farm != null && farm.IsPlaying)
        {
            pendingResult = result;
            hasPendingResult = true;
            return;
        }
        ApplyResult(result);
    }

    private void OnPlaybackFinished()
    {
        if (!hasPendingResult) return;
        hasPendingResult = false;
        ApplyResult(pendingResult);
    }

    private void ApplyResult(OOPIn.StageResult r)
    {
        SetStars(r.star1, r.star2, r.star3, r.detail1, r.detail2, r.detail3);
        PushCondition();

        string earned = (r.star1 ? "★" : "☆") + (r.star2 ? "★" : "☆") + (r.star3 ? "★" : "☆");
        OOPIn.RunLog.Add(new OOPIn.RunLogEntry { ok = r.star1, text = CurrentStageName + " result: " + earned });
        OOPIn.RunLog.Add(new OOPIn.RunLogEntry { ok = r.star1, text = "cleared: " + r.detail1 });
        OOPIn.RunLog.Add(new OOPIn.RunLogEntry { ok = r.star2, text = "OOP: " + r.detail2 });
        OOPIn.RunLog.Add(new OOPIn.RunLogEntry { ok = r.star3, text = "O(log n): " + r.detail3 });
    }

    private void SetStars(bool s1, bool s2, bool s3, string d1, string d2, string d3)
    {
        stars[0] = s1; stars[1] = s2; stars[2] = s3;
        details[0] = d1 ?? ""; details[1] = d2 ?? ""; details[2] = d3 ?? "";
    }

    private void OnConditionMessage(string message)
    {
        if (message != "stage-next") return;

        if (!stars[0])
        {
            Debug.Log("[StageUIController] Clear the stage (Star 1) before moving on.", this);
            return;
        }
        if (stages == null || current + 1 >= stages.Count)
        {
            OOPIn.RunLog.Add(new OOPIn.RunLogEntry { ok = true, text = "That was the last stage." });
            return;
        }

        current++;
        hasPendingResult = false;
        SetStars(false, false, false, "", "", "");
        ParseCurrent();
        PushQuestion();
        PushCondition();
    }

    // ---- Parsing ------------------------------------------------------------------------

    private void ParseCurrent()
    {
        question = null;
        condition = null;
        questionError = null;
        conditionError = null;

        if (stages == null || stages.Count == 0)
        {
            questionError = conditionError = "no stages defined";
            LogOnce("[StageUIController] No stages defined.");
            return;
        }

        var stage = stages[current];
        question = Parse<QuestionData>(stage.questionJson, out questionError);
        if (question != null && string.IsNullOrEmpty(question.title)) questionError = "\"title\" is required";

        condition = Parse<ConditionData>(stage.conditionJson, out conditionError);
        if (condition != null && string.IsNullOrEmpty(condition.entry)) conditionError = "\"entry\" is required";

        if (questionError != null) LogOnce("[Stage \"" + stage.name + "\"] questionJson: " + questionError);
        if (conditionError != null) LogOnce("[Stage \"" + stage.name + "\"] conditionJson: " + conditionError);
    }

    private static T Parse<T>(string json, out string error) where T : class
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty";
            return null;
        }
        try
        {
            var data = JsonUtility.FromJson<T>(json);
            if (data == null) error = "not a JSON object";
            return data;
        }
        catch (System.Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    // Typing JSON in the Inspector re-validates on every keystroke; log each problem once.
    private void LogOnce(string message)
    {
        if (message == lastLoggedError) return;
        lastLoggedError = message;
        Debug.LogError(message, this);
    }

    // ---- Pages --------------------------------------------------------------------------

    private void PushQuestion()
    {
        if (problemView == null) return;

        string title, html;
        if (questionError != null)
        {
            title = "Invalid stage";
            html = "<p class=\"invalid\">questionJson: " + Escape(questionError) + "</p>";
        }
        else
        {
            title = question.title;
            html = BuildDescriptionHtml(question);
        }
        problemView.EvaluateJS("window.loadStageData && window.loadStageData(" +
                               WebViewController.ToJsString(title) + ", " +
                               WebViewController.ToJsString(html) + ");");
    }

    private void PushCondition()
    {
        if (conditionView == null) return;

        var js = new StringBuilder();
        js.Append("if (window.updateConditionUI) {");

        js.Append("setStarRules([");
        for (int i = 0; i < StarRules.Length; i++)
        {
            if (i > 0) js.Append(',');
            js.Append(WebViewController.ToJsString(StarRules[i]));
        }
        js.Append("]);");

        js.Append("setTests([");
        if (condition != null && conditionError == null && condition.tests != null)
        {
            for (int i = 0; i < condition.tests.Count; i++)
            {
                var t = condition.tests[i];
                if (i > 0) js.Append(',');
                js.Append("{args:").Append(WebViewController.ToJsString(t.args ?? ""))
                  .Append(",expected:").Append(WebViewController.ToJsString(t.expected ?? ""))
                  .Append(",ctor:").Append(WebViewController.ToJsString(t.ctor ?? "")).Append('}');
            }
        }
        js.Append("]);");

        bool valid = condition != null && conditionError == null;
        js.Append("setInvalid(").Append(WebViewController.ToJsString(valid ? "" : "conditionJson: " + conditionError)).Append(");");
        js.Append("updateConditionUI(")
          .Append(WebViewController.ToJsString(valid ? condition.sampleInput ?? "" : "")).Append(',')
          .Append(WebViewController.ToJsString(valid ? condition.sampleOutput ?? "" : "")).Append(',')
          .Append(Bool(stars[0])).Append(',').Append(Bool(stars[1])).Append(',').Append(Bool(stars[2])).Append(");");
        js.Append("setStarDetails([")
          .Append(WebViewController.ToJsString(details[0])).Append(',')
          .Append(WebViewController.ToJsString(details[1])).Append(',')
          .Append(WebViewController.ToJsString(details[2])).Append("]);");
        js.Append("setHasNext(").Append(Bool(stages != null && current + 1 < stages.Count)).Append(");");

        js.Append('}');
        conditionView.EvaluateJS(js.ToString());
    }

    private static string Bool(bool b) { return b ? "true" : "false"; }

    /// <summary>
    /// Plain-text description -> paragraphs (split on blank lines) with `backtick` spans as
    /// inline code, followed by the stage code as a code block. Everything is HTML-escaped.
    /// </summary>
    private static string BuildDescriptionHtml(QuestionData q)
    {
        var html = new StringBuilder();
        var text = (q.description ?? "").Replace("\r\n", "\n");
        foreach (var para in Regex.Split(text, @"\n\s*\n"))
        {
            if (string.IsNullOrWhiteSpace(para)) continue;
            var body = Regex.Replace(Escape(para.Trim()), "`([^`]+)`", "<code>$1</code>").Replace("\n", "<br>");
            html.Append("<p>").Append(body).Append("</p>");
        }
        if (!string.IsNullOrEmpty(q.code))
        {
            html.Append("<pre><code>").Append(Escape(q.code.Replace("\r\n", "\n"))).Append("</code></pre>");
        }
        return html.ToString();
    }

    private static string Escape(string s)
    {
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static List<StageEntry> DefaultStages()
    {
        return new List<StageEntry>
        {
            new StageEntry
            {
                name = "Stage 1",
                questionJson =
@"{
  ""title"": ""Find the Seed Bag"",
  ""description"": ""The shelf holds seed bags sorted by weight.\n\nComplete `Shelf.find_index` so it returns the index of the bag with the `target` weight, or `-1` if there is none. The grader calls `find_index(bags, target)`."",
  ""code"": ""class Shelf:\n    def __init__(self, bags):\n        self.bags = bags\n\n    def find_index(self, target):\n        # return the index of target in self.bags, or -1\n        return -1\n\n\ndef find_index(bags, target):\n    return Shelf(bags).find_index(target)""
}",
                conditionJson =
@"{
  ""entry"": ""find_index"",
  ""sampleInput"": ""bags = [2, 5, 8, 12, 16]\ntarget = 12"",
  ""sampleOutput"": ""3"",
  ""tests"": [
    { ""args"": ""([2, 5, 8, 12, 16], 12)"", ""expected"": ""3"" },
    { ""args"": ""([2, 5, 8, 12, 16], 2)"", ""expected"": ""0"" },
    { ""args"": ""([1, 3], 4)"", ""expected"": ""-1"" },
    { ""args"": ""([], 7)"", ""expected"": ""-1"" }
  ],
  ""complexity"": {
    ""sizes"": [1024, 16384, 262144, 1048576],
    ""genArgs"": ""lambda n: (list(range(n)), n - 1)""
  },
  ""stepLimit"": 200000
}"
            }
        };
    }
}
