using UnityEngine;

namespace LobbyKit
{
    internal static class SleddingUiAdapter
    {
        public static UiElement Assume(GameObject gameObject)
        {
            return NativeUiBackend.Assume(gameObject);
        }

        public static UiElement Assume(Component component)
        {
            return NativeUiBackend.Assume(component);
        }

        public static UiElement Assume(Transform transform)
        {
            return NativeUiBackend.Assume(transform);
        }

        public static UiElement Find(string sceneObjectName)
        {
            return NativeUiBackend.Find(sceneObjectName);
        }

        public static void CaptureSceneTemplates(bool overwriteExisting = false)
        {
            NativeUiTemplates.CaptureScene(overwriteExisting);
        }

        public static void CaptureTemplatesFrom(Transform root, bool overwriteExisting = false)
        {
            NativeUiTemplates.CaptureFrom(root, overwriteExisting);
        }

    }
}
