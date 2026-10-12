using System;
using HarmonyLib;
using Il2CppGame;
using Il2CppPlayer.GameplayInput.ButtonsActions;
using MelonLoader;
using UnityEngine;

namespace FruitLib
{
    /// <summary>
    /// A one-line text box at the bottom of the screen, for any mod that wants the player to type
    /// something: a line to an NPC, a terminal command, a name. While it is open the game's own
    /// controls stand still (camera, tools, hotkeys such as slow motion), every FruitLib-gated mod
    /// hotkey is quiet (<see cref="FruitMenu.IsInputSuppressed"/>), and Esc closes the box instead
    /// of opening the pause menu.
    ///
    /// <code>
    /// if (Input.GetKeyDown(KeyCode.U) &amp;&amp; !FruitMenu.IsInputSuppressed)
    ///     FruitTextInput.Open("MyMod", "Say:", (text, shift) => Say(text, shout: shift));
    /// </code>
    ///
    /// How it works: characters come from IMGUI key events (OnGUI), the same path the FruitLib
    /// menu's number fields use, so the keyboard layout and key repeat are the system's. The game
    /// is held through its own ButtonsInputBlockService (Player.GameplayInput.ButtonsActions), the
    /// block the pause menu and the loading curtain take: PlayerControlService, the god inventory
    /// items and every button action read it. The service is a Zenject object FruitLib can't
    /// look up, so it is picked up from the game's own calls to it.
    /// </summary>
    /// <remarks>Since 5.16.0.</remarks>
    public static class FruitTextInput
    {
        private static Action<string, bool> _submit;
        private static Action _cancel;
        private static string _text = "", _prompt = "", _owner = "";
        private static int _caret, _maxLength = 200;
        private static int _openedFrame = -10, _closedFrame = -10;

        private static ButtonsInputBlockService _block;
        private static ButtonsInputBlockService _blockHeldOn;
        private static Il2CppSystem.Object _holder;
        private static bool _warnedNoBlock;

        /// <summary>The box is open.</summary>
        public static bool Active { get; private set; }

        /// <summary>The box closed this frame or the last: the Enter or Esc that closed it must
        /// not also reach a hotkey.</summary>
        public static bool JustClosed => Time.frameCount - _closedFrame <= 1;

        /// <summary>Which mod opened the box ("" when closed).</summary>
        public static string Owner => Active ? _owner : "";

        /// <summary>What has been typed so far.</summary>
        public static string Text => _text;

        /// <summary>Whether the game's controls could be held. False means the camera and tools
        /// still answer the keyboard while typing (the service has not been seen yet).</summary>
        public static bool HoldsGame => _blockHeldOn != null;

        /// <summary>
        /// Opens the box. <paramref name="onSubmit"/> gets the line (trimmed, never empty) and
        /// whether Shift was held on Enter; <paramref name="onCancel"/> runs on Esc. Returns false
        /// if the box is already open for another mod.
        /// </summary>
        public static bool Open(string owner, string prompt, Action<string, bool> onSubmit,
                                Action onCancel = null, int maxLength = 200)
        {
            if (Active && _owner != owner) return false;
            _owner = owner ?? "";
            _prompt = prompt ?? "";
            _submit = onSubmit;
            _cancel = onCancel;
            _maxLength = Mathf.Clamp(maxLength, 1, 1000);
            _text = "";
            _caret = 0;
            _openedFrame = Time.frameCount;
            Active = true;
            HoldGame(true);
            return true;
        }

        /// <summary>Changes the label in front of the text while the box is open.</summary>
        public static void SetPrompt(string prompt) => _prompt = prompt ?? "";

        /// <summary>Closes the box without submitting or calling the cancel callback.</summary>
        public static void Close()
        {
            if (!Active) return;
            Active = false;
            _closedFrame = Time.frameCount;
            _submit = null;
            _cancel = null;
            HoldGame(false);
        }

        // ── Holding the game ──

        internal static void Seen(ButtonsInputBlockService s)
        {
            if (s == null) return;
            _block = s;
            // Opened before the service was known: take the hold now.
            if (Active && _blockHeldOn == null) HoldGame(true);
        }

