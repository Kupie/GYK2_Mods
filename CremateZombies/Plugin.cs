using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using LazyBearTechnology;

namespace CremateZombies
{
	// Lets a zombie the player is carrying be put into a crematorium, the same way a corpse is.
	//
	// A carried zombie is two things: its "body_zombie" item on the player's overhead stack,
	// whose inventory holds the body parts plus the equipped collar/weapon/armor, and a
	// ZombieWgoData (name, talent tree, perks, tech points) kept in ZombieSystemData's store
	// under the same unique id. Cremating one:
	//   - rebuilds a plain "body_corpse" item from the body parts, puts it in the crematorium
	//     and starts the crematorium's own burn craft, so the output is whatever a normal
	//     corpse gives;
	//   - hands the equipped collar (and weapon/armor, if any) back to the player;
	//   - optionally (DropOrgans) drops everything else in the body - organs, blood, fat,
	//     embalming items - at the player's feet instead of burning it;
	//   - deletes the ZombieWgoData from ZombieSystemData, returns its name to the free-name
	//     pool, and lowers cur_zombies_count by 1.
	[BepInPlugin("kupie.gk2.crematezombies", "Cremate Zombies", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<bool> DropOrgans;

		private Harmony harmony;

		private void Awake()
		{
			DropOrgans = Config.Bind(
				"General",
				"Drop Organs",
				false,
				"When a zombie is cremated, drop everything inside its body at your feet instead of burning it: brain, heart, guts, skin, skull, bones, blood, fat, flesh, embalming items and so on. The burial certificate still burns with the body, as it does for a corpse. The crematorium still runs its normal burn on the now-empty body. Takes effect immediately, no restart needed.");

			harmony = new Harmony("kupie.gk2.crematezombies");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}
	}

	internal static class ZombieCremation
	{
		private const string BodyCorpseItemId = "body_corpse";
		private const string ZombieGroupId = "zombie";
		private const string BurialRewardGroupId = "burial_reward";
		private const string CurZombiesCountKey = "cur_zombies_count";
		private const string ExcessiveZombieDebuff = "debuff_excessive_zombie";

		// Mirrors the checks vanilla HasInteraction makes before offering "Cremate body", except
		// that the overhead item is a zombie. Vanilla gets first pick: if a real corpse is being
		// carried too, that one goes in instead.
		internal static bool CanCremate(CrematoriumInteractionHandler handler, out Item zombieItem, out ZombieWgoData zombie)
		{
			zombieItem = null;
			zombie = null;

			WgoData wgoData = handler.assignedWgo?.Data;
			CraftComponent craftComponent = wgoData?.CraftComponent;
			if (craftComponent == null || craftComponent.IsStarted || craftComponent.Status == CraftComponentStatus.ReadyToFinishAutoCraft)
			{
				return false;
			}

			if (craftComponent.AvailableCrafts == null || craftComponent.AvailableCrafts.Count == 0)
			{
				return false;
			}

			if (handler.HasCorpseItemInside(out _) || handler.TryGetInsertableOverheadCorpse(out _))
			{
				return false;
			}

			PlayerData player = MainGame.PlayerData;
			ZombieSystemData zombies = MainGame.ZombieSystemData;
			if (player == null || zombies == null)
			{
				return false;
			}

			if (!player.TryGetOverheadItem(item => item.Definition.itemGroupIds.Contains(ZombieGroupId) && !item.HasItemsByItemType(ItemType.Demon), out zombieItem))
			{
				return false;
			}

			zombie = zombies.GetZombie(zombieItem.UniqueId);
			return zombie != null;
		}

		internal static void Cremate(CrematoriumInteractionHandler handler, Item zombieItem, ZombieWgoData zombie)
		{
			PlayerData player = MainGame.PlayerData;
			WgoData crematorium = handler.assignedWgo.Data;

			// Split the zombie's body inventory into what goes into the fire (body parts, pocket
			// items - everything a corpse would have), its equipment, which goes back to the
			// player, and - with DropOrgans on - the body's contents, which get dropped. Anything
			// that won't fit in the new corpse item is given back too, rather than silently lost.
			bool dropOrgans = Plugin.DropOrgans.Value;
			Item corpse = new Item(BodyCorpseItemId, 1);
			List<Item> giveBack = new List<Item>();
			List<Item> drop = new List<Item>();
			foreach (Item item in new List<Item>(zombieItem.Inventory))
			{
				if (IsEquipment(zombie, item))
				{
					giveBack.Add(item);
				}
				else if (dropOrgans && !item.Definition.itemGroupIds.Contains(BurialRewardGroupId))
				{
					drop.Add(item);
				}
				else if (!corpse.AddItemToInventory(item, false))
				{
					giveBack.Add(item);
				}
			}

			// Same as CrematoriumInteractionHandler.Interact, minus its cur_bodies_count
			// decrement: a zombie was never counted as a body (resurrection already took it off
			// that count), so only the zombie count changes below.
			crematorium.Inventory.AddItemToInventory(corpse, null, false);
			CraftComponent craftComponent = crematorium.CraftComponent;
			CraftDefBase craftDef = craftComponent.AvailableCrafts[0];
			CraftParamsData craftParams = new CraftParamsData(craftDef.id, crematorium, CraftParamsData.CraftParamsType.Common, -1);
			craftComponent.TryStartCraft(new CraftElement(craftDef.id, 1, craftParams));

			player.RemoveOverheadItem(zombieItem);
			RemoveZombie(zombie);

			foreach (Item item in giveBack)
			{
				GiveToPlayer(player, item);
			}

			// Dropped at the player's feet like any other drop, so the pickup magnet grabs the
			// small ones right away.
			foreach (Item item in drop)
			{
				MainGame.Instance.dropSystem.DropItem(item, player.currentGameSceneId, player.position.Value, null);
			}

			RedrawZoneWidget(player);
			handler.assignedWgo.DrawWidgets();
		}

		private static bool IsEquipment(ZombieWgoData zombie, Item item)
		{
			if (item.Definition.type == ItemType.Collar)
			{
				return true;
			}

			return (!zombie.equippedHand.IsEmpty && item.UniqueId == zombie.equippedHand)
				|| (!zombie.equippedArmor.IsEmpty && item.UniqueId == zombie.equippedArmor);
		}

		// Everything about a carried zombie beyond its item lives on its ZombieWgoData (talent
		// tree, perks, tech points, name), and ZombieSystemData is the only thing that keeps it
		// in the save - so dropping it from there is what gets it out of the save file.
		private static void RemoveZombie(ZombieWgoData zombie)
		{
			// Frees its name for future zombies, as renaming one does.
			zombie.SetName(string.Empty, true);

			// Runs each perk's on-remove effects, in case any of them touch more than the
			// zombie itself.
			zombie.RemoveAllPerks();
			zombie.talentData.Clear();

			MainGame.ZombieSystemData.RemoveZombie(zombie.UniqueId);

			PlayerData player = MainGame.PlayerData;
			if (player.GetResInt(CurZombiesCountKey) > 0)
			{
				player.SubRes(CurZombiesCountKey, 1f);
			}

			// Same check as the TrySetOrRemoveResurrectionDebuff expression: with one zombie
			// fewer, the "too many zombies" debuff may no longer apply.
			WorldZoneData resurrectionZone = MainGame.WorldData?.GetWorldZoneDataById("resurrection");
			if (resurrectionZone != null && player.GetResInt(CurZombiesCountKey) <= (int)resurrectionZone.GetTotalQuality())
			{
				MainGame.Instance.GameSave.perkSystemData.RemovePerk(ExcessiveZombieDebuff, false);
			}
		}

		private static void GiveToPlayer(PlayerData player, Item item)
		{
			if (player.Inventory != null && player.Inventory.AddItemToInventory(item, null, false))
			{
				return;
			}

			MainGame.Instance.dropSystem.DropItem(item, player.currentGameSceneId, player.position.Value, null);
		}

		// Both zone counters (bodies in the morgue, zombies in the resurrection zone) are shown
		// by the world zone widget, so refresh it if the player is standing in either.
		private static void RedrawZoneWidget(PlayerData player)
		{
			string zoneId = player.CurrentWorldZoneData?.Definition?.id;
			if (zoneId == "morgue" || zoneId == "resurrection")
			{
				GUIElements.Instance.WorldZoneWidget.Draw(new WorldZoneWidgetData());
			}
		}
	}

	[HarmonyPatch(typeof(CrematoriumInteractionHandler), nameof(CrematoriumInteractionHandler.HasInteraction))]
	internal static class CrematoriumInteractionHandler_HasInteraction_Patch
	{
		private static void Postfix(CrematoriumInteractionHandler __instance, ref bool __result)
		{
			if (__result || !ZombieCremation.CanCremate(__instance, out _, out _))
			{
				return;
			}

			__instance.assignedCraftComponent = __instance.assignedWgo.Data.CraftComponent;
			__result = true;
		}
	}

	[HarmonyPatch(typeof(CrematoriumInteractionHandler), nameof(CrematoriumInteractionHandler.Interact))]
	internal static class CrematoriumInteractionHandler_Interact_Patch
	{
		private static bool Prefix(CrematoriumInteractionHandler __instance, ref bool __result)
		{
			if (!ZombieCremation.CanCremate(__instance, out Item zombieItem, out ZombieWgoData zombie))
			{
				return true;
			}

			ZombieCremation.Cremate(__instance, zombieItem, zombie);
			__result = true;
			return false;
		}
	}

	// Without this the crematorium would fall through to CraftInteractionHandler's hints while
	// a zombie is carried, instead of showing the usual "Cremate body" one.
	[HarmonyPatch(typeof(CrematoriumInteractionHandler), nameof(CrematoriumInteractionHandler.FormInteractionInfo))]
	internal static class CrematoriumInteractionHandler_FormInteractionInfo_Patch
	{
		private static bool Prefix(CrematoriumInteractionHandler __instance, ref InteractionInfos __result)
		{
			if (!ZombieCremation.CanCremate(__instance, out _, out _))
			{
				return true;
			}

			__instance.assignedCraftComponent = __instance.assignedWgo.Data.CraftComponent;
			__result = new InteractionInfos();
			__result.Add(new InteractionInfo(__instance.LocalizeHintWithActionIcon("hint_cremate_body", GameKey.Interaction)));
			return false;
		}
	}
}
