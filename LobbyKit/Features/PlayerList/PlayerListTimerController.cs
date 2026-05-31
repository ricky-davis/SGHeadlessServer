using System;
using System.Collections.Generic;
using Il2Cpp;
using Il2CppEpic.OnlineServices;
using Il2CppPlayEveryWare.EpicOnlineServices.Samples;
using Il2CppTMPro;
using UnityEngine;

namespace LobbyKit.Features.PlayerList
{
    internal sealed class PlayerListTimerController
    {
        private const string TimerTextName = "LobbyKitSessionTimerText";
        private readonly Dictionary<int, PlayerRowTimer> _rowTimers = new Dictionary<int, PlayerRowTimer>();
        private float _nextDisplayUpdateTime;

        public static PlayerListTimerController Instance { get; } = new PlayerListTimerController();

        private PlayerListTimerController()
        {
        }

        public void BindRow(PlayersListNameItem row, LobbyMember lobbyMember, ProductUserId lobbyLeaderId)
        {
            if (row == null)
            {
                Debug.LogWarning("[LobbyKit] Player row timer skipped: row instance was null.");
                return;
            }

            if (!TryReadLobbyMember(lobbyMember, out string productId, out string displayName))
                return;

            TMP_Text nameText = FindNameText(row, displayName, productId);
            if (nameText == null)
            {
                Debug.LogWarning($"[LobbyKit] Player row timer skipped for {productId}: no TMP name text found.");
                return;
            }

            ConfigureRichText(nameText);

            int rowId = row.GetInstanceID();
            string baseName = !string.IsNullOrWhiteSpace(displayName)
                ? displayName
                : StripSessionTimer(nameText.text);

            if (string.IsNullOrWhiteSpace(baseName))
                baseName = productId;

            if (_rowTimers.TryGetValue(rowId, out PlayerRowTimer existingTimer))
            {
                existingTimer.IsActive = false;
                DestroyTimerText(existingTimer);
            }

            DestroyOrphanTimerTexts(row);

            nameText.text = StripSessionTimer(baseName);
            TMP_Text timerText = CreateTimerText(nameText);
            if (timerText == null)
            {
                Debug.LogWarning($"[LobbyKit] Player row timer skipped for {productId}: could not create timer text.");
                return;
            }

            bool isLobbyOwner = IsLobbyOwner(productId, lobbyLeaderId);
            var timer = new PlayerRowTimer(row, nameText, timerText, productId, StripSessionTimer(baseName), isLobbyOwner);
            _rowTimers[rowId] = timer;
            Debug.Log($"[LobbyKit] Bound player row session timer: row={rowId}, productId={productId}, baseName={timer.BaseName}, isLobbyOwner={timer.IsLobbyOwner}");
            ApplySessionTimer(timer);
        }

        public void MarkRowDestroyed(PlayersListNameItem row)
        {
            if (row == null)
                return;

            int rowId = row.GetInstanceID();
            if (_rowTimers.TryGetValue(rowId, out PlayerRowTimer timer))
            {
                timer.IsActive = false;
                DestroyTimerText(timer);
            }

            _rowTimers.Remove(rowId);
        }

        public void UpdateVisibleRows()
        {
            if (Time.unscaledTime < _nextDisplayUpdateTime)
                return;

            _nextDisplayUpdateTime = Time.unscaledTime + 1f;
            UpdateBoundRows();
        }

        private void ApplySessionTimer(PlayerRowTimer timer)
        {
            if (timer == null || !timer.IsActive || LobbyKitCore.Instance == null)
                return;

            if (!LobbyKitCore.Instance.TryGetPlayerListTimerDuration(timer.ProductId, timer.IsLobbyOwner, out double sessionSeconds))
            {
                timer.LogFailureOnce("core did not return a session duration");
                return;
            }

            string label = LobbyKitCore.FormatLobbyJoinTime(sessionSeconds);
            if (timer.LastLabel == label)
                return;

            timer.LastLabel = label;
            timer.NameText.text = timer.BaseName;
            timer.TimerText.text = label;

            if (!timer.HasLoggedFirstUpdate)
            {
                timer.HasLoggedFirstUpdate = true;
                Debug.Log($"[LobbyKit] Player row session timer active: {timer.BaseName} ({timer.TimerText.text})");
            }
        }

        private void UpdateBoundRows()
        {
            if (_rowTimers.Count == 0)
                return;

            List<int> staleRowIds = null;
            foreach (KeyValuePair<int, PlayerRowTimer> entry in _rowTimers)
            {
                PlayerRowTimer timer = entry.Value;
                if (timer == null || !timer.IsActive || timer.Row == null || timer.NameText == null || timer.TimerText == null)
                {
                    if (staleRowIds == null)
                        staleRowIds = new List<int>();

                    staleRowIds.Add(entry.Key);
                    continue;
                }

                ApplySessionTimer(timer);
            }

            if (staleRowIds == null)
                return;

            for (int i = 0; i < staleRowIds.Count; i++)
                _rowTimers.Remove(staleRowIds[i]);
        }

