using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZombieTechTransfer
{
	// Adds a "-50 / +50" button pair under each of the zombie window's three tech point
	// counters (red, green, blue), shown only on the Perks tab. "+" moves points from the
	// player to the zombie, "-" moves them from the zombie back to the player.
	//
	// The two sides are stored differently: the player's points are the "tech_red",
	// "tech_green" and "tech_blue" game resources on PlayerData (what the tech tree spends),
	// and a zombie's are the plain techRed/techGreen/techBlue ints on its ZombieWgoData (what
	// its talent level-ups spend). A transfer is a subtract on one side and an add on the other,
	// capped by what the giving side has, so nothing is created or lost.
	[BepInPlugin("kupie.gk2.zombietechtransfer", "Zombie Tech Transfer", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ConfigEntry<int> TransferAmount;
		internal static ConfigEntry<float> ButtonOffsetX;
		internal static ConfigEntry<float> ButtonOffsetY;

		private Harmony harmony;

		private void Awake()
		{
			TransferAmount = Config.Bind(
				"General",
				"Transfer Amount",
				50,
				new ConfigDescription(
					"How many tech points each button moves. If the giving side has fewer than this, whatever it has is moved. Takes effect immediately, no restart needed.",
					new AcceptableValueRange<int>(1, 100000)));

			ButtonOffsetX = Config.Bind(
				"Layout",
				"Button Offset X",
				0f,
				"Moves each button pair left (negative) or right (positive) from its default spot, centered under its tech point counter. In UI units. Takes effect immediately.");

			ButtonOffsetY = Config.Bind(
				"Layout",
				"Button Offset Y",
				-2f,
				"Moves each button pair down (negative) or up (positive) from its default spot, just under its tech point counter. In UI units. Takes effect immediately.");

			harmony = new Harmony("kupie.gk2.zombietechtransfer");
			harmony.PatchAll();
		}

		private void OnDestroy()
		{
			harmony?.UnpatchSelf();
		}
	}

	internal enum TechColor
	{
		Red,
		Green,
		Blue
	}

	internal static class TechTransfer
	{
		internal static string PlayerResId(TechColor color)
		{
			switch (color)
			{
				case TechColor.Red:
					return "tech_red";
				case TechColor.Green:
					return "tech_green";
				default:
					return "tech_blue";
			}
		}

		internal static int GetZombiePoints(ZombieWgoData zombie, TechColor color)
		{
			switch (color)
			{
				case TechColor.Red:
					return zombie.techRed;
				case TechColor.Green:
					return zombie.techGreen;
				default:
					return zombie.techBlue;
			}
		}

		private static void AddZombiePoints(ZombieWgoData zombie, TechColor color, int value)
		{
			switch (color)
			{
				case TechColor.Red:
					zombie.techRed += value;
					break;
				case TechColor.Green:
					zombie.techGreen += value;
					break;
				default:
					zombie.techBlue += value;
					break;
			}
		}

		internal static int GetPlayerPoints(TechColor color)
		{
			return MainGame.PlayerData?.GetResInt(PlayerResId(color)) ?? 0;
		}

		internal static void GiveToZombie(UIZombieWorkerWindow window, TechColor color)
		{
			ZombieWgoData zombie = window.data?.ZombieWgoData;
			PlayerData player = MainGame.PlayerData;
			if (zombie == null || player == null)
			{
				return;
			}

			int amount = Mathf.Min(Plugin.TransferAmount.Value, GetPlayerPoints(color));
			if (amount <= 0)
			{
				return;
			}

			player.SubRes(PlayerResId(color), amount);
			AddZombiePoints(zombie, color, amount);
			OnTransferred(window);
		}

		internal static void TakeFromZombie(UIZombieWorkerWindow window, TechColor color)
		{
			ZombieWgoData zombie = window.data?.ZombieWgoData;
			PlayerData player = MainGame.PlayerData;
			if (zombie == null || player == null)
			{
				return;
			}

			int amount = Mathf.Min(Plugin.TransferAmount.Value, GetZombiePoints(zombie, color));
			if (amount <= 0)
			{
				return;
			}

			// The player's tech resources are clamped to a max by GK2GameResSystem, so only take
			// from the zombie what actually landed on the player.
			string resId = PlayerResId(color);
			float before = player.GetRes(resId);
			player.AddRes(resId, amount);
			int moved = Mathf.RoundToInt(player.GetRes(resId) - before);
			if (moved <= 0)
			{
				return;
			}

			AddZombiePoints(zombie, color, -moved);
			OnTransferred(window);
		}

		// Not raised through ZombieWgoData.OnTechPointsAddedToZombie: that event's other
		// listener (UIGameResNotificatorData) expects the workstation the points came from and
		// would pop a "+N" bubble over it. The window's own handler for that event only does
		// the two redraws below, so they're done directly.
		private static void OnTransferred(UIZombieWorkerWindow window)
		{
			window.RedrawSpheres();
			if (window.zombieProgressionWidget.gameObject.activeSelf)
			{
				// Re-evaluates which talent level-ups the zombie can now afford.
				window.zombieProgressionWidget.Redraw();
			}

			// Same refresh FlyingTechPoint does after adding a collected point.
			HUD hud = LazyUI.Get<HUD>();
			if (hud != null)
			{
				hud.UpdateTechPointsInstant();
			}
			LazyAudio.PlayAndForget("tech_point_collect");
		}
	}

	// Lives on the zombie window's GameObject and owns the three button rows.
	internal class TechTransferPanel : MonoBehaviour
	{
		private static readonly Color ButtonColor = new Color(0.16f, 0.12f, 0.1f, 0.9f);

		private UIZombieWorkerWindow window;
		private readonly TechTransferRow[] rows = new TechTransferRow[3];

		internal static void Attach(UIZombieWorkerWindow window)
		{
			if (window.GetComponent<TechTransferPanel>() != null)
			{
				return;
			}

			TechTransferPanel panel = window.gameObject.AddComponent<TechTransferPanel>();
			panel.window = window;
			panel.rows[0] = panel.CreateRow(TechColor.Red, window.redSpheresLabel);
			panel.rows[1] = panel.CreateRow(TechColor.Green, window.greenSpheresLabel);
			panel.rows[2] = panel.CreateRow(TechColor.Blue, window.blueSpheresLabel);
			panel.SetVisible(false);
		}

		internal void SetVisible(bool visible)
		{
			foreach (TechTransferRow row in rows)
			{
				if (row != null)
				{
					row.gameObject.SetActive(visible);
				}
			}

			if (visible)
			{
				Refresh();
			}
		}

		// Greys out a button when the side it takes from has nothing to give.
		internal void Refresh()
		{
			ZombieWgoData zombie = window.data?.ZombieWgoData;
			string amount = Plugin.TransferAmount.Value.ToString();
			foreach (TechTransferRow row in rows)
			{
				if (row == null)
				{
					continue;
				}

				row.takeLabel.text = "-" + amount;
				row.giveLabel.text = "+" + amount;
				row.takeButton.interactable = zombie != null && TechTransfer.GetZombiePoints(zombie, row.color) > 0;
				row.giveButton.interactable = zombie != null && TechTransfer.GetPlayerPoints(row.color) > 0;
			}
		}

		private TechTransferRow CreateRow(TechColor color, TextMeshProUGUI counter)
		{
			if (counter == null)
			{
				return null;
			}

			// Parented to the counter's canvas rather than next to the counter itself, so the
			// counter's layout isn't disturbed and hovering the buttons doesn't also trigger the
			// tooltip on the counter's parent. TechTransferRow keeps it positioned under the
			// counter.
			Canvas counterCanvas = counter.GetComponentInParent<Canvas>(true);
			Transform parent = counterCanvas != null ? counterCanvas.transform : counter.transform.parent;

			GameObject rowObject = new GameObject("ZombieTechTransfer_" + color, typeof(RectTransform));
			RectTransform rowTransform = (RectTransform)rowObject.transform;
			rowTransform.SetParent(parent, false);
			rowTransform.SetAsLastSibling();
			rowTransform.anchorMin = new Vector2(0.5f, 0.5f);
			rowTransform.anchorMax = new Vector2(0.5f, 0.5f);
			rowTransform.pivot = new Vector2(0.5f, 1f);

			// A nested canvas that keeps its parent's sorting, with its own raycaster so the
			// buttons get clicks even if the counter's canvas has none.
			Canvas rowCanvas = rowObject.AddComponent<Canvas>();
			rowCanvas.overrideSorting = false;
			rowObject.AddComponent<GraphicRaycaster>();

			rowObject.AddComponent<LayoutElement>().ignoreLayout = true;
			HorizontalLayoutGroup layout = rowObject.AddComponent<HorizontalLayoutGroup>();
			layout.spacing = 4f;
			layout.childAlignment = TextAnchor.MiddleCenter;
			layout.childControlWidth = true;
			layout.childControlHeight = true;
			layout.childForceExpandWidth = false;
			layout.childForceExpandHeight = false;
			ContentSizeFitter fitter = rowObject.AddComponent<ContentSizeFitter>();
			fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
			fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

			TechTransferRow row = rowObject.AddComponent<TechTransferRow>();
			row.color = color;
			row.anchor = counter.rectTransform;

			float fontSize = Mathf.Max(10f, counter.fontSize * 0.75f);
			row.takeButton = CreateButton(rowTransform, counter, fontSize, out row.takeLabel);
			row.takeButton.onClick.AddListener(() => TechTransfer.TakeFromZombie(window, color));
			row.giveButton = CreateButton(rowTransform, counter, fontSize, out row.giveLabel);
			row.giveButton.onClick.AddListener(() => TechTransfer.GiveToZombie(window, color));
			return row;
		}

		private static Button CreateButton(Transform parent, TextMeshProUGUI styleSource, float fontSize, out TextMeshProUGUI label)
		{
			GameObject buttonObject = new GameObject("Button", typeof(RectTransform));
			buttonObject.transform.SetParent(parent, false);

			Image background = buttonObject.AddComponent<Image>();
			background.color = ButtonColor;

			Button button = buttonObject.AddComponent<Button>();
			button.targetGraphic = background;
			// Mouse only: the window's gamepad navigation is its own system, and Unity's
			// built-in navigation would only fight it.
			button.navigation = new Navigation { mode = Navigation.Mode.None };

			LayoutElement size = buttonObject.AddComponent<LayoutElement>();
			size.preferredWidth = fontSize * 2.8f;
			size.preferredHeight = fontSize * 1.4f;

			GameObject labelObject = new GameObject("Label", typeof(RectTransform));
			RectTransform labelTransform = (RectTransform)labelObject.transform;
			labelTransform.SetParent(buttonObject.transform, false);
			labelTransform.anchorMin = Vector2.zero;
			labelTransform.anchorMax = Vector2.one;
			labelTransform.offsetMin = Vector2.zero;
			labelTransform.offsetMax = Vector2.zero;

			label = labelObject.AddComponent<TextMeshProUGUI>();
			if (styleSource.font != null)
			{
				label.font = styleSource.font;
				label.fontSharedMaterial = styleSource.fontSharedMaterial;
			}
			label.fontSize = fontSize;
			label.color = Color.white;
			label.alignment = TextAlignmentOptions.Center;
			label.overflowMode = TextOverflowModes.Overflow;
			label.raycastTarget = false;
			return button;
		}
	}

	// One counter's "-N +N" pair. Positioned every frame from the counter's current rect, since
	// the window's layout isn't final at Init and the counters can move with it.
	internal class TechTransferRow : MonoBehaviour
	{
		internal TechColor color;
		internal RectTransform anchor;
		internal Button takeButton;
		internal Button giveButton;
		internal TextMeshProUGUI takeLabel;
		internal TextMeshProUGUI giveLabel;

		private void LateUpdate()
		{
			RectTransform parent = transform.parent as RectTransform;
			if (anchor == null || parent == null)
			{
				return;
			}

			Rect rect = anchor.rect;
			Vector3 bottomCenter = anchor.TransformPoint(new Vector3(rect.center.x, rect.yMin, 0f));
			Vector3 local = parent.InverseTransformPoint(bottomCenter);
			transform.localPosition = new Vector3(local.x + Plugin.ButtonOffsetX.Value, local.y + Plugin.ButtonOffsetY.Value, 0f);
		}
	}

	[HarmonyPatch(typeof(UIZombieWorkerWindow), nameof(UIZombieWorkerWindow.Init))]
	internal static class UIZombieWorkerWindow_Init_Patch
	{
		private static void Postfix(UIZombieWorkerWindow __instance)
		{
			TechTransferPanel.Attach(__instance);
		}
	}

	[HarmonyPatch(typeof(UIZombieWorkerWindow), nameof(UIZombieWorkerWindow.RedrawPerksTab))]
	internal static class UIZombieWorkerWindow_RedrawPerksTab_Patch
	{
		private static void Postfix(UIZombieWorkerWindow __instance)
		{
			if (__instance.TryGetComponent(out TechTransferPanel panel))
			{
				panel.SetVisible(true);
			}
		}
	}

	[HarmonyPatch(typeof(UIZombieWorkerWindow), nameof(UIZombieWorkerWindow.RedrawCharacterTab))]
	internal static class UIZombieWorkerWindow_RedrawCharacterTab_Patch
	{
		private static void Postfix(UIZombieWorkerWindow __instance)
		{
			if (__instance.TryGetComponent(out TechTransferPanel panel))
			{
				panel.SetVisible(false);
			}
		}
	}

	// Called on every open and after anything changes the zombie's points, which is when the
	// buttons' enabled state can change.
	[HarmonyPatch(typeof(UIZombieWorkerWindow), nameof(UIZombieWorkerWindow.RedrawSpheres))]
	internal static class UIZombieWorkerWindow_RedrawSpheres_Patch
	{
		private static void Postfix(UIZombieWorkerWindow __instance)
		{
			if (__instance.TryGetComponent(out TechTransferPanel panel))
			{
				panel.Refresh();
			}
		}
	}
}
