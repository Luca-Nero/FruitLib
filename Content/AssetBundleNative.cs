using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Bindings;

namespace FruitLib
{
    /// <summary>
    /// Direct calls into the AssetBundle icalls UnityPlayer.dll still registers, for the methods
    /// the game stripped.
    ///
    /// MelonLoader regenerates the stripped managed methods from Unity's reference assemblies,
    /// but in Unity 6 those bodies pin their string/array arguments through
    /// ReadOnlySpan.GetPinnableReference - itself stripped from this game - so every call dies
    /// with "Method not found". Here the marshalling is done by hand instead, matching what the
    /// game's surviving sibling LoadAsset_Internal does (dump.cs, UnityEngine.Bindings):
    ///
    ///   in:  a string or array goes as ManagedSpanWrapper { void* begin; int length }, by ref.
    ///   out: an Object comes back as a GCHandle, unwrapped via Unmarshal.FromIntPtrUnsafe(h).Target.
    ///   self: an instance method takes the object's native pointer (Object.m_CachedPtr).
    ///
    /// Signatures are Unity 6000.3's AssetBundle.bindings.cs. A Unity upgrade can change them.
    /// </summary>
    internal static class AssetBundleNative
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SpanWrapper
        {
            public IntPtr Begin;
            public int Length;
        }

        private delegate IntPtr LoadFromFileFn(ref SpanWrapper path, uint crc, ulong offset);
        private delegate IntPtr LoadFromMemoryFn(ref SpanWrapper binary, uint crc);
        private delegate IntPtr GetAllAssetNamesFn(IntPtr self);
        private delegate void UnloadFn(IntPtr self, [MarshalAs(UnmanagedType.U1)] bool unloadAllLoadedObjects);

        private static LoadFromFileFn _loadFromFile;
        private static LoadFromMemoryFn _loadFromMemory;
        private static GetAllAssetNamesFn _getAllAssetNames;
        private static UnloadFn _unload;

        private static T ICall<T>(ref T cache, string name) where T : Delegate
            => cache ??= IL2CPP.ResolveICall<T>("UnityEngine.AssetBundle::" + name)
                         ?? throw new MissingMethodException($"icall UnityEngine.AssetBundle::{name} not registered");

        public static AssetBundle LoadFromFile(string path)
        {
            var fn = ICall(ref _loadFromFile, "LoadFromFile_Internal_Injected");
            var pin = GCHandle.Alloc(path, GCHandleType.Pinned);
            try
            {
                var span = new SpanWrapper { Begin = pin.AddrOfPinnedObject(), Length = path.Length };
                return Unwrap(fn(ref span, 0, 0));
            }
            finally { pin.Free(); }
        }

        public static AssetBundle LoadFromMemory(byte[] bytes)
        {
            var fn = ICall(ref _loadFromMemory, "LoadFromMemory_Internal_Injected");
            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                var span = new SpanWrapper { Begin = pin.AddrOfPinnedObject(), Length = bytes.Length };
                return Unwrap(fn(ref span, 0));
            }
            finally { pin.Free(); }
        }

        // ── Async ───────────────────────────────────────────────────────────────
        //
        // An AsyncOperation is not a UnityEngine.Object. The icall hands back its *native*
        // pointer (Unity's managed side wraps it in `new AssetBundleCreateRequest(ptr)`), not a
        // GCHandle, and that same pointer is the `self` of its own icalls. With no managed
        // wrapper to finalize it, it is released here by InternalDestroy once read.

        private delegate IntPtr LoadFromFileAsyncFn(ref SpanWrapper path, uint crc, ulong offset);
        [return: MarshalAs(UnmanagedType.U1)]
        private delegate bool IsDoneFn(IntPtr op);
        private delegate float ProgressFn(IntPtr op);
        private delegate IntPtr GetAssetBundleFn(IntPtr request);
        private delegate void DestroyFn(IntPtr op);

        private static LoadFromFileAsyncFn _loadFromFileAsync;
        private static IsDoneFn _isDone;
        private static ProgressFn _progress;
        private static GetAssetBundleFn _requestBundle;
        private static DestroyFn _destroyOp;

        private static T ICallAt<T>(ref T cache, string fullName) where T : Delegate
            => cache ??= IL2CPP.ResolveICall<T>(fullName)
                         ?? throw new MissingMethodException($"icall {fullName} not registered");

        /// <summary>Starts an async load. Poll with IsDone, then EndLoadFromFileAsync exactly once.</summary>
        public static IntPtr BeginLoadFromFileAsync(string path)
        {
            var fn = ICall(ref _loadFromFileAsync, "LoadFromFileAsync_Internal_Injected");
            var pin = GCHandle.Alloc(path, GCHandleType.Pinned);
            try
            {
                var span = new SpanWrapper { Begin = pin.AddrOfPinnedObject(), Length = path.Length };
                return fn(ref span, 0, 0);
            }
            finally { pin.Free(); }
        }

        public static bool IsDone(IntPtr op)
            => ICallAt(ref _isDone, "UnityEngine.AsyncOperation::get_isDone_Injected")(op);

        public static float Progress(IntPtr op)
            => ICallAt(ref _progress, "UnityEngine.AsyncOperation::get_progress_Injected")(op);

        /// <summary>Reads the finished request's bundle and releases the request.</summary>
        public static AssetBundle EndLoadFromFileAsync(IntPtr op)
        {
            try { return Unwrap(ICallAt(ref _requestBundle, "UnityEngine.AssetBundleCreateRequest::get_assetBundle_Injected")(op)); }
            finally { ICallAt(ref _destroyOp, "UnityEngine.AsyncOperation::InternalDestroy")(op); }
        }

        public static string[] GetAllAssetNames(AssetBundle bundle)
        {
            var fn = ICall(ref _getAllAssetNames, "GetAllAssetNames_Injected");
            IntPtr arr = fn(Self(bundle));
            if (arr == IntPtr.Zero) return Array.Empty<string>();

            var raw = new Il2CppStringArray(arr);
            var names = new string[raw.Length];
            for (int i = 0; i < raw.Length; i++) names[i] = raw[i];
            return names;
        }

        public static void Unload(AssetBundle bundle, bool unloadAllLoadedObjects)
            => ICall(ref _unload, "Unload_Injected")(Self(bundle), unloadAllLoadedObjects);

        private static IntPtr Self(AssetBundle bundle)
        {
            IntPtr p = bundle != null ? bundle.m_CachedPtr : IntPtr.Zero;
            if (p == IntPtr.Zero) throw new ObjectDisposedException("AssetBundle", "bundle is null or already unloaded");
            return p;
        }

        private static AssetBundle Unwrap(IntPtr gcHandle)
        {
            if (gcHandle == IntPtr.Zero) return null;   // native load failed; Unity logs why
            var target = Unmarshal.FromIntPtrUnsafe(gcHandle).Target;
            return target?.TryCast<AssetBundle>();
        }
    }
}