        private static bool TryReadLobbyMember(LobbyMember lobbyMember, out string productId, out string displayName)
        {
            productId = null;
            displayName = null;

            if (lobbyMember == null)
            {
                Debug.LogWarning("[LobbyKit] Player row timer skipped: lobby member was null.");
                return false;
            }

            productId = lobbyMember.ProductId?.ToString();
            displayName = lobbyMember.DisplayName;

            if (string.IsNullOrWhiteSpace(productId))
            {
                Debug.LogWarning("[LobbyKit] Player row timer skipped: lobby member had no ProductId.");
                return false;
            }

            return true;
        }

        private static bool IsLobbyOwner(string productId, ProductUserId lobbyLeaderId)
        {
            if (string.IsNullOrWhiteSpace(productId) || lobbyLeaderId == null)
                return false;

            return productId == lobbyLeaderId.ToString();
        }

        private static TMP_Text CreateTimerText(TMP_Text nameText)
        {
            if (nameText == null || nameText.transform == null || nameText.transform.parent == null)
                return null;

            TMP_Text timerText = UnityEngine.Object.Instantiate(nameText, nameText.transform.parent);
            if (timerText == null)
                return null;

            timerText.name = TimerTextName;
            timerText.text = string.Empty;
            timerText.alignment = TextAlignmentOptions.Right;
            timerText.raycastTarget = false;
            timerText.enableWordWrapping = false;
            ConfigureRichText(timerText);

            RectTransform sourceRect = nameText.rectTransform;
            RectTransform timerRect = timerText.rectTransform;
            if (sourceRect != null && timerRect != null)
            {
                timerRect.anchorMin = sourceRect.anchorMin;
                timerRect.anchorMax = sourceRect.anchorMax;
                timerRect.pivot = sourceRect.pivot;
                timerRect.anchoredPosition = sourceRect.anchoredPosition;
                timerRect.sizeDelta = sourceRect.sizeDelta;
                timerRect.offsetMin = sourceRect.offsetMin;
                timerRect.offsetMax = sourceRect.offsetMax;
            }

            timerText.transform.SetAsLastSibling();
            return timerText;
        }

        private static void ConfigureRichText(TMP_Text text)
        {
            if (text == null)
                return;

            text.richText = true;
            text.ParseInputText();
            text.SetVerticesDirty();
        }

        private static void DestroyTimerText(PlayerRowTimer timer)
        {
            if (timer?.TimerText == null)
                return;

            timer.TimerText.text = string.Empty;
            UnityEngine.Object.Destroy(timer.TimerText.gameObject);
        }

        private static void DestroyOrphanTimerTexts(PlayersListNameItem row)
        {
            if (row == null)
                return;

            TMP_Text[] texts = row.GetComponentsInChildren<TMP_Text>(true);
            if (texts == null)
                return;

            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text != null && text.name == TimerTextName)
                {
                    text.text = string.Empty;
                    UnityEngine.Object.Destroy(text.gameObject);
                }
            }
        }

        private static TMP_Text FindNameText(PlayersListNameItem row, string displayName, string productId)
        {
            TMP_Text[] texts = row.GetComponentsInChildren<TMP_Text>(true);
            if (texts == null || texts.Length == 0)
                return null;

            TMP_Text firstNonNumericText = null;
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null || text.name == TimerTextName)
                    continue;

                string value = StripSessionTimer(text.text);
                if (!string.IsNullOrWhiteSpace(displayName) && value == displayName)
                    return text;

                if (!string.IsNullOrWhiteSpace(productId) && value == productId)
                    return text;

                if (firstNonNumericText == null && !string.IsNullOrWhiteSpace(value) && !double.TryParse(value, out _))
                    firstNonNumericText = text;
            }

            return firstNonNumericText ?? texts[0];
        }

        private static string StripSessionTimer(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            int suffixStart = text.LastIndexOf(" (", StringComparison.Ordinal);
            if (suffixStart <= 0 || !text.EndsWith(")", StringComparison.Ordinal))
                return text;

            string suffix = text.Substring(suffixStart + 2, text.Length - suffixStart - 3);
            for (int i = 0; i < suffix.Length; i++)
            {
                char c = suffix[i];
                if (!char.IsDigit(c) && c != ':')
                    return text;
            }

            return text.Substring(0, suffixStart);
        }

        private sealed class PlayerRowTimer
        {
            public readonly PlayersListNameItem Row;
            public readonly TMP_Text NameText;
            public readonly TMP_Text TimerText;
            public readonly string ProductId;
            public readonly string BaseName;
            public readonly bool IsLobbyOwner;
            public bool IsActive = true;
            public bool HasLoggedFirstUpdate;
            public string LastLabel;
            private string _lastFailure;

            public PlayerRowTimer(PlayersListNameItem row, TMP_Text nameText, TMP_Text timerText, string productId, string baseName, bool isLobbyOwner)
            {
                Row = row;
                NameText = nameText;
                TimerText = timerText;
                ProductId = productId;
                BaseName = baseName;
                IsLobbyOwner = isLobbyOwner;
            }

            public void LogFailureOnce(string reason)
            {
                if (_lastFailure == reason)
                    return;

                _lastFailure = reason;
                Debug.LogWarning($"[LobbyKit] Player row timer waiting for {BaseName}: {reason}.");
            }
        }
    }
}
