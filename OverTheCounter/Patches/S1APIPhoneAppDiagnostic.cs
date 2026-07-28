using System;
using System.Runtime.ExceptionServices;
using OverTheCounter.Utilities;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Diagnostic-only: captures a full stack trace for the "Could not load type
    /// 'Il2CppScheduleOne.DevUtilities.ExitAction'" failure that prevents S1API's
    /// PhoneApp registration from registering ANY custom phone app (OTC's
    /// CustomersApp/GreenTabApp, ModsApp, etc.) -- confirmed as the reason the OTC
    /// icon never appears on the in-game phone home screen.
    ///
    /// S1API catches this exception INTERNALLY inside its own registration method
    /// and only logs ex.Message ("[PhoneApp] Failed to register X: Could not load
    /// type ...") -- never a stack trace -- so we can't tell exactly which S1API
    /// method/line is responsible from that log line alone. Rather than guess (the
    /// NPCLoader_Load_Prefix name mismatch earlier this session came from exactly
    /// that kind of guess and cost a whole wasted round), we hook
    /// AppDomain.FirstChanceException, which fires for EVERY exception the instant
    /// it's thrown -- including ones a try/catch further up the call stack is about
    /// to swallow. That gives us the full ex.ToString() trace for this specific
    /// failure without touching or guessing at S1API's internals.
    ///
    /// Subscribed as the very first line of Core.OnInitializeMelonImpl(), before
    /// anything else runs, so it's active well before any save loads (which is when
    /// the Phone -- and therefore this registration pass -- actually spins up).
    ///
    /// Purely diagnostic: never suppresses, alters, or rethrows anything. Filters
    /// tightly on message content so it doesn't spam the log -- FirstChanceException
    /// fires constantly for unrelated exceptions across the whole process.
    /// </summary>
    public static class S1APIPhoneAppDiagnostic
    {
        private static bool _subscribed;
        private static int _logged;
        private const int MaxLogged = 5; // avoid spam if this fires once per custom app

        public static void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;

            try
            {
                AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
                OTCLog.Msg(OTCLog.Systems.Patch,
                    "S1APIPhoneAppDiagnostic: subscribed to AppDomain.FirstChanceException to trace the ExitAction/PhoneApp registration failure.");
            }
            catch (Exception ex)
            {
                OTCLog.Warning(OTCLog.Systems.Patch, $"S1APIPhoneAppDiagnostic.Subscribe failed: {ex.Message}");
            }
        }

        private static void OnFirstChanceException(object sender, FirstChanceExceptionEventArgs e)
        {
            try
            {
                var msg = e.Exception?.Message;
                if (string.IsNullOrEmpty(msg)) return;
                if (msg.IndexOf("ExitAction", StringComparison.OrdinalIgnoreCase) < 0) return;
                if (System.Threading.Interlocked.Increment(ref _logged) > MaxLogged) return;

                OTCLog.Warning(OTCLog.Systems.Patch,
                    $"S1APIPhoneAppDiagnostic: captured full trace for an ExitAction-related exception " +
                    $"(#{_logged}/{MaxLogged}) -- this is the bug blocking custom phone app registration " +
                    $"(including OTC's own app icon):\n{e.Exception}");
            }
            catch
            {
                // Never let diagnostic logging itself throw from inside a
                // first-chance-exception handler.
            }
        }
    }
}
