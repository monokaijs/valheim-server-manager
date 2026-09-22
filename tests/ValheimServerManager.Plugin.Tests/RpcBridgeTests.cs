using ValheimServerManager.Bootstrap;
using Xunit;

namespace ValheimServerManager.Plugin.Tests;

public sealed class RpcBridgeTests
{
    [Fact]
    public void EarlyNoticesCanRegisterBeforeTheClientRuntimeIsInstalled()
    {
        var rpc = new FakeRpc();
        object? sender = null;
        string? notice = null;
        RpcReflectionBridge.RegisterStrings(rpc, "VSM_AdminNotice", (source, title, message) =>
        {
            sender = source;
            notice = title + ": " + message;
        });
        ((Action<FakeRpc, string, string>)rpc.Callbacks["VSM_AdminNotice"])(rpc, "Whitelist approval required", "Contact an administrator.");
        Assert.Same(rpc, sender);
        Assert.Equal("Whitelist approval required: Contact an administrator.", notice);
    }

    [Fact]
    public void CurrentAndLegacyManifestCallbacksPreserveTheirSource()
    {
        var rpc = new FakeRpc();
        object? sender = null;
        string? manifest = null;
        RpcReflectionBridge.RegisterString(rpc, "Manifest", (source, json) => { sender = source; manifest = json; });
        ((Action<FakeRpc, string>)rpc.Callbacks["Manifest"])(rpc, "{}");
        Assert.Same(rpc, sender);
        Assert.Equal("{}", manifest);
    }

    public sealed class FakeRpc
    {
        public Dictionary<string, Delegate> Callbacks { get; } = new();
        public void Register<T>(string name, Action<FakeRpc, T> callback) => Callbacks[name] = callback;
        public void Register<T, U>(string name, Action<FakeRpc, T, U> callback) => Callbacks[name] = callback;
    }
}
