# How this project maps onto Model–View–Controller

The project is not MVC by framework; it is MVC by convention, and the split is unusually clean
because the **view layer is literally a separate process**: HTML pages rendered by a native
WebView. Nothing in the pages can touch game state except through messages, which forces the
boundary that MVC only asks for politely.

There are really **two MVC triangles** that meet at one point:

1. a **farm simulation** triangle (grid, plants, tools),
2. a **stage/teaching** triangle (question, condition, grading, stars).

They meet at `InteractivePythonManager.ExecuteSessionCode`, the single controller entry point for
"the player pressed Run".

---

## 1. The Model — state, no presentation

| Type | File | What it owns |
|---|---|---|
| `FarmBridgeManager` (state half) | `MainProject/Assets/FarmBridgeManager.cs` | `plots[]`, `plotPlant[]`, `projectedPlots[]`, `projectedHarvest`, `harvestCounts`, the pending command list |
| `RunLog` | `MainProject/Assets/RunLog.cs` | the ordered list of `RunLogEntry` for the current run |
| `StageEntry` / `QuestionData` / `ConditionData` / `FarmGoal` | `MainProject/Assets/StageDefinition.cs` | pure serializable stage data, parsed from Inspector JSON |
| `StageResult` | same file | three stars plus their detail strings |

Two model traits are worth naming because they do real work:

- **`RunLog` is a classic observable model.** A static store with `Logged` and `Cleared` events and
  a read-only `Entries` list. It knows nothing about who displays it. Two unrelated views
  subscribe: `RunLogPanel` (the results page) and `MonacoBridge` (red squiggles in the editor).
  Adding a third consumer needs no change to the model. This is the purest MVC relationship in the
  codebase.
- **The farm keeps two parallel models: projected and actual.** `projectedPlots` is the state
  *after every queued command*, computed while the script is still being validated. `plots` is the
  state *currently on screen*, advanced one step at a time by the playback coroutine. That
  separation lets grading finish before the animation does, and it is why `StageUIController` can
  hold a result and release it on `PlaybackFinished`.

**Mixed responsibility to be honest about:** `FarmBridgeManager` is a `MonoBehaviour` that also owns
the transforms, instantiates crop copies, and runs the tween coroutine. It is model **and** the
farm's view. Splitting it (a plain `FarmState` class plus a `FarmRenderer`) is the single biggest
MVC cleanup available in the project.

---

## 2. The View — HTML pages and the Unity scene

### The web views

Four pages in `StreamingAssets`, each a dumb renderer exposing a small function vocabulary that C#
calls:

| Page | Functions it exposes | Messages it sends back |
|---|---|---|
| `index.html` (Monaco editor) | `unityKey`, `unityCopy`, `unityPaste`, `markError` | `run-python?code=…`, `clipboard?text=…` |
| `Question/question.html` | `loadStageData(title, html)` | — |
| `Condition/condition.html` | `setSamples(in, out, commands)`, `setInvalid(msg)` | — |
| `results.html` | `clearLog`, `appendLog` | — |
| `Result/stage_result.html` | `setStageResult(s1, s2, s3)` | `next-stage`, `retry-stage` |

None of them contains a rule. `stage_result.html` does not know when a star is earned, only how a
filled star looks; `condition.html` does not know what makes a sample valid, only how to grey out an
error line. Grading logic lives entirely on the C# side, which is exactly the MVC contract.

### The transport that makes the view replaceable

