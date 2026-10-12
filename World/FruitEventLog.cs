using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// FruitLib's diagnostics for <see cref="FruitEvents"/> and <see cref="FruitBodies"/>: a log
    /// file (UserData/FruitEvents.log, FruitLibConfig.EventLog) with every event and a body
    /// snapshot every few seconds, and a HUD panel (FruitLibConfig.EventPanel) with the newest
    /// events and one line per body.
    /// </summary>
    internal static class FruitEventLog
    {
        private static StreamWriter _writer;
        private static bool _failed;
        private static float _nextSnapshot;
        private static HudHandle _panel;

        private static string LogPath => Path.Combine(FruitPaths.UserData, "FruitEvents.log");

        internal static void Init()
        {
            _panel = FruitHud.Register("World events", BuildPanel, order: 95, minWidth: 420f);
        }

        internal static void Write(FruitEvent e)
        {
            if (!FruitLibConfig.EventLog) return;
            var w = Writer();
            if (w == null) return;
            try
            {
                w.WriteLine(e.ToString());
                if (!string.IsNullOrEmpty(e.Detail)) w.WriteLine("           " + e.Detail);
            }
            catch (Exception ex) { Fail(ex); }
        }

        internal static void Tick()
        {
            if (_panel != null) _panel.Visible = FruitLibConfig.EventPanel;

            if (!FruitLibConfig.EventLog)
            {
                Close();
                return;
            }

            float now = Time.time;
            if (now >= _nextSnapshot && FruitBodies.All.Count > 0)
            {
                _nextSnapshot = now + Mathf.Max(1f, FruitLibConfig.EventLogSnapshotSeconds);
                var w = Writer();
                if (w != null)
                {
                    try
                    {
                        w.WriteLine($"[{now,8:0.00}] -- bodies --");
                        foreach (var b in FruitBodies.All)
                        {
                            foreach (var line in b.Describe().Split('\n'))
                                if (line.Length > 0) w.WriteLine("           " + line);
                            w.WriteLine($"           state={b.StateName} posture={b.Posture} grounded={b.Grounded} at=({b.Position.x:0.0},{b.Position.y:0.0},{b.Position.z:0.0})");
                        }
                    }
                    catch (Exception ex) { Fail(ex); }
                }
            }

            try { _writer?.Flush(); } catch (Exception ex) { Fail(ex); }
        }

        private static StreamWriter Writer()
        {
            if (_writer != null || _failed) return _writer;
            try
            {
                _writer = new StreamWriter(LogPath, append: false, Encoding.UTF8);
                _writer.WriteLine($"# FruitEvents log - FruitLib {FruitVersion.Current}, started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _writer.WriteLine("# [time] Source.Kind body#id @(position) near #body:distance - text");
                MelonLogger.Msg($"[FruitEvents] logging world events to {LogPath}");
            }
            catch (Exception ex) { Fail(ex); }
            return _writer;
        }

        private static void Fail(Exception ex)
        {
            if (_failed) return;
            _failed = true;
            MelonLogger.Warning($"[FruitEvents] event log disabled for this session: {ex.Message}");
            Close();
        }

        private static void Close()
        {
            if (_writer == null) return;
            try { _writer.Dispose(); } catch { }
            _writer = null;
        }

        internal static void Shutdown() => Close();

        // ── Panel ──

        private static readonly List<FruitEvent> _recent = new List<FruitEvent>(8);

        private static void BuildPanel(HudPanel p)
        {
            p.Header("World events");
            var bodies = FruitBodies.All;
            if (bodies.Count == 0) p.Line("no bodies tracked", HudPanel.Dim);
            foreach (var b in bodies)
            {
                var c = !b.Alive ? HudPanel.Dim : !b.Conscious ? HudPanel.Warn : b.Pain > 50f ? HudPanel.Bad : HudPanel.Normal;
                string worst = b.WorstLimb != null ? $" | {b.WorstLimb.Name} {b.WorstLimb.Pain:0}" : "";
                p.Line($"#{b.Id} {b.Behaviour} {b.Posture} | cog {b.Cognition:0} pain {b.Pain:0} blood {b.Blood:0}%{worst}", c);
            }
            p.Separator();

            _recent.Clear();
            FruitEvents.CopySince(Math.Max(0, FruitEvents.LastSeq - 8), _recent);
            float now = Time.time;
            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                var e = _recent[i];
                string who = e.Body != 0 ? $" #{e.Body}" : e.Near.Count > 0 ? $" near #{e.Near[0].Body} {e.Near[0].Distance:0.0}m" : "";
                p.Line($"{now - e.Time,4:0}s {e.Source}.{e.Kind}{who}", now - e.Time < 3f ? HudPanel.Good : HudPanel.Dim);
            }
        }
    }
}
