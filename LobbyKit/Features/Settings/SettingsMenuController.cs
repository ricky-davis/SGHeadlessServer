using System;
using HarmonyLib;
using Il2Cpp;
using Il2Cpp_Scripts.Systems.Chat;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace LobbyKit.Features.Settings
{
    internal delegate bool SettingsTemplateValidator(out string missingTemplates);

    internal sealed class SettingsMenuController
    {
        private GameObject _root;
        private GameObject _firstSelectable;
        private GameObject _confirmationRoot;
        private SettingsDraft _draft;
        private SettingsDraft _cleanDraft;
        private Action<bool> _dirtyStateChanged;

        public static SettingsMenuController Instance { get; } = new SettingsMenuController();

        private SettingsMenuController()
        {
        }

        public bool IsOpen => _root != null;

        public bool HasUnappliedChanges => _draft != null && !_draft.ValueEquals(_cleanDraft);

        public void Open(
            Func<Transform, SettingsDraft, SettingsMenuHandle> buildMenu,
            SettingsTemplateValidator validateTemplates = null)
        {
            CloseWithoutPrompt();
            SleddingUiAdapter.CaptureSceneTemplates(overwriteExisting: false);
            if (validateTemplates != null && !validateTemplates(out string missingTemplates))
            {
                Debug.LogWarning($"[LobbyKit] Could not open settings UI: missing native UI templates: {missingTemplates}.");
                return;
            }

            ForceCloseChat();
            UiReferenceController.Instance?.CloseAllOpenMenus(false);

            Transform parent = FindNativeUiParent();
            if (parent == null)
            {
                Debug.LogWarning("[LobbyKit] Could not open settings UI: no native UI parent is available.");
                return;
            }

            _draft = SettingsDraft.FromCurrent();
            _cleanDraft = _draft.Clone();

            SettingsMenuHandle handle = buildMenu?.Invoke(parent, _draft);
            _root = handle?.Root;
            _firstSelectable = handle?.FirstSelectable;

            if (_root == null)
            {
                Debug.LogWarning("[LobbyKit] Could not open settings UI: settings view was not created.");
                _draft = null;
                _cleanDraft = null;
                return;
            }

            NotifyDirtyStateChanged();
            OpenAsGameMenu();
        }

        public void RequestClose()
        {
            if (_root == null)
                return;

            if (HasUnappliedChanges)
            {
                ShowCloseConfirmation();
                return;
            }

            CloseWithoutPrompt();
        }

        public void Apply()
        {
            if (_draft == null)
                return;

            _draft.Apply();
            _cleanDraft = _draft.Clone();
            SettingsMenuView.RefreshToggleState(_root, "Enable Guest !Bang Commands", _draft.EnableGuestBangCommands);
            NotifyDirtyStateChanged();
        }

        public void ApplyAndClose()
        {
            Apply();
            CloseWithoutPrompt();
        }

        public void DiscardAndClose()
        {
            CloseWithoutPrompt();
        }

        public void NotifyDraftChanged()
        {
            NotifyDirtyStateChanged();
        }

        public void AddDirtyStateListener(Action<bool> listener)
        {
            _dirtyStateChanged += listener;
            listener?.Invoke(HasUnappliedChanges);
        }

        private void NotifyDirtyStateChanged()
        {
            _dirtyStateChanged?.Invoke(HasUnappliedChanges);
        }

        private void CloseWithoutPrompt()
        {
            if (_root == null)
            {
                _confirmationRoot = null;
                _firstSelectable = null;
                _draft = null;
                _cleanDraft = null;
                _dirtyStateChanged = null;
                return;
            }

            UiReferenceController.Instance?.CloseAllOpenMenus(false);
            _confirmationRoot = null;
            Object.Destroy(_root);
            _root = null;
            _firstSelectable = null;
            _draft = null;
            _cleanDraft = null;
            _dirtyStateChanged = null;

            ForceCloseChat();
            RestoreClosedCursorState();
        }

        public void HandleEscape()
        {
            if (_root == null || !UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                return;

            RequestClose();
        }

        private void ShowCloseConfirmation()
        {
            if (_root == null)
                return;

            if (_confirmationRoot != null)
            {
                _confirmationRoot.SetActive(true);
                return;
            }

            _confirmationRoot = NativeUiFactory.Object("Unapplied Changes Confirmation", _root.transform).GameObject;
            UiLayout.Stretch(_confirmationRoot);

            Image backdrop = NativeUiFactory.Backdrop(_confirmationRoot.transform, new Color(0f, 0f, 0f, 0.35f));

            UiElement panel = NativeUiFactory.Panel(backdrop != null ? backdrop.transform : _confirmationRoot.transform, "Confirmation Panel");
            panel.CenterAnchors().Resize(new Vector2(560f, 190f)).Move(Vector2.zero);
            UiLayout.SetVertical(panel.GameObject, new RectOffset(26, 26, 22, 22), spacing: 14f);

            TMP_Text title = NativeUiFactory.Label(panel.Transform, "Unapplied Changes", "Confirmation Title", NativeUiTemplates.Current.HeaderLabel ?? NativeUiTemplates.Current.Label);
            UiStyles.SetTextMetrics(title, 20f, TextAlignmentOptions.Center);
            title.color = Color.white;
            title.fontStyle = FontStyles.Bold;
            UiLayout.SetFixedSize(title.gameObject, flexibleWidth: 1f, preferredHeight: 34f);

            TMP_Text body = NativeUiFactory.Label(panel.Transform, "Apply your changes before closing?", "Confirmation Body", NativeUiTemplates.Current.Label);
            UiStyles.SetTextMetrics(body, 15f, TextAlignmentOptions.Center, autoSize: true, minFontSize: 12f);
            body.color = Color.white;
            UiLayout.SetFixedSize(body.gameObject, flexibleWidth: 1f, preferredHeight: 38f);

            GameObject row = NativeUiBuilder.HorizontalRow(panel.Transform, "Confirmation Actions", height: 36f, spacing: 10f).GameObject;
            HorizontalLayoutGroup rowLayout = row.GetComponent<HorizontalLayoutGroup>();
            if (rowLayout != null)
            {
                rowLayout.childControlHeight = false;
                rowLayout.childForceExpandHeight = false;
            }
            UiLayout.SetFixedSize(row, flexibleWidth: 1f, preferredHeight: 36f);

            Button applyClose = CreateNativeButton(row.transform, "Apply & Close", (UnityEngine.Events.UnityAction)ApplyAndClose, "Apply & Close");
            UiLayout.SetFixedSize(applyClose.gameObject, flexibleWidth: 1f, preferredHeight: 36f);

            Button discardClose = CreateNativeButton(row.transform, "Discard & Close", (UnityEngine.Events.UnityAction)DiscardAndClose, "Discard & Close");
            UiLayout.SetFixedSize(discardClose.gameObject, flexibleWidth: 1f, preferredHeight: 36f);

            Button cancel = CreateNativeButton(row.transform, "Cancel", (UnityEngine.Events.UnityAction)(() => _confirmationRoot?.SetActive(false)), "Cancel");
            UiLayout.SetFixedSize(cancel.gameObject, flexibleWidth: 1f, preferredHeight: 36f);
        }

        private static Button CreateNativeButton(Transform parent, string label, UnityEngine.Events.UnityAction onClick, string name)
        {
            return SettingsMenuView.CreateSettingsButton(parent, label, onClick, name, 14f, new Vector2(150f, 36f));
        }

        private static Transform FindNativeUiParent()
        {
            UiReferenceController uiController = UiReferenceController.Instance;
            Transform parent =
                uiController?.settingsMenu.panel?.transform.parent ??
                uiController?.pauseMenu.panel?.transform.parent ??
                uiController?.playerActiveCanvas?.transform ??
                uiController?.mainMenu.panel?.transform.parent;

            if (parent != null)
                return parent;

            Canvas canvas = Object.FindObjectOfType<Canvas>();
            return canvas != null ? canvas.transform : null;
        }

        private static void RestoreClosedCursorState()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private static void ForceCloseChat()
        {
            ChatManager chat = ChatManager.Instance;
            if (chat == null)
                return;

            try
            {
                chat.chatBox?.Disable(false);
                Traverse.Create(chat).Property("IsChatOpen").SetValue(false);
                Traverse.Create(chat).Field("<IsChatOpen>k__BackingField").SetValue(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyKit] Failed to force close chat: {ex.Message}");
            }
        }

        private void OpenAsGameMenu()
        {
            UiReferenceController uiController = UiReferenceController.Instance;
            if (uiController == null || _root == null)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                return;
            }

            UiReferenceController.UiToggleableMenu menu = new UiReferenceController.UiToggleableMenu
            {
                panel = _root,
                firstSelectable = _firstSelectable
            };

            uiController.OpenMenu(menu);
        }
    }
}
