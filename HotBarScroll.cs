using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ScrollHotbar
{
    [BepInPlugin("ScrollHotbar", "HotbarScroll", "1.2.6")]
    public class Main : BaseUnityPlugin
    {
        private const int HotbarSlots = 8;

        internal static ConfigEntry<KeyCode> CameraZoomKey;
        internal static ConfigEntry<bool> InvertScroll;
        internal static ConfigEntry<string> IgnoredItems;

        private readonly Harmony harmony = new Harmony("ScrollHotbar");
        private ManualLogSource logger;

		internal static Main Instance { get; private set; }

		internal static void LogWarning(string message)
		{
			Instance?.logger.LogWarning(message);
		}

		internal static void LogInfo(string message)
		{
			Instance?.logger.LogInfo(message);
		}

        private int currentIndex = -1;
        private int pendingIndex = -1;
        private float pendingTimer;
        private const float SelectionDelay = 0.05f;

		private void Awake()
		{
			Instance = this;
			logger = base.Logger;

			CameraZoomKey = Config.Bind(
				"Hotbar Scroll Settings",
				"Camera Zoom Key",
				KeyCode.LeftControl,
				"Hold this key to allow the mouse wheel to control camera zoom."
			);

			InvertScroll = Config.Bind(
				"Hotbar Scroll Settings",
				"Invert Scroll Direction",
				false,
				"If true, scrolling up selects lower hotbar slots and vice versa."
			);

			IgnoredItems = Config.Bind(
				"Hotbar Scroll Settings",
				"Ignored Items",
				"$item_hammer, $item_cultivator, $item_hoe",
				"Comma-separated list of item names that will not scroll the hotbar when held. Example: $item_hammer, $item_cultivator, $item_hoe"
			);

			harmony.PatchAll();

			logger.LogInfo("HotbarScroll 1.2.6 loaded");
		}

        private void Update()
        {
            Player player = Player.m_localPlayer;
            if (player == null)
                return;

			// While the preview key is held, the mouse wheel belongs to the camera.
			if (CameraZoomKey != null &&
				Input.GetKey(CameraZoomKey.Value))
			{
				pendingIndex = -1;
				pendingTimer = 0f;
				return;
			}

            if (UiIsBlocking())
            {
                pendingIndex = -1;
                pendingTimer = 0f;
                return;
            }

            for (int i = 0; i < HotbarSlots; i++)
            {
                if (Input.GetKeyDown((KeyCode)(KeyCode.Alpha1 + i)) ||
                    Input.GetKeyDown((KeyCode)(KeyCode.Keypad1 + i)))
                {
                    currentIndex = i;
                    pendingIndex = -1;
                    pendingTimer = 0f;
                    break;
                }
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");

            // Do not create a direction when the wheel is stationary.
            if (!Mathf.Approximately(scroll, 0f))
            {
                if (currentIndex < 0)
                    currentIndex = GetCurrentHotbarIndex(player);

                int direction = scroll > 0f ? 1 : -1;
                if (InvertScroll.Value)
                    direction = -direction;

                int baseIndex = pendingIndex >= 0 ? pendingIndex : currentIndex;
                pendingIndex = WrapIndex(baseIndex + direction);
                pendingTimer = SelectionDelay;
            }

            // One pending destination only. Rapid scrolling replaces the target;
            // it does not queue intermediate equip operations.
            if (pendingIndex >= 0)
            {
                pendingTimer -= Time.deltaTime;
                if (pendingTimer <= 0f)
                {
                    int target = pendingIndex;
                    pendingIndex = -1;
                    currentIndex = target;
                    SelectHotbarSlot(player, target);
                }
            }
        }

        private static int WrapIndex(int index)
        {
            if (index >= HotbarSlots)
                return index % HotbarSlots;

            if (index < 0)
                return (index % HotbarSlots + HotbarSlots) % HotbarSlots;

            return index;
        }

        private int GetCurrentHotbarIndex(Player player)
        {
            if (player.m_inventory == null)
                return 0;

            ItemDrop.ItemData currentItem = player.GetRightItem();
            if (currentItem == null)
                return 0;

            for (int i = 0; i < HotbarSlots; i++)
            {
                ItemDrop.ItemData item = player.m_inventory.GetItemAt(i, 0);
                if (item == currentItem)
                    return i;
            }

            return 0;
        }

        private void SelectHotbarSlot(Player player, int index)
        {
            if (player.m_inventory == null || index < 0 || index >= HotbarSlots)
                return;

            ItemDrop.ItemData item = player.m_inventory.GetItemAt(index, 0);
            if (item == null)
            {
                logger.LogInfo($"Slot {index + 1} is empty");
                return;
            }

            player.EquipItem(item);
            logger.LogInfo($"Equipped slot {index + 1} ({item.m_shared.m_name})");
        }

		internal bool UiIsBlocking()
		{
			// Each check is isolated so a failing/patched API cannot disable
			// the other checks. In particular Player.InPlaceMode() is patched
			// by other mods (e.g. EasyRelocate), so the ignored-item check
			// deliberately does not depend on it: holding an ignored tool
			// bypasses hotbar scroll whether or not place mode is active.
			try
			{
				if (Menu.IsVisible())
					return true;
			}
			catch
			{
				// Keep the plugin alive if a UI API changes.
			}

			try
			{
				if (InventoryGui.instance != null && InventoryGui.IsVisible())
					return true;
			}
			catch
			{
				// Keep the plugin alive if a UI API changes.
			}

			try
			{
				if (Minimap.IsOpen())
					return true;
			}
			catch
			{
				// Keep the plugin alive if a UI API changes.
			}

			try
			{
				Player player = Player.m_localPlayer;
				if (player != null && IsIgnoredItem(player.GetRightItem()))
					return true;
			}
			catch
			{
				// Keep the plugin alive if a UI API changes.
			}

			return false;
		}

		private static bool IsIgnoredItem(ItemDrop.ItemData item)
		{
			if (item == null)
				return false;

			string itemName = item.m_shared.m_name;
			string ignored = IgnoredItems?.Value ?? "";
			foreach (string part in ignored.Split(','))
			{
				if (part.Trim() == itemName)
					return true;
			}
			return false;
		}

		// Suppress the camera zoom at the source instead of transpiling
		// GameCamera.UpdateCamera. A transpiler looking for the
		// ZInput.GetMouseScrollWheel call breaks when another mod (e.g.
		// SmarterHoe) transpiles the same method first and removes that call.
		// A prefix on ZInput.GetMouseScrollWheel is order-independent: the
		// hotbar keeps reading the raw wheel via Input.GetAxis while the
		// camera (and any other ZInput reader) sees 0.
		[HarmonyPatch(typeof(ZInput), "GetMouseScrollWheel")]
		internal static class ZInputMouseScrollPatch
		{
			private static bool Prefix(ref float __result)
			{
				Main main = Main.Instance;
				if (main == null)
					return true;

				Player player = Player.m_localPlayer;
				if (player == null)
					return true;

				// Zoom key held: wheel belongs to the camera.
				if (Main.CameraZoomKey != null &&
					Input.GetKey(Main.CameraZoomKey.Value))
					return true;

				// Menus, map, or ignored build tools: leave the wheel alone so
				// other mods (map pins, hoe radius, relocate, ...) keep working.
				if (main.UiIsBlocking())
					return true;

				__result = 0f;
				return false;
			}
		}
    }
}
