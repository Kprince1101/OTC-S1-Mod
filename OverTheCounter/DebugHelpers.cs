#if DEBUG
using Il2CppInterop.Runtime.Injection;
using S1API.Console;
using S1API.GameTime;
using S1API.Money;
using UnityEngine;
using MelonLoader;
using System;

namespace OverTheCounter
{
    public class DebugHelpers : MonoBehaviour
    {
        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("DebugHelpers");
        private bool _menuVisible;

        public static void Register()
        {
            ClassInjector.RegisterTypeInIl2Cpp<DebugHelpers>();
        }

        public DebugHelpers() : base() { }
        public DebugHelpers(IntPtr ptr) : base(ptr) { }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
                _menuVisible = !_menuVisible;
        }

        private void OnGUI()
        {
            if (!_menuVisible) return;

            GUILayout.BeginArea(new Rect(10, 10, 220, 200), "DEV TOOLS", GUI.skin.window);

            if (GUILayout.Button("+$1000 Cash"))
                Money.ChangeCashBalance(1000f, true, true);

            if (GUILayout.Button("+100 XP"))
                ConsoleHelper.GiveXp(100);

            if (GUILayout.Button("Set Night (22:00)"))
                ConsoleHelper.SetTime("2200");

            if (GUILayout.Button("+1 Hour"))
            {
                int current = TimeManager.CurrentTime;
                int hours = (current / 100 + 1) % 24;
                int mins = current % 100;
                ConsoleHelper.SetTime((hours * 100 + mins).ToString("D4"));
            }

            if (GUILayout.Button("Force Save"))
                ConsoleHelper.SaveGame();

            GUILayout.EndArea();
        }
    }
}
#endif
