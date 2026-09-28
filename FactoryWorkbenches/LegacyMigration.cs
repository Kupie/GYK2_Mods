using System;
using System.Collections.Generic;
using HarmonyLib;

namespace FactoryWorkbenches
{
	// Factories built before this mod have ConveyorCrafter zombies on their benches. Instead of
	// making the player pick each one up and put it back (which is what turns it into a Crafter),
	// every such zombie on a converted bench is re-attached as a Crafter right after a save loads,
	// the same two calls the game makes itself: UnAttachFromWgoData, then AttachToCraftWgoData. The
	// zombie keeps its place, its body and its bench; the result is an ordinary Crafter, so the
	// save only holds vanilla state.
	//
	// Postfix: it runs at the end of the game's own load step, after every zombie has been prepared.
	[HarmonyPatch(typeof(ZombieSystemData), nameof(ZombieSystemData.ResumeCrafterWorkAfterLoad))]
	internal static class ZombieSystemData_ResumeCrafterWorkAfterLoad_Migration_Patch
	{
		private static void Postfix(ZombieSystemData __instance)
		{
			// PrepareForUninstall wants the opposite: no mod-made state left in the save.
			if (!Plugin.Enabled.Value || Plugin.PrepareForUninstall.Value)
			{
				return;
			}

			int converted = 0;
			foreach (SGuid id in new List<SGuid>(__instance.zombieOnSceneWgoIds))
			{
				try
				{
					if (Convert(__instance, id))
					{
						converted++;
					}
				}
				catch (Exception e)
				{
					Plugin.Log.LogError("Could not convert zombie " + id + " to a Crafter: " + e);
				}
			}

			if (converted > 0)
			{
				Plugin.Log.LogInfo("Converted " + converted + " conveyor crafter zombie(s) on factory benches to regular Crafters.");
			}
		}

		private static bool Convert(ZombieSystemData zombies, SGuid id)
		{
			ZombieWgoData zombie = zombies.GetZombie(id);
			if (zombie == null || zombie.ZombieType != ZombieType.ConveyorCrafter)
			{
				return false;
			}

			WgoData bench = zombie.AttachedWgoData;
			if (bench == null || !Factory.IsConvertedBench(bench))
			{
				return false;
			}

			SGuid benchId = bench.UniqueId;
			Item zombieItem = zombie.ZombieItem;
			zombie.UnAttachFromWgoData(false);
			zombie.AttachToCraftWgoData(benchId, zombieItem, null);
			SettleFinishedCraft(bench, zombie);

			Plugin.Verbose("Converted the conveyor crafter on " + bench.id + " to a Crafter.");
			return true;
		}

		// A ConveyorCrafter parks a finished craft in WaitingForOutputDrop and only a belt empties it,
		// which a Crafter never does. Hand the waiting outputs to the normal pickup flow (an order
		// for the caretaker, or the output belt), or finish the craft if nothing is left to move.
		private static void SettleFinishedCraft(WgoData bench, ZombieWgoData zombie)
		{
			CraftComponent craft = bench.CraftComponent;
			if (craft.Status != CraftComponentStatus.WaitingForOutputDrop)
			{
				return;
			}

			CraftElementBase current = craft.CurrentCraftElement;
			Inventory inventory = bench.CraftableObjectCraftInventory;
			WorldZoneData zone = zombie.WorldZoneData;
			if (current == null || inventory == null || inventory.Data == null || zone == null)
			{
				return;
			}

			bool placed = false;
			HashSet<string> seen = new HashSet<string>();
			foreach (ItemCount output in current.PreOutputItems)
			{
				string itemId = output.Def != null ? output.Def.id : null;
				if (string.IsNullOrEmpty(itemId) || !seen.Add(itemId))
				{
					continue;
				}
				int count = inventory.Data.GetTotalCountInInventory(itemId, null, false);
				if (count > 0)
				{
					zone.PlaceNewOrder(new PickupOrder(zombie.UniqueId, new Item(itemId, count)));
					placed = true;
				}
			}

			if (placed)
			{
				craft.Status = CraftComponentStatus.WaitingForWorkerPickUp;
				current.PrevCraftComponentStatus = craft.Status;
			}
			else
			{
				craft.TryFinishCurCraft();
			}
		}
	}
}
