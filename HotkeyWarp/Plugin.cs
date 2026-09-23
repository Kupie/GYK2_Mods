using BepInEx;
using BepInEx.Configuration;
using LazyBearTechnology;
using UnityEngine;

namespace HotkeyWarp
{
	// The warp menu (UIMapWindow) is normally only reachable through
	// TeleportMilestoneInteractionHandler.Interact, which is wired to a specific monument's
	// FlowCanvas graph (Flow_OpenMapOnMilestone) rather than being called directly in C#. That
	// node's own open call is:
	//
	//   LazyUI.GetWindow<UIMapWindow>().Open(new MapPageWidgetData(MainGame.Instance.GameSave,
	//       true, base.SelfWgoData.id));
	//
	// Every piece of that is public API, so this mod calls it the same way instead of faking a
	// monument interaction (no need to find/raycast a nearby monument WGO or fight proximity
	// checks). The only monument-specific argument is the last one, currentMilestone - traced
	// into MapPageWidget.Draw, it is only used to disable clicking the milestone that matches it
	// (you're "already there"), nothing else depends on it. Passing null here means every
	// unlocked destination stays clickable, including the one nearest the player - a harmless
	// cosmetic difference from the monument's own behavior, not a functional one.
	[BepInPlugin("kupie.gk2.hotkeywarp", "Hotkey Warp", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<KeyboardShortcut> OpenWarpMenuKey;

		private void Awake()
		{
			OpenWarpMenuKey = Config.Bind(
				"General",
				"OpenWarpMenuKey",
				new KeyboardShortcut(KeyCode.Z),
				"Opens the fast travel (warp) menu from anywhere, without needing to stand at a monument.");
		}

		private void Update()
		{
			if (!OpenWarpMenuKey.Value.IsDown())
			{
				return;
			}

			TryOpenWarpMenu();
		}

		private void TryOpenWarpMenu()
		{
			if (!LazyUI.IsInitialized)
			{
				return;
			}

			if (MainGame.Instance == null || MainGame.Instance.GameSave == null)
			{
				return;
			}

			PlayerController playerController = MainGame.PlayerController;
			if (playerController == null || !playerController.IsControlsEnabled)
			{
				// Covers dialogue, cutscenes, building mode, sleeping and every other state that
				// already takes control away from the player - the same flag the game itself
				// checks before letting its own menus and interactions open.
				return;
			}

			UIMapWindow warpMenu = LazyUI.GetWindow<UIMapWindow>();
			if (warpMenu == null || warpMenu.IsShown)
			{
				return;
			}

			if (LazyWindowsStackController.HasAnyModalWindowOpened)
			{
				// Don't stack the warp menu on top of another open modal window (inventory,
				// build menu, dialogue box and similar).
				return;
			}

			warpMenu.Open(new MapPageWidgetData(MainGame.Instance.GameSave, true, null));
		}
	}
}
