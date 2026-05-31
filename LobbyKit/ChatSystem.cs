using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppFishNet;
using Il2CppFishNet.Connection;
using Il2CppDissonance.Integrations.FishNet;
using Il2Cpp_Scripts.Player;
using Il2Cpp_Scripts.Systems.Chat;
using MelonLoader;
using LobbyKit.Features.Settings;
using UnityEngine;
using System.Text.RegularExpressions;

namespace LobbyKit.Patches
{
    [HarmonyPatch]
    public static class ChatSystem
    {
        private const int HostConnectionId = 32767;
        private const int PrivateReplySystemMessageType = 0;
        private const string MotdCommand = "!motd";
        private const string HideSystemMessageSuffixTag = "<size=0>";

        private delegate void ChatCommandHandler(PlayerControl playerControl, string args);

        private sealed class CommandDefinition
        {
            public CommandDefinition(
                ChatCommandHandler handler,
                string usage,
                string description,
                bool hostCommand = false,
                bool hiddenFromHelp = false)
            {
                Handler = handler;
                Usage = usage;
                Description = description;
                HostCommand = hostCommand;
                HiddenFromHelp = hiddenFromHelp;
            }

            public ChatCommandHandler Handler { get; }
            public string Usage { get; }
            public string Description { get; }
            public bool HostCommand { get; }
            public bool HiddenFromHelp { get; }
        }

        private static readonly Dictionary<int, (string Message, int Frame)> LastCommandBySource = new();
        private static readonly Dictionary<int, string> LastExplicitCommandBySource = new();
        private static readonly HashSet<int> MotdRecipients = new();
        private static readonly Dictionary<int, int> TeleportRequests = new();

        private static readonly Dictionary<string, CommandDefinition> Commands = new(StringComparer.OrdinalIgnoreCase)
        {
            ["!help"] = new CommandDefinition(
                HandleHelpCommand,
                "!help [command]",
                "Shows available commands or details for one command.",
                hiddenFromHelp: true),
            ["!settings"] = new CommandDefinition(
                OpenSettingsMenu,
                "!settings",
                "Open LobbyKit settings.",
                hostCommand: true),
            [MotdCommand] = new CommandDefinition(
                HandleMotdCommand,
                "!motd [message]",
                "Shows the message of the day. Host can pass a message to set it."),
            ["!bc"] = new CommandDefinition(
                HandleBangCommandsCommand,
                "!bc <on|off>",
                "Enable or disable guest bang commands.",
                hostCommand: true),
            ["!tp"] = new CommandDefinition(
                HandleTpCommand,
                "!tp <name>",
                "Teleport to a player by name."),
            ["!tpme"] = new CommandDefinition(
                HandleTpMeCommand,
                "!tpme <name>",
                "Ask a player to Teleport to you."),
            ["!tpa"] = new CommandDefinition(
                HandleTpAcceptCommand,
                "!tpa",
                "Accept a Teleport request."),
            ["!tpf"] = new CommandDefinition(
                HandleTpForceCommand,
                "!tpf <name>",
                "Force a player to Teleport to you.",
                hostCommand: true)
        };

        private static void OpenSettingsMenu(PlayerControl playerControl, string args)
        {
            SettingsMenuController.Instance.Open(SettingsMenuView.Build, SettingsMenuView.HasRequiredTemplates);
        }

        [HarmonyPatch(typeof(ChatManager), "ProcessChatInput")]
        [HarmonyPrefix]
        private static bool ChatManager_ProcessChatInput_Prefix(ChatManager __instance)
        {
            var localPlayer = LobbyKitCore.Instance?.GetLocalPlayer();
            if (!LobbyKitCore.isHost || localPlayer == null || localPlayer.ConnectionID != HostConnectionId)
                return true;

            var chatBox = __instance?.chatBox;
            string raw = chatBox?.InputField?.text ?? string.Empty;
            if (raw.StartsWith("/", StringComparison.Ordinal))
                return true;

            string message = StringHelpers.PrepareStringForChat(raw);
            if (string.IsNullOrWhiteSpace(message))
                return true;

            if (WasHandledThisFrame(localPlayer.ConnectionID, message))
                return false;

            bool handledCommand = TryHandleCommand(message, localPlayer.ConnectionID, isHostLocal: true);
            if (!handledCommand)
                BroadcastMessage(0, message, AutoCloseTmpRichText(localPlayer.Username), showAboveUser: localPlayer.ConnectionID);

            chatBox.inputFieldValue = string.Empty;
            chatBox.ClearInputBox();
            MarkHandled(localPlayer.ConnectionID, message);
            return false;
        }

