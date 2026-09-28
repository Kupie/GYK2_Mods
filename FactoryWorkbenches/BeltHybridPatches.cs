using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FactoryWorkbenches
{
	// Hybrid belt support. A converted bench stays on the regular Crafter state
	// machine the whole time: no WaitingForOutputDrop, no ConveyorCraftElement. The belts are bolted
	// on around it:
	//
	//   in:  ConveyorWorkbenchComponent.DoJobIn (vanilla, untouched) already pulls the first queued
	//        recipe's missing materials from an adjacent belt into CraftableObjectCraftInventory,
	//        which for a Crafter zombie is also its WorkerInventory. A postfix then cancels the
	//        caretaker deliveries that the belt just made pointless.
	//   out: a postfix on DoJobOut puts a finished output, one item per tick per output cell, onto
	//        an out belt, and settles the matching PickupOrder the way a caretaker would.
	//
	// Nobody handles an item twice because the caretaker and the belt only ever act on orders whose
	// ExecutorUniqueId is empty, and the caretaker claims an order (sets that id) the instant it
	// takes it. Everything runs on the main thread, so there is no window between the check and
	// the claim.
	internal static class BeltHybrid
	{
		private static readonly Dictionary<Guid, float> firstOffered = new Dictionary<Guid, float>();
		private static bool loggedFailure;

		// Outputs follow OutputPreference alone; only inputs depend on Belt Inputs Allowed.
		internal static bool OutputsActive
		{
			get { return Plugin.Enabled.Value; }
		}

		internal static bool InputsActive
		{
			get { return Plugin.Enabled.Value && Plugin.BeltInputsAllowed.Value; }
		}

		internal static void LogFailureOnce(string where, Exception e)
		{
			if (loggedFailure)
			{
				return;
			}
			loggedFailure = true;
			Plugin.Log.LogError("Hybrid belt handling failed in " + where + " (further failures are not logged): " + e);
		}

		// The converted bench's belt component and its Crafter zombie, if the bench is currently
		// running the regular Crafter flow.
		internal static bool TryGetCrafter(ConveyorWorkbenchComponent bench, out ZombieWgoData zombie)
		{
			zombie = null;
			if (bench == null || bench.WgoData == null || !Factory.IsRegularMode(bench.WgoData))
			{
				return false;
			}
			zombie = bench.WgoData.Worker as ZombieWgoData;
			return zombie != null && zombie.ZombieType == ZombieType.Crafter;
		}

		// The bench's out cells that a belt actually leads away from. Every bench has its own
		// single-slot cell attached as its output; with nothing pulling from it, an item put there
		// just sits (and can be reached by neither belt nor caretaker), so such a cell is not an
		// out belt. A cell counts once some other conveyor element lists it as a parent, because
		// that is how items move on: every element pulls from its parents.
		internal static List<ConveyorWgoData> ConnectedOutCells(ConveyorWorkbenchComponent bench)
		{
			List<ConveyorWgoData> result = new List<ConveyorWgoData>();
			foreach (ConveyorWgoData cell in bench.ConveyorOutWgoDataList)
			{
				if (cell != null && HasConsumer(cell))
				{
					result.Add(cell);
				}
			}
			return result;
		}

		private static bool HasConsumer(ConveyorWgoData cell)
		{
			List<ConveyorComponent> all = MainGame.Instance.GameSave.conveyorSystemData.conveyorComponents;
			for (int i = 0; i < all.Count; i++)
			{
				ConveyorComponent other = all[i];
				if (other == null || other == cell.ConveyorComponent)
				{
					continue;
				}
				foreach (ConveyorWgoData parent in other.ParentsData.Values)
				{
					if (parent == cell)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static int PendingPickupCount(ZombieWgoData zombie, WorldZoneData zone, string itemId)
		{
			int count = 0;
			foreach (SGuid id in zombie.CrafterOrders)
			{
				PickupOrder pickup = zone.FindOrder(id) as PickupOrder;
				if (pickup != null && pickup.Item.id == itemId)
				{
					count += pickup.Item.Count;
				}
			}
			return count;
		}

		// ---- inputs ------------------------------------------------------------------------

		// Removes the Crafter's unclaimed DeliveryOrders that the bench inventory now fully covers.
		// Claimed orders are left alone: cancelling one mid-trip can leave the caretaker walking to
		// the station with a partly picked-up stack in its hands (CaretakerOnOrderRemoved only
		// re-routes it to a chest from a few of its states), and letting it finish just leaves a
		// spare set of materials in the bench inventory that the next craft uses.
		internal static void ReconcileDeliveries(ConveyorWorkbenchComponent bench)
		{
			ZombieWgoData zombie;
			if (!TryGetCrafter(bench, out zombie))
			{
				return;
			}
			WorldZoneData zone = zombie.WorldZoneData;
			Inventory craftInventory = bench.WgoData.CraftableObjectCraftInventory;
			if (zone == null || craftInventory == null || craftInventory.Data == null)
			{
				return;
			}

			bool removedAny = false;
			Dictionary<string, int> reserved = new Dictionary<string, int>();
			foreach (SGuid id in new List<SGuid>(zombie.CrafterOrders))
			{
				DeliveryOrder delivery = zone.FindOrder(id) as DeliveryOrder;
				if (delivery == null || !SGuid.IsNullOrEmpty(delivery.ExecutorUniqueId))
				{
					continue;
				}

				string itemId = delivery.Item.id;
				int already;
				reserved.TryGetValue(itemId, out already);
				int available = craftInventory.Data.GetTotalCountInInventory(itemId, null, false) - PendingPickupCount(zombie, zone, itemId);
				if (available - already < delivery.Item.Count)
				{
					continue;
				}

				reserved[itemId] = already + delivery.Item.Count;
				zone.RemoveOrder(delivery.UniqueId);
				removedAny = true;
				Plugin.Verbose("Belt covered the delivery of " + delivery.Item.Count + " " + itemId + " to " + bench.WgoData.id + ", order cancelled.");
			}

			// The Crafter starts the craft (or places orders for what is still missing) from
			// CrafterTryPlaceOrderForCurrentCraftOrStartIt. It returns at once while any order is
			// left, so calling it here is safe; the vanilla triggers (order executed, craft queued)
			// do not fire for an order that was cancelled instead of executed.
			if (removedAny || (zombie.CrafterOrders.Count == 0 && zombie.ZombieCraftActivity == null))
			{
				zombie.CrafterTryPlaceOrderForCurrentCraftOrStartIt();
			}
		}

		// ---- outputs -----------------------------------------------------------------------

		// Can a belt take this order's item? True when outputs are handled by this mod, the bench has an output
		// belt connected, and the conveyor system is running and powered.
		internal static bool IsBeltEligible(PickupOrder order)
		{
			if (!OutputsActive || Plugin.OutputPreference.Value == OutputPreferenceMode.NoBelts)
			{
				return false;
			}
			ZombieWgoData zombie = order.ZombieWgoData;
			if (zombie == null || zombie.ZombieType != ZombieType.Crafter)
			{
				return false;
			}
			ConveyorWgoData data = zombie.AttachedWgoData as ConveyorWgoData;
			ConveyorWorkbenchComponent bench = data != null ? data.ConveyorComponent as ConveyorWorkbenchComponent : null;
			if (bench == null || !Factory.IsRegularMode(data))
			{
				return false;
			}
			if (!BeltCanCarry(order.Item) || !ConveyorRunning())
			{
				return false;
			}
			return ConnectedOutCells(bench).Count > 0;
		}

		// Should caretakers leave this PickupOrder alone for now?
		internal static bool IsDeferredToBelt(PickupOrder order)
		{
			Guid key = order.UniqueId.Guid;
			if (IsSupplyBox(order.Item))
			{
				// caretakers cannot store these; the belt or the drop fallback deals with them
				return BeltsWanted;
			}
			if (!IsBeltEligible(order))
			{
				firstOffered.Remove(key);
				return false;
			}
			if (Plugin.OutputPreference.Value == OutputPreferenceMode.BeltOnly)
			{
				return true;
			}

			float now = Time.time;
			float since;
			if (!firstOffered.TryGetValue(key, out since))
			{
				if (firstOffered.Count > 256)
				{
					firstOffered.Clear();
				}
				since = now;
				firstOffered[key] = since;
			}
			return now - since < Plugin.BeltFirstDelaySeconds.Value;
		}

		// The Crafter's oldest unclaimed PickupOrder whose item is actually in the craft inventory.
		private static PickupOrder NextPickup(ZombieWgoData zombie, WorldZoneData zone, Inventory craftInventory)
		{
			foreach (SGuid id in new List<SGuid>(zombie.CrafterOrders))
			{
				PickupOrder pickup = zone.FindOrder(id) as PickupOrder;
				if (pickup == null || !SGuid.IsNullOrEmpty(pickup.ExecutorUniqueId))
				{
					continue;
				}
				if (!BeltCanCarry(pickup.Item))
				{
					continue;
				}
				if (craftInventory.Data.HasItemQuantityInInventory(pickup.Item.id, 1))
				{
					return pickup;
				}
			}
			return null;
		}

		// Mirrors ConveyorWorkbenchComponent.PutItemToConveyor, but takes the item from the pending
		// PickupOrder instead of Inventory[0] (inputs and outputs share the craft inventory).
		internal static void PushOutputs(ConveyorWorkbenchComponent bench)
		{
			ZombieWgoData zombie;
			if (!TryGetCrafter(bench, out zombie))
			{
				return;
			}
			ConveyorWgoData data = bench.WgoData;
			if (data.CraftComponent.IsDestroyingCraftActive)
			{
				return;
			}
			WorldZoneData zone = zombie.WorldZoneData;
			Inventory craftInventory = data.CraftableObjectCraftInventory;
			if (zone == null || craftInventory == null || craftInventory.Data == null)
			{
				return;
			}
			if (!ConveyorRunning())
			{
				return;
			}

			List<ConveyorWgoData> cells = ConnectedOutCells(bench);
			DropStrandedSupplyBoxes(data, zombie, zone, craftInventory, cells.Count > 0);

			foreach (ConveyorWgoData cell in cells)
			{
				if (cell.Inventory.Data.Inventory.Count != 0)
				{
					continue;
				}

				PickupOrder order = NextPickup(zombie, zone, craftInventory);
				if (order == null)
				{
					return;
				}

				string itemId = order.Item.id;
				List<Item> taken = craftInventory.RemoveItemById(itemId, 1, null, null, false);
				if (taken.Count == 0)
				{
					continue;
				}

				Direction direction;
				bench.TryGetConnectorDirection(cell.UniqueId, out direction);
				cell.ConveyorComponent.InItem = new ConveyorMovableItemData(itemId, direction, false);
				cell.Inventory.AddItemsToInventory(taken);
				bench.wasPerformedItemTransfer = true;
				Plugin.Verbose("Put " + itemId + " from " + data.id + " on an output belt.");

				order.Item.Count -= 1;
				if (order.Item.Count <= 0)
				{
					CompletePickup(data, zombie, zone, order);
				}
				RestartDelay(zombie, zone);
			}
		}

		// A belt that just moved an item is working, so the BeltFirst delay starts over for all of
		// this Crafter's pending outputs: a slow belt keeps a big batch instead of a caretaker
		// coming for the leftovers.
		private static void RestartDelay(ZombieWgoData zombie, WorldZoneData zone)
		{
			float now = Time.time;
			foreach (SGuid id in new List<SGuid>(zombie.CrafterOrders))
			{
				PickupOrder pickup = zone.FindOrder(id) as PickupOrder;
				if (pickup != null && SGuid.IsNullOrEmpty(pickup.ExecutorUniqueId))
				{
					firstOffered[pickup.UniqueId.Guid] = now;
				}
			}
		}

		private static bool ConveyorRunning()
		{
			ConveyorSystem system = MainGame.Instance.conveyorSystem;
			return system != null && !system.IsPaused && system.HasEnoughPower;
		}

		// Anything real can ride a belt, big and "overhead" items included (they end up in a big
		// items chest, like on a vanilla factory bench). An empty PickupOrder
		// (CrafterFinishAndContinueAfterBigItemDropped) has no item at all.
		private static bool BeltCanCarry(Item item)
		{
			return item != null && !string.IsNullOrEmpty(item.id) && item.Count > 0 && item.Definition != null;
		}

		// Supply boxes (item group town_box): the game never puts these in a crafter's inventory,
		// it drops them on the ground, and ordinary chests refuse them (they are "overhead" items),
		// so a caretaker that took one would have nowhere to put it.
		internal static bool IsSupplyBox(Item item)
		{
			return item != null && item.Definition != null && item.Definition.itemGroupIds.Contains("town_box");
		}

		private static bool BeltsWanted
		{
			get { return OutputsActive && Plugin.OutputPreference.Value != OutputPreferenceMode.NoBelts; }
		}

		// True when a supply box made by this bench should be handled as a normal pickup (and so
		// travel on the belt) instead of being dropped on the ground: a converted bench with a
		// Crafter, belts wanted, the conveyor running and an output belt that leads somewhere.
		internal static bool SupplyBoxGoesOnBelt(WgoData bench)
		{
			ConveyorWgoData data = bench as ConveyorWgoData;
			ConveyorWorkbenchComponent component = data != null ? data.ConveyorComponent as ConveyorWorkbenchComponent : null;
			ZombieWgoData zombie;
			if (!BeltsWanted || component == null || !TryGetCrafter(component, out zombie) || !ConveyorRunning())
			{
				return false;
			}
			return ConnectedOutCells(component).Count > 0;
		}

		private static bool WaitedLongEnough(PickupOrder order)
		{
			Guid key = order.UniqueId.Guid;
			float now = Time.time;
			float since;
			if (!firstOffered.TryGetValue(key, out since))
			{
				firstOffered[key] = now;
				return false;
			}
			return now - since >= Plugin.BeltFirstDelaySeconds.Value;
		}

		// A supply box that no belt takes falls back to what the game does with it anyway: it is
		// dropped on the ground and the pickup counts as done. That happens at once when no output
		// belt leads anywhere, and after the Belt First delay with Belt First; Belt Only keeps
		// waiting for its belt.
		private static void DropStrandedSupplyBoxes(WgoData bench, ZombieWgoData zombie, WorldZoneData zone, Inventory craftInventory, bool haveBelt)
		{
			foreach (SGuid id in new List<SGuid>(zombie.CrafterOrders))
			{
				PickupOrder pickup = zone.FindOrder(id) as PickupOrder;
				if (pickup == null || !SGuid.IsNullOrEmpty(pickup.ExecutorUniqueId) || !IsSupplyBox(pickup.Item))
				{
					continue;
				}
				if (!craftInventory.Data.HasItemQuantityInInventory(pickup.Item.id, pickup.Item.Count))
				{
					continue;
				}
				if (haveBelt && (Plugin.OutputPreference.Value == OutputPreferenceMode.BeltOnly || !WaitedLongEnough(pickup)))
				{
					continue;
				}

				string itemId = pickup.Item.id;
				foreach (Item item in craftInventory.RemoveItemById(itemId, pickup.Item.Count, null, null, false))
				{
					MainGame.Instance.dropSystem.DropItem(item, bench.WorldId, bench.GetDropPos(item), null);
				}
				Plugin.Verbose("Dropped " + itemId + " from " + bench.id + " on the ground: no belt took it.");
				CompletePickup(bench, zombie, zone, pickup);
			}
		}

		// Same tail as PickupOrder.ExecuteOrder followed by the caretaker's RemoveOrder and
		// CrafterOnOrderExecuted: the first completed pickup finishes the craft.
		private static void CompletePickup(WgoData bench, ZombieWgoData zombie, WorldZoneData zone, PickupOrder order)
		{
			firstOffered.Remove(order.UniqueId.Guid);
			if (bench.CraftComponent.Status == CraftComponentStatus.WaitingForWorkerPickUp)
			{
				bench.CraftComponent.TryFinishCurCraft();
				bench.DropStoredTechPoints();
			}
			zone.RemoveOrder(order.UniqueId);
			zombie.CrafterOnOrderExecuted(order);
		}

		// The best order for a caretaker when the top pick had to be skipped. Same rules as
		// WorldZoneData.GetOrderForCaretaker, plus the belt deferral.
		internal static OrderBase BestOrderForCaretaker(WorldZoneData zone, Item executorCurrentItem)
		{
			OrderBase best = null;
			int bestPriority = int.MinValue;
			foreach (OrderBase order in zone.orders)
			{
				if (!order.ExecutorUniqueId.IsEmpty || order is ConveyorPickupOrder)
				{
					continue;
				}

				PickupOrder pickup = order as PickupOrder;
				if (pickup != null && IsDeferredToBelt(pickup))
				{
					continue;
				}

				DeliveryOrder delivery = order as DeliveryOrder;
				if (delivery != null)
				{
					int carried = executorCurrentItem == null ? 0 : (executorCurrentItem.id == delivery.Item.id ? executorCurrentItem.Count : 0);
					if (!zone.CanDeliveryOrderBeTakenOnExecution(delivery, carried))
					{
						continue;
					}
				}

				int priority = order.GetPriority();
				if (priority > bestPriority)
				{
					bestPriority = priority;
					best = order;
				}
			}
			return best;
		}
	}

	// Input side. Postfix: DoJobIn is left completely alone (it is what feeds the bench), this only
	// reacts afterwards when it moved something.
	[HarmonyPatch(typeof(ConveyorWorkbenchComponent), nameof(ConveyorWorkbenchComponent.DoJobIn))]
	internal static class ConveyorWorkbenchComponent_DoJobIn_Patch
	{
		private static void Postfix(ConveyorWorkbenchComponent __instance)
		{
			if (!BeltHybrid.InputsActive || !__instance.wasPerformedItemTransfer)
			{
				return;
			}
			try
			{
				BeltHybrid.ReconcileDeliveries(__instance);
			}
			catch (Exception e)
			{
				BeltHybrid.LogFailureOnce("DoJobIn", e);
			}
		}
	}

	// Output side. Postfix: the vanilla DoJobOut only acts on WaitingForOutputDrop, which converted
	// benches never reach, so after it has run this pushes pending Crafter outputs onto out belts.
	[HarmonyPatch(typeof(ConveyorWorkbenchComponent), nameof(ConveyorWorkbenchComponent.DoJobOut))]
	internal static class ConveyorWorkbenchComponent_DoJobOut_Patch
	{
		private static void Postfix(ConveyorWorkbenchComponent __instance)
		{
			if (!BeltHybrid.OutputsActive || Plugin.OutputPreference.Value == OutputPreferenceMode.NoBelts)
			{
				return;
			}
			try
			{
				BeltHybrid.PushOutputs(__instance);
			}
			catch (Exception e)
			{
				BeltHybrid.LogFailureOnce("DoJobOut", e);
			}
		}
	}

	// Keeps caretakers off outputs the belt is about to take (BeltFirst for a grace period,
	// BeltOnly for good). Postfix: the vanilla method picks the best order in one pass; only when
	// that pick is a deferred PickupOrder is the pass repeated without it.
	[HarmonyPatch(typeof(WorldZoneData), nameof(WorldZoneData.GetOrderForCaretaker))]
	internal static class WorldZoneData_GetOrderForCaretaker_Patch
	{
		private static void Postfix(WorldZoneData __instance, Item executorCurrentItem, ref OrderBase __result)
		{
			PickupOrder pickup = __result as PickupOrder;
			if (pickup == null || !BeltHybrid.OutputsActive)
			{
				return;
			}
			try
			{
				if (BeltHybrid.IsDeferredToBelt(pickup))
				{
					__result = BeltHybrid.BestOrderForCaretaker(__instance, executorCurrentItem);
				}
			}
			catch (Exception e)
			{
				BeltHybrid.LogFailureOnce("GetOrderForCaretaker", e);
			}
		}
	}

	// WgoData.MakeDrop(Item) drops a supply box on the ground for any crafter zombie, so a supply
	// box made on a converted bench never reaches a belt. The decision is one List<string>.Contains
	// call ("does this item have the town_box group") that picks the drop branch in the middle of
	// the method, and the drop itself cannot be taken back afterwards, so a postfix is too late.
	// The transpiler replaces that one Contains call with SupplyBoxRule.IsDroppedSupplyBox, which
	// answers false for a converted bench whose supply box should go on a belt. The item then takes
	// the method's normal crafter path (CrafterAddCraftDrop: a PickupOrder plus the craft
	// inventory), which the belt code above already handles. Every other case is unchanged.
	[HarmonyPatch(typeof(WgoData), nameof(WgoData.MakeDrop), new[] { typeof(Item) })]
	internal static class WgoData_MakeDrop_Patch
	{
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
			MethodInfo contains = AccessTools.Method(typeof(List<string>), nameof(List<string>.Contains));
			MethodInfo helper = AccessTools.Method(typeof(SupplyBoxRule), nameof(SupplyBoxRule.IsDroppedSupplyBox));

			List<int> hits = new List<int>();
			for (int i = 1; i < codes.Count; i++)
			{
				if (codes[i].Calls(contains) && codes[i - 1].opcode == OpCodes.Ldstr && (codes[i - 1].operand as string) == "town_box")
				{
					hits.Add(i);
				}
			}

			if (hits.Count != 1)
			{
				Plugin.Log.LogWarning("WgoData.MakeDrop: expected one town_box check, found " + hits.Count + ". Supply boxes will not go on belts.");
				return codes;
			}

			// [groups, "town_box"] Contains  ->  [groups, "town_box"] ldarg.0 IsDroppedSupplyBox(groups, group, bench)
			int at = hits[0];
			CodeInstruction loadThis = new CodeInstruction(OpCodes.Ldarg_0);
			loadThis.labels.AddRange(codes[at].labels);
			codes[at].labels.Clear();
			codes.Insert(at, loadThis);
			codes[at + 1].opcode = OpCodes.Call;
			codes[at + 1].operand = helper;
			return codes;
		}
	}

	internal static class SupplyBoxRule
	{
		internal static bool IsDroppedSupplyBox(List<string> groups, string group, WgoData bench)
		{
			if (!groups.Contains(group))
			{
				return false;
			}
			try
			{
				return !BeltHybrid.SupplyBoxGoesOnBelt(bench);
			}
			catch (Exception e)
			{
				BeltHybrid.LogFailureOnce("MakeDrop", e);
				return true;
			}
		}
	}
}
