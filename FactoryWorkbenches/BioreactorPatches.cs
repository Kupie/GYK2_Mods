using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace FactoryWorkbenches
{
	// The bioreactor (conveyor_bioreactor) is an auto crafter that only belts feed, so vanilla has
	// canInsertZombie off and CraftInteractionHandler answers "nothing to do" for it. Converting it
	// means a zombie can stand on it as a normal Crafter and a caretaker can deliver its wheat.
	//
	// Everything else about it stays vanilla on purpose. Its CraftableType stays ConveyorWorkbench,
	// so it is still ticked (and its craft queue refilled) by the conveyor system only, at the same
	// speed and with the same power need as before. Making it Regular would register it in
	// CraftSystem as well, and both would tick the auto craft, doubling its speed. The same
	// vanilla code already runs a Crafter zombie on a ConveyorWorkbench-typed bench (see README).
	//
	// A zombie can only be put on it if its prefab has a dock point; that is scene data the game
	// files here do not show, so the first refusal for that reason is logged.
	internal static class BioreactorState
	{
		internal static bool loggedNoDockPoint;
	}

	// canInsertZombie is false for the bioreactor. Postfix: the vanilla method computes everything
	// else (interactor, free bench, dock points, carried zombie), so when it only failed on the
	// flag the postfix repeats the last step, the carried-zombie lookup.
	[HarmonyPatch(typeof(WGOInteractionHandlerBase), "TryGetInsertableZombieOverhead")]
	internal static class WGOInteractionHandlerBase_TryGetInsertableZombieOverhead_Patch
	{
		private static void Postfix(WGOInteractionHandlerBase __instance, ref bool __result, ref Item zombieItem)
		{
			if (__result || !(__instance is CraftInteractionHandler) || __instance.interactor == null)
			{
				return;
			}
			Wgo wgo = __instance.assignedWgo;
			if (wgo == null || wgo.Data == null || wgo.Data.Worker != null || !Factory.IsAutoMode(wgo.Data))
			{
				return;
			}
			if (wgo.DockPoints == null || wgo.DockPoints.Count == 0)
			{
				if (!BioreactorState.loggedNoDockPoint)
				{
					BioreactorState.loggedNoDockPoint = true;
					Plugin.Log.LogInfo(wgo.Data.id + " has no dock point, so a zombie cannot be put on it.");
				}
				return;
			}
			__result = __instance.interactor.PlayerData.TryGetOverheadItem((Item item) => item.Definition.itemGroupIds.Contains("zombie"), out zombieItem);
		}
	}

	// CraftInteractionHandler ends every interaction early for a conveyor auto crafter. Postfix:
	// the answer is only corrected to "no" while a zombie can be put on the bioreactor, and while a
	// Crafter zombie works on it (so orders can be served by hand like on any bench). Otherwise it
	// stays vanilla and the player cannot open the craft window on it.
	[HarmonyPatch(typeof(CraftInteractionHandler), "IsConveyorAutoCrafter")]
	internal static class CraftInteractionHandler_IsConveyorAutoCrafter_Patch
	{
		private static void Postfix(CraftInteractionHandler __instance, ref bool __result)
		{
			if (!__result)
			{
				return;
			}
			WgoData bench = __instance.assignedWgo != null ? __instance.assignedWgo.Data : null;
			if (bench == null || !Factory.IsAutoMode(bench))
			{
				return;
			}

			ZombieWgoData worker = bench.Worker as ZombieWgoData;
			if (worker != null)
			{
				if (worker.ZombieType == ZombieType.Crafter)
				{
					__result = false;
				}
				return;
			}
			if (bench.Worker == null && __instance.HasInsertableZombieOverhead())
			{
				__result = false;
			}
		}
	}

	// CraftInteractionHandler.Interact picks the zombie's role from WgoData.CraftableType right
	// after taking the zombie out of the player's hands: ConveyorWorkbench makes a ConveyorCrafter,
	// anything else a Crafter. The bioreactor keeps the ConveyorWorkbench type (see above) but must
	// get a Crafter. That decision sits in the middle of Interact, after side effects, so a postfix
	// cannot redo it. The transpiler swaps the one CraftableType read for a call to
	// AttachRole.TypeFor, which answers Regular for the converted bioreactor and the real value
	// otherwise.
	[HarmonyPatch(typeof(CraftInteractionHandler), nameof(CraftInteractionHandler.Interact))]
	internal static class CraftInteractionHandler_Interact_Patch
	{
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
			MethodInfo getter = AccessTools.PropertyGetter(typeof(WgoData), nameof(WgoData.CraftableType));
			MethodInfo helper = AccessTools.Method(typeof(AttachRole), nameof(AttachRole.TypeFor));

			List<int> hits = new List<int>();
			for (int i = 0; i < codes.Count; i++)
			{
				if (codes[i].Calls(getter))
				{
					hits.Add(i);
				}
			}

			if (hits.Count != 1)
			{
				Plugin.Log.LogWarning("CraftInteractionHandler.Interact: expected one CraftableType read, found " + hits.Count + ". The bioreactor cannot take a Crafter zombie.");
				return codes;
			}

			codes[hits[0]].opcode = OpCodes.Call;
			codes[hits[0]].operand = helper;
			return codes;
		}
	}

	internal static class AttachRole
	{
		internal static CraftableType TypeFor(WgoData bench)
		{
			CraftableType type = bench.CraftableType;
			if (type == CraftableType.ConveyorWorkbench && Factory.IsAutoMode(bench))
			{
				return CraftableType.Regular;
			}
			return type;
		}
	}
}
