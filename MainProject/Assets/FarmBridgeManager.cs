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

        /// <summary>Called by the session runner when submitted code raises.</summary>
        public static void ReportError(int line, string errorType, string message)
        {
            var entry = new RunLogEntry
            {
                line = line,
                ok = false,
                pythonError = true,
                text = (line > 0 ? "Line " + line + ": " : "") + errorType + ": " + message
            };
            if (FarmBridgeManager.Instance != null) FarmBridgeManager.Instance.EnqueueLog(entry);
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

    public enum FarmAction { Plow, Remove, Plant, Harvest, LogOnly }

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
        [Tooltip("Height of a planted copy above its cube.")]
        public float plantHeight = 1f;
        [Tooltip("Extra rotation applied to planted copies, if the models need it.")]
        public Vector3 plantEulerOffset;

        [Header("Motion")]
        public float heightOffset = 5f;
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
            public RunLogEntry log;
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

        private readonly Queue<Command> queue = new Queue<Command>();
        private Coroutine runner;

        private Vector3[] toolHomePositions;
        private Quaternion[] toolHomeRotations;

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

            var tools = Tools();
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
            if (plots == null) return false;

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

        /// <summary>Queues a log row so it appears in order with the commands around it.</summary>
        public void EnqueueLog(RunLogEntry entry)
        {
            Push(new Command { action = FarmAction.LogOnly, log = entry });
        }

        /// <summary>
        /// Called before each new code submission: drops pending commands, returns tools to
        /// where they started, hides every cube, destroys planted crops and zeroes harvest counts.
        /// </summary>
        public void ResetForRun()
        {
            queue.Clear();
            if (runner != null)
            {
                StopCoroutine(runner);
                runner = null;
            }

            var tools = Tools();
            for (int i = 0; i < tools.Length; i++)
            {
                if (tools[i] == null) continue;
                tools[i].SetPositionAndRotation(toolHomePositions[i], toolHomeRotations[i]);
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
            EnqueueLog(new RunLogEntry { line = line, ok = false, text = text });
            return false;
        }

        private void Push(Command cmd)
        {
            queue.Enqueue(cmd);
            if (runner == null) runner = StartCoroutine(RunQueue());
        }

        private IEnumerator RunQueue()
        {
            while (queue.Count > 0)
            {
                var cmd = queue.Dequeue();
                if (cmd.action == FarmAction.LogOnly)
                {
                    RunLog.Add(cmd.log);
                    continue;
                }

                var cube = gridRoot.GetChild(cmd.index);
                yield return MoveTool(ToolFor(cmd.action), cube.position);

                string plot = "Plot " + cmd.x + "," + cmd.z;
                string cleared = plotPlant[cmd.index];
                string text;

                switch (cmd.action)
                {
                    case FarmAction.Plow:
                        ClearPlant(cmd.index);
                        SetVisible(cube, true);
                        plots[cmd.index] = PlotState.Plowed;
                        text = plot + " plowed." + (cleared != null ? " " + cleared + " cleared." : "");
                        break;

                    case FarmAction.Remove:
                        ClearPlant(cmd.index);
                        SetVisible(cube, false);
                        plots[cmd.index] = PlotState.Empty;
                        text = plot + " removed." + (cleared != null ? " " + cleared + " cleared." : "");
                        break;

                    case FarmAction.Plant:
                        SpawnPlant(cmd.index, cmd.plant, cube.position);
                        plots[cmd.index] = PlotState.Planted;
                        text = plot + " " + cmd.plant + " planted.";
                        break;

                    default: // Harvest
                        ClearPlant(cmd.index);
                        plots[cmd.index] = PlotState.Plowed;
                        int count;
                        harvestCounts.TryGetValue(cleared, out count);
                        harvestCounts[cleared] = ++count;
                        text = plot + " " + cleared + " harvested. Current count: " + count + " " + cleared;
                        break;
                }

                RunLog.Add(new RunLogEntry { line = cmd.line, ok = true, text = text });

                if (dwellSeconds > 0f) yield return new WaitForSeconds(dwellSeconds);
            }
            runner = null;
        }

        private void SpawnPlant(int index, string key, Vector3 cubePosition)
        {
            Transform template;
            if (!plantTemplates.TryGetValue(key, out template)) return;

            var copy = Instantiate(template.gameObject);
            copy.name = key + " (plot " + index + ")";
            // The template is laid out by Flexalon; the copy must not be.
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb != null && mb.GetType().Namespace == "Flexalon") Destroy(mb);
            }
            copy.transform.SetParent(null, false);
            copy.transform.position = cubePosition + Vector3.up * plantHeight;
            copy.transform.rotation = Quaternion.Euler(plantEulerOffset) * template.localRotation;
            copy.transform.localScale = template.lossyScale;
            SetVisible(copy.transform, true);

            plotPlant[index] = key;
            plotPlantObjects[index] = copy;
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

        private IEnumerator MoveTool(Transform tool, Vector3 cubePosition)
        {
            if (tool == null) yield break;

            var destination = new Vector3(cubePosition.x, cubePosition.y + heightOffset, cubePosition.z);
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

        private Transform[] Tools()
        {
            return new[] { hoe, shovel, sickle };
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
