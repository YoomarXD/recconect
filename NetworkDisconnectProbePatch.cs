using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;

namespace Recconect;

[HarmonyPatch(typeof(PhotonNetwork), nameof(PhotonNetwork.Disconnect))]
internal static class PhotonNetworkDisconnectPatch
{
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
