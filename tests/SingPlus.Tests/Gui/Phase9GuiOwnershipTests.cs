using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Gui;
using SingPlus.System;

namespace SingPlus.Tests.Gui;

public sealed class Phase9GuiOwnershipTests
{
    [Fact]
    public async Task CompositorSurfaceLastIdentityPublishesOnceAndNextAdmissionHasNoEffect()
    {
        var scenario = CreateScenario();
        var client = ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        typeof(RuntimeCompositorServiceHost).GetField("_nextSurface", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scenario.Host, ulong.MaxValue);
        var first = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!;
        var registering = client.RegisterSurfaceAsync(new(scenario.Capability, first.Handle, Metadata())).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        var surface = (await registering).Authority.Surface;
        Assert.Equal(ulong.MaxValue, surface.Identity.Value);
        var second = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!;
        var refused = client.RegisterSurfaceAsync(new(scenario.Capability, second.Handle, Metadata())).AsTask();
        Assert.Equal(KernelError.CapacityExhausted, scenario.Host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refused.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(SurfaceLifecycleState.Writable, scenario.Host.GetSurfaceState(surface));
        var surfaces = (global::System.Collections.IDictionary)typeof(RuntimeCompositorServiceHost)
            .GetField("_surfaces", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Host)!;
        Assert.Single(surfaces.Keys.Cast<object>());
        Assert.All(scenario.Kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Owned, region.State));
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Caller).IsSuccess);
        Assert.All(scenario.Kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Released, region.State));
    }

    [Fact]
    public async Task CompositorPresentLastIdentityRetainsOriginalPrepareAndRefusesNextBeforeLoan()
    {
        var scenario = CreateScenario();
        var client = ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!;
        var registering = client.RegisterSurfaceAsync(new(scenario.Capability, buffer.Handle, Metadata())).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        var surface = (await registering).Authority.Surface;
        typeof(RuntimeCompositorServiceHost).GetField("_nextOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scenario.Host, ulong.MaxValue);
        var prepared = client.PreparePresentAsync(new(surface, PresentTransferMode.ReadLease)).AsTask();
        scenario.Kernel.SessionInvocationSettlementReservedHook = () =>
        {
            Assert.Equal(KernelError.SessionDraining, scenario.Host.ProcessNext().Error);
            Assert.Equal(SurfaceLifecycleState.Writable, scenario.Host.GetSurfaceState(surface));
        };
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        await prepared;
        scenario.Kernel.SessionInvocationSettlementReservedHook = null;
        var presenting = client.PresentReadLeaseAsync(buffer).AsTask();
        Assert.True(scenario.Host.AcceptNext().IsSuccess);
        Assert.True(scenario.Host.CompleteAccepted().IsSuccess);
        var fence = (await presenting).Fence;
        Assert.True(fence.Terminal);
        Assert.Equal(ulong.MaxValue, fence.Operation.Value);
        var refused = client.PreparePresentAsync(new(surface, PresentTransferMode.ReadLease)).AsTask();
        Assert.Equal(KernelError.CapacityExhausted, scenario.Host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refused.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(SurfaceLifecycleState.Writable, scenario.Host.GetSurfaceState(surface));
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Caller).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
    }

    [Theory]
    [InlineData(0, false, false)] [InlineData(1, false, false)] [InlineData(2, false, false)]
    [InlineData(0, true, false)] [InlineData(1, true, false)] [InlineData(2, true, false)]
    [InlineData(0, true, true)]
    public async Task ExactCompositorBorrowReturnSurvivesFencePublicationRefusal(int fault, bool afterReservation, bool laterLoan)
    {
        var scenario = CreateScenario();
        var (buffer, surface, present) = await AcceptReadLeasePresent(scenario);
        var foreign = CreateScenario();
        var (_, foreignSurface, foreignPresent) = await AcceptReadLeasePresent(foreign);
        Assert.True(foreign.Host.CompleteAccepted().IsSuccess);
        Assert.True((await foreignPresent.WaitAsync(TimeSpan.FromSeconds(10))).Fence.Terminal);
        var registry = (EndpointSessionInvocationRegistry)typeof(RuntimeKernel)
            .GetProperty("SessionInvocations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Kernel)!;
        var foreignRegistry = (EndpointSessionInvocationRegistry)typeof(RuntimeKernel)
            .GetProperty("SessionInvocations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(foreign.Kernel)!;
        var routes = (global::System.Collections.IDictionary)typeof(EndpointSessionInvocationRegistry)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry)!;
        var foreignRoutes = (global::System.Collections.IDictionary)typeof(EndpointSessionInvocationRegistry)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(foreignRegistry)!;
        var work = typeof(RuntimeCompositorServiceHost).GetField("_accepted", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario.Host)!;
        var invocation = (EndpointSessionInvocationHandle)work.GetType().GetProperty("Invocation")!.GetValue(work)!;
        object key = (invocation.Session.SessionId, invocation.Session.Generation, invocation.InvocationId);
        var original = routes[key]!;
        void LoseRoute()
        {
            if (fault == 0) routes.Remove(key);
            else routes[key] = fault == 1 ? foreignRoutes[key] : original.GetType()
                .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null);
        }
        if (afterReservation) scenario.Kernel.SessionInvocationSettlementReservedHook = LoseRoute;
        else LoseRoute();
        Assert.False(scenario.Host.CompleteAccepted().IsSuccess);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal(SurfaceLifecycleState.Quarantined, scenario.Host.GetSurfaceState(surface));
        Assert.False(present.IsCompleted);
        Assert.False(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        Assert.Equal(SurfaceLifecycleState.Writable, foreign.Host.GetSurfaceState(foreignSurface));
        BorrowLeaseHandle? nextLoan = null;
        if (laterLoan)
        {
            var loan = scenario.Kernel.Regions.Loan(buffer.Handle,
                new RegionOwner(scenario.Kernel.Processes.Resolve(scenario.Caller).Value!.DomainId, scenario.Caller.Generation),
                new RegionOwner(scenario.Kernel.Processes.Resolve(scenario.Service).Value!.DomainId, scenario.Service.Generation));
            Assert.True(loan.IsSuccess);
            nextLoan = loan.Value;
        }
        routes[key] = original;
        scenario.Kernel.SessionInvocationSettlementReservedHook = null;
        Assert.True(scenario.Host.CompleteAccepted().IsSuccess);
        var fence = (await present.WaitAsync(TimeSpan.FromSeconds(10))).Fence;
        Assert.True(fence.Terminal);
        Assert.Equal(surface, fence.Surface);
        if (nextLoan is { } fresh)
        {
            Assert.Equal(RegionState.Loaned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
            Assert.False(scenario.Kernel.ReleaseRegion(scenario.Caller, buffer).IsSuccess);
            Assert.True(scenario.Kernel.ReturnBorrow(scenario.Service, fresh).IsSuccess);
        }
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Caller).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
    }

    private static async Task<(OwnedBuffer<byte> Buffer, SurfaceHandle Surface, Task<ReleaseFenceResponse> Present)> AcceptReadLeasePresent(Scenario scenario)
    {
        var client = ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!;
        var registration = client.RegisterSurfaceAsync(new(scenario.Capability, buffer.Handle, Metadata())).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        var surface = (await registration).Authority.Surface;
        var prepare = client.PreparePresentAsync(new(surface, PresentTransferMode.ReadLease)).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        await prepare;
        var present = client.PresentReadLeaseAsync(buffer).AsTask();
        Assert.True(scenario.Host.AcceptNext().IsSuccess);
        return (buffer, surface, present);
    }

    [Fact]
    public async Task CompositorCompletionExceptionRetainsOriginalWorkAndRefusesEffectRetry()
    {
        var scenario = CreateScenario();
        var client = ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!;
        var registration = client.RegisterSurfaceAsync(new(scenario.Capability, buffer.Handle, Metadata())).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        var surface = (await registration).Authority.Surface;
        var prepare = client.PreparePresentAsync(new(surface, PresentTransferMode.ReadLease)).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        await prepare;
        var present = client.PresentReadLeaseAsync(buffer).AsTask();
        Assert.True(scenario.Host.AcceptNext().IsSuccess);
        var calls = 0;
        scenario.Kernel.SessionInvocationSettlementReservedHook = () =>
        {
            calls++;
            Assert.Equal(KernelError.SessionDraining, scenario.Host.CompleteAccepted().Error);
            Assert.Equal(KernelError.SessionDraining, scenario.Host.Drain().Error);
            throw new InvalidOperationException("Original completion outcome unavailable.");
        };
        Assert.Throws<InvalidOperationException>(() => scenario.Host.CompleteAccepted());
        Assert.Equal(KernelError.ExternalEffectUncontained, scenario.Host.CompleteAccepted().Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, scenario.Host.AcceptNext().Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, scenario.Host.Drain().Error);
        Assert.False(present.IsCompleted);
        Assert.False(scenario.Kernel.TerminateProcess(scenario.Caller).IsSuccess);
        Assert.False(scenario.Kernel.QueryProcessTeardown(scenario.Caller).Value.LocalReclaimCompleted);
        Assert.NotEqual(RegionState.Released, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => present.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OriginalCompositorCompletionRejectsOverlapWithoutRepeatingBorrowRelease()
    {
        var scenario = CreateScenario();
        var client = ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!;
        var registration = client.RegisterSurfaceAsync(new(scenario.Capability, buffer.Handle, Metadata())).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        var surface = (await registration).Authority.Surface;
        var prepare = client.PreparePresentAsync(new(surface, PresentTransferMode.ReadLease)).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        await prepare;
        var present = client.PresentReadLeaseAsync(buffer).AsTask();
        Assert.True(scenario.Host.AcceptNext().IsSuccess);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        scenario.Kernel.SessionInvocationSettlementReservedHook = () =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        // This worker intentionally blocks inside a callback; keep unrelated
        // async session consumers off its scheduling dependency.
        var completion = Task.Factory.StartNew(scenario.Host.CompleteAccepted,
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
            Assert.Equal(KernelError.SessionDraining, scenario.Host.CompleteAccepted().Error);
            Assert.Equal(KernelError.SessionDraining, scenario.Host.AcceptNext().Error);
            Assert.Equal(KernelError.SessionDraining, scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).Error);
            Assert.Equal(SurfaceLifecycleState.Presented, scenario.Host.GetSurfaceState(surface));
            Assert.False(present.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True((await completion.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.True((await present.WaitAsync(TimeSpan.FromSeconds(10))).Fence.Terminal);
        scenario.Kernel.SessionInvocationSettlementReservedHook = null;
        Assert.Equal(1, calls);
        Assert.True(scenario.Kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        Assert.True(scenario.Kernel.TerminateProcess(scenario.Caller).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public async Task CpuReadLeaseUsesGeneratedSessionAndBlocksWritesUntilExactTerminalRelease()
    {
        var scenario = CreateScenario();
        var pixels = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!;
        pixels.Span.Fill(7);
        var compositor = new Compositor(ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session)), scenario.Capability);
        var register = compositor.RegisterAsync(pixels, Metadata()).AsTask();
        var registrationResult = scenario.Host.ProcessNext();
        Assert.True(registrationResult.IsSuccess, registrationResult.Message);
        var surface = await register;

        var present = compositor.PresentReadLeaseAsync(surface).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        Assert.True(SpinWait.SpinUntil(() => scenario.Kernel.Regions.Snapshot().Single(x => x.Handle.RegionId == pixels.Handle.RegionId).State == RegionState.Loaned, 1000));
        // The borrow exists before the async continuation enqueues its request.
        // Observe actual delivery, and preserve any non-empty-queue failure.
        KernelResult acceptance = default;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            acceptance = scenario.Host.AcceptNext();
            return acceptance.IsSuccess || acceptance.Error != KernelError.InvalidMessage ||
                acceptance.Message != "Channel queue is empty.";
        }, 1000), "The read-lease present request was not delivered within the test bound.");
        Assert.True(acceptance.IsSuccess, acceptance.Message);
        Assert.Throws<InvalidOperationException>(() => surface.WritablePixels[0] = 9);
        Assert.True(scenario.Host.CompleteAccepted().IsSuccess);
        var fence = await present;

        Assert.True(fence.Terminal);
        Assert.Equal(surface.Authority.Surface, fence.Surface);
        surface.WritablePixels[0] = 9;
        Assert.Equal(9, surface.WritablePixels[0]);
    }

    [Fact]
    public async Task MoveAdvancesRegionGenerationAndReturnsExclusiveAuthorityOnlyThroughResponse()
    {
        var scenario = CreateScenario();
        var compositor = new Compositor(ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session)), scenario.Capability);
        var register = compositor.RegisterAsync(scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!, Metadata()).AsTask();
        var registrationResult = scenario.Host.ProcessNext();
        Assert.True(registrationResult.IsSuccess, registrationResult.Message);
        var surface = await register;
        var before = surface.Pixels.Handle.Generation;

        var present = compositor.PresentMoveAsync(surface).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        Assert.True(SpinWait.SpinUntil(() => !surface.Pixels.IsValid, 1000));
        // MOVE invalidates the caller's buffer before the asynchronous continuation enqueues
        // the request. Wait for delivery, rather than treating invalidation as queue readiness.
        KernelResult acceptance = default;
        Assert.True(SpinWait.SpinUntil(() =>
        {
            acceptance = scenario.Host.AcceptNext();
            return acceptance.IsSuccess || acceptance.Error != KernelError.InvalidMessage ||
                acceptance.Message != "Channel queue is empty.";
        }, 1000), "The moved present request was not delivered within the test bound.");
        Assert.True(acceptance.IsSuccess, acceptance.Message);
        Assert.Throws<InvalidOperationException>(() => _ = surface.WritablePixels.Length);
        Assert.False(present.IsCompleted);
        Assert.True(scenario.Host.CompleteAccepted().IsSuccess);
        await present;

        Assert.Equal(before.Value + 2, surface.Pixels.Handle.Generation.Value);
        surface.WritablePixels.Fill(3);
    }

    [Fact]
    public async Task ForgedStaleWrongOwnerWrongSessionAndMissingCapabilityFailBeforeComposition()
    {
        var scenario = CreateScenario();
        var client = ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!;

        var forged = client.RegisterSurfaceAsync(new(scenario.Capability, new RegionHandle(new RegionId(99999), new RegionGeneration(1)), Metadata())).AsTask();
        Assert.Equal(KernelError.RegionNotFound, scenario.Host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forged);

        var stale = client.RegisterSurfaceAsync(new(scenario.Capability, new RegionHandle(buffer.Handle.RegionId, new RegionGeneration(99)), Metadata())).AsTask();
        Assert.Equal(KernelError.StaleGeneration, scenario.Host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);

        var other = TestFixtures.Create(scenario.Kernel, 3020, 30200).Handle;
        var otherBuffer = scenario.Kernel.AllocateBuffer<byte>(other, 16).Value!;
        var wrongOwner = client.RegisterSurfaceAsync(new(scenario.Capability, otherBuffer.Handle, Metadata())).AsTask();
        Assert.Equal(KernelError.WrongRegionOwner, scenario.Host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrongOwner);

        var missing = scenario.Kernel.OpenSession(scenario.Caller, scenario.Descriptor, []);
        Assert.Equal(KernelError.MissingCapability, missing.Error);

        var sibling = scenario.Kernel.OpenSession(scenario.Caller, scenario.Descriptor, [scenario.Capability]).Value;
        var siblingHost = RuntimeCompositorServiceHost.CreateForSession(scenario.Kernel, scenario.Service, sibling).Value!;
        var siblingClient = ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, sibling));
        var registered = client.RegisterSurfaceAsync(new(scenario.Capability, buffer.Handle, Metadata())).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        var exactSurface = (await registered).Authority.Surface;
        var staleSurface = exactSurface with { Generation = new SurfaceGeneration(exactSurface.Generation.Value + 1) };
        var stalePrepare = client.PreparePresentAsync(new(staleSurface, PresentTransferMode.ReadLease)).AsTask();
        Assert.Equal(KernelError.StaleHandle, scenario.Host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stalePrepare);
        var wrongSessionPrepare = siblingClient.PreparePresentAsync(new(new SurfaceHandle(sibling, new SurfaceIdentity(1), new SurfaceGeneration(1)), PresentTransferMode.ReadLease)).AsTask();
        Assert.Equal(KernelError.StaleHandle, siblingHost.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrongSessionPrepare);
    }

    [Fact]
    public async Task StaleDuplicateOrMismatchedReleaseCannotRestoreWriteAuthority()
    {
        var scenario = CreateScenario();
        var client = ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!;
        var registration = client.RegisterSurfaceAsync(new(scenario.Capability, buffer.Handle, Metadata())).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        var authority = (await registration).Authority;
        var prepare = client.PreparePresentAsync(new(authority.Surface, PresentTransferMode.ReadLease)).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        await prepare;
        var present = client.PresentReadLeaseAsync(buffer).AsTask();
        Assert.True(scenario.Host.AcceptNext().IsSuccess);
        var descriptor = scenario.Kernel.Regions.Snapshot().Single(x => x.Handle.RegionId == buffer.Handle.RegionId);
        var mismatched = new BorrowLeaseHandle(buffer.Handle, new BorrowLeaseGeneration(999));
        Assert.Equal(KernelError.StaleGeneration, scenario.Kernel.ReturnBorrow(scenario.Service, mismatched).Error);
        Assert.Throws<InvalidOperationException>(() => buffer.Span[0] = 1);
        Assert.True(scenario.Host.CompleteAccepted().IsSuccess);
        _ = await present;
        Assert.Equal(RegionState.Owned, scenario.Kernel.Regions.Snapshot().Single(x => x.Handle.RegionId == descriptor.Handle.RegionId).State);
        Assert.NotEqual(KernelError.None, scenario.Kernel.ReturnBorrow(scenario.Service, new BorrowLeaseHandle(buffer.Handle, new BorrowLeaseGeneration(1))).Error);
    }

    [Fact]
    public async Task AcceptedCrashPinsAmbiguousWorkWhileUnacceptedCrashReturnsByChannelContract()
    {
        var scenario = CreateScenario();
        var client = ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session));
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!;
        var registration = client.RegisterSurfaceAsync(new(scenario.Capability, buffer.Handle, Metadata())).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        var surface = (await registration).Authority.Surface;
        var prepare = client.PreparePresentAsync(new(surface, PresentTransferMode.ReadLease)).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        await prepare;
        var present = client.PresentReadLeaseAsync(buffer).AsTask();
        Assert.True(scenario.Host.AcceptNext().IsSuccess);
        Assert.Equal(KernelError.PlatformFaulted, scenario.Host.Drain().Error);
        Assert.Equal(SurfaceLifecycleState.Quarantined, scenario.Host.GetSurfaceState(surface));
        Assert.Throws<InvalidOperationException>(() => buffer.Span[0] = 4);
        Assert.False(present.IsCompleted);

        var clean = CreateScenario(3100);
        var queued = ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(clean.Kernel, clean.Caller, clean.Session))
            .RegisterSurfaceAsync(new(clean.Capability, clean.Kernel.AllocateBuffer<byte>(clean.Caller, 16).Value!.Handle, Metadata())).AsTask();
        Assert.True(clean.Kernel.FaultComponent(new ComponentIdentity("phase9-compositor-3100")).IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }

    [Fact]
    public void UiRolesInputAndWindowManagerRemainSeparateAuthorityBoundaries()
    {
        var contracts = new[]
        {
            typeof(IDisplayService), typeof(ICompositorService), typeof(IWindowManagerService), typeof(IInputService),
            typeof(IClipboardService), typeof(IFontTextService), typeof(IAccessibilityService), typeof(INotificationService), typeof(IShellService),
        };
        Assert.Equal(contracts.Length, contracts.Distinct().Count());
        Assert.DoesNotContain("Irq", string.Join(' ', typeof(IInputService).GetMethods().Select(static method => method.ToString())), StringComparison.OrdinalIgnoreCase);

        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 3200, 32000).Handle;
        var wm = Mint(kernel, caller, ResourceKind.WindowManager, CapabilityResourceIds.WindowManagement, CapabilityRights.Configure);
        var display = Admit(kernel, 3201, 32010, "display-needs-display", "display-boundary", IDisplayServiceProtocol.CreateDefinition(), IDisplayServiceResponseProtocol.Definition,
            new(ResourceKind.Display, CapabilityResourceIds.DisplayModel, CapabilityRights.Read));
        Assert.Equal(KernelError.MissingCapability, kernel.OpenSession(caller, display.Descriptor, [wm]).Error);
    }

    [Fact]
    public async Task CompatibilityProjectionCannotOutliveOrWidenNativeSessionAuthority()
    {
        var scenario = CreateScenario();
        var native = new Compositor(ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session)), scenario.Capability);
        var compatibility = new CompatibilityWindowFacade(CompatibilityPersonality.Win32, native);
        var registration = native.RegisterAsync(scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!, Metadata()).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        var surface = await registration;
        Assert.True(scenario.Kernel.RevokeCapability(scenario.Capability).IsSuccess);
        await Assert.ThrowsAsync<InvalidOperationException>(() => compatibility.PresentAsync(surface).AsTask());

        var hostFeatures = ((IPlatformFeatureProvider)new HostPlatformAuthorityProvider()).QueryFeatures();
        Assert.NotEqual(PlatformFeatureAvailability.Executable, hostFeatures.Resolve(PlatformFeatureFamily.SurfacePresentation).Availability);
        Assert.Equal(PlatformFeatureAvailability.ModelOnly, hostFeatures.Resolve(PlatformFeatureFamily.VirtualizationDomains).Availability);
    }

    [Fact]
    public async Task TripleBuffersRemainSeparateAndUnavailableScanoutFallsBackToCpuOwnershipProtocol()
    {
        var scenario = CreateScenario(3300);
        var features = ((IPlatformFeatureProvider)new HostPlatformAuthorityProvider()).QueryFeatures();
        Assert.Equal(PlatformFeatureAvailability.Unavailable, features.Resolve(PlatformFeatureFamily.SurfacePresentation).Availability);

        var compositor = new Compositor(ICompositorServiceRuntimeClient.Create(new RuntimeSipClientTransport(scenario.Kernel, scenario.Caller, scenario.Session)), scenario.Capability);
        var buffers = Enumerable.Range(0, 3).Select(_ => scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 16).Value!).ToArray();
        Assert.Equal(3, buffers.Select(static buffer => buffer.Handle.RegionId).Distinct().Count());

        var surfaces = new List<SurfaceBuffer>();
        foreach (var buffer in buffers)
        {
            var registration = compositor.RegisterAsync(buffer, Metadata()).AsTask();
            Assert.True(scenario.Host.ProcessNext().IsSuccess);
            surfaces.Add(await registration);
        }

        surfaces[0].WritablePixels.Fill(0x41);
        Assert.Equal(0, surfaces[1].WritablePixels[0]);
        Assert.Equal(0, surfaces[2].WritablePixels[0]);

        var present = compositor.PresentReadLeaseAsync(surfaces[0]).AsTask();
        Assert.True(scenario.Host.ProcessNext().IsSuccess);
        // PreparePresent completes before the async client continuation borrows pixels.
        // Yield the test worker while waiting for that ownership handoff.
        var waitStarted = global::System.Diagnostics.Stopwatch.GetTimestamp();
        while (scenario.Kernel.Regions.Snapshot().Single(x => x.Handle.RegionId == buffers[0].Handle.RegionId).State != RegionState.Loaned &&
               global::System.Diagnostics.Stopwatch.GetElapsedTime(waitStarted) < TimeSpan.FromSeconds(1) && !present.IsCompleted)
            await Task.Delay(1);
        var observed = scenario.Kernel.Regions.Snapshot().Single(x => x.Handle.RegionId == buffers[0].Handle.RegionId);
        Assert.True(observed.State == RegionState.Loaned,
            $"Read-lease handoff state={observed.State}; present={present.Status}; error={present.Exception}");
        Assert.True(scenario.Host.AcceptNext().IsSuccess);
        Assert.True(scenario.Host.CompleteAccepted().IsSuccess);
        var fence = await present;

        Assert.True(fence.Terminal);
        surfaces[0].WritablePixels[0] = 0x42;
        surfaces[1].WritablePixels[0] = 0x43;
        surfaces[2].WritablePixels[0] = 0x44;
    }

    [Fact]
    public void PublicSurfaceAndDependenciesContainNoHardwareProviderOrExternalRuntimeLeakage()
    {
        var forbidden = new[] { "PhysicalAddress", "GpuPointer", "DmaPointer", "Iommu", "Vmcs", "Vmx", "HybridCpu", "ProviderLease", "IrqVector" };
        var assemblies = new[] { typeof(SurfaceHandle).Assembly, typeof(ICompositorService).Assembly, typeof(Compositor).Assembly };
        foreach (var member in assemblies.SelectMany(static assembly => assembly.GetExportedTypes()).SelectMany(static type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)))
        foreach (var word in forbidden) Assert.DoesNotContain(word, member.ToString(), StringComparison.OrdinalIgnoreCase);

        var root = FindRoot();
        var sdk = XDocument.Load(Path.Combine(root, "sdk", "SingPlus.System", "SingPlus.System.csproj"));
        Assert.DoesNotContain(sdk.Descendants("ProjectReference"), item => ((string?)item.Attribute("Include") ?? "").Contains("Platform", StringComparison.OrdinalIgnoreCase));
        var directExternal = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.Combine("tools", "HybridCpu_ExecutableAdapter"), StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith(Path.Combine("src", "Runtime", "SingPlus.Runtime", "SingPlus.Runtime.csproj"), StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.Combine("tests", "SingPlus.Tests"), StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("HybridCPU.ExternalRuntime", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(directExternal);
    }

    private static SurfaceMetadata Metadata() => new(SurfacePixelFormat.Bgra8888, 2, 2, 8, SurfaceLayout.Linear, [new(0, 16, 8)], 1);

    private static Scenario CreateScenario(ulong seed = 3000)
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, seed, seed * 10).Handle;
        var configure = Mint(kernel, caller, ResourceKind.KernelService, CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure);
        var budget = kernel.AdmitProcessBudget(caller, configure, caller, "surface-caller",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 256), new(ServiceBudgetDimension.IpcMessages, 32), new(ServiceBudgetDimension.IpcBytes, 16384)]);
        Assert.True(budget.IsSuccess, budget.Message);
        var admitted = Admit(kernel, seed + 1, seed * 10 + 1, $"compositor-{seed}", $"phase9-compositor-{seed}", ICompositorServiceProtocol.CreateDefinition(), ICompositorServiceResponseProtocol.Definition,
            new(ResourceKind.Compositor, CapabilityResourceIds.CompositorPresent, CapabilityRights.Read | CapabilityRights.Write));
        var capability = Mint(kernel, caller, ResourceKind.Compositor, CapabilityResourceIds.CompositorPresent, CapabilityRights.Read | CapabilityRights.Write);
        var session = kernel.OpenSession(caller, admitted.Descriptor, [capability]).Value;
        var host = RuntimeCompositorServiceHost.CreateForSession(kernel, admitted.Process, session).Value!;
        return new(kernel, admitted.Process, caller, admitted.Descriptor, capability, session, host);
    }

    private static CapabilityId Mint(RuntimeKernel kernel, ProcessHandle owner, ResourceKind kind, string resource, CapabilityRights rights) =>
        kernel.MintCapability(new DomainId(owner.ProcessId.Value * 10), owner, kind, resource, rights).Value!.CapabilityId;

    private static (ProcessHandle Process, ServiceEndpointDescriptor Descriptor) Admit(RuntimeKernel kernel, ulong processId, ulong domainId, string serviceName, string componentName, ProtocolDefinitionV1 protocol, ResponseProtocolDefinitionV1 response, CapabilityRequirementV1 requirement)
    {
        byte[] image = [(byte)(processId & 0xff), 0x50, 0x39];
        var contract = new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest);
        var provided = new ProvidedServiceManifestV1(serviceName, contract);
        var manifest = new ServiceManifestV1(new(componentName), new("1"), Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(), TestFixtures.Manifest(processId, domainId, 1, componentName), [provided],
            budgetRequests: [new(ServiceBudgetDimension.OwnedMemoryBytes, 256), new(ServiceBudgetDimension.IpcMessages, 32), new(ServiceBudgetDimension.IpcBytes, 16384)]);
        var result = kernel.AdmitComponent(new(manifest, image, providedServices: [new(provided, protocol, response, [requirement])]));
        Assert.True(result.IsSuccess, result.Message);
        return (result.Value!.Process, kernel.ResolveByServiceName(serviceName).Value);
    }

    private static string FindRoot() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx"))) directory = directory.Parent; return directory!.FullName; }
    private sealed record Scenario(RuntimeKernel Kernel, ProcessHandle Service, ProcessHandle Caller, ServiceEndpointDescriptor Descriptor, CapabilityId Capability, EndpointSessionHandle Session, RuntimeCompositorServiceHost Host);
}
