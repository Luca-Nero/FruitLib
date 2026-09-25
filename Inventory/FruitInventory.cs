using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppData.Player.Inventory.God;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// The four shelves of the release build's inventory window.
    ///
    /// The numbers are what <see cref="FruitInventory.AddItem(string, string, int, Sprite, string)"/>
    /// accepts. <c>Misc</c> is the same shelf as <c>Etc</c> - the game calls it Etc.
    /// </summary>
    public enum FruitItemCategory
    {
        Weapon = 0,
        Tool   = 1,
        Etc    = 2,
        Prop   = 3,
        Misc   = Etc,
    }

    /// <summary>
    /// A custom item in the game's inventory. Build one through
    /// <see cref="FruitInventory.AddItem(FruitItem)"/> or one of its shorthands.
    /// </summary>
    public class FruitItem
    {
        /// <summary>Stable identifier, e.g. "BombsAway:Frag". Derived from your assembly and
        /// <see cref="Name"/> if left empty.</summary>
        public string Id;

        public string Name        = "Custom Item";
        public string Description = "";

        /// <summary>Shown in the inventory window and on the toolbar. A grey disc if null.</summary>
        public Sprite Icon;

        /// <summary>
        /// Which shelf. A <see cref="FruitItemCategory"/> name ("Weapon", "Tool", "Etc",
        /// "Misc", "Prop"), its number, or the name of any category the game ships - matched
        /// loosely, so "weapons", "Weapons" and "WeaponCategory" all find the same shelf.
        /// An unknown name is filed under Etc with a warning naming the ones that exist.
        /// </summary>
        public string Category = nameof(FruitItemCategory.Etc);

        // ── The card ──────────────────────────────────────────────────────────────
        //
        // The inventory window shows a card for the hovered item: its name, then key/value
        // rows, then its description as flavor text. The game types the card by what the item
        // is. A weapon's card always opens with "caliber" and "fire mode" (like the LYNX-F's
        // 7.62 mm / automatic), and a prop's with "size". Set those here rather than as Stats:
        // they then come from the game's own descriptor, spelled and ordered as native items
        // spell and order them. Stats follow them.

        /// <summary>Weapon card, e.g. "7.62 mm". Setting this or <see cref="FireMode"/> makes it a weapon card.</summary>
        public string Caliber;

        /// <summary>Weapon card, e.g. "automatic", "semi-automatic", "pump-action".</summary>
        public string FireMode;

        /// <summary>Prop card, e.g. "small". Ignored on a weapon card.</summary>
        public string Size;

        /// <summary>
        /// Further key/value rows after the typed ones, e.g. ("rate", "600 rounds per minute").
        /// Rows with an empty key or value, or a key used twice (including caliber / fire
        /// mode / size), are dropped with a warning - the game refuses the whole item over them.
        /// </summary>
        public readonly List<KeyValuePair<string, string>> Stats = new List<KeyValuePair<string, string>>();

        /// <summary>Make this a weapon card: caliber and fire mode rows, then <see cref="Stats"/>.</summary>
        public FruitItem WeaponCard(string caliber, string fireMode)
        {
            Caliber  = caliber;
            FireMode = fireMode;
            return this;
        }

        /// <summary>Make this a prop card: a size row, then <see cref="Stats"/>.</summary>
        public FruitItem PropCard(string size)
        {
            Size = size;
            return this;
        }

        /// <summary>
        /// Optional. Cloned into the item when it is registered, so the item has a body in
        /// the player's hand. Keep its local transform where you want it relative to the hand.
        /// </summary>
        public GameObject Model;

        /// <summary>The item came into the player's hand. <see cref="Held"/> is set.</summary>
        public Action<FruitItem> OnSelected;
        /// <summary>The item left the player's hand, or was destroyed while in it.</summary>
        public Action<FruitItem> OnDeselected;
        /// <summary>Every frame while the item is in hand.</summary>
        public Action<FruitItem> WhileHeld;

        // Mouse input, as the game dispatches it to whatever is in hand. It only arrives while
        // the item is held and gameplay has input - not while the inventory window is open.
        public Action<FruitItem>        OnPrimary;
        public Action<FruitItem, float> OnPrimaryHold;
        public Action<FruitItem>        OnPrimaryRelease;
        public Action<FruitItem>        OnSecondary;
        public Action<FruitItem, float> OnSecondaryHold;
        public Action<FruitItem>        OnSecondaryRelease;
        public Action<FruitItem>        OnMiddle;
        public Action<FruitItem, float> OnScroll;

        /// <summary>True once the game knows about the item and lists it.</summary>
        public bool IsRegistered => Data != null;

        /// <summary>True while the item is in the player's hand.</summary>
        public bool IsHeld => Held != null;

        /// <summary>The in-hand instance while held, else null. Parent effects and models here.</summary>
        public GameObject Held { get; internal set; }

        /// <summary>The toolbar slot the held instance sits in, else -1.</summary>
        public int Slot { get; internal set; } = -1;

        /// <summary>The name of the shelf the game actually filed this under, once registered.</summary>
        public string ResolvedCategory { get; internal set; }

        public FruitItem AddStat(string key, string value)
        {
            Stats.Add(new KeyValuePair<string, string>(key, value));
            return this;
        }

        /// <summary>
        /// Change the name and/or icon after registration. The inventory window shows the new
        /// ones the next time it draws; an entry already sitting on the toolbar keeps the old
        /// ones until it is replaced.
        /// </summary>
        public void SetDisplay(string name = null, Sprite icon = null)
        {
            if (!string.IsNullOrEmpty(name)) Name = name;
            if (icon != null) Icon = icon;
            if (IsRegistered) FruitInventoryNative.RefreshDisplay(this);
        }

        // ── Native side, owned by FruitInventoryNative ────────────────────────────
        internal IGodInventoryItemData          Data;
        internal GodInventoryItemData           Concrete;
        internal SerializedItemDescriptor       Descriptor;
        internal SerializedGIIRegistrationData  Registration;
        internal string                         InternalName;
        internal bool                           Failed;
        internal bool                           Holding;
        internal IntPtr                         HeldInstance;

        public override string ToString() => $"'{Name}' ({Id})";
    }

    /// <summary>
    /// Custom items in the release build's inventory.
    ///
    /// <code>
    /// FruitInventory.AddItem("MyMod:Flare", "Flare", FruitItemCategory.Tool);
    /// FruitInventory.AddItem("MyMod:Crate", "Crate", "prop");
    /// FruitInventory.AddItem("MyMod:Knife", "Knife", 0);   // 0 = Weapon
    /// </code>
    ///
    /// Call from OnInitializeMelon. Items registered then are in the game's item registry
    /// from boot, exactly like the native ones: listed on their shelf in the inventory
    /// window, sendable to any toolbar slot with the number keys, and spawned into the
    /// player's hand as a real item. Adding later works too; the item appears the next time
    /// the window draws.
    /// </summary>
    public static class FruitInventory
    {
        private static readonly List<FruitItem> _items = new List<FruitItem>();

        public static IReadOnlyList<FruitItem> Items => _items;

        /// <summary>The shelves the game offers, by display name. Empty until the game has booted.</summary>
        public static IReadOnlyList<string> Categories => FruitInventoryNative.CategoryNames();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static FruitItem AddItem(string id, string name, FruitItemCategory category,
                                        Sprite icon = null, string description = null)
            => Add(id, name, category.ToString(), icon, description, Assembly.GetCallingAssembly());

        /// <summary>
        /// <paramref name="category"/> is a <see cref="FruitItemCategory"/> number:
        /// 0 Weapon, 1 Tool, 2 Etc/Misc, 3 Prop.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static FruitItem AddItem(string id, string name, int category,
                                        Sprite icon = null, string description = null)
        {
            string shelf;
            if (Enum.IsDefined(typeof(FruitItemCategory), category))
                shelf = ((FruitItemCategory)category).ToString();
            else
            {
                MelonLogger.Warning($"[FruitInventory] '{name}' asks for category {category}; " +
                                    "valid numbers are 0 Weapon, 1 Tool, 2 Etc, 3 Prop. Filing it under Etc.");
                shelf = nameof(FruitItemCategory.Etc);
            }

            return Add(id, name, shelf, icon, description, Assembly.GetCallingAssembly());
        }

        /// <summary>
        /// <paramref name="category"/> is a shelf name - see <see cref="FruitItem.Category"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static FruitItem AddItem(string id, string name, string category,
                                        Sprite icon = null, string description = null)
            => Add(id, name, category, icon, description, Assembly.GetCallingAssembly());

        /// <summary>Register a fully configured item: stats, model, input callbacks.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static FruitItem AddItem(FruitItem item) => Add(item, Assembly.GetCallingAssembly());

        public static FruitItem Get(string id)
        {
            foreach (var item in _items) if (item.Id == id) return item;
            return null;
        }

        private static FruitItem Add(string id, string name, string category, Sprite icon,
                                     string description, Assembly caller)
            => Add(new FruitItem
            {
                Id          = id,
                Name        = string.IsNullOrEmpty(name) ? "Custom Item" : name,
                Category    = category,
                Icon        = icon,
                Description = description ?? "",
            }, caller);

        internal static FruitItem Add(FruitItem item, Assembly caller)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            if (string.IsNullOrEmpty(item.Id))
            {
                string owner;
                try { owner = caller?.GetName().Name ?? "Unknown"; }
                catch { owner = "Unknown"; }
                item.Id = owner + ":" + item.Name;
            }

            var existing = Get(item.Id);
            if (existing != null)
            {
                MelonLogger.Warning($"[FruitInventory] duplicate id '{item.Id}' — keeping the first registration.");
                return existing;
            }

            if (string.IsNullOrEmpty(item.Category)) item.Category = nameof(FruitItemCategory.Etc);

            _items.Add(item);
            MelonLogger.Msg($"[FruitInventory] queued {item} for '{item.Category}'");

            // Straight in if the game is already up; otherwise the boot hook picks it up.
            FruitInventoryNative.RegisterPending();
            return item;
        }

        // ── Category names ────────────────────────────────────────────────────────

        /// <summary>
        /// One spelling per shelf, so a request and a game asset can be compared. Lower case,
        /// letters and digits only, a trailing "category" dropped, and the aliases folded:
        /// "Weapons", "weapon" and "WeaponCategory" all come out as "weapon".
        /// </summary>
        internal static string Canonical(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            var chars = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw)
                if (char.IsLetterOrDigit(c)) chars.Append(char.ToLowerInvariant(c));

            string s = chars.ToString();
            if (s.Length > "category".Length && s.EndsWith("category")) s = s.Substring(0, s.Length - "category".Length);

            switch (s)
            {
                case "weapon": case "weapons": case "gun": case "guns":
                    return "weapon";
                case "tool": case "tools":
                    return "tool";
                case "prop": case "props":
                    return "prop";
                case "etc": case "misc": case "miscellaneous": case "other": case "others":
                    return "etc";
                default:
                    return s;
            }
        }

        // ── Called from the game side ─────────────────────────────────────────────

        internal static FruitItem ByData(IGodInventoryItemData data)
        {
            if (data == null) return null;
            IntPtr ptr = data.Pointer;
            foreach (var item in _items)
                if (item.Data != null && item.Data.Pointer == ptr) return item;
            return null;
        }

        internal static FruitItem ByInternalName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var item in _items)
                if (item.InternalName == name) return item;
            return null;
        }

        internal static void Selected(FruitItem item, GameObject held, IntPtr instance, int slot)
        {
            if (item.Holding && item.HeldInstance == instance) return;
            if (item.Holding) Deselected(item);

            item.Holding      = true;
            item.HeldInstance = instance;
            item.Held         = held;
            item.Slot         = slot;
            MelonLogger.Msg($"[FruitInventory] {item} in hand (slot {slot})");
            FruitTrace.Mark($"{item} in hand, slot {slot}: calling the mod's OnSelected");
            Invoke(item.OnSelected, item, nameof(FruitItem.OnSelected));
            FruitTrace.Mark($"{item} OnSelected returned");
        }

        internal static void Deselected(FruitItem item)
        {
            if (!item.Holding) return;
            item.Holding = false;

            // Held stays readable during the callback, so a mod can take back whatever it
            // parented there. It is already null if the instance was destroyed.
            FruitTrace.Mark($"{item} leaving hand: calling the mod's OnDeselected");
            Invoke(item.OnDeselected, item, nameof(FruitItem.OnDeselected));
            FruitTrace.Mark($"{item} OnDeselected returned");

            item.Held         = null;
            item.HeldInstance = IntPtr.Zero;
            item.Slot         = -1;
        }

        internal static void Invoke(Action<FruitItem> callback, FruitItem item, string what)
        {
            if (callback == null) return;
            try { callback(item); }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] {item} {what} threw: {e}"); }
        }

        internal static void Invoke(Action<FruitItem, float> callback, FruitItem item, float value, string what)
        {
            if (callback == null) return;
            try { callback(item, value); }
            catch (Exception e) { MelonLogger.Warning($"[FruitInventory] {item} {what} threw: {e}"); }
        }

        internal static void Tick()
        {
            if (_items.Count == 0) return;

            FruitInventoryGc.ReleaseDead();

            foreach (var item in _items)
            {
                if (!item.Holding) continue;

                // Destroyed with the scene or the toolbar entry, without the game saying so
                // first. A destroyed object compares equal to null, which is the tell.
                if (item.Held == null) { Deselected(item); continue; }

                Invoke(item.WhileHeld, item, nameof(FruitItem.WhileHeld));
            }
        }

        internal static void ResetForScene()
        {
            FruitSfx.Reset();
            FruitInventoryNative.PruneDeadInstances();
        }
    }
}
