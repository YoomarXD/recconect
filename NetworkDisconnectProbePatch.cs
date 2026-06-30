using HarmonyLib;
using Photon.Realtime;

namespace Recconect;

[HarmonyPatch(typeof(NetworkConnect), nameof(NetworkConnect.OnDisconnected))]
internal static class NetworkConnectOnDisconnectedPatch
{
    private static void Prefix(DisconnectCause _cause)
    {
        NetworkStateSnapshot.Log("NetworkConnect.OnDisconnected:prefix", _cause);
    }

    private static void Postfix(DisconnectCause _cause)
    {
        NetworkStateSnapshot.Log("NetworkConnect.OnDisconnected:postfix", _cause);
    }
}

[HarmonyPatch(typeof(NetworkConnect), nameof(NetworkConnect.OnJoinedRoom))]
internal static class NetworkConnectOnJoinedRoomPatch
{
    private static void Postfix()
    {
        if (Recconect.ModConfig.LogJoinState.Value)
        {
            NetworkStateSnapshot.Log("NetworkConnect.OnJoinedRoom:postfix");
        }
    }
}

[HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.OnDisconnected))]
internal static class NetworkManagerOnDisconnectedPatch
{
    private static void Prefix(DisconnectCause cause)
    {
        NetworkStateSnapshot.Log("NetworkManager.OnDisconnected:prefix", cause);
    }

    private static void Postfix(DisconnectCause cause)
    {
        NetworkStateSnapshot.Log("NetworkManager.OnDisconnected:postfix", cause);
    }
}
