using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MelonLoader;

namespace FruitLib
{
    public static class FruitUpdateCheck
    {
        public static void Register(string modName, string currentVersion, string owner, string repo)
        {
            if (!FruitHudConfig.CheckForUpdates) return;
            Task.Run(() => CheckAsync(modName, currentVersion, owner, repo));
        }

        // ── FruitLibMod hooks (called automatically) ────────────────────────────

        internal static void RegisterPanel() =>
            FruitHud.Register("Updates", BuildPanel, order: -100);

        // ── Internals ─────────────────────────────────────────────────────────

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        private static readonly List<(string mod, string current, string latest, string url)> _outdated =
            new List<(string, string, string, string)>();

        private static async Task CheckAsync(string modName, string currentVersion, string owner, string repo)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
                req.Headers.UserAgent.ParseAdd("FruitLib-UpdateCheck");

                using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode) return; // no releases yet / offline / rate-limited — stay silent

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    string tag = ExtractTagName(body);
                    if (string.IsNullOrEmpty(tag)) return;

                    string latest = tag.TrimStart('v', 'V');
                    if (IsNewer(latest, currentVersion))
                    {
                        string url = $"https://github.com/{owner}/{repo}/releases/tag/{tag}";
                        MelonLogger.Warning($"[FruitLib] {modName} v{currentVersion} is out of date " +
                                             $"— v{latest} is available: {url}");
                        lock (_outdated) _outdated.Add((modName, currentVersion, latest, url));
                    }
                }
            }
            catch { /* never disrupt startup over a network hiccup */ }
        }

        private static void BuildPanel(HudPanel p)
        {
            lock (_outdated)
            {
                if (_outdated.Count == 0) return;
                p.Header("Update available", HudPanel.Warn);
                foreach (var e in _outdated)
                    p.Line($"{e.mod}  v{e.current} → v{e.latest}", HudPanel.Warn);
            }
        }

        private static readonly Regex TagNameRegex = new Regex("\"tag_name\"\\s*:\\s*\"([^\"]+)\"");

        private static string ExtractTagName(string json)
        {
            var m = TagNameRegex.Match(json);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static bool IsNewer(string latest, string current)
        {
            var (lMaj, lMin, lPat) = ParseVersion(latest);
            var (cMaj, cMin, cPat) = ParseVersion(current);

            if (lMaj != cMaj) return lMaj > cMaj;
            if (lMin != cMin) return lMin > cMin;
            return lPat > cPat;
        }

        private static (int major, int minor, int patch) ParseVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return (0, 0, 0);

            // drop prerelease/build metadata: "1.2.3-beta.1" / "1.2.3+build" → "1.2.3"
            int cut = version.IndexOfAny(new[] { '-', '+' });
            string core = cut >= 0 ? version.Substring(0, cut) : version;

            var parts = core.Split('.');
            int major = 0, minor = 0, patch = 0;
            if (parts.Length > 0) int.TryParse(parts[0], out major);
            if (parts.Length > 1) int.TryParse(parts[1], out minor);
            if (parts.Length > 2) int.TryParse(parts[2], out patch);
            return (major, minor, patch);
        }
    }
}
