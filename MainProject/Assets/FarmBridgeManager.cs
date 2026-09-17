using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OOPIn
{
    /// <summary>
    /// Python-facing entry point. Python (pythonnet) can only import namespaced types. The
    /// session bootstrap wraps this so each call also passes the editor line it came from.
    /// </summary>
    public static class Bridge
    {
        public static bool Plow(int gridX, int gridZ, int line) { return Run(m => m.Enqueue(FarmAction.Plow, gridX, gridZ, null, line)); }
        public static bool Remove(int gridX, int gridZ, int line) { return Run(m => m.Enqueue(FarmAction.Remove, gridX, gridZ, null, line)); }
        public static bool Plant(int gridX, int gridZ, string plantName, int line) { return Run(m => m.Enqueue(FarmAction.Plant, gridX, gridZ, plantName, line)); }
        public static bool Harvest(int gridX, int gridZ, int line) { return Run(m => m.Enqueue(FarmAction.Harvest, gridX, gridZ, null, line)); }

        /// <summary>True once the current run has raised a Python error. Cleared per run.</summary>
        public static bool RunHadError { get; set; }

        /// <summary>Called by the session runner when submitted code raises.</summary>
        public static void ReportError(int line, string errorType, string message)
        {
            RunHadError = true;
            var entry = new RunLogEntry()
            {
                line = line,
                ok = false,
                pythonError = true,
                text = (line > 0 ? "Line " + line + ": " : "") + errorType + ": " + message
            };
            if (FarmBridgeManager.Instance != null) FarmBridgeManager.Instance.ReportFailure(entry);
            else RunLog.Add(entry);
        }

        private static bool Run(System.Func<FarmBridgeManager, bool> action)
        {
            if (FarmBridgeManager.Instance == null)
            {
                Debug.LogError("[Bridge] No FarmBridgeManager in the scene.");
                return false;
            }
            return action(FarmBridgeManager.Instance);
        }
    }

    public enum FarmAction { Plow, Remove, Plant, Harvest }

    public class FarmBridgeManager : MonoBehaviour
    {
        public static FarmBridgeManager Instance { get; private set; }

        [Header("Grid (found by name under Game when empty)")]
        [Tooltip("Parent of the grid cubes, e.g. 'Game/Grid Layout (1)'.")]
        public Transform gridRoot;
        [Tooltip("Must match the Flexalon grid's column count. Cells are read row by row in " +
                 "child order: index = gridZ * columns + gridX.")]
        public int columns = 5;

        [Header("Tools (found by name under Game/Tools when empty)")]
        public Transform hoe;
        public Transform shovel;
        public Transform sickle;

        [Header("Plants (found at Game/Plants/Grid Layout (1) when empty)")]
        [Tooltip("Each child is a plant template, named by its model (Carrot, Cabbage_01, ...). " +
                 "Templates are hidden at start and copied onto plots.")]
        public Transform plantsRoot;
        [Tooltip("Planted copy width as a fraction of its cube's width.")]
        [Range(0.1f, 1f)] public float plantFootprint = 0.6f;
        [Tooltip("Tallest a planted copy may be, as a multiple of its cube's height.")]
        public float plantMaxHeight = 1.5f;
        [Tooltip("Gap between the cube's top surface and the plant's base.")]
        public float plantGap = 0f;
        [Tooltip("Rotation of planted copies in world space, for models authored lying down.")]
        public Vector3 plantEulerOffset;

        [Header("Motion")]
        [Tooltip("Space between a tool's lowest point and the tallest possible plant.")]
        public float toolClearance = 0.5f;
        [Tooltip("Seconds for a tool to travel to its target. 0 = teleport.")]
        public float moveDuration = 0.35f;
        [Tooltip("Pause after each command before the next one starts.")]
        public float dwellSeconds = 0.2f;

        private enum PlotState { Empty, Plowed, Planted }

        private struct Command
        {
            public FarmAction action;
            public int index, x, z, line;
            public string plant;
        }

        // Cubes are hidden by disabling renderers/colliders, never SetActive(false): Flexalon
        // skips inactive children and reassigns cells in child order, so deactivating one
        // would slide every later cube into its cell.
        private PlotState[] plots;
        private string[] plotPlant;
        private GameObject[] plotPlantObjects;

        // State once the queue drains, so a whole script validates in order before it plays.
        private PlotState[] projectedPlots;
        private string[] projectedPlant;

        private readonly Dictionary<string, Transform> plantTemplates = new Dictionary<string, Transform>();
        private readonly Dictionary<string, int> harvestCounts = new Dictionary<string, int>();

        // A run is checked in full before anything plays: Bridge calls only validate and
        // collect, and CommitRun() plays the list once the script has finished without error.
        private readonly List<Command> pending = new List<Command>();
        private bool runFailed;
        private RunLogEntry runError;
        private Coroutine runner;
        // Set before StartCoroutine: a run with nothing to wait on can finish inside that call.
        private bool playing;

        /// <summary>True while tool/plant animations of the current run are still playing.</summary>
        public bool IsPlaying { get { return playing; } }

        /// <summary>True when the last run stopped on an error (Python or rejected command).</summary>
        public bool RunFailed { get { return runFailed; } }

        /// <summary>Raised when a run's animations finish, are skipped, or are interrupted.</summary>
        public static event System.Action PlaybackFinished;

        private Transform[] tools;
        private Vector3[] toolHomePositions;
        private Quaternion[] toolHomeRotations;
        private WaitForSeconds dwell;

        private Transform plantedCrops;
        private bool placementLogged;
        private bool placementWarned;

        /// <summary>Plant names accepted by Bridge.Plant, sorted.</summary>
        public IEnumerable<string> PlantNames { get { return plantTemplates.Keys.OrderBy(k => k); } }

        private int Rows { get { return gridRoot == null || columns <= 0 ? 0 : gridRoot.childCount / columns; } }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[FarmBridgeManager] Duplicate instance on '" + name + "' ignored.", this);
                enabled = false;
                return;
            }
            Instance = this;

            // Two objects are named "Grid Layout (1)" (cubes and plants), so the plants one is
            // excluded by parent and found by path instead.
            if (gridRoot == null) gridRoot = FindByName(t => t.parent == null || t.parent.name != "Plants", "Grid Layout (1)", "Grid_Layout");
            if (plantsRoot == null)
            {
                var plants = FindByName(null, "Plants");
                if (plants != null) plantsRoot = plants.Find("Grid Layout (1)");
            }
            if (hoe == null) hoe = FindByName(null, "Hoe");
            if (shovel == null) shovel = FindByName(null, "Shovel", "Spade");
            if (sickle == null) sickle = FindByName(null, "Sickle");

            if (gridRoot == null) Debug.LogError("[FarmBridgeManager] Grid root not found - assign it.", this);
            if (plantsRoot == null) Debug.LogError("[FarmBridgeManager] Plants root not found - assign Game/Plants/Grid Layout (1).", this);
            if (hoe == null || shovel == null || sickle == null)
                Debug.LogError("[FarmBridgeManager] One or more tools not found - assign Hoe/Shovel/Sickle.", this);

            tools = new[] { hoe, shovel, sickle };
            dwell = new WaitForSeconds(dwellSeconds);
            toolHomePositions = new Vector3[tools.Length];
            toolHomeRotations = new Quaternion[tools.Length];
            for (int i = 0; i < tools.Length; i++)
            {
                if (tools[i] == null) continue;
                toolHomePositions[i] = tools[i].position;
                toolHomeRotations[i] = tools[i].rotation;
            }
        }

        private void Start()
        {
            if (plantsRoot != null)
            {
                foreach (Transform child in plantsRoot)
                {
                    var key = BaseName(child.name);
                    if (!plantTemplates.ContainsKey(key)) plantTemplates.Add(key, child);
                    SetVisible(child, false);
                }
            }

            if (gridRoot == null) return;

            int count = gridRoot.childCount;
            plots = new PlotState[count];
            plotPlant = new string[count];
            plotPlantObjects = new GameObject[count];
            projectedPlots = new PlotState[count];
            projectedPlant = new string[count];
            for (int i = 0; i < count; i++)
            {
                SetVisible(gridRoot.GetChild(i), false);
            }

            if (count > 0)
            {
                Debug.Log("[FarmBridgeManager] " + columns + "x" + Rows + " grid. Cell (0,0) at " +
                          gridRoot.GetChild(0).position + ", last cell at " + gridRoot.GetChild(count - 1).position +
                          ". Plants: " + string.Join(", ", PlantNames), this);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public bool Enqueue(FarmAction action, int gridX, int gridZ, string plantName, int line)
        {
            // After the first error the run is already stopped; later calls change nothing.
            if (plots == null || runFailed) return false;

            string plot = "Plot " + gridX + "," + gridZ;
            if (gridX < 0 || gridX >= columns || gridZ < 0 || gridZ >= Rows)
            {
                return Fail(line, plot + " does not exist (grid is " + columns + "x" + Rows + ").");
            }

            int index = gridZ * columns + gridX;
            var state = projectedPlots[index];
            string planted = projectedPlant[index];

            switch (action)
            {
                case FarmAction.Plow:
                    if (state == PlotState.Plowed) return Fail(line, plot + " failed to plow: already plowed.");
                    projectedPlots[index] = PlotState.Plowed;
                    projectedPlant[index] = null;
                    break;

                case FarmAction.Remove:
                    if (state == PlotState.Empty) return Fail(line, plot + " failed to remove: plot is not plowed.");
                    projectedPlots[index] = PlotState.Empty;
                    projectedPlant[index] = null;
                    break;

                case FarmAction.Plant:
                    string key = ResolvePlant(plantName);
                    string shown = string.IsNullOrEmpty(plantName) ? "(no name)" : plantName;
                    if (key == null)
                        return Fail(line, plot + " failed to plant " + shown + ": unknown plant. Available: " + string.Join(", ", PlantNames));
                    if (state == PlotState.Empty) return Fail(line, plot + " failed to plant " + key + " in empty plot.");
                    if (state == PlotState.Planted) return Fail(line, plot + " failed to plant " + key + ": " + planted + " is already planted.");
                    projectedPlots[index] = PlotState.Planted;
                    projectedPlant[index] = key;
                    plantName = key;
                    break;

                case FarmAction.Harvest:
                    if (state == PlotState.Empty) return Fail(line, plot + " failed to harvest: plot is not plowed.");
                    if (state == PlotState.Plowed) return Fail(line, plot + " failed to harvest: nothing planted.");
                    projectedPlots[index] = PlotState.Plowed;
                    projectedPlant[index] = null;
                    break;
            }

            Push(new Command { action = action, index = index, x = gridX, z = gridZ, line = line, plant = plantName });
            return true;
        }

        /// <summary>
        /// Records a Python error. The run stops: nothing already collected will play.
        /// Only the first error of a run is kept.
        /// </summary>
        public void ReportFailure(RunLogEntry entry)
        {
            if (runFailed) return;
            runFailed = true;
            runError = entry;
        }

        /// <summary>
        /// Called once the submitted script has finished executing. On error, logs the lines
        /// checked before it plus the error and plays nothing; otherwise plays every command.
        /// </summary>
        public void CommitRun()
        {
            if (runFailed)
            {
                foreach (var cmd in pending)
                {
                    RunLog.Add(new RunLogEntry { line = cmd.line, ok = true, text = CheckedText(cmd) });
                }
                RunLog.Add(runError);
                RunLog.Add(new RunLogEntry
                {
                    line = runError.line,
                    ok = false,
                    text = "Run stopped" + (runError.line > 0 ? " at line " + runError.line : "") +
                           ": no commands were executed."
                });
                pending.Clear();
                RaisePlaybackFinished();
                return;
            }

            if (pending.Count == 0)
            {
                RaisePlaybackFinished();
                return;
            }
            var commands = new List<Command>(pending);
            pending.Clear();
            playing = true;
            var started = StartCoroutine(PlayRun(commands));
            if (playing) runner = started;
        }

        private void RaisePlaybackFinished()
        {
            playing = false;
            if (PlaybackFinished != null) PlaybackFinished();
        }

        /// <summary>
        /// Called before each new code submission: stops any run in progress, returns every
        /// tool home and visible, hides every cube, destroys planted crops and zeroes counts.
        /// </summary>
        public void ResetForRun()
        {
            pending.Clear();
            runFailed = false;
            if (runner != null)
            {
                StopCoroutine(runner);
                runner = null;
            }
            if (playing) RaisePlaybackFinished();

            for (int i = 0; i < tools.Length; i++)
            {
                if (tools[i] == null) continue;
                tools[i].SetPositionAndRotation(toolHomePositions[i], toolHomeRotations[i]);
                SetVisible(tools[i], true);
            }

            harvestCounts.Clear();
            if (plots == null) return;

            for (int i = 0; i < plots.Length; i++)
            {
                ClearPlant(i);
                SetVisible(gridRoot.GetChild(i), false);
                plots[i] = PlotState.Empty;
                projectedPlots[i] = PlotState.Empty;
                projectedPlant[i] = null;
            }
        }

        private bool Fail(int line, string text)
        {
            ReportFailure(new RunLogEntry { line = line, ok = false, text = text });
            return false;
        }

        private void Push(Command cmd)
        {
            pending.Add(cmd);
        }

        private static string CheckedText(Command cmd)
        {
            string plot = "Plot " + cmd.x + "," + cmd.z;
            switch (cmd.action)
            {
                case FarmAction.Plow: return plot + " plow checked.";
                case FarmAction.Remove: return plot + " remove checked.";
                case FarmAction.Plant: return plot + " plant " + cmd.plant + " checked.";
                default: return plot + " harvest checked.";
            }
        }

        private IEnumerator PlayRun(List<Command> commands)
        {
            Transform activeTool = null;
            int activeCell = -1;

            foreach (var cmd in commands)
            {
                var cube = gridRoot.GetChild(cmd.index);
                var tool = ToolFor(cmd.action);
                var target = tool != null ? ToolTarget(tool, cube) : Vector3.zero;

                if (tool != activeTool)
                {
                    // Only one tool is ever visible: the previous one disappears.
                    if (activeTool != null) SetVisible(activeTool, false);
                    if (tool != null)
                    {
                        SetVisible(tool, true);
                        // Same cube: swap in place, so it reads as changing tools.
                        if (cmd.index == activeCell) tool.position = target;
                        else yield return MoveTool(tool, target);
                    }
                    activeTool = tool;
                }
                else if (cmd.index != activeCell)
                {
                    // Same tool, different cube.
                    yield return MoveTool(tool, target);
                }
                activeCell = cmd.index;

                RunLog.Add(new RunLogEntry { line = cmd.line, ok = true, text = Apply(cmd, cube) });

                if (dwellSeconds > 0f) yield return dwell;
            }
            runner = null;
            RaisePlaybackFinished();
        }

        private string Apply(Command cmd, Transform cube)
        {
            string plot = "Plot " + cmd.x + "," + cmd.z;
            string cleared = plotPlant[cmd.index];

            switch (cmd.action)
            {
                case FarmAction.Plow:
                    ClearPlant(cmd.index);
                    SetVisible(cube, true);
                    plots[cmd.index] = PlotState.Plowed;
                    return plot + " plowed." + (cleared != null ? " " + cleared + " cleared." : "");

                case FarmAction.Remove:
                    ClearPlant(cmd.index);
                    SetVisible(cube, false);
                    plots[cmd.index] = PlotState.Empty;
                    return plot + " removed." + (cleared != null ? " " + cleared + " cleared." : "");

                case FarmAction.Plant:
                    SpawnPlant(cmd.index, cmd.plant, cube);
                    plots[cmd.index] = PlotState.Planted;
                    return plot + " " + cmd.plant + " planted.";

                default: // Harvest
                    ClearPlant(cmd.index);
                    plots[cmd.index] = PlotState.Plowed;
                    int count;
                    harvestCounts.TryGetValue(cleared, out count);
                    harvestCounts[cleared] = ++count;
                    return plot + " " + cleared + " harvested. Current count: " + count + " " + cleared;
            }
        }

        private void SpawnPlant(int index, string key, Transform cube)
        {
            Transform template;
            if (!plantTemplates.TryGetValue(key, out template)) return;

            if (plantedCrops == null)
            {
                // Scene root, not under this object: Tools is a Flexalon layout and would
                // treat the container as one more cell to arrange.
                plantedCrops = new GameObject("Planted Crops").transform;
            }

            var copy = Instantiate(template.gameObject, plantedCrops);
            copy.name = key + " (plot " + index + ")";
            // The template is laid out by Flexalon; the copy must not be.
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb != null && mb.GetType().Namespace == "Flexalon") Destroy(mb);
            }
            SetVisible(copy.transform, true);

            // The template's transform belongs to the tilted, Flexalon-scaled shelf, so
            // place the copy by measured bounds instead: upright, fitted, resting on top.
            var t = copy.transform;
            t.rotation = Quaternion.Euler(plantEulerOffset);
            SetWorldScale(t, 1f);

            Bounds cubeBounds, plantBounds;
            if (!TryGetBounds(cube, out cubeBounds) || !TryGetBounds(t, out plantBounds)
                || plantBounds.size.x <= 0f || plantBounds.size.z <= 0f || plantBounds.size.y <= 0f)
            {
                t.position = cube.position + Vector3.up;
                if (!placementWarned)
                {
                    placementWarned = true;
                    Debug.LogWarning("[FarmBridgeManager] Could not measure bounds for '" + key +
                                     "' or its cube; placed at the cube pivot instead.", this);
                }
            }
            else
            {
                float fit = plantFootprint * Mathf.Min(cubeBounds.size.x / plantBounds.size.x,
                                                       cubeBounds.size.z / plantBounds.size.z);
                float heightCap = plantMaxHeight * cubeBounds.size.y / plantBounds.size.y;
                SetWorldScale(t, Mathf.Min(fit, heightCap));

                TryGetBounds(t, out plantBounds);
                var offset = new Vector3(cubeBounds.center.x - plantBounds.center.x,
                                         cubeBounds.max.y + plantGap - plantBounds.min.y,
                                         cubeBounds.center.z - plantBounds.center.z);
                t.position += offset;

                if (!placementLogged)
                {
                    placementLogged = true;
                    TryGetBounds(t, out plantBounds);
                    Debug.Log("[FarmBridgeManager] First crop '" + copy.name + "': position " + t.position +
                              ", scale " + t.localScale + ", bounds " + plantBounds + ". Cube bounds " + cubeBounds +
                              ", plant ceiling Y " + PlantCeilingY(cubeBounds) + ".", this);
                }
            }

            plotPlant[index] = key;
            plotPlantObjects[index] = copy;
        }

        /// <summary>Highest point any fitted plant can reach on a cube with these bounds.</summary>
        private float PlantCeilingY(Bounds cubeBounds)
        {
            return cubeBounds.max.y + plantGap + plantMaxHeight * cubeBounds.size.y;
        }

        /// <summary>
        /// Hover point for a tool over a cube: its lowest point sits toolClearance above the
        /// tallest plant that could ever grow there, so no tool overlaps any plant.
        /// </summary>
        private Vector3 ToolTarget(Transform tool, Transform cube)
        {
            Bounds cubeBounds;
            if (!TryGetBounds(cube, out cubeBounds))
            {
                return cube.position + Vector3.up * (plantMaxHeight + toolClearance + 1f);
            }

            float bottomOffset = 0f;
            Bounds toolBounds;
            if (TryGetBounds(tool, out toolBounds))
            {
                bottomOffset = tool.position.y - toolBounds.min.y;
            }

            float y = PlantCeilingY(cubeBounds) + toolClearance + bottomOffset;
            // Centre the tool's bounds, not its pivot, over the cube.
            float dx = tool.position.x - toolBounds.center.x;
            float dz = tool.position.z - toolBounds.center.z;
            return new Vector3(cubeBounds.center.x + dx, y, cubeBounds.center.z + dz);
        }

        // Renderer.bounds stays valid while a renderer is disabled, so hidden cubes/tools measure too.
        private static bool TryGetBounds(Transform target, out Bounds bounds)
        {
            bounds = new Bounds();
            bool found = false;
            foreach (var r in target.GetComponentsInChildren<Renderer>(true))
            {
                if (!found) { bounds = r.bounds; found = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return found;
        }

        private static void SetWorldScale(Transform t, float uniform)
        {
            var parentScale = t.parent != null ? t.parent.lossyScale : Vector3.one;
            t.localScale = new Vector3(uniform / parentScale.x, uniform / parentScale.y, uniform / parentScale.z);
        }

        private void ClearPlant(int index)
        {
            if (plotPlantObjects[index] != null) Destroy(plotPlantObjects[index]);
            plotPlantObjects[index] = null;
            plotPlant[index] = null;
        }

        /// <summary>Case-insensitive; "cabbage" also matches "Cabbage_01".</summary>
        private string ResolvePlant(string plantName)
        {
            if (string.IsNullOrEmpty(plantName)) return null;
            var wanted = BaseName(plantName);
            foreach (var key in plantTemplates.Keys)
            {
                if (string.Equals(key, wanted, System.StringComparison.OrdinalIgnoreCase)) return key;
            }
            return null;
        }

        /// <summary>"Cabbage_01" -> "Cabbage", "Carrot (1)" -> "Carrot".</summary>
        private static string BaseName(string raw)
        {
            var s = raw.Trim();
            int paren = s.IndexOf(" (", System.StringComparison.Ordinal);
            if (paren > 0) s = s.Substring(0, paren);
            int underscore = s.IndexOf('_');
            if (underscore > 0) s = s.Substring(0, underscore);
            return s;
        }

        private Transform ToolFor(FarmAction action)
        {
            switch (action)
            {
                case FarmAction.Plant: return shovel;
                case FarmAction.Harvest: return sickle;
                default: return hoe;
            }
        }

        private static void SetVisible(Transform target, bool visible)
        {
            foreach (var r in target.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
            foreach (var c in target.GetComponentsInChildren<Collider>(true)) c.enabled = visible;
        }

        private IEnumerator MoveTool(Transform tool, Vector3 destination)
        {
            if (tool == null) yield break;

            if (moveDuration <= 0f)
            {
                tool.position = destination;
                yield break;
            }

            Vector3 start = tool.position;
            for (float t = 0f; t < moveDuration; t += Time.deltaTime)
            {
                tool.position = Vector3.Lerp(start, destination, Mathf.SmoothStep(0f, 1f, t / moveDuration));
                yield return null;
            }
            tool.position = destination;
        }

        private static Transform FindByName(System.Func<Transform, bool> filter, params string[] names)
        {
#if UNITY_2023_1_OR_NEWER
            var all = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var all = Resources.FindObjectsOfTypeAll<Transform>();
#endif
            foreach (var n in names)
            {
                foreach (var t in all)
                {
                    if (t.name == n && t.gameObject.scene.IsValid() && (filter == null || filter(t))) return t;
                }
            }
            return null;
        }
    }
}
