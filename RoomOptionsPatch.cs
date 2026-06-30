using System;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;

namespace Recconect;

internal static class RoomOptionsPatch
{
    internal static void Configure(ref RoomOptions? roomOptions, string source)
    {
        if (!Recconect.ModConfig.ExperimentalReconnectEnabled.Value ||
            !Recconect.ModConfig.ConfigureRoomTtlOnCreate.Value)
        {
            return;
        }

        roomOptions ??= new RoomOptions();

        int playerTtl = Recconect.ModConfig.PlayerTtlMilliseconds.Value;
        int emptyRoomTtl = Recconect.ModConfig.EmptyRoomTtlMilliseconds.Value;

        if (roomOptions.PlayerTtl < playerTtl)
        {
            roomOptions.PlayerTtl = playerTtl;
        }

        if (roomOptions.EmptyRoomTtl < emptyRoomTtl)
        {
            roomOptions.EmptyRoomTtl = emptyRoomTtl;
        }

        Recconect.Logger.LogInfo($"{source}: PlayerTtl={roomOptions.PlayerTtl}, EmptyRoomTtl={roomOptions.EmptyRoomTtl}");
    }
}

[HarmonyPatch(
    typeof(PhotonNetwork),
    nameof(PhotonNetwork.CreateRoom),
    new[] { typeof(string), typeof(RoomOptions), typeof(TypedLobby), typeof(string[]) })]
internal static class PhotonNetworkCreateRoomPatch
{
    private static void Prefix(ref RoomOptions? __1)
    {
        RoomOptionsPatch.Configure(ref __1, "PhotonNetwork.CreateRoom");
    }
}

[HarmonyPatch(
    typeof(PhotonNetwork),
    nameof(PhotonNetwork.JoinOrCreateRoom),
    new[] { typeof(string), typeof(RoomOptions), typeof(TypedLobby), typeof(string[]) })]
internal static class PhotonNetworkJoinOrCreateRoomPatch
{
    private static void Prefix(ref RoomOptions? __1)
    {
        RoomOptionsPatch.Configure(ref __1, "PhotonNetwork.JoinOrCreateRoom");
    }
}

[HarmonyPatch(
    typeof(PhotonNetwork),
    nameof(PhotonNetwork.JoinRandomOrCreateRoom),
    new[]
    {
        typeof(ExitGames.Client.Photon.Hashtable),
        typeof(byte),
        typeof(MatchmakingMode),
        typeof(TypedLobby),
        typeof(string),
        typeof(string),
        typeof(RoomOptions),
        typeof(string[])
    })]
internal static class PhotonNetworkJoinRandomOrCreateRoomPatch
{
    private static void Prefix(ref RoomOptions? __6)
    {
        RoomOptionsPatch.Configure(ref __6, "PhotonNetwork.JoinRandomOrCreateRoom");
    }
}
