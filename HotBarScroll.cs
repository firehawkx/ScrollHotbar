using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using System.Reflection;
using UnityEngine;

namespace ScrollHotbar
{
    [BepInPlugin("com.kurophantom.scrollhotbar", "HotbarScroll", "1.2.5")]
    public class Main : BaseUnityPlugin
    {
        private const int HotbarSlots = 8;

        private readonly Harmony HarmonyInstance = new Harmony("com.kurophantom.scrollhotbar");
        private ManualLogSource logger;

        private ConfigEntry<KeyCode> keybindPreview;
        private ConfigEntry<bool> invertScroll;

        private int currentIndex = 0;
        private float savedZoom = 5f;

        private float scrollTimer = 0f;
        private float scrollDelay = 0.1f;
        private bool pendingEquip = false;

        private float lastScrollValue = 0f;
        private bool scrollJustEnded = false;

        private static FieldInfo distanceField;

        private void Awake()
        {
            logger = (ManualLogSource)base.Logger;
            distanceField = typeof(GameCamera).GetField("m_distance",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (distanceField == null)
                logger.LogWarning("GameCamera.m_distance not found — zoom restore disabled");

            keybindPreview = Config.Bind(
                "Hotbar Scroll Settings",
                "Preview Key",
                KeyCode.LeftControl,
                "Key used to activate hotbar preview scrolling."
            );

            invertScroll = Config.Bind(
                "Hotbar Scroll Settings",
                "Invert Scroll Direction",
                false,
                "If true, scrolling up selects lower hotbar slots and vice versa."
            );

            HarmonyInstance.PatchAll();
            logger.LogInfo("HotbarScroll 1.2.5 loaded for Valheim 1.0!");
        }

        private void Update()
        {
            Player player = Player.m_localPlayer;
            if (player == null || GameCamera.instance == null) return;
            if (UiIsBlocking()) return;

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            bool isPreviewing = Input.GetKey(keybindPreview.Value);
            int direction = scroll > 0f ? 1 : scroll < 0f ? -1 : 0;
            if (invertScroll.Value) direction *= -1;

            GameCamera cam = GameCamera.instance;

            // Save zoom while the preview key is held
            if (isPreviewing && distanceField != null)
                savedZoom = (float)distanceField.GetValue(cam);

            // Detect scroll end
            scrollJustEnded = (lastScrollValue != 0f && Mathf.Approximately(scroll, 0f));
            lastScrollValue = scroll;

            // Restore zoom after a zoom-scroll ends
            if (!isPreviewing && scrollJustEnded && distanceField != null)
            {
                float currentZoom = (float)distanceField.GetValue(cam);
                if (Mathf.Abs(currentZoom - savedZoom) > 0.0005f)
                    distanceField.SetValue(cam, savedZoom);
            }

            // Hotbar scrolling
            if (!isPreviewing && direction != 0)
            {
                if (currentIndex < 0 || currentIndex >= HotbarSlots)
                    currentIndex = 0;

                // Fixed wrap math: consistent modulo in both directions
                currentIndex = (currentIndex + HotbarSlots + direction) % HotbarSlots;
                scrollTimer = scrollDelay;
                pendingEquip = true;
            }

            if (pendingEquip)
            {
                scrollTimer -= Time.deltaTime;
                if (scrollTimer <= 0f)
                {
                    player.UseHotbarItem(currentIndex);
                    logger.LogInfo($"Equipped slot: {currentIndex + 1}");
                    pendingEquip = false;
                }
            }
        }

        private bool UiIsBlocking()
        {
            try
            {
                if (Menu.IsVisible()) return true;
                if (InventoryGui.instance != null && InventoryGui.IsVisible()) return true;
                if (Chat.instance != null && Chat.IsVisible()) return true;
                if (Minimap.instance != null && Minimap.IsVisible()) return true;
                return false;
            }
            catch
            {
                // A renamed/removed UI class would throw — fail open so the mod keeps working
                return false;
            }
        }
    }
}
