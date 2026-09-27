using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class DiscordActivitySettingsTests
{
    [Fact]
    public void AcceptsSupportedVariablesAndHttpsArtwork()
    {
        var settings = DiscordActivitySettingsService.Validate(new DiscordActivitySettings(
            "123456789012345678", "{server} in {world}", "{region} · {players}/{maxPlayers} · {freeSlots} free",
            "https://example.com/valheim.png"));

        Assert.Equal("https://example.com/valheim.png", settings.ImageUrl);
        Assert.Contains("{freeSlots}", settings.StateTemplate);
    }

    [Theory]
    [InlineData("{unknown}", "{players} online", "https://example.com/image.png")]
    [InlineData("{server", "{players} online", "https://example.com/image.png")]
    [InlineData("{server}", "{players} online", "http://example.com/image.png")]
    public void RejectsMalformedTemplatesAndInsecureArtwork(string details, string state, string image)
    {
        Assert.Throws<ArgumentException>(() => DiscordActivitySettingsService.Validate(new DiscordActivitySettings(
            "123456789012345678", details, state, image)));
    }
}
