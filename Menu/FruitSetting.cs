using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using MelonLoader.Preferences;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// One setting as the menu sees it, whatever stores it.
    ///
    /// Two stores exist: a FruitLib mod's static config field, and a MelonPreferences entry
    /// from a mod that has never heard of FruitLib. Both are normalised here to the five
    /// types the menu can draw - bool, int, float, string, KeyCode - so the native pages and
    /// the IMGUI panel draw them the same way without knowing which kind they have.
    /// </summary>
    internal sealed class FruitSetting
    {
        internal string Key;          // ini / preference identifier; never shown
        internal string Label;
        internal string Category;     // null = stored but never drawn
        internal Type   Type;         // bool, int, float, string or KeyCode
        internal bool   Momentary;    // a bool that is really an action button
        internal bool   HasRange;
        internal float  Min, Max;
        internal object Default;

        internal Func<object>   Get;
        internal Action<object> Set;

        internal object Value
        {
            get => Get();
            set => Set(value);
        }

        internal static bool Drawable(Type t) =>
            t == typeof(bool) || t == typeof(float) || t == typeof(int) ||
            t == typeof(string) || t == typeof(KeyCode);

        // ── From a FruitLib config class ─────────────────────────────────────────────

        /// <summary>Whether a config field becomes a setting. The one place that decides, so
        /// <see cref="CaptureDefaults"/> and <see cref="FromField"/> can never disagree.</summary>
        private static bool IsSettingField(FieldInfo f) =>
            !(f.IsSpecialName || f.IsLiteral || f.IsInitOnly || !Drawable(f.FieldType));

        // What each field held before its mod loaded the ini. Reset to Defaults goes back to
        // these; without an entry it falls back to whatever the field holds at registration.
        private static readonly Dictionary<FieldInfo, object> _captured = new Dictionary<FieldInfo, object>();
        private static readonly HashSet<Type> _capturedTypes = new HashSet<Type>();

        /// <summary>Copies an array so a later in-place edit cannot rewrite the default with it.
        /// Anything else the menu draws is a value type or an immutable string.</summary>
        private static object Snapshot(object v) => v is Array a ? a.Clone() : v;

        /// <summary>Records the current value of every field <see cref="FromField"/> would turn
        /// into a setting. First call for a type wins; later ones are ignored.</summary>
        internal static void CaptureDefaults(Type configType)
        {
            if (configType == null || !_capturedTypes.Add(configType)) return;
            foreach (var f in configType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (!IsSettingField(f)) continue;
                try { _captured[f] = Snapshot(f.GetValue(null)); }
                catch (Exception e) { MelonLogger.Warning($"[FruitMenu] could not capture the default of '{f.Name}': {e.Message}"); }
            }
        }

        internal static FruitSetting FromField(FieldInfo f)
        {
            if (!IsSettingField(f)) return null;

            var cat   = (MenuCategoryAttribute)Attribute.GetCustomAttribute(f, typeof(MenuCategoryAttribute));
            var btn   = (MenuButtonAttribute)Attribute.GetCustomAttribute(f, typeof(MenuButtonAttribute));
            var range = (MenuRangeAttribute)Attribute.GetCustomAttribute(f, typeof(MenuRangeAttribute));

            return new FruitSetting
            {
                Key       = f.Name,
                Label     = FruitMenu.LabelOf(f),
                Category  = cat?.Name,
                Type      = f.FieldType,
                Momentary = f.FieldType == typeof(bool) && btn?.Kind == ButtonKind.Momentary,
                HasRange  = range != null,
                Min       = range?.Min ?? 0f,
                Max       = range?.Max ?? 0f,
                Default   = _captured.TryGetValue(f, out var captured) ? Snapshot(captured) : Snapshot(f.GetValue(null)),
                Get       = () => f.GetValue(null),
                Set       = v => f.SetValue(null, v),
            };
        }

        // ── From MelonPreferences ────────────────────────────────────────────────────

        /// <summary>
        /// Null for a type the menu has no control for (a Vector3, an enum other than KeyCode,
        /// a list). Doubles and longs are shown as float and int and written back in their
        /// own type, so the mod reading them never sees the difference.
        /// </summary>
        internal static FruitSetting FromPreference(MelonPreferences_Entry e, string category)
        {
            Type stored;
            try { stored = e.GetReflectedType(); } catch { return null; }
            if (stored == null) return null;

            Type shown;
            Func<object, object> toShown, toStored;
            if (Drawable(stored))            { shown = stored;        toShown = v => v; toStored = v => v; }
            else if (stored == typeof(double)) { shown = typeof(float); toShown = v => Convert.ToSingle(v); toStored = v => Convert.ToDouble(v); }
            else if (stored == typeof(long))   { shown = typeof(int);   toShown = v => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)v)); toStored = v => Convert.ToInt64(v); }
            else return null;

            var s = new FruitSetting
            {
                Key      = e.Identifier,
                Label    = string.IsNullOrEmpty(e.DisplayName) ? e.Identifier : e.DisplayName,
                Category = category,
                Type     = shown,
                Get      = () => toShown(e.BoxedValue),
                // Through BoxedValue, so the entry's validator clamps it and the owning mod's
                // OnValueChanged fires as if it had set the value itself.
                Set      = v => e.BoxedValue = toStored(v),
            };

            // The typed entry's own default, so Reset to Defaults means what the mod shipped,
            // not whatever the player had when the menu first looked.
            try
            {
                var dv = e.GetType().GetProperty("DefaultValue")?.GetValue(e);
                s.Default = dv != null ? toShown(dv) : null;
            }
            catch { s.Default = null; }
            try
            {
                if (e.Validator is IValueRange r)
                {
                    s.HasRange = true;
                    s.Min = Convert.ToSingle(r.MinValue);
                    s.Max = Convert.ToSingle(r.MaxValue);
                }
            }
            catch { s.HasRange = false; }

            return s;
        }
    }

    /// <summary>
    /// Lists every MelonPreferences category in the MODS menu, so mods that keep their
    /// settings the MelonLoader way get a page without referencing FruitLib at all.
    ///
    /// Mods create their categories in their own OnInitializeMelon, after FruitLib's, so this
    /// looks again whenever the menu is about to list mods rather than once at start.
    ///
    /// <b>Applying changes.</b> A change goes into the entry at once, which is enough for a mod
    /// that reads <c>entry.Value</c> live. Most copy their values at startup instead and refresh
    /// them in <c>OnPreferencesLoaded</c>, which MelonLoader raises when it loads a preferences
    /// file (that is why editing the file by hand applies straight away). Saving through
    /// MelonLoader raises nothing of the kind, so changed categories are queued and
    /// <see cref="Flush"/> - on pause close, scene change and quit - saves them and reloads
    /// each file once, raising the event every mod already listens for.
    /// </summary>
    internal static class FruitPreferencesBridge
    {
        private static readonly HashSet<string> _added = new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<MelonPreferences_Category> _dirty = new List<MelonPreferences_Category>();

        private static readonly FieldInfo _fileField =
            typeof(MelonPreferences_Category).GetField("File", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>The file a category lives in (MelonLoader keeps it internal). If that can't be
        /// read, every category counts as the default file: one reload, which covers them all.</summary>
        private static object FileOf(MelonPreferences_Category cat)
        {
            try { return _fileField?.GetValue(cat) ?? "default"; }
            catch { return "default"; }
        }

        private static void MarkDirty(MelonPreferences_Category cat)
        {
            if (!_dirty.Contains(cat)) _dirty.Add(cat);
        }

        internal static void Flush()
        {
            if (_dirty.Count == 0) return;

            var files = new List<object>();
            var reloadVia = new List<MelonPreferences_Category>();

            foreach (var cat in _dirty)
            {
                try { cat.SaveToFile(false); }
                catch (Exception e) { MelonLogger.Warning($"[FruitMenu] saving MelonPreferences '{cat.Identifier}' failed: {e.Message}"); continue; }

                // Categories share files (most use MelonPreferences.cfg). One reload per file,
                // or every mod's OnPreferencesLoaded would run once per changed category.
                object file = FileOf(cat);
                if (files.Contains(file)) continue;
                files.Add(file);
                reloadVia.Add(cat);
            }

            foreach (var cat in reloadVia)
            {
                try { cat.LoadFromFile(false); }
                catch (Exception e) { MelonLogger.Warning($"[FruitMenu] reloading MelonPreferences '{cat.Identifier}' failed: {e.Message}"); }
            }

            FruitLog.Info($"[FruitMenu] applied {_dirty.Count} MelonPreferences categor{(_dirty.Count == 1 ? "y" : "ies")}; " +
                          $"{reloadVia.Count} file(s) reloaded, OnPreferencesLoaded raised.");
            _dirty.Clear();
        }

        internal static void Sync(Action<string, List<FruitSetting>, Action> add)
        {
            List<MelonPreferences_Category> categories;
            try { categories = MelonPreferences.Categories; }
            catch (Exception e) { MelonLogger.Warning($"[FruitMenu] reading MelonPreferences failed: {e.Message}"); return; }
            if (categories == null) return;

            foreach (var cat in categories)
            {
                try
                {
                    if (cat == null || cat.IsHidden || _added.Contains(cat.Identifier)) continue;

                    string name = string.IsNullOrEmpty(cat.DisplayName) ? cat.Identifier : cat.DisplayName;
                    var settings = new List<FruitSetting>();
                    foreach (var entry in cat.Entries)
                    {
                        if (entry == null || entry.IsHidden) continue;
                        var s = FruitSetting.FromPreference(entry, name);
                        if (s != null) settings.Add(s);
                    }

                    // Created but not filled yet, most likely: look again next time.
                    if (settings.Count == 0) continue;

                    _added.Add(cat.Identifier);
                    var captured = cat;
                    add(name, settings, () => MarkDirty(captured));
                    FruitLog.Info($"[FruitMenu] listed MelonPreferences category '{name}' ({settings.Count} setting(s)).");
                }
                catch (Exception e) { MelonLogger.Warning($"[FruitMenu] MelonPreferences category '{cat?.Identifier}' skipped: {e.Message}"); }
            }
        }
    }
}
