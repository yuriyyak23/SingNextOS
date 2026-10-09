using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformIrqBindingTests
{
    [Fact]
    public void ExhaustedEventSequenceRepeatedlyRefusesBeforeActualIrqCompletion()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1400, 1900);
        var region = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/event-last-sequence", "last-sequence", IrqTriggerMode.Edge);
        var binding = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = (KernelEventRegistry)typeof(RuntimeKernel).GetField("_kernelEvents", flags)!.GetValue(kernel)!;
        typeof(KernelEventRegistry).GetField("_nextEventSequence", flags)!.SetValue(registry, ulong.MaxValue);
        Assert.True(provider.Signal("last-sequence"));
        Assert.True(kernel.PollPlatformInterrupt(subject, binding).IsSuccess);
        Assert.True(kernel.ConsumeKernelEvent(subject, endpoint).IsSuccess);
        Assert.Equal(1, provider.CompleteCalls);
        Assert.True(provider.Signal("last-sequence"));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(KernelError.CapacityExhausted, kernel.PollPlatformInterrupt(subject, binding).Error);
            Assert.Equal(1, provider.CompleteCalls);
            Assert.True(provider.IsPending("last-sequence"));
            Assert.False(kernel.ConsumeKernelEvent(subject, endpoint).IsSuccess);
        }
        Assert.True(kernel.RevokePlatformInterrupt(subject, binding).IsSuccess);
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Fact]
    public void ExhaustedEventEndpointIdentityDoesNotRestartAfterRefusal()
    {
        var kernel = new RuntimeKernel();
        var (_, subject) = TestFixtures.Create(kernel, 1401, 1901);
        var first = kernel.CreateKernelEventEndpoint(subject).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = (KernelEventRegistry)typeof(RuntimeKernel).GetField("_kernelEvents", flags)!.GetValue(kernel)!;
        typeof(KernelEventRegistry).GetField("_nextEndpointId", flags)!.SetValue(registry, ulong.MaxValue);
        var last = kernel.CreateKernelEventEndpoint(subject).Value!;
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(KernelError.CapacityExhausted, kernel.CreateKernelEventEndpoint(subject).Error);
        Assert.True(registry.Validate(subject, first).IsSuccess);
        Assert.True(registry.Validate(subject, last).IsSuccess);
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LateIrqBindEndpointRouteLossBalancesOriginalAdmissionAndRetainsSource(bool copied)
    {
        var provider = new IrqProvider { IrqRevokeStatus = PlatformAuthorityStatus.Denied };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1399, 1899);
        var region = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/late-endpoint-route", "pending", IrqTriggerMode.Edge);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = (KernelEventRegistry)typeof(RuntimeKernel).GetField("_kernelEvents", flags)!.GetValue(kernel)!;
        var routes = (global::System.Collections.IDictionary)typeof(KernelEventRegistry).GetField("_endpoints", flags)!.GetValue(registry)!;
        var original = routes[endpoint.EndpointId]!;
        provider.AfterIrqBindEffect = () =>
        {
            if (copied) routes[endpoint.EndpointId] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            else routes.Remove(endpoint.EndpointId);
        };
        Assert.False(kernel.BindPlatformInterrupt(subject, device, capability, endpoint).IsSuccess);
        Assert.Equal(0, original.GetType().GetProperty("PendingIrqAdmissions")!.GetValue(original));
        Assert.NotEmpty(registry.SnapshotIrqSources());
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LostEventRouteCannotHideOriginalStageFromActualReclaim(bool copied)
    {
        var kernel = new RuntimeKernel();
        var (_, subject) = TestFixtures.Create(kernel, 1398, 1898);
        var region = kernel.AllocateRegion(subject, 4096).Value!;
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = (KernelEventRegistry)typeof(RuntimeKernel).GetField("_kernelEvents", flags)!.GetValue(kernel)!;
        var staged = registry.Stage(subject, endpoint, KernelEventClass.ExternalSignal, "original-stage").Value!;
        var waiter = kernel.WaitForKernelEventAsync(subject, endpoint).AsTask();
        var routes = (global::System.Collections.IDictionary)typeof(KernelEventRegistry).GetField("_endpoints", flags)!.GetValue(registry)!;
        var original = routes[endpoint.EndpointId]!;
        if (copied)
        {
            var foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
            original.GetType().GetProperty("Staged")!.SetValue(foreign, null);
            original.GetType().GetProperty("Waiter")!.SetValue(foreign, null);
            routes[endpoint.EndpointId] = foreign;
            Assert.False(registry.Validate(subject, endpoint).IsSuccess);
        }
        else routes.Remove(endpoint.EndpointId);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await waiter.WaitAsync(TimeSpan.FromSeconds(10)));
        routes[endpoint.EndpointId] = original;
        Assert.True(registry.RollbackExact(subject, staged).IsSuccess);
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    [InlineData(false, 5)] [InlineData(true, 5)]
    public async Task EndpointOnlyOriginalIrqSourceRefusesActualCapabilityCleanupWithoutBridgeRouteRestore(bool foreign, int outcome)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1397, 1897);
        var region = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/endpoint-only-original", "pending", IrqTriggerMode.Edge);
        if (outcome == 1) provider.ThrowOnIrqBind = true;
        if (outcome == 2) provider.RewriteBindReceipt = receipt => receipt with { Source = new("foreign", receipt.Source.Trigger) };
        if (outcome == 5) provider.IrqBindStatus = PlatformAuthorityStatus.Denied;
        var bind = kernel.BindPlatformInterrupt(subject, device, capability, endpoint);
        Assert.Equal(outcome is 0 or 3 or 4, bind.IsSuccess);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var bridge = kernel.PlatformAuthority;
        var irqs = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(bridge)!;
        var devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(bridge)!;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(bridge)!;
        var originalDevice = devices[device.LeaseId]!;
        var originalRoot = domains[device.DomainBinding.BindingId]!;
        var originals = irqs.Values.Cast<object>().ToArray();
        object Clone(object owner) => typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(owner, null)!;
        void DropRoutes()
        {
            irqs.Clear(); devices.Clear(); domains.Clear();
            if (!foreign) return;
            var foreignRoot = Clone(originalRoot);
            originalRoot.GetType().GetProperty("IrqSources")!.SetValue(foreignRoot, new HashSet<object>(ReferenceEqualityComparer.Instance));
            domains[device.DomainBinding.BindingId] = foreignRoot;
            var foreignDevice = Clone(originalDevice);
            originalDevice.GetType().GetProperty("BindParentSource")!.SetValue(foreignDevice, foreignRoot);
            originalDevice.GetType().GetProperty("UnresolvedIrqCapabilityId")!.SetValue(foreignDevice, null);
            originalDevice.GetType().GetProperty("PlatformClosed")!.SetValue(foreignDevice, true);
            devices[device.LeaseId] = foreignDevice;
            foreach (var original in originals)
            {
                var replacement = Clone(original);
                original.GetType().GetProperty("PlatformClosed")!.SetValue(replacement, true);
                var binding = (PlatformIrqBinding)original.GetType().GetProperty("Binding")!.GetValue(original)!;
                irqs[binding.BindingId] = replacement;
            }
        }
        if (outcome is 3 or 4)
        {
            provider.AfterIrqRevokeEffect = () =>
            {
                DropRoutes();
                if (outcome == 4) throw new IOException("Original revoke reply lost after all bridge routes disappeared");
            };
            Assert.False(kernel.RevokePlatformInterrupt(subject, bind.Value!).IsSuccess);
            provider.AfterIrqRevokeEffect = null;
        }
        else DropRoutes();
        using var cancellation = new CancellationTokenSource();
        var waiting = kernel.WaitForKernelEventAsync(subject, endpoint, cancellation.Token).AsTask();
        int revokes = provider.IrqRevokeCalls;
        var unrelated = Mint(kernel, subject, ResourceKind.Irq, "unrelated-irq-cleanup", CapabilityRights.Signal);
        Assert.True(kernel.RevokeCapability(unrelated).IsSuccess);
        for (int i = 0; i < 2; i++)
        {
            var result = kernel.RevokeCapability(capability);
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Contains("IRQ effect lacks an exact closure receipt", result.Message);
        }
        Assert.Equal(revokes, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.False(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.False(waiting.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot(), x => x.Handle == region.Handle).State);
        Assert.Equal(revokes, provider.IrqRevokeCalls);
        foreach (var original in originals) Assert.Equal(false, original.GetType().GetProperty("PlatformClosed")!.GetValue(original));
    }

    [Fact]
    public void ExactIrqClosureRemovesEndpointCorrelationBeforeCapabilityCleanup()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1398, 1898);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/endpoint-exact-cleanup", "pending", IrqTriggerMode.Edge);
        var binding = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        Assert.True(kernel.RevokePlatformInterrupt(subject, binding).IsSuccess);
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.True(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public void OriginalIrqCleanupSourceLossRefusesEffectOrEndpointRelease(int loss, bool afterEffect)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1393, 1893);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/irq-original-source", "pending", IrqTriggerMode.Edge);
        var binding = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = records[binding.BindingId]!;
        var owners = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField(loss < 2 ? "_deviceLeases" : "_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        object key = loss < 2 ? device.LeaseId : device.DomainBinding.BindingId;
        var source = owners[key]!;
        void LoseSource()
        {
            if (loss % 2 == 0) owners.Remove(key);
            else owners[key] = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(source, null)!;
        }
        if (afterEffect) provider.AfterIrqRevokeEffect = LoseSource;
        else LoseSource();
        Assert.False(kernel.RevokePlatformInterrupt(subject, binding).IsSuccess);
        Assert.Equal(afterEffect ? 1 : 0, provider.IrqRevokeCalls);
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        if (afterEffect) Assert.NotNull(original.GetType().GetProperty("RevokeObservation")!.GetValue(original));
        else Assert.Null(original.GetType().GetProperty("RevokeObservation")!.GetValue(original));
        provider.AfterIrqRevokeEffect = null;
        Assert.False(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(afterEffect ? 1 : 0, provider.IrqRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 0, false, false)]
    [InlineData(PlatformAuthorityStatus.Denied, 0, false, false)]
    [InlineData(PlatformAuthorityStatus.Stale, 0, false, false)]
    [InlineData((PlatformAuthorityStatus)999, 0, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 1, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 2, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 0, true, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, 0, false, true)]
    public void FailedOriginalIrqBindAfterEffectRetainsRawAndPinsActualConsumers(
        PlatformAuthorityStatus status, int ownerLoss, bool sourceThrows, bool lostReply)
    {
        var provider = new IrqProvider { IrqBindReplyStatus = status };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1395, 1895);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/failed-original-irq-bind", "pending", IrqTriggerMode.Edge);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = devices[device.LeaseId]!;
        var originalDomain = domains[device.DomainBinding.BindingId]!;
        object? foreign = null;
        object? Read(string name) => original.GetType().GetProperty(name)!.GetValue(original);
        provider.BeforeIrqBindEffect = () =>
        {
            Assert.Equal(true, Read("IrqBindMayHaveEffect"));
            Assert.Same(originalDomain, Read("IrqBindParent"));
            Assert.NotNull(Read("IrqBindEndpointSource"));
            Assert.Null(Read("IrqBindObservation"));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
        };
        provider.AfterIrqBindEffect = () =>
        {
            Assert.True(provider.Signal("pending")); // Original provider route remains live.
            if (ownerLoss == 1) devices.Remove(device.LeaseId);
            if (ownerLoss == 2)
            {
                foreign = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                devices[device.LeaseId] = foreign;
            }
            if (sourceThrows) provider.IncarnationRead = () =>
            {
                Assert.Equal(status, Assert.IsType<PlatformAuthorityResult<PlatformProviderIrqBinding>>(Read("IrqBindObservation")).Status);
                throw new IOException("Late source observation failed after bind reply.");
            };
            if (lostReply) throw new IOException("Original IRQ bind reply lost after effect.");
        };
        Assert.False(kernel.BindPlatformInterrupt(subject, device, capability, endpoint).IsSuccess);
        Assert.Equal(true, Read("IrqBindMayHaveEffect"));
        Assert.Equal(true, Read("FaultPinned"));
        var sourceReservation = Read("IrqBindEndpointSource")!;
        Assert.Same(original, sourceReservation.GetType().GetField("_source", flags)!.GetValue(sourceReservation));
        Assert.Equal(0, Read("PendingIrqBinds"));
        if (lostReply) Assert.Null(Read("IrqBindObservation"));
        else Assert.Equal(status, Assert.IsType<PlatformAuthorityResult<PlatformProviderIrqBinding>>(Read("IrqBindObservation")).Status);
        if (foreign is not null) Assert.Null(foreign.GetType().GetProperty("IrqBindObservation")!.GetValue(foreign));
        provider.IncarnationRead = null;
        provider.AfterIrqBindEffect = null;
        provider.BeforeIrqBindEffect = null;
        provider.IrqBindReplyStatus = null;
        Assert.False(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.False(kernel.BindPlatformInterrupt(subject, device, capability, endpoint).IsSuccess);
        if (ownerLoss == 0) Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.BindCalls);
        Assert.Equal(0, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
    }
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    public async Task LostOriginalIrqRouteCannotReleaseEndpointOrCancelWaiter(bool foreignOwner, int outcome)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1393, 1893);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/endpoint-original-route", "pending", IrqTriggerMode.Edge);
        var binding = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        using var cancellation = new CancellationTokenSource();
        var waiter = kernel.WaitForKernelEventAsync(subject, endpoint, cancellation.Token).AsTask();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = records[binding.BindingId]!;
        if (outcome == 1) provider.IrqRevokeReplyStatus = PlatformAuthorityStatus.NotAccepted;
        provider.AfterIrqRevokeEffect = () =>
        {
            records.Remove(binding.BindingId);
            Assert.False(provider.Signal("pending"));
            if (outcome == 2) throw new IOException("Original revoke reply lost.");
        };
        Assert.False(kernel.RevokePlatformInterrupt(subject, binding).IsSuccess);
        provider.AfterIrqRevokeEffect = null;
        provider.IrqRevokeReplyStatus = null;
        if (foreignOwner)
        {
            var foreign = Activator.CreateInstance(original.GetType(), binding with { Generation = new(binding.Generation.Value + 1) },
                original.GetType().GetProperty("ProviderBinding")!.GetValue(original), capability)!;
            // A copied receipt/source token is not the original cleanup consumer.
            foreign.GetType().GetProperty("EndpointSource")!.SetValue(foreign, original.GetType().GetProperty("EndpointSource")!.GetValue(original));
            foreign.GetType().GetProperty("RevokeParent")!.SetValue(foreign, original.GetType().GetProperty("RevokeParent")!.GetValue(original));
            foreign.GetType().GetProperty("ProviderClosureConfirmed")!.SetValue(foreign, true);
            records[binding.BindingId] = foreign;
        }
        Assert.False(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.False(waiter.IsCompleted);
        Assert.False(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiter);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void EndpointSourcePinSeparatesMalformedUnknownNoEffectAndKnownClosure(int outcome)
    {
        var provider = new IrqProvider();
        if (outcome == 0) provider.RewriteBindReceipt = receipt => receipt with { Source = new("foreign", receipt.Source.Trigger) };
        if (outcome == 1) provider.ThrowOnIrqBind = true;
        if (outcome == 2) provider.IrqBindStatus = PlatformAuthorityStatus.NotAccepted;
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1394, 1894);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/endpoint-pin-outcome", "pending", IrqTriggerMode.Edge);
        var bind = kernel.BindPlatformInterrupt(subject, device, capability, endpoint);
        if (outcome == 3)
        {
            Assert.True(bind.IsSuccess);
            Assert.True(kernel.RevokePlatformInterrupt(subject, bind.Value!).IsSuccess);
        }
        else Assert.False(bind.IsSuccess);
        Assert.Equal(outcome == 3, kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.Equal(outcome is 0 or 3 ? 1 : 0, provider.IrqRevokeCalls);
    }

    [Theory]
    [InlineData(false, 0, false)] [InlineData(false, 0, true)]
    [InlineData(false, 1, false)] [InlineData(false, 1, true)]
    [InlineData(false, 2, false)] [InlineData(false, 2, true)]
    [InlineData(true, 0, false)] [InlineData(true, 0, true)]
    [InlineData(true, 1, false)] [InlineData(true, 1, true)]
    [InlineData(true, 2, false)] [InlineData(true, 2, true)]
    public void KnownIrqClosureRetainsOriginalDomainAndPinsActualReclaim(bool rejectedBind, int ownerLoss, bool sourceThrows)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1392, 1892);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/known-child-domain", "pending", IrqTriggerMode.Edge);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var devices = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        var domains = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_domains", flags)!.GetValue(kernel.PlatformAuthority)!;
        var originalDomain = domains[device.DomainBinding.BindingId]!;
        var deviceOwner = devices[device.LeaseId]!;
        object original = null!;
        object? foreign = null;
        provider.AfterIrqRevokeEffect = () =>
        {
            original = Assert.Single(children.Values.Cast<object>());
            Assert.Same(originalDomain, original.GetType().GetProperty("RevokeParent")!.GetValue(original));
            Assert.False(provider.Signal("pending"));
            if (ownerLoss == 0) domains.Remove(device.DomainBinding.BindingId);
            else if (ownerLoss == 1)
            {
                foreign = originalDomain.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(originalDomain, null)!;
                domains[device.DomainBinding.BindingId] = foreign;
            }
            else originalDomain.GetType().GetProperty("ParentRevokeMayHaveEffect")!.SetValue(originalDomain, true);
            if (sourceThrows) provider.IncarnationRead = () => throw new IOException("Final child source observation failed.");
        };
        if (rejectedBind)
        {
            provider.BeforeIrqBindEffect = () => Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
            Assert.False(kernel.BindPlatformInterrupt(subject, device, capability, endpoint).IsSuccess);
        }
        else
        {
            var child = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
            Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformInterrupt(subject, child).Error);
        }
        Assert.NotNull(original);
        var bindFact = Assert.IsType<PlatformAuthorityResult<PlatformProviderIrqBinding>>(original.GetType().GetProperty("BindObservation")!.GetValue(original));
        Assert.True(bindFact.IsSuccess);
        Assert.Equal(original.GetType().GetProperty("ProviderBinding")!.GetValue(original), bindFact.Value);
        Assert.Null(deviceOwner.GetType().GetProperty("IrqBindObservation")!.GetValue(deviceOwner));
        Assert.Equal(false, deviceOwner.GetType().GetProperty("IrqBindMayHaveEffect")!.GetValue(deviceOwner));
        Assert.True(((PlatformAuthorityResult)original.GetType().GetProperty("RevokeObservation")!.GetValue(original)!).IsSuccess);
        Assert.Equal(true, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("PlatformClosed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(true, deviceOwner.GetType().GetProperty("FaultPinned")!.GetValue(deviceOwner));
        if (foreign is not null)
        {
            Assert.Equal(false, foreign.GetType().GetProperty("ParentRevokeMayHaveEffect")!.GetValue(foreign));
            Assert.Null(foreign.GetType().GetProperty("RootRevokeObservation")!.GetValue(foreign));
        }
        provider.IncarnationRead = null;
        provider.AfterIrqRevokeEffect = null;
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }


    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MalformedIrqCompensationRetainsRawFactWithoutOriginalClosure(int outcome)
    {
        var provider = new IrqProvider { RewriteBindReceipt = receipt => receipt with { Source = new("foreign", receipt.Source.Trigger) } };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1390, 1890);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/malformed-fact", "pending", IrqTriggerMode.Edge);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var facts = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = facts[device.LeaseId]!;
        object? Read(string name) => original.GetType().GetProperty(name)!.GetValue(original);
        if (outcome == 1) provider.IrqRevokeStatus = PlatformAuthorityStatus.NotAccepted;
        if (outcome == 2) provider.IrqRevokeStatus = (PlatformAuthorityStatus)999;
        if (outcome == 3) provider.AfterIrqRevokeEffect = () => throw new IOException("Compensation reply lost after provider effect.");
        provider.BeforeIrqRevokeEffect = () =>
        {
            Assert.Equal(true, Read("MalformedIrqCleanupMayHaveEffect"));
            Assert.NotNull(Read("MalformedIrqReceipt"));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Error);
        Assert.Equal(true, Read("MalformedIrqCleanupMayHaveEffect"));
        if (outcome == 3) Assert.Null(Read("MalformedIrqCleanupObservation"));
        else
        {
            var raw = Assert.IsType<PlatformAuthorityResult>(Read("MalformedIrqCleanupObservation"));
            if (outcome == 0) Assert.True(raw.IsSuccess);
            else Assert.Equal(outcome == 1 ? PlatformAuthorityStatus.NotAccepted : (PlatformAuthorityStatus)999, raw.Status);
        }
        Assert.Equal(false, Read("ProviderClosureConfirmed"));
        Assert.True(kernel.PlatformAuthority.HasUnresolvedIrqEffect(id => id == capability,
            ((KernelEventRegistry)typeof(RuntimeKernel).GetField("_kernelEvents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!).SnapshotIrqSources()));
        provider.IrqRevokeStatus = null;
        provider.AfterIrqRevokeEffect = null;
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MalformedIrqCompensationRefusesReplacedParentBeforeCallback(bool domainOwner)
    {
        var provider = new IrqProvider { RewriteBindReceipt = receipt => receipt with { Source = new("foreign", receipt.Source.Trigger) } };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1391, 1891);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/malformed-fact", "pending", IrqTriggerMode.Edge);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var deviceFacts = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        var deviceOwner = deviceFacts[device.LeaseId]!;
        var facts = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField(domainOwner ? "_domains" : "_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object key = domainOwner ? device.DomainBinding.BindingId : device.LeaseId;
        var original = facts[key]!;
        var replaced = false;
        provider.BeforeIrqBindEffect = () =>
        {
            var reads = 0;
            provider.IncarnationRead = () =>
            {
                if (++reads != 2) return;
                provider.IncarnationRead = null;
                facts[key] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                replaced = true;
            };
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Error);
        Assert.True(replaced);
        Assert.Equal(0, provider.IrqRevokeCalls);
        Assert.Equal(false, deviceOwner.GetType().GetProperty("MalformedIrqCleanupMayHaveEffect")!.GetValue(deviceOwner));
        Assert.Null(deviceOwner.GetType().GetProperty("MalformedIrqCleanupObservation")!.GetValue(deviceOwner));
        Assert.NotNull(deviceOwner.GetType().GetProperty("MalformedIrqReceipt")!.GetValue(deviceOwner));
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RejectedIrqCleanupRefusesChangedOriginalParentBeforeEffect(bool domainOwner)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1350, 1719);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/rejected-irq-source", "pending", IrqTriggerMode.Edge);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var facts = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField(domainOwner ? "_domains" : "_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        object key = domainOwner ? device.DomainBinding.BindingId : device.LeaseId;
        var original = facts[key]!;
        var replaced = false;
        provider.BeforeIrqBindEffect = () =>
        {
            Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
            var reads = 0;
            provider.IncarnationRead = () =>
            {
                if (++reads != 2) return;
                provider.IncarnationRead = null;
                facts[key] = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                replaced = true;
            };
        };
        Assert.False(kernel.BindPlatformInterrupt(subject, device, capability, endpoint).IsSuccess);
        Assert.True(replaced);
        Assert.Equal(0, provider.IrqRevokeCalls);
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var child = Assert.Single(children.Values.Cast<object>());
        Assert.Equal(false, child.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(child));
        Assert.Null(child.GetType().GetProperty("RevokeObservation")!.GetValue(child));
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted, false, false)]
    [InlineData(PlatformAuthorityStatus.Denied, false, false)]
    [InlineData(PlatformAuthorityStatus.Stale, false, false)]
    [InlineData((PlatformAuthorityStatus)999, false, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, false)]
    [InlineData(PlatformAuthorityStatus.NotAccepted, true, true)]
    public void RejectedIrqCleanupRetainsOriginalOutcomeAndPinsActualTeardown(PlatformAuthorityStatus status, bool loseOwner, bool loseReply)
    {
        var provider = new IrqProvider { IrqRevokeReplyStatus = status };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1349, 1718);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/rejected-irq-outcome", "pending", IrqTriggerMode.Edge);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        object? original = null;
        provider.BeforeIrqBindEffect = () => Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
        provider.AfterIrqRevokeEffect = () =>
        {
            original = Assert.Single(children.Values.Cast<object>());
            Assert.Equal(true, original.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(original));
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            Assert.False(provider.Signal("pending"));
            if (loseOwner) children.Clear();
            if (loseReply) throw new IOException("Rejected IRQ original cleanup reply lost");
        };
        Assert.False(kernel.BindPlatformInterrupt(subject, device, capability, endpoint).IsSuccess);
        Assert.NotNull(original);
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        var raw = original.GetType().GetProperty("RevokeObservation")!.GetValue(original);
        if (loseReply) Assert.Null(raw); else Assert.Equal(status, ((PlatformAuthorityResult)raw!).Status);
        provider.IrqRevokeReplyStatus = null;
        provider.AfterIrqRevokeEffect = null;
        _ = kernel.RevokeCapability(capability);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.PollCalls);
        Assert.Equal(0, provider.CompleteCalls);
        if (!loseOwner) Assert.False(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void KnownIrqClosureWithLostChildOwnerPinsOriginalParent(bool foreignOwner, bool sourceThrows)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1348, 1717);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/known-child-loss", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var parents = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = children[route.BindingId]!;
        var parent = parents[device.LeaseId]!;
        object? foreign = null;
        provider.AfterIrqRevokeEffect = () =>
        {
            Assert.False(provider.Signal(route.Source.ResourceId)); // Original provider route is gone.
            if (foreignOwner)
            {
                foreign = Activator.CreateInstance(original.GetType(), route with { Generation = new PlatformIrqBindingGeneration(route.Generation.Value + 1) },
                    original.GetType().GetProperty("ProviderBinding")!.GetValue(original), capability)!;
                children[route.BindingId] = foreign;
            }
            else children.Remove(route.BindingId);
            if (sourceThrows) provider.IncarnationRead = () => throw new IOException("Source observation failed after owner loss");
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformInterrupt(subject, route).Error);
        Assert.True(((PlatformAuthorityResult)original.GetType().GetProperty("RevokeObservation")!.GetValue(original)!).IsSuccess);
        Assert.Equal(true, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("PlatformClosed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(true, parent.GetType().GetProperty("FaultPinned")!.GetValue(parent));
        if (foreign is not null)
        {
            Assert.Null(foreign.GetType().GetProperty("RevokeObservation")!.GetValue(foreign));
            Assert.Equal(false, foreign.GetType().GetProperty("FaultPinned")!.GetValue(foreign));
        }
        provider.IncarnationRead = null;
        provider.AfterIrqRevokeEffect = null;
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LostIrqOwnerAfterAmbiguousRevokePinsActualParentDeviceAndProcess(bool lostReply)
    {
        var provider = new IrqProvider { IrqRevokeReplyStatus = PlatformAuthorityStatus.NotAccepted };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1347, 1716);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/lost-revoke-owner", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var parents = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = children[route.BindingId]!;
        var parent = parents[device.LeaseId]!;
        provider.AfterIrqRevokeEffect = () =>
        {
            children.Remove(route.BindingId);
            if (lostReply) throw new IOException("Revoke outcome lost with original routing");
        };
        Assert.False(kernel.RevokePlatformInterrupt(subject, route).IsSuccess);
        Assert.Equal(true, parent.GetType().GetProperty("FaultPinned")!.GetValue(parent));
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        Assert.Equal(lostReply, original.GetType().GetProperty("RevokeObservation")!.GetValue(original) is null);
        provider.AfterIrqRevokeEffect = null;
        provider.IrqRevokeReplyStatus = null;
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(PlatformAuthorityStatus.NotAccepted)]
    [InlineData(PlatformAuthorityStatus.Denied)]
    [InlineData(PlatformAuthorityStatus.Stale)]
    [InlineData((PlatformAuthorityStatus)999)]
    public void FailedOriginalIrqRevokeAfterEffectRetainsRawReplyAndPinsActualTeardown(PlatformAuthorityStatus status)
    {
        var provider = new IrqProvider { IrqRevokeReplyStatus = status };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1346, 1715);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/revoke-outcome", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = records[route.BindingId]!;
        provider.AfterIrqRevokeEffect = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            Assert.Equal(true, original.GetType().GetProperty("RevokeMayHaveEffect")!.GetValue(original));
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformInterrupt(subject, route).Error);
        Assert.False(provider.Signal("pending"));
        var raw = (PlatformAuthorityResult)original.GetType().GetProperty("RevokeObservation")!.GetValue(original)!;
        Assert.Equal(status, raw.Status);
        Assert.Equal(false, original.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("PlatformClosed")!.GetValue(original));
        Assert.Equal(false, original.GetType().GetProperty("ClosureInFlight")!.GetValue(original));
        provider.IrqRevokeReplyStatus = null;
        provider.AfterIrqRevokeEffect = null;
        Assert.False(kernel.RevokePlatformInterrupt(subject, route).IsSuccess);
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(raw, original.GetType().GetProperty("RevokeObservation")!.GetValue(original));
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    [InlineData(true, 0)] [InlineData(true, 1)]
    public void CompletionSourceOwnerOrSequenceLossRefusesAckAndRetainsActualPins(bool parentOwner, int fault)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1344, 1713);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/completion-capture", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        Assert.True(provider.Signal("pending"));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var originalChild = children[route.BindingId]!;
        var facts = parentOwner ? (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)! : children;
        object key = parentOwner ? device.LeaseId : route.BindingId;
        var original = facts[key]!;
        var reads = 0;
        provider.BeforeIrqPollEffect = () => provider.IncarnationRead = () =>
        {
            if (++reads != 2) return; // after returned poll, then before completion admission
            provider.IncarnationRead = null;
            if (fault == 0) facts.Remove(key);
            if (fault == 1)
            {
                var replacement = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                if (parentOwner) replacement.GetType().GetProperty("ProviderIncarnation")!.SetValue(replacement, new PlatformProviderIncarnation(2));
                facts[key] = replacement;
            }
            if (fault == 2) originalChild.GetType().GetProperty("DeliveryInFlight")!.SetValue(originalChild, false);
            if (fault == 3)
            {
                var property = originalChild.GetType().GetProperty("PollObservation")!;
                var poll = (PlatformAuthorityResult<PlatformInterruptDeliveryObservation>)property.GetValue(originalChild)!;
                property.SetValue(originalChild, PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Ok(poll.Value with { Sequence = new(2) }));
            }
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.PollPlatformInterrupt(subject, route).Error);
        Assert.Equal(1, provider.PollCalls);
        Assert.Equal(0, provider.CompleteCalls);
        Assert.Equal(false, originalChild.GetType().GetProperty("DeliveryInFlight")!.GetValue(originalChild));
        Assert.Equal(false, originalChild.GetType().GetProperty("CompletionMayHaveEffect")!.GetValue(originalChild));
        Assert.Null(originalChild.GetType().GetProperty("CompletionObservation")!.GetValue(originalChild));
        Assert.Equal(KernelError.ResponseNotAvailable, kernel.ConsumeKernelEvent(subject, endpoint).Error);
        Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void CompletionOriginalOutcomeSurvivesAfterAckFailureWithoutEventOrRepeat(int fault)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1345, 1714);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/completion-outcome", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        Assert.True(provider.Signal("pending"));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = records[route.BindingId]!;
        provider.AfterIrqCompleteEffect = () =>
        {
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            Assert.Equal(true, original.GetType().GetProperty("CompletionMayHaveEffect")!.GetValue(original));
            if (fault == 0) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 1) provider.Incarnation = 2;
            if (fault == 2) provider.IncarnationRead = () => throw new IOException("Source unavailable after ack");
            if (fault == 6) throw new IOException("Ack response lost");
        };
        if (fault is 3 or 4 or 5) provider.CompletionReplyStatus = fault == 3 ? PlatformAuthorityStatus.NotAccepted : fault == 4 ? PlatformAuthorityStatus.Denied : PlatformAuthorityStatus.Stale;
        Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
        Assert.False(provider.IsPending("pending")); // provider ack happened even when the reply refuses publication
        Assert.Equal(new PlatformProviderInterruptDeliverySequence(1), original.GetType().GetProperty("CompletionSequence")!.GetValue(original));
        var raw = original.GetType().GetProperty("CompletionObservation")!.GetValue(original);
        if (fault == 6) Assert.Null(raw);
        else Assert.Equal(provider.CompletionReplyStatus ?? PlatformAuthorityStatus.Success, ((PlatformAuthorityResult)raw!).Status);
        Assert.Equal(false, original.GetType().GetProperty("DeliveryInFlight")!.GetValue(original));
        provider.AfterIrqCompleteEffect = null;
        provider.IncarnationRead = null;
        provider.CompletionReplyStatus = null;
        Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
        Assert.Equal(1, provider.PollCalls);
        Assert.Equal(1, provider.CompleteCalls);
        Assert.Equal(KernelError.ResponseNotAvailable, kernel.ConsumeKernelEvent(subject, endpoint).Error);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)]
    [InlineData(true, 0)] [InlineData(true, 1)]
    public void IrqPollSourceOwnerLossRefusesOriginalEffectAndBalancesCapturedAdmission(bool parentOwner, int fault)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1343, 1712);
        var buffer = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/poll-capture", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        Assert.True(provider.Signal("pending"));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var originalChild = children[route.BindingId]!;
        var facts = parentOwner ? (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)! : children;
        object key = parentOwner ? device.LeaseId : route.BindingId;
        var original = facts[key]!;
        object? replacement = null;
        provider.IncarnationRead = () =>
        {
            provider.IncarnationRead = null;
            Assert.False(Monitor.IsEntered(typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!));
            if (fault == 0) facts.Remove(key);
            if (fault == 1)
            {
                replacement = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                if (parentOwner) replacement.GetType().GetProperty("ProviderIncarnation")!.SetValue(replacement, new PlatformProviderIncarnation(2));
                facts[key] = replacement;
            }
            if (fault == 2) originalChild.GetType().GetProperty("DeliveryInFlight")!.SetValue(originalChild, false);
        };
        Assert.Equal(KernelError.PlatformFaulted, kernel.PollPlatformInterrupt(subject, route).Error);
        Assert.Equal(0, provider.PollCalls);
        Assert.Equal(0, provider.CompleteCalls);
        Assert.Equal(false, originalChild.GetType().GetProperty("DeliveryInFlight")!.GetValue(originalChild));
        Assert.Equal(true, originalChild.GetType().GetProperty("FaultPinned")!.GetValue(originalChild));
        Assert.Null(originalChild.GetType().GetProperty("PollObservation")!.GetValue(originalChild));
        if (!parentOwner && replacement is not null)
        {
            // Copied flags are foreign state; balancing/faulting the original must not write this owner.
            Assert.Equal(true, replacement.GetType().GetProperty("DeliveryInFlight")!.GetValue(replacement));
            Assert.Equal(false, replacement.GetType().GetProperty("FaultPinned")!.GetValue(replacement));
        }
        Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
        Assert.Equal(0, provider.PollCalls);
        Assert.Equal(KernelError.ResponseNotAvailable, kernel.ConsumeKernelEvent(subject, endpoint).Error);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void OriginalIrqPollOutcomeSurvivesLateRefusalWithoutEventCompletionOrReclaim(int fault)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1334, 1640);
        _ = kernel.AllocateRegion(subject, 4096).Value!;
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/original-poll", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        Assert.True(provider.Signal("pending"));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var original = records[route.BindingId]!;
        object? foreign = null;
        var expected = (PlatformProviderIrqBinding)original.GetType().GetProperty("ProviderBinding")!.GetValue(original)!;
        provider.BeforeIrqPollEffect = () =>
        {
            var gate = typeof(PlatformAuthorityBridge).GetField("_secureDomainLifecycleGate", flags)!.GetValue(kernel.PlatformAuthority)!;
            Assert.False(Monitor.IsEntered(gate));
            if (fault == 0) Assert.True(kernel.ObservePlatformBackendReset().IsSuccess);
            if (fault == 6)
            {
                foreign = typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                records[route.BindingId] = foreign;
            }
            if (fault == 1) provider.Incarnation = 2;
            if (fault == 2) Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(capability).Error);
            if (fault == 5) provider.IncarnationRead = () => throw new IOException("Source unavailable after original poll");
        };
        if (fault == 3) provider.RewritePollReceipt = observation => observation with { Sequence = new(0) };
        if (fault == 4) provider.PollStatus = PlatformAuthorityStatus.NotAccepted;
        Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
        var raw = (PlatformAuthorityResult<PlatformInterruptDeliveryObservation>)original.GetType()
            .GetProperty("PollObservation")!.GetValue(original)!;
        Assert.Equal(fault == 4 ? PlatformAuthorityStatus.NotAccepted : PlatformAuthorityStatus.Success, raw.Status);
        if (fault != 4)
        {
            Assert.Equal(expected, raw.Value.Binding);
            Assert.True(raw.Value.DeliveryAvailable);
            Assert.Equal(fault == 3 ? 0UL : 1UL, raw.Value.Sequence.Value);
        }
        Assert.Equal(false, original.GetType().GetProperty("DeliveryInFlight")!.GetValue(original));
        Assert.Equal(true, original.GetType().GetProperty("FaultPinned")!.GetValue(original));
        if (foreign is not null)
        {
            Assert.Equal(false, foreign.GetType().GetProperty("FaultPinned")!.GetValue(foreign));
            Assert.Null(foreign.GetType().GetProperty("PollObservation")!.GetValue(foreign));
        }
        provider.BeforeIrqPollEffect = null;
        provider.IncarnationRead = null;
        provider.PollStatus = null;
        provider.RewritePollReceipt = null;
        Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
        Assert.Equal(1, provider.PollCalls);
        Assert.Equal(0, provider.CompleteCalls);
        Assert.Equal(KernelError.ResponseNotAvailable, kernel.ConsumeKernelEvent(subject, endpoint).Error);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(subject).Value!.LocalReclaimCompleted);
        Assert.Equal(RegionState.Owned, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(raw, original.GetType().GetProperty("PollObservation")!.GetValue(original));
    }

    [Theory]
    [InlineData(false, false)][InlineData(false, true)]
    [InlineData(true, false)][InlineData(true, true)]
    public void CleanupSourceOwnerLossRefusesEffectAndPinsTeardown(bool parentOwner, bool replacementOwner)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1342, 1711);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject, "device/irq-capture", "pending", IrqTriggerMode.Edge);
        var child = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        object childKey = child.BindingId;
        KernelResult Close() => kernel.RevokePlatformInterrupt(subject, child);
        var buffer = kernel.AllocateRegion(subject, 4096).Value!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var children = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_irqBindings", flags)!.GetValue(kernel.PlatformAuthority)!;
        var originalChild = children[childKey]!;
        var facts = parentOwner ? (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_deviceLeases", flags)!.GetValue(kernel.PlatformAuthority)! : children;
        object key = parentOwner ? device.LeaseId : childKey;
        var original = facts[key]!;
        provider.IncarnationRead = () =>
        {
            provider.IncarnationRead = null;
            if (replacementOwner)
            {
                var replacement = original.GetType().GetMethod("MemberwiseClone", flags)!.Invoke(original, null)!;
                if (parentOwner) replacement.GetType().GetProperty("ProviderIncarnation")!.SetValue(replacement, new PlatformProviderIncarnation(2));
                facts[key] = replacement;
            }
            else facts.Remove(key);
        };
        Assert.False(Close().IsSuccess);
        Assert.Equal(0, provider.IrqRevokeCalls);
        Assert.Equal(false, originalChild.GetType().GetProperty("ClosureInFlight")!.GetValue(originalChild));
        Assert.Equal(false, originalChild.GetType().GetProperty("ProviderClosureConfirmed")!.GetValue(originalChild));
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == buffer.Handle).State);
        Assert.Equal(0, provider.IrqRevokeCalls);
    }

    [Fact]
    public void CanonicalIrqCapabilityRoundTripsSemanticDeviceSourceAndTrigger()
    {
        var id = CapabilityResourceIds.Irq(
            "device/net0",
            "rx-ready",
            IrqTriggerMode.Edge);

        Assert.True(CapabilityResourceIds.TryParseIrq(id, out var parsed));
        Assert.Equal("device/net0", parsed.DeviceResourceId);
        Assert.Equal("rx-ready", parsed.SourceResourceId);
        Assert.Equal(IrqTriggerMode.Edge, parsed.Trigger);
        Assert.False(CapabilityResourceIds.TryParseIrq("irq:4", out _));
    }

    [Fact]
    public void ExactIrqCapabilityDeliversLocalEventAndDeviceRevokeClosesRouteFirst()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1301, 1310);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            subject,
            "device/net0",
            "rx-ready",
            IrqTriggerMode.Edge);

        var route = kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint);
        Assert.True(route.IsSuccess, route.Message);
        Assert.Equal("rx-ready", route.Value!.Source.ResourceId);
        Assert.Equal(endpoint, route.Value.EventEndpoint);

        Assert.True(provider.Signal("rx-ready"));
        var delivered = kernel.PollPlatformInterrupt(subject, route.Value);
        Assert.True(delivered.IsSuccess, delivered.Message);
        Assert.True(delivered.Value!.DeliveryAvailable);
        Assert.True(delivered.Value.Event.HasValue);
        Assert.Equal("rx-ready", delivered.Value.Event.Value.SourceResourceId);
        Assert.Equal(KernelEventClass.ExternalSignal, delivered.Value.Event.Value.EventClass);
        Assert.Equal(1, provider.CompleteCalls);

        var consumed = kernel.ConsumeKernelEvent(subject, endpoint);
        Assert.True(consumed.IsSuccess, consumed.Message);
        Assert.Equal(delivered.Value.Event.Value, consumed.Value);

        var revokeDevice = kernel.RevokePlatformDevice(subject, device);
        Assert.True(revokeDevice.IsSuccess, revokeDevice.Message);
        Assert.Equal(
            new[]
            {
                "bind-irq:rx-ready",
                "revoke-irq:rx-ready",
                "revoke-device:device/net0",
            },
            provider.Log);
    }

    [Fact]
    public void AdmissionFailuresStopBeforeProviderInterruptBinding()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1302, 1320);
        var (_, other) = TestFixtures.Create(kernel, 1303, 1330);
        var domain = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/audio0",
            CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            domain,
            deviceCapability,
            PlatformDeviceRights.Configure).Value!;
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        var foreignEndpoint = kernel.CreateKernelEventEndpoint(other).Value!;

        var wrongDevice = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/other", "period", IrqTriggerMode.Level),
            CapabilityRights.Signal);
        var noSignal = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/audio0", "period", IrqTriggerMode.Level),
            CapabilityRights.Read);
        var nonCanonical = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            "irq:4",
            CapabilityRights.Signal);
        var good = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/audio0", "period2", IrqTriggerMode.Edge),
            CapabilityRights.Signal);

        Assert.Equal(
            KernelError.WrongCapabilityResource,
            kernel.BindPlatformInterrupt(subject, device, wrongDevice, endpoint).Error);
        Assert.Equal(
            KernelError.InsufficientRights,
            kernel.BindPlatformInterrupt(subject, device, noSignal, endpoint).Error);
        Assert.Equal(
            KernelError.WrongCapabilityResource,
            kernel.BindPlatformInterrupt(subject, device, nonCanonical, endpoint).Error);
        Assert.Equal(
            KernelError.WrongEndpointOwner,
            kernel.BindPlatformInterrupt(subject, device, good, foreignEndpoint).Error);
        Assert.Equal(0, provider.BindCalls);
    }

    [Fact]
    public void BusyKernelEventEndpointLeavesExternalDeliveryPendingUntilPublicationCanSucceed()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1304, 1340);
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            "device/storage0",
            CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Configure).Value!;
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;

        var firstCapability = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/storage0", "queue0", IrqTriggerMode.Edge),
            CapabilityRights.Signal);
        var secondCapability = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/storage0", "queue1", IrqTriggerMode.Edge),
            CapabilityRights.Signal);
        var first = kernel.BindPlatformInterrupt(subject, device, firstCapability, endpoint).Value!;
        var second = kernel.BindPlatformInterrupt(subject, device, secondCapability, endpoint).Value!;

        Assert.True(provider.Signal("queue0"));
        Assert.True(kernel.PollPlatformInterrupt(subject, first).IsSuccess);
        Assert.Equal(1, provider.CompleteCalls);

        Assert.True(provider.Signal("queue1"));
        var blocked = kernel.PollPlatformInterrupt(subject, second);
        Assert.Equal(KernelError.CapacityExhausted, blocked.Error);
        Assert.Equal(1, provider.CompleteCalls);
        Assert.True(provider.IsPending("queue1"));

        Assert.True(kernel.ConsumeKernelEvent(subject, endpoint).IsSuccess);
        var retried = kernel.PollPlatformInterrupt(subject, second);
        Assert.True(retried.IsSuccess, retried.Message);
        Assert.True(retried.Value!.DeliveryAvailable);
        Assert.Equal(2, provider.CompleteCalls);
        Assert.False(provider.IsPending("queue1"));
    }

    [Fact]
    public async Task GenericNotAcceptedCompletionRollsBackInvisibleEventAndPinsOriginalInvocation()
    {
        var provider = new IrqProvider { CompletionStatus = PlatformAuthorityStatus.NotAccepted };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1309, 1390);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            subject,
            "device/retry0",
            "notify",
            IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint).Value!;
        using var cancellation = new CancellationTokenSource();
        var wait = kernel.WaitForKernelEventAsync(subject, endpoint, cancellation.Token).AsTask();

        Assert.True(provider.Signal("notify"));
        var failed = kernel.PollPlatformInterrupt(subject, route);
        Assert.False(failed.IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, failed.Error);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            kernel.ConsumeKernelEvent(subject, endpoint).Error);
        Assert.False(wait.IsCompleted);
        Assert.True(provider.IsPending("notify"));

        provider.CompletionStatus = null;
        var retry = kernel.PollPlatformInterrupt(subject, route);
        Assert.False(retry.IsSuccess);
        Assert.False(wait.IsCompleted);
        Assert.Equal(1, provider.PollCalls);
        Assert.Equal(1, provider.CompleteCalls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            kernel.ConsumeKernelEvent(subject, endpoint).Error);
        Assert.True(provider.IsPending("notify"));
    }

    [Fact]
    public void FaultedInterruptCompletionCannotAuthorizeRetryOrParentReclaim()
    {
        var provider = new IrqProvider { CompletionStatus = PlatformAuthorityStatus.Faulted };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1331, 1610);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/faulted-delivery", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        Assert.True(provider.Signal("pending"));
        Assert.Equal(KernelError.PlatformFaulted, kernel.PollPlatformInterrupt(subject, route).Error);
        Assert.Equal(KernelError.ResponseNotAvailable, kernel.ConsumeKernelEvent(subject, endpoint).Error);
        provider.CompletionStatus = null;
        Assert.Equal(KernelError.PlatformFaulted, kernel.PollPlatformInterrupt(subject, route).Error);
        Assert.Equal(1, provider.PollCalls);
        Assert.Equal(1, provider.CompleteCalls);
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(0, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Fact]
    public async Task WaiterBeforeInterruptPublicationReceivesTheExactCommittedEventOnce()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1310, 1400);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            subject,
            "device/wait0",
            "ready",
            IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint).Value!;
        using var cancellation = new CancellationTokenSource();

        var wait = kernel.WaitForKernelEventAsync(
            subject,
            endpoint,
            cancellation.Token).AsTask();
        Assert.False(wait.IsCompleted);

        Assert.True(provider.Signal("ready"));
        var delivery = kernel.PollPlatformInterrupt(subject, route);

        Assert.True(delivery.IsSuccess, delivery.Message);
        Assert.True(delivery.Value!.Event.HasValue);
        cancellation.Cancel();
        var received = await wait;
        Assert.True(received.IsSuccess, received.Message);
        Assert.Equal(delivery.Value.Event.Value, received.Value);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            kernel.ConsumeKernelEvent(subject, endpoint).Error);
    }

    [Fact]
    public async Task PrecommittedEventWinsOverAnAlreadyCancelledWaitToken()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1311, 1410);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            subject,
            "device/wait1",
            "complete",
            IrqTriggerMode.Level);
        var route = kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint).Value!;

        Assert.True(provider.Signal("complete"));
        var delivery = kernel.PollPlatformInterrupt(subject, route);
        Assert.True(delivery.IsSuccess, delivery.Message);
        Assert.True(delivery.Value!.Event.HasValue);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var received = await kernel.WaitForKernelEventAsync(
            subject,
            endpoint,
            cancellation.Token);

        Assert.True(received.IsSuccess, received.Message);
        Assert.Equal(delivery.Value.Event.Value, received.Value);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            kernel.ConsumeKernelEvent(subject, endpoint).Error);
    }

    [Fact]
    public async Task CallerCancellationRemovesOnlyTheWaiterAndLeavesEndpointReusable()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1312, 1420);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            subject,
            "device/wait2",
            "notify",
            IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint).Value!;
        using var cancellation = new CancellationTokenSource();
        var cancelledWait = kernel.WaitForKernelEventAsync(
            subject,
            endpoint,
            cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
        Assert.Equal(0, provider.CompleteCalls);
        Assert.True(provider.Signal("notify"));
        var delivery = kernel.PollPlatformInterrupt(subject, route);
        Assert.True(delivery.IsSuccess, delivery.Message);
        Assert.True(delivery.Value!.Event.HasValue);

        var laterWait = await kernel.WaitForKernelEventAsync(subject, endpoint);
        Assert.True(laterWait.IsSuccess, laterWait.Message);
        Assert.Equal(delivery.Value.Event.Value, laterWait.Value);
        Assert.Equal(1, provider.CompleteCalls);
    }

    [Fact]
    public async Task EndpointAdmitsOneWaiterAndCloseCancelsOnlyThatUncommittedWait()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1313, 1430);
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        var first = kernel.WaitForKernelEventAsync(subject, endpoint).AsTask();

        var second = await kernel.WaitForKernelEventAsync(subject, endpoint);

        Assert.Equal(KernelError.CapacityExhausted, second.Error);
        Assert.True(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(
            KernelError.EndpointNotFound,
            (await kernel.WaitForKernelEventAsync(subject, endpoint)).Error);

        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        var (_, recycled) = TestFixtures.Create(kernel, 1313, 1460, generation: 2);
        Assert.Equal(
            KernelError.StaleHandle,
            (await kernel.WaitForKernelEventAsync(subject, endpoint)).Error);
        Assert.Equal(
            KernelError.WrongEndpointOwner,
            (await kernel.WaitForKernelEventAsync(recycled, endpoint)).Error);
    }

    [Fact]
    public async Task StaleForeignAndClosedEndpointWaitsFailBeforeWaiterRegistration()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1314, 1440);
        var (_, other) = TestFixtures.Create(kernel, 1315, 1450);
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        var stale = endpoint with
        {
            Generation = new KernelEventEndpointGeneration(endpoint.Generation.Value + 1),
        };
        var forged = endpoint with { Owner = other };

        Assert.Equal(
            KernelError.StaleGeneration,
            (await kernel.WaitForKernelEventAsync(subject, stale)).Error);
        Assert.Equal(
            KernelError.PlatformFaulted,
            (await kernel.WaitForKernelEventAsync(subject, forged)).Error);
        Assert.Equal(
            KernelError.WrongEndpointOwner,
            (await kernel.WaitForKernelEventAsync(other, endpoint)).Error);

        using (var cancellation = new CancellationTokenSource())
        {
            var valid = kernel.WaitForKernelEventAsync(
                subject,
                endpoint,
                cancellation.Token).AsTask();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => valid);
        }

        Assert.True(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.Equal(
            KernelError.EndpointNotFound,
            (await kernel.WaitForKernelEventAsync(subject, endpoint)).Error);
    }

    [Fact]
    public void RecycledProcessGenerationCannotReceiveFromOldInterruptRoute()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, oldSubject) = TestFixtures.Create(kernel, 1305, 1350, generation: 1);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            oldSubject,
            "device/gpu0",
            "doorbell",
            IrqTriggerMode.Level);
        var oldRoute = kernel.BindPlatformInterrupt(
            oldSubject,
            device,
            irqCapability,
            endpoint).Value!;

        Assert.True(kernel.TerminateProcess(oldSubject).IsSuccess);
        var (_, recycled) = TestFixtures.Create(kernel, 1305, 1351, generation: 2);
        var pollCallsBefore = provider.PollCalls;

        var staleSubject = kernel.PollPlatformInterrupt(oldSubject, oldRoute);
        Assert.Equal(KernelError.StaleHandle, staleSubject.Error);

        var recycledAttempt = kernel.PollPlatformInterrupt(recycled, oldRoute);
        Assert.Equal(KernelError.WrongEndpointOwner, recycledAttempt.Error);
        Assert.Equal(pollCallsBefore, provider.PollCalls);
    }

    [Fact]
    public void CompensationTeardownSelectsExactSubjectWithoutClosingAnotherProcessRoute()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (first, subject) = TestFixtures.Create(kernel, 1343, 1730);
        var (other, otherSubject) = TestFixtures.Create(kernel, 1344, 1740);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/isolated-compensation", "pending", IrqTriggerMode.Edge);
        var (otherDevice, otherCapability, otherEndpoint) = CreateAuthorities(kernel, otherSubject,
            "device/other-route", "other", IrqTriggerMode.Edge);
        var otherRoute = kernel.BindPlatformInterrupt(otherSubject, otherDevice, otherCapability, otherEndpoint).Value!;
        provider.BeforeIrqBindEffect = () => kernel.CapabilityAuthority.Revoke(capability);
        provider.AfterIrqRevokeEffect = () => provider.IncarnationRead = () =>
        {
            provider.IncarnationRead = null;
            throw new IOException("late compensation source observation");
        };
        Assert.Equal(KernelError.CapabilityRevoked,
            kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Error);
        provider.BeforeIrqBindEffect = null;
        provider.AfterIrqRevokeEffect = null;
        Assert.Empty(kernel.PlatformAuthority.ActiveIrqBindingsForProcess(new(first.DomainId, otherSubject)));
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(ProcessState.Exited, first.State);
        Assert.True(kernel.Processes.Resolve(otherSubject).IsSuccess);
        Assert.Equal(otherRoute, Assert.Single(kernel.PlatformAuthority.ActiveIrqBindingsForDevice(otherDevice)));
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.True(provider.Signal("other"));
        Assert.True(kernel.PollPlatformInterrupt(otherSubject, otherRoute).IsSuccess);
        Assert.True(kernel.TerminateProcess(otherSubject).IsSuccess);
        Assert.Equal(ProcessState.Exited, other.State);
        Assert.Equal(2, provider.IrqRevokeCalls);
        Assert.Equal(2, provider.DeviceRevokeCalls);
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void MalformedReceiptSuccessfulCleanupNeverClosesOriginalAdmission(int mutation)
    {
        var provider = new IrqProvider();
        provider.RewriteBindReceipt = receipt => mutation switch
        {
            0 => receipt with { Source = new("wrong-source", receipt.Source.Trigger) },
            1 => receipt with { DeviceLease = receipt.DeviceLease with { Generation = new(99) } },
            2 => receipt with { BindingId = new(0) },
            _ => receipt with { Generation = new(0) }
        };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1345, 1750);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/malformed-success", "signal", IrqTriggerMode.Edge);
        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Error);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.True(kernel.PlatformAuthority.HasUnresolvedIrqEffect(id => id == capability,
            ((KernelEventRegistry)typeof(RuntimeKernel).GetField("_kernelEvents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!).SnapshotIrqSources()));
        Assert.Empty(kernel.PlatformAuthority.ActiveIrqBindingsForDevice(device));
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public void RefusedIrqAdmissionRetainsExactCompensationReplyForActualCleanup(int fault, int consumer)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, subject) = TestFixtures.Create(kernel, 1342, 1720);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/compensation-receipt", "pending", IrqTriggerMode.Edge);
        var revokeAccepted = false;
        var resetObserved = false;
        KernelResult nested = default;
        provider.BeforeIrqBindEffect = () => revokeAccepted = kernel.CapabilityAuthority.Revoke(capability).IsSuccess;
        provider.BeforeIrqRevokeEffect = () => nested = kernel.RevokePlatformInterrupt(subject,
            Assert.Single(kernel.PlatformAuthority.ActiveIrqBindingsForDevice(device)));
        provider.AfterIrqRevokeEffect = () =>
        {
            if (fault == 3) throw new IOException("compensation effect reply lost");
            provider.IncarnationRead = () =>
            {
                provider.IncarnationRead = null;
                if (fault == 0) throw new IOException("known compensation late source query lost");
                if (fault == 1) provider.Incarnation = 2;
                if (fault == 2) resetObserved = kernel.PlatformAuthority.ObserveBackendReset().IsSuccess;
            };
        };
        var admission = kernel.BindPlatformInterrupt(subject, device, capability, endpoint);
        Assert.True(revokeAccepted);
        Assert.Equal(KernelError.CapabilityRevoked, admission.Error);
        Assert.Equal(KernelError.PlatformBindingActive, nested.Error);
        Assert.Equal(1, provider.BindCalls);
        Assert.Equal(1, provider.IrqRevokeCalls);
        if (fault == 2) Assert.True(resetObserved);
        var route = Assert.Single(kernel.PlatformAuthority.ActiveIrqBindingsForDevice(device));
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_irqBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var record = records[route.BindingId]!;
        bool Fact(string name) => (bool)record.GetType().GetProperty(name)!.GetValue(record)!;
        Assert.Equal(fault != 3, Fact("ProviderClosureConfirmed"));
        Assert.True(Fact("LocalAuthorizationRevoked"));
        Assert.False(Fact("ClosureInFlight"));
        Assert.False(Fact("PlatformClosed"));
        Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
        Assert.Equal(0, provider.PollCalls);
        provider.BeforeIrqRevokeEffect = null;
        provider.AfterIrqRevokeEffect = null;
        provider.IncarnationRead = null;
        var retried = consumer switch
        {
            0 => kernel.RevokeCapability(capability),
            1 => kernel.RevokePlatformDevice(subject, device),
            _ => kernel.TerminateProcess(subject)
        };
        Assert.Equal(fault == 0, retried.IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(fault == 0, Fact("PlatformClosed"));
        if (fault == 0)
        {
            Assert.Empty(kernel.PlatformAuthority.ActiveIrqBindingsForDevice(device));
            if (consumer == 0) Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            if (consumer == 2)
            {
                Assert.Equal(ProcessState.Exited, process.State);
                Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(subject).Error);
            }
            else Assert.True(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
            Assert.Equal(1, provider.DeviceRevokeCalls);
        }
        else
        {
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
            Assert.Single(kernel.PlatformAuthority.ActiveIrqBindingsForDevice(device));
        }
    }
    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public void KnownIrqClosureReplyRetriesLocalConsumersWithoutDuplicateProviderEffect(int fault, int consumer)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, subject) = TestFixtures.Create(kernel, 1341, 1710);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/known-closure", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        var reads = 0;
        var resetObserved = false;
        provider.IncarnationRead = () =>
        {
            if (++reads != 2) return;
            provider.IncarnationRead = null;
            if (fault == 0) throw new IOException("late IRQ source read lost");
            if (fault == 1) provider.Incarnation = 2;
            if (fault == 2) resetObserved = kernel.PlatformAuthority.ObserveBackendReset().IsSuccess;
        };
        if (fault == 3) provider.AfterIrqRevokeEffect = () => throw new IOException("successful provider effect reply lost");
        var first = kernel.RevokePlatformInterrupt(subject, route);
        Assert.Equal(KernelError.PlatformFaulted, first.Error);
        Assert.Equal(1, provider.IrqRevokeCalls);
        if (fault == 2) Assert.True(resetObserved);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_irqBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var record = records[route.BindingId]!;
        bool Fact(string name) => (bool)record.GetType().GetProperty(name)!.GetValue(record)!;
        Assert.Equal(fault != 3, Fact("ProviderClosureConfirmed"));
        Assert.False(Fact("PlatformClosed"));
        Assert.False(Fact("ClosureInFlight"));
        Assert.True(Fact("LocalAuthorizationRevoked"));
        Assert.Single(kernel.PlatformAuthority.ActiveIrqBindingsForDevice(device));
        Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
        Assert.Equal(0, provider.PollCalls);
        Assert.False(kernel.RevokePlatformInterrupt(subject,
            route with { Generation = new PlatformIrqBindingGeneration(route.Generation.Value + 1) }).IsSuccess);
        provider.IncarnationRead = null;
        provider.AfterIrqRevokeEffect = null;
        var retry = consumer switch
        {
            0 => kernel.RevokePlatformInterrupt(subject, route),
            1 => kernel.RevokeCapability(capability),
            _ => kernel.TerminateProcess(subject)
        };
        Assert.Equal(fault == 0, retry.IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(fault == 0, Fact("PlatformClosed"));
        Assert.False(Fact("ClosureInFlight"));
        if (fault == 0)
        {
            Assert.Empty(kernel.PlatformAuthority.ActiveIrqBindingsForDevice(device));
            if (consumer != 2)
            {
                Assert.True(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
                Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            }
            else
            {
                Assert.Equal(ProcessState.Exited, process.State);
                Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(subject).Error);
                Assert.Equal(1, provider.DomainRevokeCalls);
            }
            Assert.Equal(1, provider.DeviceRevokeCalls);
        }
        else
        {
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
            Assert.Single(kernel.PlatformAuthority.ActiveIrqBindingsForDevice(device));
        }
    }
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void DelegatedRootIrqRevokeClosesPublishedDescendant(int depth, bool fault)
    {
        var provider = new IrqProvider { IrqRevokeStatus = fault ? PlatformAuthorityStatus.Faulted : null };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1306, 1360);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            subject,
            "device/timer0",
            "tick",
            IrqTriggerMode.Edge);
        var root = Mint(kernel, subject, ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/timer0", "tick", IrqTriggerMode.Edge), CapabilityRights.Signal | CapabilityRights.Delegate);
        irqCapability = root;
        for (var i = 0; i < depth; i++)
            irqCapability = kernel.DelegateCapability(subject, subject, irqCapability,
                CapabilityRights.Signal | (i + 1 < depth ? CapabilityRights.Delegate : CapabilityRights.None)).Value!.CapabilityId;
        var route = kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint).Value!;

        var revoke = kernel.RevokeCapability(root);

        if (fault)
        {
            Assert.Equal(KernelError.PlatformFaulted, revoke.Error);
            Assert.Equal(1, provider.IrqRevokeCalls);
            Assert.Equal(KernelError.CapabilityRevoked,
                kernel.PlatformAuthority.ValidateIrqBinding(route, device.DomainBinding.Subject).Error);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
            Assert.False(kernel.RevokeCapability(root).IsSuccess);
            return;
        }
        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(
            KernelError.PlatformBindingRevoked,
            kernel.RevokePlatformInterrupt(subject, route).Error);
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IrqAdmissionCallbackBlocksParentClosureAndNestedAdmission(bool throws)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1320, 1500);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/pending-irq", "pending", IrqTriggerMode.Edge);
        provider.BeforeIrqBindEffect = () =>
        {
            provider.BeforeIrqBindEffect = null;
            Assert.Equal(KernelError.PlatformBindingActive,
                kernel.RevokePlatformDevice(subject, device).Error);
            Assert.Equal(0, provider.DeviceRevokeCalls);
            Assert.Equal(KernelError.PlatformBindingActive,
                kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Error);
            if (throws) throw new InvalidOperationException("Possible IRQ effect without receipt.");
        };
        var result = kernel.BindPlatformInterrupt(subject, device, capability, endpoint);
        Assert.Equal(1, provider.BindCalls);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_deviceLeases", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(kernel.PlatformAuthority)!;
        var record = records[device.LeaseId]!;
        Assert.Equal(0, (int)record.GetType().GetProperty("PendingIrqBinds")!.GetValue(record)!);
        Assert.False((bool)record.GetType().GetProperty("ClosureInFlight")!.GetValue(record)!);
        if (throws)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, device).Error);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(kernel.RevokePlatformInterrupt(subject, result.Value!).IsSuccess);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(1, provider.DeviceRevokeCalls);
        }
    }
    [Fact]
    public void DeviceClosureCallbackRefusesIrqBeforeProviderEffect()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1321, 1510);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/closing-irq", "pending", IrqTriggerMode.Edge);
        provider.BeforeDeviceRevokeEffect = () =>
        {
            provider.BeforeDeviceRevokeEffect = null;
            Assert.Equal(KernelError.PlatformBindingActive,
                kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Error);
            Assert.Equal(KernelError.PlatformBindingActive,
                kernel.RevokePlatformDevice(subject, device).Error);
            Assert.Equal(0, provider.BindCalls);
        };
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(1, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.BindCalls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IrqAdmissionCallbackKeepsExactEndpointOpenUntilReceipt(bool concurrent)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1322, 1520);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/pending-endpoint", "pending", IrqTriggerMode.Edge);
        provider.BeforeIrqBindEffect = () =>
        {
            provider.BeforeIrqBindEffect = null;
            var close = concurrent
                ? Task.Run(() => kernel.CloseKernelEventEndpoint(subject, endpoint)).GetAwaiter().GetResult()
                : kernel.CloseKernelEventEndpoint(subject, endpoint);
            Assert.Equal(KernelError.PlatformBindingDraining, close.Error);
        };
        var result = kernel.BindPlatformInterrupt(subject, device, capability, endpoint);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(1, provider.BindCalls);
        Assert.True(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IrqAdmissionFailureBalancesEndpointReservationWithoutClosingUnknownEffect(bool receiptLost)
    {
        var provider = new IrqProvider { ThrowOnIrqBind = receiptLost };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1323, 1530);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/failure-endpoint", "pending", IrqTriggerMode.Edge);
        if (!receiptLost) Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        var result = kernel.BindPlatformInterrupt(subject, device, capability, endpoint);
        Assert.Equal(receiptLost ? KernelError.PlatformFaulted : KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(receiptLost ? 1 : 0, provider.BindCalls);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var registry = typeof(RuntimeKernel).GetField("_kernelEvents", flags)!.GetValue(kernel)!;
        var endpoints = (global::System.Collections.IDictionary)registry.GetType().GetField("_endpoints", flags)!.GetValue(registry)!;
        var originalEndpoint = endpoints[endpoint.EndpointId]!;
        Assert.Equal(0, originalEndpoint.GetType().GetProperty("PendingIrqAdmissions")!.GetValue(originalEndpoint));
        Assert.Equal(!receiptLost, kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        if (receiptLost)
        {
            Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, device).Error);
            Assert.Equal(0, provider.DeviceRevokeCalls);
            Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        }
        else Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void IrqAdmissionRootRevokeNeverPublishesPermissionOrReportsUnknownClosure(int fault)
    {
        var provider = new IrqProvider { ThrowOnIrqBind = fault == 3, ThrowOnIrqRevoke = fault == 5 };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1324, 1540);
        var (device, _, endpoint) = CreateAuthorities(kernel, subject,
            "device/revoke-pending", "pending", IrqTriggerMode.Edge);
        var root = Mint(kernel, subject, ResourceKind.Irq,
            CapabilityResourceIds.Irq("device/revoke-pending", "pending", IrqTriggerMode.Edge),
            CapabilityRights.Signal | CapabilityRights.Delegate);
        var child = kernel.DelegateCapability(subject, subject, root,
            CapabilityRights.Signal | CapabilityRights.Delegate).Value!.CapabilityId;
        child = kernel.DelegateCapability(subject, subject, child, CapabilityRights.Signal).Value!.CapabilityId;
        provider.BeforeIrqBindEffect = () =>
        {
            provider.BeforeIrqBindEffect = null;
            if (fault == 2) Assert.True(kernel.CapabilityAuthority.Revoke(root).IsSuccess);
            else
            {
                var revoke = fault == 1 ? Task.Run(() => kernel.RevokeCapability(root)).GetAwaiter().GetResult()
                    : kernel.RevokeCapability(root);
                Assert.Equal(KernelError.PlatformBindingDraining, revoke.Error);
            }
            if (fault == 4) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        var result = kernel.BindPlatformInterrupt(subject, device, child, endpoint);
        var irqRecords = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_irqBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        Assert.Equal(fault is 4 or 5 ? 1 : 0, irqRecords.Count);
        Assert.Empty((global::System.Collections.IEnumerable)typeof(RuntimeKernel)
            .GetField("_pendingPlatformIrqCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!);
        Assert.Equal(fault is 3 or 4 ? KernelError.PlatformFaulted : KernelError.CapabilityRevoked, result.Error);
        Assert.Equal(1, provider.BindCalls);
        provider.ThrowOnIrqBind = false;
        provider.ThrowOnIrqRevoke = false;
        if (fault >= 3)
        {
            for (var retry = 0; retry < 2; retry++) Assert.False(kernel.RevokeCapability(root).IsSuccess);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        }
        else
        {
            Assert.Equal(1, provider.IrqRevokeCalls);
            Assert.True(kernel.RevokeCapability(root).IsSuccess);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        }
        var before = provider.BindCalls;
        Assert.False(kernel.BindPlatformInterrupt(subject, device, child, endpoint).IsSuccess);
        Assert.Equal(before, provider.BindCalls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IrqAdmissionNotAcceptedNeedsExactPostCallbackContinuity(bool reset)
    {
        var provider = new IrqProvider { IrqBindStatus = PlatformAuthorityStatus.NotAccepted };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1325, 1550);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/not-accepted-irq", "pending", IrqTriggerMode.Edge);
        provider.BeforeIrqBindEffect = () =>
        {
            provider.BeforeIrqBindEffect = null;
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(capability).Error);
            if (reset) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        Assert.False(kernel.BindPlatformInterrupt(subject, device, capability, endpoint).IsSuccess);
        Assert.Equal(1, provider.BindCalls);
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.False(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Fact]
    public void ProcessExitDuringIrqAdmissionRejectsAndClosesKnownReceipt()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1326, 1560);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/exiting-irq", "pending", IrqTriggerMode.Edge);
        provider.BeforeIrqBindEffect = () =>
        {
            provider.BeforeIrqBindEffect = null;
            Assert.False(kernel.TerminateProcess(subject).IsSuccess);
        };
        Assert.Equal(KernelError.InvalidTransition,
            kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Error);
        Assert.Equal(1, provider.BindCalls);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.True(kernel.TerminateProcess(subject).IsSuccess);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(subject).Error);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IrqAdmissionGenerationGetterRevocationPreventsPublication(int read)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1327, 1570);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/getter-irq", "pending", IrqTriggerMode.Edge);
        var reads = 0;
        provider.IncarnationRead = () =>
        {
            if (++reads != read) return;
            provider.IncarnationRead = null;
            Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(capability).Error);
        };
        Assert.Equal(KernelError.CapabilityRevoked,
            kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Error);
        Assert.Equal(read == 1 ? 0 : 1, provider.BindCalls);
        Assert.Equal(read == 1 ? 0 : 1, provider.IrqRevokeCalls);
        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.True(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IrqClosureCallbackBlocksOverlappingClosureAndNewPoll(bool concurrent, bool fault)
    {
        var provider = new IrqProvider { ThrowOnIrqRevoke = fault };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1328, 1580);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/closing-route", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        KernelResult nested = default;
        KernelError poll = default;
        provider.BeforeIrqRevokeEffect = () =>
        {
            provider.BeforeIrqRevokeEffect = null;
            nested = concurrent ? Task.Run(() => kernel.RevokePlatformInterrupt(subject, route)).GetAwaiter().GetResult()
                : kernel.RevokePlatformInterrupt(subject, route);
            poll = kernel.PollPlatformInterrupt(subject, route).Error;
        };
        var result = kernel.RevokePlatformInterrupt(subject, route);
        Assert.Equal(KernelError.PlatformBindingActive, nested.Error);
        Assert.Equal(KernelError.CapabilityRevoked, poll);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.PollCalls);
        if (fault)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            provider.ThrowOnIrqRevoke = false;
            Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformInterrupt(subject, route).Error);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        }
    }
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    public void IrqClosureGenerationGetterInterlockPreservesFailureAndExactState(int read, int fault)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1329, 1590);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/closure-getter", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        var reads = 0;
        provider.IncarnationRead = () =>
        {
            if (++reads != read) return;
            provider.IncarnationRead = null;
            Assert.Equal(KernelError.PlatformBindingActive, kernel.RevokePlatformInterrupt(subject, route).Error);
            Assert.Equal(KernelError.CapabilityRevoked, kernel.PollPlatformInterrupt(subject, route).Error);
            if (fault == 1) throw new InvalidOperationException("Lost IRQ closure generation read.");
            if (fault == 2) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
        };
        var result = kernel.RevokePlatformInterrupt(subject, route);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_irqBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.PlatformAuthority)!;
        var record = records[route.BindingId]!;
        Assert.False((bool)record.GetType().GetProperty("ClosureInFlight")!.GetValue(record)!);
        Assert.Equal(0, provider.PollCalls);
        if (fault == 0)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(1, provider.IrqRevokeCalls);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        }
        else
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(read == 1 ? 0 : 1, provider.IrqRevokeCalls);
            if (read == 2 && fault == 1)
            {
                Assert.True(kernel.RevokeCapability(capability).IsSuccess);
                Assert.Equal(1, provider.IrqRevokeCalls);
                Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
                return;
            }
            Assert.False(kernel.RevokeCapability(capability).IsSuccess);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IrqDeliveryUsesFreshPermissionAndDoesNotCloseDuringAdmittedEffect(int phase)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1330, 1600);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/delivery-permission", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        Assert.True(provider.Signal("pending"));
        if (phase == 0) Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
        else
        {
            void Revoke()
            {
                provider.BeforeIrqPollEffect = null;
                provider.BeforeIrqCompleteEffect = null;
                Assert.Equal(KernelError.PlatformBindingDraining, kernel.RevokeCapability(capability).Error);
            }
            if (phase == 1) provider.BeforeIrqPollEffect = Revoke;
            else provider.BeforeIrqCompleteEffect = Revoke;
        }
        var result = kernel.PollPlatformInterrupt(subject, route);
        Assert.Equal(phase == 0 ? 0 : 1, provider.PollCalls);
        Assert.Equal(phase == 2 ? 1 : 0, provider.CompleteCalls);
        if (phase == 2)
        {
            // Completion was admitted before revoke; publication consumes the exact already-staged mailbox ticket.
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Value.DeliveryAvailable);
            Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        }
        else
        {
            Assert.Equal(KernelError.CapabilityRevoked, result.Error);
            if (phase == 1) Assert.False(kernel.RevokeCapability(capability).IsSuccess);
            else Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        }
        var polls = provider.PollCalls;
        Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
        Assert.Equal(polls, provider.PollCalls);
        if (phase == 1) Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        else Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void IrqDeliveryGenerationGetterRevokeDeniesNextEffect(int read)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1332, 1620);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/delivery-getter", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        Assert.True(provider.Signal("pending"));
        var reads = 0;
        provider.IncarnationRead = () =>
        {
            if (++reads != read) return;
            provider.IncarnationRead = null;
            Assert.True(kernel.CapabilityAuthority.Revoke(capability).IsSuccess);
        };
        Assert.Equal(KernelError.CapabilityRevoked, kernel.PollPlatformInterrupt(subject, route).Error);
        Assert.Equal(read == 1 ? 0 : 1, provider.PollCalls);
        Assert.Equal(0, provider.CompleteCalls);
        Assert.Equal(KernelError.ResponseNotAvailable, kernel.ConsumeKernelEvent(subject, endpoint).Error);
        if (read == 1) Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        else Assert.False(kernel.RevokeCapability(capability).IsSuccess);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IrqDeliveryResetOrExceptionKeepsExactRoutePinnedAndBalancesAdmission(bool complete, bool reset)
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1333, 1630);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/delivery-loss", "pending", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        Assert.True(provider.Signal("pending"));
        void LoseContinuity()
        {
            if (reset) Assert.True(kernel.PlatformAuthority.ObserveBackendReset().IsSuccess);
            else throw new InvalidOperationException("Injected ambiguous IRQ delivery effect.");
        }
        if (complete) provider.BeforeIrqCompleteEffect = LoseContinuity;
        else provider.BeforeIrqPollEffect = LoseContinuity;
        Assert.Equal(KernelError.PlatformFaulted, kernel.PollPlatformInterrupt(subject, route).Error);
        var records = (global::System.Collections.IDictionary)typeof(PlatformAuthorityBridge)
            .GetField("_irqBindings", global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.NonPublic)!
            .GetValue(kernel.PlatformAuthority)!;
        var record = records[route.BindingId]!;
        Assert.False((bool)record.GetType().GetProperty("DeliveryInFlight")!.GetValue(record)!);
        Assert.False(kernel.PollPlatformInterrupt(subject, route).IsSuccess);
        Assert.Equal(1, provider.PollCalls);
        Assert.Equal(complete ? 1 : 0, provider.CompleteCalls);
        Assert.False(kernel.RevokeCapability(capability).IsSuccess);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(0, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IrqCapabilityClosureCallbackAllowsWorkerAndRetainsFaultPins(bool fault)
    {
        var provider = new IrqProvider { ThrowOnIrqRevoke = fault };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1340, 1700);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/capability-closure", "tick", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;
        var observed = false;
        provider.BeforeIrqRevokeEffect = () =>
        {
            var worker = Task.Run(() =>
            {
                Assert.True(kernel.MintCapability(new DomainId(1700), subject,
                    ResourceKind.Compute, "compute/irq-closure", CapabilityRights.Execute).IsSuccess);
                Assert.Equal(KernelError.PlatformBindingActive,
                    kernel.RevokePlatformInterrupt(subject, route).Error);
                Assert.Equal(KernelError.CapabilityRevoked,
                    kernel.PollPlatformInterrupt(subject, route).Error);
            });
            Assert.True(worker.Wait(TimeSpan.FromSeconds(5)));
            observed = true;
        };
        var result = kernel.RevokeCapability(capability);
        Assert.True(observed);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(!fault, result.IsSuccess);
        if (fault)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            provider.ThrowOnIrqRevoke = false;
            Assert.False(kernel.RevokeCapability(capability).IsSuccess);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
            Assert.Equal(1, provider.IrqRevokeCalls);
        }
        else
        {
            Assert.Equal(KernelError.PlatformBindingRevoked,
                kernel.RevokePlatformInterrupt(subject, route).Error);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        }
    }

    [Fact]
    public void IrqCapabilityRevokeClosesRouteWithoutClosingDevice()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1306, 1360);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            subject,
            "device/timer0",
            "tick",
            IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint).Value!;

        var revoke = kernel.RevokeCapability(irqCapability);

        Assert.True(revoke.IsSuccess, revoke.Message);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(
            KernelError.PlatformBindingRevoked,
            kernel.RevokePlatformInterrupt(subject, route).Error);
        Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
    }

    [Fact]
    public void ProcessTeardownClosesIrqThenDeviceThenDomain()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1307, 1370);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            subject,
            "device/controller0",
            "notify",
            IrqTriggerMode.Level);
        Assert.True(kernel.BindPlatformInterrupt(
            subject, device, irqCapability, endpoint).IsSuccess);

        var terminate = kernel.TerminateProcess(subject);

        Assert.True(terminate.IsSuccess, terminate.Message);
        Assert.Equal(
            new[]
            {
                "bind-irq:notify",
                "revoke-irq:notify",
                "revoke-device:device/controller0",
                "revoke-domain",
            },
            provider.Log);
    }

    [Fact]
    public void InterruptCloseFaultPinsTeardownBeforeDeviceDomainAndEventReclaim()
    {
        var provider = new IrqProvider { IrqRevokeStatus = PlatformAuthorityStatus.Faulted };
        var kernel = new RuntimeKernel(provider);
        var (process, subject) = TestFixtures.Create(kernel, 1308, 1380);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            subject,
            "device/fault0",
            "fault",
            IrqTriggerMode.Edge);
        Assert.True(kernel.BindPlatformInterrupt(
            subject, device, irqCapability, endpoint).IsSuccess);

        var terminate = kernel.TerminateProcess(subject);

        Assert.Equal(KernelError.PlatformFaulted, terminate.Error);
        Assert.Equal(ProcessState.Exiting, process.State);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.Equal(0, provider.DeviceRevokeCalls);
        Assert.Equal(0, provider.DomainRevokeCalls);
        Assert.Equal(ProcessTeardownPhase.PlatformFaulted, kernel.QueryProcessTeardown(subject).Value!.Phase);
        Assert.Equal(KernelError.InvalidTransition, kernel.ConsumeKernelEvent(subject, endpoint).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevokedOrThrowingInterruptClosureKeepsDevicePinned(bool throws)
    {
        var provider = new IrqProvider
        {
            IrqRevokeStatus = throws ? null : PlatformAuthorityStatus.Revoked,
            ThrowOnIrqRevoke = throws,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, throws ? 1317UL : 1318UL,
            throws ? 1470UL : 1480UL);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/ambiguous-irq", "signal", IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Value!;

        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformInterrupt(subject, route).Error);
        provider.IrqRevokeStatus = null;
        provider.ThrowOnIrqRevoke = false;
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformInterrupt(subject, route).Error);
        Assert.Equal(1, provider.IrqRevokeCalls);
        Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedInterruptCleanupWithoutExactSuccessPinsParentDevice(bool throws)
    {
        var provider = new IrqProvider
        {
            ReturnWrongSource = true,
            IrqRevokeStatus = throws ? null : PlatformAuthorityStatus.Revoked,
            ThrowOnIrqRevoke = throws,
        };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, throws ? 1319UL : 1320UL,
            throws ? 1490UL : 1500UL);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/malformed-irq", "signal", IrqTriggerMode.Edge);

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Error);
        Assert.Equal(1, provider.IrqRevokeCalls);
        provider.IrqRevokeStatus = null;
        provider.ThrowOnIrqRevoke = false;
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, device).Error);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Fact]
    public void InterruptBindReceiptLossPinsParentDevice()
    {
        var provider = new IrqProvider { ThrowOnIrqBind = true };
        var kernel = new RuntimeKernel(provider);
        var (_, subject) = TestFixtures.Create(kernel, 1321, 1510);
        var (device, capability, endpoint) = CreateAuthorities(kernel, subject,
            "device/lost-irq-receipt", "signal", IrqTriggerMode.Edge);

        Assert.Equal(KernelError.PlatformFaulted,
            kernel.BindPlatformInterrupt(subject, device, capability, endpoint).Error);
        provider.ThrowOnIrqBind = false;
        Assert.Equal(KernelError.PlatformFaulted, kernel.RevokePlatformDevice(subject, device).Error);
        Assert.Equal(0, provider.DeviceRevokeCalls);
    }

    [Fact]
    public async Task ProcessReclaimWaitsForAdmittedCompletionBeforePlatformClosure()
    {
        var provider = new IrqProvider();
        var kernel = new RuntimeKernel(provider);
        var (process, subject) = TestFixtures.Create(kernel, 1316, 1460);
        var (device, irqCapability, endpoint) = CreateAuthorities(
            kernel,
            subject,
            "device/drain0",
            "pending",
            IrqTriggerMode.Edge);
        var route = kernel.BindPlatformInterrupt(
            subject,
            device,
            irqCapability,
            endpoint).Value!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        provider.CompletionEntered = entered;
        provider.CompletionRelease = release;
        var wait = kernel.WaitForKernelEventAsync(subject, endpoint).AsTask();

        Assert.True(provider.Signal("pending"));
        var poll = Task.Run(() => kernel.PollPlatformInterrupt(subject, route));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

            Assert.Equal(KernelError.PlatformBindingActive, kernel.PollPlatformInterrupt(subject, route).Error);
            Assert.Equal(1, provider.PollCalls);
            Assert.Equal(1, provider.CompleteCalls);

            var terminate = kernel.TerminateProcess(subject);

            Assert.False(terminate.IsSuccess);
            Assert.Equal(KernelError.PlatformBindingDraining, terminate.Error);
            Assert.Equal(ProcessState.Exiting, process.State);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
            var snapshot = kernel.QueryProcessTeardown(subject);
            Assert.True(snapshot.IsSuccess, snapshot.Message);
            Assert.Equal(ProcessTeardownPhase.PlatformDraining, snapshot.Value!.Phase);
            Assert.False(snapshot.Value.PlatformDomainClosed);
            Assert.Equal(0, provider.IrqRevokeCalls);
            Assert.Equal(0, provider.DeviceRevokeCalls);
            Assert.False(snapshot.Value.LocalReclaimCompleted);
            Assert.True(kernel.Processes.Resolve(subject).IsSuccess);
        }
        finally
        {
            release.Set();
        }

        var delivery = await poll;
        Assert.True(delivery.IsSuccess, delivery.Message);
        Assert.Equal(1, provider.CompleteCalls);

        var observed = kernel.ObserveProcessTeardown(subject);
        Assert.True(observed.IsSuccess, observed.Message);
        Assert.True(observed.Value!.LocalReclaimCompleted);
        Assert.Equal(ProcessState.Exited, process.State);
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(subject).Error);
    }

    [Fact]
    public void PublicIrqAndKernelEventSurfacesCarryNoProviderOrRawHardwareIdentity()
    {
        var surface = new[]
        {
            typeof(IrqTriggerMode),
            typeof(IrqResourceId),
            typeof(KernelEventEndpointId),
            typeof(KernelEventEndpointGeneration),
            typeof(KernelEventEndpoint),
            typeof(KernelEventSequence),
            typeof(KernelEvent),
            typeof(PlatformInterruptTrigger),
            typeof(PlatformInterruptSourceIdentity),
            typeof(PlatformIrqBindingId),
            typeof(PlatformIrqBindingGeneration),
            typeof(PlatformIrqBinding),
            typeof(PlatformInterruptPollResult),
        };
        var forbidden = new[]
        {
            "PlatformProvider",
            "HybridCPU",
            "Neutral",
            "Vector",
            "Controller",
            "Apic",
            "Gic",
            "Msi",
            "Gsi",
            "Physical",
            "PageTable",
            "Pte",
            "Iommu",
            "DmaWindow",
            "Vmcs",
            "Vmx",
            "Lane",
            "Opcode",
            "Queue",
        };

        foreach (var type in surface)
        foreach (var member in type.GetMembers(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var signature = member.ToString() ?? member.Name;
            foreach (var term in forbidden)
                Assert.DoesNotContain(term, signature, StringComparison.OrdinalIgnoreCase);
        }

        var wait = typeof(RuntimeKernel).GetMethod(
            nameof(RuntimeKernel.WaitForKernelEventAsync),
            [typeof(ProcessHandle), typeof(KernelEventEndpoint), typeof(CancellationToken)]);
        Assert.NotNull(wait);
        Assert.Equal(
            typeof(ValueTask<KernelResult<KernelEvent>>),
            wait.ReturnType);
        Assert.Equal(
            new[] { typeof(ProcessHandle), typeof(KernelEventEndpoint), typeof(CancellationToken) },
            wait.GetParameters().Select(static parameter => parameter.ParameterType));
        var waitSignature = wait.ToString()!;
        foreach (var term in forbidden)
            Assert.DoesNotContain(term, waitSignature, StringComparison.OrdinalIgnoreCase);
    }

    private static (
        PlatformDeviceLease Device,
        CapabilityId IrqCapability,
        KernelEventEndpoint Endpoint) CreateAuthorities(
        RuntimeKernel kernel,
        ProcessHandle subject,
        string deviceResourceId,
        string sourceResourceId,
        IrqTriggerMode trigger)
    {
        var binding = kernel.BindPlatformDomain(subject).Value!;
        var deviceCapability = Mint(
            kernel,
            subject,
            ResourceKind.Device,
            deviceResourceId,
            CapabilityRights.Configure);
        var device = kernel.BindPlatformDevice(
            subject,
            binding,
            deviceCapability,
            PlatformDeviceRights.Configure).Value!;
        var irqCapability = Mint(
            kernel,
            subject,
            ResourceKind.Irq,
            CapabilityResourceIds.Irq(deviceResourceId, sourceResourceId, trigger),
            CapabilityRights.Signal);
        var endpoint = kernel.CreateKernelEventEndpoint(subject).Value!;
        return (device, irqCapability, endpoint);
    }

    private static CapabilityId Mint(
        RuntimeKernel kernel,
        ProcessHandle subject,
        ResourceKind kind,
        string resourceId,
        CapabilityRights rights)
    {
        var process = kernel.Processes.Resolve(subject);
        Assert.True(process.IsSuccess, process.Message);
        var minted = kernel.MintCapability(
            process.Value!.DomainId,
            subject,
            kind,
            resourceId,
            rights);
        Assert.True(minted.IsSuccess, minted.Message);
        return minted.Value!.CapabilityId;
    }

    private sealed class IrqProvider :
        IPlatformAuthorityProvider,
        IPlatformFeatureProvider,
        IPlatformDeviceLeaseProvider,
        IPlatformIrqBindingProvider,
        IPlatformProviderIncarnationSource
    {
        private sealed class IrqRecord(PlatformProviderIrqBinding binding)
        {
            public PlatformProviderIrqBinding Binding { get; } = binding;
            public PlatformProviderInterruptDeliverySequence Pending { get; set; }
        }

        private readonly Dictionary<PlatformProviderDomainLeaseId, PlatformProviderDomainLease> _domains = [];
        private readonly Dictionary<PlatformProviderDeviceLeaseId, PlatformProviderDeviceLease> _devices = [];
        private readonly Dictionary<PlatformProviderIrqBindingId, IrqRecord> _irqs = [];
        private ulong _nextDomain = 1;
        private ulong _nextDevice = 1;
        private ulong _nextIrq = 1;
        private ulong _nextDelivery = 1;

        public bool ReturnWrongSource { get; set; }
        public Func<PlatformProviderIrqBinding, PlatformProviderIrqBinding>? RewriteBindReceipt { get; set; }
        public bool ThrowOnIrqBind { get; set; }
        public PlatformAuthorityStatus? IrqRevokeStatus { get; set; }
        public PlatformAuthorityStatus? IrqRevokeReplyStatus { get; set; }
        public bool ThrowOnIrqRevoke { get; set; }
        public PlatformAuthorityStatus? CompletionStatus { get; set; }
        public ManualResetEventSlim? CompletionEntered { get; set; }
        public ManualResetEventSlim? CompletionRelease { get; set; }
        public ulong Incarnation { get; set; } = 1;
        public Action? IncarnationRead { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get { IncarnationRead?.Invoke(); return new PlatformProviderIncarnation(Incarnation); }
        }
        public PlatformAuthorityStatus? IrqBindStatus { get; set; }
        public PlatformAuthorityStatus? IrqBindReplyStatus { get; set; }
        public Action? AfterIrqBindEffect { get; set; }
        public Action? BeforeIrqPollEffect { get; set; }
        public Func<PlatformInterruptDeliveryObservation, PlatformInterruptDeliveryObservation>? RewritePollReceipt { get; set; }
        public PlatformAuthorityStatus? PollStatus { get; set; }
        public Action? BeforeIrqCompleteEffect { get; set; }
        public Action? AfterIrqCompleteEffect { get; set; }
        public PlatformAuthorityStatus? CompletionReplyStatus { get; set; }
        public Action? BeforeIrqRevokeEffect { get; set; }
        public Action? AfterIrqRevokeEffect { get; set; }
        public Action? BeforeDeviceRevokeEffect { get; set; }
        public Action? BeforeIrqBindEffect { get; set; }
        public int BindCalls { get; private set; }
        public int PollCalls { get; private set; }
        public int CompleteCalls { get; private set; }
        public int IrqRevokeCalls { get; private set; }
        public int DeviceRevokeCalls { get; private set; }
        public int DomainRevokeCalls { get; private set; }
        public List<string> Log { get; } = [];

        public PlatformProviderDescriptor Descriptor { get; } = new(
            new PlatformProviderId("irq-test"),
            1,
            PlatformAuthorityFeatures.NeutralDomainBinding);

        public PlatformFeatureManifest QueryFeatures() => new(new[]
        {
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.NeutralDomains,
                PlatformDomainContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.IoDomainBinding,
                PlatformDeviceLeaseContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
            new PlatformFeatureDescriptor(
                PlatformFeatureFamily.IrqBinding,
                PlatformIrqBindingContract.ContractVersion,
                PlatformFeatureAvailability.Executable),
        });

        public PlatformAuthorityResult<PlatformProviderDomainLease> BindDomain(PlatformDomainIdentity subject)
        {
            var lease = new PlatformProviderDomainLease(
                new PlatformProviderDomainLeaseId(_nextDomain++),
                new PlatformProviderLeaseGeneration(1),
                subject);
            _domains.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDomainLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDomain(PlatformProviderDomainLease lease)
        {
            if (_devices.Values.Any(device => device.DomainLease == lease))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Device remains live.");
            DomainRevokeCalls++;
            Log.Add("revoke-domain");
            _domains.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderDeviceLease> BindDevice(
            PlatformProviderDomainLease domainLease,
            PlatformDeviceIdentity device,
            PlatformDeviceRights rights)
        {
            var lease = new PlatformProviderDeviceLease(
                new PlatformProviderDeviceLeaseId(_nextDevice++),
                new PlatformProviderLeaseGeneration(1),
                domainLease,
                device,
                rights);
            _devices.Add(lease.LeaseId, lease);
            return PlatformAuthorityResult<PlatformProviderDeviceLease>.Ok(lease);
        }

        public PlatformAuthorityResult RevokeDevice(PlatformProviderDeviceLease lease)
        {
            BeforeDeviceRevokeEffect?.Invoke();
            if (_irqs.Values.Any(irq => irq.Binding.DeviceLease == lease))
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Denied, "Interrupt route remains live.");
            DeviceRevokeCalls++;
            Log.Add($"revoke-device:{lease.Device.ResourceId}");
            _devices.Remove(lease.LeaseId);
            return PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult<PlatformProviderIrqBinding> BindInterrupt(
            PlatformProviderDeviceLease deviceLease,
            PlatformInterruptSourceIdentity source)
        {
            BindCalls++;
            BeforeIrqBindEffect?.Invoke();
            if (IrqBindStatus is { } bindStatus)
                return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(bindStatus, "Injected IRQ admission reply.");
            Log.Add($"bind-irq:{source.ResourceId}");
            var returnedSource = ReturnWrongSource
                ? new PlatformInterruptSourceIdentity(source.ResourceId + "-wrong", source.Trigger)
                : source;
            var binding = new PlatformProviderIrqBinding(
                new PlatformProviderIrqBindingId(_nextIrq++),
                new PlatformProviderLeaseGeneration(1),
                deviceLease,
                returnedSource);
            _irqs.Add(binding.BindingId, new IrqRecord(binding));
            AfterIrqBindEffect?.Invoke();
            if (IrqBindReplyStatus is { } replyStatus)
                return PlatformAuthorityResult<PlatformProviderIrqBinding>.Fail(replyStatus, "Original bind failed after effect.");
            if (ThrowOnIrqBind) throw new InvalidOperationException("Injected interrupt bind receipt loss.");
            return PlatformAuthorityResult<PlatformProviderIrqBinding>.Ok(RewriteBindReceipt?.Invoke(binding) ?? binding);
        }

        public PlatformAuthorityResult<PlatformInterruptDeliveryObservation> PollInterrupt(
            PlatformProviderIrqBinding binding)
        {
            PollCalls++;
            BeforeIrqPollEffect?.Invoke();
            if (!_irqs.TryGetValue(binding.BindingId, out var record) || record.Binding != binding)
            {
                return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "Unknown interrupt binding.");
            }

            if (PollStatus is { } status)
                return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Fail(status, "Original poll response failed");
            var observation = new PlatformInterruptDeliveryObservation(binding, record.Pending.Value != 0, record.Pending);
            return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Ok(RewritePollReceipt?.Invoke(observation) ?? observation);
        }

        public PlatformAuthorityResult CompleteInterruptDelivery(
            PlatformProviderIrqBinding binding,
            PlatformProviderInterruptDeliverySequence sequence)
        {
            CompleteCalls++;
            BeforeIrqCompleteEffect?.Invoke();
            if (!_irqs.TryGetValue(binding.BindingId, out var record) || record.Binding != binding)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Faulted, "Unknown interrupt binding.");
            if (record.Pending.Value == 0 || record.Pending != sequence)
                return PlatformAuthorityResult.Fail(PlatformAuthorityStatus.Stale, "Wrong delivery sequence.");
            CompletionEntered?.Set();
            if (CompletionRelease is { } release && !release.Wait(TimeSpan.FromSeconds(10)))
            {
                return PlatformAuthorityResult.Fail(
                    PlatformAuthorityStatus.Faulted,
                    "Timed out waiting for the test completion gate.");
            }
            if (CompletionStatus is { } injected)
                return PlatformAuthorityResult.Fail(injected, "Injected completion fault.");
            record.Pending = default;
            AfterIrqCompleteEffect?.Invoke();
            return CompletionReplyStatus is { } reply ? PlatformAuthorityResult.Fail(reply, "Completion reply failed after ack") : PlatformAuthorityResult.Ok();
        }

        public PlatformAuthorityResult RevokeInterrupt(PlatformProviderIrqBinding binding)
        {
            BeforeIrqRevokeEffect?.Invoke();
            IrqRevokeCalls++;
            Log.Add($"revoke-irq:{binding.Source.ResourceId}");
            if (ThrowOnIrqRevoke) throw new InvalidOperationException("Injected interrupt revoke receipt loss.");
            if (IrqRevokeStatus is { } injected)
                return PlatformAuthorityResult.Fail(injected, "Injected interrupt revoke fault.");
            _irqs.Remove(binding.BindingId);
            AfterIrqRevokeEffect?.Invoke();
            return IrqRevokeReplyStatus is { } reply ? PlatformAuthorityResult.Fail(reply, "Revoke reply failed after effect") : PlatformAuthorityResult.Ok();
        }

        public bool Signal(string sourceResourceId)
        {
            var record = _irqs.Values.SingleOrDefault(irq =>
                string.Equals(irq.Binding.Source.ResourceId, sourceResourceId, StringComparison.Ordinal));
            if (record is null || record.Pending.Value != 0) return false;
            record.Pending = new PlatformProviderInterruptDeliverySequence(_nextDelivery++);
            return true;
        }

        public bool IsPending(string sourceResourceId) =>
            _irqs.Values.Any(irq =>
                string.Equals(irq.Binding.Source.ResourceId, sourceResourceId, StringComparison.Ordinal) &&
                irq.Pending.Value != 0);

        public PlatformAuthorityResult<PlatformProviderRegionMappingLease> MapOwnedRegion(
            PlatformProviderDomainLease domainLease,
            PlatformRegionIdentity region,
            PlatformMemoryAccess access) =>
            PlatformAuthorityResult<PlatformProviderRegionMappingLease>.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not used by IRQ tests.");

        public PlatformAuthorityResult RevokeRegionMapping(
            PlatformProviderRegionMappingLease mapping,
            PlatformRegionRevocationPolicy policy) =>
            PlatformAuthorityResult.Fail(
                PlatformAuthorityStatus.Unsupported,
                "Not used by IRQ tests.");
    }
}
