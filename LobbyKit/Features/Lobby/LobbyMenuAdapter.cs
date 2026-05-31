using Il2Cpp;
using Il2Cpp_Scripts.Managers;
using Il2Cpp_Scripts.UI.Pre_Game;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LobbyKit.Features.Lobby
{
    internal sealed class LobbyMenuAdapter
    {
        public Transform FindCreateLobbyRoot(Transform mainMenuRoot)
        {
            for (Transform current = mainMenuRoot; current != null; current = current.parent)
            {
                if (current.name == SleddingUiPaths.CreateLobbyRoot)
                    return current;
            }

            Transform root = mainMenuRoot?.Find(SleddingUiPaths.CreateLobbyRoot);
            if (root != null)
                return root;

            return UiReferenceController.Instance != null
                ? UiReferenceController.Instance.createLobby.panel?.transform
                : null;
        }

        public UICreateLobby ActivateEmbeddedCreateLobby(Transform createLobbyRoot, bool showPanel)
        {
            if (createLobbyRoot == null)
                return null;

            SleddingUiAdapter.Assume(createLobbyRoot).Show();
            UiLayout.SetChildrenActive(createLobbyRoot, true, skipNameContains: SleddingUiPaths.BackgroundFadeNameContains);
            for (int i = 0; i < createLobbyRoot.childCount; i++)
            {
                Transform child = createLobbyRoot.GetChild(i);
                if (child == null || child.name.Contains(SleddingUiPaths.BackgroundFadeNameContains))
                    continue;

                if (child.name.Contains(SleddingUiPaths.PanelsNameContains))
                    UiLayout.SetChildrenActive(child, true, skipNameContains: SleddingUiPaths.EditorNameContains);
            }

            UICreateLobby createLobby = createLobbyRoot.GetComponent<UICreateLobby>()
                ?? createLobbyRoot.GetComponentInChildren<UICreateLobby>(true);
            if (createLobby != null)
            {
                UiLayout.ActivatePathToRoot(createLobby.transform, createLobbyRoot);

                if (showPanel)
                    createLobby.ShowPanel();
            }

            UiLayout.SetCanvasGroups(createLobbyRoot, alpha: 1f, interactable: true, blocksRaycasts: true);

            return createLobby;
        }

        public void HideCreateLobbyCloseButton(UICreateLobby createLobby)
        {
            Transform createLobbyRoot = FindCreateLobbyRoot(createLobby?.transform);
            Transform searchRoot = createLobbyRoot ?? createLobby?.transform;
            if (searchRoot == null)
                return;

            HideButton(searchRoot.Find(SleddingUiPaths.LobbyCloseButton)?.GetComponent<Button>());
            HideButton(searchRoot.Find(SleddingUiPaths.LobbyRootCloseButton)?.GetComponent<Button>());

            foreach (Button button in searchRoot.GetComponentsInChildren<Button>(true))
            {
                if (button == null)
                    continue;

                TMP_Text label = button.GetComponentInChildren<TMP_Text>(true);
                string labelText = label != null ? label.text : string.Empty;
                if (button.name.Contains("Close Menu") || button.name.Contains("Close") || labelText == "X" || labelText == "×")
                    HideButton(button);
            }
        }

        private static void HideButton(Button button)
        {
            if (button == null)
                return;

            button.interactable = false;
            button.gameObject.SetActive(false);
        }
    }
}
