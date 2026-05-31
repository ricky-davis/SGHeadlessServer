using Il2Cpp_Scripts.UI.Pre_Game;
using UnityEngine;
using UnityEngine.UI;

namespace LobbyKit.Features.Lobby
{
    internal sealed class MainMenuAdapter
    {
        public Transform Configure(UIMainMenu mainMenu)
        {
            if (mainMenu == null || mainMenu.hostButton == null)
                return mainMenu?.transform.Find("Panel") ?? mainMenu?.transform;

            Transform hostRow = mainMenu.hostButton.transform.parent;
            Transform menuLayout = hostRow?.parent;
            if (menuLayout == null)
                return mainMenu.transform;

            Transform oldQuickHostRow = menuLayout.Find("horizontal layout (quick host)");
            if (oldQuickHostRow != null)
                SleddingUiAdapter.Assume(oldQuickHostRow).DestroyImmediate();

            SleddingUiAdapter.Assume(mainMenu.hostButton).Hide();

            Button joinButton = mainMenu.joinButton ?? GameObject.Find(SleddingUiPaths.JoinButtonUpper)?.GetComponent<Button>();
            if (joinButton != null)
            {
                SleddingUiAdapter.Assume(joinButton)
                    .SiblingIndex(0)
                    .Show()
                    .Layout(flexibleWidth: 1f, preferredWidth: -1f);
            }

            Button quitButton = mainMenu.quitButton ?? GameObject.Find(SleddingUiPaths.QuitButton)?.GetComponent<Button>();
            UiStyles.SetButtonColors(quitButton, new Color(0.867f, 0.298f, 0.298f, 1f), new Color(0.298f, 0f, 0f, 1f));

            HorizontalLayoutGroup hostRowLayout = hostRow.GetComponent<HorizontalLayoutGroup>();
            if (hostRowLayout != null)
            {
                hostRowLayout.childControlWidth = true;
                hostRowLayout.childForceExpandWidth = true;
            }

            SleddingUiAdapter.Assume(menuLayout)
                .Scale(new Vector3(0.78f, 0.78f, 1f))
                .Move(new Vector2(295f, 0f));

            return menuLayout.parent ?? mainMenu.transform;
        }
    }
}
