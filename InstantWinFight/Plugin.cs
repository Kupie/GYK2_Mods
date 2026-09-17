using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace InstantWinFight
{
	[BepInPlugin("kupie.gk2.instantwinfight", "Instant Win Fight", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<KeyboardShortcut> WinFightKey;

		private void Awake()
		{
			WinFightKey = Config.Bind(
				"General",
				"WinFightKey",
				new KeyboardShortcut(KeyCode.F12, KeyCode.LeftControl),
				"Key that instantly resolves the current fight as a win, for testing.");
		}

		// FightingGameController.FinishAsWon() is the same method the game itself calls on a
		// real victory - it hands out fight rewards, plays the win window/audio, and chains
		// into the next fight if one is queued, so this behaves like an actual win rather than
		// faking one by killing enemies or zeroing health.
		private void Update()
		{
			if (!WinFightKey.Value.IsDown())
			{
				return;
			}

			FightingGameController controller = FightingGameController.Instance;
			if (controller == null || controller.CurrentLevel == null)
			{
				Logger.LogWarning("Ctrl+F12: no fight is currently active.");
				return;
			}

			controller.FinishAsWon();
			Logger.LogInfo("Ctrl+F12: forced fight win.");
		}
	}
}
