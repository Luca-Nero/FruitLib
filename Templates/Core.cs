using FruitLib;
using MelonLoader;
using System.Runtime.CompilerServices;
using UnityEngine;

[assembly: MelonInfo(typeof(MyMod.Core), "MyMod", MyMod.Core.Version, "YourName")]
[assembly: MelonGame()]
[assembly: MelonOptionalDependencies("FruitLib")]
// No PatchAll anywhere: MelonLoader already applies every [HarmonyPatch] in this assembly.
// Calling it again installs each hook twice. Add [assembly: HarmonyDontPatchAll] only if
// you want to decide yourself what gets patched.

namespace MyMod
{
    /// <summary>
    /// The shape every FruitLib mod shares.
    ///
    /// FruitLib is an *optional* dependency on purpose: if it is missing or too old, the
    /// mod says so and unregisters instead of dying with a TypeLoadException. For that to
    /// work nothing FruitLib-typed may sit in a method that runs before the gate passes,
    /// which is why every body that touches FruitLib is its own [NoInlining] method.
    /// </summary>
    public class Core : MelonMod
    {
        public const string Version = "1.0.0";

        // The oldest FruitLib this mod is built against.
        private const int LibMajor = 5, LibMinor = 0, LibPatch = 0;
        private bool _active;

        private static ModPerf _perf;
        private static bool    _equipped;

        public override void OnInitializeMelon()
        {
            _active = FruitGate.Check("MyMod", LibMajor, LibMinor, LibPatch);
            if (_active) Init();
        }

        public override void OnLateInitializeMelon()
        {
            if (_active) return;
            try { Unregister(FruitGate.FailureReason, silent: true); } catch { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Init()
        {
            ConfigLoader.Load();
            FruitMenu.Register("MyMod", ConfigLoader.IniPath, typeof(Config));
            FruitHud.Register("MyMod", BuildHud);

            // One item per variant: SetDisplay can't refresh a copy already on the toolbar,
            // so cycling one item through variants leaves a stale icon there.
            FruitInventory.AddItem(new FruitItem
            {
                Id           = "MyMod:Thing",
                Name         = "Thing",
                Description  = "What it does, in one line.",
                Category     = nameof(FruitItemCategory.Tool),
                Icon         = FruitIcons.Solid(new Color(0.3f, 0.6f, 0.9f)),
                // FruitLib always delivers the previous item's deselect before the next select.
                OnSelected   = item => _equipped = true,
                OnDeselected = item => _equipped = false,
                OnPrimary    = item => Use(),
            }
            .AddStat("left click", "use"));

            _perf = FruitPerfMon.For("MyMod")
                .Value("Equipped", () => _equipped ? "yes" : "no");

            FruitUpdateCheck.Register("MyMod", Version, "YourGitHubUser", "MyMod");
            LoggerInstance.Msg($"MyMod v{Version} loaded.");
        }

        public override void OnUpdate()
        {
            if (_active) UpdateBody();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UpdateBody()
        {
            // Paused, or in / just out of the mod menu: gameplay keys belong to the menu.
            if (FruitMenu.BlocksGameplayInput) return;
            if (Config.Enabled && Input.GetKeyDown(Config.UseKey)) Use();
        }

        private static void Use()
        {
            if (!Config.Enabled) return;
            using (_perf.Time("Use"))
            {
                if (Config.Dbg1) MelonLogger.Msg($"[MyMod] used with strength {Config.Strength}");
            }
        }

        private static void BuildHud(HudPanel p)
        {
            if (!_equipped) return;   // nothing on screen unless the item is in hand
            p.Header("MyMod");
            p.Line($"Strength {Config.Strength:F0} N", HudPanel.Dim);
        }
    }
}
