using FruitLib;
using UnityEngine;

namespace MyMod
{
    /// <summary>
    /// Every setting is a public static field. The field name keys the ini, so renaming one
    /// resets it for players; change the [MenuLabel] instead.
    ///
    /// [MenuCategory] puts it in the in-game menu (and a section of the ini); without one it
    /// is ini-only. [MenuRange] turns a number into a slider with those ends; declare it,
    /// FruitLib's guess from the default is usually wrong where it matters.
    /// </summary>
    public static class Config
    {
        [MenuCategory("General"), MenuLabel("Enabled")]
        public static bool Enabled = true;

        [MenuCategory("General"), MenuLabel("Strength (N)"), MenuRange(0, 500)]
        public static float Strength = 100f;

        [MenuCategory("Controls"), MenuLabel("Use key")]
        public static KeyCode UseKey = KeyCode.G;

        [MenuCategory("Debug"), MenuLabel("Debug level"), MenuRange(0, 2)]
        public static int DebugLevel = 0;

        public static bool Dbg1 => DebugLevel >= 1;
        public static bool Dbg2 => DebugLevel >= 2;
    }
}
