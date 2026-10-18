using SingPlus.Contracts;
using YAKSys_Hybrid_CPU.Core;
using System.Reflection;
using System.Collections;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed partial class ChildClosureTupleTests
{
    [Theory]
    [InlineData("exact")][InlineData("child-copy")][InlineData("device-copy")][InlineData("root-copy")]
    [InlineData("bind-throw")][InlineData("close-throw")][InlineData("close-foreign")][InlineData("late-device-copy")]
    [InlineData("history-loss")][InlineData("late-bind-device-copy")]
    [InlineData("source-aba")]
    public void ProviderVirtualIoUsesOriginalFactoriesAndRetainedClosure(string fault)
    {
        var runtime = new NeutralDomainRuntimeFacade();
        var childRuntime = new ClosureProvider();
        var provider = new HybridCpuPlatformAuthorityProvider(runtime, childRuntime);
        var parent = provider.BindDomain(new PlatformDomainIdentity(new DomainId(10), new ProcessHandle(new ProcessId(12), 7))).Value!;
        const PlatformChildAuthorityClass authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Io;
        var child = provider.CreateChildDomain(parent, new(new(1,4096),new(authority,authority))).Value!;
        var device = provider.BindDevice(parent, new("device:original-io"), PlatformDeviceRights.Read).Value!;
        var request = new PlatformVirtualIoRequest(child, device, new(PlatformDeviceRights.Read,512));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = fault == "child-copy" ? "_providerChildren" : fault == "root-copy" ? "_domains" : "_deviceLeases";
        var routes = (IDictionary)provider.GetType().GetField(field, flags)!.GetValue(provider)!;
        var key = Assert.Single(routes.Keys.Cast<object>());
        var original = routes[key]!;
        void CopyOwner() => routes[key] = typeof(object).GetMethod("MemberwiseClone",flags)!.Invoke(original,null)!;
        if (fault is "child-copy" or "device-copy" or "root-copy") CopyOwner();
        childRuntime.IoFault = fault;
        childRuntime.BeforeIoBind = () =>
        {
            Assert.False(Monitor.IsEntered(provider.GetType().GetField("_childCreateCommitGate",flags)!.GetValue(provider)!));
            Assert.False(provider.CloseChildDomain(child).IsSuccess);
            Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.False(provider.BindVirtualIo(request).IsSuccess);
            if (fault == "late-bind-device-copy") CopyOwner();
        };
        var result = provider.BindVirtualIo(request);
        if (fault is "child-copy" or "device-copy" or "root-copy")
        { Assert.False(result.IsSuccess); Assert.Equal(0,childRuntime.IoBinds); return; }
        Assert.Equal(1,childRuntime.IoBinds);
        if (fault is "bind-throw" or "late-bind-device-copy")
        {
            routes[key] = original;
            Assert.False(result.IsSuccess); Assert.False(provider.BindVirtualIo(request).IsSuccess);
            Assert.False(provider.CloseChildDomain(child).IsSuccess); Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.Equal(1,childRuntime.IoBinds); return;
        }
        Assert.True(result.IsSuccess);
        if (fault == "history-loss")
        {
            ((IDictionary)provider.GetType().GetField("_originalProviderVirtualIo", flags)!.GetValue(provider)!).Clear();
            Assert.False(provider.RevokeVirtualIo(result.Value!).IsSuccess);
            Assert.False(provider.CloseChildDomain(child).IsSuccess); Assert.False(provider.RevokeDevice(device).IsSuccess);
            Assert.False(provider.BindVirtualIo(request).IsSuccess); Assert.Equal(0,childRuntime.IoCloses); return;
        }
        if (fault == "late-device-copy") childRuntime.AfterIoClose = CopyOwner;
        var sourceField = provider.GetType().GetField("_originalNativeMappingIncarnation", flags)!;
        var originalSource = sourceField.GetValue(provider);
        if (fault == "source-aba") childRuntime.AfterIoClose = () => sourceField.SetValue(provider, Guid.NewGuid());
        var closed = provider.RevokeVirtualIo(result.Value!);
        Assert.Equal(fault == "exact",closed.IsSuccess);
        Assert.Equal(1,childRuntime.IoCloses);
        if (fault == "late-device-copy")
        {
            routes[key] = original; childRuntime.AfterIoClose = null;
            Assert.True(provider.RevokeVirtualIo(result.Value!).IsSuccess); // Same retained raw; no native repeat.
        }
        if (fault == "source-aba") { sourceField.SetValue(provider, originalSource); childRuntime.AfterIoClose = null; }
        if (fault is "close-throw" or "close-foreign" or "source-aba")
        {
            Assert.False(provider.RevokeVirtualIo(result.Value!).IsSuccess);
            Assert.False(provider.CloseChildDomain(child).IsSuccess); Assert.False(provider.RevokeDevice(device).IsSuccess);
        }
        else
        {
            CopyOwner(); Assert.True(provider.RevokeVirtualIo(result.Value!).IsSuccess); // Already consumed, no active permission renewed.
            routes[key] = original;
            Assert.True(provider.RevokeDevice(device).IsSuccess);
            Assert.True(provider.CloseChildDomain(child).IsSuccess);
        }
        Assert.Equal(1,childRuntime.IoCloses);
    }
}
