using System;
using System.Collections;
using Il2Cpp;
using Il2CppTMPro;
using Il2Cpp_Scripts.UI.Pre_Game;
using MelonLoader;
using LobbyKit;
using UnityEngine;
using UnityEngine.UI;

namespace LobbyKit.Features.Lobby
{
    internal sealed class SearchLobbiesUiController
    {
        public static SearchLobbiesUiController Instance { get; } = new SearchLobbiesUiController();

        private bool _watcherRunning;
        private bool _panelLoopRunning;
        private bool _suppressCapture;
        private bool _applyPreferencesOnNextSearch;

        private SearchLobbiesUiController()
        {
        }

        public void ConfigureMainMenuOnEnable(UIMainMenu mainMenu)
        {
            if (_watcherRunning)
                return;

            _watcherRunning = true;
            MelonCoroutines.Start(WatchLobbyExplorerPanel());
        }

        public void MarkLobbyExplorerOpening()
        {
            _applyPreferencesOnNextSearch = true;
        }

        public void ApplyPreferencesBeforeInitialSearch(UILobbyExplorer lobbyExplorer)
        {
            if (!_applyPreferencesOnNextSearch)
                return;

            ApplyLobbyExplorerPreferences(lobbyExplorer != null ? lobbyExplorer.transform : null);
            _applyPreferencesOnNextSearch = false;
        }

        private IEnumerator WatchLobbyExplorerPanel()
        {
            bool wasActive = false;

            while (true)
            {
                yield return null;

                Transform root = NativeUiBackend.Find("UI_Lobbies").Transform;
                bool isOpen = IsOpen(root);
                if (isOpen && !wasActive && !_panelLoopRunning)
                    MelonCoroutines.Start(HandleLobbyExplorerOpened(root));

                wasActive = isOpen;
            }
        }

        private IEnumerator HandleLobbyExplorerOpened(Transform root)
        {
            _panelLoopRunning = true;
            _suppressCapture = true;

            for (int frame = 0; frame < 8; frame++)
            {
                yield return null;

                if (!IsOpen(root))
                {
                    _suppressCapture = false;
                    _panelLoopRunning = false;
                    yield break;
                }

                ApplyLobbyExplorerPreferences(root);
            }

            _suppressCapture = false;

            while (IsOpen(root))
            {
                CaptureLobbyExplorerPreferenceChanges(root);
                RefreshMaxPlayersDisplay(GetControls(root).MaxPlayersFilterSlider);
                yield return null;
            }

            _panelLoopRunning = false;
        }

        private void ApplyLobbyExplorerPreferences(Transform root)
        {
            if (root == null)
                return;

            try
            {
                LobbyExplorerControls controls = GetControls(root);

                ApplyMaxPlayers(controls.MaxPlayersFilterSlider);
                SetToggleWithoutNotify(controls.ShowLockedLobbiesToggle, LobbyKitCore.SearchShowLocked);
                SetToggleWithoutNotify(controls.ShowModdedLobbiesToggle, LobbyKitCore.SearchShowModded);
                SetToggleWithoutNotify(controls.ShowOnlyYourLanguageToggle, LobbyKitCore.SearchShowYourLanguageOnly);
                SetToggleWithoutNotify(controls.OnlyPeacefulModeToggle, LobbyKitCore.SearchOnlyPeacefulLobbies);
                SetToggleWithoutNotify(controls.OnlyNonPeacefulModeToggle, LobbyKitCore.SearchHidePeacefulLobbies);
                ApplyRegionMode(controls, LobbyKitCore.SearchRegionMode);
                HideButton(controls.CreateNewLobbyButton);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyKit] SearchLobbiesUiController failed to apply UI_Lobbies preferences: {ex}");
            }
        }

