#if IL2CPP
using Il2CppInterop.Runtime.Injection;
using Il2CppScheduleOne.Quests;
#else
using ScheduleOne.Quests;
#endif

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Custom QuestWindowConfig subclass to identify desperation/immediate contracts.
    /// These contracts have urgent delivery windows and should not be consolidated.
    /// </summary>
    public class ImmediateQuestWindowConfig : QuestWindowConfig
    {
        /// <summary>
        /// Registers this type with IL2CPP so it can be used at runtime.
        /// Call this once during mod initialization.
        /// </summary>
        public static void Register()
        {
#if IL2CPP
            ClassInjector.RegisterTypeInIl2Cpp<ImmediateQuestWindowConfig>();
#endif
        }

        public ImmediateQuestWindowConfig() : base()
        {
        }

#if IL2CPP
        public ImmediateQuestWindowConfig(System.IntPtr ptr) : base(ptr)
        {
        }
#endif
    }
}
