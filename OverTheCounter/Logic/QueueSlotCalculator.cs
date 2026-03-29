using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using S1MAPI.Building;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Computes ordered queue positions as a snake line from behind the checkout counter.
    /// Each slot is found by BFS from the previous slot (chained), so the line
    /// follows a continuous path through walkable cells.
    /// </summary>
    internal static class QueueSlotCalculator
    {
        private const int MaxSlots = 10;
        private const float MinSpacing = 1.0f;
        private const int BfsMaxVisited = 500;

        internal static List<Vector3> Compute(BuildingTarget target, CheckoutCounterInstance counter)
        {
            var results = new List<Vector3>();
            var nav = target?.NavBuilder;
            if (nav == null || !nav.IsBuilt || counter?.CounterPosition == null)
                return results;

            float cellSize = nav.CellSize;
            if (cellSize <= 0f) return results;

            // Start 1.0m behind counter in local coords
            var counterWorld = counter.CounterPosition.Value;
            var behindWorld = counterWorld - counter.CounterTransform.forward * 1.0f;
            var startLocal = nav.WorldToLocal(behindWorld);
            startLocal = SnapToGrid(startLocal, cellSize);

            if (!nav.IsWalkable(startLocal))
                startLocal = nav.NearestWalkableCell(startLocal);

            var usedCells = new HashSet<(int, int)>();
            usedCells.Add(GridKey(startLocal, cellSize));
            results.Add(startLocal);

            OTCLog.Msg(OTCLog.Systems.Customer, $"[Queue] cellSize={cellSize:F2} start={startLocal}");

            Vector3[] offsets =
            {
                new Vector3(0, 0, -cellSize),
                new Vector3(0, 0, cellSize),
                new Vector3(-cellSize, 0, 0),
                new Vector3(cellSize, 0, 0)
            };

            float minSpacingSqr = MinSpacing * MinSpacing;

            // Chain: each slot is found by BFS from the PREVIOUS slot
            while (results.Count < MaxSlots)
            {
                var next = FindNextSlot(results[results.Count - 1], usedCells, nav, cellSize, offsets, minSpacingSqr);
                if (!next.HasValue) break;
                usedCells.Add(GridKey(next.Value, cellSize));
                results.Add(next.Value);
                OTCLog.Msg(OTCLog.Systems.Customer, $"[Queue] slot {results.Count - 1}: {next.Value}");
            }

            OTCLog.Msg(OTCLog.Systems.Customer, $"[Queue] computed {results.Count} slots");
            return results;
        }

        /// <summary>
        /// BFS from 'from' to find the nearest walkable cell that is at least
        /// MinSpacing from the previous slot and not already used by another slot.
        /// </summary>
        private static Vector3? FindNextSlot(Vector3 from, HashSet<(int, int)> usedCells,
            NavigationBuilder nav, float cellSize, Vector3[] offsets, float minSpacingSqr)
        {
            var queue = new Queue<Vector3>();
            var visited = new HashSet<(int, int)>();

            queue.Enqueue(from);
            visited.Add(GridKey(from, cellSize));

            while (queue.Count > 0 && visited.Count < BfsMaxVisited)
            {
                var current = queue.Dequeue();
                var key = GridKey(current, cellSize);

                // Must not already be used by another slot
                if (!usedCells.Contains(key))
                {
                    // Only check distance from the PREVIOUS slot (from)
                    float dx = current.x - from.x;
                    float dz = current.z - from.z;
                    if (dx * dx + dz * dz >= minSpacingSqr)
                        return current;
                }

                // Explore neighbors
                for (int i = 0; i < offsets.Length; i++)
                {
                    var neighbor = SnapToGrid(current + offsets[i], cellSize);
                    var nkey = GridKey(neighbor, cellSize);
                    if (visited.Contains(nkey)) continue;
                    visited.Add(nkey);

                    if (!nav.IsWalkable(neighbor)) continue;

                    queue.Enqueue(neighbor);
                }
            }

            return null;
        }

        private static Vector3 SnapToGrid(Vector3 local, float cellSize)
        {
            int gx = Mathf.FloorToInt(local.x / cellSize);
            int gz = Mathf.FloorToInt(local.z / cellSize);
            return new Vector3((gx + 0.5f) * cellSize, 0f, (gz + 0.5f) * cellSize);
        }

        private static (int, int) GridKey(Vector3 local, float cellSize)
        {
            return (Mathf.FloorToInt(local.x / cellSize), Mathf.FloorToInt(local.z / cellSize));
        }
    }
}
