using UnityEngine;
using UnityEngine.UI;

namespace LobbyKit
{
    internal static class UiLayout
    {
        public static VerticalLayoutGroup SetVertical(
            GameObject gameObject,
            RectOffset padding = null,
            float spacing = 0f,
            bool childControlWidth = true,
            bool childControlHeight = true,
            bool childForceExpandWidth = true,
            bool childForceExpandHeight = false)
        {
            return NativeUiBackend.SetVerticalLayout(
                gameObject,
                padding,
                spacing,
                childControlWidth,
                childControlHeight,
                childForceExpandWidth,
                childForceExpandHeight);
        }

        public static void SetRect(
            Component component,
            Vector2? anchorMin = null,
            Vector2? anchorMax = null,
            Vector2? pivot = null,
            Vector2? anchoredPosition = null,
            Vector2? sizeDelta = null,
            Vector3? scale = null)
        {
            NativeUiBackend.SetRect(
                component,
                anchoredPosition: anchoredPosition,
                sizeDelta: sizeDelta,
                anchorMin: anchorMin,
                anchorMax: anchorMax,
                pivot: pivot,
                scale: scale);
        }

        public static LayoutElement SetLayout(
            GameObject gameObject,
            float? preferredWidth = null,
            float? preferredHeight = null,
            float? flexibleWidth = null,
            float? flexibleHeight = null,
            float? minWidth = null,
            float? minHeight = null)
        {
            return NativeUiBackend.SetLayout(gameObject, preferredWidth, preferredHeight, flexibleWidth, flexibleHeight, minWidth, minHeight);
        }

        public static void SetFixedSize(
            GameObject gameObject,
            float? preferredWidth = null,
            float? preferredHeight = null,
            float? flexibleWidth = null,
            float? flexibleHeight = null)
        {
            NativeUiBackend.SetFixedLayoutSize(
                gameObject,
                preferredWidth: preferredWidth,
                preferredHeight: preferredHeight,
                flexibleWidth: flexibleWidth,
                flexibleHeight: flexibleHeight);
        }

        public static void Stretch(GameObject gameObject)
        {
            NativeUiBackend.Stretch(gameObject);
        }

        public static void LayoutGridTracks(GridTrackRow row, float spacing, float[] trackWidths)
        {
            NativeUiBackend.LayoutGridTracks(row, spacing: spacing, trackWidths: trackWidths);
        }

        public static void StabilizeClonedControl(GameObject gameObject)
        {
            NativeUiBackend.StabilizeClonedControl(gameObject);
        }

        public static void SetCanvasGroups(Transform root, float alpha, bool interactable, bool blocksRaycasts)
        {
            NativeUiBackend.SetCanvasGroups(root, alpha, interactable, blocksRaycasts);
        }

        public static void ActivatePathToRoot(Transform start, Transform root)
        {
            NativeUiBackend.ActivatePathToRoot(start, root);
        }

        public static void SetChildrenActive(Transform parent, bool active, string skipNameContains = null)
        {
            NativeUiBackend.SetChildrenActive(parent, active, skipNameContains);
        }
    }
}
