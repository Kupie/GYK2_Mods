using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZombieTechTransfer
{
	// Adds a column of "-50 / +50" button pairs, one per tech point color (red, green, blue),
	// to the zombie window's Character tab, over the zombie silhouette just left of the
	// equipment slots. "+" moves points from the player to the zombie, "-" moves them from the
	// zombie back to the player.
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

		internal static BepInEx.Logging.ManualLogSource Log;

		private Harmony harmony;

		private void Awake()
		{
			Log = Logger;

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
				"Moves the button column left (negative) or right (positive) from its default spot, just left of the equipment slots on the Character tab. In UI units. Takes effect immediately.");

			ButtonOffsetY = Config.Bind(
				"Layout",
				"Button Offset Y",
				0f,
				"Moves the button column down (negative) or up (positive) from its default spot, centered on the equipment slots. In UI units. Takes effect immediately.");

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

	// Lives on the zombie window's GameObject and owns the button column.
	internal class TechTransferPanel : MonoBehaviour
	{
		private static readonly Color FallbackButtonColor = new Color(0.16f, 0.12f, 0.1f, 0.9f);

		private UIZombieWorkerWindow window;
		private readonly TechTransferRow[] rows = new TechTransferRow[3];

		internal static void Attach(UIZombieWorkerWindow window)
		{
			if (window.GetComponent<TechTransferPanel>() != null)
			{
				return;
			}

			ZombieEquipmentInventoryWidget equipment = window.zombieEquipmentInventoryWidget;
			if (window.mainTabWidget == null || equipment == null || equipment.collarCell == null || equipment.handCell == null)
			{
				Plugin.Log.LogWarning("Zombie window layout isn't what was expected, so no tech point buttons were added.");
				return;
			}

			TechTransferPanel panel = window.gameObject.AddComponent<TechTransferPanel>();
			panel.window = window;
			panel.Build(equipment);
			Plugin.Log.LogInfo("Added tech point buttons to the zombie window.");
		}

		// Greys out a button when the side it takes from has nothing to give.
		internal void Refresh()
		{
			ZombieWgoData zombie = window.data?.ZombieWgoData;
			string amount = Plugin.TransferAmount.Value.ToString();
			foreach (TechTransferRow row in rows)
			{
				row.takeLabel.text = "-" + amount;
				row.giveLabel.text = "+" + amount;
				row.takeButton.interactable = zombie != null && TechTransfer.GetZombiePoints(zombie, row.color) > 0;
				row.giveButton.interactable = zombie != null && TechTransfer.GetPlayerPoints(row.color) > 0;
			}
		}

		// The column is a child of the Character tab's root (mainTabWidget), so it shows and
		// hides with that tab and is drawn on top of the silhouette behind the equipment slots.
		// It opts out of any layout there; TechTransferColumn positions and sizes it every frame
		// from the equipment slots instead.
		private void Build(ZombieEquipmentInventoryWidget equipment)
		{
			GameObject columnObject = new GameObject("ZombieTechTransfer", typeof(RectTransform));
			RectTransform columnTransform = (RectTransform)columnObject.transform;
			columnTransform.SetParent(window.mainTabWidget.transform, false);
			columnTransform.SetAsLastSibling();
			columnTransform.anchorMin = new Vector2(0.5f, 0.5f);
			columnTransform.anchorMax = new Vector2(0.5f, 0.5f);
			columnTransform.pivot = new Vector2(1f, 0.5f);

			// Its own canvas, sorted just above whatever canvas draws the tab (see
			// TechTransferColumn), so nothing else in the window can end up drawn over the
			// buttons. That also means it needs its own raycaster to get clicks.
			Canvas canvas = columnObject.AddComponent<Canvas>();
			canvas.overrideSorting = true;
			columnObject.AddComponent<GraphicRaycaster>();

			columnObject.AddComponent<LayoutElement>().ignoreLayout = true;
			VerticalLayoutGroup layout = columnObject.AddComponent<VerticalLayoutGroup>();
			layout.childAlignment = TextAnchor.MiddleCenter;
			layout.childControlWidth = true;
			layout.childControlHeight = true;
			layout.childForceExpandWidth = false;
			layout.childForceExpandHeight = false;
			ContentSizeFitter fitter = columnObject.AddComponent<ContentSizeFitter>();
			fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
			fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

			TechTransferColumn column = columnObject.AddComponent<TechTransferColumn>();
			column.canvas = canvas;
			column.layout = layout;
			column.collarCell = (RectTransform)equipment.collarCell.transform;
			column.handCell = (RectTransform)equipment.handCell.transform;

			// Buttons are styled after the small framed boxes under the armor and hand slots:
			// same font, and that box's sprite as the background if it has one.
			TextMeshProUGUI textStyle = equipment.armorLabel != null ? equipment.armorLabel : window.redSpheresLabel;
			Image boxStyle = equipment.armorLabel != null ? equipment.armorLabel.transform.parent.GetComponent<Image>() : null;

			TechColor[] colors = { TechColor.Red, TechColor.Green, TechColor.Blue };
			for (int i = 0; i < colors.Length; i++)
			{
				TechColor color = colors[i];
				TechTransferRow row = new TechTransferRow { color = color };

				GameObject group = CreateLayoutObject("Group_" + color, columnTransform, vertical: true, spacing: 1f);

				// The sphere icon, drawn with the header counters' font so its sprite resolves.
				row.icon = CreateText("Icon", group.transform, window.redSpheresLabel);
				row.icon.text = TechTransfer.PlayerResId(color).FontIcon();

				GameObject buttons = CreateLayoutObject("Buttons", group.transform, vertical: false, spacing: 2f);
				row.takeButton = CreateButton(buttons.transform, textStyle, boxStyle, out row.takeLabel, out row.takeSize);
				row.takeButton.onClick.AddListener(() => TechTransfer.TakeFromZombie(window, color));
				row.giveButton = CreateButton(buttons.transform, textStyle, boxStyle, out row.giveLabel, out row.giveSize);
				row.giveButton.onClick.AddListener(() => TechTransfer.GiveToZombie(window, color));

				rows[i] = row;
			}

			column.rows = rows;
		}

		private static GameObject CreateLayoutObject(string name, Transform parent, bool vertical, float spacing)
		{
			GameObject layoutObject = new GameObject(name, typeof(RectTransform));
			layoutObject.transform.SetParent(parent, false);

			HorizontalOrVerticalLayoutGroup layout = vertical
				? (HorizontalOrVerticalLayoutGroup)layoutObject.AddComponent<VerticalLayoutGroup>()
				: layoutObject.AddComponent<HorizontalLayoutGroup>();
			layout.spacing = spacing;
			layout.childAlignment = TextAnchor.MiddleCenter;
			layout.childControlWidth = true;
			layout.childControlHeight = true;
			layout.childForceExpandWidth = false;
			layout.childForceExpandHeight = false;
			return layoutObject;
		}

		private static TextMeshProUGUI CreateText(string name, Transform parent, TextMeshProUGUI styleSource)
		{
			GameObject textObject = new GameObject(name, typeof(RectTransform));
			textObject.transform.SetParent(parent, false);

			TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
			if (styleSource != null)
			{
				if (styleSource.font != null)
				{
					text.font = styleSource.font;
					text.fontSharedMaterial = styleSource.fontSharedMaterial;
					text.spriteAsset = styleSource.spriteAsset;
				}
				text.fontSize = styleSource.fontSize;
			}
			text.color = Color.white;
			text.alignment = TextAlignmentOptions.Center;
			text.overflowMode = TextOverflowModes.Overflow;
			text.raycastTarget = false;
			return text;
		}

		private static Button CreateButton(Transform parent, TextMeshProUGUI textStyle, Image boxStyle, out TextMeshProUGUI label, out LayoutElement size)
		{
			GameObject buttonObject = new GameObject("Button", typeof(RectTransform));
			buttonObject.transform.SetParent(parent, false);

			Image background = buttonObject.AddComponent<Image>();
			if (boxStyle != null && boxStyle.sprite != null)
			{
				background.sprite = boxStyle.sprite;
				background.type = boxStyle.type;
				background.pixelsPerUnitMultiplier = boxStyle.pixelsPerUnitMultiplier;
				background.color = boxStyle.color;
			}
			else
			{
				background.color = FallbackButtonColor;
			}

			Button button = buttonObject.AddComponent<Button>();
			button.targetGraphic = background;
			// Mouse only: the window's gamepad navigation is its own system, and Unity's
			// built-in navigation would only fight it.
			button.navigation = new Navigation { mode = Navigation.Mode.None };

			// Filled in by TechTransferColumn once the equipment slots have a size.
			size = buttonObject.AddComponent<LayoutElement>();

			label = CreateText("Label", buttonObject.transform, textStyle);
			RectTransform labelTransform = label.rectTransform;
			labelTransform.anchorMin = Vector2.zero;
			labelTransform.anchorMax = Vector2.one;
			labelTransform.offsetMin = Vector2.zero;
			labelTransform.offsetMax = Vector2.zero;
			return button;
		}
	}

	internal class TechTransferRow
	{
		internal TechColor color;
		internal TextMeshProUGUI icon;
		internal Button takeButton;
		internal Button giveButton;
		internal TextMeshProUGUI takeLabel;
		internal TextMeshProUGUI giveLabel;
		internal LayoutElement takeSize;
		internal LayoutElement giveSize;
	}

	// Keeps the column just left of the equipment slots, vertically centered on them, and sizes
	// the buttons from the slot size. Done every frame because the window's layout isn't final
	// at Init.
	internal class TechTransferColumn : MonoBehaviour
	{
		internal RectTransform collarCell;
		internal RectTransform handCell;
		internal Canvas canvas;
		internal VerticalLayoutGroup layout;
		internal TechTransferRow[] rows;

		private readonly Vector3[] corners = new Vector3[4];

		private void LateUpdate()
		{
			RectTransform parent = transform.parent as RectTransform;
			if (collarCell == null || handCell == null || parent == null)
			{
				return;
			}

			// The window re-sorts its canvases when it redraws or another window opens on top,
			// so follow the canvas the tab is actually drawn with.
			Canvas tabCanvas = GetSortingCanvas(parent);
			if (tabCanvas != null)
			{
				if (canvas.sortingLayerID != tabCanvas.sortingLayerID)
				{
					canvas.sortingLayerID = tabCanvas.sortingLayerID;
				}

				if (canvas.sortingOrder != tabCanvas.sortingOrder + 1)
				{
					canvas.sortingOrder = tabCanvas.sortingOrder + 1;
				}
			}

			// Corners come back as bottom-left, top-left, top-right, bottom-right.
			collarCell.GetWorldCorners(corners);
			Vector3 collarBottomLeft = parent.InverseTransformPoint(corners[0]);
			Vector3 collarTopRight = parent.InverseTransformPoint(corners[2]);
			handCell.GetWorldCorners(corners);
			Vector3 handBottomLeft = parent.InverseTransformPoint(corners[0]);

			float cellSize = collarTopRight.x - collarBottomLeft.x;
			if (cellSize <= 0f)
			{
				return;
			}

			float x = collarBottomLeft.x - cellSize * 0.15f + Plugin.ButtonOffsetX.Value;
			float y = (collarTopRight.y + handBottomLeft.y) * 0.5f + Plugin.ButtonOffsetY.Value;
			transform.localPosition = new Vector3(x, y, 0f);

			float buttonWidth = cellSize * 0.8f;
			float buttonHeight = Mathf.Max(cellSize * 0.4f, rows[0].takeLabel.fontSize * 1.3f);
			layout.spacing = cellSize * 0.25f;
			foreach (TechTransferRow row in rows)
			{
				SetSize(row.takeSize, buttonWidth, buttonHeight);
				SetSize(row.giveSize, buttonWidth, buttonHeight);
			}
		}

		// A nested canvas without overrideSorting draws with its parent's order, so walk up to
		// the first canvas that sets its own.
		private static Canvas GetSortingCanvas(Transform from)
		{
			Canvas canvas = from.GetComponentInParent<Canvas>();
			while (canvas != null && !canvas.isRootCanvas && !canvas.overrideSorting)
			{
				Transform above = canvas.transform.parent;
				canvas = above != null ? above.GetComponentInParent<Canvas>() : null;
			}

			return canvas;
		}

		private static void SetSize(LayoutElement element, float width, float height)
		{
			if (!Mathf.Approximately(element.preferredWidth, width))
			{
				element.preferredWidth = width;
			}

			if (!Mathf.Approximately(element.preferredHeight, height))
			{
				element.preferredHeight = height;
			}
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
