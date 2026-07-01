using System;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;

namespace Recconect;

internal static class RoomOptionsPatch
{
    private static bool installed;
    private static bool skippedLogged;

    internal static void InstallDeferred(Harmony? harmony)
    {
        if (installed)
        {
            return;
        }

        if (!Recconect.ModConfig.ExperimentalReconnectEnabled.Value)
        {
            LogSkippedOnce("Experimental reconnect disabled; Photon room TTL patches were not installed.");
            return;
        }

        if (!Recconect.ModConfig.ConfigureRoomTtlOnCreate.Value)
        {
            LogSkippedOnce("Room TTL patching disabled by config; Photon room TTL patches were not installed.");
            return;
        }

        if (harmony == null)
        {
            Recconect.Logger.LogWarning("Could not install room TTL patches because Harmony is not initialized.");
            return;
        }

        Recconect.Logger.LogInfo("Installing deferred Photon room TTL patches after connection to Master Server.");
        Patch(harmony, nameof(PhotonNetwork.CreateRoom), new[] { typeof(string), typeof(RoomOptions), typeof(TypedLobby), typeof(string[]) }, nameof(CreateRoomPrefix));
        Patch(harmony, nameof(PhotonNetwork.JoinOrCreateRoom), new[] { typeof(string), typeof(RoomOptions), typeof(TypedLobby), typeof(string[]) }, nameof(JoinOrCreateRoomPrefix));
        Patch(
            harmony,
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
            },
            nameof(JoinRandomOrCreateRoomPrefix));
        installed = true;
    }

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

        if (Recconect.ModConfig.PreservePlayerObjectsDuringReconnect.Value && roomOptions.CleanupCacheOnLeave)
        {
            roomOptions.CleanupCacheOnLeave = false;
        }

        Recconect.Logger.LogInfo(
            $"{source}: PlayerTtl={roomOptions.PlayerTtl}, EmptyRoomTtl={roomOptions.EmptyRoomTtl}, CleanupCacheOnLeave={roomOptions.CleanupCacheOnLeave}");
    }

    private static void Patch(Harmony harmony, string methodName, Type[] argumentTypes, string prefixName)
    {
        System.Reflection.MethodInfo? original = AccessTools.Method(typeof(PhotonNetwork), methodName, argumentTypes);
        System.Reflection.MethodInfo? prefix = AccessTools.Method(typeof(RoomOptionsPatch), prefixName);

        if (original == null || prefix == null)
        {
            Recconect.Logger.LogWarning($"Could not install room TTL patch for {methodName}.");
            return;
        }

        harmony.Patch(original, prefix: new HarmonyMethod(prefix));
        Recconect.Logger.LogInfo($"Installed room TTL patch for {methodName}.");
    }

    private static void LogSkippedOnce(string message)
    {
        if (skippedLogged)
        {
            return;
        }

        skippedLogged = true;
        Recconect.Logger.LogInfo(message);
    }

    private static void CreateRoomPrefix(ref RoomOptions? __1)
    {
        RoomOptionsPatch.Configure(ref __1, "PhotonNetwork.CreateRoom");
    }

    private static void JoinOrCreateRoomPrefix(ref RoomOptions? __1)
    {
        RoomOptionsPatch.Configure(ref __1, "PhotonNetwork.JoinOrCreateRoom");
    }

    private static void JoinRandomOrCreateRoomPrefix(ref RoomOptions? __6)
    {
        RoomOptionsPatch.Configure(ref __6, "PhotonNetwork.JoinRandomOrCreateRoom");
    }
}
