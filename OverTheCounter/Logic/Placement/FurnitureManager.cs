using System.Collections.Generic;
using OverTheCounter.Utilities;
using UnityEngine;

namespace OverTheCounter.Logic.Placement
{
    internal struct FurnitureSlot
    {
        public string SlotId;
        public string DefaultMeshId;
        public Vector3 LocalPosition;
        public Vector3 EulerAngles;
        public string[] MaterialOverrides;
    }

    internal static class FurnitureManager
    {
        private static readonly Dictionary<string, Dictionary<string, GameObject>> _spawned = new();

        internal static void SpawnFurniture(string buildingId, Transform root, FurnitureSlot[] slots)
        {
            MeshVault.MeshVaultAPI.Init();

            if (!_spawned.ContainsKey(buildingId))
                _spawned[buildingId] = new Dictionary<string, GameObject>();
            var dict = _spawned[buildingId];

            foreach (var slot in slots)
            {
                if (dict.ContainsKey(slot.SlotId)) continue;

                var worldPos = root.TransformPoint(slot.LocalPosition);
                var worldRot = root.rotation * Quaternion.Euler(slot.EulerAngles);

                var go = MeshVault.MeshVaultAPI.Spawn(slot.DefaultMeshId, worldPos, worldRot, parent: root, materialOverrides: slot.MaterialOverrides);
                if (go != null)
                {
                    dict[slot.SlotId] = go;
                    OTCLog.Msg(OTCLog.Systems.Furniture,
                        $"[{buildingId}] Placed '{slot.DefaultMeshId}' at slot '{slot.SlotId}'");
                }
                else
                {
                    OTCLog.Warning(OTCLog.Systems.Furniture,
                        $"[{buildingId}] Failed to spawn '{slot.DefaultMeshId}' at slot '{slot.SlotId}'");
                }
            }
        }

        internal static bool SwapFurniture(string buildingId, Transform root,
            FurnitureSlot[] slots, string slotId, string newMeshId)
        {
            if (!_spawned.TryGetValue(buildingId, out var dict))
                return false;

            FurnitureSlot? targetSlot = null;
            foreach (var slot in slots)
            {
                if (slot.SlotId == slotId)
                {
                    targetSlot = slot;
                    break;
                }
            }
            if (targetSlot == null) return false;

            if (dict.TryGetValue(slotId, out var oldGo) && oldGo != null)
                UnityEngine.Object.Destroy(oldGo);

            var ts = targetSlot.Value;
            var worldPos = root.TransformPoint(ts.LocalPosition);
            var worldRot = root.rotation * Quaternion.Euler(ts.EulerAngles);
            var newGo = MeshVault.MeshVaultAPI.Spawn(newMeshId, worldPos, worldRot, parent: root, materialOverrides: ts.MaterialOverrides);

            if (newGo != null)
            {
                dict[slotId] = newGo;
                OTCLog.Msg(OTCLog.Systems.Furniture,
                    $"[{buildingId}] Swapped slot '{slotId}' to '{newMeshId}'");
                return true;
            }

            dict.Remove(slotId);
            return false;
        }

        internal static void CleanupFurniture(string buildingId)
        {
            if (!_spawned.TryGetValue(buildingId, out var dict))
                return;
            foreach (var go in dict.Values)
                if (go != null) UnityEngine.Object.Destroy(go);
            dict.Clear();
            _spawned.Remove(buildingId);
        }
    }
}
