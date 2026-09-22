using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class ServerNoticeTests
{
    [Fact]
    public void PastedWindowsLineEndingsAreNormalizedBeforeValidation()
    {
        var templates = ServerMessageService.Validate(ServerMessageService.Defaults with { Kick = "Reason: {reason}\r\nContact an admin.\rThen reconnect." });
        Assert.Equal("Reason: {reason}\nContact an admin.\nThen reconnect.", templates.Kick);
    }

    [Fact]
    public void PlayerNoticesRejectMoreThanFourLines()
    {
        Assert.Throws<ArgumentException>(() => ServerMessageService.Validate(ServerMessageService.Defaults with { Kick = "1\n2\n3\n4\n5" }));
    }
}
