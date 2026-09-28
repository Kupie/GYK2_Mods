using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace DeconstructToPlayer
{
	// Instant removal (build mode's Remove tool) drops a building's refund and contents via
	// WgoData.MakeDrop -> GetDropPos, i.e. at the building. While the Remove tool is open, this
	// moves that position onto the player so the pickup magnet grabs everything right away.
	[BepInPlugin("kupie.gk2.deconstructtoplayer", "Deconstruct To Player", "1.1.0")]
	public class Plugin : BaseUnityPlugin
	{
		private Harmony harmony;

		private void Awake()
		{
			harmony = new Harmony("kupie.gk2.deconstructtoplayer");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}
	}

	[HarmonyPatch(typeof(WgoData), nameof(WgoData.GetDropPos))]
	internal static class WgoData_GetDropPos_Patch
	{
		private static void Postfix(WgoData __instance, ref Vector3 __result)
		{
			BuildController buildController = BuildController.Instance;
			if (buildController == null || !buildController.IsRemoveMode)
			{
				return;
			}

			PlayerController player = MainGame.PlayerController;
			if (player == null)
			{
				return;
			}

			// The drop still goes into the building's own scene, so the player's position only
			// makes sense if they're in that same scene.
			GameScene playerScene = player.CurrentGameScene;
			if (playerScene == null || playerScene.Id != __instance.WorldId)
			{
				return;
			}

			__result = player.transform.position;
		}
	}
}
