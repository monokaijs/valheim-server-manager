namespace ValheimServerManager.ServerSupport;

internal static class ConnectionPolicy
{
    internal const int VanillaPlayerLimit = 10;

    // Hold world data while either admission requirement completes its handshake.
    internal static bool ShouldBufferWorldTraffic(bool serverCharactersEnabled, bool requiredModsEnabled) =>
        serverCharactersEnabled || requiredModsEnabled;
    internal static bool ShouldOverridePlayerLimit(int maximumPlayers) => maximumPlayers != VanillaPlayerLimit;
}
