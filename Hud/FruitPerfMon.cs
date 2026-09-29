using MelonLoader;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// A per-mod debug overlay: frame rate and load on top, then each mod's own counters,
    /// timers and live values, under that mod's name.
    ///
    /// <code>
    /// var perf = FruitPerfMon.For("BombsAway");
    /// perf.Counter("Ordnance", () => _grenades.Count);
    /// perf.Value("Hologram", () => _holoRunning ? _holoFor.ToString() : "off");
    /// using (perf.Time("Blast")) Detonate();
    /// </code>
    ///
    /// F11 shows it. Shift+F11 steps through the views: every mod's counters at once, then
    /// each mod in full. Ctrl+F11 resets the peaks of what is on screen. Getters only run for
    /// the view on screen, so a hidden overlay costs nothing and peaks are only tracked while
    /// shown.
    /// </summary>
    public static class FruitPerfMon
    {
        /// <summary>0..1: how hard the game is struggling right now. See the docs for the two signals.</summary>
        public static float PressureLevel { get; private set; }

        /// <summary>The ~300-frame average frame rate, a baseline that doesn't jump with one spike.</summary>
        public static float LongFps => _longFps.Fps;

        /// <summary>The frame rate below which <see cref="PressureLevel"/> starts to rise.</summary>
        public static float TargetFps => FruitLibConfig.PerfTargetFps;

        /// <summary>A drop against the long average smaller than this doesn't count as pressure.</summary>
        public static float PressureDrop = 0.15f;

        /// <summary>
        /// The overlay section for one mod, created on first ask. Use the name players see your
        /// mod by; asking again with the same name returns the same handle.
        /// </summary>
        public static ModPerf For(string modName)
        {
            if (string.IsNullOrEmpty(modName)) modName = "Unnamed";
            foreach (var m in _mods) if (m.Name == modName) return m;

            var mod = new ModPerf(modName);
            _mods.Add(mod);
            _mods.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return mod;
        }

        // ── Pre-5.0 API: one flat list for everyone ──────────────────────────────────

        [Obsolete("Use FruitPerfMon.For(\"YourMod\").Counter(name, getter). This files the counter under the calling assembly's name.")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void RegisterCounter(string name, Func<int> getter)
            => ForAssembly(Assembly.GetCallingAssembly()).Counter(name, getter);

        [Obsolete("Use FruitPerfMon.For(\"YourMod\").Begin(name), or .Time(name) in a using block.")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Begin(string name) => ForAssembly(Assembly.GetCallingAssembly()).Begin(name);

        [Obsolete("Use FruitPerfMon.For(\"YourMod\").End(name).")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void End(string name) => ForAssembly(Assembly.GetCallingAssembly()).End(name);

        private static ModPerf ForAssembly(Assembly a)
        {
            string name;
            try { name = a?.GetName().Name; } catch { name = null; }
            return For(name);
        }

        // ── Internals ─────────────────────────────────────────────────────────────────

        private static readonly List<ModPerf> _mods = new List<ModPerf>();
        private static HudHandle _panel;

        /// <summary>-1 = every mod's counters, otherwise an index into _mods.</summary>
        private static int _view = -1;

        private static bool Visible => _panel != null && _panel.Visible;

        internal static ModPerf Lib => For("FruitLib");

        internal static void RegisterPanel()
        {
            _panel = FruitHud.Register("FruitPerfMon", BuildPanel,
                                       order: 100, corner: HudCorner.TopRight,
                                       minWidth: 310f, fontSize: 11);
            _panel.Visible = false;

            Lib.Counter("Projectiles", () => FruitProjectiles.Live.Count);
            Lib.Counter("Ejecta", () => FruitEjecta.ChunkCount);
            Lib.Counter("Blood decals", () => FruitEjecta.DecalCount);
        }

        internal static void Tick()
        {
            HandleKeys();

            float dt = Time.unscaledDeltaTime;
            _shortFps.Push(dt);
            _longFps.Push(dt);

            float sf   = _shortFps.Fps;
            float lf   = _longFps.Fps;
            float absP = TargetFps > 0f ? Mathf.Clamp01(1f - sf / TargetFps) : 0f;
            float drop = lf > 0f ? (lf - sf) / lf : 0f;
            float relP = Mathf.Clamp01((drop - PressureDrop) / Mathf.Max(PressureDrop, 0.001f));
            PressureLevel = Mathf.Max(absP, relP);

            if (!Visible) return;
            if (_view < 0) foreach (var m in _mods) m.PollCounters();
            else if (_view < _mods.Count) _mods[_view].PollCounters();
        }

        private static void HandleKeys()
        {
            var key = FruitLibConfig.PerfKey;
            if (_panel == null || key == KeyCode.None || FruitMenu.IsInputSuppressed || !Input.GetKeyDown(key)) return;

            bool shift = Input.GetKey(KeyCode.LeftShift)   || Input.GetKey(KeyCode.RightShift);
            bool ctrl  = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (ctrl)
            {
                if (_view < 0) foreach (var m in _mods) m.ResetPeaks();
                else if (_view < _mods.Count) _mods[_view].ResetPeaks();
            }
            else if (shift)
            {
                // Overview, then each mod, then back round. Showing the overlay if it was hidden.
                _panel.Visible = true;
                _view = _view + 1 >= _mods.Count ? -1 : _view + 1;
            }
            else _panel.Visible = !_panel.Visible;
        }

        private static void BuildPanel(HudPanel p)
        {
            if (_view >= _mods.Count) _view = -1;

            string view = _view < 0 ? "all mods" : _mods[_view].Name;
            p.Line($"FPS {_shortFps.Fps,6:F1}  ft {_shortFps.FtMs,5:F2} ms  P {PressureLevel * 100f,4:F0}%");
            p.Line($"[{FruitLibConfig.PerfKey}] {view}   shift: next view   ctrl: reset peaks", HudPanel.Dim);

            if (_view < 0)
            {
                foreach (var m in _mods)
                {
                    if (!m.HasCounters) continue;
                    p.Separator();
                    p.Header(m.Name);
                    m.DrawCounters(p);
                }
                return;
            }

            var mod = _mods[_view];
            p.Separator();
            p.Header(mod.Name);
            if (!mod.DrawAll(p)) p.Line("nothing registered", HudPanel.Dim);
        }

        // ── Rolling FPS tracker ───────────────────────────────────────────────────────

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

    /// <summary>One mod's section of the <see cref="FruitPerfMon"/> overlay. Get it from <see cref="FruitPerfMon.For"/>.</summary>
    public sealed class ModPerf
    {
        public string Name { get; }

        private readonly List<CounterEntry>            _counters = new List<CounterEntry>();
        private readonly List<ValueEntry>              _values   = new List<ValueEntry>();
        private readonly Dictionary<string, TimerEntry> _timers  = new Dictionary<string, TimerEntry>();
        private readonly List<TimerEntry>              _timerOrder = new List<TimerEntry>();

        internal ModPerf(string name) => Name = name;

        /// <summary>
        /// A number shown with its running peak, e.g. live projectiles. Registering a name again
        /// replaces its getter. Runs once a frame while this mod's counters are on screen, so
        /// return a field, not a scene query.
        /// </summary>
        public ModPerf Counter(string name, Func<int> getter)
        {
            if (string.IsNullOrEmpty(name) || getter == null) return this;
            foreach (var c in _counters) if (c.Name == name) { c.Getter = getter; return this; }
            _counters.Add(new CounterEntry { Name = name, Getter = getter });
            return this;
        }

        /// <summary>
        /// Any state worth watching, as text: the selected mode, a lock target, which fallback a
        /// lookup took. Shown only in this mod's own view, read once a frame while it is.
        /// </summary>
        public ModPerf Value(string name, Func<string> getter)
        {
            if (string.IsNullOrEmpty(name) || getter == null) return this;
            foreach (var v in _values) if (v.Name == name) { v.Getter = getter; return this; }
            _values.Add(new ValueEntry { Name = name, Getter = getter });
            return this;
        }

        /// <summary>Starts timing a section. Pair with <see cref="End"/>, or use <see cref="Time"/>.</summary>
        public void Begin(string name) => Timer(name).Begin();

        /// <summary>Ends a section started with <see cref="Begin"/>. Without one it is ignored.</summary>
        public void End(string name)
        {
            if (name != null && _timers.TryGetValue(name, out var t)) t.End();
        }

        /// <summary>Times the enclosing using block: <c>using (perf.Time("Blast")) { ... }</c>.</summary>
        public PerfTiming Time(string name) => new PerfTiming(Timer(name));

        public void ResetPeaks()
        {
            foreach (var c in _counters)   c.Peak = 0;
            foreach (var t in _timerOrder) t.PeakMs = 0f;
        }

        private TimerEntry Timer(string name)
        {
            name ??= "";
            if (!_timers.TryGetValue(name, out var t))
            {
                _timers[name] = t = new TimerEntry { Name = name };
                _timerOrder.Add(t);
            }
            return t;
        }

        // ── Drawing (FruitPerfMon) ────────────────────────────────────────────────────

        internal bool HasCounters => _counters.Count > 0;

        internal void PollCounters()
        {
            foreach (var c in _counters) c.Poll(Name);
        }

        internal void DrawCounters(HudPanel p)
        {
            foreach (var c in _counters)
                p.Line(c.Failed ? $"{c.Name,-22} threw" : $"{c.Name,-22}{c.Current,6}   pk {c.Peak,6}",
                       c.Failed ? HudPanel.Bad : HudPanel.Normal);
        }

        /// <summary>Everything this mod registered. False if that is nothing.</summary>
        internal bool DrawAll(HudPanel p)
        {
            DrawCounters(p);

            if (_timerOrder.Count > 0)
            {
                if (_counters.Count > 0) p.Separator();
                foreach (var t in _timerOrder)
                    p.Line($"{t.Name,-22}{t.AvgMs,5:F2} ms   pk {t.PeakMs,5:F2} ms");
            }

            if (_values.Count > 0)
            {
                if (_counters.Count > 0 || _timerOrder.Count > 0) p.Separator();
                foreach (var v in _values) p.Line($"{v.Name,-22}{v.Read(Name)}", HudPanel.Dim);
            }

            return _counters.Count + _timerOrder.Count + _values.Count > 0;
        }
    }

    /// <summary>What <see cref="ModPerf.Time"/> returns; disposing it ends the section.</summary>
    public readonly struct PerfTiming : IDisposable
    {
        private readonly TimerEntry _timer;
        internal PerfTiming(TimerEntry timer) { _timer = timer; timer.Begin(); }
        public void Dispose() => _timer?.End();
    }

    // ── Entries ───────────────────────────────────────────────────────────────────────

    internal sealed class CounterEntry
    {
        public string    Name;
        public Func<int> Getter;
        public int       Current;
        public int       Peak;
        public bool      Failed;

        /// <summary>A getter that throws is shown as such and skipped, rather than taking the
        /// other counters down with it.</summary>
        public void Poll(string mod)
        {
            if (Failed) return;
            try { Current = Getter(); }
            catch (Exception e)
            {
                Failed = true;
                MelonLogger.Warning($"[FruitPerfMon] {mod} counter '{Name}' threw and was disabled: {e.Message}");
                return;
            }
            if (Current > Peak) Peak = Current;
        }
    }

    internal sealed class ValueEntry
    {
        public string       Name;
        public Func<string> Getter;
        private bool        _failed;

        public string Read(string mod)
        {
            if (_failed) return "threw";
            try { return Getter() ?? "null"; }
            catch (Exception e)
            {
                _failed = true;
                MelonLogger.Warning($"[FruitPerfMon] {mod} value '{Name}' threw and was disabled: {e.Message}");
                return "threw";
            }
        }
    }

    internal sealed class TimerEntry
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
            if (!Sw.IsRunning) return;
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
