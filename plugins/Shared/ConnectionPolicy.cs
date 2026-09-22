namespace ValheimServerManager.ServerSupport;

internal static class ConnectionPolicy
{
    internal const int VanillaPlayerLimit = 10;

    // Optional features must not intercept Valheim's connection path unless
    // the administrator explicitly enabled behavior that requires it.
    internal static bool ShouldBufferWorldTraffic(bool serverCharactersEnabled) => serverCharactersEnabled;
    internal static bool ShouldOverridePlayerLimit(int maximumPlayers) => maximumPlayers != VanillaPlayerLimit;
}
