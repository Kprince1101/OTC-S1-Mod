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
        /// Returns a brightness multiplier (0.4–1.0) based on time of day.
        /// Full brightness at night (9PM–5AM), dim during midday (10AM–4PM),
        /// smooth ramp during dawn/dusk transitions.
        /// </summary>
        public static float GetLightBrightness()
        {
            int hhmm = TimeManager.CurrentTime;
            int hour = hhmm / 100;
            int minute = hhmm % 100;
            float t = hour + minute / 60f; // 0.0–24.0

            // Night: full brightness
            if (t >= 21f || t <= 5f) return 1.0f;

            // Midday: dim
            if (t >= 10f && t <= 16f) return 0.4f;

            // Dawn ramp: 5AM–10AM → 1.0 down to 0.4
            if (t > 5f && t < 10f)
                return Mathf.Lerp(1.0f, 0.4f, (t - 5f) / 5f);

            // Dusk ramp: 4PM–9PM → 0.4 up to 1.0
            return Mathf.Lerp(0.4f, 1.0f, (t - 16f) / 5f);
        }
    }
}
