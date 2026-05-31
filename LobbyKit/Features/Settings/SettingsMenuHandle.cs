using UnityEngine;

namespace LobbyKit.Features.Settings
{
    internal sealed class SettingsMenuHandle
    {
        public SettingsMenuHandle(GameObject root, GameObject firstSelectable)
        {
            Root = root;
            FirstSelectable = firstSelectable;
        }

        public GameObject Root { get; }
        public GameObject FirstSelectable { get; }
    }
}

