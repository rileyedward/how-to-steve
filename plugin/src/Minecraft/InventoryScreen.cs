using System;
using System.Collections.Generic;
using BepInEx.Logging;
using SteveFish.Bridge;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SteveFish.Minecraft
{
    /// <summary>
    /// Minecraft inventory and container screens (player inventory with the 2×2 grid, crafting table,
    /// chests, furnaces), drawn with the jar's gui/container textures in vanilla's slot layout. Every click
    /// goes to the server as window_click; the server decides what happens and sends the new slots back.
    /// </summary>
    internal sealed class InventoryScreen
    {
        private readonly McClientState _state;
        private readonly Action<string, object> _send;
        private readonly ManualLogSource _log;
        private readonly Key _key;
        private InputActionMap _disabledMap;
        private readonly List<KeyValuePair<int, Rect>> _slotRects = new List<KeyValuePair<int, Rect>>();
        private Rect _panel;

        public InventoryScreen(McClientState state, string key, Action<string, object> send, ManualLogSource log)
        {
            _state = state;
            _send = send;
            _log = log;
            if (!Enum.TryParse(key, true, out _key) || _key == Key.None) _key = Key.E;
        }

        public int WindowId => _state.Window?.WindowId ?? 0;

        // ---------- per-frame input (main thread) ----------

        public void Tick(bool allowed)
        {
            var kb = Keyboard.current;
            if (!allowed)
            {
                if (_state.ScreenOpen) Close(true);
                return;
            }

            // A container the server opened (right-clicked crafting table, chest, ...) shows immediately.
            if (_state.Window != null && !_state.ScreenOpen) Open();

            if (kb != null && kb[_key].wasPressedThisFrame)
            {
                if (_state.ScreenOpen) Close(true);
                else Open();
            }
            else if (_state.ScreenOpen && kb != null && kb.escapeKey.wasPressedThisFrame) Close(true);

            if (!_state.ScreenOpen) return;

            // Free the mouse and freeze How to Fish's own controls while the screen is up.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            var map = GameInfo.Input?.actions?.FindActionMap("Player");
            if (map != null && map.enabled) { map.Disable(); _disabledMap = map; }

            HandleClicks();
        }

        private void Open()
        {
            _state.ScreenOpen = true;
            _log.LogInfo($"inventory screen open: {(_state.Window != null ? _state.Window.Type : "player inventory")}");
        }

        /// <summary>Closes our screen; tells the server unless it closed the window itself.</summary>
        public void Close(bool tellServer)
        {
            if (!_state.ScreenOpen && _state.Window == null) return;
            if (tellServer) _send("window_close", new WindowIdPayload { WindowId = WindowId });
            _state.ScreenOpen = false;
            _state.Window = null;
            if (_disabledMap != null) { _disabledMap.Enable(); _disabledMap = null; }
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void HandleClicks()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            var left = mouse.leftButton.wasPressedThisFrame;
            var right = mouse.rightButton.wasPressedThisFrame;
            if (!left && !right) return;
            var p = mouse.position.ReadValue();
            var gui = new Vector2(p.x, Screen.height - p.y); // Input System: bottom-left origin; IMGUI: top-left
            var shift = Keyboard.current != null && (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);

            var slot = -999; // outside the panel = drop the cursor stack
            foreach (var kv in _slotRects)
                if (kv.Value.Contains(gui)) { slot = kv.Key; break; }
            if (slot == -999 && _panel.Contains(gui)) return; // background inside the panel: nothing
            _send("window_click", new WindowClickPayload { WindowId = WindowId, Slot = slot, Button = right ? 1 : 0, Mode = shift && slot >= 0 ? 1 : 0 });
        }

        // ---------- drawing (OnGUI) ----------

        private struct Layout
        {
            public string Texture;
            public int Height;
            public int ContainerRows; // generic_9xN only
            public List<Vector3Int> Slots; // (slot, x, y) in GUI pixels
            public string Title;
        }

        public void Draw(HudOverlay hud, float s)
        {
            var layout = BuildLayout();
            var slots = _state.Window?.Slots ?? _state.Inventory?.Slots;
            var cursor = _state.Window != null ? _state.Window.Cursor : _state.Inventory?.Cursor;

            var w = 176 * s;
            var h = layout.Height * s;
            var x0 = Mathf.Round((Screen.width - w) / 2f);
            var y0 = Mathf.Round((Screen.height - h) / 2f);
            _panel = new Rect(x0, y0, w, h);

            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), hud.Dim);
            var bg = hud.GuiTexture("container/" + layout.Texture);
            if (bg != null)
            {
                float tw = bg.width, th = bg.height;
                if (layout.ContainerRows > 0)
                {
                    // generic_54: top part (title + rows) then the player inventory part from y=126.
                    var top = layout.ContainerRows * 18 + 17;
                    GUI.DrawTextureWithTexCoords(new Rect(x0, y0, w, top * s), bg, new Rect(0, 1 - top / th, 176 / tw, top / th));
                    GUI.DrawTextureWithTexCoords(new Rect(x0, y0 + top * s, w, 96 * s), bg, new Rect(0, 1 - (126 + 96) / th, 176 / tw, 96 / th));
                }
                else
                {
                    GUI.DrawTextureWithTexCoords(_panel, bg, new Rect(0, 1 - layout.Height / th, 176 / tw, layout.Height / th));
                }
            }
            else
            {
                GUI.DrawTexture(_panel, Texture2D.grayTexture);
            }

            hud.Text(new Rect(x0 + 8 * s, y0 + 5 * s, w, 10 * s), layout.Title, new Color(0.25f, 0.25f, 0.25f));

            _slotRects.Clear();
            var mouse = Mouse.current;
            var mp = mouse != null ? mouse.position.ReadValue() : Vector2.zero;
            var gui = new Vector2(mp.x, Screen.height - mp.y);
            foreach (var sl in layout.Slots)
            {
                var r = new Rect(x0 + sl.y * s, y0 + sl.z * s, 16 * s, 16 * s);
                _slotRects.Add(new KeyValuePair<int, Rect>(sl.x, r));
                var item = slots != null && sl.x < slots.Count ? slots[sl.x] : null;
                if (item != null) hud.ItemStack(r, item, s);
                if (r.Contains(gui)) GUI.DrawTexture(r, hud.Highlight);
            }

            if (cursor != null) hud.ItemStack(new Rect(gui.x - 8 * s, gui.y - 8 * s, 16 * s, 16 * s), cursor, s);
        }

        private Layout BuildLayout()
        {
            var type = _state.Window?.Type ?? "minecraft:inventory";
            var t = type.StartsWith("minecraft:") ? type.Substring(10) : type;
            var title = _state.Window?.Title;
            var slots = new List<Vector3Int>();
            void Grid(int first, int cols, int rows, int x, int y)
            {
                for (var r = 0; r < rows; r++)
                    for (var c = 0; c < cols; c++)
                        slots.Add(new Vector3Int(first + r * cols + c, x + c * 18, y + r * 18));
            }
            void PlayerInv(int first, int y) { Grid(first, 9, 3, 8, y); Grid(first + 27, 9, 1, 8, y + 58); }

            if (t == "crafting")
            {
                slots.Add(new Vector3Int(0, 124, 35));
                Grid(1, 3, 3, 30, 17);
                PlayerInv(10, 84);
                return new Layout { Texture = "crafting_table", Height = 166, Slots = slots, Title = Title(title, "Crafting") };
            }
            if (t == "furnace" || t == "blast_furnace" || t == "smoker")
            {
                slots.Add(new Vector3Int(0, 56, 17));
                slots.Add(new Vector3Int(1, 56, 53));
                slots.Add(new Vector3Int(2, 116, 35));
                PlayerInv(3, 84);
                return new Layout { Texture = t, Height = 166, Slots = slots, Title = Title(title, "Furnace") };
            }
            if (t.StartsWith("generic_9x") && int.TryParse(t.Substring(10), out var rows) && rows >= 1 && rows <= 6)
            {
                Grid(0, 9, rows, 8, 18);
                PlayerInv(rows * 9, rows * 18 + 31);
                return new Layout { Texture = "generic_54", Height = rows * 18 + 17 + 96, ContainerRows = rows, Slots = slots, Title = Title(title, "Chest") };
            }
            if (t == "inventory" || _state.Window == null)
            {
                slots.Add(new Vector3Int(0, 154, 28));
                Grid(1, 2, 2, 98, 18);
                for (var i = 0; i < 4; i++) slots.Add(new Vector3Int(5 + i, 8, 8 + i * 18));
                PlayerInv(9, 84);
                slots.Add(new Vector3Int(45, 77, 62));
                return new Layout { Texture = "inventory", Height = 166, Slots = slots, Title = "" };
            }
            // Anything else: show the container's slots in rows of 9 above the player inventory.
            var n = Math.Max(0, (_state.Window.Slots?.Count ?? 36) - 36);
            var gr = Math.Min(6, (n + 8) / 9);
            Grid(0, 9, gr, 8, 18);
            PlayerInv(n, gr * 18 + 31);
            return new Layout { Texture = "generic_54", Height = gr * 18 + 17 + 96, ContainerRows = gr, Slots = slots, Title = Title(title, t) };
        }

        private static string Title(string fromServer, string fallback) =>
            string.IsNullOrEmpty(fromServer) || fromServer.StartsWith("container.") ? fallback : fromServer;
    }
}
