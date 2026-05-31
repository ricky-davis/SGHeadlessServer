using Il2Cpp;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace LobbyKit
{
    internal static class NativeUiFactory
    {
        public static UiElement Object(string name, Transform parent = null)
        {
            return NativeUiBackend.Create(name, parent);
        }

        public static TMP_Text Label(Transform parent, string text, string name = "Label", TMP_Text template = null)
        {
            return NativeUiBackend.CreatePlainLabel(parent, text, name, template);
        }

        public static TMP_InputField Input(
            Transform parent,
            string text = "",
            string placeholder = "",
            string name = "Input Field",
            UnityAction<string> onValueChanged = null)
        {
            TMP_InputField input = NativeUiBackend.CloneInputField(NativeUiTemplates.Current.InputField, parent, name, text, placeholder);
            if (input != null && onValueChanged != null)
                input.onValueChanged.AddListener(onValueChanged);
            return input;
        }

        public static TMP_InputField InputFromTemplate(
            TMP_InputField template,
            Transform parent,
            string text = null,
            string placeholder = null,
            string name = "Input Field",
            int? siblingIndex = null)
        {
            return NativeUiBackend.CloneInputField(template, parent, name, text, placeholder, siblingIndex);
        }

        public static Toggle Toggle(
            Transform parent,
            string label,
            bool isOn,
            UnityAction<bool> onValueChanged = null,
            string name = "Toggle")
        {
            return NativeUiBackend.CloneToggle(NativeUiTemplates.Current.Toggle, parent, name, label, isOn, onValueChanged);
        }

        public static Toggle ToggleFromTemplate(
            Toggle template,
            Transform parent,
            string label,
            bool isOn,
            UnityAction<bool> onValueChanged = null,
            string name = "Toggle",
            int? siblingIndex = null)
        {
            return NativeUiBackend.CloneToggle(template, parent, name, label, isOn, onValueChanged, siblingIndex);
        }

        public static MySliderUI Slider(
            Transform parent,
            string name = "Slider",
            float? minValue = null,
            float? maxValue = null,
            float? value = null,
            UnityAction<float> onValueChanged = null)
        {
            return NativeUiBackend.CreateSlider(parent, name, NativeUiTemplates.Current.Slider, minValue, maxValue, value, onValueChanged);
        }

        public static UiElement Panel(Transform parent, string name = "Panel")
        {
            return NativeUiBackend.CreatePanel(parent, name);
        }

        public static Image Backdrop(Transform parent, Image template = null)
        {
            return NativeUiBackend.CreateScreenBackdrop(parent, template: template);
        }

        public static Image Backdrop(Transform parent, Color color)
        {
            return NativeUiBackend.CreateScreenBackdrop(parent, color: color);
        }

        public static ScrollViewport ScrollView(Transform parent, string name = "Scroll Viewport")
        {
            return NativeUiBackend.CreateScrollViewport(parent, name);
        }
    }
}
