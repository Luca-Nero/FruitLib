using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// A picture of what a body sees: one shared camera, put at the body's eyes
    /// (<see cref="FruitSight.Eye"/>), rendered with <c>Camera.Render()</c> and read straight back.
    /// About 1 ms to render and 1–2 ms to read at 384 × 256 (sight test 2026-10-10); encode it with
    /// <see cref="FruitPng"/> off the main thread.
    ///
    /// <code>
    /// byte[] rgba = FruitEyeCamera.Capture(body);        // main thread
    /// Task.Run(() => FruitPng.FromRgba(rgba, FruitEyeCamera.Width, FruitEyeCamera.Height));
    /// </code>
    ///
    /// <b>The player's orb.</b> The player is a floating camera with no body. With
    /// <see cref="ShowPlayerOrb"/> set, a glowing sphere stands in for it in every capture, and only
    /// in captures: it is switched on for the render and off again, so neither the player nor any
    /// other camera ever sees it.
    /// </summary>
    /// <remarks>Since 5.18.0.</remarks>
    public static class FruitEyeCamera
    {
        public const int Width = 384, Height = 256;
        /// <summary>Horizontal field of view, degrees.</summary>
        public static float FieldOfView = 90f;

        /// <summary>Draw the player's orb into captures.</summary>
        public static bool ShowPlayerOrb;
        /// <summary>The orb's diameter, metres.</summary>
        public static float OrbSize = 0.5f;

        /// <summary>Captures so far, the last one's main-thread time (ms), and whether capturing works at all.</summary>
        public static int Captures { get; private set; }
        public static double LastMs { get; private set; }
        public static bool Broken { get; private set; }

        private static GameObject _go, _orb;
        private static Camera _cam;
        private static RenderTexture _rt;
        private static Texture2D _read;

        /// <summary>What <paramref name="b"/> sees right now, as RGBA texels bottom row first
        /// (<see cref="Width"/> × <see cref="Height"/> × 4 bytes); null if it can't be taken.</summary>
        public static byte[] Capture(FruitBody b)
        {
            if (Broken || b == null || !b.HasPosition) return null;
            var sw = Stopwatch.StartNew();
            try
            {
                if (!Ensure()) return null;
                FruitSight.Eye(b, out Vector3 eye, out Vector3 fwd, out Vector3 up);
                _go.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(fwd, up));

                bool orb = ShowPlayerOrb && FruitBodies.HasView && EnsureOrb();
                if (orb)
                {
                    _orb.transform.position = FruitBodies.ViewPosition;
                    _orb.transform.localScale = Vector3.one * Mathf.Max(0.05f, OrbSize);
                    _orb.SetActive(true);
                }
                try { _cam.Render(); }
                finally { if (orb) _orb.SetActive(false); }

                var prev = RenderTexture.active;
                try
                {
                    RenderTexture.active = _rt;
                    _read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                }
                finally { RenderTexture.active = prev; }

                Il2CppStructArray<Color32> px = _read.GetPixels32();
                var bytes = new byte[Width * Height * 4];
                // One block copy out of the IL2CPP array: its indexer is three native calls a texel.
                Marshal.Copy(px.Pointer + 4 * IntPtr.Size, bytes, 0, bytes.Length);
                Captures++;
                LastMs = sw.Elapsed.TotalMilliseconds;
                return bytes;
            }
            catch (Exception e)
            {
                Broken = true;
                MelonLogger.Warning($"[FruitEyeCamera] capturing failed; no more pictures this session: {e.Message}");
                return null;
            }
        }

        private static bool Ensure()
        {
            if (_rt == null)
            {
                _rt = new RenderTexture(Width, Height, 24) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                _rt.Create();
            }
            if (_read == null) _read = new Texture2D(Width, Height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (_cam == null)
            {
                if (_go != null) Object.Destroy(_go);
                _go = new GameObject("FruitSight_EyeCamera");
                Object.DontDestroyOnLoad(_go);
                _cam = _go.AddComponent<Camera>();
                _cam.enabled = false;
                _cam.targetTexture = _rt;
                // Unity's field of view is vertical.
                _cam.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(FieldOfView * 0.5f * Mathf.Deg2Rad) * Height / Width) * Mathf.Rad2Deg;
                _cam.nearClipPlane = 0.05f;
                _cam.farClipPlane = 400f;
            }
            return _cam != null;
        }

        private static bool EnsureOrb()
        {
            if (_orb != null) return true;
            try
            {
                _orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _orb.name = "FruitSight_PlayerOrb";
                Object.DontDestroyOnLoad(_orb);
                var col = _orb.GetComponent<Collider>();
                if (col != null) Object.Destroy(col);
                var r = _orb.GetComponent<Renderer>();
                var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Universal Render Pipeline/Unlit");
                if (r != null && sh != null)
                {
                    // Bright and saturated: the pale 0.3 m orb of the sight test went unmentioned.
                    r.material = new Material(sh) { color = new Color(0.25f, 0.8f, 1f) };
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    r.receiveShadows = false;
                }
                _orb.SetActive(false);
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitEyeCamera] the player's orb couldn't be made: {e.Message}");
                _orb = null;
                ShowPlayerOrb = false;
                return false;
            }
        }
    }
}
