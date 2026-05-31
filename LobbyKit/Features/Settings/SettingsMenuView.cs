using System;
using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace LobbyKit.Features.Settings
{
    internal static class SettingsMenuView
    {
        private static readonly Vector2 PanelSize = new Vector2(900f, 640f);
        private const float LabelWidth = 300f;
        private const float InputHeight = 36f;
        private const float SliderHeight = 30f;
        private const float ToggleHeight = 24.5f;
        private const float MessageRowHeight = 36f;
        private const float ActionRowHeight = 36f;
        private const float RowSpacing = 8f;
        private const float VerticalRowSpacing = RowSpacing + 5f;
        private const int ScrollBottomPadding = 10;
        private const float ScrollWheelSensitivity = 35f;
        private const float TitleFontSize = 24f;
        private const float LabelFontSize = 16f;
        private const float InputFontSize = 16f;
        private const float ServerNameFontSize = 16f;
        private const float ButtonFontSize = 14f;
        private const float CloseButtonFontSize = 16f;
        private const float SectionHeaderHeight = 36f;
        private const float HeaderTopInset = 2f;
        private static readonly Color NormalButtonColor = new Color(22f / 255f, 156f / 255f, 220f / 255f, 1f);
        private static readonly Color NormalButtonShadowColor = new Color(13f / 255f, 85f / 255f, 132f / 255f, 1f);
        private static readonly Color DirtyButtonColor = new Color(108f / 255f, 99f / 255f, 184f / 255f, 1f);
        private static readonly Color DirtyButtonShadowColor = new Color(65f / 255f, 60f / 255f, 112f / 255f, 1f);
        private static readonly Color CloseButtonColor = new Color(0.827f, 0.184f, 0.184f, 1f);
        private static readonly Color CloseButtonShadowColor = new Color(0.440f, 0f, 0f, 1f);

        private static GameObject _firstSelectable;

        public static bool HasRequiredTemplates(out string missing)
        {
            var templates = NativeUiTemplates.Current;
            string result = string.Empty;
            AddMissingTemplate(ref result, templates.Panel != null || templates.Background != null, "panel/background");
            AddMissingTemplate(ref result, templates.Label != null, "label");
            AddMissingTemplate(ref result, templates.Button != null, "button");
            AddMissingTemplate(ref result, templates.InputField != null, "input field");
            AddMissingTemplate(ref result, templates.Toggle != null, "toggle");
            AddMissingTemplate(ref result, templates.Slider != null, "slider");
            AddMissingTemplate(ref result, templates.Scrollbar != null, "scrollbar");
            missing = result;
            return string.IsNullOrEmpty(result);
        }

        public static SettingsMenuHandle Build(Transform parent, SettingsDraft draft)
        {
            GameObject root = NativeUiFactory.Object("LobbyKit Settings", parent).GameObject;
            UiLayout.Stretch(root);
            _firstSelectable = null;
            CanvasGroup canvasGroup = root.GetComponent<CanvasGroup>() ?? root.AddComponent<CanvasGroup>();
            canvasGroup.alpha = 1f;
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;

            Image fadeTemplate = SleddingUiAdapter.Find(SleddingUiPaths.BackgroundFade).Get<Image>();
            Image backdrop = NativeUiFactory.Backdrop(root.transform, fadeTemplate);

            UiElement panel = NativeUiFactory.Panel(backdrop != null ? backdrop.transform : root.transform, "Panel");
            panel.CenterAnchors().Resize(PanelSize).Move(Vector2.zero);
            UiLayout.SetVertical(
                panel.GameObject,
                new RectOffset(43, 43, 26, 26),
                spacing: VerticalRowSpacing);

            AddHeader(panel.Transform, "LobbyKit Settings", addCloseButton: true);
            var scroll = NativeUiFactory.ScrollView(panel.Transform, "Scroll Viewport");
            if (scroll.ScrollRect != null)
                scroll.ScrollRect.scrollSensitivity = ScrollWheelSensitivity;

            Transform content = scroll.ContentRect != null ? scroll.ContentRect.transform : panel.Transform;
            VerticalLayoutGroup contentLayout = content.GetComponent<VerticalLayoutGroup>();
            if (contentLayout != null)
            {
                contentLayout.spacing = VerticalRowSpacing;
                contentLayout.padding.bottom = ScrollBottomPadding;
            }

            // AddSchemaField(content, SettingsSchema.LobbySimpleFields[0], draft);
            // AddCapacitySlider(content, draft);
            // AddSchemaField(content, SettingsSchema.LobbySimpleFields[1], draft);

            // RectTransform passwordInputTrack = null;
            // AddGridRow(content, MessageRowHeight, 0f, new[] { 3f, 4f }, cells =>
            // {
            //     AddToggle(
            //         cells[0],
            //         "Password Protected",
            //         draft.IsPasswordProtected,
            //         value => draft.IsPasswordProtected = value,
            //         labelOffsetX: 17f,
            //         anchoredPosition: new Vector2(0f, -MessageRowHeight * 0.5f));
            //     passwordInputTrack = cells[1]?.GetComponent<RectTransform>();
            //     AddInput(cells[1], string.Empty, draft.LobbyPassword, value => draft.LobbyPassword = value ?? string.Empty, placeholderText: "enter password...");
            // });
            // if (passwordInputTrack != null)
            //     passwordInputTrack.offsetMin = new Vector2(308f, passwordInputTrack.offsetMin.y);

            // AddSchemaField(content, SettingsSchema.LobbySimpleFields[2], draft);
            // AddSchemaField(content, SettingsSchema.LobbySimpleFields[3], draft);
            // AddDivider(content);
            // AddHeader(content, "Mod Settings", textScale: 0.7f, rowHeight: SectionHeaderHeight);
            foreach (SettingsField field in SettingsSchema.ModSimpleFields)
                AddSchemaField(content, field, draft);
            AddMessageRow(
                content,
                "Join Messages",
                draft.ShowJoinMessages,
                value => draft.ShowJoinMessages = value,
                draft.JoinMessageSize,
                value => draft.JoinMessageSize = value);
            AddMessageRow(
                content,
                "Leave Messages",
                draft.ShowLeaveMessages,
                value => draft.ShowLeaveMessages = value,
                draft.LeaveMessageSize,
                value => draft.LeaveMessageSize = value);

            AddActionButtons(panel.Transform);

            return new SettingsMenuHandle(root, _firstSelectable);
        }

        private static void AddMissingTemplate(ref string missing, bool present, string label)
        {
            if (present)
                return;

            missing = string.IsNullOrEmpty(missing) ? label : missing + ", " + label;
        }

        private static void CloseButtonOnClick()
        {
            SettingsMenuController.Instance.RequestClose();
        }

        private static void AddHeader(
            Transform parent,
            string label = null,
            bool addCloseButton = false,
            float textScale = 1f,
            Vector2? anchoredPosition = null,
            float rowHeight = 50f)
        {
            GameObject row = NativeUiBuilder.HorizontalRow(parent, height: rowHeight).GameObject;
            DisableChildHeightControl(row);

            GameObject titleContainer = NativeUiFactory.Object("Header Title Container", row.transform).GameObject;
            UiLayout.SetFixedSize(titleContainer, flexibleWidth: 1f, preferredHeight: rowHeight);

            TMP_Text title = CreateHeaderLabel(titleContainer.transform, label, "Header");

            UiStyles.SetTextMetrics(title, TitleFontSize * Mathf.Max(0.01f, textScale), TextAlignmentOptions.Top);
            UiLayout.SetRect(
                title,
                anchorMin: Vector2.zero,
                anchorMax: Vector2.one,
                pivot: new Vector2(0.5f, 1f),
                anchoredPosition: Vector2.zero,
                sizeDelta: Vector2.zero);
            title.margin = Vector4.zero;
            title.margin = new Vector4(0f, HeaderTopInset, 0f, 0f);
            title.color = Color.white;
            title.fontStyle = FontStyles.Bold;
            title.ForceMeshUpdate();
            if (anchoredPosition != null)
            {
                title.rectTransform.anchoredPosition = anchoredPosition.Value;
                UiLayout.SetFixedSize(row, flexibleWidth: 1f, preferredHeight: rowHeight);
            }
            else
            {
                UiLayout.SetFixedSize(row, flexibleWidth: 1f, preferredHeight: rowHeight);
            }

            if (!addCloseButton)
                return;

            Button closeButton = CreateSettingsButton(
                row.transform,
                "X",
                (UnityAction)CloseButtonOnClick,
                "Close",
                CloseButtonFontSize,
                new Vector2(25f, 25f),
                CloseButtonColor,
                CloseButtonShadowColor);
            _firstSelectable = closeButton.gameObject;
            UiLayout.SetFixedSize(closeButton.gameObject, preferredWidth: 25f, preferredHeight: 25f);
        }

        private static TMP_Text CreateHeaderLabel(Transform parent, string label, string name)
        {
            GameObject labelObject = NativeUiFactory.Object(name, parent).GameObject;
            TMP_Text text = labelObject.AddComponent<TextMeshProUGUI>();
            UiStyles.ApplyText(text, NativeUiTemplates.Current.HeaderLabel ?? NativeUiTemplates.Current.Label);
            UiStyles.SetText(text, label);
            text.raycastTarget = false;
            text.margin = Vector4.zero;
            return text;
        }

        private static void AddActionButtons(Transform parent)
        {
            GameObject row = NativeUiBuilder.HorizontalRow(parent, "Actions", height: ActionRowHeight, spacing: RowSpacing).GameObject;
            DisableChildHeightControl(row);
            UiLayout.SetFixedSize(row, flexibleWidth: 1f, preferredHeight: ActionRowHeight);

            GameObject spacer = NativeUiFactory.Object("Actions Spacer", row.transform).GameObject;
            UiLayout.SetFixedSize(spacer, flexibleWidth: 1f, preferredHeight: ActionRowHeight);

            Button apply = CreateSettingsButton(row.transform, "Apply", (UnityAction)SettingsMenuController.Instance.Apply, "Apply", ButtonFontSize, new Vector2(150f, 36f));
            UiLayout.SetFixedSize(apply.gameObject, preferredWidth: 150f, preferredHeight: 36f);

            Button applyClose = CreateSettingsButton(row.transform, "Apply & Close", (UnityAction)SettingsMenuController.Instance.ApplyAndClose, "Apply & Close", ButtonFontSize, new Vector2(190f, 36f));
            UiLayout.SetFixedSize(applyClose.gameObject, preferredWidth: 190f, preferredHeight: 36f);

            RegisterDirtyButtonFeedback(apply, applyClose);
        }

        private static void AddCapacitySlider(Transform parent, SettingsDraft draft)
        {
            MySliderUI slider = null;
            slider = NativeUiFactory.Slider(
                parent,
                name: "Server Capacity Slider",
                minValue: 1f,
                maxValue: 64f,
                value: Mathf.Clamp(draft.ServerCapacity, 1, 64),
                onValueChanged: (UnityAction<float>)((value) =>
                {
                    draft.ServerCapacity = Mathf.RoundToInt(value);
                    slider?.UpdateSliderValueDisplay();
                    SettingsMenuController.Instance.NotifyDraftChanged();
                }));

            if (slider == null || slider.slider == null)
            {
                Debug.LogWarning("[LobbyKit] Could not create settings capacity slider from native template.");
                return;
            }

            UiLayout.StabilizeClonedControl(slider.gameObject);

            TMP_Text label = slider.transform.Find("(Text) Slider Label")?.GetComponent<TMP_Text>();
            if (label != null)
            {
                UiStyles.SetText(label, "Server Capacity");
                UiStyles.SetTextMetrics(label, LabelFontSize, TextAlignmentOptions.Left);
                label.color = Color.white;
                label.ForceMeshUpdate();
            }

            if (slider.sliderValueText != null)
            {
                UiStyles.SetTextMetrics(slider.sliderValueText, LabelFontSize, TextAlignmentOptions.Right);
                slider.sliderValueText.color = Color.white;
                slider.sliderValueText.ForceMeshUpdate();
            }

            UiLayout.SetFixedSize(slider.gameObject, flexibleWidth: 1f, preferredHeight: SliderHeight);
        }

        private static void AddSchemaField(Transform parent, SettingsField field, SettingsDraft draft)
        {
            if (field is TextSettingsField textField)
            {
                AddInput(
                    parent,
                    textField.Label,
                    textField.Get(draft),
                    value => textField.Set(draft, value),
                    textField.Placeholder);
                return;
            }

            if (field is ToggleSettingsField toggleField)
            {
                AddToggle(
                    parent,
                    toggleField.Label,
                    toggleField.Get(draft),
                    value => toggleField.Set(draft, value));
            }
        }

        private static void AddDivider(Transform parent, string label = null)
        {
            GameObject row = NativeUiBuilder.HorizontalRow(parent, "Divider", height: 2f, spacing: 0f).GameObject;
            UiLayout.SetFixedSize(row, flexibleWidth: 1f, preferredHeight: 2f);

            if (!string.IsNullOrEmpty(label))
            {
                TMP_Text text = NativeUiFactory.Label(row.transform, label, "Divider Label", NativeUiTemplates.Current.SliderLabel ?? NativeUiTemplates.Current.Label);
                UiStyles.SetTextMetrics(text, LabelFontSize, TextAlignmentOptions.Left);
                UiLayout.SetFixedSize(text.gameObject, preferredWidth: LabelWidth, preferredHeight: 24f);
            }

            Image line = NativeUiFactory.Object("Divider Line", row.transform).GameObject.AddComponent<Image>();
            line.color = new Color(1f, 1f, 1f, 0.28f);
            line.raycastTarget = false;
            UiLayout.SetLayout(line.gameObject, flexibleWidth: 1f, preferredHeight: 2f, minWidth: 0f, minHeight: 0f);
        }

        private static void AddInput(Transform parent, string label, string value, Action<string> setter, string placeholderText = "")
        {
            GameObject row = AddPreferenceRow(parent, label);
            TMP_InputField input = NativeUiFactory.Input(
                row.transform,
                text: value ?? string.Empty,
                placeholder: placeholderText ?? string.Empty,
                name: "Input",
                onValueChanged: (UnityAction<string>)((text) =>
                {
                    setter(text);
                    SettingsMenuController.Instance.NotifyDraftChanged();
                }));
            input.lineType = TMP_InputField.LineType.SingleLine;
            bool isServerName = string.Equals(label, "Server Name", StringComparison.OrdinalIgnoreCase);
            UiStyles.SetInputTextStyle(input, isServerName ? ServerNameFontSize : InputFontSize, autoSize: isServerName, minFontSize: 13f);
            UiLayout.SetFixedSize(input.gameObject, flexibleWidth: 1f, preferredHeight: InputHeight);
        }

        private static void AddToggle(
            Transform parent,
            string label,
            bool value,
            Action<bool> setter,
            bool clearLabel = false,
            bool stabilize = true,
            bool useFixedLayout = true,
            float? preferredHeight = null,
            Vector2? anchoredPosition = null,
            float labelOffsetX = 0f)
        {
            Toggle toggle = NativeUiFactory.Toggle(
                parent,
                label: label,
                isOn: value,
                name: label,
                onValueChanged: (UnityAction<bool>)((isOn) =>
                {
                    setter(isOn);
                    SettingsMenuController.Instance.NotifyDraftChanged();
                }));

            TMP_Text labelText = toggle.GetComponentInChildren<TMP_Text>(true);
            if (clearLabel && labelText != null)
                UiStyles.SetText(labelText, string.Empty);
            UiStyles.SetTextMetrics(labelText, LabelFontSize, TextAlignmentOptions.Left, autoSize: true, minFontSize: 12f);
            if (labelText != null && Mathf.Abs(labelOffsetX) > 0.001f)
                labelText.rectTransform.localPosition += new Vector3(labelOffsetX, 0f, 0f);

            if (anchoredPosition.HasValue)
                UiLayout.SetRect(toggle, anchoredPosition: anchoredPosition.Value);

            float height = preferredHeight ?? ToggleHeight;
            if (useFixedLayout)
                UiLayout.SetFixedSize(toggle.gameObject, flexibleWidth: 1f, preferredHeight: height);
            else
                UiLayout.SetLayout(toggle.gameObject, flexibleWidth: 1f, preferredHeight: height);

            if (stabilize)
                UiLayout.StabilizeClonedControl(toggle.gameObject);
        }

        internal static Button CreateSettingsButton(
            Transform parent,
            string label,
            UnityAction onClick,
            string name,
            float fontSize,
            Vector2 size,
            Color? imageColor = null,
            Color? shadowColor = null)
        {
            GameObject buttonObject = NativeUiFactory.Object(name, parent).GameObject;
            UiLayout.SetRect(
                buttonObject.GetComponent<RectTransform>(),
                anchorMin: new Vector2(0.5f, 0.5f),
                anchorMax: new Vector2(0.5f, 0.5f),
                pivot: new Vector2(0.5f, 0.5f),
                anchoredPosition: Vector2.zero,
                sizeDelta: size,
                scale: Vector3.one);

            var templates = NativeUiTemplates.Current;
            Image image = UiStyles.CopyImage(templates.Button != null ? templates.Button.GetComponent<Image>() : templates.Background, buttonObject);
            Shadow shadow = UiStyles.CopyShadow(templates.Button != null ? templates.Button.GetComponent<Shadow>() : templates.Shadow, buttonObject);
            if (image != null)
                image.color = imageColor ?? NormalButtonColor;
            if (shadow != null)
            {
                shadow.effectColor = shadowColor ?? NormalButtonShadowColor;
                shadow.effectDistance *= 0.55f;
            }
            Button button = buttonObject.AddComponent<Button>();
            button.targetGraphic = image;
            if (templates.Button != null)
            {
                button.colors = templates.Button.colors;
                button.spriteState = templates.Button.spriteState;
                button.transition = Selectable.Transition.ColorTint;
                button.navigation = templates.Button.navigation;
            }
            button.colors = CreateButtonColorBlock(button.colors);
            if (onClick != null)
                button.onClick.AddListener(onClick);

            UiLayout.SetFixedSize(button.gameObject, preferredWidth: size.x, preferredHeight: size.y, flexibleWidth: 0f, flexibleHeight: 0f);

            GameObject labelObject = NativeUiFactory.Object("Label", buttonObject.transform).GameObject;
            TMP_Text text = labelObject.AddComponent<TextMeshProUGUI>();
            UiStyles.ApplyText(text, templates.ButtonLabel ?? templates.Label);
            text.color = Color.white;
            text.raycastTarget = false;
            UiStyles.SetText(text, label);
            UiStyles.SetTextMetrics(text, fontSize, TextAlignmentOptions.Center, autoSize: true, minFontSize: 10f);
            UiLayout.Stretch(labelObject);
            text.ForceMeshUpdate();

            buttonObject.SetActive(true);
            return button;
        }

        private static void RegisterDirtyButtonFeedback(params Button[] buttons)
        {
            ColorBlock[] normalColorBlocks = new ColorBlock[buttons.Length];

            for (int i = 0; i < buttons.Length; i++)
            {
                normalColorBlocks[i] = buttons[i] != null ? buttons[i].colors : default;
            }

            SettingsMenuController.Instance.AddDirtyStateListener((dirty) =>
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button button = buttons[i];
                    if (button == null)
                        continue;

                    Image image = button.GetComponent<Image>();
                    Shadow shadow = button.GetComponent<Shadow>();
                    Color imageColor = dirty ? DirtyButtonColor : NormalButtonColor;
                    Color shadowColor = dirty ? DirtyButtonShadowColor : NormalButtonShadowColor;

                    if (image != null)
                        image.color = imageColor;
                    if (shadow != null)
                        shadow.effectColor = shadowColor;

                    button.colors = CreateButtonColorBlock(normalColorBlocks[i]);
                }
            });
        }

        private static ColorBlock CreateButtonColorBlock(ColorBlock template)
        {
            template.normalColor = Color.white;
            template.highlightedColor = new Color(0.88f, 0.88f, 0.88f, 1f);
            template.selectedColor = template.highlightedColor;
            template.pressedColor = new Color(0.72f, 0.72f, 0.72f, 1f);
            template.disabledColor = new Color(0.72f, 0.72f, 0.72f, 0.5f);
            return template;
        }

        private static void DisableChildHeightControl(GameObject row)
        {
            HorizontalLayoutGroup layout = row != null ? row.GetComponent<HorizontalLayoutGroup>() : null;
            if (layout == null)
                return;

            layout.childControlHeight = false;
            layout.childForceExpandHeight = false;
        }

        private static void AddMessageRow(
            Transform parent,
            string label,
            bool isOn,
            Action<bool> setToggle,
            int sizeValue,
            Action<int> setSize)
        {
            AddGridRow(parent, MessageRowHeight, RowSpacing, new[] { 2f, 1f, 4f }, cells =>
            {
                AddToggle(
                    cells[0],
                    label,
                    isOn,
                    setToggle,
                    clearLabel: false,
                    stabilize: false,
                    useFixedLayout: false,
                    preferredHeight: MessageRowHeight,
                    anchoredPosition: new Vector2(0f, -MessageRowHeight * 0.5f));
                CreateSizeSlider(cells[1], sizeValue, setSize);
            });
        }

        public static void RefreshToggleState(GameObject root, string toggleName, bool isOn)
        {
            if (root == null || string.IsNullOrEmpty(toggleName))
                return;

            Toggle[] toggles = root.GetComponentsInChildren<Toggle>(true);
            for (int i = 0; i < toggles.Length; i++)
            {
                Toggle toggle = toggles[i];
                if (toggle == null)
                    continue;

                if (string.Equals(toggle.gameObject.name, toggleName, StringComparison.Ordinal))
                {
                    toggle.SetIsOnWithoutNotify(isOn);
                    return;
                }
            }
        }

        private static MySliderUI CreateSizeSlider(Transform parent, int sizeValue, Action<int> setSize)
        {
            MySliderUI sliderUi = null;
            sliderUi = NativeUiFactory.Slider(
                parent,
                name: "Size Slider",
                minValue: 50f,
                maxValue: 100f,
                value: Mathf.Clamp(sizeValue, 50, 100),
                onValueChanged: (UnityAction<float>)((value) =>
                {
                    setSize(Mathf.RoundToInt(value));
                    sliderUi?.UpdateSliderValueDisplay();
                    SettingsMenuController.Instance.NotifyDraftChanged();
                }));

            if (sliderUi == null || sliderUi.slider == null)
                return null;

            foreach (LayoutGroup layout in sliderUi.GetComponentsInChildren<LayoutGroup>(true))
            {
                if (layout != null)
                    layout.enabled = false;
            }

            UiLayout.SetRect(sliderUi, anchoredPosition: new Vector2(350f, MessageRowHeight * -0.5f));
            UiLayout.SetRect(sliderUi.slider, anchoredPosition: Vector2.zero);
            sliderUi.slider.transform.localPosition = Vector3.zero;

            TMP_Text sliderLabel = sliderUi.transform.Find("(Text) Slider Label")?.GetComponent<TMP_Text>();
            if (sliderLabel != null)
            {
                UiStyles.SetText(sliderLabel, "Size");
                UiStyles.SetTextMetrics(sliderLabel, LabelFontSize, TextAlignmentOptions.Left);
                sliderLabel.color = Color.white;
                sliderLabel.ForceMeshUpdate();
            }

            if (sliderUi.sliderValueText != null)
            {
                UiLayout.SetRect(
                    sliderUi.sliderValueText,
                    anchorMin: new Vector2(0f, 0.5f),
                    anchorMax: new Vector2(0f, 0.5f),
                    pivot: new Vector2(1f, 0.5f));
                sliderUi.sliderValueText.transform.localPosition = new Vector3(160f, 17.5f, 0f);
                UiStyles.SetTextMetrics(sliderUi.sliderValueText, LabelFontSize, TextAlignmentOptions.Right);
                sliderUi.sliderValueText.color = Color.white;
                sliderUi.sliderValueText.ForceMeshUpdate();
            }

            UiLayout.SetFixedSize(sliderUi.gameObject, flexibleWidth: 1f, preferredHeight: SliderHeight);

            return sliderUi;
        }

        private static void AddGridRow(Transform parent, float height, float spacing, float[] trackWeights, Action<Transform[]> build)
        {
            var row = NativeUiBuilder.GridTrackRow(parent, height: height, spacing: spacing, trackWidths: trackWeights);
            UiLayout.SetFixedSize(row.GameObject, flexibleWidth: 1f, preferredHeight: height);

            if (build == null || row.Tracks == null)
                return;

            build(Array.ConvertAll(row.Tracks, track => track != null ? track.transform : null));
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(row.GameObject.GetComponent<RectTransform>());
            UiLayout.LayoutGridTracks(row, spacing: spacing, trackWidths: trackWeights);
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(row.GameObject.GetComponent<RectTransform>());
        }

        private static GameObject AddPreferenceRow(Transform parent, string label)
        {
            GameObject row = NativeUiBuilder.HorizontalRow(parent, height: InputHeight, spacing: RowSpacing).GameObject;
            UiLayout.SetFixedSize(row, flexibleWidth: 1f, preferredHeight: InputHeight);

            if (string.IsNullOrEmpty(label))
            {
                UiLayout.SetRect(
                    row.GetComponent<RectTransform>(),
                    anchorMin: new Vector2(0f, 0.5f),
                    anchorMax: new Vector2(1f, 0.5f),
                    pivot: new Vector2(0.5f, 0.5f));
                RectTransform rowRect = row.GetComponent<RectTransform>();
                if (rowRect != null)
                {
                    rowRect.offsetMin = new Vector2(0f, -InputHeight * 0.5f);
                    rowRect.offsetMax = new Vector2(0f, InputHeight * 0.5f);
                }
                return row;
            }

            TMP_Text labelText = NativeUiFactory.Label(row.transform, label, "Label", NativeUiTemplates.Current.SliderLabel ?? NativeUiTemplates.Current.Label);
            UiStyles.SetTextMetrics(labelText, LabelFontSize, TextAlignmentOptions.Left, autoSize: true, minFontSize: 12f);
            labelText.color = Color.white;
            labelText.ForceMeshUpdate();
            UiLayout.SetFixedSize(labelText.gameObject, preferredWidth: LabelWidth, preferredHeight: InputHeight);
            return row;
        }
    }
}
