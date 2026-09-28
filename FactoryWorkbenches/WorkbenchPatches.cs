using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace FactoryWorkbenches
{
	// The single switch that makes a factory bench "regular": everything in the game that asks a
	// workbench whether it is a ConveyorWorkbench goes through this getter (CraftInteractionHandler
	// .Interact, CraftComponent.ShouldRegisterInCraftSystem / TryDropConveyorWorkbenchCraftInventory /
	// AddToQueue paths / RemoveDestroyCraft / RemoveFromQueue, PlayerWorkComponent.CanWorkOn, and
	// the ICraftable interface). Answering Regular sends all of them down the normal workbench path,
	// so a zombie put on the bench becomes a Crafter (AttachToCraftWgoData) that posts
	// DeliveryOrder / PickupOrder for the caretaker.
	//
	// Postfix: a pure getter, the result is simply corrected on the way out.
	[HarmonyPatch(typeof(WgoData), nameof(WgoData.CraftableType), MethodType.Getter)]
	internal static class WgoData_CraftableType_Patch
	{
		private static void Postfix(WgoData __instance, ref CraftableType __result)
		{
			if (__result == CraftableType.ConveyorWorkbench && Factory.IsRegularMode(__instance))
			{
				__result = CraftableType.Regular;
			}
		}
	}

	// ConveyorWgoData replaces GetCraftableMultiInventory with "the bench's own inventories plus
	// fuel containers". A normal workbench uses WgoData's version: the worker's inventory plus every
	// OpenInMultiInventory container in the bench's world zone, which is what makes the conveyor
	// chests count as storage for start checks, the "space for outputs" check and recipe
	// availability. Converted benches get the WgoData result back. The stub below is a Harmony
	// reverse patch: it holds a copy of WgoData's own body, so it gives the base result even though
	// the method is virtual.
	//
	// Postfix: the override has no early exit that matters, so the result is replaced afterwards.
	[HarmonyPatch]
	internal static class WgoData_GetCraftableMultiInventory_Reverse
	{
		[HarmonyReversePatch]
		[HarmonyPatch(typeof(WgoData), nameof(WgoData.GetCraftableMultiInventory))]
		internal static MultiInventory BaseImplementation(WgoData instance, bool excludeWorkerInventory)
		{
			throw new NotImplementedException("Replaced by Harmony's reverse patch.");
		}
	}

	[HarmonyPatch(typeof(ConveyorWgoData), nameof(ConveyorWgoData.GetCraftableMultiInventory))]
	internal static class ConveyorWgoData_GetCraftableMultiInventory_Patch
	{
		private static void Postfix(ConveyorWgoData __instance, bool excludeWorkerInventory, ref MultiInventory __result)
		{
			if (Factory.IsRegularMode(__instance))
			{
				__result = WgoData_GetCraftableMultiInventory_Reverse.BaseImplementation(__instance, excludeWorkerInventory);
			}
		}
	}

	// CraftDefExtensions.CanActuallyStartCraft (the "can this recipe be started right now" flag
	// the craft window uses per recipe) skips the needItems check entirely on factory benches,
	// because belts were meant to supply them. Converted benches must check them against the
	// craftable MultiInventory like a normal bench.
	//
	// Postfix: the vanilla bypass result is an upper bound (it runs the same status check with an
	// empty need list), so only a "true" ever needs to be re-checked, and the re-check is a copy of
	// the method's own normal branch.
	[HarmonyPatch(typeof(CraftDefExtensions), nameof(CraftDefExtensions.CanActuallyStartCraft))]
	internal static class CraftDefExtensions_CanActuallyStartCraft_Patch
	{
		private static void Postfix(CraftDef craftDef, WgoData wgoData, ref bool __result)
		{
			if (!__result || !Factory.IsRegularMode(wgoData))
			{
				return;
			}
			if (craftDef.needItems == null || craftDef.needItems.Count == 0)
			{
				return;
			}

			CraftParamsData paramsData = new CraftParamsData(craftDef.id, wgoData, CraftParamsData.CraftParamsType.Common, -1);
			MultiInventory multiInventory = wgoData.GetCraftableMultiInventory(false);
			List<List<NeedItemData>> combinations = CraftDefExtensions.ExpandNeedItemCombinations(craftDef.needItems, multiInventory, wgoData);

			__result = false;
			foreach (List<NeedItemData> combination in combinations)
			{
				if (craftDef.CanActuallyStartCraftWithNeeds(combination, paramsData, wgoData))
				{
					__result = true;
					return;
				}
			}
		}
	}

	// CraftInteractionHandler.OnCraftPressed refuses to queue a recipe when the bench is a
	// factory bench and its queue already holds a different recipe id. The check is an early
	// return in the middle of the method (before the element is added), which a postfix cannot
	// undo, so this transpiler swaps the one "Definition.conveyorType" read in that method for a
	// call to QueueRule.TypeFor. The rest of the method is untouched.
	[HarmonyPatch(typeof(CraftInteractionHandler), "OnCraftPressed")]
	internal static class CraftInteractionHandler_OnCraftPressed_Patch
	{
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
			FieldInfo conveyorType = AccessTools.Field(typeof(WGODef), nameof(WGODef.conveyorType));
			MethodInfo helper = AccessTools.Method(typeof(QueueRule), nameof(QueueRule.TypeFor));

			List<int> hits = new List<int>();
			for (int i = 1; i < codes.Count; i++)
			{
				MethodInfo previous = codes[i - 1].operand as MethodInfo;
				if (codes[i].LoadsField(conveyorType) && codes[i - 1].opcode == OpCodes.Callvirt && previous != null && previous.Name == "get_Definition")
				{
					hits.Add(i);
				}
			}

			if (hits.Count != 1)
			{
				Plugin.Log.LogWarning("CraftInteractionHandler.OnCraftPressed: expected one conveyorType check, found " + hits.Count + ". The single-recipe queue limit is left as vanilla.");
				return codes;
			}

			// [WgoData] get_Definition() ldfld conveyorType  ->  [WgoData] QueueRule.TypeFor(WgoData) nop
			int at = hits[0];
			codes[at - 1].opcode = OpCodes.Call;
			codes[at - 1].operand = helper;
			codes[at].opcode = OpCodes.Nop;
			codes[at].operand = null;
			return codes;
		}
	}

	internal static class QueueRule
	{
		internal static ConveyorElementType TypeFor(WgoData bench)
		{
			ConveyorElementType type = bench.Definition.conveyorType;
			if (type == ConveyorElementType.Workbench && Plugin.LiftSingleRecipeQueueLimit.Value && Factory.IsRegularMode(bench))
			{
				return ConveyorElementType.None;
			}
			return type;
		}
	}

	// UICraftWindow.OnCraftStartPressed builds a ConveyorCraftElement (one item per craft cycle,
	// only meant for a ConveyorCrafter) whenever CraftDef.isConveyorCraft is set, and every recipe
	// of the factory benches has it set. Converted benches need a plain CraftElement. The flag is
	// read in the middle of the method, right before the element is constructed, and the created
	// element is only ever handed to the callback, so neither a postfix nor a later swap can get
	// at it in time. The transpiler replaces the one "def.isConveyorCraft" read with a call to
	// ConveyorCraftRule.IsConveyorCraft(def, window), so the balance data itself stays untouched.
	[HarmonyPatch(typeof(UICraftWindow), "OnCraftStartPressed")]
	internal static class UICraftWindow_OnCraftStartPressed_Patch
	{
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
			FieldInfo isConveyorCraft = AccessTools.Field(typeof(CraftDef), nameof(CraftDef.isConveyorCraft));
			MethodInfo helper = AccessTools.Method(typeof(ConveyorCraftRule), nameof(ConveyorCraftRule.IsConveyorCraft));

			List<int> hits = new List<int>();
			for (int i = 0; i < codes.Count; i++)
			{
				if (codes[i].LoadsField(isConveyorCraft))
				{
					hits.Add(i);
				}
			}

			if (hits.Count != 1)
			{
				Plugin.Log.LogWarning("UICraftWindow.OnCraftStartPressed: expected one isConveyorCraft read, found " + hits.Count + ". Converted benches will queue vanilla conveyor craft elements.");
				return codes;
			}

			// [CraftDef] ldfld isConveyorCraft  ->  [CraftDef] ldarg.0 ConveyorCraftRule.IsConveyorCraft(CraftDef, UICraftWindow)
			int at = hits[0];
			CodeInstruction loadThis = new CodeInstruction(OpCodes.Ldarg_0);
			loadThis.labels.AddRange(codes[at].labels);
			loadThis.blocks.AddRange(codes[at].blocks);
			codes[at] = loadThis;
			codes.Insert(at + 1, new CodeInstruction(OpCodes.Call, helper));
			return codes;
		}
	}

	internal static class ConveyorCraftRule
	{
		internal static bool IsConveyorCraft(CraftDef def, UICraftWindow window)
		{
			if (!def.isConveyorCraft)
			{
				return false;
			}
			Wgo bench = (window != null && window.data != null) ? window.data.AssignedWgo : null;
			return !Factory.IsRegularMode(bench != null ? bench.Data : null);
		}
	}

	// ZombieDeliveryIndication.IsCraftStalledWithoutCaretaker (the "no caretaker in this zone"
	// icon over a bench) returns false for every recipe with isConveyorCraft set. On a converted
	// bench the Crafter waits for a caretaker exactly like on a normal bench, so the icon should
	// show. The vanilla answer is only wrong in that one case, so the postfix redoes the same
	// checks without the isConveyorCraft condition.
	[HarmonyPatch(typeof(ZombieDeliveryIndication), nameof(ZombieDeliveryIndication.IsCraftStalledWithoutCaretaker))]
	internal static class ZombieDeliveryIndication_IsCraftStalledWithoutCaretaker_Patch
	{
		private static void Postfix(WgoData workbench, ref bool __result)
		{
			if (__result || workbench == null || workbench.Definition == null || workbench.isTempObject || workbench.IsHidden)
			{
				return;
			}
			if (!Factory.IsCrafterMode(workbench))
			{
				return;
			}

			ZombieWgoData zombie = workbench.Worker as ZombieWgoData;
			if (zombie == null || zombie.ZombieType != ZombieType.Crafter)
			{
				return;
			}

			CraftElementBase current = workbench.CraftComponent.CurrentCraftElement;
			if (current == null || current.IsStarted || !(current.Def is CraftDef))
			{
				return;
			}
			if (!(zombie.CrafterCurrentOrder is DeliveryOrder))
			{
				return;
			}

			WorldZoneData zone = workbench.WorldZoneData;
			__result = zone != null && !ZombieDeliveryIndication.HasCaretakerInZone(zone);
		}
	}
}
