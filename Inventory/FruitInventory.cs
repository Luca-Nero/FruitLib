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

        /// <summary>
        /// Whether a person would be afraid of the player holding this: a gun, a grenade, a
        /// blade. Null (the default) means yes for the Weapon shelf and no for every other.
        /// Read through <see cref="FruitInventory.HeldDangerous"/>. Since 5.17.0.
        /// </summary>
        public bool? Dangerous;

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

        /// <summary>
        /// Set to let the player put the item down in the world as a loose prop, with the game's
        /// own placement: the spawn-mode key and hologram of the native guns, or, with
        /// <see cref="FruitItemProp.PlaceOnClick"/>, the always-on hologram of the native props.
        /// See <see cref="Placeable"/>. Since 5.28.0.
        /// </summary>
        public FruitItemProp Prop;

        /// <summary>True while the player is in spawn mode with this item. Since 5.28.0.</summary>
        public bool InSpawnMode { get; internal set; }

        /// <summary>
        /// Make the item placeable as a prop (see <see cref="Prop"/>). <paramref name="model"/> is
        /// what is put down; null uses <see cref="Model"/>. Since 5.28.0.
        /// </summary>
        public FruitItem Placeable(GameObject model = null, float mass = 1f, FruitAffords affords = FruitAffords.None)
        {
            Prop ??= new FruitItemProp();
            Prop.Model   = model;
            Prop.Mass    = mass;
            Prop.Affords = affords;
            return this;
        }

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
    /// How a <see cref="FruitItem"/> is put down in the world. The placed thing is made with
    /// <see cref="FruitProps.Spawn(GameObject, Vector3, Quaternion, string, float, FruitAffords, string, string, IEnumerable{FruitMenuEntry})"/>:
    /// a rigidbody, colliders, listed in <see cref="FruitItems"/>, with the game's right-click
    /// menu. Since 5.28.0. See docs/items.md.
    /// </summary>
    public sealed class FruitItemProp
    {
        /// <summary>What is put down, and what the hologram shows. Null uses the item's <see cref="FruitItem.Model"/>.
        /// Its root lands where the hologram's root stood.</summary>
        public GameObject Model;

        /// <summary>
        /// Makes <see cref="Model"/> when it is first needed: when the item first reaches the
        /// toolbar, or is first put down. For a model that needs the loaded game (shaders, an
        /// asset bundle). Keep what it returns alive and inactive; returning null means "not yet",
        /// and it is asked again at the next selection. Since 5.29.0.
        /// </summary>
        public Func<GameObject> MakeModel;

        /// <summary>The prop's name in the world and its menu title. Null uses the item's name.</summary>
        public string Name;

        /// <summary>Rigidbody mass, kg.</summary>
        public float Mass = 1f;

        /// <summary>What bodies may do with it (<see cref="FruitItems"/>). None works it out from mass and shape.</summary>
        public FruitAffords Affords = FruitAffords.None;

        /// <summary>The right-click menu's category: "props" or "weapons".</summary>
        public string MenuCategory = "props";

        /// <summary>The prop's right-click menu lines, asked once per placed prop. Delete is always added.</summary>
        public Func<FruitWorldItem, IEnumerable<FruitMenuEntry>> Menu;

        /// <summary>
        /// What the placed prop does when used, as <see cref="FruitWorldItem.OnUse"/>. The player
        /// uses it by pointing at it and pressing the game's interaction key, as with a placed
        /// gun (<see cref="FruitPropUse"/>); the user and target are then null. A body holding it
        /// uses it through <see cref="FruitBodyControl.Use"/>. Null: no use. Since 5.29.0.
        /// </summary>
        public Func<FruitBody, FruitWorldItem, FruitBody, Vector3?, bool> OnUse;

        /// <summary>Every frame while the player holds the interaction key on the prop, with the
        /// seconds since the press (an automatic gun's trigger). Since 5.29.0.</summary>
        public Action<FruitWorldItem, float> OnUseHeld;

        /// <summary>The player let go of the interaction key. Since 5.29.0.</summary>
        public Action<FruitWorldItem> OnUseReleased;

        /// <summary>The use in a few words, for NPC prompts: "pull the pin". Since 5.29.0.</summary>
        public string UseVerb = "";

        /// <summary>How a body handles the placed prop when told to use it, as
        /// <see cref="FruitWorldItem.Handling"/> (<see cref="FruitHandling"/>: ArmAndThrow,
        /// AimAndFire, PlaceAndArm, PlaceAndDetonate). Null: the default. Since 5.30.0.</summary>
        public FruitUseStep[] Handling;

        /// <summary>As <see cref="FruitWorldItem.HandlingPause"/> and <see cref="FruitWorldItem.RetreatDistance"/>. Since 5.30.0.</summary>
        public float HandlingPause = 0.35f, RetreatDistance = 10f;

        /// <summary>Just after a prop is put down: the item, and the prop in the world.</summary>
        public Action<FruitItem, FruitWorldItem> OnPlaced;

        /// <summary>The player entered (true) or left (false) spawn mode with the item.</summary>
        public Action<FruitItem, bool> OnSpawnMode;

        /// <summary>The hologram's starting turn about the vertical, degrees.</summary>
        public float ModelTurn;

        /// <summary>
        /// False (default): like the game's guns. The item works in the hand as usual, and the
        /// spawn-mode key brings up the hologram; a left click places, and the item's own mouse
        /// callbacks pause while the mode is on.
        /// True: like the game's props. The hologram shows whenever the item is held, a left
        /// click places and the wheel moves it nearer or further in free placement, so
        /// <see cref="FruitItem.OnPrimary"/> and <see cref="FruitItem.OnScroll"/> never fire.
        /// </summary>
        public bool PlaceOnClick;

        /// <summary>Set when the game's placement could not be built for this item; it stays hand-only.</summary>
        public bool Broken { get; internal set; }
    }

    /// <summary>
    /// A shelf of a mod's own in the inventory window, added after the game's four. Made with
    /// <see cref="FruitInventory.AddCategory"/>; items join it by putting its name in
    /// <see cref="FruitItem.Category"/>.
    /// </summary>
    public class FruitCategory
    {
        public string Name;
        public string Description = "";

        /// <summary>The square's icon in the window's category strip. A grey disc if null.</summary>
        public Sprite Icon;

        /// <summary>True once the category is in the window's layout.</summary>
        public bool InLayout { get; internal set; }

        internal SerializedGodInventoryCategoryData Native;

        public override string ToString() => $"category '{Name}'";
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
    /// <summary>What kind of thing the player has in hand. Since 5.17.0.</summary>
    public enum FruitHeldKind
    {
        /// <summary>Nothing selected.</summary>
        None,
        /// <summary>The game's Cursor: the bare hand that grabs, pins and inspects.</summary>
        Hand,
        /// <summary>A tool: the Cutter, a mod's tool, anything that is not one of the others.</summary>
        Tool,
        /// <summary>Something that places things: the human spawner, props.</summary>
        Spawner,
        /// <summary>A gun or other weapon.</summary>
        Weapon,
    }

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

        private static readonly List<FruitCategory> _customCategories = new List<FruitCategory>();

        /// <summary>The shelves mods added, in the order they were added.</summary>
        public static IReadOnlyList<FruitCategory> CustomCategories => _customCategories;

        /// <summary>
        /// A shelf of your own, after the game's four:
        /// <code>
        /// FruitInventory.AddCategory("Bombs Away", icon);
        /// FruitInventory.AddItem(new FruitItem { Category = "Bombs Away", ... });
        /// </code>
        /// Call from OnInitializeMelon, before adding its items. A name that is already a shelf
        /// (the game's or another mod's) returns that one, so mods can share a shelf by name.
        /// If the window cannot show it, its items fall back to Etc with a warning.
        /// </summary>
        public static FruitCategory AddCategory(string name, Sprite icon = null, string description = null)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A category needs a name.", nameof(name));

            string key = Canonical(name);
            foreach (var c in _customCategories)
                if (Canonical(c.Name) == key) return c;
            if (key == "weapon" || key == "tool" || key == "prop" || key == "etc")
            {
                MelonLogger.Warning($"[FruitInventory] '{name}' is one of the game's own shelves; nothing to add.");
                return null;
            }

            var category = new FruitCategory { Name = name.Trim(), Icon = icon, Description = description ?? "" };
            _customCategories.Add(category);
            FruitLog.Info($"[FruitInventory] queued {category}");
            FruitInventoryNative.OnCategoryAdded(category);
            return category;
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
            FruitLog.Info($"[FruitInventory] queued {item} for '{item.Category}'");

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

        // ── Whatever the player holds, the game's items included ──

        private static IntPtr _heldPtr;

        /// <summary>
        /// What the player has in hand, any item (the game's own and every mod's), in plain words:
        /// "Glock 17", "Cutter", "frag grenade"... Empty while nothing is selected. A FruitLib item
        /// gives its own name; a game item its prefab name, split into words.
        /// </summary>
        /// <remarks>Since 5.13.0.</remarks>
        public static string HeldName { get; private set; } = "";

        /// <summary>What kind of thing <see cref="HeldName"/> is. Since 5.17.0.</summary>
        public static FruitHeldKind HeldKind { get; private set; }

        /// <summary>
        /// Whether the player holds something a person would fear: the game's guns and Cutter, a
        /// mod item on the Weapon shelf, or one marked <see cref="FruitItem.Dangerous"/>. The bare
        /// hand, spawners and harmless tools are not. Since 5.17.0.
        /// </summary>
        public static bool HeldDangerous { get; private set; }

        internal static void NoteHeld(Il2CppPlayer.Appearances.God.InventoryItems.GodInventoryItem instance)
        {
            if (instance == null) return;
            _heldPtr = instance.Pointer;
            var mine = FruitInventoryNative.ItemFor(instance);
            if (mine != null)
            {
                HeldName = mine.Name;
                bool weapon = Shelf(mine) == nameof(FruitItemCategory.Weapon);
                HeldKind = weapon ? FruitHeldKind.Weapon : FruitHeldKind.Tool;
                HeldDangerous = mine.Dangerous ?? weapon;
                return;
            }
            string raw;
            try { raw = instance.gameObject.name; } catch { raw = ""; }
            HeldName = Prettify(raw);
            HeldKind = KindOf(instance, out bool dangerous);
            HeldDangerous = dangerous;
        }

        internal static void NoteDropped(Il2CppPlayer.Appearances.God.InventoryItems.GodInventoryItem instance)
        {
            if (instance != null && instance.Pointer == _heldPtr)
            {
                _heldPtr = IntPtr.Zero;
                HeldName = "";
                HeldKind = FruitHeldKind.None;
                HeldDangerous = false;
            }
        }

        private static string Shelf(FruitItem item)
        {
            string c = string.IsNullOrEmpty(item.ResolvedCategory) ? item.Category : item.ResolvedCategory;
            return c != null && c.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) ? nameof(FruitItemCategory.Weapon) : c ?? "";
        }

        /// <summary>A game item by its class: CursorGodInventoryItem, CutterGodInventoryItem,
        /// HumanSpawnerGII and PropGodInventoryItem (SpawnableGodInventoryItem), and the guns,
        /// which all derive from WeaponGodInventoryItem&lt;T&gt; (Release dump.cs).</summary>
        private static FruitHeldKind KindOf(Il2CppPlayer.Appearances.God.InventoryItems.GodInventoryItem instance, out bool dangerous)
        {
            dangerous = false;
            try
            {
                for (var t = instance.GetIl2CppType(); t != null; t = t.BaseType)
                {
                    string n = t.Name ?? "";
                    if (n.StartsWith("WeaponGodInventoryItem", StringComparison.Ordinal)) { dangerous = true; return FruitHeldKind.Weapon; }
                    if (n.StartsWith("Cutter", StringComparison.Ordinal)) { dangerous = true; return FruitHeldKind.Tool; }
                    if (n.StartsWith("Cursor", StringComparison.Ordinal)) return FruitHeldKind.Hand;
                    if (n.StartsWith("SpawnableGodInventoryItem", StringComparison.Ordinal)) return FruitHeldKind.Spawner;
                    if (n == "GodInventoryItem" || n == "Object") break;
                }
            }
            catch (Exception e) { FruitLog.Info($"[FruitInventory] could not tell what kind of item is held: {e.Message}"); }
            return FruitHeldKind.Tool;
        }

        /// <summary>"Glock17GodInventoryItem(Clone)_3" -> "Glock 17".</summary>
        internal static string Prettify(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int cut = raw.IndexOfAny(new[] { '(', '_' });
            if (cut > 0) raw = raw.Substring(0, cut);
            foreach (var tail in new[] { "GodInventoryItem", "GII", "Item" })
                if (raw.EndsWith(tail, StringComparison.Ordinal) && raw.Length > tail.Length) { raw = raw.Substring(0, raw.Length - tail.Length); break; }
            var sb = new System.Text.StringBuilder(raw.Length + 4);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (i > 0 && (char.IsUpper(c) && !char.IsUpper(raw[i - 1]) || char.IsDigit(c) && !char.IsDigit(raw[i - 1]))) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        internal static void Selected(FruitItem item, GameObject held, IntPtr instance, int slot)
        {
            if (item.Holding && item.HeldInstance == instance) return;
            if (item.Holding) Deselected(item);

            // One hand. Switching straight from one item to another can deliver the new
            // select before the old deselect; putting the old one down first means every mod
            // sees deselect-then-select, and the game's late deselect finds it already down.
            foreach (var other in _items)
                if (other != item && other.Holding) Deselected(other);

            item.Holding      = true;
            item.HeldInstance = instance;
            item.Held         = held;
            item.Slot         = slot;
            FruitLog.Info($"[FruitInventory] {item} in hand (slot {slot})");
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
            FruitSpawnMode.ResetForScene();
        }
    }
}
