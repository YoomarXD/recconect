using System;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;

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
            roomState,
            localPlayerState,
            SteamLobbyState());
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
        return target?.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
    }
}
