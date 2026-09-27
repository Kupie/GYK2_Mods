using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace DeconstructToPlayer
{
	// Every item a building gives back when it's removed - its refund, its stored inventory,
	// and the inputs of any crafts still queued on it - is dropped through
	// WgoData.MakeDrop(Item), which places it at WgoData.GetDropPos(Item): the building's own
	// drop point / dock point. This moves that position onto the player instead, so the
	// player's pickup magnet (DropSearcher/DropCollector trigger colliders) grabs the items
	// as soon as their short spawn collect-delay ends.
	//
	// Only drops made while a specific building is being deconstructed are redirected - see
	// DropRedirect - so ordinary crafting output, harvests, etc. still land where they normally do.
	[BepInPlugin("kupie.gk2.deconstructtoplayer", "Deconstruct To Player", "1.0.0")]
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

	// The building currently being deconstructed. Set for the duration of the call that drops
	// its items and restored afterwards (by a Finalizer, so an exception can't leave it stuck).
	// Previous values are saved rather than cleared, in case these scopes ever nest.
	internal static class DropRedirect
	{
		internal static WgoData Target;
	}

	[HarmonyPatch(typeof(WgoData), nameof(WgoData.GetDropPos))]
	internal static class WgoData_GetDropPos_Patch
	{
		private static void Postfix(WgoData __instance, ref Vector3 __result)
		{
			if (DropRedirect.Target == null || __instance != DropRedirect.Target)
			{
				return;
			}

			PlayerController player = MainGame.PlayerController;
			if (player == null)
			{
				return;
			}

			// MakeDrop still drops into the building's own scene (its worldId), so a player
			// position is only meaningful if the player is standing in that same scene.
			GameScene playerScene = player.CurrentGameScene;
			if (playerScene == null || playerScene.Id != __instance.WorldId)
			{
				return;
			}

			__result = player.transform.position;
		}
	}

	// Instant removal from build mode's Remove tool: refund + stored inventory are dropped
	// right here. Priority.First so the target is already set if another mod (e.g. a refund
	// mod) replaces DoBuildRemove with its own skipping prefix that drops items itself.
	[HarmonyPatch(typeof(Wgo), nameof(Wgo.DoBuildRemove))]
	internal static class Wgo_DoBuildRemove_Patch
	{
		[HarmonyPriority(Priority.First)]
		private static void Prefix(Wgo __instance, out WgoData __state)
		{
			__state = DropRedirect.Target;
			DropRedirect.Target = __instance.Data;
		}

		private static void Finalizer(WgoData __state)
		{
			DropRedirect.Target = __state;
		}
	}

	// Deconstruct-over-time removal: DoBuildRemove only queues an isObjDestroyCraft craft, and
	// the refund is dropped later when that craft finishes. Finish() covers both its
	// HandleOutput() call and the inputs of other queued crafts it hands back afterwards.
	[HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.Finish))]
	internal static class CraftComponent_Finish_Patch
	{
		private static void Prefix(CraftComponent __instance, out WgoData __state)
		{
			__state = DropRedirect.Target;
			if (__instance.IsDestroyingCraftActive)
			{
				DropRedirect.Target = __instance.craftableObject as WgoData;
			}
		}

		private static void Finalizer(WgoData __state)
		{
			DropRedirect.Target = __state;
		}
	}

	// HandleOutput() is also reached outside Finish() (ProcessInstantCraft), so it's scoped
	// on its own too, keyed off the craft being handled rather than the current one.
	[HarmonyPatch(typeof(CraftComponent), nameof(CraftComponent.HandleOutput))]
	internal static class CraftComponent_HandleOutput_Patch
	{
		private static void Prefix(CraftComponent __instance, CraftElementBase craftElementBase, out WgoData __state)
		{
			__state = DropRedirect.Target;
			if (craftElementBase?.Def is CraftDef craftDef && craftDef.isObjDestroyCraft)
			{
				DropRedirect.Target = __instance.craftableObject as WgoData;
			}
		}

		private static void Finalizer(WgoData __state)
		{
			DropRedirect.Target = __state;
		}
	}
}
