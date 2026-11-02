using System;
using System.Collections.Generic;
using Il2CppLVA.Puppeteers.Variants.Humanoid;
using UnityEngine;

namespace FruitLib
{
    public static partial class FruitBodyControl
    {
        public static event Action<FruitBody> LookSampled;
        public static event Action<FruitBody, bool> LookScanEnded;
        public static bool IsLookingAround(FruitBody body) => body != null && _lookScans.ContainsKey(body.Id);

        internal static void YieldLookToPose(FruitBody body)
        {
            if (body != null) StopLookScan(body.Id, false);
        }

        private sealed class LookScan
        {
            public FruitBody Body;
            public IntPtr Creature;
            public HumanoidReprojectorOverrideLayer Layer;
            public HumanoidBoneOverride Head;
            public Quaternion Base;
            public Vector3 Forward;
            public float Start, Seconds;
            public bool Left, Right;
        }
        private static readonly Dictionary<int, LookScan> _lookScans = new Dictionary<int, LookScan>();
        private static readonly List<LookScan> _lookTick = new List<LookScan>();
        private static bool StartLookScan(FruitBody b, float seconds)
        {
            if (b == null || !b.Alive || !b.Conscious || !Upright(b) || IsWalking(b) ||
                FruitPose.IsHolding(b) || b.Pain >= 5f || !b.Limbs[FruitBodies.Head].Present) return false;
            StopLookScan(b.Id, false);
            try
            {
                var layer = b.Bind.Refs?.Animation?.Pipeline?.OverrideLayer;
                if (layer == null || layer.BoneOverridesInUse.ContainsKey(HPBoneType.Head)) return false;
                var head = layer.StartOverride(HPBoneType.Head);
                if (head == null || head.Bone == null) { if (head != null) layer.StopOverride(head); return false; }
                FruitSight.Eye(b, out _, out Vector3 forward, out _);
                _lookScans[b.Id] = new LookScan { Body = b, Creature = b.CreaturePtr, Layer = layer,
                    Head = head, Base = head.Bone.localRotation, Forward = forward, Start = Time.fixedTime,
                    Seconds = Mathf.Clamp(seconds, 2f, 12f) };
                return true;
            }
            catch (Exception e) { FruitBodies.ReportOnce("start head scan", e); return false; }
        }
        private static float ScanEase(float t) { t = Mathf.Clamp01(t); return t * t * (3f - 2f * t); }
        // Left, pause, right, pause, centre. Pauses give the physical neck time to settle.
        private static float ScanYaw(float t)
        {
            if (t < 0.2f) return -55f * ScanEase(t / 0.2f);
            if (t < 0.4f) return -55f;
            if (t < 0.65f) return Mathf.Lerp(-55f, 55f, ScanEase((t - 0.4f) / 0.25f));
            if (t < 0.85f) return 55f;
            return 55f * (1f - ScanEase((t - 0.85f) / 0.15f));
        }
        private static void TickLookScans()
        {
            _lookTick.Clear(); _lookTick.AddRange(_lookScans.Values);
            foreach (var scan in _lookTick)
            {
                var b = scan.Body;
                if (!_lookScans.TryGetValue(b.Id, out var current) || current != scan) continue;
                try
                {
                    if (b.CreaturePtr != scan.Creature || !b.Alive || !b.Conscious || !Upright(b) ||
                        b.Pain >= 5f || IsWalking(b) || FruitPose.IsHolding(b) || scan.Head.WasCollected ||
                        scan.Head.Bone == null || !b.Limbs[FruitBodies.Head].Present)
                    { StopLookScan(b.Id, false); continue; }
                    float t = (Time.fixedTime - scan.Start) / scan.Seconds;
                    var parent = scan.Head.Bone.parent;
                    Vector3 axis = parent != null ? Quaternion.Inverse(parent.rotation) * Vector3.up : Vector3.up;
                    scan.Head.Pull(Quaternion.AngleAxis(ScanYaw(t), axis) * scan.Base, ScanEase(t / 0.1f));
                    // Observe the real head, not merely a target the physical rig may not have reached.
                    if (!scan.Left && t >= 0.35f && t < 0.5f && HeadAt(scan, -55f)) { scan.Left = true; SampleLook(b); }
                    if (!scan.Right && t >= 0.8f && t < 1f && HeadAt(scan, 55f)) { scan.Right = true; SampleLook(b); }
                    if (t >= 1f) StopLookScan(b.Id, scan.Left && scan.Right);
                }
                catch (Exception e) { FruitBodies.ReportOnce("head scan", e); StopLookScan(b.Id, false); }
            }
        }
        private static bool HeadAt(LookScan scan, float degrees)
        {
            FruitSight.Eye(scan.Body, out _, out Vector3 forward, out _);
            return Vector3.Angle(forward, Quaternion.AngleAxis(degrees, Vector3.up) * scan.Forward) < 8f;
        }
        private static void SampleLook(FruitBody b)
        {
            if (LookSampled == null) return;
            foreach (Action<FruitBody> handler in LookSampled.GetInvocationList())
                try { handler(b); } catch (Exception e) { FruitBodies.ReportOnce("head scan sample", e); }
        }
        private static void StopLookScan(int id, bool completed)
        {
            if (!_lookScans.TryGetValue(id, out var scan)) return;
            _lookScans.Remove(id);
            try { if (scan.Layer != null && !scan.Layer.WasCollected && scan.Head != null && !scan.Head.WasCollected) scan.Layer.StopOverride(scan.Head); }
            catch (Exception e) { FruitBodies.ReportOnce("release head scan", e); }
            if (LookScanEnded == null) return;
            foreach (Action<FruitBody, bool> handler in LookScanEnded.GetInvocationList())
                try { handler(scan.Body, completed); } catch (Exception e) { FruitBodies.ReportOnce("head scan ended", e); }
        }
        private static void ResetLookScans()
        {
            _lookTick.Clear(); _lookTick.AddRange(_lookScans.Values);
            foreach (var scan in _lookTick) StopLookScan(scan.Body.Id, false);
            _lookTick.Clear();
        }
    }
}
