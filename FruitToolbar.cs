using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// The pre-release toolbar-slot API, kept so mods written against it still build and run.
    ///
    /// The release build replaced the fixed toolbar with an inventory, and FruitLib no longer
    /// grows the toolbar itself. A registered toolbar item is now an inventory item on the
    /// Tools shelf; the player sends it to whichever slot they like, and the callbacks
    /// receive that slot.
    /// </summary>
    [Obsolete("The release build replaced the toolbar with an inventory. Use FruitItem with FruitInventory.AddItem.")]
    public class FruitToolbarItem
    {
        public string Id;

        public string Name = "Custom Item";
        public Sprite Icon;
        public Action<int> OnSelected;
        public Action<int> OnDeselected;

        internal FruitItem Item;

        public void SetDisplay(string name, Sprite icon = null)
        {
            if (!string.IsNullOrEmpty(name)) Name = name;
            if (icon != null) Icon = icon;
            Item?.SetDisplay(name, icon);
        }
    }

    [Obsolete("The release build replaced the toolbar with an inventory. Use FruitInventory.")]
    public static class FruitToolbar
    {
        /// <summary>Registers the item on the inventory's Tools shelf.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Register(FruitToolbarItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            var bridged = new FruitItem
            {
                Id           = item.Id,
                Name         = item.Name,
                Icon         = item.Icon,
                Category     = nameof(FruitItemCategory.Tool),
                OnSelected   = fi => item.OnSelected?.Invoke(fi.Slot),
                OnDeselected = fi => item.OnDeselected?.Invoke(fi.Slot),
            };

            item.Item = FruitInventory.Add(bridged, Assembly.GetCallingAssembly());
            item.Id   = item.Item.Id;
        }

        /// <summary>The toolbar slot the item is held from, or -1 while it is not in hand.</summary>
        public static int GetSlot(string id) => FruitInventory.Get(id)?.Slot ?? -1;

        public static Sprite LoadIcon(Assembly assembly, string resourceName,
                                      FilterMode filter = FilterMode.Bilinear)
            => FruitIcons.Load(assembly, resourceName, filter);

        public static Sprite LoadIcon(byte[] png, FilterMode filter = FilterMode.Bilinear,
                                      string name = "FruitLib icon")
            => FruitIcons.Load(png, filter, name);

        public static Sprite MakeSolidIcon(Color color, int size = 64) => FruitIcons.Solid(color, size);

        // The slot-layout knobs below have nothing left to act on: the player decides where an
        // item goes. They remain so existing mods compile, and do nothing.

        [Obsolete("No effect: the player chooses slots in the inventory window.")]
        public static readonly List<string> PreferredOrder = new List<string>();

        [Obsolete("No effect: FruitLib no longer adds toolbar slots.")]
        public static int NativeBaseOverride = -1;

        [Obsolete("No effect: FruitLib no longer adds toolbar slots.")]
        public static bool AutoCentreSlotRow = true;

        [Obsolete("No effect: FruitLib no longer adds toolbar slots.")]
        public static Vector2 SlotRowOffset = Vector2.zero;
    }
}
