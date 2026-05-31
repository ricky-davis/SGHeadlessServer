using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace LobbyKit
{
    public class UiElement
    {
        public UiElement(GameObject gameObject)
        {
            GameObject = gameObject;
        }

        public GameObject GameObject { get; }
        public Transform Transform => GameObject != null ? GameObject.transform : null;
        public RectTransform RectTransform => GameObject != null ? GameObject.GetComponent<RectTransform>() : null;
        public bool Exists => GameObject != null;

        public T Get<T>() where T : Component
        {
            return GameObject != null ? GameObject.GetComponent<T>() : null;
        }

        public T GetInChildren<T>(bool includeInactive = true) where T : Component
        {
            return GameObject != null ? GameObject.GetComponentInChildren<T>(includeInactive) : null;
        }

        public UiElement Show()
        {
            return SetActive(true);
        }

        public UiElement Hide()
        {
            return SetActive(false);
        }

        public UiElement SetActive(bool active)
        {
            if (GameObject != null)
                GameObject.SetActive(active);
            return this;
        }

        public UiElement Reparent(Transform parent, bool worldPositionStays = false)
        {
            if (Transform != null && parent != null)
                Transform.SetParent(parent, worldPositionStays);
            return this;
        }

        public UiElement SiblingIndex(int index)
        {
            if (Transform != null)
                Transform.SetSiblingIndex(index);
            return this;
        }

        public UiElement LastSibling()
        {
            if (Transform != null)
                Transform.SetAsLastSibling();
            return this;
        }

        public UiElement FirstSibling()
        {
            if (Transform != null)
                Transform.SetAsFirstSibling();
            return this;
        }

        public UiElement Move(Vector2 anchoredPosition)
        {
            if (RectTransform != null)
                RectTransform.anchoredPosition = anchoredPosition;
            return this;
        }

        public UiElement Resize(Vector2 sizeDelta)
        {
            if (RectTransform != null)
                RectTransform.sizeDelta = sizeDelta;
            return this;
        }

        public UiElement Scale(float scale)
        {
            return Scale(new Vector3(scale, scale, 1f));
        }

        public UiElement Scale(Vector3 scale)
        {
            if (RectTransform != null)
                RectTransform.localScale = scale;
            else if (Transform != null)
                Transform.localScale = scale;
            return this;
        }

        public UiElement Anchor(Vector2 anchorMin, Vector2 anchorMax)
        {
            if (RectTransform != null)
            {
                RectTransform.anchorMin = anchorMin;
                RectTransform.anchorMax = anchorMax;
            }
            return this;
        }

        public UiElement Pivot(Vector2 pivot)
        {
            if (RectTransform != null)
                RectTransform.pivot = pivot;
            return this;
        }

        public UiElement CenterAnchors()
        {
            Vector2 center = new Vector2(0.5f, 0.5f);
            return Anchor(center, center).Pivot(center);
        }

        public UiElement CanvasGroup(float alpha = 1f, bool interactable = true, bool blocksRaycasts = true)
        {
            NativeUiBackend.SetCanvasGroups(Transform, alpha, interactable, blocksRaycasts);
            return this;
        }

        public UiElement Layout(
            float? preferredWidth = null,
            float? preferredHeight = null,
            float? flexibleWidth = null,
            float? flexibleHeight = null,
            float? minWidth = null,
            float? minHeight = null)
        {
            NativeUiBackend.SetLayout(GameObject, preferredWidth, preferredHeight, flexibleWidth, flexibleHeight, minWidth, minHeight);
            return this;
        }

        public UiElement Destroy()
        {
            if (GameObject != null)
                UnityObject.Destroy(GameObject);
            return this;
        }

        public UiElement DestroyImmediate()
        {
            if (GameObject != null)
                UnityObject.DestroyImmediate(GameObject);
            return this;
        }
    }
}
