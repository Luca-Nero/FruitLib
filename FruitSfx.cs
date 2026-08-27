using Il2Cpp;
using Il2CppInfrastructure.Project.Installers.AssetsHandlers.SFX;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Audio;

namespace FruitLib
{
    /// <summary>
    /// Access to the game's own sound effects as plain <see cref="AudioClip"/>s.
    ///
    /// The SFX service also has play methods (<c>ipf/ipg/iph/ipi</c>), but they take a long list
    /// of opaque parameters and — in practice — ignore the position you hand them, playing
    /// everything on the listener. Fetching the clip and driving your own AudioSource gives you
    /// real 3D placement and pitch control instead of guessing at an obfuscated signature.
    ///
    /// It also fixes availability: clips reached this way exist from startup, whereas scanning
    /// for them with <c>Resources.FindObjectsOfTypeAll&lt;AudioClip&gt;</c> only finds one after
    /// the game has actually played it once.
    /// </summary>
    public static class FruitSfx
    {
        private static bfe _impl;

        // bfe is the concrete service behind the bfc interface; its handlers each map an enum
        // to an AudioResource (svb weapon, svc impact, svd whoosh, sve tools).
        private static bfe Impl
        {
            get
            {
                if (_impl != null) return _impl;

                var svc = FruitToolbar.Sfx;
                if (svc == null) return null;

                try { _impl = svc.TryCast<bfe>(); }
                catch { _impl = null; }

                return _impl;
            }
        }

        public static AudioClip Weapon(WeaponSFXType type)
        {
            try { var h = Impl?.svb; return h == null ? null : AsClip(h.eha(type)); }
            catch { return null; }
        }

        public static AudioClip Impact(ImpactSFXType type)
        {
            try { var h = Impl?.svc; return h == null ? null : AsClip(h.eha(type)); }
            catch { return null; }
        }

        public static AudioClip Whoosh(WhooshSFXType type)
        {
            try { var h = Impl?.svd; return h == null ? null : AsClip(h.eha(type)); }
            catch { return null; }
        }

        public static AudioClip Tools(ToolsSFXType type)
        {
            try { var h = Impl?.sve; return h == null ? null : AsClip(h.eha(type)); }
            catch { return null; }
        }

        // AudioClip derives from AudioResource, so the handler's return value is the clip.
        private static AudioClip AsClip(AudioResource res)
        {
            if (res == null) return null;
            try { return res.TryCast<AudioClip>(); }
            catch { return null; }
        }

        internal static void Reset() => _impl = null;
    }
}
