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
        public static bool Plow(int gridX, int gridZ) { return Run(m => m.Plow(gridX, gridZ)); }
        public static bool Remove(int gridX, int gridZ) { return Run(m => m.Remove(gridX, gridZ)); }
        public static bool Plant(int gridX, int gridZ) { return Run(m => m.Plant(gridX, gridZ)); }
        public static bool Harvest(int gridX, int gridZ) { return Run(m => m.Harvest(gridX, gridZ)); }

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

        // Positions of soft-deleted cubes, keyed by grid cell. Recorded before deactivation,
        // because an inactive child can be skipped by the layout and its transform can't be
        // trusted afterwards.
        private readonly Dictionary<Vector2Int, Vector3> removedCells = new Dictionary<Vector2Int, Vector3>();
        private readonly Dictionary<Transform, Coroutine> activeMoves = new Dictionary<Transform, Coroutine>();

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
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public bool Plow(int gridX, int gridZ)
        {
            Transform cube;
            if (!TryGetCell(gridX, gridZ, out cube)) return false;

            var cell = new Vector2Int(gridX, gridZ);
            Vector3 target = cube.position;
            if (!cube.gameObject.activeSelf)
            {
                Vector3 cached;
                if (removedCells.TryGetValue(cell, out cached)) target = cached;
                cube.gameObject.SetActive(true);
                removedCells.Remove(cell);
            }

            MoveTool(hoe, target);
            return true;
        }

        public bool Remove(int gridX, int gridZ)
        {
            Transform cube;
            if (!TryGetCell(gridX, gridZ, out cube)) return false;

            if (!cube.gameObject.activeSelf)
            {
                Debug.LogWarning("[FarmBridgeManager] Cell (" + gridX + ", " + gridZ + ") is already removed.", this);
                return false;
            }

            var cell = new Vector2Int(gridX, gridZ);
            removedCells[cell] = cube.position;
            MoveTool(hoe, cube.position);
            cube.gameObject.SetActive(false);
            return true;
        }

        public bool Plant(int gridX, int gridZ)
        {
            return MoveToolOverCell(shovel, gridX, gridZ);
        }

        public bool Harvest(int gridX, int gridZ)
        {
            return MoveToolOverCell(sickle, gridX, gridZ);
        }

        private bool MoveToolOverCell(Transform tool, int gridX, int gridZ)
        {
            Transform cube;
            if (!TryGetCell(gridX, gridZ, out cube)) return false;

            if (!cube.gameObject.activeSelf)
            {
                Debug.LogWarning("[FarmBridgeManager] Cell (" + gridX + ", " + gridZ + ") is removed - plow it first.", this);
                return false;
            }

            MoveTool(tool, cube.position);
            return true;
        }

        private bool TryGetCell(int gridX, int gridZ, out Transform cube)
        {
            cube = null;
            if (gridRoot == null || columns <= 0) return false;

            int rows = gridRoot.childCount / columns;
            if (gridX < 0 || gridX >= columns || gridZ < 0 || gridZ >= rows)
            {
                Debug.LogError("[FarmBridgeManager] Cell (" + gridX + ", " + gridZ + ") is outside the " +
                               columns + "x" + rows + " grid.", this);
                return false;
            }

            // GetChild includes inactive children, so a soft-deleted cube keeps its index.
            cube = gridRoot.GetChild(gridZ * columns + gridX);
            return true;
        }

        private void MoveTool(Transform tool, Vector3 cubePosition)
        {
            if (tool == null) return;

            var destination = new Vector3(cubePosition.x, cubePosition.y + heightOffset, cubePosition.z);

            Coroutine running;
            if (activeMoves.TryGetValue(tool, out running) && running != null)
            {
                StopCoroutine(running);
            }

            if (moveDuration <= 0f)
            {
                tool.position = destination;
                activeMoves.Remove(tool);
                return;
            }
            activeMoves[tool] = StartCoroutine(MoveRoutine(tool, destination));
        }

        private IEnumerator MoveRoutine(Transform tool, Vector3 destination)
        {
            Vector3 start = tool.position;
            for (float t = 0f; t < moveDuration; t += Time.deltaTime)
            {
                tool.position = Vector3.Lerp(start, destination, Mathf.SmoothStep(0f, 1f, t / moveDuration));
                yield return null;
            }
            tool.position = destination;
            activeMoves.Remove(tool);
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
