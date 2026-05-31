using System;
using LobbyKit.Features.Lobby;

namespace LobbyKit.Features.Settings
{
    internal sealed class SettingsDraft
    {
        public bool EnableGuestBangCommands { get; set; }
        public string ServerName { get; set; }
        public int ServerCapacity { get; set; }
        public bool IsPublicLobby { get; set; }
        public bool IsPasswordProtected { get; set; }
        public string LobbyPassword { get; set; }
        public bool IsPeacefulMode { get; set; }
        public bool IsTextChatOnly { get; set; }
        public string MessageOfTheDay { get; set; }
        public bool ShowJoinMessages { get; set; }
        public bool ShowLeaveMessages { get; set; }
        public int JoinMessageSize { get; set; }
        public int LeaveMessageSize { get; set; }
        public bool AutoRestartOnCrash { get; set; }
        public bool EnableAnticheat { get; set; }

        public static SettingsDraft FromCurrent()
        {
            return new SettingsDraft
            {
                EnableGuestBangCommands = LobbyKitCore.EnableGuestBangCommands,
                ServerName = LobbyKitCore.ServerName,
                ServerCapacity = LobbyKitCore.ServerCapacity,
                IsPublicLobby = LobbyKitCore.IsPublicLobby,
                IsPasswordProtected = LobbyKitCore.IsPasswordProtected,
                LobbyPassword = LobbyKitCore.LobbyPassword,
                IsPeacefulMode = LobbyKitCore.IsPeacefulMode,
                IsTextChatOnly = LobbyKitCore.IsTextChatOnly,
                MessageOfTheDay = LobbyKitCore.MessageOfTheDay,
                ShowJoinMessages = LobbyKitCore.ShowJoinMessages,
                ShowLeaveMessages = LobbyKitCore.ShowLeaveMessages,
                JoinMessageSize = LobbyKitCore.JoinMessageSize,
                LeaveMessageSize = LobbyKitCore.LeaveMessageSize,
                AutoRestartOnCrash = LobbyKitCore.AutoRestartOnCrash,
                EnableAnticheat = LobbyKitCore.EnableAnticheat
            };
        }

        public SettingsDraft Clone()
        {
            return new SettingsDraft
            {
                EnableGuestBangCommands = EnableGuestBangCommands,
                ServerName = ServerName,
                ServerCapacity = ServerCapacity,
                IsPublicLobby = IsPublicLobby,
                IsPasswordProtected = IsPasswordProtected,
                LobbyPassword = LobbyPassword,
                IsPeacefulMode = IsPeacefulMode,
                IsTextChatOnly = IsTextChatOnly,
                MessageOfTheDay = MessageOfTheDay,
                ShowJoinMessages = ShowJoinMessages,
                ShowLeaveMessages = ShowLeaveMessages,
                JoinMessageSize = JoinMessageSize,
                LeaveMessageSize = LeaveMessageSize,
                AutoRestartOnCrash = AutoRestartOnCrash,
                EnableAnticheat = EnableAnticheat
            };
        }

        public bool ValueEquals(SettingsDraft other)
        {
            if (other == null)
                return false;

            return EnableGuestBangCommands == other.EnableGuestBangCommands &&
                   string.Equals(ServerName, other.ServerName, StringComparison.Ordinal) &&
                   ServerCapacity == other.ServerCapacity &&
                   IsPublicLobby == other.IsPublicLobby &&
                   IsPasswordProtected == other.IsPasswordProtected &&
                   string.Equals(LobbyPassword, other.LobbyPassword, StringComparison.Ordinal) &&
                   IsPeacefulMode == other.IsPeacefulMode &&
                   IsTextChatOnly == other.IsTextChatOnly &&
                   string.Equals(MessageOfTheDay, other.MessageOfTheDay, StringComparison.Ordinal) &&
                   ShowJoinMessages == other.ShowJoinMessages &&
                   ShowLeaveMessages == other.ShowLeaveMessages &&
                   JoinMessageSize == other.JoinMessageSize &&
                   LeaveMessageSize == other.LeaveMessageSize &&
                   AutoRestartOnCrash == other.AutoRestartOnCrash &&
                   EnableAnticheat == other.EnableAnticheat;
        }

        public void Apply()
        {
            LobbyKitCore.SetEnableGuestBangCommands(EnableGuestBangCommands);
            LobbyKitCore.SetServerName(ServerName);
            LobbyKitCore.SetServerCapacity(ServerCapacity);
            LobbyKitCore.SetIsPublicLobby(IsPublicLobby);
            LobbyKitCore.SetIsPasswordProtected(IsPasswordProtected);
            LobbyKitCore.SetLobbyPassword(LobbyPassword);
            LobbyKitCore.SetIsPeacefulMode(IsPeacefulMode);
            LobbyKitCore.SetIsTextChatOnly(IsTextChatOnly);
            LobbyKitCore.SetMessageOfTheDay(MessageOfTheDay);
            LobbyKitCore.SetShowJoinMessages(ShowJoinMessages);
            LobbyKitCore.SetShowLeaveMessages(ShowLeaveMessages);
            LobbyKitCore.SetJoinMessageSize(JoinMessageSize);
            LobbyKitCore.SetLeaveMessageSize(LeaveMessageSize);
            LobbyKitCore.SetAutoRestartOnCrash(AutoRestartOnCrash);
            LobbyKitCore.SetEnableAnticheat(EnableAnticheat);

            LobbyUiController.Instance.RefreshCreateLobbyFromPreferences();
        }
    }
}

