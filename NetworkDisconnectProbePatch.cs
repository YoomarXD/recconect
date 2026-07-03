using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Recconect;

internal static class PhotonNetworkDisconnectPatch
{
    private static bool installed;

    internal static void InstallDeferred(Harmony? harmony)
    {
        if (installed)
        {
            return;
        }

        if (harmony == null)
        {
            Recconect.Logger.LogWarning("Could not install PhotonNetwork.Disconnect guard because Harmony is not initialized.");
            return;
        }

        System.Reflection.MethodInfo? original = AccessTools.Method(typeof(PhotonNetwork), nameof(PhotonNetwork.Disconnect));
        System.Reflection.MethodInfo? prefix = AccessTools.Method(typeof(PhotonNetworkDisconnectPatch), nameof(Prefix));

        if (original == null || prefix == null)
        {
            Recconect.Logger.LogWarning("Could not install PhotonNetwork.Disconnect guard.");
            return;
        }

        harmony.Patch(original, prefix: new HarmonyMethod(prefix));
        installed = true;
        Recconect.Logger.LogInfo("Installed deferred PhotonNetwork.Disconnect guard.");
    }

    private static bool Prefix()
    {
        if (!ReconnectCoordinator.ShouldBlockPhotonDisconnect)
        {
            return true;
        }

        Recconect.Logger.LogWarning("Blocked PhotonNetwork.Disconnect during reconnect stabilization.");
        NetworkStateSnapshot.Log("PhotonNetwork.Disconnect:blocked");
        return false;
    }
}

[HarmonyPatch(typeof(NetworkConnect), nameof(NetworkConnect.OnConnectedToMaster))]
internal static class NetworkConnectOnConnectedToMasterPatch
{
    private static void Prefix()
    {
        RoomOptionsPatch.InstallDeferred(Recconect.Instance.Harmony);
        PhotonNetworkDisconnectPatch.InstallDeferred(Recconect.Instance.Harmony);
        ReconnectCoordinator.InstallEventHandler();
    }
}

[HarmonyPatch(typeof(NetworkConnect), nameof(NetworkConnect.OnDisconnected))]
internal static class NetworkConnectOnDisconnectedPatch
{
    private static bool Prefix(DisconnectCause _cause)
    {
        NetworkStateSnapshot.Log("NetworkConnect.OnDisconnected:prefix", _cause);
        return !ReconnectCoordinator.TryHandleDisconnect(_cause, "NetworkConnect.OnDisconnected");
    }

    private static void Postfix(DisconnectCause _cause)
    {
        NetworkStateSnapshot.Log("NetworkConnect.OnDisconnected:postfix", _cause);
        ReconnectCoordinator.ClearAfterVanillaDisconnect();
    }
}

[HarmonyPatch(typeof(NetworkConnect), nameof(NetworkConnect.OnJoinedRoom))]
internal static class NetworkConnectOnJoinedRoomPatch
{
    private static void Postfix()
    {
        ReconnectCoordinator.RecordJoinedRoom();

        if (Recconect.ModConfig.LogJoinState.Value)
        {
            NetworkStateSnapshot.Log("NetworkConnect.OnJoinedRoom:postfix");
        }
    }
}

[HarmonyPatch(typeof(NetworkConnect), nameof(NetworkConnect.OnJoinRoomFailed))]
internal static class NetworkConnectOnJoinRoomFailedPatch
{
    private static bool Prefix(short returnCode, string _cause)
    {
        Recconect.Logger.LogWarning($"NetworkConnect.OnJoinRoomFailed: returnCode={returnCode}, cause={_cause}");
        NetworkStateSnapshot.Log("NetworkConnect.OnJoinRoomFailed:prefix");

        if (!ReconnectCoordinator.IsReconnecting)
        {
            return true;
        }

        Recconect.Logger.LogInfo("Suppressing vanilla join-room failure handling during reconnect flow.");
        return false;
    }
}

[HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.OnDisconnected))]
internal static class NetworkManagerOnDisconnectedPatch
{
    private static bool Prefix(DisconnectCause cause)
    {
        NetworkStateSnapshot.Log("NetworkManager.OnDisconnected:prefix", cause);
        return !ReconnectCoordinator.TryHandleDisconnect(cause, "NetworkManager.OnDisconnected");
    }

