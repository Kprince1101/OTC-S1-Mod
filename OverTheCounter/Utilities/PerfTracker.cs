using MelonLoader;
using MelonLoader.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace OverTheCounter.Utilities
{
    /// <summary>
    /// Lightweight performance profiler that writes periodic reports to a text file.
    /// Gated behind Config.ProfilingEnabled — zero overhead when disabled (single bool check).
    /// </summary>
    public static class PerfTracker
    {
        private struct RegionStats
        {
            public long TotalTicks;
            public int CallCount;
            public long MaxTicks;
        }

        private static readonly Dictionary<string, RegionStats> _regions = new();
        private static readonly Dictionary<string, long> _pending = new();
        private static readonly List<string> _regionOrder = new();

        // Frame tracking
        private static long _frameStartTick;
        private static long _frameTotalTicks;
        private static long _frameMaxTicks;
        private static int _frameCount;
        private static readonly List<long> _frameTicks = new();

        // GC tracking
        private static long _lastGcMem;
        private static int _lastGen0;
        private static int _lastGen1;
        private static long _gcAllocTotal;
        private static long _gcAllocPeak;
        private static int _gcGen0Collections;
        private static int _gcGen1Collections;

        // Report timing
        private static long _lastReportTick;
        private static long _windowStartTick;
        private const double ReportIntervalSeconds = 30.0;
        private static readonly double TickFreq = Stopwatch.Frequency;

        private static string ReportPath =>
            Path.Combine(MelonEnvironment.UserDataDirectory, "OTC_PerfReport.txt");

        /// <summary>Call at the top of OnLateUpdate.</summary>
        public static void BeginFrame()
        {
            if (!Config.ProfilingEnabled.Value) return;

            _frameStartTick = Stopwatch.GetTimestamp();
            if (_windowStartTick == 0) _windowStartTick = _frameStartTick;

            // GC snapshot
            long mem = GC.GetTotalMemory(false);
            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);

            if (_lastGcMem > 0)
            {
                long delta = mem - _lastGcMem;
                if (delta > 0) _gcAllocTotal += delta;
                if (delta > _gcAllocPeak) _gcAllocPeak = delta;
                _gcGen0Collections += gen0 - _lastGen0;
                _gcGen1Collections += gen1 - _lastGen1;
            }

            _lastGcMem = mem;
            _lastGen0 = gen0;
            _lastGen1 = gen1;
        }

        /// <summary>Call at the bottom of OnLateUpdate.</summary>
        public static void EndFrame()
        {
            if (!Config.ProfilingEnabled.Value) return;
            if (_frameStartTick == 0) return;

            long elapsed = Stopwatch.GetTimestamp() - _frameStartTick;
            _frameTotalTicks += elapsed;
            _frameCount++;
            if (elapsed > _frameMaxTicks) _frameMaxTicks = elapsed;
            _frameTicks.Add(elapsed);

            // Auto-dump check
            long now = Stopwatch.GetTimestamp();
            if (_lastReportTick == 0)
            {
                _lastReportTick = now;
            }
            else if ((now - _lastReportTick) / TickFreq >= ReportIntervalSeconds)
            {
                WriteReport();
            }
        }

        /// <summary>Start timing a named region.</summary>
        public static void Begin(string name)
        {
            if (!Config.ProfilingEnabled.Value) return;
            _pending[name] = Stopwatch.GetTimestamp();
        }

        /// <summary>Stop timing a named region.</summary>
        public static void End(string name)
        {
            if (!Config.ProfilingEnabled.Value) return;
            if (!_pending.TryGetValue(name, out long start)) return;

            long elapsed = Stopwatch.GetTimestamp() - start;
            _pending.Remove(name);

            if (_regions.TryGetValue(name, out var stats))
            {
                stats.TotalTicks += elapsed;
                stats.CallCount++;
                if (elapsed > stats.MaxTicks) stats.MaxTicks = elapsed;
                _regions[name] = stats;
            }
            else
            {
                _regions[name] = new RegionStats
                {
                    TotalTicks = elapsed,
                    CallCount = 1,
                    MaxTicks = elapsed
                };
                _regionOrder.Add(name);
            }
        }

        /// <summary>Write the report to disk and reset all counters.</summary>
        public static void WriteReport()
        {
            if (_frameCount == 0) return;

            try
            {
                double windowSeconds = (Stopwatch.GetTimestamp() - _windowStartTick) / TickFreq;

                // Sort frame ticks for percentiles
                _frameTicks.Sort();
                double frameAvgMs = (_frameTotalTicks / (double)_frameCount) / TickFreq * 1000.0;
                double frameMaxMs = _frameMaxTicks / TickFreq * 1000.0;
                double frameP95Ms = Percentile(_frameTicks, 0.95);
                double frameP99Ms = Percentile(_frameTicks, 0.99);

                var sb = new StringBuilder();
                sb.AppendLine("═══════════════════════════════════════════════════════");
                sb.AppendLine("  OTC Performance Report");
                sb.AppendLine($"  Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"  Sample window: {windowSeconds:F1}s ({_frameCount} frames)");
                sb.AppendLine("═══════════════════════════════════════════════════════");
                sb.AppendLine();

                // Frame time
                sb.AppendLine("── Frame Time (OTC OnLateUpdate only) ─────────────────");
                sb.AppendLine($"  Avg:   {frameAvgMs:F3} ms");
                sb.AppendLine($"  Max:   {frameMaxMs:F3} ms");
                sb.AppendLine($"  p95:   {frameP95Ms:F3} ms");
                sb.AppendLine($"  p99:   {frameP99Ms:F3} ms");
                sb.AppendLine();

                // GC
                sb.AppendLine("── GC ─────────────────────────────────────────────────");
                long avgAlloc = _frameCount > 0 ? _gcAllocTotal / _frameCount : 0;
                sb.AppendLine($"  Alloc/frame (avg):  {FormatBytes(avgAlloc)}");
                sb.AppendLine($"  Peak alloc frame:   {FormatBytes(_gcAllocPeak)}");
                sb.AppendLine($"  Gen0 collections:   {_gcGen0Collections}");
                sb.AppendLine($"  Gen1 collections:   {_gcGen1Collections}");
                sb.AppendLine();

                // Regions
                sb.AppendLine("── Regions (sorted by total time) ─────────────────────");
                sb.AppendLine($"  {"Region",-32} {"Avg ms",8} {"Max ms",8} {"Calls",8} {"Total ms",10}");
                sb.AppendLine($"  {new string('─', 70)}");

                var sorted = _regionOrder
                    .Where(n => _regions.ContainsKey(n))
                    .Select(n => (Name: n, Stats: _regions[n]))
                    .OrderByDescending(r => r.Stats.TotalTicks);

                foreach (var (name, stats) in sorted)
                {
                    double avgMs = (stats.TotalTicks / (double)stats.CallCount) / TickFreq * 1000.0;
                    double maxMs = stats.MaxTicks / TickFreq * 1000.0;
                    double totalMs = stats.TotalTicks / TickFreq * 1000.0;
                    sb.AppendLine($"  {name,-32} {avgMs,8:F3} {maxMs,8:F3} {stats.CallCount,8} {totalMs,10:F1}");
                }

                sb.AppendLine();
                sb.AppendLine("── Notes ──────────────────────────────────────────────");
                sb.AppendLine("  Frame Time measures OTC's OnLateUpdate only, not the");
                sb.AppendLine("  full game frame. Regions are subsections within it.");
                sb.AppendLine("  GC alloc is process-wide (not OTC-specific).");

                File.WriteAllText(ReportPath, sb.ToString());
                OTCLog.Msg(OTCLog.Systems.General, $"Perf report written to {ReportPath}");
            }
            catch (Exception ex)
            {
                OTCLog.Error(OTCLog.Systems.General, $"Failed to write perf report: {ex.Message}");
            }

            Reset();
        }

        private static void Reset()
        {
            _regions.Clear();
            _regionOrder.Clear();
            _pending.Clear();
            _frameTotalTicks = 0;
            _frameMaxTicks = 0;
            _frameCount = 0;
            _frameTicks.Clear();
            _gcAllocTotal = 0;
            _gcAllocPeak = 0;
            _gcGen0Collections = 0;
            _gcGen1Collections = 0;
            _lastGcMem = 0;
            _lastReportTick = Stopwatch.GetTimestamp();
            _windowStartTick = 0;
        }

        private static double Percentile(List<long> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            int index = (int)Math.Ceiling(p * sorted.Count) - 1;
            if (index < 0) index = 0;
            return sorted[index] / TickFreq * 1000.0;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}
