using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>One stage as authored in the Inspector: two JSON documents, one per webview.</summary>
[Serializable]
public class StageEntry
{
    public string name = "Stage";

    [Tooltip("Shown in Canvas/Problem. { \"title\", \"description\", \"code\" }")]
    [TextArea(8, 30)] public string questionJson;

    [Tooltip("Shown in Canvas/Condition and used to grade runs. { \"entry\", \"sampleInput\", " +
             "\"sampleOutput\", \"tests\": [{\"args\",\"expected\",\"ctor\"}], " +
             "\"complexity\": {\"sizes\", \"genArgs\"}, \"stepLimit\" }")]
    [TextArea(8, 30)] public string conditionJson;
}

/// <summary>questionJson. All fields are plain text; the page renders <c>code</c> as a code block.</summary>
[Serializable]
public class QuestionData
{
    public string title;
    public string description;
    public string code;
}

/// <summary>
/// conditionJson. Test values are Python literals kept as strings so JsonUtility can parse
/// them; the evaluator reads them with ast.literal_eval.
/// </summary>
[Serializable]
public class ConditionData
{
    /// <summary>"function" or "Class.method".</summary>
    public string entry;
    public string sampleInput;
    public string sampleOutput;
    public List<StageTest> tests = new List<StageTest>();
    public StageComplexity complexity = new StageComplexity();
    public int stepLimit = 200000;
}

[Serializable]
public class StageTest
{
    /// <summary>Python literal of the call arguments, e.g. "([1, 2, 3], 2)".</summary>
    public string args;
    /// <summary>Python literal of the expected return value.</summary>
    public string expected;
    /// <summary>Constructor arguments when entry is "Class.method". Optional.</summary>
    public string ctor;
}

[Serializable]
public class StageComplexity
{
    /// <summary>Input sizes to measure, smallest to largest.</summary>
    public int[] sizes;
    /// <summary>
    /// Python lambda n -> call args (a tuple), or -> (ctor args, call args) when entry is
    /// "Class.method".
    /// </summary>
    public string genArgs;
}

namespace OOPIn
{
    public struct StageResult
    {
        public bool star1, star2, star3;
        public string detail1, detail2, detail3;
    }
}