        /// <summary>Forced on for any reader of ButtonsInputBlockService.Blocked while the box is
        /// open, in case our own block request could not be placed.</summary>
        internal static bool ForcesBlock => Active;

        private static void HoldGame(bool on)
        {
            try
            {
                if (on)
                {
                    if (_blockHeldOn != null) return;
                    if (_block == null || _block.WasCollected)
                    {
                        if (!_warnedNoBlock)
                        {
                            _warnedNoBlock = true;
                            FruitLog.Info("[FruitTextInput] the game's input block service hasn't been seen yet; the camera may move while typing.");
                        }
                        return;
                    }
                    _holder ??= new Il2CppSystem.Object();
                    // Marked held first: AddBlockRequest runs our own postfix, which would take it again.
                    _blockHeldOn = _block;
                    _blockHeldOn.AddBlockRequest(_holder);
                }
                else
                {
                    var held = _blockHeldOn;
                    _blockHeldOn = null;
                    if (held != null && !held.WasCollected) held.RemoveBlockRequest(_holder);
                }
            }
            catch (Exception e)
            {
                _blockHeldOn = null;
                MelonLogger.Warning($"[FruitTextInput] input block {(on ? "take" : "release")} failed: {e.Message}");
            }
        }

        internal static void ResetForScene()
        {
            // A new scene brings new services; a hold on the old one went with it.
            Active = false;
            _blockHeldOn = null;
            _block = null;
            _submit = null;
            _cancel = null;
        }

        // ── Keys and drawing (FruitLibMod.OnGUI) ──

        internal static void OnGui()
        {
            if (!Active) return;
            var ev = Event.current;
            if (ev == null) return;
            if (ev.type == EventType.KeyDown) HandleKey(ev);
            else if (ev.type == EventType.Repaint) Draw();
        }

        private static void HandleKey(Event ev)
        {
            // The key that opened the box arrives as a character in the same frame (or the next).
            if (Time.frameCount - _openedFrame <= 1) { ev.Use(); return; }

            switch (ev.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                {
                    string line = _text.Trim();
                    var submit = _submit;
                    bool shift = ev.shift;
                    ev.Use();
                    Close();
                    if (line.Length > 0) Invoke(() => submit?.Invoke(line, shift), "submit");
                    return;
                }
                case KeyCode.Escape:
                {
                    var cancel = _cancel;
                    ev.Use();
                    Close();
                    Invoke(() => cancel?.Invoke(), "cancel");
                    return;
                }
                case KeyCode.LeftArrow:  if (_caret > 0) _caret--; ev.Use(); return;
                case KeyCode.RightArrow: if (_caret < _text.Length) _caret++; ev.Use(); return;
                case KeyCode.Home:       _caret = 0; ev.Use(); return;
                case KeyCode.End:        _caret = _text.Length; ev.Use(); return;
                case KeyCode.Backspace:
                    if (_caret > 0)
                    {
                        int from = ev.control ? WordStart(_caret) : _caret - 1;
                        _text = _text.Remove(from, _caret - from);
                        _caret = from;
                    }
                    ev.Use();
                    return;
                case KeyCode.Delete:
                    if (_caret < _text.Length) _text = _text.Remove(_caret, 1);
                    ev.Use();
                    return;
            }

            char ch = ev.character;
            if (ch == '\0' || char.IsControl(ch)) { ev.Use(); return; }
            if (_text.Length < _maxLength)
            {
                _text = _text.Insert(_caret, ch.ToString());
                _caret++;
            }
            ev.Use();
        }

        private static int WordStart(int from)
        {
            int i = from;
            while (i > 0 && _text[i - 1] == ' ') i--;
            while (i > 0 && _text[i - 1] != ' ') i--;
            return i;
        }

        private static void Invoke(Action a, string what)
        {
            try { a(); }
            catch (Exception e) { MelonLogger.Warning($"[FruitTextInput] {_owner} {what} callback threw: {e}"); }
        }

        private static GUIStyle _style, _promptStyle;
        private static int _styleSize;
        private static readonly GUIContent _content = new GUIContent();

        private static readonly Color Bg      = new Color(0.04f, 0.05f, 0.06f, 0.82f);
        private static readonly Color Edge    = new Color(0.55f, 0.66f, 0.90f, 0.9f);
        private static readonly Color PromptC = new Color(0.62f, 0.72f, 0.95f, 1f);

