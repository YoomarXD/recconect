using BepInEx.Configuration;

namespace Recconect;

internal sealed class RecconectConfig
{
    internal ConfigEntry<bool> DiagnosticsEnabled { get; }
    internal ConfigEntry<bool> LogJoinState { get; }
    internal ConfigEntry<bool> ExperimentalReconnectEnabled { get; }
    internal ConfigEntry<bool> ConfigureRoomTtlOnCreate { get; }
    internal ConfigEntry<bool> AllowHostReconnect { get; }
    internal ConfigEntry<int> PlayerTtlMilliseconds { get; }
    internal ConfigEntry<int> EmptyRoomTtlMilliseconds { get; }
    internal ConfigEntry<int> MaxReconnectAttempts { get; }
    internal ConfigEntry<float> ReconnectAttemptDelaySeconds { get; }
    internal ConfigEntry<float> ReconnectAttemptTimeoutSeconds { get; }
    internal ConfigEntry<string> EligibleDisconnectCauses { get; }

    internal RecconectConfig(ConfigFile config)
    {
        DiagnosticsEnabled = config.Bind(
            "Diagnostics",
            "Enabled",
            true,
            "Log Photon and lobby state around connection events.");

        LogJoinState = config.Bind(
            "Diagnostics",
            "LogJoinState",
            true,
            "Log state snapshots when the game reports a successful room join.");

        ExperimentalReconnectEnabled = config.Bind(
            "Reconnect",
            "ExperimentalReconnectEnabled",
            false,
            "Opt-in experimental reconnect attempts for unstable-network disconnects.");

        ConfigureRoomTtlOnCreate = config.Bind(
            "Reconnect",
            "ConfigureRoomTtlOnCreate",
            true,
            "When experimental reconnect is enabled, set nonzero Photon room TTL values for rooms this client creates.");

        AllowHostReconnect = config.Bind(
            "Reconnect",
            "AllowHostReconnect",
            false,
            "Allow reconnect attempts when this client was the master client at the last successful join. Keep false until host behavior is tested.");

        PlayerTtlMilliseconds = config.Bind(
            "Reconnect",
            "PlayerTtlMilliseconds",
            30000,
            new ConfigDescription(
                "Photon PlayerTtl for rooms created while experimental reconnect is enabled. Rejoin usually requires this to be greater than zero.",
                new AcceptableValueRange<int>(0, 300000)));

        EmptyRoomTtlMilliseconds = config.Bind(
            "Reconnect",
            "EmptyRoomTtlMilliseconds",
            60000,
            new ConfigDescription(
                "Photon EmptyRoomTtl for rooms created while experimental reconnect is enabled.",
                new AcceptableValueRange<int>(0, 300000)));

        MaxReconnectAttempts = config.Bind(
            "Reconnect",
            "MaxReconnectAttempts",
            3,
            new ConfigDescription(
                "Maximum reconnect attempts after an eligible disconnect.",
                new AcceptableValueRange<int>(0, 10)));

        ReconnectAttemptDelaySeconds = config.Bind(
            "Reconnect",
            "ReconnectAttemptDelaySeconds",
            2f,
            new ConfigDescription(
                "Delay between reconnect attempts.",
                new AcceptableValueRange<float>(0.25f, 30f)));

        ReconnectAttemptTimeoutSeconds = config.Bind(
            "Reconnect",
            "ReconnectAttemptTimeoutSeconds",
            12f,
            new ConfigDescription(
                "Maximum time to wait for a single reconnect attempt to reach a joined room.",
                new AcceptableValueRange<float>(2f, 60f)));

        EligibleDisconnectCauses = config.Bind(
            "Reconnect",
            "EligibleDisconnectCauses",
            "ClientTimeout,ServerTimeout,Exception,ExceptionOnConnect",
            "Comma-separated Photon DisconnectCause names that may trigger reconnect when experimental reconnect is enabled.");
    }
}
