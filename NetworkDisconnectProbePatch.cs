using HarmonyLib;
using Photon.Realtime;

namespace Recconect;

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
