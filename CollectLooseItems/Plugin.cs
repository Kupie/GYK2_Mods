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
		internal static ConfigEntry<bool> RelocateBigItems;

		// Golden angle, so successive items around the player spread out evenly.
		private const float GoldenAngle = 2.39996323f;
		private const float BigItemSpacing = 0.6f;
		private const float BigItemStartRadius = 1.5f;
		private const float BigItemDropHeight = 1.5f;

		private void Awake()
		{
			CollectKey = Config.Bind(
				"General",
				"CollectKey",
				new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl, KeyCode.LeftShift),
				"Picks up every loose item in the world (including ones that fell out of bounds) into your inventory.");

			RelocateBigItems = Config.Bind(
				"General",
				"RelocateBigItems",
				true,
				"Big items (corpses, logs, etc.) can't go in the inventory, so move them to just around the player instead. "
				+ "Only affects big items in the scene the player is currently in.");
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
			int leftNoRoom = 0;
			int relocated = 0;
			int leftElsewhere = 0;
			int leftBig = 0;
			bool inventoryFull = false;

			PlayerController player = MainGame.PlayerController;
			GameScene playerScene = player.CurrentGameScene;
			Vector3 center = player.transform.position;

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

					// Same exclusions as the pickup magnet (DropCollector.CanCollectDrop): Big items
					// (corpses, logs, etc.) and wgo-linked items (zombie bodies) never go into the
					// inventory. Those get moved next to the player instead.
					if (drop.DropType == DropType.WgoData || drop.Size == ItemSize.Big)
					{
						if (!RelocateBigItems.Value)
						{
							leftBig++;
						}
						else if (playerScene == null || playerScene.Id != scene.id)
						{
							// The drop lives in its own scene, so a position next to the player means nothing there.
							leftElsewhere++;
						}
						else
						{
							RelocateDrop(drop, GetSpotAround(center, relocated));
							relocated++;
						}
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
				+ (relocated > 0 ? $" Moved {relocated} big item(s) next to the player." : string.Empty)
				+ (leftBig > 0 ? $" {leftBig} big item(s) left where they are (RelocateBigItems is off)." : string.Empty)
				+ (leftElsewhere > 0 ? $" {leftElsewhere} big item(s) left in other scenes." : string.Empty));

			if (inventoryFull)
			{
				LazySingleton<UINotificator>.Instance.HandleInventoryFull();
			}
		}

		// Spread positions in a spiral around the player, dropped from a little above so the drop's
		// physics settles them onto the ground; anything overlapping gets pushed apart by the game.
		private static Vector3 GetSpotAround(Vector3 center, int index)
		{
			float radius = BigItemStartRadius + BigItemSpacing * Mathf.Sqrt(index);
			float angle = index * GoldenAngle;
			return center + new Vector3(Mathf.Cos(angle) * radius, BigItemDropHeight, Mathf.Sin(angle) * radius);
		}

		private static void RelocateDrop(DropData drop, Vector3 position)
		{
			drop.Position = position;

			// If its view is loaded (it always is for the current scene) move that too - the view is
			// what actually falls/settles, and it writes its own position back into the data.
			DropView view = FindView(drop);
			if (view != null)
			{
				view.ApplySyncedWorldPosition(position);
				view.WakeUp();
			}
		}

		private static DropView FindView(DropData drop)
		{
			GameSceneManager sceneManager = LazySingleton<GameSceneManager>.Instance;
			if (sceneManager == null)
			{
				return null;
			}

			List<GameScene> loadedScenes = sceneManager.LoadedGameScenes;
			for (int i = 0; i < loadedScenes.Count; i++)
			{
				DropView view;
				if (loadedScenes[i] != null && loadedScenes[i].TryGetDropView(drop.Item, out view))
				{
					return view;
				}
			}
			return null;
		}

		private static bool IsTimedCollecting(DropData drop)
		{
			DropView view = FindView(drop);
			return view != null && view.IsTimedCollecting;
		}
	}
}
