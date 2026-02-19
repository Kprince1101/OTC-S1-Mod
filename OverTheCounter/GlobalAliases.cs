// Global namespace aliases for dual IL2CPP / Mono compilation.
// On IL2CPP, game types live under Il2Cpp-prefixed namespaces.
// On Mono, they live under unprefixed namespaces.
// These aliases let inline code use the unprefixed form (e.g. ScheduleOne.NPCs.NPC)
// and resolve correctly in both builds.
// NOTE: These aliases work in CODE but not in using directives.
// Using directives still need #if IL2CPP / #else blocks per-file.

#if IL2CPP
global using ScheduleOne = Il2CppScheduleOne;
global using FishNet = Il2CppFishNet;
global using Steamworks = Il2CppSteamworks;
global using TMPro = Il2CppTMPro;
global using GameSystem = Il2CppSystem;
#else
global using GameSystem = System;
#endif