        private static void Draw()
        {
            float scale = Mathf.Max(0.6f, Screen.height / 1080f);
            int size = Mathf.RoundToInt(19f * scale);
            if (_style == null || _styleSize != size)
            {
                _styleSize = size;
                _style = FruitMenu.WithText(new GUIStyle { padding = new RectOffset(0, 0, 0, 0), normal = { textColor = Color.white } },
                                            size, false, FruitMenu.TextAlign.MiddleLeft);
                _promptStyle = FruitMenu.WithText(new GUIStyle { padding = new RectOffset(0, 0, 0, 0), normal = { textColor = PromptC } },
                                                  size, true, FruitMenu.TextAlign.MiddleLeft);
            }

            float w = Mathf.Min(900f * scale, Screen.width - 40f);
            float h = Mathf.Round(size * 2.1f);
            float x = (Screen.width - w) * 0.5f;
            float y = Mathf.Round(Screen.height * 0.80f);
            float pad = Mathf.Round(12f * scale);

            var saved = GUI.color;
            var savedContent = GUI.contentColor;
            try
            {
                FruitMenu.Rect(x, y, w, h, Bg);
                FruitMenu.Rect(x, y + h - 2f, w, 2f, Edge);
                GUI.color = Color.white;
                GUI.contentColor = Color.white;

                float tx = x + pad;
                if (_prompt.Length > 0)
                {
                    _content.text = _prompt;
                    float pw = _promptStyle.CalcSize(_content).x;
                    GUI.Label(new UnityEngine.Rect(tx, y, pw, h), _content, _promptStyle);
                    tx += pw + pad * 0.6f;
                }

                // Keep the caret in view: show the tail of a long line.
                float room = x + w - pad - tx;
                bool blink = (int)(Time.unscaledTime * 2f) % 2 == 0;
                string shown = _text.Substring(0, _caret) + (blink ? "|" : " ") + _text.Substring(_caret);
                _content.text = shown;
                int cut = 0;
                while (cut < _caret && _style.CalcSize(_content).x > room)
                {
                    cut += 4;
                    _content.text = shown.Substring(Mathf.Min(cut, _caret));
                }
                GUI.Label(new UnityEngine.Rect(tx, y, room, h), _content, _style);
            }
            finally
            {
                GUI.color = saved;
                GUI.contentColor = savedContent;
            }
        }
    }

    // ── Picking up the game's input block service ──
    // Every reader asks Blocked; the loading curtain and the pause menu add and remove requests.
    // Whichever runs first hands us the instance.

    [HarmonyPatch(typeof(ButtonsInputBlockService), nameof(ButtonsInputBlockService.AddBlockRequest))]
    internal static class FruitTextInput_AddBlockPatch
    {
        static void Postfix(ButtonsInputBlockService __instance)
        {
            try { FruitTextInput.Seen(__instance); }
            catch (Exception e) { MelonLogger.Warning($"[FruitTextInput] block hook failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(ButtonsInputBlockService), nameof(ButtonsInputBlockService.RemoveBlockRequest))]
    internal static class FruitTextInput_RemoveBlockPatch
    {
        static void Postfix(ButtonsInputBlockService __instance)
        {
            try { FruitTextInput.Seen(__instance); }
            catch (Exception e) { MelonLogger.Warning($"[FruitTextInput] block hook failed: {e.Message}"); }
        }
    }

    [HarmonyPatch(typeof(ButtonsInputBlockService), nameof(ButtonsInputBlockService.Blocked), MethodType.Getter)]
    internal static class FruitTextInput_BlockedPatch
    {
        static void Postfix(ButtonsInputBlockService __instance, ref bool __result)
        {
            try
            {
                FruitTextInput.Seen(__instance);
                if (FruitTextInput.ForcesBlock) __result = true;
            }
            catch { }
        }
    }

    /// <summary>Esc closes the text box; it must not pause the game as well.</summary>
    [HarmonyPatch(typeof(PauseService), nameof(PauseService.SetPause))]
    internal static class FruitTextInput_PausePatch
    {
        static bool Prefix()
        {
            try { return !(FruitTextInput.Active || FruitTextInput.JustClosed); }
            catch { return true; }
        }
    }
}
