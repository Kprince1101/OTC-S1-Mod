using OverTheCounter.Logic;
using OverTheCounter.Logic.Placement;
using OverTheCounter.Utilities;
using S1API.Quests;
using S1API.Utils;
using S1API.GameTime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace OverTheCounter.Quests
{
    /// <summary>
    /// Abstract base for per-building checkout alert quests.
    /// Shows an entry per counter with waiting customers and a color-coded countdown subtitle.
    /// Subclasses provide Title, BuildingId, and singleton management.
    /// </summary>
    public abstract class BuildingAlertQuest : Quest
    {
        protected override string Description => "Customers are waiting at the checkout.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => Core.OtcIconDir != null
            ? ImageUtils.LoadImage(Path.Combine(Core.OtcIconDir, "StoreAlertIcon.png"))
            : null;

        /// <summary>Building ID to filter counters (e.g. "westville_shack").</summary>
        protected abstract string BuildingId { get; }

        /// <summary>Called after quest is created to set the subclass singleton.</summary>
        protected abstract void SetInstance();

        /// <summary>Called on dismiss/complete to clear the subclass singleton.</summary>
        protected abstract void ClearInstance();

        private bool _initialized;
        private bool _shown;
        private bool _showDebugLogged;
        private bool _subtitleDebugLogged;

        private struct AlertInfo
        {
            public int QueueCount;
            public int MinutesRemaining;
        }

        // Reusable list to avoid per-frame allocation
        private readonly List<AlertInfo> _alerts = new();

        // Track what we last displayed to avoid redundant updates
        private int _lastHash;

        // ── Boilerplate (ConsolidatedQuest pattern) ──

        private ScheduleOne.Quests.Quest GetS1Quest()
        {
            var field = typeof(Quest).GetField("S1Quest", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(this) as ScheduleOne.Quests.Quest;
        }

        internal void InitAndBegin()
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null) return;
                s1Quest.InitializeQuest(Title, Description,
                    Array.Empty<ScheduleOne.Persistence.Datas.QuestEntryData>(), s1Quest.StaticGUID);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"{Title} TriggerInternalInit failed: {ex.Message}");
            }

            _initialized = true;
            Begin();
        }

        private void SetSubtitleViaReflection(string subtitle)
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null) return;

                s1Quest.SetSubtitle(subtitle);

                if (s1Quest.hudUI != null)
                    s1Quest.hudUI.UpdateMainLabel();

                if (!_subtitleDebugLogged)
                {
                    OTCLog.Msg(OTCLog.Systems.Quest, $"{Title} SetSubtitle: '{subtitle}'");
                    _subtitleDebugLogged = true;
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"{Title} SetSubtitle failed: {ex.Message}");
            }
        }

        private void RemoveExcessEntries(int keepCount)
        {
            try
            {
                var s1Quest = GetS1Quest();
                for (int i = QuestEntries.Count - 1; i >= keepCount; i--)
                {
                    try
                    {
                        if (s1Quest != null && s1Quest.Entries != null && i < s1Quest.Entries.Count)
                        {
                            var entry = s1Quest.Entries[i];
                            if (entry != null)
                            {
                                if (entry.GetEntryUI() != null && entry.GetEntryUI().gameObject != null)
                                    UnityEngine.Object.DestroyImmediate(entry.GetEntryUI().gameObject);
                                if (entry.gameObject != null)
                                    UnityEngine.Object.DestroyImmediate(entry.gameObject);
                            }
                            s1Quest.Entries.RemoveAt(i);
                        }
                    }
                    catch { }

                    if (i < QuestEntries.Count)
                        QuestEntries.RemoveAt(i);
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"{Title} RemoveExcessEntries failed: {ex.Message}");
            }
        }

        private void ClearAllEntries()
        {
            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest == null || s1Quest.Entries == null)
                {
                    QuestEntries.Clear();
                    return;
                }

                for (int i = s1Quest.Entries.Count - 1; i >= 0; i--)
                {
                    var entry = s1Quest.Entries[i];
                    if (entry != null)
                    {
                        if (entry.GetEntryUI() != null && entry.GetEntryUI().gameObject != null)
                            UnityEngine.Object.DestroyImmediate(entry.GetEntryUI().gameObject);
                        if (entry.gameObject != null)
                            UnityEngine.Object.DestroyImmediate(entry.gameObject);
                    }
                }
                s1Quest.Entries.Clear();
                QuestEntries.Clear();
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"{Title} ClearAllEntries failed: {ex.Message}");
                try { QuestEntries.Clear(); } catch { }
            }
        }

        public void Show()
        {
            if (_shown) return;

            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest?.hudUI?.gameObject == null) return;

                var go = s1Quest.hudUI.gameObject;

                if (!_showDebugLogged)
                {
                    OTCLog.Msg(OTCLog.Systems.Quest, $"{Title} Show: hudUI exists, active={go.activeSelf}");
                    _showDebugLogged = true;
                }

                if (!go.activeSelf)
                    go.SetActive(true);

                var cg = go.GetComponent<CanvasGroup>();
                if (cg != null)
                {
                    cg.alpha = 1f;
                    cg.blocksRaycasts = true;
                    cg.interactable = true;
                }

                var le = go.GetComponent<LayoutElement>();
                if (le != null)
                    le.ignoreLayout = false;

                _shown = true;
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"{Title} Show failed: {ex.Message}");
            }
        }

        public void Dismiss()
        {
            try { ClearAllEntries(); }
            catch (Exception ex) { OTCLog.Warning(OTCLog.Systems.Quest, $"{Title} Dismiss: ClearAllEntries threw: {ex.Message}"); }

            try
            {
                var s1Quest = GetS1Quest();
                if (s1Quest != null)
                    s1Quest.Fail(false);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"{Title} Dismiss failed: {ex.Message}");
            }

            ClearInstance();
        }

        // ── Lifecycle ──

        protected override void OnCreated()
        {
            base.OnCreated();
            SetInstance();
        }

        // ── Tick (called from Core.OnLateUpdateImpl) ──

        public void Tick()
        {
            if (!_initialized) return;

            try
            {
                GatherAlerts();

                if (_alerts.Count == 0)
                {
                    // No waiting customers — complete and tear down
                    try
                    {
                        ClearAllEntries();
                        Complete();
                        End();
                    }
                    catch { }
                    ClearInstance();
                    return;
                }

                // Check if anything changed since last tick
                int hash = ComputeHash();
                if (hash != _lastHash)
                {
                    _lastHash = hash;
                    UpdateEntries();
                }

                UpdateSubtitle();
                Show(); // idempotent — retries until HUD exists
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"{Title} Tick failed: {ex.Message}");
            }
        }

        private void GatherAlerts()
        {
            _alerts.Clear();

            int currentTime = TimeManager.CurrentTime;
            int currentMins = (currentTime / 100) * 60 + (currentTime % 100);

            int totalWaiting = 0;
            int nearestDeadline = int.MaxValue;

            foreach (var counter in CheckoutCounter.AllCounters)
            {
                if (counter.Queue.Count == 0) continue;
                if (counter.BuildingId != BuildingId) continue;

                // Don't alert for counters with an active budtender — they're being handled
                if (counter.IsStaffed) continue;

                totalWaiting += counter.Queue.Count;

                // Get front customer's start time for deadline tracking
                string frontId = counter.Queue[0];
                if (!CustomerInstance.Active.TryGetValue(frontId, out var frontCustomer))
                    continue;

                int startTime = frontCustomer.CheckoutStartTime;
                if (startTime <= 0) continue;

                int startMins = (startTime / 100) * 60 + (startTime % 100);
                int remaining = 240 - (currentMins - startMins);
                if (remaining < 0) remaining += 1440; // midnight wrap
                if (remaining > 240) remaining = 0;   // past deadline

                if (remaining < nearestDeadline)
                    nearestDeadline = remaining;
            }

            if (totalWaiting > 0)
            {
                _alerts.Add(new AlertInfo
                {
                    QueueCount = totalWaiting,
                    MinutesRemaining = nearestDeadline == int.MaxValue ? 240 : nearestDeadline
                });
            }
        }

        private void UpdateEntries()
        {
            int existingCount = QuestEntries.Count;
            int newCount = _alerts.Count;

            // Update existing entries in place
            for (int i = 0; i < Math.Min(existingCount, newCount); i++)
            {
                string text = FormatEntryText(_alerts[i]);
                if (QuestEntries[i].Title != text)
                    QuestEntries[i].Title = text;
            }

            // Add new entries (no POI — this is a status quest, not a waypoint)
            for (int i = existingCount; i < newCount; i++)
            {
                var entry = AddEntry(FormatEntryText(_alerts[i]));
                entry.Begin();
            }

            // Remove excess
            if (existingCount > newCount)
                RemoveExcessEntries(newCount);
        }

        private static string FormatEntryText(AlertInfo alert)
        {
            return $"\u2022 {alert.QueueCount} waiting";
        }

        private void UpdateSubtitle()
        {
            if (_alerts.Count == 0) return;

            // Find the nearest deadline
            int nearest = int.MaxValue;
            for (int i = 0; i < _alerts.Count; i++)
            {
                if (_alerts[i].MinutesRemaining < nearest)
                    nearest = _alerts[i].MinutesRemaining;
            }

            int hours = nearest / 60;
            int mins = nearest % 60;
            string timeStr = hours > 0 ? $"{hours}h {mins}m" : $"{mins}m";

            string subtitle = nearest < 120
                ? $"<color=#ff6b6b> ({timeStr})</color>"
                : $"<color=green> ({timeStr})</color>";

            SetSubtitleViaReflection(subtitle);
        }

        private int ComputeHash()
        {
            int h = _alerts.Count;
            for (int i = 0; i < _alerts.Count; i++)
            {
                h = h * 31 + _alerts[i].QueueCount;
                h = h * 31 + _alerts[i].MinutesRemaining;
            }
            return h;
        }
    }
}
