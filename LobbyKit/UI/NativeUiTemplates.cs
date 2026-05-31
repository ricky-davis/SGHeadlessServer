using UnityEngine;

namespace LobbyKit
{
    internal static class NativeUiTemplates
    {
        public static NativeUiTemplateSet Current => NativeUiBackend.Defaults;

        public static void CaptureScene(bool overwriteExisting = false)
        {
            NativeUiBackend.CaptureSceneDefaults(overwriteExisting);
        }

        public static void CaptureFrom(Transform root, bool overwriteExisting = false)
        {
            NativeUiBackend.CaptureDefaultsFrom(root, overwriteExisting);
        }

    }
}
