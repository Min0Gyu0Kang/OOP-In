using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace OOPIn
{
    /// <summary>
    /// Python-facing entry point. Python (pythonnet) can only import namespaced types, so
    /// editor code reaches this as <c>from OOPIn import Bridge</c>.
    /// </summary>
    public static class Bridge
    {
        public static bool Plow(int gridX, int gridZ) { return Run(m => m.Enqueue(FarmAction.Plow, gridX, gridZ)); }
        public static bool Remove(int gridX, int gridZ) { return Run(m => m.Enqueue(FarmAction.Remove, gridX, gridZ)); }
        public static bool Plant(int gridX, int gridZ) { return Run(m => m.Enqueue(FarmAction.Plant, gridX, gridZ)); }
        public static bool Harvest(int gridX, int gridZ) { return Run(m => m.Enqueue(FarmAction.Harvest, gridX, gridZ)); }

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

        [Header("Motion")]
        public float heightOffset = 5f;
        [Tooltip("Seconds for a tool to travel to its target. 0 = teleport.")]
        public float moveDuration = 0.35f;
        [Tooltip("Pause after each command before the next one starts.")]
        public float dwellSeconds = 0.2f;

        private struct Command
        {
            public FarmAction action;
            public int index;
        }

        // Cubes are hidden by disabling renderers/colliders, never SetActive(false): Flexalon
        // skips inactive children and reassigns cells in child order, so deactivating one
        // would slide every later cube into its cell.
        private bool[] visibleCells;
        // What visibility will be once the queue drains - lets Plow(1,1); Plant(1,1) in one
        // submission validate before the plow has actually played.
        private bool[] projectedVisible;

        private readonly Queue<Command> queue = new Queue<Command>();
        private Coroutine runner;

        private Vector3[] toolHomePositions;
        private Quaternion[] toolHomeRotations;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[FarmBridgeManager] Duplicate instance on '" + name + "' ignored.", this);
                enabled = false;
                return;
            }
            Instance = this;

            if (gridRoot == null) gridRoot = FindByName("Grid Layout (1)", "Grid_Layout");
            if (hoe == null) hoe = FindByName("Hoe");
            if (shovel == null) shovel = FindByName("Shovel", "Spade");
            if (sickle == null) sickle = FindByName("Sickle");

            if (gridRoot == null) Debug.LogError("[FarmBridgeManager] Grid root not found - assign it.", this);
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
            if (gridRoot == null) return;

            int count = gridRoot.childCount;
            visibleCells = new bool[count];
            projectedVisible = new bool[count];
            for (int i = 0; i < count; i++)
            {
                SetCubeVisible(gridRoot.GetChild(i), false);
            }

            if (count > 0)
            {
                Debug.Log("[FarmBridgeManager] " + columns + "x" + (count / columns) + " grid. Cell (0,0) at " +
                          gridRoot.GetChild(0).position + ", last cell at " + gridRoot.GetChild(count - 1).position +
                          ". gridX runs along columns, gridZ along rows.", this);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public bool Enqueue(FarmAction action, int gridX, int gridZ)
        {
            int index;
            if (!TryGetIndex(gridX, gridZ, out index)) return false;

            switch (action)
            {
                case FarmAction.Plow:
                    projectedVisible[index] = true;
                    break;
                case FarmAction.Remove:
                    if (!projectedVisible[index])
                    {
                        Debug.LogWarning("[FarmBridgeManager] Cell (" + gridX + ", " + gridZ + ") has no cube to remove.", this);
                        return false;
                    }
                    projectedVisible[index] = false;
                    break;
                default:
                    if (!projectedVisible[index])
                    {
                        Debug.LogWarning("[FarmBridgeManager] Cell (" + gridX + ", " + gridZ + ") is not plowed - plow it first.", this);
                        return false;
                    }
                    break;
            }

            queue.Enqueue(new Command { action = action, index = index });
            if (runner == null)
            {
                runner = StartCoroutine(RunQueue());
            }
            return true;
        }

        /// <summary>
        /// Drops pending commands, returns every tool to where it started and hides every cube. Called before
        /// each new code submission.
        /// </summary>
        public void ResetTools()
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

            if (visibleCells == null) return;

            for (int i = 0; i < visibleCells.Length; i++)
            {
                SetCubeVisible(gridRoot.GetChild(i), false);
                projectedVisible[i] = false;
            }
        }

        private IEnumerator RunQueue()
        {
            while (queue.Count > 0)
            {
                var cmd = queue.Dequeue();
                var cube = gridRoot.GetChild(cmd.index);

                yield return MoveTool(ToolFor(cmd.action), cube.position);

                if (cmd.action == FarmAction.Plow) SetCubeVisible(cube, true);
                else if (cmd.action == FarmAction.Remove) SetCubeVisible(cube, false);

                if (dwellSeconds > 0f) yield return new WaitForSeconds(dwellSeconds);
            }
            runner = null;
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

        private void SetCubeVisible(Transform cube, bool visible)
        {
            foreach (var r in cube.GetComponentsInChildren<Renderer>(true)) r.enabled = visible;
            foreach (var c in cube.GetComponentsInChildren<Collider>(true)) c.enabled = visible;

            int index = cube.GetSiblingIndex();
            if (visibleCells != null && index < visibleCells.Length)
            {
                visibleCells[index] = visible;
            }
        }

        private bool TryGetIndex(int gridX, int gridZ, out int index)
        {
            index = -1;
            if (gridRoot == null || columns <= 0 || visibleCells == null) return false;

            int rows = gridRoot.childCount / columns;
            if (gridX < 0 || gridX >= columns || gridZ < 0 || gridZ >= rows)
            {
                Debug.LogError("[FarmBridgeManager] Cell (" + gridX + ", " + gridZ + ") is outside the " +
                               columns + "x" + rows + " grid.", this);
                return false;
            }

            index = gridZ * columns + gridX;
            return true;
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

        private static Transform FindByName(params string[] names)
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
                    if (t.name == n && t.gameObject.scene.IsValid()) return t;
                }
            }
            return null;
        }
    }
}
