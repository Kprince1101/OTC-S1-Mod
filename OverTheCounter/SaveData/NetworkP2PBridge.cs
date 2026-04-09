using OverTheCounter.Utilities;
using SteamNetworkLib;
using SteamNetworkLib.Models;
using System;
using System.Collections.Generic;

#if IL2CPP
using Il2CppSteamworks;
#else
using Steamworks;
#endif

namespace OverTheCounter.SaveData
{
    /// <summary>
    /// Optional P2P networking layer using SteamNetworkLib's Steam P2P support.
    /// Dormant by default — nothing subscribes until a feature explicitly opts in.
    ///
    /// <para><b>Why this exists:</b></para>
    /// <para>
    /// OTC's primary sync path (<see cref="NetworkSyncBridge"/>) uses Steam lobby
    /// metadata (SetLobbyData/SetLobbyMemberData). This works well for small,
    /// persistent state but has hard limits:
    /// </para>
    /// <list type="bullet">
    ///   <item>Shared ~16KB budget across the game, OTC, and every other mod</item>
    ///   <item>8,192 bytes per individual key (k_cubChatMetadataMax)</item>
    ///   <item>Unreliable change callbacks on IL2CPP (requires polling workarounds)</item>
    ///   <item>No targeted sends — lobby data is visible to all members</item>
    ///   <item>~1-2s propagation latency through Steam's metadata layer</item>
    /// </list>
    ///
    /// <para>
    /// This P2P bridge provides a parallel channel with none of those limits:
    /// 32KB per packet, no shared budget, targeted sends, lower latency.
    /// It runs over Steam's relay network (ISteamNetworking) — no raw sockets,
    /// no IP exposure, same trust model as lobby data.
    /// </para>
    ///
    /// <para><b>When to use this instead of lobby data:</b></para>
    /// <list type="bullet">
    ///   <item>Data that could grow beyond 8KB per value (unbounded lists)</item>
    ///   <item>High-frequency updates where 1-2s lobby latency is too slow</item>
    ///   <item>Client→host messages that need targeting (not broadcast to all)</item>
    ///   <item>Payloads that would push total lobby data usage toward the budget</item>
    /// </list>
    ///
    /// <para><b>When to keep using lobby data:</b></para>
    /// <list type="bullet">
    ///   <item>Small, persistent state that late joiners must receive (config, flags)</item>
    ///   <item>Data that rarely changes (quest progress, store open/closed)</item>
    ///   <item>Anything under ~1KB that doesn't need low latency</item>
    /// </list>
    ///
    /// <para><b>Migration candidates — current lobby data channels at risk:</b></para>
    /// <list type="bullet">
    ///   <item>
    ///     <b>CustomerState (OTC_customers)</b> — HIGH risk. Each customer's
    ///     SelectedProducts list is serialized without bounds. 5 customers each
    ///     selecting 10+ items could push this beyond 4KB. This is the most
    ///     likely channel to hit the 8KB per-value limit first.
    ///   </item>
    ///   <item>
    ///     <b>ManagerSlots (OTC_m0–m7)</b> — MEDIUM risk. Manager config strings
    ///     (inventory, routes, upgrades) grow with gameplay progression. Currently
    ///     spread across 8 separate keys to avoid truncation — a sign the data was
    ///     already too large for a single key.
    ///   </item>
    ///   <item>
    ///     <b>ManagerMessages / DrifterMessages (OTC_mgrmsg, OTC_driftermsg)</b> —
    ///     MEDIUM risk. Message text length is unbounded. Long NPC dialogue or
    ///     batched messages could spike unexpectedly.
    ///   </item>
    ///   <item>
    ///     <b>QuestActions (OTC_action)</b> — LOW-MEDIUM risk. Most actions are
    ///     short strings, but MANAGER_CONFIG payloads embed full config data.
    ///     Also uses ClientSyncVar (member data) which has unreliable callbacks,
    ///     requiring polling. P2P would fix both the size and reliability issues.
    ///   </item>
    /// </list>
    ///
    /// <para><b>Architecture:</b></para>
    /// <list type="bullet">
    ///   <item>Uses <see cref="DataSyncMessage"/> (key-value strings over P2P)</item>
    ///   <item>Shares the <see cref="SteamNetworkClient"/> from <see cref="NetworkSyncBridge"/>
    ///     — ProcessIncomingMessages() already ticks both lobby data and P2P</item>
    ///   <item>Keyed routing: each key maps to a callback, unknown keys are ignored</item>
    ///   <item>DataType="otc" tag prevents cross-mod collisions with other mods using DataSyncMessage</item>
    ///   <item>Self-echo filtering: own broadcasts are discarded on receive</item>
    /// </list>
    ///
    /// <para><b>Tradeoff vs lobby data:</b> P2P messages are NOT persistent.
    /// Late joiners won't receive prior messages. Any channel migrated to P2P
    /// needs a "catch-up" mechanism (e.g., host re-sends full state on player join).</para>
    /// </summary>
    internal static class NetworkP2PBridge
    {
        private static SteamNetworkClient _client;
        private static bool _initialized;

