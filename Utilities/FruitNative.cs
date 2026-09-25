using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FruitLib
{
    /// <summary>
    /// Engine methods the game stripped but UnityPlayer.dll still implements, called straight
    /// through their icalls. Only the ones mods have actually needed are here.
    ///
    /// Calling these through MelonLoader's regenerated wrappers either throws "Method not
    /// found" (anything that takes a string or array: the wrapper pins it through a stripped
    /// ReadOnlySpan method) or is untested. See AssetBundleNative for the marshalling rules.
    /// </summary>
    public static class FruitNative
    {
        private delegate void SetIntegerIdFn(IntPtr self, int id, int value);
        private delegate void SetObjectFn(IntPtr self, IntPtr value);
        private delegate void SetBlendShapeFn(IntPtr self, int index, float value);
        private delegate float GetBlendShapeFn(IntPtr self, int index);

        private static SetIntegerIdFn _setInteger;
        private static SetObjectFn _setController;
        private static SetBlendShapeFn _setBlendShape;
        private static GetBlendShapeFn _getBlendShape;

        private static T ICall<T>(ref T cache, string name) where T : Delegate
            => cache ??= IL2CPP.ResolveICall<T>(name)
                         ?? throw new MissingMethodException($"icall {name} not registered");

        private static IntPtr Self(Object o)
        {
            IntPtr p = o != null ? o.m_CachedPtr : IntPtr.Zero;
            if (p == IntPtr.Zero) throw new ArgumentNullException(nameof(o), "object is null or destroyed");
            return p;
        }

        /// <summary>Animator.SetInteger, which the game stripped. Name is hashed on this side.</summary>
        public static void SetInteger(Animator animator, string name, int value)
            => SetInteger(animator, Animator.StringToHash(name), value);

        public static void SetInteger(Animator animator, int id, int value)
            => ICall(ref _setInteger, "UnityEngine.Animator::SetIntegerID_Injected")(Self(animator), id, value);

        /// <summary>
        /// Animator.runtimeAnimatorController setter, which the game stripped. Swaps an
        /// animator's controller, e.g. to one loaded from a bundle. Null clears it.
        /// </summary>
        public static void SetController(Animator animator, RuntimeAnimatorController controller)
            => ICall(ref _setController, "UnityEngine.Animator::set_runtimeAnimatorController_Injected")(
                   Self(animator), controller != null ? controller.m_CachedPtr : IntPtr.Zero);

        /// <summary>SkinnedMeshRenderer.SetBlendShapeWeight (0-100 by default), which the game stripped.</summary>
        public static void SetBlendShapeWeight(SkinnedMeshRenderer smr, int index, float weight)
            => ICall(ref _setBlendShape, "UnityEngine.SkinnedMeshRenderer::SetBlendShapeWeight_Injected")(Self(smr), index, weight);

        public static float GetBlendShapeWeight(SkinnedMeshRenderer smr, int index)
            => ICall(ref _getBlendShape, "UnityEngine.SkinnedMeshRenderer::GetBlendShapeWeight_Injected")(Self(smr), index);
    }
}
