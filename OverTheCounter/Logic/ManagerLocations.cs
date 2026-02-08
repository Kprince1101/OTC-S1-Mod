using System;
using System.Collections.Generic;
using UnityEngine;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Static spawn and destination locations for Manager NPCs at each eligible business.
    /// Each business has one entry keyed by PropertyCode.
    /// Spawn = where the NPC appears (out of sight), Destination = where it walks to and idles.
    /// </summary>
    public static class ManagerLocations
    {
        /// <summary>
        /// PropertyCode constants for the 4 eligible businesses.
        /// These match the serialized propertyCode strings set on each Business in the scene.
        /// The game has no enum for these — they're plain strings on the Property base class.
        /// </summary>
        public const string CarWash = "carwash";
        public const string Laundromat = "laundromat";
        public const string TacoTicklers = "tacoticklers";
        public const string PostOffice = "postoffice";

        public class BusinessLocation
        {
            /// <summary>Where the Manager NPC spawns in (out of player sight).</summary>
            public Vector3 SpawnPosition { get; }
            /// <summary>Facing direction at spawn.</summary>
            public Quaternion SpawnRotation { get; }
            /// <summary>Where the Manager walks to and idles at the business.</summary>
            public Vector3 Destination { get; }
            /// <summary>Facing direction at destination.</summary>
            public Quaternion DestRotation { get; }

            public BusinessLocation(
                Vector3 spawnPosition, float spawnYRotation,
                Vector3 destination, float destYRotation)
            {
                SpawnPosition = spawnPosition;
                SpawnRotation = Quaternion.Euler(0f, spawnYRotation, 0f);
                Destination = destination;
                DestRotation = Quaternion.Euler(0f, destYRotation, 0f);
            }
        }

        /// <summary>
        /// Manager spawn/destination locations keyed by PropertyCode.
        /// Use the F8 "Manager Loc Editor" to record new locations in-game.
        /// </summary>
        private static readonly Dictionary<string, BusinessLocation> _locations = new(StringComparer.OrdinalIgnoreCase)
        {
            { CarWash, new BusinessLocation(
                new Vector3(-22.94f, 1.06f, -20.05f), 88.5f,
                new Vector3(-4.56f, 1.06f, -23.59f), 99.8f) },
            { Laundromat, new BusinessLocation(
                new Vector3(-23.11f, 1.06f, -19.85f), 93.2f,
                new Vector3(-28.68f, 1.56f, 26.85f), 140.5f) },
            { TacoTicklers, new BusinessLocation(
                new Vector3(-47.31f, 1.07f, 62.54f), 204.2f,
                new Vector3(-27.90f, 1.06f, 72.98f), 239.2f) },
            { PostOffice, new BusinessLocation(
                new Vector3(35.38f, 1.06f, 0.13f), 272.7f,
                new Vector3(48.83f, 1.12f, 4.18f), 330.6f) },
        };

        /// <summary>
        /// Gets the spawn/destination location for a business by PropertyCode.
        /// Returns null if no location is registered for that business.
        /// </summary>
        public static BusinessLocation GetLocation(string propertyCode)
        {
            if (propertyCode != null && _locations.TryGetValue(propertyCode, out var loc))
                return loc;
            return null;
        }

        /// <summary>
        /// Checks whether a business has a registered manager location.
        /// </summary>
        public static bool HasLocation(string propertyCode)
        {
            return propertyCode != null && _locations.ContainsKey(propertyCode);
        }

        /// <summary>
        /// All registered property codes that support managers.
        /// </summary>
        public static IEnumerable<string> EligiblePropertyCodes => _locations.Keys;
    }
}
