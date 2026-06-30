using BepInEx.Configuration;

namespace Recconect;

internal sealed class RecconectConfig
{
    internal ConfigEntry<bool> DiagnosticsEnabled { get; }
    internal ConfigEntry<bool> LogJoinState { get; }
    internal ConfigEntry<bool> ExperimentalReconnectEnabled { get; }
    internal ConfigEntry<int> MaxReconnectAttempts { get; }
    internal ConfigEntry<float> ReconnectAttemptDelaySeconds { get; }

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
            "Reserved for future reconnect attempts. Keep false until disconnect diagnostics are validated.");

        MaxReconnectAttempts = config.Bind(
            "Reconnect",
            "MaxReconnectAttempts",
            3,
            new ConfigDescription(
                "Reserved maximum reconnect attempts once reconnect behavior is implemented.",
                new AcceptableValueRange<int>(0, 10)));

        ReconnectAttemptDelaySeconds = config.Bind(
            "Reconnect",
            "ReconnectAttemptDelaySeconds",
            2f,
            new ConfigDescription(
                "Reserved delay between reconnect attempts once reconnect behavior is implemented.",
                new AcceptableValueRange<float>(0.25f, 30f)));
    }
}
