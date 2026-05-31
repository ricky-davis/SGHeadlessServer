using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LobbyKit
{
    internal static class UiStyles
    {
        public static void ApplyText(TMP_Text target, TMP_Text template = null)
        {
            NativeUiBackend.ApplyTextStyle(target, template);
        }

        public static void SetText(TMP_Text target, string text, bool removeLocalization = true)
        {
            NativeUiBackend.SetText(target, text, removeLocalization);
        }

        public static void SetButtonColors(Button button, Color imageColor, Color shadowColor)
        {
            NativeUiBackend.SetButtonColors(button, imageColor, shadowColor);
        }

        public static Image CopyImage(Image template, GameObject target)
        {
            return NativeUiBackend.CopyImage(template, target);
        }

        public static Shadow CopyShadow(Shadow template, GameObject target)
        {
            return NativeUiBackend.CopyShadow(template, target);
        }

        public static void SetTextMetrics(
            TMP_Text text,
            float fontSize,
            TextAlignmentOptions alignment = TextAlignmentOptions.Left,
            bool autoSize = false,
            float minFontSize = 10f)
        {
            NativeUiBackend.SetTextMetrics(text, fontSize, alignment, autoSize, minFontSize);
        }

        public static void SetInputTextStyle(TMP_InputField input, float fontSize, bool autoSize = false, float minFontSize = 10f)
        {
            NativeUiBackend.SetInputTextStyle(input, fontSize, autoSize: autoSize, minFontSize: minFontSize);
        }
    }
}
