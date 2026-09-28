using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerManager.Data;
using ValheimServerManager.Services;
using Xunit;

namespace ValheimServerManager.Tests;

public sealed class VoiceChatSettingsTests
{
    [Fact]
    public async Task DefaultsCanBeOverriddenAndRangeIsValidated()
    {
        var root = Path.Combine(Path.GetTempPath(), "vsm-voice-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<ManagerDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "manager.db")};Pooling=False"));
            services.AddSingleton<VoiceChatSettingsService>();
            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<ManagerDbContext>().Database.EnsureCreatedAsync();
            var settings = provider.GetRequiredService<VoiceChatSettingsService>();
            Assert.Equal(new VoiceChatSettings(true, 40), await settings.Get());
            await settings.Set(new VoiceChatSettings(false, 30));
            Assert.Equal(new VoiceChatSettings(false, 30), await settings.Get());
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => settings.Set(new VoiceChatSettings(true, 101)));
        }
        finally { Directory.Delete(root, true); }
    }
}
