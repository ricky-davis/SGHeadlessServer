using System;
using HarmonyLib;
using Il2Cpp;
using Il2CppEpic.OnlineServices;
using Il2Cpp_Scripts.UI.Pre_Game;
using Il2CppPlayEveryWare.EpicOnlineServices.Samples;
using LobbyKit.Features.PlayerList;
using UnityEngine;

namespace LobbyKit.Patches
{
    [HarmonyPatch]
    internal static class PlayerListPatches
    {
        [HarmonyPatch(typeof(PlayersListNameItem), "InitializeWithDetails")]
        [HarmonyPostfix]
        private static void PlayersListNameItem_InitializeWithDetails_Postfix(
            PlayersListNameItem __instance,
            PlayerListPlayerActionsMenu actionMenu,
            LobbyMember lobbyMember,
            ProductUserId lobbyLeaderId)
        {
            try
            {
                PlayerListTimerController.Instance.BindRow(__instance, lobbyMember, lobbyLeaderId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyKit] Failed to bind player row session timer: {ex}");
            }
        }

        [HarmonyPatch(typeof(PlayersListNameItem), "OnDestroy")]
        [HarmonyPostfix]
        private static void PlayersListNameItem_OnDestroy_Postfix(PlayersListNameItem __instance)
        {
            PlayerListTimerController.Instance.MarkRowDestroyed(__instance);
        }

        [HarmonyPatch(typeof(PlayerListDisplayUI), "Update")]
        [HarmonyPostfix]
        private static void PlayerListDisplayUI_Update_Postfix()
        {
            try
            {
                PlayerListTimerController.Instance.UpdateVisibleRows();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LobbyKit] Failed to update player list session timers: {ex}");
            }
        }
    }
}
