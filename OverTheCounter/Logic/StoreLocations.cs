using System;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheCounter.Logic
{
    public enum StoreType { GasMart, Hardware, NightMarket }

    public class StoreLocation
    {
        public StoreType Type;
        public Vector3 Position;
        public Quaternion Rotation;
        public string DisplayName;

        public StoreLocation(StoreType type, Vector3 position, float yRotation, string displayName)
        {
            Type = type;
            Position = position;
            Rotation = Quaternion.Euler(0f, yRotation, 0f);
            DisplayName = displayName;
        }
    }

    /// <summary>
    /// Static store coordinates for supply run destinations.
    /// Mirrors ManagerLocations.cs structure.
    /// </summary>
    public static class StoreLocations
    {
        private static readonly List<StoreLocation> _locations = new List<StoreLocation>
        {
            // Gas Mart 1 (south)
            new StoreLocation(StoreType.GasMart, new Vector3(-115.5f, -2.8f, 63.6f), 268f, "Gas Mart"),
            // Gas Mart 2 (central)
            new StoreLocation(StoreType.GasMart, new Vector3(17.7f, 1.2f, -3.3f), 340f, "Gas Mart"),
            // Hardware Store 1 (west)
            new StoreLocation(StoreType.Hardware, new Vector3(-17.7f, -2.7f, 138f), 87f, "Hardware Store"),
            // Hardware Store 2 (east)
            new StoreLocation(StoreType.Hardware, new Vector3(107.3f, 1.1f, 24.8f), 180f, "Hardware Store"),
            // Oscar's Store (outside warehouse wall — NPC can't pathfind inside)
            new StoreLocation(StoreType.NightMarket, new Vector3(-57.4f, -1.5f, 20.2f), 0f, "Oscar's Store"),
        };

        /// <summary>
        /// Gets the nearest store of a given type from a position.
        /// </summary>
        public static StoreLocation GetNearest(StoreType type, Vector3 from)
        {
            StoreLocation nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var loc in _locations)
            {
                if (loc.Type != type) continue;
                float dist = Vector3.Distance(from, loc.Position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = loc;
                }
            }

            return nearest;
        }

    }
}
