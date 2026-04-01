using MelonLoader;
using MelonLoader.Utils;
using OverTheCounter.SaveData;
using OverTheCounter.Utilities;
using S1API.Quests;
using S1API.Quests.Constants;
using S1API.Saveables;
using S1API.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace OverTheCounter.Quests
{
    /// <summary>Loads and caches tinted versions of the CrimeWare quest icon.</summary>
    internal static class QuestIconHelper
    {
        private static readonly Dictionary<int, Sprite> _cache = new();

        private static string IconPath => Core.OtcIconDir != null
            ? Path.Combine(Core.OtcIconDir, "CrimeWareQuest.png")
            : null;

        internal static Sprite Load() => IconPath != null ? ImageUtils.LoadImage(IconPath) : null;

        internal static Sprite LoadTinted(Color tint)
        {
            int key = tint.GetHashCode();
            if (_cache.TryGetValue(key, out var cached) && cached != null) return cached;

            try
            {
                var bytes = File.ReadAllBytes(IconPath);
                var tex = new Texture2D(2, 2);
                if (!ImageConversion.LoadImage(tex, bytes)) return Load();

                var pixels = tex.GetPixels();
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = new Color(
                        pixels[i].r * tint.r,
                        pixels[i].g * tint.g,
                        pixels[i].b * tint.b,
                        pixels[i].a);
                }
                tex.SetPixels(pixels);
                tex.Apply();

                var sprite = Sprite.Create(tex,
                    new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                _cache[key] = sprite;
                return sprite;
            }
            catch { return Load(); }
        }
    }

    /// <summary>
    /// Workaround for S1API POI position bug — forces the correct world position
    /// on the underlying game QuestEntry after the POI has been created.
    /// </summary>
    internal static class QuestPoiFixer
    {
        private static FieldInfo _s1EntryField;

        public static void FixPosition(QuestEntry entry, Vector3 worldPos)
        {
            if (entry == null) return;
            MelonCoroutines.Start(FixPositionDelayed(entry, worldPos));
        }

        private static IEnumerator FixPositionDelayed(QuestEntry entry, Vector3 worldPos)
        {
            OTCLog.Msg(OTCLog.Systems.Quest, $"QuestPoiFixer: starting, target={worldPos}");

            // Wait for Quest.Start() prefix (CreateInternal) + QuestEntry.Start() (CreatePoI)
            for (int i = 0; i < 5; i++)
                yield return null;

            try
            {
                if (_s1EntryField == null)
                    _s1EntryField = typeof(QuestEntry).GetField("S1QuestEntry",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                OTCLog.Msg(OTCLog.Systems.Quest, $"QuestPoiFixer: field={_s1EntryField != null}");

#if IL2CPP
                var s1Entry = _s1EntryField?.GetValue(entry) as Il2CppScheduleOne.Quests.QuestEntry;
#else
                var s1Entry = _s1EntryField?.GetValue(entry) as ScheduleOne.Quests.QuestEntry;
#endif
                if (s1Entry == null)
                {
                    OTCLog.Warning(OTCLog.Systems.Quest, "QuestPoiFixer: s1Entry is null");
                    yield break;
                }

                var poiLoc = s1Entry.PoILocation;
                var poi = s1Entry.PoI;
                OTCLog.Msg(OTCLog.Systems.Quest,
                    $"QuestPoiFixer: PoILocation={poiLoc != null} pos={poiLoc?.position} PoI={poi != null} poiPos={poi?.transform.position}");

                s1Entry.SetPoILocation(worldPos);

                OTCLog.Msg(OTCLog.Systems.Quest,
                    $"QuestPoiFixer: AFTER SetPoILocation → PoILocation.pos={s1Entry.PoILocation?.position} PoI.pos={s1Entry.PoI?.transform.position}");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"QuestPoiFixer failed: {ex.Message}");
            }
        }
    }

    public class StaticIntroQuest : Quest
    {
        protected override string Title => "Crimeware as a Service";
        protected override string Description => "Someone noticed your operation. Check the OTC app for a new message.";
        protected override bool AutoBegin => false;
        protected override Sprite QuestIcon => QuestIconHelper.Load();

        [SaveableField("static_quest_stage")]
        private int _stage; // 0=not started, 1=check messages, 2=pay, 3=drop off, 4=done

        private QuestEntry _checkMessagesEntry;
        private QuestEntry _payEntry;
        private QuestEntry _dropOffEntry;

        public static StaticIntroQuest Instance { get; private set; }
        internal static void ResetInstance() => Instance = null;

        public int Stage => _stage;

        private static Vector3 DeadDropPosition => Logic.Placement.CasinoDeadDrop.Position;

        public void Initialize()
        {
            try
            {
                _checkMessagesEntry = AddEntry("Check your messages in the OTC app");
                _payEntry = AddEntry(GetPayText());
                _dropOffEntry = AddEntry(GetDropOffText(), DeadDropPosition);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"Initialize failed: {ex.Message}");
            }
        }

        private static string GetPayText() =>
            $"Pay ${Config.StaticTier1BankCost.Value:N0} via OTC app";

        private static string GetDropOffText() =>
            $"Drop off {Config.StaticTier1WeedGrams.Value}g weed at the casino dead drop";

        public void RefreshEntryText()
        {
            if (_payEntry != null && _stage >= 2 && _stage < 4)
                _payEntry.Title = GetPayText();
            if (_dropOffEntry != null && _stage >= 2 && _stage < 4)
                _dropOffEntry.Title = GetDropOffText();
        }

        public void StartQuest()
        {
            try
            {
                _stage = 1;
                Begin();
                _checkMessagesEntry?.Begin();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"StartQuest failed: {ex.Message}");
            }
        }

        /// <summary>Stage 1→2: Player accepted intro in OTC app.</summary>
        public void CompleteObj1()
        {
            try
            {
                _stage = 2;
                _checkMessagesEntry?.Begin();
                _checkMessagesEntry?.Complete();
                _payEntry?.Begin();
                _dropOffEntry?.Begin();
                QuestPoiFixer.FixPosition(_dropOffEntry, DeadDropPosition);
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"CompleteObj1 failed: {ex.Message}");
            }
        }

        /// <summary>Stage 2→3: One of the two steps (pay or drop off) completed.</summary>
        public void CompleteObj2()
        {
            try
            {
                _stage = 3;
                // Individual entries are completed by CompletePay / CompleteDropOff
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"CompleteObj2 failed: {ex.Message}");
            }
        }

        /// <summary>Stage 3→4: Both steps done, quest complete.</summary>
        public void CompleteObj3()
        {
            try
            {
                _stage = 4;
                _payEntry?.Begin();
                _payEntry?.Complete();
                _dropOffEntry?.Begin();
                _dropOffEntry?.Complete();
                Complete();
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.Quest, $"CompleteObj3 failed: {ex.Message}");
            }
        }

        public void CompletePay()
        {
            _payEntry?.Begin();
            _payEntry?.Complete();
            _dropOffEntry?.Begin(); // Now show dead drop marker
        }

        public void CompleteDropOff()
        {
            _dropOffEntry?.Begin();
            _dropOffEntry?.Complete();
        }

        protected override void OnCreated()
        {
            base.OnCreated();
            Instance = this;
        }

        protected override void OnLoaded()
        {
            base.OnLoaded();
            Instance = this;

            try
            {
                // StaticSaveData is the authority — host state may have advanced
                // via SyncVar before this quest's save was loaded.
                if (StaticSaveData.Instance != null)
                {
                    int hostStage = StaticSaveData.Instance.CrmTier >= 1 ? 4
                        : StaticSaveData.Instance.IntroCompleted ? 2
                        : StaticSaveData.Instance.QuestTriggered ? 1 : 0;
                    if (hostStage > _stage)
                        _stage = hostStage;
                }

                // Entries aren't restored from save — rebuild them
                QuestEntries.Clear();
                _checkMessagesEntry = AddEntry("Check your messages in the OTC app");
                _payEntry = AddEntry(GetPayText());
                _dropOffEntry = AddEntry(GetDropOffText(), DeadDropPosition);

                // Restore entry states based on saved stage
                if (_stage >= 1)
                    _checkMessagesEntry?.Begin();
                if (_stage >= 2)
                {
                    _checkMessagesEntry?.Complete();
                    _payEntry?.Begin();
                    _dropOffEntry?.Begin();
                    QuestPoiFixer.FixPosition(_dropOffEntry, DeadDropPosition);
                }

                // Check split purchase flags for individual entry completion
                if (_stage >= 2 && _stage < 4 && StaticSaveData.Instance != null)
                {
                    if (StaticSaveData.Instance.Tier1MoneyPaid)
                        CompletePay(); // Also begins _dropOffEntry
                    if (StaticSaveData.Instance.Tier1ProductDelivered)
                        CompleteDropOff();
                }

                if (_stage >= 4)
                {
                    _payEntry?.Complete();
                    _dropOffEntry?.Complete();
                    Complete();
                }
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Quest, $"OnLoaded rebuild failed: {ex.Message}");
            }
        }
    }
}
