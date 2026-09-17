using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Drives the stage webviews: Canvas/Problem (question.html), Canvas/Condition
/// (condition.html, sample input/output) and the centered result popup
/// (<see cref="StageResultModal"/>). Stages are authored as JSON in the Inspector. Star
/// results from <see cref="OOPIn.PythonASTEvaluator"/> open the popup only after the farm
/// animation finishes.
/// </summary>
public class StageUIController : MonoBehaviour
{
    public static StageUIController Instance { get; private set; }

    [Header("Webviews")]
    [Tooltip("Canvas/Problem - WebViewController with Url 'Question/question.html'.")]
    [SerializeField] private WebViewController problemView;
    [Tooltip("Canvas/Condition - WebViewController with Url 'Condition/condition.html'.")]
    [SerializeField] private WebViewController conditionView;
    [Tooltip("Canvas/StageResult - the centered popup (StageResultModal) showing the stars.")]
    [SerializeField] private StageResultModal resultModal;

    [Header("Stages")]
    public List<StageEntry> stages = DefaultStages();
    [Min(0)] public int startStage;

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

    /// <summary>The parsed conditionJson, or null when there is no valid stage to grade.</summary>
    public ConditionData CurrentCondition
    {
        get { return conditionError == null ? condition : null; }
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
        if (resultModal == null)
            Debug.LogError("[StageUIController] Result Modal is not assigned - drag Canvas/StageResult in.", this);

        current = (stages == null || stages.Count == 0) ? 0 : Mathf.Clamp(startStage, 0, stages.Count - 1);
        ParseCurrent();
    }

    private void OnEnable()
    {
        if (problemView != null) problemView.PageLoaded += PushQuestion;
        if (conditionView != null) conditionView.PageLoaded += PushCondition;
        if (resultModal != null) resultModal.NextPressed += OnNextPressed;
        OOPIn.FarmBridgeManager.PlaybackFinished += OnPlaybackFinished;
    }

    private void OnDisable()
    {
        if (problemView != null) problemView.PageLoaded -= PushQuestion;
        if (conditionView != null) conditionView.PageLoaded -= PushCondition;
        if (resultModal != null) resultModal.NextPressed -= OnNextPressed;
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

    /// <summary>Called when new code is submitted: closes the popup and drops any result still waiting.</summary>
    public void OnRunStarted()
    {
        hasPendingResult = false;
        SetStars(false, false, false, "", "", "");
        if (resultModal != null) resultModal.Hide();
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

        // Not cleared: no popup, the stage keeps running and the log says why.
        if (!r.star1)
        {
            OOPIn.RunLog.Add(new OOPIn.RunLogEntry { ok = false, text = CurrentStageName + " not cleared: " + r.detail1 });
            return;
        }

        if (resultModal != null) resultModal.Show(r.star1, r.star2, r.star3);

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

    // Next on the popup: advance when the stage is cleared, otherwise close so the player can retry.
    private void OnNextPressed()
    {
        if (!stars[0])
        {
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
        OOPIn.RunLog.Clear();
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
        if (condition != null && (condition.expected == null ||
                                  (condition.expected.harvest.Count == 0 &&
                                   (string.IsNullOrEmpty(condition.expected.plots) || condition.expected.plots == "any"))))
            conditionError = "\"expected\" needs a harvest goal or a plots goal";

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

        bool valid = condition != null && conditionError == null;
        var commands = new StringBuilder("[");
        if (valid && condition.helpfulCommands != null)
        {
            for (int i = 0; i < condition.helpfulCommands.Count; i++)
            {
                if (i > 0) commands.Append(',');
                commands.Append(WebViewController.ToJsString(condition.helpfulCommands[i] ?? ""));
            }
        }
        commands.Append(']');

        conditionView.EvaluateJS(
            "if (window.setSamples) {" +
            "setInvalid(" + WebViewController.ToJsString(valid ? "" : "conditionJson: " + conditionError) + ");" +
            "setSamples(" + WebViewController.ToJsString(valid ? condition.sampleInput ?? "" : "") + "," +
                            WebViewController.ToJsString(valid ? condition.sampleOutput ?? "" : "") + "," +
                            commands + ");" +
            "}");
    }


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
  ""description"": ""`bags` holds seed bag weights sorted from light to heavy, one bag per plot: bag i belongs to plot (i % 5, i // 5).\n\nFind the bag whose weight is `target`, then Plow its plot, Plant a Carrot there and Harvest it."",
  ""code"": ""class Shelf:\n    def __init__(self, bags):\n        self.bags = bags\n\n    def find_index(self, target):\n        # return the index of target in self.bags\n        return 0\n\n\ni = Shelf(bags).find_index(target)\nBridge.Plow(i % 5, i // 5)""
}",
                conditionJson =
@"{
  ""sampleInput"": ""bags = [2, 5, 8, ..., 74]   # 25 bags\ntarget = 50"",
  ""sampleOutput"": ""Carrot harvested: 1\nplot (1, 3) plowed, every other plot empty"",
  ""helpfulCommands"": [""Bridge.Plow(x, z)"", ""Bridge.Plant(x, z, \""Carrot\"")"", ""Bridge.Harvest(x, z)""],
  ""inputs"": ""lambda n: {'bags': [2 + 3 * i for i in range(n)], 'target': 2 + 3 * (n * 2 // 3)}"",
  ""runSize"": 25,
  ""expected"": {
    ""harvest"": [ { ""plant"": ""Carrot"", ""count"": 1 } ],
    ""exactHarvest"": true,
    ""plots"": ""listed"",
    ""plotList"": [ { ""x"": 1, ""z"": 3, ""state"": ""Plowed"" } ]
  },
  ""complexity"": { ""sizes"": [64, 1024, 16384, 262144] },
  ""stepLimit"": 200000
}"
            },
            new StageEntry
            {
                name = "Stage 2",
                questionJson =
@"{
  ""title"": ""High Water"",
  ""description"": ""`levels` holds field heights sorted from low to high. Water rises to `water`: every field with height <= `water` floods.\n\nCount the flooded fields `k`, then grow a Pumpkin on plot (k % 5, k // 5) and leave it planted. Don't harvest anything."",
  ""code"": ""class Flood:\n    def __init__(self, levels):\n        self.levels = levels\n\n    def count_flooded(self, water):\n        # number of levels <= water\n        return 0\n\n\nk = Flood(levels).count_flooded(water)""
}",
                conditionJson =
@"{
  ""sampleInput"": ""levels = [0, 2, 4, ..., 48]   # 25 fields\nwater = 25"",
  ""sampleOutput"": ""k = 13\nPumpkin planted on plot (3, 2), nothing harvested"",
  ""helpfulCommands"": [""Bridge.Plow(x, z)"", ""Bridge.Plant(x, z, \""Pumpkin\"")""],
  ""inputs"": ""lambda n: {'levels': [2 * i for i in range(n)], 'water': n}"",
  ""runSize"": 25,
  ""expected"": {
    ""harvest"": [],
    ""exactHarvest"": true,
    ""plots"": ""listed"",
    ""plotList"": [ { ""x"": 3, ""z"": 2, ""state"": ""Planted"", ""plant"": ""Pumpkin"" } ]
  },
  ""complexity"": { ""sizes"": [64, 1024, 16384, 262144] },
  ""stepLimit"": 200000
}"
            }
        };
    }
}
