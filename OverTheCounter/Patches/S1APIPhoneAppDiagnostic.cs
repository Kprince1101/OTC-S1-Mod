using System;
using System.Runtime.ExceptionServices;
using OverTheCounter.Utilities;

namespace OverTheCounter.Patches
{
    /// <summary>
    /// Diagnostic-only: captures full stack traces for "game update removed/renamed
    /// a type S1API still references" failures -- the same bug class behind the
    /// ExitAction/PhoneApp registration failure (blocks OTC's own phone app icon),
    /// and also whatever is causing "[NPCPatches] [S1API] Failed to instantiate
    /// custom NPC type 'X' with default data: Exception has been thrown by the
    /// target of an invocation." for Bella/Static/Vic -- that message is just
    /// TargetInvocationException's generic wrapper text, so the real cause (the
    /// inner exception) never reaches the log at all.
    ///
    /// Originally scoped to only "ExitAction"-mentioning exceptions; broadened to
    /// catch TypeLoadException/MissingMemberException (MissingMethodException and
    /// MissingFieldException both derive from it) REGARDLESS of message content,
    /// since those two exception types are specifically what "a game update
    /// removed/renamed a member S1API's compiled code still references" throws --
    /// rare enough in general Unity/game code that filtering by type alone (no
    /// string-guessing) stays low-noise while catching any instance of this bug
    /// class we haven't hit in a log yet, including the NPC-instantiate one above.
    ///
    /// S1API catches these exceptions INTERNALLY and only logs ex.Message (or, for
    /// the NPC-instantiate case, just the wrapper's fixed generic text) -- never a
    /// stack trace -- so we can't tell which S1API method/line is responsible from
    /// the log line alone. Rather than guess (the NPCLoader_Load_Prefix name
    /// mismatch earlier this session came from exactly that kind of guess and cost
    /// a whole wasted round), we hook AppDomain.FirstChanceException, which fires
    /// for EVERY exception the instant it's thrown -- including the INNER exception
    /// of a reflection call, before it gets wrapped into a TargetInvocationException
    /// and before any try/catch further up the stack swallows it.
    ///
    /// Subscribed as the very first line of Core.OnInitializeMelon(), before
    /// anything else runs (including Config.Initialize()) -- so logging inside the
    /// handler must never depend on Config being ready; use OTCLog.Warning (always
    /// fires) rather than OTCLog.Msg (gated on Config.PatchVerboseLogging, which is
    /// still null this early and would NRE).
    ///
    /// Purely diagnostic: never suppresses, alters, or rethrows anything. Filters
    /// on exception type so it doesn't spam the log -- FirstChanceException fires
    /// constantly for unrelated exceptions across the whole process.
    /// </summary>
    public static class S1APIPhoneAppDiagnostic
    {
        private static bool _subscribed;
        private static int _logged;
        private const int MaxLogged = 30; // several distinct call sites can each fire a few times

        public static void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;

            try
            {
                AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
                OTCLog.Warning(OTCLog.Systems.Patch,
                    "S1APIPhoneAppDiagnostic: subscribed to AppDomain.FirstChanceException to trace TypeLoad/MissingMember failures.");
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
                var ex = e.Exception;
                if (ex == null) return;
                if (!(ex is TypeLoadException) && !(ex is MissingMemberException)) return;
                if (System.Threading.Interlocked.Increment(ref _logged) > MaxLogged) return;

                OTCLog.Warning(OTCLog.Systems.Patch,
                    $"S1APIPhoneAppDiagnostic: captured full trace for a {ex.GetType().Name} " +
                    $"(#{_logged}/{MaxLogged}) -- likely a game update removed/renamed a member " +
                    $"still referenced somewhere in S1API or OTC:\n{ex}");
            }
            catch
            {
                // Never let diagnostic logging itself throw from inside a
                // first-chance-exception handler.
            }
        }
    }
}
