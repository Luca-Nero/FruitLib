using System;
using Il2CppAudio;
using Il2CppInfrastructure.Project.AssetsHandlers.SFX;
using Il2CppInterop.Runtime;
using Il2CppPlayer.Appearances.God.InventoryItems;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// Access to the game's own sound effects as plain audio assets.
    ///
    /// The SFX service also has play methods, but they take a long list of opaque
    /// parameters and — in practice — ignore the position you hand them, playing everything
    /// on the listener. Fetching the asset and driving your own AudioSource gives you real 3D
    /// placement and pitch control instead.
    ///
    /// It also fixes availability: assets reached this way exist from startup, whereas scanning
    /// for them with <c>Resources.FindObjectsOfTypeAll&lt;AudioClip&gt;</c> only finds one after
    /// the game has actually played it once.
    /// </summary>
    public static class FruitSfx
    {
        private static SFXPlayerService _impl;
        private static float _nextSearch;

        /// <summary>How long to wait before looking again after a failed search.</summary>
        private const float RetrySeconds = 2f;

        /// <summary>
        /// The game's sound service.
        ///
        /// <b>Not findable directly.</b> <c>SFXPlayerService</c> is a plain object built by
        /// Zenject, not a MonoBehaviour, so no scene search will ever turn one up. What can be
        /// found is something it was injected into, and the cursor tool is one.
        ///
        /// On 0.1 this came off a toolbar slot view instead, which held the service in a field
        /// of its own. 0.14 removed that field along with the setter that populated it, so the
        /// cursor tool is now the route.
        ///
        /// <b>The throttle is not optional.</b> Callers ask for a sound per event — one per
        /// bullet impact, in GunsGunsGuns' case — and a failed lookup here would otherwise run
        /// a whole-scene <c>FindObjectsOfType</c> every one of those, for as long as the tool
        /// is absent. The service does not appear mid-frame, so retrying a few times a second
        /// costs nothing and finds it just as fast.
        /// </summary>
        private static SFXPlayerService Impl
        {
            get
            {
                if (_impl != null) return _impl;
                if (Time.unscaledTime < _nextSearch) return null;
                _nextSearch = Time.unscaledTime + RetrySeconds;

                try
                {
                    // Searched afresh rather than cached off a tool reference: the tool is
                    // destroyed whenever it leaves the player's hand, so a held one does not
                    // survive a toolbar change. Inactive objects count — the toolbar keeps
                    // every tool in the scene and switches the unselected ones off.
                    var tools = Object.FindObjectsOfType<CursorGodInventoryItem>(true);
                    if (tools == null) return null;

                    foreach (var tool in tools)
                    {
                        var asInterface = tool != null ? tool.m_sfxPlayerService : null;
                        if (asInterface == null) continue;

                        _impl = asInterface.TryCast<SFXPlayerService>();
                        if (_impl != null) break;
                    }
                }
                catch { _impl = null; }

                return _impl;
            }
        }

        // ── FAudioResource: what the handlers actually hold ───────────────────────

        /// <summary>
        /// The weapon sound for <paramref name="type"/>, as the engine stores it.
        ///
        /// The Steam demo build replaced Unity's <c>AudioResource</c> with the game's own
        /// <c>FAudioResource</c>, a ScriptableObject holding a list of interchangeable takes.
        /// Prefer these over the <c>AudioClip</c> methods when you want to pick a take
        /// yourself, or play the same one twice — see <see cref="AsClip"/>.
        /// </summary>
        public static FAudioResource WeaponResource(WeaponSFXType type)
        {
            try { var h = Impl?.WeaponClips; return h?.Get(type); }
            catch { return null; }
        }

        /// <summary>The impact sound for <paramref name="type"/>, as the engine stores it.</summary>
        public static FAudioResource ImpactResource(ImpactSFXType type)
        {
            try { var h = Impl?.ImpactClips; return h?.Get(type); }
            catch { return null; }
        }

        /// <summary>The whoosh sound for <paramref name="type"/>, as the engine stores it.</summary>
        public static FAudioResource WhooshResource(WhooshSFXType type)
        {
            try { var h = Impl?.WhooshClips; return h?.Get(type); }
            catch { return null; }
        }

        /// <summary>The tool sound for <paramref name="type"/>, as the engine stores it.</summary>
        public static FAudioResource ToolsResource(ToolsSFXType type)
        {
            try { var h = Impl?.ToolsClips; return h?.Get(type); }
            catch { return null; }
        }

        // ── AudioClip: one take, ready to hand to an AudioSource ───────────────────

        public static AudioClip Weapon(WeaponSFXType type) => AsClip(WeaponResource(type));

        public static AudioClip Impact(ImpactSFXType type) => AsClip(ImpactResource(type));

        public static AudioClip Whoosh(WhooshSFXType type) => AsClip(WhooshResource(type));

        public static AudioClip Tools(ToolsSFXType type) => AsClip(ToolsResource(type));

        /// <summary>
        /// One playable take out of <paramref name="res"/>, or null if it holds none.
        ///
        /// Sounds with variants — gore, most organic impacts — used to be
        /// <c>AudioRandomContainer</c>s, which are not <c>AudioClip</c>s, so this came back
        /// null for them on 0.14 while their neighbours in the same category worked. The
        /// Steam demo build made every sound an <c>FAudioResource</c> over a clip list, so
        /// those now return a clip like any other; <c>Pick()</c> chooses the take.
        ///
        /// Because the take is chosen per call, a caller that wants the same one twice should
        /// hold the returned clip rather than call again, and one that wants a specific take
        /// should read <c>Clips</c> off the matching <c>*Resource</c> method.
        /// </summary>
        public static AudioClip AsClip(FAudioResource res)
        {
            if (res == null) return null;
            try { return res.Pick(); }
            catch { return null; }
        }

        /// <summary>
        /// Plays one of the menu's own interface sounds.
        ///
        /// Through the service rather than an AudioSource of our own, so it lands on the
        /// game's mixer and obeys the master volume the player set. A mod menu that ignored
        /// the audio settings would be the one part of it that still felt bolted on.
        /// </summary>
        public static void PlayUI(UISFXType type)
        {
            try
            {
                var impl = Impl;
                if (impl == null) return;

                // A zeroed params object, not null. The signature defaults the parameter to
                // null and the game's own callers evidently never take it up on that - passing
                // null throws a NullReferenceException inside Play. Allocating one leaves
                // every override unset: no pitch shift, no distance, and a spatial blend of
                // zero, which is 2D. Which is what a button press wants anyway.
                SFXPlayParams asAuthored = new SFXPlayParams();
                impl.Play(type, ref asAuthored);
            }
            catch (Exception e) { MelonLogger.Warning($"[FruitLib] UI sound {type} failed: {e.Message}"); }
        }

        internal static void Reset()
        {
            _impl = null;
            _nextSearch = 0f;   // a new scene should look immediately, not wait out the throttle
        }
    }
}
