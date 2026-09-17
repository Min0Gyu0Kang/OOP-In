using System;
using System.Text;
using UnityEngine;

namespace OOPIn
{
    /// <summary>
    /// Grades a run against the current stage's conditionJson. The grading itself is Python,
    /// run in the same session scope right after the user's code, so the user's functions and
    /// classes are callable. Results come back through <see cref="ReportEvaluation"/>.
    ///
    /// Star 1 "cleared": the entry function returns the expected value for every test.
    /// Star 2 "OOP": the AST shows a class with a self method, self attributes, an instance,
    ///               and a method call.
    /// Star 3 "O(log n)": executed line count (sys.settrace) grows logarithmically with n.
    /// </summary>
    public static class PythonASTEvaluator
    {
        // No double quotes inside: kept as a C# verbatim string. %%COND%% / %%SRC%% are base64.
        private const string Template = @"
import ast as _oo_ast, io as _oo_io, json as _oo_json, math as _oo_math, sys as _oo_sys, base64 as _oo_b64
from contextlib import redirect_stdout as _oo_redirect
from OOPIn import PythonASTEvaluator as _oo_eval

def _oo_evaluate(g):
    cond = _oo_json.loads(_oo_b64.b64decode('%%COND%%').decode('utf-8'))
    src = _oo_b64.b64decode('%%SRC%%').decode('utf-8')
    entry = cond.get('entry') or ''
    step_limit = int(cond.get('stepLimit') or 200000)

    class StepLimit(Exception):
        pass

    counter = [0]

    def local_trace(frame, event, arg):
        if event == 'line':
            counter[0] += 1
            if counter[0] > step_limit:
                raise StepLimit()
        return local_trace

    def global_trace(frame, event, arg):
        return local_trace if frame.f_code.co_filename == '<editor>' else None

    def traced(fn, args):
        counter[0] = 0
        _oo_sys.settrace(global_trace)
        try:
            with _oo_redirect(_oo_io.StringIO()):
                return fn(*args)
        finally:
            _oo_sys.settrace(None)

    def as_tuple(v):
        return v if isinstance(v, tuple) else (v,)

    def resolve(ctor):
        if '.' in entry:
            cls_name, meth = entry.split('.', 1)
            cls = g.get(cls_name)
            if cls is None:
                raise NameError('class %s is not defined' % cls_name)
            with _oo_redirect(_oo_io.StringIO()):
                obj = cls(*ctor)
            return getattr(obj, meth)
        fn = g.get(entry)
        if fn is None or not callable(fn):
            raise NameError('function %s is not defined' % entry)
        return fn

    # ---- Star 1: test cases ----
    tests = cond.get('tests') or []
    passed = 0
    first_fail = ''
    for i, t in enumerate(tests, 1):
        try:
            args = as_tuple(_oo_ast.literal_eval(t.get('args') or '()'))
            expected = _oo_ast.literal_eval(t.get('expected'))
            ctor = as_tuple(_oo_ast.literal_eval(t.get('ctor') or '()'))
            got = traced(resolve(ctor), args)
            if got == expected:
                passed += 1
            elif not first_fail:
                first_fail = 'test %d expected %r, got %r' % (i, expected, got)
        except StepLimit:
            if not first_fail:
                first_fail = 'test %d stopped after %d steps (too slow or endless loop)' % (i, step_limit)
        except Exception as e:
            if not first_fail:
                first_fail = 'test %d raised %s: %s' % (i, type(e).__name__, e)
    s1 = len(tests) > 0 and passed == len(tests)
    if not tests:
        d1 = 'no test cases defined for this stage'
    else:
        d1 = '%d/%d tests passed' % (passed, len(tests)) + ('' if s1 else ' - ' + first_fail)

    # ---- Star 2: OOP structure ----
    s2 = False
    try:
        tree = _oo_ast.parse(src)
    except SyntaxError:
        tree = None
    if tree is None:
        d2 = 'code could not be parsed'
    else:
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
        s2 = not missing
        d2 = 'class, self, instance and method call found' if s2 else 'missing ' + ', '.join(missing)

    # ---- Star 3: step growth ----
    s3 = False
    if not s1:
        d3 = 'needs Star 1 first'
    else:
        comp = cond.get('complexity') or {}
        sizes = sorted(int(x) for x in (comp.get('sizes') or []))
        gen_src = comp.get('genArgs')
        if len(sizes) < 2 or not gen_src:
            d3 = 'this stage has no complexity setup'
        else:
            n = sizes[0]
            steps = []
            try:
                gen = eval(gen_src, {})
                for n in sizes:
                    produced = gen(n)
                    if '.' in entry:
                        ctor, args = produced
                    else:
                        ctor, args = (), produced
                    traced(resolve(as_tuple(ctor)), as_tuple(args))
                    steps.append(counter[0])
                lo, hi = steps[0], steps[-1]
                allowed = lo * (_oo_math.log2(sizes[-1]) / _oo_math.log2(max(sizes[0], 2))) * 2 + 20
                s3 = hi <= allowed
                d3 = 'steps %d -> %d for n %d -> %d' % (lo, hi, sizes[0], sizes[-1])
                if not s3:
                    d3 += ' (grows faster than O(log n))'
            except StepLimit:
                d3 = 'stopped at n=%d after %d steps: not O(log n)' % (n, step_limit)
            except Exception as e:
                d3 = 'measurement failed at n=%d: %s: %s' % (n, type(e).__name__, e)

    _oo_eval.ReportEvaluation(bool(s1), bool(s2), bool(s3), d1, d2, d3)

try:
    _oo_evaluate(globals())
except Exception as _oo_e:
    _oo_sys.settrace(None)
    _oo_eval.ReportEvaluation(False, False, False, 'evaluator error: %s: %s' % (type(_oo_e).__name__, _oo_e), '', '')
";

        /// <summary>Python that grades the code currently loaded in the session.</summary>
        public static string BuildScript(string conditionJson, string userCode)
        {
            return Template
                .Replace("%%COND%%", Base64(conditionJson))
                .Replace("%%SRC%%", Base64(userCode));
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

        /// <summary>No grading happened because the run itself stopped on an error.</summary>
        public static void ReportNotRun(string reason)
        {
            ReportEvaluation(false, false, false, reason, reason, reason);
        }

        private static string Base64(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        }
    }
}
