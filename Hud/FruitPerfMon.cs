using MelonLoader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace FruitLib
{
    public static class FruitPerfMon
    {
        public static KeyCode ToggleKey   = KeyCode.F11;
        public static float   TargetFps    = 60f;
        public static float   PressureDrop = 0.15f;

        public static float PressureLevel { get; private set; }
        public static float LongFps       => _longFps.Fps;

        private static HudHandle _panel;
        private static bool _visible => _panel != null && _panel.Visible;

        private static readonly Dictionary<string, CounterEntry> _counters = new Dictionary<string, CounterEntry>();
        private static readonly Dictionary<string, TimerEntry>   _timers   = new Dictionary<string, TimerEntry>();

        // ── Public API ────────────────────────────────────────────────────────

        public static void RegisterCounter(string name, Func<int> getter) =>
            _counters[name] = new CounterEntry { Name = name, Getter = getter };


        public static void Begin(string name)
        {
            if (!_timers.TryGetValue(name, out var t))
                _timers[name] = t = new TimerEntry { Name = name };
            t.Begin();
        }

        public static void End(string name)
        {
            if (_timers.TryGetValue(name, out var t)) t.End();
        }

        // ── FruitLibMod hooks (called automatically) ──────────────────────────

        internal static void RegisterPanel()
        {
            _panel = FruitHud.Register("FruitPerfMon", BuildPanel,
                                       order: 100, corner: HudCorner.TopRight,
                                       minWidth: 310f, fontSize: 11);
            _panel.Visible = false;
        }

        internal static void Tick()
        {
            if (!FruitMenu.IsInputSuppressed && Input.GetKeyDown(ToggleKey) && _panel != null)
                _panel.Visible = !_panel.Visible;

            if (_visible && Input.GetKeyDown(KeyCode.R))
                ResetPeaks();

            float dt = Time.unscaledDeltaTime;
            _shortFps.Push(dt);
            _longFps.Push(dt);

            float sf      = _shortFps.Fps;
            float lf      = _longFps.Fps;
            float absP    = TargetFps > 0f ? Mathf.Clamp01(1f - sf / TargetFps) : 0f;
            float drop    = lf > 0f ? (lf - sf) / lf : 0f;
            float relP    = Mathf.Clamp01((drop - PressureDrop) / Mathf.Max(PressureDrop, 0.001f));
            PressureLevel = Mathf.Max(absP, relP);

            foreach (var c in _counters.Values) c.Poll();
        }

        private static void BuildPanel(HudPanel p)
        {
            p.Line($"FPS {_shortFps.Fps,6:F1}  ft {_shortFps.FtMs,5:F2} ms  P {PressureLevel * 100f,4:F0}%  [{ToggleKey}]");

            if (_counters.Count > 0)
            {
                p.Separator();
                foreach (var c in _counters.Values)
                    p.Line($"{c.Name,-22}{c.Current,6}   pk {c.Peak,6}");
            }

            if (_timers.Count > 0)
            {
                p.Separator();
                foreach (var t in _timers.Values)
                    p.Line($"{t.Name,-22}{t.AvgMs,5:F2} ms   pk {t.PeakMs,5:F2} ms");
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void ResetPeaks()
        {
            foreach (var c in _counters.Values) c.Peak = 0;
            foreach (var t in _timers.Values)   t.PeakMs = 0f;
        }

        // ── Rolling FPS tracker ───────────────────────────────────────────────

        private static readonly FpsTracker _shortFps = new FpsTracker(15);
        private static readonly FpsTracker _longFps  = new FpsTracker(300);

        private class FpsTracker
        {
            private readonly int     _n;
            private readonly float[] _buf;
            private          int     _idx;
            private          float   _sum;

            public FpsTracker(int samples) { _n = samples; _buf = new float[samples]; }

            public float Fps  { get; private set; }
            public float FtMs { get; private set; }

            public void Push(float dt)
            {
                _sum      -= _buf[_idx];
                _buf[_idx]  = dt;
                _sum      += dt;
                _idx       = (_idx + 1) % _n;
                float avg  = _sum / _n;
                FtMs = avg * 1000f;
                Fps  = avg > 0f ? 1f / avg : 0f;
            }
        }
    }

    // ── Counter entry ─────────────────────────────────────────────────────────

    internal class CounterEntry
    {
        public string    Name;
        public Func<int> Getter;
        public int       Current;
        public int       Peak;

        public void Poll()
        {
            Current = Getter();
            if (Current > Peak) Peak = Current;
        }
    }

    // ── Timer entry ───────────────────────────────────────────────────────────

    internal class TimerEntry
    {
        private const    int     kSamples = 60;
        private readonly float[] _samples = new float[kSamples];
        private          int     _idx;
        private          float   _sum;

        public string    Name;
        public Stopwatch Sw     = new Stopwatch();
        public float     AvgMs;
        public float     PeakMs;

        public void Begin() => Sw.Restart();

        public void End()
        {
            Sw.Stop();
            float ms     = (float)Sw.Elapsed.TotalMilliseconds;
            _sum        -= _samples[_idx];
            _samples[_idx] = ms;
            _sum        += ms;
            _idx         = (_idx + 1) % kSamples;
            AvgMs        = _sum / kSamples;
            if (ms > PeakMs) PeakMs = ms;
        }
    }
}
