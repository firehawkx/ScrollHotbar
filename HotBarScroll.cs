using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace ScrollHotbar
{
    [BepInPlugin("ScrollHotbar", "HotbarScroll", "1.2.6")]
    public class Main : BaseUnityPlugin
    {
        private const int HotbarSlots = 8;

        internal static ConfigEntry<KeyCode> CameraZoomKey;
        internal static ConfigEntry<bool> InvertScroll;

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

            if (currentIndex < 0)
                currentIndex = GetCurrentHotbarIndex(player);

            float scroll = Input.GetAxis("Mouse ScrollWheel");

            // Do not create a direction when the wheel is stationary.
            if (!Mathf.Approximately(scroll, 0f))
            {
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

         private bool UiIsBlocking()
         {
             try
             {
                 if (Menu.IsVisible())
                     return true;

                 if (InventoryGui.instance != null && InventoryGui.IsVisible())
                     return true;

                 if (Minimap.IsOpen())
                     return true;

                 Player player = Player.m_localPlayer;
                 if (player != null && player.InPlaceMode() && (player.GetRightItem()?.m_shared.m_name == "$item_hammer" || player.GetRightItem()?.m_shared.m_name == "$item_cultivator" || player.GetRightItem()?.m_shared.m_name == "$item_hoe"))
                     return true;
             }
             catch
             {
                 // Keep the plugin alive if a UI API changes.
             }

             return false;
         }

		[HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
		internal static class GameCameraUpdatePatch
		{
			private static readonly MethodInfo GetMouseScrollWheelMethod =
				AccessTools.Method(
					"ZInput:GetMouseScrollWheel",
					new Type[0]
				);

			private static readonly MethodInfo GetCameraScrollMethod =
				AccessTools.Method(
					typeof(GameCameraUpdatePatch),
					nameof(GetCameraScroll)
				);

			private static float GetCameraScroll()
			{
				if (Main.CameraZoomKey != null &&
					Input.GetKey(Main.CameraZoomKey.Value))
				{
					return GetOriginalScrollWheel();
				}

				return 0f;
			}

			private static float GetOriginalScrollWheel()
			{
				if (GetMouseScrollWheelMethod == null)
					return 0f;

				try
				{
					object result = GetMouseScrollWheelMethod.Invoke(null, null);
					return result is float value ? value : 0f;
				}
				catch
				{
					return 0f;
				}
			}

			private static IEnumerable<CodeInstruction> Transpiler(
				IEnumerable<CodeInstruction> instructions)
			{
				bool replaced = false;

				foreach (CodeInstruction instruction in instructions)
				{
					if (GetMouseScrollWheelMethod != null &&
						instruction.opcode == OpCodes.Call &&
						instruction.operand is MethodInfo calledMethod &&
						calledMethod == GetMouseScrollWheelMethod)
					{
						yield return new CodeInstruction(
							OpCodes.Call,
							GetCameraScrollMethod
						);

						replaced = true;
					}
					else
					{
						yield return instruction;
					}
				}

				if (!replaced)
				{
					Main.LogWarning(
						"ScrollHotbar could not find ZInput.GetMouseScrollWheel " +
						"inside GameCamera.UpdateCamera."
					);
				}
				else
				{
					Main.LogInfo(
						"ScrollHotbar patched GameCamera.UpdateCamera wheel input."
					);
				}
			}
		}
    }
}