        private void CaptureLobbyExplorerPreferenceChanges(Transform root)
        {
            if (root == null || _suppressCapture)
                return;

            try
            {
                LobbyExplorerControls controls = GetControls(root);

                if (controls.MaxPlayersFilterSlider?.slider != null)
                {
                    int value = Mathf.RoundToInt(controls.MaxPlayersFilterSlider.slider.value);
                    if (value != LobbyKitCore.SearchMaxPlayers)
                        LobbyKitCore.SetSearchMaxPlayers(value);
                }

                SaveToggleIfChanged(controls.ShowLockedLobbiesToggle, LobbyKitCore.SearchShowLocked, LobbyKitCore.SetSearchShowLocked);
                SaveToggleIfChanged(controls.ShowModdedLobbiesToggle, LobbyKitCore.SearchShowModded, LobbyKitCore.SetSearchShowModded);
                SaveToggleIfChanged(controls.ShowOnlyYourLanguageToggle, LobbyKitCore.SearchShowYourLanguageOnly, LobbyKitCore.SetSearchShowYourLanguageOnly);
                SaveToggleIfChanged(controls.OnlyPeacefulModeToggle, LobbyKitCore.SearchOnlyPeacefulLobbies, LobbyKitCore.SetSearchOnlyPeacefulLobbies);
                SaveToggleIfChanged(controls.OnlyNonPeacefulModeToggle, LobbyKitCore.SearchHidePeacefulLobbies, LobbyKitCore.SetSearchHidePeacefulLobbies);
                SaveRegionModeIfChanged(controls);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyKit] SearchLobbiesUiController failed to capture UI_Lobbies preferences: {ex}");
            }
        }

        private static bool IsOpen(Transform root)
        {
            Transform panel = root != null ? root.Find("Panel") : null;
            return panel != null && panel.gameObject != null && panel.gameObject.activeSelf;
        }

        private static void ApplyMaxPlayers(MySliderUI sliderUi)
        {
            if (sliderUi?.slider == null)
                return;

            Slider slider = sliderUi.slider;
            slider.minValue = 0f;
            slider.maxValue = 64f;
            slider.wholeNumbers = true;
            slider.SetValueWithoutNotify(LobbyKitCore.SearchMaxPlayers);
            sliderUi.UpdateSliderValueDisplay();
            RefreshMaxPlayersDisplay(sliderUi);
        }

        private static void RefreshMaxPlayersDisplay(MySliderUI sliderUi)
        {
            Transform sliderRoot = sliderUi != null ? sliderUi.transform : null;
            if (sliderRoot == null || sliderUi.slider == null)
                return;

            int value = Mathf.RoundToInt(sliderUi.slider.value);
            TMP_Text valueText = FindText(sliderRoot, "(Text) Slider Value Text");
            TMP_Text anyText = FindText(sliderRoot, "(Text) Slider ANY Text");

            if (valueText != null)
            {
                valueText.text = $">={value}";
                valueText.gameObject.SetActive(value != 0);
                valueText.ForceMeshUpdate();
            }

            if (anyText != null)
                anyText.gameObject.SetActive(value == 0);
        }

        private static void SetToggleWithoutNotify(Toggle toggle, bool value)
        {
            if (toggle != null)
                toggle.SetIsOnWithoutNotify(value);
        }

        private static void SaveToggleIfChanged(Toggle toggle, bool preference, Action<bool> setPreference)
        {
            if (toggle != null && toggle.isOn != preference)
                setPreference(toggle.isOn);
        }

        private static void ApplyRegionMode(LobbyExplorerControls controls, int regionMode)
        {
            Toggle[] toggles =
            {
                controls.RegionNearToggle,
                controls.RegionNearbyToggle,
                controls.RegionFarToggle,
                controls.RegionWorldwideToggle
            };

            int selectedIndex = Math.Clamp(regionMode, 0, toggles.Length - 1);
            for (int i = 0; i < toggles.Length; i++)
                SetToggleWithoutNotify(toggles[i], i == selectedIndex);
        }

        private static void SaveRegionModeIfChanged(LobbyExplorerControls controls)
        {
            int? selectedRegionMode = GetSelectedRegionMode(controls);
            if (selectedRegionMode.HasValue && selectedRegionMode.Value != LobbyKitCore.SearchRegionMode)
                LobbyKitCore.SetSearchRegionMode(selectedRegionMode.Value);
        }

        private static int? GetSelectedRegionMode(LobbyExplorerControls controls)
        {
            if (controls.RegionNearToggle != null && controls.RegionNearToggle.isOn)
                return 0;
            if (controls.RegionNearbyToggle != null && controls.RegionNearbyToggle.isOn)
                return 1;
            if (controls.RegionFarToggle != null && controls.RegionFarToggle.isOn)
                return 2;
            if (controls.RegionWorldwideToggle != null && controls.RegionWorldwideToggle.isOn)
                return 3;

            return null;
        }

        private static LobbyExplorerControls GetControls(Transform root)
        {
            return new LobbyExplorerControls
            {
                MaxPlayersFilterSlider = FindSlider(root, "Panel/layout group/Filter Menu/(Slider) max players filter"),
                ShowLockedLobbiesToggle = FindToggle(root, "Panel/layout group/Filter Menu/Lobby Filters/(Toggle) show locked lobbies", "show locked lobbies"),
                ShowModdedLobbiesToggle = FindToggle(root, "Panel/layout group/Filter Menu/Lobby Filters/(Toggle) show modded lobbies", "show modded lobbies"),
                OnlyPeacefulModeToggle = FindToggle(root, "Panel/layout group/Filter Menu/peaceful/(Toggle) only peaceful lobbies", "only peaceful lobbies"),
                OnlyNonPeacefulModeToggle = FindToggle(root, "Panel/layout group/Filter Menu/peaceful/(Toggle) no peaceful lobbies", "no peaceful lobbies"),
                ShowOnlyYourLanguageToggle = FindToggle(root, "Panel/layout group/Filter Menu/Lobby Filters/(Toggle) show your language only", "show your language only"),
                RegionNearToggle = FindToggle(root, "Panel/layout group/Filter Menu/region/(Toggle) Settings Toggle (1)", "near "),
                RegionNearbyToggle = FindToggle(root, "Panel/layout group/Filter Menu/region/(Toggle) Settings Toggle (2)", "nearby regions"),
                RegionFarToggle = FindToggle(root, "Panel/layout group/Filter Menu/region/(Toggle) Settings Toggle (3)", "far "),
                RegionWorldwideToggle = FindToggle(root, "Panel/layout group/Filter Menu/region/(Toggle) Settings Toggle (4)", "worldwide"),
                CreateNewLobbyButton = FindButton(root, "Panel/layout group/List/(Button) Create New Lobby")
            };
        }

        private static MySliderUI FindSlider(Transform root, string path)
        {
            Transform transform = root?.Find(path);
            return transform != null
                ? transform.GetComponent<MySliderUI>() ?? transform.GetComponentInChildren<MySliderUI>(true)
                : root?.GetComponentInChildren<MySliderUI>(true);
        }

        private static Toggle FindToggle(Transform root, string path, string label)
        {
            Transform transform = root?.Find(path);
            Toggle toggle = transform != null ? transform.GetComponent<Toggle>() : null;
            if (toggle != null)
                return toggle;

            return FindToggleByLabel(root, label);
        }

        private static Button FindButton(Transform root, string path)
        {
            Transform transform = root?.Find(path);
            return transform != null
                ? transform.GetComponent<Button>() ?? transform.GetComponentInChildren<Button>(true)
                : null;
        }

        private static void HideButton(Button button)
        {
            if (button == null)
                return;

            button.interactable = false;
            button.gameObject.SetActive(false);
        }

        private static Toggle FindToggleByLabel(Transform root, string label)
        {
            if (root == null || string.IsNullOrWhiteSpace(label))
                return null;

            string normalizedLabel = label.Trim().ToLowerInvariant();
            foreach (Toggle toggle in root.GetComponentsInChildren<Toggle>(true))
            {
                TMP_Text text = toggle.GetComponentInChildren<TMP_Text>(true);
                if (text?.text != null && text.text.Trim().ToLowerInvariant().Contains(normalizedLabel))
                    return toggle;
            }

            return null;
        }

        private static TMP_Text FindText(Transform root, string childName)
        {
            Transform child = root != null ? root.Find(childName) : null;
            return child != null ? child.GetComponent<TMP_Text>() : null;
        }

        private sealed class LobbyExplorerControls
        {
            public MySliderUI MaxPlayersFilterSlider { get; set; }
            public Toggle ShowLockedLobbiesToggle { get; set; }
            public Toggle ShowModdedLobbiesToggle { get; set; }
            public Toggle OnlyPeacefulModeToggle { get; set; }
            public Toggle OnlyNonPeacefulModeToggle { get; set; }
            public Toggle ShowOnlyYourLanguageToggle { get; set; }
            public Toggle RegionNearToggle { get; set; }
            public Toggle RegionNearbyToggle { get; set; }
            public Toggle RegionFarToggle { get; set; }
            public Toggle RegionWorldwideToggle { get; set; }
            public Button CreateNewLobbyButton { get; set; }
        }
    }
}
