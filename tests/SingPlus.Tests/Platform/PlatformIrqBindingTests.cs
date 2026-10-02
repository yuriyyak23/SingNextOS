using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Runtime;

namespace SingPlus.Tests.Platform;

public sealed class PlatformIrqBindingTests
{
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
    public async Task NotAcceptedInterruptCompletionRollsBackInvisibleReservationBeforeRetry()
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
        var wait = kernel.WaitForKernelEventAsync(subject, endpoint).AsTask();

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
        Assert.True(retry.IsSuccess, retry.Message);
        var received = await wait;
        Assert.True(received.IsSuccess, received.Message);
        Assert.Equal(retry.Value!.Event, received.Value);
        Assert.Equal(
            KernelError.ResponseNotAvailable,
            kernel.ConsumeKernelEvent(subject, endpoint).Error);
        Assert.False(provider.IsPending("notify"));
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
        Assert.True(kernel.CloseKernelEventEndpoint(subject, endpoint).IsSuccess);
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
        if (reset)
        {
            Assert.False(kernel.RevokeCapability(capability).IsSuccess);
            Assert.False(kernel.RevokePlatformDevice(subject, device).IsSuccess);
            Assert.Equal(0, provider.DeviceRevokeCalls);
        }
        else
        {
            Assert.True(kernel.RevokeCapability(capability).IsSuccess);
            Assert.True(kernel.RevokePlatformDevice(subject, device).IsSuccess);
        }
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
        public bool ThrowOnIrqBind { get; set; }
        public PlatformAuthorityStatus? IrqRevokeStatus { get; set; }
        public bool ThrowOnIrqRevoke { get; set; }
        public PlatformAuthorityStatus? CompletionStatus { get; set; }
        public ManualResetEventSlim? CompletionEntered { get; set; }
        public ManualResetEventSlim? CompletionRelease { get; set; }
        public Action? IncarnationRead { get; set; }
        public PlatformProviderIncarnation CurrentIncarnation
        {
            get { IncarnationRead?.Invoke(); return new PlatformProviderIncarnation(1); }
        }
        public PlatformAuthorityStatus? IrqBindStatus { get; set; }
        public Action? BeforeIrqPollEffect { get; set; }
        public Action? BeforeIrqCompleteEffect { get; set; }
        public Action? BeforeIrqRevokeEffect { get; set; }
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
            if (ThrowOnIrqBind) throw new InvalidOperationException("Injected interrupt bind receipt loss.");
            return PlatformAuthorityResult<PlatformProviderIrqBinding>.Ok(binding);
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

            return PlatformAuthorityResult<PlatformInterruptDeliveryObservation>.Ok(
                new PlatformInterruptDeliveryObservation(
                    binding,
                    record.Pending.Value != 0,
                    record.Pending));
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
            return PlatformAuthorityResult.Ok();
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
            return PlatformAuthorityResult.Ok();
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