`WebViewController.cs` is infrastructure, not a controller in the MVC sense despite the name. It
wraps `WebViewObject` and offers `EvaluateJS` (C# → page), `MessageReceived` (page → C#),
`PageLoaded`, `ToJsString`, plus placement (`SetScreenMargins`, `worldAnchor`) and draw order.

`PageLoaded` deserves attention: every presenter re-pushes its state when the page loads, so a
reload never desynchronizes the view from the model. `RunLogPanel.OnPageLoaded` even replays the
whole `RunLog`. That is the idempotent-render discipline of a modern UI framework, reached by hand.

### The scene as a view

Cubes, tools and crop instances are the 3D view of the farm model. Their only state changes are
renderer/collider toggles and transforms. `WebViewTrigger`, `WebViewWindow` and its drag/resize
handles are window chrome: pure view behaviour with no model access.

---

## 3. The Controllers — three distinct kinds

### a. Input controllers (view → intent)

- `MonacoBridge.cs` parses `run-python?code=…` and calls `ExecuteSessionCode`. It also does a small
  view job (`markError`), so it is a two-way adapter.
- `WebViewKeyboardBridge.cs` translates Unity `Event`s into page commands, because the native
  webview only forwards printable characters.
- `StageRetryButton.cs` turns a 3D collider click into `StageUIController.RetryStage()`.

The last one is the clearest demonstration of the pattern paying off: a click on a cube in 3D space
and a click on an HTML button reach the same controller method through completely different
transports, and neither duplicates a line of reset logic.

### b. The orchestrating controller

`InteractivePythonManager.ExecuteSessionCode` is the transaction script for a run, in strict order:

1. `stageUI.OnRunStarted()` — clear view state before the model resets
2. `farm.ResetForRun()`, `RunLog.Clear()`
3. inject the stage's input variables into the Python session
4. run the player's code (step-capped) — Bridge calls only *validate and queue*
5. `farm.CommitRun()` — start playback, or report the first error
6. grade: farm goal in C#, then the Python pass for OOP and complexity

That ordering constraint ("before the farm reset: that reset ends any playback, which must not
release a result") is the kind of sequencing MVC usually hides; here it is explicit and commented,
which is worth keeping.

### c. The presentation controller

`StageUIController.cs` is the closest thing to a textbook controller: it holds no farm state, parses
stage JSON into models, pushes models into three views, and turns view events into state changes.

- **Model → view:** `PushQuestion`, `PushCondition`, `ApplyResult`
- **View → model:** `OnNextPressed`, `RetryStage`
- **Cross-triangle coordination:** `ShowResult` buffers a result while `farm.IsPlaying` and releases
  it on the static `PlaybackFinished` event.

`StageResultModal.cs` sits one level below as a **view-model / presenter**: it owns the popup's
geometry and visibility, translates `Show(s1, s2, s3)` into a `setStageResult` call, and re-raises
page messages as the C# events `NextPressed` and `RetryPressed`. It never decides whether Next
should advance a stage — `StageUIController` does, by checking star 1.

### d. The grader — a service, not a controller

`PythonASTEvaluator.cs` is a stateless domain service. `CheckFarmGoal` reads the farm model and
returns a verdict; the Python template checks structure and measures growth, then calls back into
`ReportEvaluation`, which produces a `StageResult` model and hands it to the controller. It touches
no view.

---

## 4. The message flow, end to end

```
Monaco page ──run-python──▶ MonacoBridge ──▶ InteractivePythonManager
                                                  │
                    Bridge.Plow/Plant/…  ◀────────┤ (Python, validate + queue)
                                                  │
                         FarmBridgeManager (model + playback view)
                                   │                        │
                            PlaybackFinished          RunLog.Logged
                                   │                    │        │
                        StageUIController        RunLogPanel   MonacoBridge
                                   │                 │             │
                       StageResultModal        results.html   markError
                                   │
                          stage_result.html
```

No arrow runs from a page straight into the model. Every inbound path passes through a controller,
and every outbound path is either an `EvaluateJS` push or a model event. That invariant is what
makes the whole thing testable from outside Unity — the star evaluator was verified with a fake
`OOPIn` module and no Editor at all.

---

## 5. Where the pattern holds, and where it leaks

**Holds well**

- `RunLog` as an observable model with several independent views.
- Grading kept out of every page.
- Two input transports converging on one controller method.
- `PageLoaded` re-push, making views stateless and reloadable.
- Stage content as data (Inspector JSON) rather than code.

**Leaks worth knowing about**

1. **`FarmBridgeManager` is model + view + animation controller** in one ~700-line class. The
   natural split is `FarmState` (plots, projections, harvest counts, validation) and `FarmView`
   (visibility, crop spawning, tool tweening, the coroutine).
2. **Static singletons everywhere** (`Instance`, static `RunLog`, static `Bridge`, static
   `PlaybackFinished`). Pragmatic — Python can only reach static C# members — but it makes
   controllers globally reachable and event lifetimes easy to get wrong. `Bridge` is essentially a
   static façade adapting an untyped scripting host to the model.
3. **`StageUIController` does three jobs:** JSON parsing, stage progression, and view pushing. If
   stages grow, `StageRepository` (parse and validate) and `StageProgress` (current index, stars
   earned) would lift out cleanly.
4. **The grading verdict is split across two languages** — the farm check in C#, OOP and complexity
   in Python — and reassembled in `ReportEvaluation`. Defensible, since only Python can introspect
   Python, but one logical model object has two birthplaces.
5. **`MonacoBridge` is a bidirectional adapter,** both controller (parse Run) and view (push
   squiggles). Small enough to leave, worth naming.

---

## 6. The one-sentence version

The model is the farm state plus the stage data and run log; the view is four HTML pages and the 3D
scene, neither of which contains a rule; and the controllers are a thin layer of adapters
(`MonacoBridge`, `WebViewKeyboardBridge`, `StageRetryButton`) feeding two coordinators
(`InteractivePythonManager` for a run, `StageUIController` for stage presentation), with
`PythonASTEvaluator` as a stateless grading service between them — and the main deviation from the
pattern is `FarmBridgeManager`, which is model and view at once.
