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

    [Tooltip("Shown in Canvas/Condition and used to grade runs. { \"sampleInput\", \"sampleOutput\", " +
             "\"helpfulCommands\": [..], \"inputs\", \"runSize\", \"expected\": {\"harvest\": [{\"plant\",\"count\"}], " +
             "\"exactHarvest\", \"plots\": \"any|empty|listed\", \"plotList\": [{\"x\",\"z\",\"state\",\"plant\"}]}, " +
             "\"complexity\": {\"sizes\"}, \"stepLimit\" }")]
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
/// conditionJson. A stage is cleared when the farm ends in the <see cref="expected"/> state.
/// </summary>
[Serializable]
public class ConditionData
{
    public string sampleInput;
    public string sampleOutput;
    /// <summary>Shown as code rows on the Condition page, e.g. "Bridge.Harvest(x, z)".</summary>
    public List<string> helpfulCommands = new List<string>();

    /// <summary>
    /// Python lambda n -> dict of variables the player's code can read, e.g.
    /// "lambda n: {'bags': list(range(n)), 'target': n - 1}". Optional.
    /// </summary>
    public string inputs;
    /// <summary>n used for the real run on the farm (the sample input).</summary>
    public int runSize = 25;

    public FarmGoal expected = new FarmGoal();
    public StageComplexity complexity = new StageComplexity();
    /// <summary>Line steps allowed per run before it counts as an endless loop.</summary>
    public int stepLimit = 200000;
}

/// <summary>What the farm must look like after the run.</summary>
[Serializable]
public class FarmGoal
{
    public List<HarvestGoal> harvest = new List<HarvestGoal>();
    /// <summary>When true, harvesting more than the goal (or other plants) fails.</summary>
    public bool exactHarvest;
    /// <summary>"any" (default): plots don't matter. "empty": every plot empty.
    /// "listed": plots in <see cref="plotList"/> must match, every other plot empty.</summary>
    public string plots = "any";
    public List<PlotGoal> plotList = new List<PlotGoal>();
}

[Serializable]
public class HarvestGoal
{
    public string plant;
    public int count;
}

[Serializable]
public class PlotGoal
{
    public int x;
    public int z;
    /// <summary>"Empty", "Plowed" or "Planted".</summary>
    public string state = "Planted";
    /// <summary>Required plant when state is "Planted". Optional.</summary>
    public string plant;
}

[Serializable]
public class StageComplexity
{
    /// <summary>Input sizes n passed to <see cref="ConditionData.inputs"/> when measuring Star 3.</summary>
    public int[] sizes;
}

namespace OOPIn
{
    public struct StageResult
    {
        public bool star1, star2, star3;
        public string detail1, detail2, detail3;
    }
}
