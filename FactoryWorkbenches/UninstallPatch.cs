using System;
using System.Collections.Generic;
using HarmonyLib;

namespace FactoryWorkbenches
{
	// Opt-in clean-up before the mod is removed (Maintenance.PrepareForUninstall). Runs once, right
	// after a save has been loaded (ZombieSystemData.ResumeCrafterWorkAfterLoad, which the game
	// calls at the end of loading for every zombie). Every Crafter zombie standing on a factory
	// bench is taken off it the same way a bench deconstruction ejects its zombie
	// (CraftComponent.AddDestroyCraft: drop the body, UnAttachFromWgoData, PutZombieFromGameSceneToStore),
	// and UnAttachFromWgoData already clears the zombie's Delivery/Pickup orders and returns its
	// carried materials to the ground. Zombies that vanilla put there (ConveyorCrafter) are left alone.
	//
	// Postfix: it only needs to run after the vanilla resume step.
	[HarmonyPatch(typeof(ZombieSystemData), nameof(ZombieSystemData.ResumeCrafterWorkAfterLoad))]
	internal static class ZombieSystemData_ResumeCrafterWorkAfterLoad_Patch
	{
		private static void Postfix(ZombieSystemData __instance)
		{
			if (!Plugin.PrepareForUninstall.Value)
			{
				return;
			}

			int ejected = 0;
			int failed = 0;
			foreach (SGuid id in new List<SGuid>(__instance.zombieOnSceneWgoIds))
			{
				try
				{
					if (Eject(__instance, id))
					{
						ejected++;
					}
				}
				catch (Exception e)
				{
					failed++;
					Plugin.Log.LogError("PrepareForUninstall: could not take zombie " + id + " off its bench: " + e);
				}
			}

			Plugin.Log.LogInfo("PrepareForUninstall: took " + ejected + " zombie(s) off factory benches" + (failed > 0 ? ", " + failed + " failed (see errors above)" : "") + ". The option has switched itself off; save the game, then remove the mod.");
			Plugin.PrepareForUninstall.Value = false;
		}

		private static bool Eject(ZombieSystemData zombies, SGuid id)
		{
			ZombieWgoData zombie = zombies.GetZombie(id);
			if (zombie == null || zombie.ZombieType != ZombieType.Crafter)
			{
				return false;
			}

			WgoData bench = zombie.AttachedWgoData;
			if (bench == null || bench.Definition == null || bench.Definition.conveyorType != ConveyorElementType.Workbench)
			{
				return false;
			}

			SGuid zombieId = zombie.UniqueId;
			WorldZoneData zone = bench.WorldZoneData;

			MainGame.Instance.dropSystem.DropItem(zombie.ZombieItem, bench.WorldId, bench.GetDropPos(zombie.ZombieItem), null);
			zombie.UnAttachFromWgoData(false);
			if (zone != null)
			{
				zone.RemoveOrdersByTarget(zombieId);
			}
			zombies.PutZombieFromGameSceneToStore(zombie);

			Plugin.Verbose("PrepareForUninstall: took a zombie off " + bench.id);
			return true;
		}
	}
}
