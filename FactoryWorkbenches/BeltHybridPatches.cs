using System;
using System.Collections.Generic;
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
			if (bench == null || bench.WgoData == null || !Factory.IsCrafterMode(bench.WgoData))
			{
				return false;
			}
			zombie = bench.WgoData.Worker as ZombieWgoData;
			return zombie != null && zombie.ZombieType == ZombieType.Crafter;
		}

		internal static List<ConveyorWgoData> ConnectedOutCells(ConveyorWorkbenchComponent bench)
		{
			List<ConveyorWgoData> result = new List<ConveyorWgoData>();
			foreach (ConveyorWgoData cell in bench.ConveyorOutWgoDataList)
			{
				if (cell != null)
				{
					result.Add(cell);
				}
			}
			return result;
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
			if (bench == null || !Factory.IsCrafterMode(data))
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

			foreach (ConveyorWgoData cell in ConnectedOutCells(bench))
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

		// Chests refuse "overhead" items and big items never fit the belt logic, so those stay with
		// the caretaker. An empty PickupOrder (CrafterFinishAndContinueAfterBigItemDropped) has no
		// item at all.
		private static bool BeltCanCarry(Item item)
		{
			if (item == null || string.IsNullOrEmpty(item.id) || item.Count <= 0 || item.Definition == null)
			{
				return false;
			}
			return item.Definition.itemSize != ItemSize.Big && !item.Definition.itemGroupIds.Contains("overhead");
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
}
