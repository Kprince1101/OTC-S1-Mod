using S1API.GameTime;
using UnityEngine;

namespace OverTheCounter.Logic
{
    /// <summary>
    /// Single source of truth for OTC store operating hours.
    /// All time-based checks (budtender shifts, deal routing, UI labels) reference these constants.
    /// </summary>
    public static class StoreHours
    {
        /// <summary>Store opens at 8 AM.</summary>
        public const int OpenHour = 8;

        /// <summary>Store closes at 8 PM (20:00).</summary>
        public const int CloseHour = 20;

        /// <summary>Open time in total minutes from midnight (480).</summary>
        public const int OpenMinute = OpenHour * 60;

        /// <summary>Close time in total minutes from midnight (1200).</summary>
        public const int CloseMinute = CloseHour * 60;

        /// <summary>Display string for UI labels.</summary>
        public const string DisplayRange = "8AM-8PM";

        /// <summary>Display string with spaces for switch labels.</summary>
        public const string DisplayRangeSpaced = "8AM - 8PM";

        /// <summary>
        /// Returns a brightness multiplier (0.8–1.5) based on time of day.
        /// Boosted at night (9PM–5AM) so neons illuminate products,
        /// reduced during midday (10AM–4PM) but still visible,
        /// smooth ramp during dawn/dusk transitions.
        /// </summary>
        public static float GetLightBrightness()
        {
            int hhmm = TimeManager.CurrentTime;
            int hour = hhmm / 100;
            int minute = hhmm % 100;
            float t = hour + minute / 60f; // 0.0–24.0

            // Night: boosted brightness
            if (t >= 21f || t <= 5f) return 1.5f;

            // Midday: reduced but still visible
            if (t >= 10f && t <= 16f) return 0.8f;

            // Dawn ramp: 5AM–10AM → 1.5 down to 0.8
            if (t > 5f && t < 10f)
                return Mathf.Lerp(1.5f, 0.8f, (t - 5f) / 5f);

            // Dusk ramp: 4PM–9PM → 0.8 up to 1.5
            return Mathf.Lerp(0.8f, 1.5f, (t - 16f) / 5f);
        }
    }
}