        [HarmonyPatch(typeof(ChatManager), "OnServerReceivedChatBroadcastFromClient")]
        [HarmonyPrefix]
        private static bool ChatManager_OnServerReceivedChatBroadcastFromClient_Prefix(
            NetworkConnection networkConnection,
            ChatMessage chatMessage,
            byte channel)
        {
            if (networkConnection == null)
                return true;

            string message = chatMessage.Message;
            if (string.IsNullOrWhiteSpace(message))
                return true;

            if (message.StartsWith("/", StringComparison.Ordinal))
                return true;

            if (WasHandledThisFrame(networkConnection.ClientId, message))
                return false;

            bool handledCommand = TryHandleCommand(message, networkConnection.ClientId, isHostLocal: false);
            if (!handledCommand)
                BroadcastPlayerMessage(networkConnection.ClientId, message, chatMessage.Username);

            MarkHandled(networkConnection.ClientId, message);
            return false;
        }

        public static void BroadcastMessage(int clientId, string text, string username = "", int showAboveUser = 0)
        {
            if (clientId != 0 && TrySendLocalReply(clientId, text))
                return;

            var serverManager = InstanceFinder.ServerManager;
            if (serverManager == null)
            {
                Debug.LogError("[ChatSystem] Cannot send chat message: server manager is unavailable.");
                return;
            }

            if (clientId == 0)
            {
                serverManager.Broadcast(CreatePublicChatMessage(text, username, showAboveUser), true);
                return;
            }

            if (!serverManager.Clients.TryGetValue(clientId, out NetworkConnection connection))
            {
                Debug.LogError($"[ChatSystem] Cannot send private chat message: client {clientId} was not found.");
                return;
            }

            serverManager.Broadcast(connection, CreateSystemChatMessage(text), true);
        }

        public static void BroadcastSystemMessage(string text)
        {
            var serverManager = InstanceFinder.ServerManager;
            if (serverManager == null)
            {
                Debug.LogError("[ChatSystem] Cannot send system chat message: server manager is unavailable.");
                return;
            }

            serverManager.Broadcast(CreateSystemChatMessage(text), true);
        }

        public static void SendMotdToPlayer(PlayerReference player)
        {
            if (!LobbyKitCore.isHost || player == null)
                return;

            int connectionId = player.ConnectionID;
            string motd = LobbyKitCore.MessageOfTheDay;
            if (string.IsNullOrWhiteSpace(motd) || !MotdRecipients.Add(connectionId))
                return;

            MelonCoroutines.Start(SendMotdWhenReady(connectionId));
        }

        public static void ForgetMotdRecipient(int connectionId)
        {
            MotdRecipients.Remove(connectionId);
        }

        public static void ForgetTeleportRequests(int connectionId)
        {
            TeleportRequests.Remove(connectionId);

            foreach (int targetConnectionId in new List<int>(TeleportRequests.Keys))
            {
                if (TeleportRequests[targetConnectionId] == connectionId)
                    TeleportRequests.Remove(targetConnectionId);
            }
        }

        public static void ResetSessionState()
        {
            MotdRecipients.Clear();
            TeleportRequests.Clear();
            LastCommandBySource.Clear();
            LastExplicitCommandBySource.Clear();
        }

        private static bool TryHandleCommand(string message, int connectionId, bool isHostLocal)
        {
            string trimmedMessage = message.Trim();
            if (trimmedMessage == "!!")
            {
                if (!LastExplicitCommandBySource.TryGetValue(connectionId, out string lastCommand) || string.IsNullOrWhiteSpace(lastCommand))
                {
                    BroadcastMessage(connectionId, "<#FA0>No previous command to repeat.");
                    return true;
                }

                message = lastCommand;
                trimmedMessage = lastCommand.Trim();
            }

            string[] parts = trimmedMessage.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !Commands.TryGetValue(parts[0], out CommandDefinition command))
                return false;

            bool isHost = connectionId == HostConnectionId;
            string args = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            bool isMotdCommand = parts[0].Equals(MotdCommand, StringComparison.OrdinalIgnoreCase);

            if (isMotdCommand && !HasMotd() && (string.IsNullOrWhiteSpace(args) || !isHost))
                return true;

            if (!LobbyKitCore.EnableGuestBangCommands && !isHost)
            {
                BroadcastMessage(connectionId, "<#FA0>Commands are disabled on this server.");
                return true;
            }

