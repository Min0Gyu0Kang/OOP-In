using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OOPIn
{
    /// <summary>
    /// Grades a run against the current stage's conditionJson.
    ///
    /// Not cleared (0 stars): the run stopped on an error, or the farm did not end in the
    ///                        expected state.
    /// Star 1 "cleared": harvest counts and plot states match <see cref="ConditionData.expected"/>
    ///                   (checked in C# from the farm's projected end state).
    /// Star 2 "OOP": the AST shows a class with a self method, self attributes, an instance,
    ///               and a method call.
    /// Star 3 "O(log n)": the code is re-run on growing inputs against a stand-in Bridge; both
    ///                    executed line steps (time) and peak allocated memory (space) must grow
    ///                    no faster than log n.
    /// Stars 2 and 3 are graded by Python and come back through <see cref="ReportEvaluation"/>.
    /// </summary>
    public static class PythonASTEvaluator
    {
        // No double quotes inside: kept as a C# verbatim string. %%...%% values are base64 or literals.
        private const string Template = @"
import ast as _oo_ast, io as _oo_io, json as _oo_json, math as _oo_math, sys as _oo_sys, base64 as _oo_b64
import builtins as _oo_builtins, tracemalloc as _oo_tm
from contextlib import redirect_stdout as _oo_redirect
from OOPIn import PythonASTEvaluator as _oo_eval

def _oo_d(s):
    return _oo_b64.b64decode(s).decode('utf-8')

def _oo_check_oop(src):
    try:
        tree = _oo_ast.parse(src)
    except SyntaxError:
        return False, 'code could not be parsed'
    classes = [n for n in _oo_ast.walk(tree) if isinstance(n, _oo_ast.ClassDef)]
    names = set(c.name for c in classes)
    methods = set()
    self_method = False
    self_attr = False
    for c in classes:
        for item in c.body:
            if isinstance(item, (_oo_ast.FunctionDef, _oo_ast.AsyncFunctionDef)):
                methods.add(item.name)
                if item.args.args and item.args.args[0].arg == 'self':
                    self_method = True
        for n in _oo_ast.walk(c):
            if isinstance(n, _oo_ast.Attribute) and isinstance(n.value, _oo_ast.Name) and n.value.id == 'self':
                self_attr = True
    calls = [n for n in _oo_ast.walk(tree) if isinstance(n, _oo_ast.Call)]
    instantiated = any(isinstance(n.func, _oo_ast.Name) and n.func.id in names for n in calls)
    method_called = any(isinstance(n.func, _oo_ast.Attribute) and n.func.attr in methods
                        and not n.func.attr.startswith('__') for n in calls)
    missing = []
    if not classes:
        missing.append('a class')
    if classes and not self_method:
        missing.append('a method taking self')
    if classes and not self_attr:
        missing.append('data stored on self')
    if not instantiated:
        missing.append('an instance of your class')
    if not method_called:
        missing.append('a method call on an instance')
    if missing:
        return False, 'missing ' + ', '.join(missing)
    return True, 'class, self, instance and method call found'

class _OoStepLimit(Exception):
    pass

class _OoMockBridge(object):
    # Same commands as the real Bridge; records nothing and never touches the farm.
    def Plow(self, x, z): return True
    def Remove(self, x, z): return True
    def Plant(self, x, z, name): return True
    def Harvest(self, x, z): return True

def _oo_measure(code, inputs, n, step_limit):
    # Runs the whole program once for input size n. Returns (steps, peak bytes).
    g = {'__builtins__': _oo_builtins, '__name__': '__main__', 'Bridge': _OoMockBridge()}
    if inputs is not None:
        g.update(inputs(n))
    counter = [0]

    def local_trace(frame, event, arg):
        if event == 'line':
            counter[0] += 1
            if counter[0] > step_limit:
                raise _OoStepLimit()
        return local_trace

    def global_trace(frame, event, arg):
        return local_trace if frame.f_code.co_filename == '<editor>' else None

    _oo_tm.start()
    base = _oo_tm.get_traced_memory()[0]
    _oo_sys.settrace(global_trace)
    try:
        with _oo_redirect(_oo_io.StringIO()):
            exec(code, g)
    finally:
        _oo_sys.settrace(None)
        peak = _oo_tm.get_traced_memory()[1]
        _oo_tm.stop()
    return counter[0], max(0, peak - base)

def _oo_check_complexity(src, cond):
    sizes = sorted(int(x) for x in ((cond.get('complexity') or {}).get('sizes') or []))
    if len(sizes) < 2:
        return False, 'this stage has no complexity setup'
    step_limit = int(cond.get('stepLimit') or 200000)
    inputs = eval(cond['inputs'], {}) if cond.get('inputs') else None
    code = compile(src, '<editor>', 'exec')
    n = sizes[0]
    try:
        steps = []
        peaks = []
        for n in sizes:
            s, p = _oo_measure(code, inputs, n, step_limit)
            steps.append(s)
            peaks.append(p)
    except _OoStepLimit:
        return False, 'stopped at n=%d after %d steps: not O(log n) time' % (n, step_limit)
    except Exception as e:
        return False, 'measurement failed at n=%d: %s: %s' % (n, type(e).__name__, e)

    r = _oo_math.log2(sizes[-1]) / _oo_math.log2(max(sizes[0], 2))
    time_ok = steps[-1] <= steps[0] * r * 2 + 20
    space_ok = peaks[-1] <= peaks[0] * r * 2 + 4096
    detail = 'time %d -> %d steps, space %.1f -> %.1f KB for n %d -> %d' % (
        steps[0], steps[-1], peaks[0] / 1024.0, peaks[-1] / 1024.0, sizes[0], sizes[-1])
    if not time_ok:
        detail += ' (time grows faster than O(log n))'
    if not space_ok:
        detail += ' (space grows faster than O(log n))'
    return time_ok and space_ok, detail

def _oo_evaluate():
    cond = _oo_json.loads(_oo_d('%%COND%%'))
    src = _oo_d('%%SRC%%')
    d1 = _oo_d('%%D1%%')
    s2, d2 = _oo_check_oop(src)
    s3, d3 = _oo_check_complexity(src, cond)
    _oo_eval.ReportEvaluation(True, bool(s2), bool(s3), d1, d2, d3)

try:
    _oo_evaluate()
except Exception as _oo_e:
    _oo_sys.settrace(None)
    _oo_eval.ReportEvaluation(True, False, False, _oo_d('%%D1%%'),
                              'evaluator error: %s: %s' % (type(_oo_e).__name__, _oo_e), '')
";

        // Makes the stage's input variables (conditionJson "inputs" at runSize) visible to the run.
        private const string InputTemplate = @"
import base64 as _oo_b64
_oo_inputs_src = _oo_b64.b64decode('%%INPUTS%%').decode('utf-8')
if _oo_inputs_src.strip():
    globals().update(eval(_oo_inputs_src, {})(%%N%%))
";

        /// <summary>Python that defines the stage input variables before the player's code runs.</summary>
        public static string BuildInputScript(ConditionData condition)
        {
            if (condition == null || string.IsNullOrEmpty(condition.inputs)) return null;
            return InputTemplate
                .Replace("%%INPUTS%%", Base64(condition.inputs))
                .Replace("%%N%%", condition.runSize.ToString());
        }

        /// <summary>Python that grades Stars 2 and 3 for a run that already cleared the stage.</summary>
        public static string BuildScript(string conditionJson, string userCode, string clearedDetail)
        {
            return Template
                .Replace("%%COND%%", Base64(conditionJson))
                .Replace("%%SRC%%", Base64(userCode))
                .Replace("%%D1%%", Base64(clearedDetail));
        }

        /// <summary>
        /// Star 1: does the farm's end state match the goal? <paramref name="detail"/> says what
        /// matched, or the first things that did not.
        /// </summary>
        public static bool CheckFarmGoal(ConditionData condition, FarmBridgeManager farm, out string detail)
        {
            if (farm == null)
            {
                detail = "no farm in the scene";
                return false;
            }
            var goal = condition.expected ?? new FarmGoal();
            var problems = new List<string>();

            var wanted = new HashSet<string>();
            foreach (var h in goal.harvest)
            {
                if (h == null || string.IsNullOrEmpty(h.plant)) continue;
                string key = farm.PlantKey(h.plant) ?? h.plant;
                wanted.Add(key);
                int got = farm.ProjectedHarvest(key);
                if (got < h.count || (goal.exactHarvest && got != h.count))
                {
                    problems.Add(key + " harvested " + got + "/" + h.count);
                }
            }
            if (goal.exactHarvest)
            {
                foreach (var pair in farm.ProjectedHarvests)
                {
                    if (pair.Value > 0 && !wanted.Contains(pair.Key)) problems.Add(pair.Key + " should not be harvested");
                }
            }

            string mode = string.IsNullOrEmpty(goal.plots) ? "any" : goal.plots.ToLowerInvariant();
            if (mode == "empty" || mode == "listed")
            {
                var listed = new Dictionary<int, PlotGoal>();
                if (mode == "listed")
                {
                    foreach (var p in goal.plotList)
                    {
                        if (p != null) listed[p.z * farm.Columns + p.x] = p;
                    }
                }
                for (int z = 0; z < farm.RowCount; z++)
                {
                    for (int x = 0; x < farm.Columns; x++)
                    {
                        FarmBridgeManager.PlotState state;
                        string plant;
                        farm.TryGetProjectedPlot(x, z, out state, out plant);

                        PlotGoal want;
                        string wantState = "Empty";
                        string wantPlant = null;
                        if (listed.TryGetValue(z * farm.Columns + x, out want))
                        {
                            wantState = string.IsNullOrEmpty(want.state) ? "Planted" : want.state;
                            wantPlant = string.IsNullOrEmpty(want.plant) ? null : (farm.PlantKey(want.plant) ?? want.plant);
                        }

                        bool ok = string.Equals(state.ToString(), wantState, StringComparison.OrdinalIgnoreCase) &&
                                  (wantPlant == null || state != FarmBridgeManager.PlotState.Planted || plant == wantPlant);
                        if (!ok)
                        {
                            string gotText = state == FarmBridgeManager.PlotState.Planted ? plant : state.ToString().ToLowerInvariant();
                            problems.Add("plot (" + x + "," + z + ") should be " +
                                         (wantPlant != null ? wantPlant : wantState.ToLowerInvariant()) + ", is " + gotText);
                        }
                    }
                }
            }

            if (problems.Count == 0)
            {
                detail = "farm matches the expected output";
                return true;
            }
            const int shown = 3;
            detail = string.Join("; ", problems.GetRange(0, Math.Min(shown, problems.Count)).ToArray()) +
                     (problems.Count > shown ? " (+" + (problems.Count - shown) + " more)" : "");
            return false;
        }

        /// <summary>Called from Python when grading finishes.</summary>
        public static void ReportEvaluation(bool star1, bool star2, bool star3, string detail1, string detail2, string detail3)
        {
            var ui = StageUIController.Instance;
            if (ui == null)
            {
                Debug.Log("[PythonASTEvaluator] Stars " + star1 + "/" + star2 + "/" + star3 + " (no StageUIController in the scene).");
                return;
            }
            ui.ShowResult(new StageResult
            {
                star1 = star1, star2 = star2, star3 = star3,
                detail1 = detail1, detail2 = detail2, detail3 = detail3
            });
        }

        /// <summary>The stage is not cleared: 0 stars, and the reason goes to the run log.</summary>
        public static void ReportNotCleared(string reason)
        {
            ReportEvaluation(false, false, false, reason, "", "");
        }

        private static string Base64(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        }
    }
}
