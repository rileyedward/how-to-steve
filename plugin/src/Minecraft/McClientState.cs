using HowToSteve.Bridge;
using UnityEngine;

namespace HowToSteve.Minecraft
{
    /// <summary>What the plugin knows about Steve's Minecraft side, for the HUD. Main thread only.</summary>
    internal sealed class McClientState
    {
        public InventoryPayload Inventory;
        public HudStatePayload Hud;
        public int HeldSlot;                 // predicted locally, corrected by inventory messages
        public int[] DigPos;
        public float DigProgress;
        public bool MinecraftMode;
        public WindowPayload Window;         // open container from the server, or null
        public bool ScreenOpen;              // our inventory/container screen is showing
        public string Toast;
        public float ToastUntil;

        public void ShowToast(string text, float seconds = 2.5f)
        {
            Toast = text;
            ToastUntil = Time.unscaledTime + seconds;
        }

        public InventorySlotDto HotbarSlot(int i)
        {
            var slots = Inventory?.Slots;
            var idx = 36 + i;
            return slots != null && idx < slots.Count ? slots[idx] : null;
        }

        public void Clear()
        {
            Inventory = null;
            Hud = null;
            DigPos = null;
            DigProgress = 0;
        }
    }
}
