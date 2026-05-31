using System;
using System.Collections.Generic;

namespace LobbyKit.Features.Settings
{
    internal abstract class SettingsField
    {
        protected SettingsField(string id, string label)
        {
            Id = id;
            Label = label;
        }

        public string Id { get; }
        public string Label { get; }
    }

    internal sealed class TextSettingsField : SettingsField
    {
        public TextSettingsField(
            string id,
            string label,
            Func<SettingsDraft, string> get,
            Action<SettingsDraft, string> set,
            string placeholder = "")
            : base(id, label)
        {
            Get = get;
            Set = set;
            Placeholder = placeholder;
        }

        public Func<SettingsDraft, string> Get { get; }
        public Action<SettingsDraft, string> Set { get; }
        public string Placeholder { get; }
    }

    internal sealed class ToggleSettingsField : SettingsField
    {
        public ToggleSettingsField(
            string id,
            string label,
            Func<SettingsDraft, bool> get,
            Action<SettingsDraft, bool> set)
            : base(id, label)
        {
            Get = get;
            Set = set;
        }

        public Func<SettingsDraft, bool> Get { get; }
        public Action<SettingsDraft, bool> Set { get; }
    }

    internal static class SettingsSchema
    {
        public static IReadOnlyList<SettingsField> LobbySimpleFields { get; } = new SettingsField[]
        {
            new TextSettingsField(
                "server-name",
                "Server Name",
                draft => draft.ServerName,
                (draft, value) => draft.ServerName = value ?? string.Empty),
            new ToggleSettingsField(
                "public-lobby",
                "Public Lobby",
                draft => draft.IsPublicLobby,
                (draft, value) => draft.IsPublicLobby = value),
            new ToggleSettingsField(
                "peaceful-mode",
                "Peaceful Mode",
                draft => draft.IsPeacefulMode,
                (draft, value) => draft.IsPeacefulMode = value),
            new ToggleSettingsField(
                "text-chat-only",
                "Text Chat Only",
                draft => draft.IsTextChatOnly,
                (draft, value) => draft.IsTextChatOnly = value)
        };

        public static IReadOnlyList<SettingsField> ModSimpleFields { get; } = new SettingsField[]
        {
            new ToggleSettingsField(
                "enable-guest-bang-commands",
                "Enable Guest !Bang Commands",
                draft => draft.EnableGuestBangCommands,
                (draft, value) => draft.EnableGuestBangCommands = value),
            new ToggleSettingsField(
                "auto-restart-on-crash",
                "Auto-Restart Lobby On Crash",
                draft => draft.AutoRestartOnCrash,
                (draft, value) => draft.AutoRestartOnCrash = value),
            new ToggleSettingsField(
                "enable-anticheat",
                "Enable AntiCheat (experimental)",
                draft => draft.EnableAnticheat,
                (draft, value) => draft.EnableAnticheat = value),
            new TextSettingsField(
                "message-of-the-day",
                "Message of the Day",
                draft => draft.MessageOfTheDay,
                (draft, value) => draft.MessageOfTheDay = value ?? string.Empty)
        };
    }
}