    private static void Postfix(DisconnectCause cause)
    {
        NetworkStateSnapshot.Log("NetworkManager.OnDisconnected:postfix", cause);
        ReconnectCoordinator.ClearAfterVanillaDisconnect();
    }
}

[HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.OnPlayerEnteredRoom))]
internal static class NetworkManagerOnPlayerEnteredRoomPatch
{
    private static void Postfix(Player newPlayer)
    {
        ReconnectCoordinator.ScheduleHostRepairForEnteredPlayer(newPlayer);
    }
}

[HarmonyPatch(typeof(PlayerAvatar), "Awake")]
internal static class PlayerAvatarAwakePatch
{
    private static void Prefix(PlayerAvatar __instance)
    {
        ReconnectCoordinator.PreparePlayerAvatarAwake(__instance);
        ReconnectCoordinator.LogPlayerAvatarLifecycle("Awake:prefix", __instance);
    }

    private static void Postfix(PlayerAvatar __instance)
    {
        ReconnectCoordinator.LogPlayerAvatarLifecycle("Awake:postfix", __instance);
    }
}

[HarmonyPatch(typeof(PlayerAvatar), "Start")]
internal static class PlayerAvatarStartPatch
{
    private static void Prefix(PlayerAvatar __instance)
    {
        ReconnectCoordinator.LogPlayerAvatarLifecycle("Start:prefix", __instance);
    }

    private static void Postfix(PlayerAvatar __instance)
    {
        ReconnectCoordinator.LogPlayerAvatarLifecycle("Start:postfix", __instance);
    }
}

[HarmonyPatch(typeof(PlayerAvatar), "OnDestroy")]
internal static class PlayerAvatarOnDestroyPatch
{
    private static void Prefix(PlayerAvatar __instance)
    {
        ReconnectCoordinator.LogPlayerAvatarLifecycle("OnDestroy:prefix", __instance);
    }
}

[HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.Spawn))]
internal static class PlayerAvatarSpawnPatch
{
    private static void Prefix(PlayerAvatar __instance, Vector3 position, Quaternion rotation)
    {
        ReconnectCoordinator.LogPlayerAvatarSpawn("Spawn:prefix", __instance, position, rotation);
    }

    private static void Postfix(PlayerAvatar __instance)
    {
        ReconnectCoordinator.LogPlayerAvatarSpawnResult("Spawn:postfix", __instance);
    }
}

[HarmonyPatch(typeof(PlayerAvatar), "SpawnRPC")]
internal static class PlayerAvatarSpawnRpcPatch
{
    private static void Prefix(PlayerAvatar __instance, Vector3 position, Quaternion rotation, PhotonMessageInfo _info)
    {
        ReconnectCoordinator.LogPlayerAvatarSpawn("SpawnRPC:prefix", __instance, position, rotation, _info);
    }

    private static void Postfix(PlayerAvatar __instance)
    {
        ReconnectCoordinator.LogPlayerAvatarSpawnResult("SpawnRPC:postfix", __instance);
    }
}

[HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.PlayerSpawnedRPC))]
internal static class NetworkManagerPlayerSpawnedRpcPatch
{
    private static void Prefix(PhotonMessageInfo _info)
    {
        ReconnectCoordinator.LogNetworkSpawnMethod("NetworkManager.PlayerSpawnedRPC:prefix", _info);
    }
}

[HarmonyPatch(typeof(LevelGenerator), nameof(LevelGenerator.PlayerSpawn))]
internal static class LevelGeneratorPlayerSpawnPatch
{
    private static void Prefix()
    {
        ReconnectCoordinator.LogNetworkSpawnMethod("LevelGenerator.PlayerSpawn:prefix");
    }

    private static void Postfix()
    {
        ReconnectCoordinator.LogNetworkSpawnMethod("LevelGenerator.PlayerSpawn:postfix");
    }
}

[HarmonyPatch(typeof(LevelGenerator), "PlayerSpawnedRPC")]
internal static class LevelGeneratorPlayerSpawnedRpcPatch
{
    private static void Prefix(PhotonMessageInfo _info)
    {
        ReconnectCoordinator.LogNetworkSpawnMethod("LevelGenerator.PlayerSpawnedRPC:prefix", _info);
    }
}