            if (command.HostCommand && !isHost)
            {
                BroadcastMessage(connectionId, "<#F00>Only the host can use that command.");
                return true;
            }

            PlayerControl playerControl = ResolvePlayerControl(connectionId, isHostLocal);
            if (playerControl == null)
            {
                BroadcastMessage(connectionId, "<#F00>Command failed: player is not ready.");
                return true;
            }

            if (!string.Equals(trimmedMessage, "!!", StringComparison.Ordinal))
                LastExplicitCommandBySource[connectionId] = trimmedMessage;

            try
            {
                command.Handler(playerControl, args);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChatSystem] Command '{parts[0]}' failed: {ex}");
                BroadcastMessage(connectionId, "<#F00>Command failed. Check the host log for details.");
            }

            return true;
        }

        private static IEnumerator SendMotdWhenReady(int connectionId)
        {
            bool isLocal = IsLocalConnectionId(connectionId);

            for (int frame = 0; frame < 300; frame++)
            {
                bool ready = isLocal
                    ? ChatManager.Instance != null
                    : InstanceFinder.ServerManager?.Clients?.ContainsKey(connectionId) == true;

                if (ready)
                {
                    string motd = LobbyKitCore.MessageOfTheDay;
                    if (string.IsNullOrWhiteSpace(motd))
                    {
                        MotdRecipients.Remove(connectionId);
                        yield break;
                    }

                    BroadcastMessage(connectionId, motd);
                    yield break;
                }

                yield return null;
            }

            MotdRecipients.Remove(connectionId);
            Debug.LogWarning($"[ChatSystem] MOTD was not sent: client {connectionId} was not ready after 300 frames.");
        }

        private static ChatMessage CreatePublicChatMessage(string text, string username, int showAboveUser)
        {
            string cleanedUsername = AutoCloseTmpRichText(username);
            return new ChatMessage
            {
                Username = string.Empty,
                UserProductId = GetPublicMessageProductId(showAboveUser),
                Message = string.IsNullOrWhiteSpace(cleanedUsername) ? text : $"{cleanedUsername}: {text}",
                MessageType = ChatMessageType.Chat,
                SystemMessageType = (SystemMessageType)(-1)
            };
        }

        public static string AutoCloseTmpRichText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var tagRegex = new Regex(@"<(/?)([a-zA-Z#][a-zA-Z0-9#-]*)(?:=[^>]*)?>");
            var openTags = new Stack<string>();
            var selfClosing = new HashSet<string> { "br", "space", "sprite", "page" };

            foreach (Match m in tagRegex.Matches(text))
            {
                string slash = m.Groups[1].Value;
                string tag = m.Groups[2].Value.ToLowerInvariant();

                if (tag.StartsWith("#"))
                    tag = "color";

                if (selfClosing.Contains(tag))
                    continue;

                if (slash == "/")
                {
                    if (openTags.Count > 0 && openTags.Peek() == tag)
                        openTags.Pop();
                }
                else
                {
                    openTags.Push(tag);
                }
            }

            while (openTags.Count > 0)
                text += $"</{openTags.Pop()}>";

            return text;
        }

        private static void BroadcastPlayerMessage(int connectionId, string text, string fallbackUsername = "")
        {
            PlayerReference player = Utils.FindPlayerFromConnectionId(connectionId);
            string username = !string.IsNullOrWhiteSpace(player?.Username)
                ? player.Username
                : fallbackUsername;

            BroadcastMessage(0, text, AutoCloseTmpRichText(username), showAboveUser: connectionId);
        }

        private static ChatMessage CreateSystemChatMessage(string text)
        {
            return new ChatMessage
            {
                Username = AutoCloseTmpRichText(text) + HideSystemMessageSuffixTag,
                UserProductId = string.Empty,
                Message = string.Empty,
                MessageType = ChatMessageType.System,
                SystemMessageType = (SystemMessageType)PrivateReplySystemMessageType
            };
        }

        private static string GetPublicMessageProductId(int showAboveUser)
        {
            if (showAboveUser != 0)
            {
                var player = Utils.FindPlayerFromConnectionId(showAboveUser);
                if (player != null && !string.IsNullOrWhiteSpace(player.ProductUserId))
                    return player.ProductUserId;
            }

            return Utils.FindPlayerFromConnectionId(HostConnectionId)?.ProductUserId ?? "SERVER_MESSAGE";
        }

        private static PlayerControl ResolvePlayerControl(int connectionId, bool isHostLocal)
        {
            if (isHostLocal)
            {
                PlayerControl hostPlayer = Utils.FindHostPlayer();
                if (hostPlayer != null)
                    return hostPlayer;
            }

            return Utils.FindPlayerFromConnectionId(connectionId)?.PlayerControl;
        }

        private static bool TrySendLocalReply(int clientId, string text)
        {
            if (!IsLocalConnectionId(clientId))
                return false;

            ChatManager chatManager = ChatManager.Instance;
            if (chatManager == null)
                return false;

            try
            {
                chatManager.SendLocalSystemChatMessage(text);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ChatSystem] Local command reply failed: {ex}");
                return false;
            }
        }

        private static bool IsLocalConnectionId(int connectionId)
        {
            if (connectionId == HostConnectionId)
                return true;

            PlayerReference localPlayer = LobbyKitCore.Instance?.GetLocalPlayer();
            return localPlayer != null && localPlayer.ConnectionID == connectionId;
        }

        private static bool WasHandledThisFrame(int connectionId, string message)
        {
            return LastCommandBySource.TryGetValue(connectionId, out var last) &&
                   last.Frame == Time.frameCount &&
                   last.Message == message;
        }

        private static void MarkHandled(int connectionId, string message)
        {
            LastCommandBySource[connectionId] = (message, Time.frameCount);
        }

        private static void HandleHelpCommand(PlayerControl playerControl, string args)
        {
            string helpSizing = "<size=75%>";
            if (!string.IsNullOrWhiteSpace(args))
            {
                string requested = args.Trim();
                if (!requested.StartsWith("!", StringComparison.Ordinal))
                    requested = "!" + requested;

                if (Commands.TryGetValue(requested, out CommandDefinition command) && CanShowInHelp(requested, command, playerControl))
                    Reply(playerControl, $"{helpSizing}<#7FF>{FormatCommandUsage(command)} - {command.Description}");
                else
                    Reply(playerControl, $"{helpSizing}<#FA0>Unknown command: {requested}");

                return;
            }

            Reply(playerControl, $"{helpSizing}<#7FF>Available commands:");
            foreach (var entry in Commands)
            {
                if (CanShowInHelp(entry.Key, entry.Value, playerControl))
                    Reply(playerControl, $"{helpSizing}<#7FF>{FormatCommandUsage(entry.Value)}");
            }
        }

        private static bool CanShowInHelp(string commandName, CommandDefinition command, PlayerControl playerControl)
        {
            return !command.HiddenFromHelp &&
                   (!commandName.Equals(MotdCommand, StringComparison.OrdinalIgnoreCase) || HasMotd()) &&
                   CanUseCommand(command, playerControl);
        }

        private static bool HasMotd()
        {
            return !string.IsNullOrWhiteSpace(LobbyKitCore.MessageOfTheDay);
        }

        private static bool CanUseCommand(CommandDefinition command, PlayerControl playerControl)
        {
            return !command.HostCommand || playerControl.OwnerId == HostConnectionId;
        }

        private static string FormatCommandUsage(CommandDefinition command)
        {
            return command.HostCommand ? $"{command.Usage} (host)" : command.Usage;
        }

        private static void HandleBangCommandsCommand(PlayerControl playerControl, string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                string state = LobbyKitCore.EnableGuestBangCommands ? "enabled" : "disabled";
                Reply(playerControl, $"<#7FF>Guest bang commands are {state}. Usage: !bangcommands <on|off>");
                return;
            }

            string value = args.Trim().ToLowerInvariant();
            bool? enabled = value switch
            {
                "on" or "enable" or "enabled" or "true" or "1" => true,
                "off" or "disable" or "disabled" or "false" or "0" => false,
                _ => null
            };

            if (!enabled.HasValue)
            {
                Reply(playerControl, "<#F00>Usage: !bangcommands <on|off>");
                return;
            }

            LobbyKitCore.SetEnableGuestBangCommands(enabled.Value);
            Reply(playerControl, $"<#FF0>Guest bang commands {(enabled.Value ? "enabled" : "disabled")}.");
        }

        private static void HandleMotdCommand(PlayerControl playerControl, string args)
        {
            if (string.IsNullOrWhiteSpace(args) || playerControl.OwnerId != HostConnectionId)
            {
                Reply(playerControl, $"<#7FF>MOTD: </color>{LobbyKitCore.MessageOfTheDay}");
                return;
            }

            LobbyKitCore.SetMessageOfTheDay(args.Trim());
            Reply(playerControl, $"<#FF0>MOTD set: </color>{LobbyKitCore.MessageOfTheDay}");
        }

        private static void HandleTpCommand(PlayerControl playerControl, string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                Reply(playerControl, "<#F00>Usage: !tp <name>");
                return;
            }

            string targetName = args.Trim();
            PlayerReference target = Utils.FindPlayerByName(targetName, sanitized: true);
            if (target == null)
            {
                Reply(playerControl, $"<#FA0>Player not found: {targetName}");
                return;
            }

            string targetUsername = AutoCloseTmpRichText(target.Username);
            if (target.PlayerControl == null)
            {
                return;
            }

            TeleportPlayerTo(playerControl, target.PlayerControl);
            Reply(playerControl, $"<#FF0>TP'd to {targetUsername}");
        }

        private static void HandleTpMeCommand(PlayerControl playerControl, string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                Reply(playerControl, "<#F00>Usage: !tpme <name>");
                return;
            }

            PlayerReference requester = Utils.FindPlayerFromConnectionId(playerControl.OwnerId);
            if (requester == null)
            {
                Reply(playerControl, "<#F00>Command failed: player is not ready.");
                return;
            }

            PlayerReference target = Utils.FindPlayerByName(args.Trim(), sanitized: true);
            if (target == null)
            {
                Reply(playerControl, $"<#FA0>Player not found: {args.Trim()}");
                return;
            }

            if (target.ConnectionID == requester.ConnectionID)
            {
                Reply(playerControl, "<#FA0>You cannot request yourself.");
                return;
            }

            string requesterUsername = AutoCloseTmpRichText(requester.Username);
            string targetUsername = AutoCloseTmpRichText(target.Username);
            TeleportRequests[target.ConnectionID] = requester.ConnectionID;
            BroadcastMessage(target.ConnectionID, $"<#7FF>{requesterUsername} wants you to TP to them. Type !tpa to accept.");
            Reply(playerControl, $"<#FF0>TP request sent to {targetUsername}.");
        }

        private static void HandleTpAcceptCommand(PlayerControl playerControl, string args)
        {
            int targetConnectionId = playerControl.OwnerId;
            if (!TeleportRequests.TryGetValue(targetConnectionId, out int requesterConnectionId))
            {
                Reply(playerControl, "<#FA0>You have no pending TP request.");
                return;
            }

            TeleportRequests.Remove(targetConnectionId);

            PlayerReference requester = Utils.FindPlayerFromConnectionId(requesterConnectionId);
            if (requester?.PlayerControl == null)
            {
                return;
            }

            string requesterUsername = AutoCloseTmpRichText(requester.Username);
            TeleportPlayerTo(playerControl, requester.PlayerControl);
            Reply(playerControl, $"<#FF0>TP'd to {requesterUsername}.");
            string accepterName = AutoCloseTmpRichText(Utils.FindPlayerFromConnectionId(targetConnectionId)?.Username ?? "A player");
            BroadcastMessage(requester.ConnectionID, $"<#FF0>{accepterName} accepted your TP request.");
        }

        private static void HandleTpForceCommand(PlayerControl playerControl, string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                Reply(playerControl, "<#F00>Usage: !tpf <name>");
                return;
            }

            PlayerReference target = Utils.FindPlayerByName(args.Trim(), sanitized: true);
            if (target == null)
            {
                Reply(playerControl, $"<#FA0>Player not found: {args.Trim()}");
                return;
            }

            string targetUsername = AutoCloseTmpRichText(target.Username);
            if (target.PlayerControl == null)
            {
                return;
            }

            if (target.ConnectionID == playerControl.OwnerId)
            {
                Reply(playerControl, "<#FA0>You cannot force TP yourself.");
                return;
            }

            TeleportPlayerTo(target.PlayerControl, playerControl);
            Reply(playerControl, $"<#FF0>Forced {targetUsername} to TP to you.");
            BroadcastMessage(target.ConnectionID, $"<#FF0>The host TP'd you to them.");
        }

        private static void TeleportPlayerTo(PlayerControl player, PlayerControl destination)
        {
            player.RpcWriter___RpcResetPosition___3848837105(
                destination.transform.position,
                destination.transform.rotation);
        }

        private static void Reply(PlayerControl playerControl, string message)
        {
            BroadcastMessage(playerControl.OwnerId, message);
        }
    }
}
