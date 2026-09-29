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
	// so this walks the data lists instead of relying on the pickup magnet reaching them, and moves
	// each one to just above the ground around the player. The game's normal pickup magnet
	// (DropSearcher/DropCollector) then collects whatever it can, with all its usual rules.
	[BepInPlugin("kupie.gk2.collectlooseitems", "Collect Loose Items", "1.1.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<KeyboardShortcut> CollectKey;
		internal static ConfigEntry<bool> RelocateBigItems;

		// Golden angle, so successive items around the player spread out evenly.
		private const float GoldenAngle = 2.39996323f;

		// Small items stay inside the magnet's reach (it gives up on anything 4+ units away), so they
		// pack in tightly; big items aren't magnet-collected, so they just need to be nearby.
		private const float SmallStartRadius = 0.3f;
		private const float SmallSpacing = 0.15f;
		private const float BigStartRadius = 2f;
		private const float BigSpacing = 0.6f;

		// Just off the ground, so drop physics settles them instead of leaving them inside the floor.
		private const float DropHeight = 0.3f;

		private void Awake()
		{
			CollectKey = Config.Bind(
				"General",
				"CollectKey",
				new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl, KeyCode.LeftShift),
				"Moves every loose item in the world (including ones that fell out of bounds) to the player, where the pickup magnet grabs it.");

			RelocateBigItems = Config.Bind(
				"General",
				"RelocateBigItems",
				false,
				"Also move big items (corpses, logs, etc.) next to the player. They can't be picked up by the magnet, so they'll just be sitting next to you. "
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

			BringEverythingToPlayer();
		}

		private void BringEverythingToPlayer()
		{
			// Resource drops (tech points, etc.) aren't physical pickups. The game already has a
			// "collect all of those across every scene" routine, so let it handle them.
			MainGame.Instance.dropSystem.CollectAllGameResDropsToPlayer(1f);

			List<GameSceneData> scenes = MainGame.WorldData.gameSceneDataList;

			PlayerController player = MainGame.PlayerController;
			GameScene playerScene = player.CurrentGameScene;
			Vector3 center = player.transform.position;

			int movedSmall = 0;
			int movedBig = 0;
			int leftBig = 0;
			int leftElsewhere = 0;

			for (int s = 0; s < scenes.Count; s++)
			{
				GameSceneData scene = scenes[s];

				// Work on a snapshot in case the game adds or removes drops while views update.
				List<DropData> drops = new List<DropData>(scene.droppedItems);
				drops.AddRange(scene.queuedDrops);

				foreach (DropData drop in drops)
				{
					if (drop == null || drop.IsRemoving || drop.Item == null || drop.Item.Definition == null || drop.IsResDrop)
					{
						continue;
					}

					// Big items and wgo-linked ones (zombie bodies) are never magnet-collected.
					bool isBig = drop.DropType == DropType.WgoData || drop.Size == ItemSize.Big;
					if (isBig && !RelocateBigItems.Value)
					{
						leftBig++;
						continue;
					}

					// The drop lives in its own scene, so a position next to the player means nothing there.
					if (playerScene == null || playerScene.Id != scene.id)
					{
						leftElsewhere++;
						continue;
					}

					DropView view = FindView(drop);
					if (view != null && (view.IsDespawning || view.IsTimedCollecting))
					{
						// Already on its way to the player (or going away).
						continue;
					}

					Vector3 spot = isBig
						? GetSpotAround(center, movedBig, BigStartRadius, BigSpacing)
						: GetSpotAround(center, movedSmall, SmallStartRadius, SmallSpacing);
					RelocateDrop(drop, view, spot);

					if (isBig)
					{
						movedBig++;
					}
					else
					{
						movedSmall++;
					}
				}
			}

			Logger.LogInfo($"Moved {movedSmall} loose item(s) to the player."
				+ (movedBig > 0 ? $" Moved {movedBig} big item(s) next to the player." : string.Empty)
				+ (leftBig > 0 ? $" {leftBig} big item(s) left where they are (RelocateBigItems is off)." : string.Empty)
				+ (leftElsewhere > 0 ? $" {leftElsewhere} item(s) left in other scenes." : string.Empty));
		}

		private static Vector3 GetSpotAround(Vector3 center, int index, float startRadius, float spacing)
		{
			float radius = startRadius + spacing * Mathf.Sqrt(index);
			float angle = index * GoldenAngle;
			return center + new Vector3(Mathf.Cos(angle) * radius, DropHeight, Mathf.Sin(angle) * radius);
		}

		private static void RelocateDrop(DropData drop, DropView view, Vector3 position)
		{
			drop.Position = position;

			// If its view is loaded, move that too - the view is what actually falls/settles, and it
			// writes its own position back into the data.
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
	}
}
