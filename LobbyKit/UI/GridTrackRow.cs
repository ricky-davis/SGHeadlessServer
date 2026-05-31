using UnityEngine;

namespace LobbyKit
{
    internal sealed class GridTrackRow
    {
        public GridTrackRow(GameObject gameObject, RectTransform[] tracks)
        {
            GameObject = gameObject;
            Tracks = tracks;
        }

        public GameObject GameObject { get; }
        public RectTransform[] Tracks { get; }
    }
}
