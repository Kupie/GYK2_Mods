using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using LazyBearTechnology;
using UnityEngine;

namespace CollectLooseItems
{
	// Every loose item in the game is a DropData in its scene's GameSceneData.droppedItems (or in
	// queuedDrops for a scene that isn't loaded); a DropView is only the visual for one while its
	// chunk is loaded. Items that fell through the floor or out of bounds still have their DropData,
	// so this walks the data lists instead of relying on the pickup magnet's trigger colliders and
	// puts each drop straight into the player's inventory, the way PlayerData.CollectDrop does.
	[BepInPlugin("kupie.gk2.collectlooseitems", "Collect Loose Items", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<KeyboardShortcut> CollectKey;

		private void Awake()
		{
			CollectKey = Config.Bind(
				"General",
				"CollectKey",
				new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl, KeyCode.LeftShift),
				"Picks up every loose item in the world (including ones that fell out of bounds) into your inventory.");
		}

		private void Update()
		{
			if (!CollectKey.Value.IsDown())
			{
				return;
			}

			if (MainGame.Instance == null || MainGame.IsGamePaused || MainGame.PlayerData == null || MainGame.PlayerController == null)
			{
				return;
			}

			CollectEverything();
		}

		private void CollectEverything()
		{
			// Resource drops (tech points, energy, etc.) aren't inventory items. The game already has
			// a "collect all of those across every scene" routine, so let it handle them.
			MainGame.Instance.dropSystem.CollectAllGameResDropsToPlayer(1f);

			PlayerData playerData = MainGame.PlayerData;
			List<GameSceneData> scenes = MainGame.WorldData.gameSceneDataList;

			int stacksCollected = 0;
			int itemsCollected = 0;
			int leftBig = 0;
			int leftLinked = 0;
			int leftNoRoom = 0;
			bool inventoryFull = false;

			for (int s = 0; s < scenes.Count; s++)
			{
				GameSceneData scene = scenes[s];

				// Collecting removes from these lists, so work on a snapshot.
				List<DropData> drops = new List<DropData>(scene.droppedItems);
				drops.AddRange(scene.queuedDrops);

				foreach (DropData drop in drops)
				{
					if (drop == null || drop.IsRemoving || drop.Item == null || drop.Item.Definition == null || drop.IsResDrop)
					{
						continue;
					}

					// Same exclusions as the pickup magnet (DropCollector.CanCollectDrop): items tied
					// to a wgo and Big items (carried, corpses, etc.) never go into the inventory.
					if (drop.DropType == DropType.WgoData)
					{
						leftLinked++;
						continue;
					}
					if (drop.Size == ItemSize.Big)
					{
						leftBig++;
						continue;
					}

					// A view that's already flying to the player will collect itself.
					if (IsTimedCollecting(drop))
					{
						continue;
					}

					if (inventoryFull || playerData.inventory.Data.CanAddItemCountToInventory(drop.Item, true, null, false) <= 0)
					{
						inventoryFull = true;
						leftNoRoom++;
						continue;
					}

					int countBefore = drop.Count;
					List<Item> added;
					playerData.inventory.AddItemToInventory(drop.Item, out added, null, false);
					itemsCollected += countBefore - drop.Count;
					stacksCollected++;

					if (added != null && added.Count > 0)
					{
						foreach (LazyExpression expression in drop.Item.Definition.onDropCollected)
						{
							expression.Evaluate(added[0]);
						}
					}

					if (drop.Count == 0)
					{
						scene.RemoveDrop(drop);
					}
					else
					{
						// Only part of the stack fit.
						drop.NotifyCountChanged();
						inventoryFull = true;
						leftNoRoom++;
					}
				}
			}

			Logger.LogInfo($"Collected {itemsCollected} item(s) from {stacksCollected} loose stack(s)."
				+ (leftNoRoom > 0 ? $" {leftNoRoom} stack(s) left: inventory full." : string.Empty)
				+ (leftBig > 0 ? $" {leftBig} big item(s) left (can't be put in the inventory)." : string.Empty)
				+ (leftLinked > 0 ? $" {leftLinked} wgo-linked item(s) left." : string.Empty));

			if (inventoryFull)
			{
				LazySingleton<UINotificator>.Instance.HandleInventoryFull();
			}
		}

		private static bool IsTimedCollecting(DropData drop)
		{
			GameSceneManager sceneManager = LazySingleton<GameSceneManager>.Instance;
			if (sceneManager == null)
			{
				return false;
			}

			List<GameScene> loadedScenes = sceneManager.LoadedGameScenes;
			for (int i = 0; i < loadedScenes.Count; i++)
			{
				DropView view;
				if (loadedScenes[i] != null && loadedScenes[i].TryGetDropView(drop.Item, out view))
				{
					return view.IsTimedCollecting;
				}
			}
			return false;
		}
	}
}
