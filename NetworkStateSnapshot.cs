using System;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine.SceneManagement;

namespace Recconect;

internal static class NetworkStateSnapshot
{
    internal static void Log(string source, DisconnectCause? cause = null)
    {
        if (!Recconect.ModConfig.DiagnosticsEnabled.Value)
        {
            return;
        }

        Recconect.Logger.LogInfo(Build(source, cause));
    }

    private static string Build(string source, DisconnectCause? cause)
    {
        Room? room = Safe(() => PhotonNetwork.CurrentRoom);
        Player? localPlayer = Safe(() => PhotonNetwork.LocalPlayer);
        LoadBalancingClient? client = Safe(() => PhotonNetwork.NetworkingClient);

        string roomState = room == null
            ? "room=<null>"
            : $"room={room.Name} players={room.PlayerCount}/{room.MaxPlayers} open={room.IsOpen} visible={room.IsVisible}";

        string localPlayerState = localPlayer == null
            ? "localPlayer=<null>"
            : $"localActor={localPlayer.ActorNumber} localUser={localPlayer.UserId ?? "<null>"} localNick={localPlayer.NickName ?? "<null>"}";

        string clientState = client == null
            ? "client=<null>"
            : $"state={client.State} disconnectedCause={client.DisconnectedCause} server={client.Server}";

        return string.Join(
            " | ",
            $"source={source}",
            $"cause={(cause.HasValue ? cause.Value.ToString() : "<none>")}",
            clientState,
            $"connected={Safe(() => PhotonNetwork.IsConnected)} ready={Safe(() => PhotonNetwork.IsConnectedAndReady)} inRoom={Safe(() => PhotonNetwork.InRoom)}",
            $"region={Safe(() => PhotonNetwork.CloudRegion) ?? "<null>"} isMaster={Safe(() => PhotonNetwork.IsMasterClient)} syncScene={Safe(() => PhotonNetwork.AutomaticallySyncScene)} queue={Safe(() => PhotonNetwork.IsMessageQueueRunning)}",
            SceneState(room),
            GameState(),
            roomState,
            localPlayerState,
            SteamLobbyState());
    }

    private static string SceneState(Room? room)
    {
        Scene scene = SceneManager.GetActiveScene();
        object? syncedScene = null;
        try
        {
            syncedScene = room?.CustomProperties["curScn"];
        }
        catch
        {
            syncedScene = null;
        }

        return $"scene={scene.buildIndex}:{scene.name} syncedScene={syncedScene ?? "<none>"}";
    }

    private static string GameState()
    {
        object? gameManager = GetStaticField(AccessTools.TypeByName("GameManager"), "instance");
        object? runManager = GetStaticField(AccessTools.TypeByName("RunManager"), "instance");
        object? gameDirector = GetStaticField(AccessTools.TypeByName("GameDirector"), "instance");
        object? networkManager = GetStaticField(AccessTools.TypeByName("NetworkManager"), "instance");
        object? playerAvatar = GetStaticField(AccessTools.TypeByName("PlayerAvatar"), "instance");
        object? playerController = GetStaticField(AccessTools.TypeByName("PlayerController"), "instance");
        object? menuManager = GetStaticField(AccessTools.TypeByName("MenuManager"), "instance");
        object? hud = GetStaticField(AccessTools.TypeByName("HUD"), "instance");
        object? loadingUi = GetStaticField(AccessTools.TypeByName("LoadingUI"), "instance");
        object? lobbyMenuOpen = GetStaticField(AccessTools.TypeByName("LobbyMenuOpen"), "instance");

        object? levelCurrent = runManager == null ? null : GetInstanceField(runManager, "levelCurrent");
        object? playerAvatarScript = playerController == null ? null : GetInstanceField(playerController, "playerAvatarScript");
        object? currentMenuPage = menuManager == null ? null : GetInstanceField(menuManager, "currentMenuPage");
        object? allPages = menuManager == null ? null : GetInstanceField(menuManager, "allPages");
        object? addedPagesOnTop = menuManager == null ? null : GetInstanceField(menuManager, "addedPagesOnTop");

        return string.Join(
            " ",
            $"gameMode={ValueOrNull(gameManager, "gameMode")}",
            $"level={GetInstanceProperty(levelCurrent, "name") ?? "<null>"}",
            $"director={ValueOrNull(gameDirector, "currentState")}",
            $"networkManager={(networkManager == null ? "null" : "ok")}",
            $"leaveRoom={ValueOrNull(networkManager, "leavePhotonRoom")}",
            $"avatar={(playerAvatar == null ? "null" : "ok")}",
            $"avatarDisabled={ValueOrNull(playerAvatar, "isDisabled")}",
            $"avatarSpawned={ValueOrNull(playerAvatar, "spawned")}",
            $"controllerAvatar={(playerAvatarScript == null ? "null" : "ok")}",
            $"menuPage={ValueOrNull(currentMenuPage, "menuPageIndex")}",
            $"menuState={ValueOrNull(menuManager, "currentMenuState")}",
            $"menuPages={CollectionCount(allPages)}+{CollectionCount(addedPagesOnTop)}",
            $"hudHidden={ValueOrNull(hud, "hidden")}",
            $"loadingActive={GameObjectActive(loadingUi)}",
            $"loadingStuck={ValueOrNull(loadingUi, "stuckActive")}",
            $"lobbyMenuOpen={ValueOrNull(lobbyMenuOpen, "opened")}/{ValueOrNull(lobbyMenuOpen, "timer")}");
    }

    private static string SteamLobbyState()
    {
        object? manager = GetStaticField(AccessTools.TypeByName("SteamManager"), "instance");
        if (manager == null)
        {
            return "steamLobby=<no SteamManager>";
        }

        object? currentLobby = GetInstanceField(manager, "currentLobby");
        if (currentLobby == null)
        {
            return "steamLobby=<null>";
        }

        object? id = GetInstanceProperty(currentLobby, "Id");
        string idText = id?.ToString() ?? "<null>";
        string isValid = GetInstanceProperty(id, "IsValid")?.ToString() ?? "<unknown>";
        string memberCount = GetInstanceProperty(currentLobby, "MemberCount")?.ToString() ?? "<unknown>";
        string maxMembers = GetInstanceProperty(currentLobby, "MaxMembers")?.ToString() ?? "<unknown>";

        return $"steamLobby={idText} valid={isValid} members={memberCount}/{maxMembers}";
    }

    private static T? Safe<T>(Func<T> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }

    private static object? GetStaticField(Type? type, string fieldName)
    {
        return type?.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
    }

    private static object? GetInstanceField(object target, string fieldName)
    {
        return target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
    }

    private static object? GetInstanceProperty(object? target, string propertyName)
    {
        try
        {
            return target?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static string ValueOrNull(object? target, string fieldName)
    {
        if (target == null)
        {
            return "<null>";
        }

        try
        {
            return GetInstanceField(target, fieldName)?.ToString() ?? "<null>";
        }
        catch
        {
            return "<error>";
        }
    }

    private static string CollectionCount(object? target)
    {
        if (target is System.Collections.ICollection collection)
        {
            return collection.Count.ToString();
        }

        return target == null ? "<null>" : "<unknown>";
    }

    private static string GameObjectActive(object? target)
    {
        object? gameObject = GetInstanceProperty(target, "gameObject");
        object? activeSelf = GetInstanceProperty(gameObject, "activeSelf");
        return activeSelf?.ToString() ?? "<null>";
    }
}
