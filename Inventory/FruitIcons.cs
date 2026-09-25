using System;
using System.IO;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// Sprites for inventory items: from a PNG embedded in a mod, from raw PNG bytes, or a
    /// generated disc for prototyping.
    ///
    /// These lived on FruitToolbar until the release build replaced the toolbar with an
    /// inventory. FruitToolbar still forwards to them, so existing callers keep compiling.
    /// </summary>
    public static class FruitIcons
    {
        /// <summary>
        /// A sprite from a PNG embedded in your mod.
        ///
        /// Mirrors how meshes are shipped: add the file to your csproj as an
        /// <c>EmbeddedResource</c> and name it here. Matching is by suffix, so
        /// "AK.png" finds "MyMod.Icons.AK.png" without you having to know how the
        /// compiler mangled the folder into the resource name.
        ///
        /// <code>
        /// Icon = FruitIcons.Load(Assembly.GetExecutingAssembly(), "Icons/AK.png");
        /// </code>
        ///
        /// Returns null and logs if the resource is missing or is not a readable image,
        /// which leaves the item on FruitLib's placeholder disc rather than undrawn.
        /// </summary>
        public static Sprite Load(Assembly assembly, string resourceName,
                                  FilterMode filter = FilterMode.Bilinear)
        {
            if (assembly == null || string.IsNullOrEmpty(resourceName)) return null;

            try
            {
                // Folder separators become dots in a manifest name, so the caller can write
                // the path the way it appears in their project and still be found.
                string wanted = resourceName.Replace('/', '.').Replace('\\', '.');

                string found = null;
                int    hits  = 0;
                foreach (var name in assembly.GetManifestResourceNames())
                {
                    if (!name.Equals(wanted, StringComparison.OrdinalIgnoreCase) &&
                        !name.EndsWith("." + wanted, StringComparison.OrdinalIgnoreCase)) continue;

                    if (hits++ == 0) found = name;
                }

                if (found == null)
                {
                    MelonLogger.Warning($"[FruitIcons] no embedded resource matching '{resourceName}' in " +
                                        $"{assembly.GetName().Name}. Is it marked as an EmbeddedResource?");
                    return null;
                }

                if (hits > 1)
                    MelonLogger.Warning($"[FruitIcons] '{resourceName}' matches {hits} resources in " +
                                        $"{assembly.GetName().Name}; using '{found}'. Give the name more of its path.");

                byte[] png;
                using (var stream = assembly.GetManifestResourceStream(found))
                {
                    if (stream == null) return null;

                    // Read to the end rather than trusting one Read to fill the buffer - a
                    // manifest stream is free to hand back less than asked for.
                    using (var buffer = new MemoryStream())
                    {
                        stream.CopyTo(buffer);
                        png = buffer.ToArray();
                    }
                }

                return Load(png, filter, found);
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitIcons] loading icon '{resourceName}' failed: {e.Message}");
                return null;
            }
        }

        /// <summary>A sprite from PNG bytes you already have.</summary>
        public static Sprite Load(byte[] png, FilterMode filter = FilterMode.Bilinear,
                                  string name = "FruitLib icon")
        {
            if (png == null || png.Length == 0) return null;

            Texture2D tex = null;
            try
            {
                // Size does not matter here; LoadImage replaces the texture with the PNG's
                // own dimensions and format.
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);

                if (!ImageConversion.LoadImage(tex, png))
                {
                    MelonLogger.Warning($"[FruitIcons] '{name}' is not a readable PNG.");
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }

                tex.filterMode = filter;
                tex.wrapMode   = TextureWrapMode.Clamp;

                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                           new Vector2(0.5f, 0.5f), 100f);

                tex.name    = name;
                sprite.name = name;

                // Both marked to survive, for the reason described on Solid: a mod loads its
                // icons at startup, and the first scene load would otherwise be free to
                // collect them.
                tex.hideFlags    = HideFlags.HideAndDontSave;
                sprite.hideFlags = HideFlags.HideAndDontSave;

                return sprite;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FruitIcons] decoding '{name}' failed: {e.Message}");
                if (tex != null) UnityEngine.Object.Destroy(tex);
                return null;
            }
        }

        /// <summary>A coloured disc drawn at runtime, for prototyping or mods that ship no art.</summary>
        public static Sprite Solid(Color color, int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;

            float r = size * 0.5f, edge = r - 2f;
            var clear = new Color(0f, 0f, 0f, 0f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - r + 0.5f, dy = y - r + 0.5f;
                    float d  = Mathf.Sqrt(dx * dx + dy * dy);
                    tex.SetPixel(x, y, d > edge ? clear : (d > edge - 4f ? color * 0.6f : color));
                }
            }

            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            tex.name    = $"FruitLib icon {ColorUtility.ToHtmlStringRGB(color)}";
            sprite.name = tex.name;

            // Both marked to survive. A texture and a sprite built at runtime belong to no
            // scene and no asset bundle, so the first load that runs UnloadUnusedAssets is
            // free to collect them - and a mod registers its icon at startup, long before the
            // first scene. What reaches the game afterwards is a destroyed object, which
            // compares equal to null and reads as "the mod never supplied one".
            tex.hideFlags    = HideFlags.HideAndDontSave;
            sprite.hideFlags = HideFlags.HideAndDontSave;

            return sprite;
        }

        private static Sprite _placeholder;

        /// <summary>
        /// A plain grey disc, for an item whose mod supplied no icon.
        ///
        /// The game's icon drawer will not take a null sprite - it throws rather than
        /// drawing an empty slot - so there is always something to hand it.
        /// </summary>
        internal static Sprite Placeholder()
        {
            if (_placeholder != null) return _placeholder;

            try { _placeholder = Solid(new Color(0.65f, 0.65f, 0.65f, 1f)); }
            catch (Exception e) { MelonLogger.Warning($"[FruitIcons] could not build a placeholder icon: {e.Message}"); }

            return _placeholder;
        }
    }
}
