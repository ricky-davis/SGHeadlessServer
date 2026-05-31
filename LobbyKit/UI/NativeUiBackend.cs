using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Localization.Components;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace LobbyKit
{
    internal static class NativeUiBackend
    {
        private static int _lastAutoCaptureFrame = -1;
        private static readonly Color FallbackPanelColor = new Color(0.09f, 0.5f, 0.74f, 0.97f);
        private static readonly Color FallbackInputColor = new Color(0.95f, 0.96f, 0.98f, 1f);
        private static readonly Color FallbackToggleCheckColor = new Color(0.17f, 0.74f, 0.45f, 1f);

        internal static NativeUiTemplateSet Defaults { get; } = new NativeUiTemplateSet();
        private static void SetDefaults(NativeUiTemplateSet references, bool overwriteExisting = true)
        {
            if (references == null)
                return;

            Defaults.Button = PickDefault(Defaults.Button, references.Button, overwriteExisting);
            Defaults.Label = PickDefault(Defaults.Label, references.Label, overwriteExisting);
            Defaults.HeaderLabel = PickDefault(Defaults.HeaderLabel, references.HeaderLabel, overwriteExisting);
            Defaults.ButtonLabel = PickDefault(Defaults.ButtonLabel, references.ButtonLabel, overwriteExisting);
            Defaults.ToggleLabel = PickDefault(Defaults.ToggleLabel, references.ToggleLabel, overwriteExisting);
            Defaults.SliderLabel = PickDefault(Defaults.SliderLabel, references.SliderLabel, overwriteExisting);
            Defaults.InputText = PickDefault(Defaults.InputText, references.InputText, overwriteExisting);
            Defaults.InputPlaceholder = PickDefault(Defaults.InputPlaceholder, references.InputPlaceholder, overwriteExisting);
            Defaults.InputField = PickDefault(Defaults.InputField, references.InputField, overwriteExisting);
            Defaults.Toggle = PickDefault(Defaults.Toggle, references.Toggle, overwriteExisting);
            Defaults.Slider = PickDefault(Defaults.Slider, references.Slider, overwriteExisting);
            Defaults.Scrollbar = PickDefault(Defaults.Scrollbar, references.Scrollbar, overwriteExisting);
            Defaults.Background = PickDefault(Defaults.Background, references.Background, overwriteExisting);
            Defaults.Shadow = PickDefault(Defaults.Shadow, references.Shadow, overwriteExisting);
            Defaults.Panel = PickDefault(Defaults.Panel, references.Panel, overwriteExisting);
        }

        private static void SetDefault(Button template, bool overwriteExisting = true)
        {
            Defaults.Button = PickDefault(Defaults.Button, template, overwriteExisting);
            Defaults.ButtonLabel = PickDefault(Defaults.ButtonLabel, template != null ? template.GetComponentInChildren<TMP_Text>(true) : null, overwriteExisting);
            Defaults.Shadow = PickDefault(Defaults.Shadow, template != null ? template.GetComponent<Shadow>() : null, overwriteExisting);
        }

        internal static void CaptureDefaultsFrom(Transform root, bool overwriteExisting = false)
        {
            if (root == null)
                return;

            Button button = FindButtonTemplate(root);
            Toggle toggle = FindToggleTemplate(root);
            MySliderUI slider = root.GetComponentInChildren<MySliderUI>(true);
            TMP_InputField input = root.GetComponentInChildren<TMP_InputField>(true);
            TMP_Text buttonLabel = button != null ? button.GetComponentInChildren<TMP_Text>(true) : null;
            TMP_Text toggleLabel = toggle != null ? toggle.GetComponentInChildren<TMP_Text>(true) : null;
            TMP_Text sliderLabel = slider != null ? slider.GetComponentInChildren<TMP_Text>(true) : null;
            TMP_Text inputText = input != null ? input.textComponent : null;
            TMP_Text inputPlaceholder = input != null && input.placeholder != null ? input.placeholder.GetComponent<TMP_Text>() : null;

            SetDefaults(new NativeUiTemplateSet
            {
                Button = button,
                Label = root.GetComponentInChildren<TMP_Text>(true),
                ButtonLabel = buttonLabel,
                ToggleLabel = toggleLabel,
                SliderLabel = sliderLabel,
                InputText = inputText,
                InputPlaceholder = inputPlaceholder,
                InputField = input,
                Toggle = toggle,
                Slider = slider,
                Scrollbar = root.GetComponentInChildren<Scrollbar>(true),
                Background = FindBackgroundImage(root),
                Shadow = root.GetComponentInChildren<Shadow>(true),
                Panel = FindPanelTemplate(root)
            }, overwriteExisting);
        }

        internal static void CaptureSceneDefaults(bool overwriteExisting = false)
        {
            GameObject createLobby = FindNamedObject(SleddingUiPaths.CreateLobbyRoot);
            if (createLobby != null)
            {
                CaptureCreateLobbyDefaults(createLobby.transform, overwriteExisting);
                CaptureDefaultsFrom(createLobby.transform, overwriteExisting);
            }

            Button hostButton = FindNamedComponent<Button>(
                SleddingUiPaths.HostButton,
                SleddingUiPaths.JoinButton,
                SleddingUiPaths.JoinButtonUpper);
            if (hostButton != null)
                SetDefault(hostButton, overwriteExisting);

            TMP_Text settingsHeader = FindNamedComponent<TMP_Text>(
                SleddingUiPaths.SettingsHeaderQuickChanges,
                SleddingUiPaths.SettingsHeaderSectionTitle,
                SleddingUiPaths.SettingsHeaderChatRelated);
            Defaults.HeaderLabel = PickDefault(Defaults.HeaderLabel, settingsHeader, overwriteExisting);

            GameObject mainMenuPanel = FindNamedObject(SleddingUiPaths.MainMenuRoot)?.transform.Find(SleddingUiPaths.MainMenuPanel)?.gameObject;
            if (mainMenuPanel != null)
                CaptureDefaultsFrom(mainMenuPanel.transform, overwriteExisting);
        }

        internal static UiElement Assume(GameObject gameObject)
        {
            return new UiElement(gameObject);
        }

        internal static UiElement Assume(Component component)
        {
            return new UiElement(component != null ? component.gameObject : null);
        }

        internal static UiElement Assume(Transform transform)
        {
            return new UiElement(transform != null ? transform.gameObject : null);
        }

        internal static UiElement Find(string sceneObjectName)
        {
            return new UiElement(string.IsNullOrEmpty(sceneObjectName) ? null : FindNamedObject(sceneObjectName));
        }

        internal static UiElement Create(string name, Transform parent = null, bool active = true)
        {
            GameObject gameObject = new GameObject(string.IsNullOrEmpty(name) ? "UI Element" : name);
            if (parent != null)
                gameObject.transform.SetParent(parent, false);

            EnsureRectTransform(gameObject);
            gameObject.SetActive(active);
            return new UiElement(gameObject);
        }

        internal static UiElement Clone(GameObject template, Transform parent, string name = null, int? siblingIndex = null, bool active = true)
        {
            if (template == null)
                return new UiElement(null);

            GameObject clone = Object.Instantiate(template, parent);
            if (clone == null)
                return new UiElement(null);

            if (!string.IsNullOrEmpty(name))
                clone.name = name;
            if (siblingIndex.HasValue)
                clone.transform.SetSiblingIndex(siblingIndex.Value);

            clone.SetActive(active);
            return new UiElement(clone);
        }

        internal static T CloneComponent<T>(T template, Transform parent, string name = null, int? siblingIndex = null, bool active = true)
            where T : Component
        {
            return Clone(template != null ? template.gameObject : null, parent, name, siblingIndex, active).Get<T>();
        }

        internal static TMP_InputField CloneInputField(
            TMP_InputField template,
            Transform parent,
            string name = null,
            string text = null,
            string placeholder = null,
            int? siblingIndex = null,
            bool clearListeners = true)
        {
            TMP_InputField clone = CloneComponent(template, parent, name, siblingIndex);
            if (clone == null)
                return null;

            if (clearListeners)
                ClearInputFieldEvents(clone);
            if (text != null)
                clone.text = text;

            TMP_Text placeholderText = clone.placeholder != null ? clone.placeholder.GetComponent<TMP_Text>() : null;
            if (placeholderText != null && placeholder != null)
                SetText(placeholderText, placeholder);

            return clone;
        }

        internal static Toggle CloneToggle(
            Toggle template,
            Transform parent,
            string name = null,
            string label = null,
            bool? isOn = null,
            UnityAction<bool> onValueChanged = null,
            int? siblingIndex = null,
            bool clearListeners = true)
        {
            Toggle clone = CloneComponent(template, parent, name, siblingIndex);
            if (clone == null)
                return null;

            if (label != null)
                SetText(clone.GetComponentInChildren<TMP_Text>(true), label);
            if (clearListeners)
                clone.onValueChanged.RemoveAllListeners();
            if (isOn.HasValue)
                clone.isOn = isOn.Value;
            ApplyToggleStyle(clone, template);
            if (onValueChanged != null)
                clone.onValueChanged.AddListener(onValueChanged);

            return clone;
        }

        internal static TMP_Text CreatePlainLabel(
            Transform parent,
            string text,
            string name = "Label",
            TMP_Text template = null,
            bool active = true)
        {
            UiElement element = Create(string.IsNullOrEmpty(name) ? "Label" : name, parent, active);
            TMP_Text label = element.GameObject.AddComponent<TextMeshProUGUI>();
            ApplyTextStyle(label, template ?? Defaults.Label);
            label.raycastTarget = false;
            SetText(label, text);
            return label;
        }

        internal static MySliderUI CreateSlider(
            Transform parent,
            string name = "Slider",
            MySliderUI template = null,
            float? minValue = null,
            float? maxValue = null,
            float? value = null,
            UnityAction<float> onValueChanged = null,
            int? siblingIndex = null,
            bool active = true,
            bool clearListeners = true)
        {
            CaptureSceneDefaultsIf(template == null && Defaults.Slider == null);
            MySliderUI sliderUi = CloneComponent(template ?? Defaults.Slider, parent, name, siblingIndex);
            if (sliderUi == null || sliderUi.slider == null)
                return sliderUi;

            if (clearListeners)
            {
                sliderUi.slider.onValueChanged.RemoveAllListeners();
                sliderUi.OnSliderValueFinishedChanging = null;
            }
            if (minValue.HasValue)
                sliderUi.slider.minValue = minValue.Value;
            if (maxValue.HasValue)
                sliderUi.slider.maxValue = maxValue.Value;
            if (value.HasValue)
                sliderUi.slider.SetValueWithoutNotify(value.Value);
            if (onValueChanged != null)
                sliderUi.slider.onValueChanged.AddListener(onValueChanged);

            sliderUi.UpdateSliderValueDisplay();
            sliderUi.gameObject.SetActive(active);
            return sliderUi;
        }

        private static Scrollbar CreateScrollbar(
            Transform parent,
            string name = "Scrollbar",
            Scrollbar template = null,
            float? value = null,
            float? size = null,
            UnityAction<float> onValueChanged = null,
            int? siblingIndex = null,
            bool active = true,
            bool clearListeners = true)
        {
            CaptureSceneDefaultsIf(template == null && Defaults.Scrollbar == null);
            Scrollbar scrollbar = CloneComponent(template ?? Defaults.Scrollbar, parent, name, siblingIndex);
            if (scrollbar == null)
                return null;

            if (clearListeners)
                scrollbar.onValueChanged.RemoveAllListeners();
            if (size.HasValue)
                scrollbar.size = size.Value;
            if (value.HasValue)
                scrollbar.value = value.Value;
            if (onValueChanged != null)
                scrollbar.onValueChanged.AddListener(onValueChanged);

            scrollbar.gameObject.SetActive(active);
            return scrollbar;
        }

        internal static ScrollViewport CreateScrollViewport(
            Transform parent,
            string name = "Scroll Viewport",
            Vector2? sizeDelta = null,
            Scrollbar scrollbarTemplate = null)
        {
            const float scrollbarInset = 24f;
            const float scrollbarWidth = 18f;
            const float contentRightPadding = 16f;

            UiElement root = Create(name, parent);
            GameObject rootGo = root.GameObject;
            if (sizeDelta.HasValue)
                SetRect(EnsureRectTransform(rootGo), sizeDelta: sizeDelta);

            SetLayout(rootGo, flexibleHeight: 1f, flexibleWidth: 1f);

            ScrollRect scrollRect = rootGo.GetComponent<ScrollRect>() ?? rootGo.AddComponent<ScrollRect>();
            Image viewportImage = CreateBackground(rootGo.transform, "Viewport Background", template: Defaults.Background ?? (Defaults.Panel != null ? Defaults.Panel.GetComponent<Image>() : null));
            RectTransform viewportRect = EnsureRectTransform(viewportImage.gameObject);
            viewportRect.anchorMin = new Vector2(0f, 0f);
            viewportRect.anchorMax = new Vector2(1f, 1f);
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = new Vector2(-(scrollbarInset + scrollbarWidth), 0f);

            // Content object
            GameObject content = new GameObject("Content");
            content.transform.SetParent(viewportRect, false);
            RectTransform contentRect = EnsureRectTransform(content);
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 0f);

            SetVerticalLayout(content, new RectOffset(0, (int)contentRightPadding, 0, 0), spacing: 8f);
            ContentSizeFitter fitter = content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            // Configure scroll rect
            scrollRect.content = contentRect;
            scrollRect.viewport = viewportRect;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;

            Scrollbar scrollbar = null;
            scrollbarTemplate ??= Defaults.Scrollbar;
            if (scrollbarTemplate != null)
            {
                scrollbar = CreateScrollbar(rootGo.transform, "Scrollbar", template: scrollbarTemplate);
                if (scrollbar != null)
                {
                    RectTransform sbRect = EnsureRectTransform(scrollbar.gameObject);
                    sbRect.anchorMin = new Vector2(1f, 0f);
                    sbRect.anchorMax = new Vector2(1f, 1f);
                    sbRect.pivot = new Vector2(1f, 0.5f);
                    sbRect.anchoredPosition = new Vector2(-scrollbarInset, 0f);
                    sbRect.sizeDelta = new Vector2(scrollbarWidth, 0f);

                    scrollRect.verticalScrollbar = scrollbar;
                    scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
                }
            }

            Mask mask = viewportImage.gameObject.GetComponent<Mask>() ?? viewportImage.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;

            return new ScrollViewport
            {
                Root = rootGo,
                ViewportRect = viewportRect,
                ContentRect = contentRect,
                ScrollRect = scrollRect,
                Scrollbar = scrollbar
            };
        }

        internal static UiElement CreatePanel(
            Transform parent,
            string name = "Panel",
            GameObject template = null,
            Vector2? sizeDelta = null,
            Vector2? anchoredPosition = null,
            int? siblingIndex = null,
            bool active = true,
            bool cloneTemplate = false)
        {
            CaptureSceneDefaultsIf(template == null && Defaults.Panel == null && Defaults.Background == null);
            Image explicitPanelBackground = template == null ? FindLobbyPanelBackground() : null;
            GameObject panelTemplate = template
                ?? (explicitPanelBackground != null ? explicitPanelBackground.gameObject : null)
                ?? Defaults.Panel
                ?? (Defaults.Background != null ? Defaults.Background.gameObject : null);

            if (cloneTemplate && panelTemplate != null)
            {
                UiElement clone = Clone(panelTemplate, parent, name, siblingIndex, active);
                NormalizeClonedUiRoot(clone.GameObject);
                SetRect(clone.RectTransform, anchoredPosition, sizeDelta);
                return clone;
            }

            UiElement panel = Create(name, parent, active);
            if (siblingIndex.HasValue)
                panel.SiblingIndex(siblingIndex.Value);

            Image panelImageTemplate = explicitPanelBackground ?? (panelTemplate != null ? panelTemplate.GetComponent<Image>() : null);
            panelImageTemplate ??= Defaults.Background;
            Image panelImage = CopyImage(panelImageTemplate, panel.GameObject);
            if (panelImage != null)
            {
                panelImage.raycastTarget = true;
                if (panelTemplate == null && Defaults.Background == null)
                    panelImage.color = FallbackPanelColor;
            }
            else if (panelTemplate == null && Defaults.Background == null)
            {
                panelImage = panel.GameObject.AddComponent<Image>();
                panelImage.color = FallbackPanelColor;
                panelImage.raycastTarget = true;
            }

            Shadow shadowTemplate = panelImageTemplate != null ? panelImageTemplate.GetComponent<Shadow>() : null;
            shadowTemplate ??= panelTemplate != null ? panelTemplate.GetComponent<Shadow>() : null;
            shadowTemplate ??= Defaults.Shadow;
            if (shadowTemplate != null)
                CopyShadow(shadowTemplate, panel.GameObject);

            LayoutElement layoutTemplate = panelTemplate != null ? panelTemplate.GetComponent<LayoutElement>() : null;
            if (layoutTemplate != null)
                CopyLayout(layoutTemplate, panel.GameObject);

            SetRect(panel.RectTransform, anchoredPosition, sizeDelta);
            return panel;
        }

        internal static Image CreateBackground(
            Transform parent,
            string name = "Background",
            Image template = null,
            Color? color = null,
            Vector2? sizeDelta = null,
            Vector2? anchoredPosition = null,
            int? siblingIndex = null,
            bool active = true)
        {
            CaptureSceneDefaultsIf(template == null && Defaults.Background == null);
            UiElement backgroundElement = Create(name, parent, active);
            if (siblingIndex.HasValue)
                backgroundElement.SiblingIndex(siblingIndex.Value);

            Image background = CopyImage(template ?? Defaults.Background ?? (Defaults.Button != null ? Defaults.Button.GetComponent<Image>() : null), backgroundElement.GameObject);
            if (background == null)
                return null;

            if (color.HasValue)
                background.color = color.Value;
            else if (template == null && Defaults.Background == null && Defaults.Button == null)
                background.color = FallbackPanelColor;
            SetRect(background, anchoredPosition, sizeDelta);
            return background;
        }

        internal static Image CreateScreenBackdrop(
            Transform parent,
            string name = "Backdrop",
            Image template = null,
            Color? color = null)
        {
            UiElement backdrop = Create(name, parent);
            Image image = CopyImage(template, backdrop.GameObject);
            if (image == null)
                return null;

            image.color = color ?? (template != null ? template.color : new Color(0f, 0f, 0f, 0.45f));
            image.raycastTarget = true;
            Stretch(backdrop.GameObject);
            return image;
        }

        internal static RectTransform EnsureRectTransform(GameObject gameObject)
        {
            if (gameObject == null)
                return null;

            RectTransform rectTransform = gameObject.GetComponent<RectTransform>();
            return rectTransform != null ? rectTransform : gameObject.AddComponent<RectTransform>();
        }

        internal static void SetRect(
            Component component,
            Vector2? anchoredPosition = null,
            Vector2? sizeDelta = null,
            Vector2? anchorMin = null,
            Vector2? anchorMax = null,
            Vector2? pivot = null,
            Vector3? scale = null)
        {
            SetRect(component != null ? component.GetComponent<RectTransform>() : null, anchoredPosition, sizeDelta, anchorMin, anchorMax, pivot, scale);
        }

        internal static void SetRect(
            RectTransform rectTransform,
            Vector2? anchoredPosition = null,
            Vector2? sizeDelta = null,
            Vector2? anchorMin = null,
            Vector2? anchorMax = null,
            Vector2? pivot = null,
            Vector3? scale = null)
        {
            if (rectTransform == null)
                return;

            if (anchoredPosition.HasValue)
                rectTransform.anchoredPosition = anchoredPosition.Value;
            if (sizeDelta.HasValue)
                rectTransform.sizeDelta = sizeDelta.Value;
            if (anchorMin.HasValue)
                rectTransform.anchorMin = anchorMin.Value;
            if (anchorMax.HasValue)
                rectTransform.anchorMax = anchorMax.Value;
            if (pivot.HasValue)
                rectTransform.pivot = pivot.Value;
            if (scale.HasValue)
                rectTransform.localScale = scale.Value;
        }

        internal static void SetCanvasGroups(
            Transform root,
            float alpha = 1f,
            bool interactable = true,
            bool blocksRaycasts = true,
            bool includeInactive = true,
            bool setRootActive = false)
        {
            if (root == null)
                return;

            if (setRootActive)
                root.gameObject.SetActive(true);
            foreach (CanvasGroup canvasGroup in root.GetComponentsInChildren<CanvasGroup>(includeInactive))
            {
                canvasGroup.alpha = alpha;
                canvasGroup.interactable = interactable;
                canvasGroup.blocksRaycasts = blocksRaycasts;
            }
        }

        internal static void ActivatePathToRoot(Transform start, Transform root)
        {
            for (Transform target = start; target != null; target = target.parent)
            {
                target.gameObject.SetActive(true);
                if (target == root)
                    break;
            }
        }

        internal static void SetChildrenActive(Transform parent, bool active, string skipNameContains = null)
        {
            if (parent == null)
                return;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child == null)
                    continue;
                if (!string.IsNullOrEmpty(skipNameContains) && child.name.Contains(skipNameContains))
                    continue;

                child.gameObject.SetActive(active);
            }
        }

        internal static VerticalLayoutGroup SetVerticalLayout(
            GameObject gameObject,
            RectOffset padding = null,
            float spacing = 0f,
            bool childControlWidth = true,
            bool childControlHeight = true,
            bool childForceExpandWidth = true,
            bool childForceExpandHeight = false)
        {
            if (gameObject == null)
                return null;

            VerticalLayoutGroup layout = gameObject.GetComponent<VerticalLayoutGroup>();
            if (layout == null)
                layout = gameObject.AddComponent<VerticalLayoutGroup>();

            layout.padding = padding ?? new RectOffset();
            layout.spacing = spacing;
            layout.childControlWidth = childControlWidth;
            layout.childControlHeight = childControlHeight;
            layout.childForceExpandWidth = childForceExpandWidth;
            layout.childForceExpandHeight = childForceExpandHeight;
            return layout;
        }

        internal static HorizontalLayoutGroup SetHorizontalLayout(
            GameObject gameObject,
            float spacing = 0f,
            RectOffset padding = null,
            bool childControlWidth = true,
            bool childControlHeight = true,
            bool childForceExpandWidth = false,
            bool childForceExpandHeight = false)
        {
            if (gameObject == null)
                return null;

            HorizontalLayoutGroup layout = gameObject.GetComponent<HorizontalLayoutGroup>();
            if (layout == null)
                layout = gameObject.AddComponent<HorizontalLayoutGroup>();

            layout.padding = padding ?? new RectOffset();
            layout.spacing = spacing;
            layout.childControlWidth = childControlWidth;
            layout.childControlHeight = childControlHeight;
            layout.childForceExpandWidth = childForceExpandWidth;
            layout.childForceExpandHeight = childForceExpandHeight;
            return layout;
        }

        internal static UiElement CreateHorizontalRow(Transform parent, string name = "Row", float height = 38f, float spacing = 14f)
        {
            UiElement row = Create(name, parent);
            SetHorizontalLayout(row.GameObject, spacing);
            SetFixedLayoutSize(row.GameObject, preferredHeight: height);
            return row;
        }

        internal static UiElement CreateFlexRow(
            Transform parent,
            string name = "Row",
            float height = 38f,
            float spacing = 14f,
            RectOffset padding = null)
        {
            UiElement row = Create(name, parent);
            SetHorizontalLayout(
                row.GameObject,
                spacing,
                padding,
                childControlWidth: false,
                childControlHeight: true,
                childForceExpandWidth: false,
                childForceExpandHeight: false);
            SetFixedLayoutSize(row.GameObject, preferredHeight: height);
            return row;
        }

        internal static GridTrackRow CreateGridTrackRow(
            Transform parent,
            string name = "Row",
            float height = 38f,
            float spacing = 14f,
            RectOffset padding = null,
            params float[] trackWidths)
        {
            UiElement row = Create(name, parent);
            SetFixedLayoutSize(row.GameObject, preferredHeight: height);

            if (trackWidths == null || trackWidths.Length == 0)
                trackWidths = new[] { 0f, 0f, 1f };

            RectTransform[] tracks = new RectTransform[trackWidths.Length];
            float totalFlexible = 0f;

            for (int i = 0; i < trackWidths.Length; i++)
            {
                if (trackWidths[i] > 0f)
                    totalFlexible += trackWidths[i];
            }

            for (int i = 0; i < trackWidths.Length; i++)
            {
                GameObject cell = Create($"{name} Track {i}", row.GameObject.transform).GameObject;
                RectTransform cellRect = EnsureRectTransform(cell);
                cellRect.anchorMin = new Vector2(0f, 0.5f);
                cellRect.anchorMax = new Vector2(0f, 0.5f);
                cellRect.pivot = new Vector2(0f, 0.5f);
                cellRect.anchoredPosition = Vector2.zero;
                cellRect.sizeDelta = new Vector2(0f, height);

                tracks[i] = cellRect;
            }

            GridTrackRow grid = new GridTrackRow(row.GameObject, tracks);
            LayoutGridTracks(grid, spacing, padding, trackWidths);
            return grid;
        }

        internal static void LayoutGridTracks(GridTrackRow gridRow, float spacing = 14f, RectOffset padding = null, params float[] trackWidths)
        {
            if (gridRow == null || gridRow.Tracks == null || gridRow.Tracks.Length == 0)
                return;

            RectTransform rowRect = gridRow.GameObject != null ? gridRow.GameObject.GetComponent<RectTransform>() : null;
            if (rowRect == null)
                return;

            float leftPadding = padding != null ? padding.left : 0f;
            float rightPadding = padding != null ? padding.right : 0f;
            float rowWidth = rowRect.rect.width > 0f ? rowRect.rect.width : rowRect.sizeDelta.x;
            if (rowWidth <= 0f && rowRect.parent is RectTransform parentRect)
                rowWidth = parentRect.rect.width > 0f ? parentRect.rect.width : parentRect.sizeDelta.x;
            if (rowWidth <= 0f)
                return;

            if (trackWidths == null || trackWidths.Length != gridRow.Tracks.Length)
                trackWidths = new float[gridRow.Tracks.Length];

            float flexibleUnits = 0f;
            for (int i = 0; i < trackWidths.Length; i++)
            {
                if (trackWidths[i] > 0f)
                    flexibleUnits += trackWidths[i];
            }

            float remainingWidth = Mathf.Max(0f, rowWidth - leftPadding - rightPadding - spacing * Mathf.Max(0, trackWidths.Length - 1));
            float unitWidth = flexibleUnits > 0f ? remainingWidth / flexibleUnits : 0f;
            float cursor = leftPadding;

            for (int i = 0; i < gridRow.Tracks.Length; i++)
            {
                RectTransform track = gridRow.Tracks[i];
                if (track == null)
                    continue;

                float width = trackWidths[i] > 0f ? trackWidths[i] * unitWidth : 0f;
                track.anchorMin = new Vector2(0f, 0.5f);
                track.anchorMax = new Vector2(0f, 0.5f);
                track.pivot = new Vector2(0f, 0.5f);
                track.anchoredPosition = new Vector2(cursor, 0f);
                track.sizeDelta = new Vector2(width, track.sizeDelta.y > 0f ? track.sizeDelta.y : rowRect.rect.height);
                cursor += width + spacing;
            }
        }

        internal static LayoutElement SetFixedLayoutSize(
            GameObject gameObject,
            float? preferredWidth = null,
            float? preferredHeight = null,
            float? flexibleWidth = null,
            float? flexibleHeight = null)
        {
            LayoutElement layout = SetLayout(
                gameObject,
                preferredWidth,
                preferredHeight,
                flexibleWidth,
                flexibleHeight,
                preferredWidth,
                preferredHeight);

            if (layout != null)
            {
                layout.ignoreLayout = false;
                layout.layoutPriority = 1;
            }

            return layout;
        }

        internal static LayoutElement SetLayout(
            GameObject gameObject,
            float? preferredWidth = null,
            float? preferredHeight = null,
            float? flexibleWidth = null,
            float? flexibleHeight = null,
            float? minWidth = null,
            float? minHeight = null)
        {
            if (gameObject == null)
                return null;

            LayoutElement layout = gameObject.GetComponent<LayoutElement>();
            if (layout == null)
                layout = gameObject.AddComponent<LayoutElement>();

            if (preferredWidth.HasValue)
                layout.preferredWidth = preferredWidth.Value;
            if (preferredHeight.HasValue)
                layout.preferredHeight = preferredHeight.Value;
            if (flexibleWidth.HasValue)
                layout.flexibleWidth = flexibleWidth.Value;
            if (flexibleHeight.HasValue)
                layout.flexibleHeight = flexibleHeight.Value;
            if (minWidth.HasValue)
                layout.minWidth = minWidth.Value;
            if (minHeight.HasValue)
                layout.minHeight = minHeight.Value;

            return layout;
        }

        internal static void StabilizeClonedControl(GameObject gameObject)
        {
            if (gameObject == null)
                return;

            LeanTween.cancel(gameObject);
            gameObject.transform.localScale = Vector3.one;

            foreach (ContentSizeFitter fitter in gameObject.GetComponentsInChildren<ContentSizeFitter>(true))
            {
                if (fitter != null)
                    fitter.enabled = false;
            }

            foreach (Selectable selectable in gameObject.GetComponentsInChildren<Selectable>(true))
            {
                if (selectable != null)
                    selectable.transition = Selectable.Transition.ColorTint;
            }

            foreach (AnimationSelectableUi selectableUi in gameObject.GetComponentsInChildren<AnimationSelectableUi>(true))
            {
                if (selectableUi == null)
                    continue;

                LeanTween.cancel(selectableUi.gameObject);
                selectableUi.transform.localScale = Vector3.one;
                selectableUi.enabled = false;
                selectableUi.OnSelect = null;
                selectableUi.OnDeselect = null;
                Object.Destroy(selectableUi);
            }

            foreach (Component component in gameObject.GetComponentsInChildren<Component>(true))
            {
                if (component != null && component.GetIl2CppType().Name == "AnimationSelectableUi")
                {
                    if (component is Behaviour behaviour)
                        behaviour.enabled = false;
                    Object.Destroy(component);
                }
            }

            NormalizeClonedUiRoot(gameObject);
        }

        internal static void SetTextMetrics(
            TMP_Text textComponent,
            float fontSize,
            TextAlignmentOptions alignment = TextAlignmentOptions.Left,
            bool autoSize = false,
            float minFontSize = 12f)
        {
            if (textComponent == null)
                return;

            textComponent.fontSize = fontSize;
            textComponent.fontSizeMax = fontSize;
            textComponent.fontSizeMin = minFontSize;
            textComponent.enableAutoSizing = autoSize;
            textComponent.alignment = alignment;
            textComponent.fontStyle = FontStyles.Normal;
            textComponent.enableWordWrapping = false;
            textComponent.overflowMode = TextOverflowModes.Ellipsis;
            textComponent.characterSpacing = 0f;
            textComponent.wordSpacing = 0f;
            textComponent.lineSpacing = 0f;
            textComponent.paragraphSpacing = 0f;
            textComponent.ForceMeshUpdate();
        }

        internal static void SetInputTextStyle(
            TMP_InputField input,
            float fontSize,
            TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft,
            bool autoSize = false,
            float minFontSize = 14f)
        {
            if (input == null)
                return;

            if (input.textComponent != null)
            {
                if (Defaults.InputText != null)
                    ApplyTextStyle(input.textComponent, Defaults.InputText);
                SetTextMetrics(input.textComponent, fontSize, alignment, autoSize, minFontSize);
            }

            TMP_Text placeholder = input.placeholder != null ? input.placeholder.GetComponent<TMP_Text>() : null;
            if (placeholder != null)
            {
                TMP_Text placeholderTemplate = Defaults.InputPlaceholder ?? Defaults.InputText;
                if (placeholderTemplate != null)
                    ApplyTextStyle(placeholder, placeholderTemplate);
                SetTextMetrics(placeholder, fontSize, alignment, autoSize, minFontSize);
            }
        }

        internal static TMP_Text SetText(TMP_Text textComponent, string text, bool removeLocalization = true)
        {
            if (textComponent == null)
                return null;

            if (removeLocalization)
                RemoveLocalization(textComponent);

            textComponent.text = text ?? string.Empty;
            textComponent.ForceMeshUpdate();
            return textComponent;
        }

        internal static void RemoveLocalization(Component component)
        {
            if (component == null)
                return;

            foreach (Component candidate in component.GetComponents<Component>())
            {
                if (candidate == null)
                    continue;

                string typeName = candidate.GetIl2CppType().Name;
                if (typeName.Contains("LocalizeStringEvent") ||
                    typeName.Contains("Localisation") ||
                    typeName.Contains("Localization"))
                {
                    Object.Destroy(candidate);
                }
            }
        }

        internal static void ApplyTextStyle(TMP_Text target, TMP_Text template = null)
        {
            if (target == null)
                return;

            template ??= Defaults.Label;
            if (template == null)
            {
                target.color = Color.white;
                return;
            }

            target.font = template.font;
            target.fontSharedMaterial = template.fontSharedMaterial;
            target.fontStyle = template.fontStyle;
            target.color = template.color;
            target.alignment = template.alignment;
            target.enableWordWrapping = template.enableWordWrapping;
            target.overflowMode = template.overflowMode;
            target.characterSpacing = template.characterSpacing;
            target.wordSpacing = template.wordSpacing;
            target.lineSpacing = template.lineSpacing;
            target.paragraphSpacing = template.paragraphSpacing;
        }

        internal static void ApplyToggleStyle(Toggle toggle, Toggle template = null)
        {
            if (toggle == null)
                return;

            template ??= Defaults.Toggle;
            Image templateBackground = FindChildImage(template != null ? template.transform : null, "Background");
            Image templateCheckmark = FindChildImage(template != null ? template.transform : null, "Checkmark");

            GameObject background = FindOrCreateChild(toggle.transform, "Background");
            Image backgroundImage = CopyImage(templateBackground ?? Defaults.Background, background);
            if (backgroundImage != null && templateBackground == null && Defaults.Background == null)
                backgroundImage.color = FallbackInputColor;

            RectTransform backgroundRect = EnsureRectTransform(background);
            RectTransform templateBackgroundRect = templateBackground != null ? templateBackground.GetComponent<RectTransform>() : null;
            if (templateBackgroundRect != null)
                CopyRect(templateBackgroundRect, backgroundRect);
            else
            {
                backgroundRect.anchorMin = new Vector2(0f, 0.5f);
                backgroundRect.anchorMax = new Vector2(0f, 0.5f);
                backgroundRect.pivot = new Vector2(0.5f, 0.5f);
                backgroundRect.anchoredPosition = new Vector2(10f, 0f);
                backgroundRect.localScale = Vector3.one;
                backgroundRect.sizeDelta = new Vector2(20f, 20f);
            }

            GameObject checkmark = FindOrCreateChild(background.transform, "Checkmark");
            Image checkmarkImage = CopyImage(templateCheckmark, checkmark);
            if (checkmarkImage != null && templateCheckmark == null)
                checkmarkImage.color = FallbackToggleCheckColor;

            RectTransform checkmarkRect = EnsureRectTransform(checkmark);
            RectTransform templateCheckmarkRect = templateCheckmark != null ? templateCheckmark.GetComponent<RectTransform>() : null;
            if (templateCheckmarkRect != null)
                CopyRect(templateCheckmarkRect, checkmarkRect);
            else
            {
                checkmarkRect.anchorMin = new Vector2(0.5f, 0.5f);
                checkmarkRect.anchorMax = new Vector2(0.5f, 0.5f);
                checkmarkRect.pivot = new Vector2(0.5f, 0.5f);
                checkmarkRect.anchoredPosition = Vector2.zero;
                checkmarkRect.localScale = Vector3.one;
                checkmarkRect.sizeDelta = new Vector2(10f, 10f);
            }

            toggle.targetGraphic = backgroundImage;
            toggle.graphic = checkmarkImage;
            if (toggle.GetComponent<MyToggleUI>() == null)
                toggle.gameObject.AddComponent<MyToggleUI>();

            if (template != null)
            {
                toggle.transition = template.transition;
                toggle.colors = template.colors;
                toggle.spriteState = template.spriteState;
                toggle.animationTriggers = template.animationTriggers;
                toggle.navigation = template.navigation;
            }

            ApplyTextStyle(toggle.GetComponentInChildren<TMP_Text>(true),
                template != null ? template.GetComponentInChildren<TMP_Text>(true) : (Defaults.ToggleLabel ?? Defaults.Label));
            NormalizeClonedUiRoot(toggle.gameObject);
        }

        private static Image SetImageColor(Component component, Color color)
        {
            Image image = component != null ? component.GetComponent<Image>() : null;
            if (image != null)
                image.color = color;
            return image;
        }

        private static Shadow SetShadowColor(Component component, Color color)
        {
            Shadow shadow = component != null ? component.GetComponent<Shadow>() : null;
            if (shadow != null)
                shadow.effectColor = color;
            return shadow;
        }

        internal static void SetButtonColors(Button button, Color imageColor, Color shadowColor)
        {
            SetImageColor(button, imageColor);
            SetShadowColor(button, shadowColor);
        }

        internal static Shadow CopyShadow(Shadow template, GameObject target)
        {
            return target != null ? CopyShadow(template, target.transform) : null;
        }

        internal static Shadow CopyShadow(Shadow template, Component component)
        {
            if (component == null)
                return null;

            Shadow shadow = component.GetComponent<Shadow>();
            if (shadow == null)
                shadow = component.gameObject.AddComponent<Shadow>();

            if (template != null)
            {
                shadow.effectColor = template.effectColor;
                shadow.effectDistance = template.effectDistance;
                shadow.useGraphicAlpha = template.useGraphicAlpha;
            }

            return shadow;
        }

        internal static Image CopyImage(Image template, GameObject target)
        {
            if (target == null)
                return null;

            Image image = target.GetComponent<Image>();
            if (image == null)
                image = target.AddComponent<Image>();

            if (template != null)
            {
                CopyGraphic(template, image);
                image.sprite = template.sprite;
                image.type = template.type;
                image.preserveAspect = template.preserveAspect;
                image.fillCenter = template.fillCenter;
                image.fillMethod = template.fillMethod;
                image.fillAmount = template.fillAmount;
                image.fillClockwise = template.fillClockwise;
                image.fillOrigin = template.fillOrigin;
                image.pixelsPerUnitMultiplier = template.pixelsPerUnitMultiplier;
            }

            return image;
        }

        private static void CopyGraphic(Graphic template, Graphic target)
        {
            if (template == null || target == null)
                return;

            target.color = template.color;
            target.material = template.material;
            target.raycastTarget = template.raycastTarget;
        }

        private static void CopyRect(RectTransform template, RectTransform target)
        {
            if (template == null || target == null)
                return;

            target.anchorMin = template.anchorMin;
            target.anchorMax = template.anchorMax;
            target.pivot = template.pivot;
            target.anchoredPosition = template.anchoredPosition;
            target.sizeDelta = template.sizeDelta;
            target.offsetMin = template.offsetMin;
            target.offsetMax = template.offsetMax;
            target.localScale = Vector3.one;
        }

        private static LayoutElement CopyLayout(LayoutElement template, GameObject target)
        {
            if (target == null)
                return null;

            LayoutElement layout = target.GetComponent<LayoutElement>();
            if (layout == null)
                layout = target.AddComponent<LayoutElement>();

            if (template != null)
            {
                layout.ignoreLayout = template.ignoreLayout;
                layout.minWidth = template.minWidth;
                layout.minHeight = template.minHeight;
                layout.preferredWidth = template.preferredWidth;
                layout.preferredHeight = template.preferredHeight;
                layout.flexibleWidth = template.flexibleWidth;
                layout.flexibleHeight = template.flexibleHeight;
                layout.layoutPriority = template.layoutPriority;
            }

            return layout;
        }

        private static void ClearInputFieldEvents(TMP_InputField inputField)
        {
            if (inputField == null)
                return;

            inputField.onValueChanged.RemoveAllListeners();
            inputField.onSubmit.RemoveAllListeners();
            inputField.onSelect.RemoveAllListeners();
            inputField.onDeselect.RemoveAllListeners();
        }

        private static T PickDefault<T>(T current, T candidate, bool overwriteExisting) where T : class
        {
            if (candidate == null)
                return current;
            return overwriteExisting || current == null ? candidate : current;
        }

        private static void CaptureSceneDefaultsIf(bool shouldCapture)
        {
            if (!shouldCapture || _lastAutoCaptureFrame == Time.frameCount)
                return;

            _lastAutoCaptureFrame = Time.frameCount;
            CaptureSceneDefaults(overwriteExisting: false);
        }

        private static void CaptureCreateLobbyDefaults(Transform root, bool overwriteExisting)
        {
            if (root == null)
                return;

            Image background = root.Find(SleddingUiPaths.LobbySettingsPanelBackground)?.GetComponent<Image>();
            Button button = root.Find(SleddingUiPaths.LobbyConfirmHostButton)?.GetComponent<Button>()
                ?? root.Find(SleddingUiPaths.LobbyCloseButton)?.GetComponent<Button>();
            TMP_InputField input = root.Find(SleddingUiPaths.CustomLobbyNameInput)?.GetComponent<TMP_InputField>()
                ?? root.Find(SleddingUiPaths.LobbyPasswordInput)?.GetComponent<TMP_InputField>();
            Toggle toggle = root.Find(SleddingUiPaths.LobbyPublicPrivateToggle)?.GetComponent<Toggle>()
                ?? root.Find(SleddingUiPaths.LobbyPasswordToggle)?.GetComponent<Toggle>();
            MySliderUI slider = root.Find(SleddingUiPaths.LobbyPlayerCountSlider)?.GetComponent<MySliderUI>();
            Scrollbar scrollbar = root.GetComponentInChildren<Scrollbar>(true);
            TMP_Text label = root.Find(SleddingUiPaths.LobbySettingsText)?.GetComponent<TMP_Text>()
                ?? root.Find(SleddingUiPaths.LobbySettingsTitleText)?.GetComponent<TMP_Text>()
                ?? button?.GetComponentInChildren<TMP_Text>(true)
                ?? toggle?.GetComponentInChildren<TMP_Text>(true);
            TMP_Text inputText = input != null ? input.textComponent : null;
            TMP_Text inputPlaceholder = input != null && input.placeholder != null ? input.placeholder.GetComponent<TMP_Text>() : null;

            SetDefaults(new NativeUiTemplateSet
            {
                Button = button,
                Label = label,
                ButtonLabel = button != null ? button.GetComponentInChildren<TMP_Text>(true) : null,
                ToggleLabel = toggle != null ? toggle.GetComponentInChildren<TMP_Text>(true) : null,
                SliderLabel = slider != null ? slider.GetComponentInChildren<TMP_Text>(true) : null,
                InputText = inputText,
                InputPlaceholder = inputPlaceholder,
                InputField = input,
                Toggle = toggle,
                Slider = slider,
                Scrollbar = scrollbar,
                Background = background,
                Shadow = background != null ? background.GetComponent<Shadow>() : null,
                Panel = background != null ? background.gameObject : null
            }, overwriteExisting);
        }

        private static Button FindButtonTemplate(Transform root)
        {
            if (root == null)
                return null;

            Button fallback = null;
            foreach (Button button in root.GetComponentsInChildren<Button>(true))
            {
                if (button == null || button.GetComponent<Image>() == null)
                    continue;

                string name = button.name.ToLowerInvariant();
                if (name.Contains("host") || name.Contains("join") || name.Contains("settings") || name.Contains("create"))
                    return button;

                fallback ??= button;
            }

            return fallback;
        }

        private static Toggle FindToggleTemplate(Transform root)
        {
            if (root == null)
                return null;

            Toggle fallback = null;
            foreach (Toggle toggle in root.GetComponentsInChildren<Toggle>(true))
            {
                if (toggle == null)
                    continue;

                if (toggle.GetComponent<MyToggleUI>() != null &&
                    FindChildImage(toggle.transform, "Background") != null &&
                    FindChildImage(toggle.transform, "Checkmark") != null)
                {
                    return toggle;
                }

                fallback ??= toggle;
            }

            return fallback;
        }

        private static T FindNamedComponent<T>(params string[] names) where T : Component
        {
            foreach (string name in names)
            {
                GameObject gameObject = FindNamedObject(name);
                T component = gameObject != null ? gameObject.GetComponent<T>() : null;
                if (component != null)
                    return component;
            }

            return null;
        }

        private static GameObject FindNamedObject(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            GameObject activeObject = GameObject.Find(name);
            if (activeObject != null)
                return activeObject;

            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name == name)
                    return gameObject;
            }

            return null;
        }

        private static GameObject FindPanelTemplate(Transform root)
        {
            Image background = FindBackgroundImage(root);
            return background != null ? background.gameObject : null;
        }

        private static Image FindLobbyPanelBackground()
        {
            Transform lobbyBackground = FindNamedObject(SleddingUiPaths.CreateLobbyRoot)?.transform.Find(SleddingUiPaths.LobbySettingsPanelBackground);
            Image image = lobbyBackground != null ? lobbyBackground.GetComponent<Image>() : null;
            if (image != null)
                return image;

            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject == null || gameObject.name != SleddingUiPaths.BackgroundImageName)
                    continue;

                image = gameObject.GetComponent<Image>();
                if (image != null && HasAncestor(gameObject.transform, SleddingUiPaths.LobbySettingsAncestorName))
                    return image;
            }

            return null;
        }

        private static bool HasAncestor(Transform transform, string nameContains)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (current.name.Contains(nameContains))
                    return true;
            }

            return false;
        }

        private static Image FindBackgroundImage(Transform root)
        {
            if (root == null)
                return null;

            Image fallback = null;
            foreach (Image image in root.GetComponentsInChildren<Image>(true))
            {
                string imageName = image.name.ToLowerInvariant();
                string spriteName = image.sprite != null ? image.sprite.name.ToLowerInvariant() : string.Empty;
                if (IsControlImage(image) ||
                    imageName.Contains("fade") ||
                    imageName.Contains("icon") ||
                    spriteName.Contains("icon"))
                {
                    continue;
                }

                if (imageName.Contains("panel") || imageName.Contains("background"))
                    return image;

                if (fallback == null)
                    fallback = image;
            }

            return fallback ?? root.GetComponentInChildren<Image>(true);
        }

        private static GameObject FindOrCreateChild(Transform parent, string name)
        {
            if (parent == null)
                return null;

            Image childImage = FindChildImage(parent, name);
            if (childImage != null)
                return childImage.gameObject;

            Transform directChild = parent.Find(name);
            if (directChild != null)
                return directChild.gameObject;

            return Create(name, parent).GameObject;
        }

        private static Image FindChildImage(Transform root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
                return null;

            foreach (Image image in root.GetComponentsInChildren<Image>(true))
            {
                if (image != null && image.name == name)
                    return image;
            }

            return null;
        }

        private static void NormalizeClonedUiRoot(GameObject gameObject)
        {
            if (gameObject == null)
                return;

            foreach (RectTransform rect in gameObject.GetComponentsInChildren<RectTransform>(true))
            {
                if (rect != null)
                    rect.localScale = Vector3.one;
            }
        }

        internal static RectTransform Stretch(GameObject gameObject)
        {
            RectTransform rect = EnsureRectTransform(gameObject);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static bool IsControlImage(Image image)
        {
            if (image == null)
                return false;

            for (Transform current = image.transform; current != null; current = current.parent)
            {
                if (current.GetComponent<Button>() != null ||
                    current.GetComponent<Toggle>() != null ||
                    current.GetComponent<Slider>() != null ||
                    current.GetComponent<Scrollbar>() != null ||
                    current.GetComponent<TMP_InputField>() != null)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
