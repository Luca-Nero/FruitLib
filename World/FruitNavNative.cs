using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// Direct calls into the navmesh icalls UnityPlayer.dll still registers. The game kept only
    /// NavMesh.CalculatePath / SamplePosition and NavMeshPath; NavMeshData, NavMeshBuilder,
    /// NavMeshBuildSource and NavMeshBuildSettings are gone from its metadata, so there is no
    /// managed way to make or add navmesh data. The engine side is all still there (Spike S2,
    /// 2026-10-10: 197 AI icalls registered).
    ///
    /// What the native side expects, from UnityPlayer.dll (Ghidra, icall table at RVA 0x1d0a230):
    ///
    /// - <b>UpdateNavMeshDataListInternal_Injected</b> (0x18011d430) takes the NavMeshData's native
    ///   pointer, the 64-byte settings by ref, the sources as a managed <c>List&lt;T&gt;</c>, and the
    ///   local bounds by ref. It reads the list as raw memory, <c>_items</c> at +0x10 and
    ///   <c>_size</c> at +0x18, and the elements from the array's data at +0x20, with no check of
    ///   the element type. So any IL2CPP list whose items array holds <c>_size</c> 96-byte
    ///   sources will do; see <see cref="SourceList"/>. It always returns true.
    /// - <b>CollectSourcesInternal_Injected</b> (0x18011d250) fills an out BlittableArrayWrapper:
    ///   if the result fits the caller's buffer it is copied in and <c>size</c> set; if not, data
    ///   becomes a native-owned block (flag 3) or a native pointer (flag 2).
    /// - <b>NavMeshData::Internal_Create</b> (0x180120320) makes the native object and binds it to
    ///   the managed wrapper passed in, which only needs UnityEngine.Object's layout
    ///   (m_CachedPtr at +0x10): a plain <c>new UnityEngine.Object()</c> serves.
    /// - <b>CalculateTriangulation_Injected</b> (0x180120bf0) writes three IL2CPP arrays
    ///   (vertices, indices, areas) into the out struct.
    ///
    /// Layouts are Unity 6000.3.18f1's (the editor's UnityEngine.AIModule): settings 15 four-byte
    /// fields plus a debug byte (64 bytes), a source a Matrix4x4, a Vector3 and five ints (96).
    /// A Unity upgrade can change them.
    /// </summary>
    /// <remarks>Since 5.19.0 (spike S2).</remarks>
    internal static unsafe class FruitNavNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct BuildSettings
        {
            public int   AgentTypeID;
            public float AgentRadius;
            public float AgentHeight;
            public float AgentSlope;
            public float AgentClimb;
            public float LedgeDropHeight;
            public float MaxJumpAcrossDistance;
            public float MinRegionArea;
            public int   OverrideVoxelSize;
            public float VoxelSize;
            public int   OverrideTileSize;
            public int   TileSize;
            public int   BuildHeightMesh;
            public uint  MaxJobWorkers;
            public int   PreserveTilesOutsideBounds;
            public byte  DebugFlags;

            public override string ToString() =>
                $"agentType {AgentTypeID}, radius {AgentRadius:0.###}, height {AgentHeight:0.##}, slope {AgentSlope:0.#}, " +
                $"climb {AgentClimb:0.###}, ledge drop {LedgeDropHeight:0.##}, jump {MaxJumpAcrossDistance:0.##}, " +
                $"min region {MinRegionArea:0.##}, voxel {(OverrideVoxelSize != 0 ? VoxelSize.ToString("0.###") : "auto")}, " +
                $"tile {(OverrideTileSize != 0 ? TileSize.ToString() : "auto")}, height mesh {BuildHeightMesh}, " +
                $"workers {MaxJobWorkers}, keep outside {PreserveTilesOutsideBounds}";
        }

        public enum Shape { Mesh = 0, Terrain = 1, Box = 2, Sphere = 3, Capsule = 4, ModifierBox = 5 }

        [StructLayout(LayoutKind.Sequential)]
        public struct BuildSource
        {
            public Matrix4x4 Transform;
            public Vector3   Size;
            public int       Shape;
            public int       Area;
            public int       InstanceID;
            public int       ComponentID;
            public int       GenerateLinks;
        }

        public const int SourceBytes = 96;

        /// <summary>False if this build's struct sizes differ from what the native side reads.</summary>
        public static bool LayoutOk => sizeof(BuildSource) == SourceBytes && sizeof(BuildSettings) == 64 && sizeof(Vector3) == 12;

        [StructLayout(LayoutKind.Sequential)]
        public struct Hit
        {
            public Vector3 Position;
            public Vector3 Normal;
            public float   Distance;
            public int     Mask;
            public int     HitFlag;
            public bool IsHit => HitFlag != 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpanWrapper { public IntPtr Begin; public int Length; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ArrayWrapper { public IntPtr Data; public int Size; public int Flags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Triangulation { public IntPtr Vertices, Indices, Areas; }

        private delegate void CreateFn(IntPtr mono, int agentTypeID);
        [return: MarshalAs(UnmanagedType.U1)]
        private delegate bool UpdateFn(IntPtr data, ref BuildSettings settings, IntPtr sources, ref Bounds localBounds);
        private delegate IntPtr UpdateAsyncFn(IntPtr data, ref BuildSettings settings, IntPtr sources, ref Bounds localBounds);
        private delegate int AddFn(IntPtr data);
        private delegate void RemoveFn(int handle);
        private delegate int CountFn();
        private delegate void SettingsFn(int id, out BuildSettings ret);
        private delegate void CollectFn(int layerMask, ref Bounds bounds, IntPtr root,
            [MarshalAs(UnmanagedType.U1)] bool useBounds, int geometry, int defaultArea,
            [MarshalAs(UnmanagedType.U1)] bool generateLinks, ref SpanWrapper markups,
            [MarshalAs(UnmanagedType.U1)] bool onlyMarked, ref ArrayWrapper ret);
        private delegate void TriangulateFn(out Triangulation ret);
        [return: MarshalAs(UnmanagedType.U1)]
        private delegate bool RaycastFn(ref Vector3 from, ref Vector3 to, out Hit hit, int areaMask);
        [return: MarshalAs(UnmanagedType.U1)]
        private delegate bool EdgeFn(ref Vector3 from, out Hit hit, int areaMask);
        [return: MarshalAs(UnmanagedType.U1)]
        private delegate bool IsDoneFn(IntPtr op);
        private delegate void DestroyOpFn(IntPtr op);
        private delegate void FreeFn(IntPtr p);

        private static CreateFn _create;
        private static UpdateFn _update;
        private static UpdateAsyncFn _updateAsync;
        private static AddFn _add;
        private static RemoveFn _remove;
        private static CountFn _settingsCount;
        private static SettingsFn _settingsById;
        private static CollectFn _collect;
        private static TriangulateFn _triangulate;
        private static RaycastFn _raycast;
        private static EdgeFn _edge;
        private static IsDoneFn _isDone;
        private static DestroyOpFn _destroyOp;
        private static FreeFn _freeOwned;

        private static T ICall<T>(ref T cache, string name) where T : Delegate
            => cache ??= IL2CPP.ResolveICall<T>(name) ?? throw new MissingMethodException($"icall {name} not registered");

        /// <summary>Every icall this class uses, for a probe to check up front.</summary>
        public static readonly string[] Names =
        {
            "UnityEngine.AI.NavMeshData::Internal_Create",
            "UnityEngine.AI.NavMeshBuilder::UpdateNavMeshDataListInternal_Injected",
            "UnityEngine.AI.NavMeshBuilder::UpdateNavMeshDataAsyncListInternal_Injected",
            "UnityEngine.AI.NavMeshBuilder::CollectSourcesInternal_Injected",
            "UnityEngine.AI.NavMesh::AddNavMeshDataInternal_Injected",
            "UnityEngine.AI.NavMesh::RemoveNavMeshDataInternal",
            "UnityEngine.AI.NavMesh::GetSettingsCount",
            "UnityEngine.AI.NavMesh::GetSettingsByID_Injected",
            "UnityEngine.AI.NavMesh::CalculateTriangulation_Injected",
            "UnityEngine.AI.NavMesh::Raycast_Injected",
            "UnityEngine.AI.NavMesh::FindClosestEdge_Injected",
            "UnityEngine.AsyncOperation::get_isDone_Injected",
            "UnityEngine.AsyncOperation::InternalDestroy",
            "UnityEngine.Bindings.BindingsAllocator::FreeNativeOwnedMemory",
        };

        /// <summary>True if the icall resolves (resolving has no side effects).</summary>
        public static bool Has(string name)
        {
            try { return IL2CPP.il2cpp_resolve_icall(name) != IntPtr.Zero; }
            catch { return false; }
        }

        // ── Settings ──

        public static int SettingsCount() => ICall(ref _settingsCount, "UnityEngine.AI.NavMesh::GetSettingsCount")();

        /// <summary>The project's agent settings for an agent type (0 = Humanoid).</summary>
        public static BuildSettings SettingsById(int agentTypeId)
        {
            ICall(ref _settingsById, "UnityEngine.AI.NavMesh::GetSettingsByID_Injected")(agentTypeId, out var s);
            return s;
        }

        // ── Data ──

        /// <summary>
        /// A NavMeshData made at runtime. <see cref="Wrapper"/> is the managed object the native
        /// one is bound to; keep it alive as long as the data is used, destroy it to free both.
        /// </summary>
        public sealed class Data
        {
            public Object Wrapper;
            public IntPtr Native => Wrapper != null && !Wrapper.WasCollected ? Wrapper.m_CachedPtr : IntPtr.Zero;
            public int Handle = -1;
            public bool Added => Handle >= 0;
        }

        public static Data CreateData(int agentTypeId)
        {
            var fn = ICall(ref _create, "UnityEngine.AI.NavMeshData::Internal_Create");
            var w = new Object();
            fn(w.Pointer, agentTypeId);
            if (w.m_CachedPtr == IntPtr.Zero) throw new InvalidOperationException("NavMeshData::Internal_Create left the wrapper unbound");
            try { w.hideFlags = HideFlags.HideAndDontSave; } catch { }
            return new Data { Wrapper = w };
        }

        /// <summary>Builds (synchronously) into <paramref name="data"/>. Bounds are in the data's local
        /// space, which is world space for data added untransformed.</summary>
        public static bool Update(Data data, ref BuildSettings settings, SourceList sources, Bounds localBounds)
        {
            var fn = ICall(ref _update, "UnityEngine.AI.NavMeshBuilder::UpdateNavMeshDataListInternal_Injected");
            return fn(Self(data), ref settings, sources.Pointer, ref localBounds);
        }

        /// <summary>Starts a background build into <paramref name="data"/>. Returns the native
        /// AsyncOperation: poll <see cref="IsDone"/>, then <see cref="Release"/> it. The source list
        /// must stay alive and unchanged until then.</summary>
        public static IntPtr UpdateAsync(Data data, ref BuildSettings settings, SourceList sources, Bounds localBounds)
        {
            var fn = ICall(ref _updateAsync, "UnityEngine.AI.NavMeshBuilder::UpdateNavMeshDataAsyncListInternal_Injected");
            return fn(Self(data), ref settings, sources.Pointer, ref localBounds);
        }

        public static bool IsDone(IntPtr op)
            => op == IntPtr.Zero || ICall(ref _isDone, "UnityEngine.AsyncOperation::get_isDone_Injected")(op);

        public static void Release(IntPtr op)
        {
            if (op != IntPtr.Zero) ICall(ref _destroyOp, "UnityEngine.AsyncOperation::InternalDestroy")(op);
        }

        public static void Add(Data data)
        {
            if (data.Added) return;
            data.Handle = ICall(ref _add, "UnityEngine.AI.NavMesh::AddNavMeshDataInternal_Injected")(Self(data));
        }

        public static void Remove(Data data)
        {
            if (data == null || !data.Added) return;
            ICall(ref _remove, "UnityEngine.AI.NavMesh::RemoveNavMeshDataInternal")(data.Handle);
            data.Handle = -1;
        }

        /// <summary>Removes it from the navmesh and destroys it.</summary>
        public static void Destroy(Data data)
        {
            if (data == null) return;
            try { Remove(data); } catch { }
            try { if (data.Wrapper != null && !data.Wrapper.WasCollected && data.Native != IntPtr.Zero) Object.Destroy(data.Wrapper); } catch { }
            data.Wrapper = null;
        }

        private static IntPtr Self(Data d)
        {
            IntPtr p = d?.Native ?? IntPtr.Zero;
            if (p == IntPtr.Zero) throw new ObjectDisposedException("NavMeshData", "data is null or destroyed");
            return p;
        }

        // ── Sources ──

        /// <summary>
        /// The "List&lt;NavMeshBuildSource&gt;" the builder reads. The game has no such type, and
        /// the native side only reads <c>_items</c>/<c>_size</c>, so this is an IL2CPP
        /// <c>List&lt;byte&gt;</c> whose items array holds the 96-byte sources and whose
        /// <c>_size</c> is set to the source count while it is in use. Nothing managed touches
        /// it in between.
        /// </summary>
        public sealed class SourceList
        {
            private Il2CppSystem.Collections.Generic.List<byte> _list;
            private int _capacityBytes;
            public int Count { get; private set; }
            public IntPtr Pointer => _list.Pointer;

            public void Set(BuildSource[] sources, int count)
            {
                int bytes = count * SourceBytes;
                if (_list == null || _capacityBytes < bytes)
                {
                    _capacityBytes = Math.Max(bytes, 64 * SourceBytes);
                    _list = new Il2CppSystem.Collections.Generic.List<byte>(_capacityBytes);
                }
                IntPtr lp = _list.Pointer;
                IntPtr items = Marshal.ReadIntPtr(lp, 0x10);
                if (items == IntPtr.Zero) throw new InvalidOperationException("source list has no items array");
                long len = Marshal.ReadInt64(items, 0x18);
                if (len < bytes) throw new InvalidOperationException($"source list array is {len} bytes, need {bytes}");

                var dst = (BuildSource*)(items + 0x20);
                for (int i = 0; i < count; i++) dst[i] = sources[i];
                Marshal.WriteInt32(lp, 0x18, count);
                Count = count;
            }

            /// <summary>Puts the list back to empty, so it is a consistent List&lt;byte&gt; again.</summary>
            public void Clear()
            {
                if (_list != null) Marshal.WriteInt32(_list.Pointer, 0x18, 0);
                Count = 0;
            }
        }

        /// <summary>The engine's own source collection (NavMeshBuilder.CollectSources), physics colliders
        /// on <paramref name="layerMask"/> inside <paramref name="worldBounds"/>.</summary>
        public static BuildSource[] CollectSources(Bounds worldBounds, int layerMask, bool physicsColliders, out int flags)
        {
            var fn = ICall(ref _collect, "UnityEngine.AI.NavMeshBuilder::CollectSourcesInternal_Injected");
            const int Cap = 16384;
            IntPtr buf = Marshal.AllocHGlobal(Cap * SourceBytes);
            try
            {
                var markups = new SpanWrapper();
                var ret = new ArrayWrapper { Data = buf, Size = Cap, Flags = 0 };
                fn(layerMask, ref worldBounds, IntPtr.Zero, true, physicsColliders ? 1 : 0, 0, false, ref markups, false, ref ret);
                flags = ret.Flags;

                IntPtr src;
                bool owned = false;
                switch (ret.Flags)
                {
                    case 4: case 5: return Array.Empty<BuildSource>();   // empty / null
                    case 3: src = Marshal.ReadIntPtr(ret.Data); owned = true; break;   // NativeOwnedMemory { void* data }
                    default: src = ret.Data; break;   // 0/1: copied into our buffer; 2: a native pointer
                }
                var result = new BuildSource[Math.Max(0, ret.Size)];
                var from = (BuildSource*)src;
                for (int i = 0; i < result.Length; i++) result[i] = from[i];
                if (owned)
                {
                    try { ICall(ref _freeOwned, "UnityEngine.Bindings.BindingsAllocator::FreeNativeOwnedMemory")(ret.Data); } catch { }
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // ── Queries ──

        /// <summary>The whole loaded navmesh as triangles (copied out at once).</summary>
        public static void Triangulate(out Vector3[] vertices, out int[] indices, out int[] areas)
        {
            ICall(ref _triangulate, "UnityEngine.AI.NavMesh::CalculateTriangulation_Injected")(out var t);
            vertices = ReadVec3(t.Vertices);
            indices = ReadInts(t.Indices);
            areas = ReadInts(t.Areas);
        }

        /// <summary>Walks the navmesh surface from one point toward another; true if something blocks it.</summary>
        public static bool Raycast(Vector3 from, Vector3 to, out Hit hit, int areaMask = -1)
            => ICall(ref _raycast, "UnityEngine.AI.NavMesh::Raycast_Injected")(ref from, ref to, out hit, areaMask);

        /// <summary>The nearest navmesh boundary to a point: <c>hit.Distance</c> is how much room there is.</summary>
        public static bool ClosestEdge(Vector3 from, out Hit hit, int areaMask = -1)
            => ICall(ref _edge, "UnityEngine.AI.NavMesh::FindClosestEdge_Injected")(ref from, out hit, areaMask);

        // IL2CPP arrays: klass, monitor, bounds, max_length (+0x18), data (+0x20).
        private static Vector3[] ReadVec3(IntPtr arr)
        {
            if (arr == IntPtr.Zero) return Array.Empty<Vector3>();
            int n = (int)Marshal.ReadInt64(arr, 0x18);
            var v = new Vector3[n];
            // Field writes, not the Vector3 constructor: that is a native call per vertex.
            fixed (Vector3* dst = v) Buffer.MemoryCopy((void*)(arr + 0x20), dst, (long)n * 12, (long)n * 12);
            return v;
        }

        private static int[] ReadInts(IntPtr arr)
        {
            if (arr == IntPtr.Zero) return Array.Empty<int>();
            int n = (int)Marshal.ReadInt64(arr, 0x18);
            var r = new int[n];
            Marshal.Copy(arr + 0x20, r, 0, n);
            return r;
        }
    }
}
