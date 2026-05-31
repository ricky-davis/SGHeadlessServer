using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppEpic.OnlineServices;
using Il2CppEpic.OnlineServices.Lobby;
using Il2CppPlayEveryWare.EpicOnlineServices.Samples;
using Il2CppTMPro;
using Il2Cpp_Scripts.Managers;
using Il2Cpp_Scripts.UI.Pre_Game;
using MelonLoader;
using LobbyKit.Features.Lobby;
using UnityEngine;
using EosLobby = Il2CppPlayEveryWare.EpicOnlineServices.Samples.Lobby;

namespace LobbyKit.Patches
{
    [HarmonyPatch]
    public static class LobbyPatches
    {
        [HarmonyPatch(typeof(LobbyManager), "CreateLobby")]
        [HarmonyPrefix]
        private static void LobbyManager_CreateLobby_Prefix(ref string lobbyName, ref int maxPlayers)
        {
            if (UnityEngine.Application.isBatchMode) return;
            LobbyUiController.Instance.ApplyLobbyManagerCreateLobby(ref lobbyName, ref maxPlayers);
        }

        [HarmonyPatch(typeof(EOSLobbyManager), "CreateLobby")]
        [HarmonyPrefix]
        private static void EOSLobbyManager_CreateLobby_Prefix(EOSLobbyManager __instance, EosLobby lobbyProperties)
        {
            if (UnityEngine.Application.isBatchMode) return;
            LobbyUiController.Instance.ApplyEosCreateLobby(lobbyProperties);
        }

        [HarmonyPatch(typeof(LobbyManager), "SearchByAttributes")]
        [HarmonyPrefix]
        private static void LobbyManager_SearchByAttributes_Prefix(object[] __args)
        {
            if (__args == null || __args.Length == 0 || __args[0] == null)
                return;

            try
            {
                // We do nothing with this right now, but it's here if we ever decide to add more search parameters or add a minimum player count filter to the UI.
                object searchParameters = __args[0];
                LobbySearchSetParameterOptions option = BuildInt64(
                    LobbyInterface.SEARCH_MINCURRENTMEMBERS,
                    1,
                    ComparisonOp.Greaterthanorequal); 

                searchParameters.GetType().GetMethod("Add")?.Invoke(searchParameters, new object[] { option });
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"Failed to inject lobby search minimum-current-members parameter: {ex}");
            }
        }