        private static readonly Dictionary<string, Action<ulong, string>> _handlers
            = new Dictionary<string, Action<ulong, string>>(StringComparer.OrdinalIgnoreCase);

        // ---- Auto-chunking ----
        /// <summary>
        /// Maximum value size per P2P chunk. SteamNetworkLib 1.2.3 raised the
        /// packet limit to ~8192 bytes, so we match that.
        /// </summary>
        internal const int MaxChunkValueSize = 8192;
        private const string ChunkMarker = "#C";
        private static int _nextMsgId;
        // Buffer key: "steamId|originalKey|msgId" → chunks + creation time
        private static readonly Dictionary<string, string[]> _chunkBuffers
            = new Dictionary<string, string[]>();
        private static readonly Dictionary<string, DateTime> _chunkTimestamps
            = new Dictionary<string, DateTime>();
        private const int ChunkTimeoutSeconds = 30;

        /// <summary>
        /// Initializes P2P message handling on the shared <see cref="SteamNetworkClient"/>.
        /// Called from <see cref="NetworkSyncBridge.EnsureNetworkReady"/> after the client
        /// is created. Safe to call multiple times — no-ops after the first.
        /// </summary>
        internal static void Initialize(SteamNetworkClient client)
        {
            if (_initialized || client == null) return;
            _client = client;

            try
            {
                _client.RegisterMessageHandler<DataSyncMessage>(OnDataSyncReceived);
                _initialized = true;
                OTCLog.Msg(OTCLog.Systems.Network, $"P2P bridge initialized. LocalPlayerId={_client.LocalPlayerId.m_SteamID}, IsInLobby={_client.IsInLobby}");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"P2P bridge init failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Registers a handler for a specific data key.
        /// Only one handler per key — subsequent calls overwrite the previous.
        /// </summary>
        /// <param name="key">Channel key (e.g., "customers", "placement").</param>
        /// <param name="handler">Callback receiving (senderSteam64Id, value).</param>
        internal static void Subscribe(string key, Action<ulong, string> handler)
        {
            _handlers[key] = handler;
            OTCLog.Msg(OTCLog.Systems.Network, $"P2P subscribed handler for key '{key}' (total: {_handlers.Count})");
        }

        /// <summary>
        /// Removes the handler for a specific data key.
        /// </summary>
        internal static void Unsubscribe(string key)
        {
            _handlers.Remove(key);
        }

        /// <summary>
        /// Broadcasts a key-value message to ALL lobby members via P2P.
        /// Auto-chunks if value exceeds <see cref="MaxChunkValueSize"/>.
        /// </summary>
        internal static void Broadcast(string key, string value)
        {
            if (!CanSend()) return;

            try
            {
                if (value != null && value.Length > MaxChunkValueSize)
                {
                    SendChunked(default, key, value, broadcast: true);
                    return;
                }
                var msg = new DataSyncMessage { Key = key, Value = value, DataType = "otc" };
                _client.BroadcastMessage(msg);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"P2P broadcast failed [{key}]: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a key-value message to a specific player via P2P.
        /// Auto-chunks if value exceeds <see cref="MaxChunkValueSize"/>.
        /// </summary>
        internal static void SendTo(CSteamID target, string key, string value)
        {
            if (!CanSend()) return;

            try
            {
                if (value != null && value.Length > MaxChunkValueSize)
                {
                    SendChunked(target, key, value, broadcast: false);
                    return;
                }
                OTCLog.Msg(OTCLog.Systems.Network, $"P2P SendTo: key='{key}', target={target.m_SteamID}, valueLen={value?.Length ?? 0}");
                var msg = new DataSyncMessage { Key = key, Value = value, DataType = "otc" };
                _ = _client.SendMessageToPlayerAsync(target, msg);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"P2P send failed [{key}→{target.m_SteamID}]: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a key-value message to the lobby host via P2P.
        /// Convenience wrapper — resolves the host's SteamID from the lobby.
        /// </summary>
        internal static void SendToHost(string key, string value)
        {
            if (!CanSend()) return;

            try
            {
                var lobby = _client.CurrentLobby;
                if (lobby == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Network, $"P2P SendToHost: not in a lobby [{key}].");
                    return;
                }

                var hostId = lobby.OwnerId;
                var localId = _client.LocalPlayerId;
                OTCLog.Msg(OTCLog.Systems.Network, $"P2P SendToHost: key='{key}', hostId={hostId.m_SteamID}, localId={localId.m_SteamID}, samePlayer={hostId.m_SteamID == localId.m_SteamID}");
                SendTo(hostId, key, value);
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Network, $"P2P SendToHost failed [{key}]: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets P2P bridge state. Called from <see cref="NetworkSyncBridge.Cleanup"/>.
        /// </summary>
        internal static void Cleanup()
        {
            _handlers.Clear();
            _chunkBuffers.Clear();
            _chunkTimestamps.Clear();
            _client = null;
            _initialized = false;
        }

        // ==================================================================
        // Internal — chunking
        // ==================================================================

        /// <summary>
        /// Splits a large value into chunks and sends each as a separate message.
        /// Key format per chunk: <c>{originalKey}#C{1-based idx}/{total}/{msgId}</c>.
        /// </summary>
        private static void SendChunked(CSteamID target, string key, string value, bool broadcast)
        {
            int total = (value.Length + MaxChunkValueSize - 1) / MaxChunkValueSize;
            int msgId = _nextMsgId++;

            for (int i = 0; i < total; i++)
            {
                int start = i * MaxChunkValueSize;
                int len = Math.Min(MaxChunkValueSize, value.Length - start);
                string chunkKey = $"{key}{ChunkMarker}{i + 1}/{total}/{msgId}";
                string chunkValue = value.Substring(start, len);

                var msg = new DataSyncMessage { Key = chunkKey, Value = chunkValue, DataType = "otc" };
                if (broadcast)
                    _client.BroadcastMessage(msg);
                else
                    _ = _client.SendMessageToPlayerAsync(target, msg);
            }
            OTCLog.Msg(OTCLog.Systems.Network, $"P2P sent {total} chunks for key='{key}', msgId={msgId}, totalLen={value.Length}");
        }

        // ==================================================================
        // Internal
        // ==================================================================

        private static bool CanSend()
        {
            if (!_initialized || _client == null)
            {
                OTCLog.Warning(OTCLog.Systems.Network, "P2P bridge not initialized — message dropped.");
                return false;
            }

            if (!_client.IsInLobby)
            {
                // Silently drop — common during scene transitions.
                return false;
            }

            return true;
        }

        /// <summary>
        /// Central receive handler registered with SteamNetworkLib.
        /// Routes incoming <see cref="DataSyncMessage"/> to the per-key handler.
        /// Messages with unknown keys or non-"otc" DataType are silently ignored.
        /// </summary>
        private static void OnDataSyncReceived(DataSyncMessage message, CSteamID senderId)
        {
            if (message == null) return;

            OTCLog.Msg(OTCLog.Systems.Network, $"P2P RECV: DataType='{message.DataType}', Key='{message.Key}', from={senderId.m_SteamID}, valueLen={message.Value?.Length ?? 0}");

            // Ignore messages from other mods using DataSyncMessage.
            if (message.DataType != "otc")
            {
                OTCLog.Msg(OTCLog.Systems.Network, $"P2P RECV: dropped (DataType != 'otc')");
                return;
            }

            // Ignore our own broadcasts (BroadcastMessage sends to all members including self).
            if (_client != null && senderId.m_SteamID == _client.LocalPlayerId.m_SteamID)
            {
                OTCLog.Msg(OTCLog.Systems.Network, $"P2P RECV: dropped (self-echo)");
                return;
            }

            string key = message.Key;
            if (string.IsNullOrEmpty(key)) return;

            // ---- Chunk reassembly ----
            int cm = key.IndexOf(ChunkMarker, StringComparison.Ordinal);
            if (cm >= 0)
            {
                string originalKey = key.Substring(0, cm);
                string[] parts = key.Substring(cm + ChunkMarker.Length).Split('/');
                if (parts.Length == 3
                    && int.TryParse(parts[0], out int ci)
                    && int.TryParse(parts[1], out int total)
                    && int.TryParse(parts[2], out int msgId)
                    && ci >= 1 && ci <= total && total > 0)
                {
                    string bufKey = $"{senderId.m_SteamID}|{originalKey}|{msgId}";
                    if (!_chunkBuffers.TryGetValue(bufKey, out var buf)
                        || (_chunkTimestamps.TryGetValue(bufKey, out var ts)
                            && (DateTime.UtcNow - ts).TotalSeconds > ChunkTimeoutSeconds))
                    {
                        buf = new string[total];
                        _chunkBuffers[bufKey] = buf;
                        _chunkTimestamps[bufKey] = DateTime.UtcNow;
                    }
                    buf[ci - 1] = message.Value;

                    // Check if all chunks arrived
                    for (int i = 0; i < buf.Length; i++)
                        if (buf[i] == null) return;

                    _chunkBuffers.Remove(bufKey);
                    _chunkTimestamps.Remove(bufKey);
                    string fullValue = string.Concat(buf);
                    OTCLog.Msg(OTCLog.Systems.Network, $"P2P RECV: reassembled {total} chunks for key='{originalKey}', totalLen={fullValue.Length}");

                    if (_handlers.TryGetValue(originalKey, out var chunkHandler))
                        chunkHandler(senderId.m_SteamID, fullValue);
                    return;
                }
            }

            if (_handlers.TryGetValue(key, out var handler))
            {
                OTCLog.Msg(OTCLog.Systems.Network, $"P2P RECV: dispatching to handler for key='{key}'");
                try
                {
                    handler(senderId.m_SteamID, message.Value);
                }
                catch (Exception ex)
                {
                    OTCLog.Warning(OTCLog.Systems.Network, $"P2P handler error [{key}]: {ex.Message}");
                }
            }
            else
            {
                OTCLog.Msg(OTCLog.Systems.Network, $"P2P RECV: no handler for key='{key}' (registered keys: {string.Join(", ", _handlers.Keys)})");
            }
        }
    }
}