        [HarmonyPatch(typeof(LobbyInterface), "CreateLobbySearch")]
        [HarmonyPrefix]
        private static void LobbyInterface_CreateLobbySearch_Prefix(ref CreateLobbySearchOptions options)
        {
            try
            {
                options.MaxResults = 100;
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"Failed to increase lobby search result limit: {ex}");
            }
        }

        [HarmonyPatch(typeof(UILobbyExplorer), "AddSearchParam")]
        [HarmonyPostfix]
        private static void UILobbyExplorer_AddSearchParam_Postfix(object[] __args)
        {
            if (__args == null || __args.Length < 4 || __args[0] == null)
                return;

            try
            {
                if (!IsMaxPlayersAttribute(__args[1]) || !IsEqualComparison(__args[3]) || !TryGetPositiveInt(__args[2], out int maxPlayers))
                    return;

                object searchParameters = __args[0];
                System.Type listType = searchParameters.GetType();
                System.Reflection.MethodInfo addMethod = listType.GetMethod("Add");
                System.Reflection.MethodInfo removeAtMethod = listType.GetMethod("RemoveAt");
                System.Reflection.PropertyInfo countProperty = listType.GetProperty("Count");
                int count = countProperty != null ? System.Convert.ToInt32(countProperty.GetValue(searchParameters)) : 0;
                if (addMethod == null || removeAtMethod == null || count == 0)
                    return;

                removeAtMethod.Invoke(searchParameters, new object[] { count - 1 });
                addMethod.Invoke(searchParameters, new object[] { BuildInt64((Utf8String)"MAXPLAYERS", maxPlayers, ComparisonOp.Greaterthanorequal) });
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"Failed to make UILobbyExplorer max-player search filter inclusive: {ex}");
            }
        }

        [HarmonyPatch(typeof(UILobbyExplorer), "ShowPanel")]
        [HarmonyPrefix]
        private static void UILobbyExplorer_ShowPanel_Prefix()
        {
            SearchLobbiesUiController.Instance.MarkLobbyExplorerOpening();
        }

        [HarmonyPatch(typeof(UILobbyExplorer), "GetListOfLobbies")]
        [HarmonyPrefix]
        private static void UILobbyExplorer_GetListOfLobbies_Prefix(UILobbyExplorer __instance)
        {
            SearchLobbiesUiController.Instance.ApplyPreferencesBeforeInitialSearch(__instance);
        }

        [HarmonyPatch(typeof(UILobbyExplorer), "UIUpdateSearchResults")]
        [HarmonyPostfix]
        private static void UILobbyExplorer_UIUpdateSearchResults_Postfix(UILobbyExplorer __instance)
        {
            try
            {
                SortVisibleLobbyRowsByCurrentPlayers(__instance);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning($"Failed to sort visible lobby rows by current players: {ex}");
            }
        }

        private static bool IsMaxPlayersAttribute(object value)
        {
            if (value == null)
                return false;

            string text = value.ToString();
            if (text == "MAXPLAYERS" || text == "9")
                return true;

            try
            {
                return System.Convert.ToInt32(value) == 9;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsEqualComparison(object value)
        {
            if (value is ComparisonOp comparisonOp)
                return comparisonOp == ComparisonOp.Equal;

            try
            {
                return System.Convert.ToInt32(value) == (int)ComparisonOp.Equal;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPositiveInt(object value, out int result)
        {
            try
            {
                if (value is Il2CppSystem.Object il2CppObject)
                {
                    result = il2CppObject.Unbox<int>();
                    return result > 0;
                }

                result = System.Convert.ToInt32(value);
                return result > 0;
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        private static void SortVisibleLobbyRowsByCurrentPlayers(UILobbyExplorer lobbyExplorer)
        {
            if (lobbyExplorer == null)
                return;

            var rows = new List<LobbyRowEntry>();
            foreach (UILobbyItem item in lobbyExplorer.GetComponentsInChildren<UILobbyItem>(true))
            {
                Transform itemTransform = item.transform;
                if (itemTransform?.parent == null || !TryGetDisplayedCurrentPlayers(item, out int currentPlayers))
                    continue;

                rows.Add(new LobbyRowEntry(itemTransform, currentPlayers, itemTransform.GetSiblingIndex()));
            }

            if (rows.Count < 2)
                return;

            rows.Sort((left, right) =>
            {
                int playerComparison = right.CurrentPlayers.CompareTo(left.CurrentPlayers);
                return playerComparison != 0 ? playerComparison : left.OriginalIndex.CompareTo(right.OriginalIndex);
            });

            for (int i = 0; i < rows.Count; i++)
                rows[i].Transform.SetSiblingIndex(i);
        }

        private static bool TryGetDisplayedCurrentPlayers(UILobbyItem item, out int currentPlayers)
        {
            foreach (TMP_Text text in item.GetComponentsInChildren<TMP_Text>(true))
            {
                string value = text?.text;
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                int slashIndex = value.IndexOf('/');
                if (slashIndex <= 0)
                    continue;

                if (int.TryParse(value.Substring(0, slashIndex).Trim(), out currentPlayers))
                    return true;
            }

            currentPlayers = 0;
            return false;
        }

        private sealed class LobbyRowEntry
        {
            public LobbyRowEntry(Transform transform, int currentPlayers, int originalIndex)
            {
                Transform = transform;
                CurrentPlayers = currentPlayers;
                OriginalIndex = originalIndex;
            }

            public Transform Transform { get; }
            public int CurrentPlayers { get; }
            public int OriginalIndex { get; }
        }

        [HarmonyPatch(typeof(UIMainMenu), "OnEnable")]
        [HarmonyPostfix]
        private static void UIMainMenu_OnEnable_Postfix(UIMainMenu __instance)
        {
            if (UnityEngine.Application.isBatchMode) return;
            LobbyUiController.Instance.ConfigureMainMenuOnEnable(__instance);
            SearchLobbiesUiController.Instance.ConfigureMainMenuOnEnable(__instance);
        }

        private static LobbySearchSetParameterOptions BuildInt64(Utf8String key, long value, ComparisonOp comparisonOp)
        {
            var attributeValue = new AttributeDataValue();
            attributeValue.AsInt64 = new Il2CppSystem.Nullable<long>(value);

            var attribute = new AttributeData();
            attribute.Key = key;
            attribute.Value = attributeValue;

            var option = new LobbySearchSetParameterOptions();
            option.Parameter = new Il2CppSystem.Nullable<AttributeData>(attribute);
            option.ComparisonOp = comparisonOp;
            return option;
        }
    }
}
