using System.Xml.Linq;
using System.Security.Cryptography;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.FileSystem;
using SingPlus.Sip.Networking;
using SingPlus.Sip.Native;
using SingPlus.Sip.Process;
using SingPlus.Sip.Sdk;
using SingPlus.System;

namespace SingPlus.Tests.NativeServices;

public sealed class NativeSystemServiceVerticalSliceTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)] [InlineData(false, 1)]
    [InlineData(false, 2)] [InlineData(false, 3)]
    public async Task CopiedSealRouteAfterAdmissionCannotAuthorizeActualNativeWrite(bool network, int fault)
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var routes = (global::System.Collections.IDictionary)typeof(SealedObjectAuthority)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.SealedObjects)!;
        Guid token = default;
        object? copy = null;
        var armed = false;
        Func<KernelResult>? drain = null;
        Func<KernelResult>? revoke = null;
        var trace = new CallbackEventSink(point =>
        {
            if (!armed || point != (fault != 0 ? NativeSipCompletedEvent.ImplementationEntered : NativeSipCompletedEvent.CapabilityAdmitted)) return;
            armed = false;
            if (fault == 2) { Assert.Equal(KernelError.SessionDraining, drain!().Error); return; }
            if (fault == 3) { Assert.True(revoke!().IsSuccess); return; }
            var original = routes[token]!;
            copy = original.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
            routes[token] = copy;
        });
        Func<KernelResult> dispatch;
        Func<Task> write;
        Func<object> getContents;
        if (network)
        {
            var s = NetworkDrainScenario();
            kernel = s.Kernel; service = s.Service; caller = s.Caller; session = s.Session;
            routes = (global::System.Collections.IDictionary)typeof(SealedObjectAuthority)
                .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.SealedObjects)!;
            var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session, trace, hook: null).Value!;
            var client = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
            var opening = client.OpenAsync(new(s.Capability, "original-pin")).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);
            var control = (await opening).Authority;
            token = control.Socket.Seal.OpaqueToken;
            dispatch = host.ProcessNext;
            write = () => client.SendAsync(new(control.Socket, control.Capability, new([7, 8]))).AsTask();
            getContents = () => Assert.Single(NetworkEntries(host).Values.Cast<object>()).GetType()
                .GetProperty("Packets")!.GetValue(Assert.Single(NetworkEntries(host).Values.Cast<object>()))!;
        }
        else
        {
            var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace).Value!;
            var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
            var opening = client.OpenAsync(new(capability, "/original-pin", true)).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);
            var control = (await opening).Authority;
            token = control.File.Seal.OpaqueToken;
            drain = host.Drain;
            revoke = () => kernel.SealedObjects.Revoke(control.File.Seal);
            dispatch = host.ProcessNext;
            write = () => client.WriteAsync(new(control.File, control.Capability, new([7, 8]))).AsTask();
            getContents = () =>
            {
                var files = (global::System.Collections.IDictionary)typeof(RuntimeFileServiceHost)
                    .GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                var entry = Assert.Single(files.Values.Cast<object>());
                return entry.GetType().GetProperty("Data")!.GetValue(entry)!;
            };
        }
        var originalRecord = routes[token]!;
        armed = true;
        var writing = write();
        Assert.Equal(fault == 2 ? KernelError.SessionDraining : KernelError.StaleHandle, dispatch().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty((global::System.Collections.IEnumerable)getContents());
        var pins = originalRecord.GetType().GetProperty("ActivePins")!;
        Assert.Equal(0, (int)pins.GetValue(originalRecord)!);
        if (copy is not null) Assert.Equal(1, (int)pins.GetValue(copy)!);
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task NativeObjectCapacityRefusalBeforeAcceptanceDoesNotPinCallerBacking(int route)
    {
        RuntimeKernel kernel;
        ProcessHandle caller;
        EndpointSessionHandle session;
        object host;
        Func<KernelResult> dispatch;
        Task opening;
        if (route == 2)
        {
            var s = NetworkDrainScenario();
            kernel = s.Kernel; caller = s.Caller; session = s.Session;
            var networkHost = RuntimeNetworkServiceHost.CreateForSession(kernel, s.Service, session).Value!;
            host = networkHost; dispatch = networkHost.ProcessNext;
            host.GetType().GetField("_nextId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, ulong.MaxValue);
            var client = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
            opening = client.OpenAsync(new(s.Capability, "capacity")).AsTask();
        }
        else
        {
            var s = FileScenario();
            kernel = s.Kernel; caller = s.Caller; session = s.Session;
            var fileHost = RuntimeFileServiceHost.CreateForSession(kernel, s.Service, session).Value!;
            host = fileHost; dispatch = fileHost.ProcessNext;
            host.GetType().GetField("_nextId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, ulong.MaxValue);
            var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
            opening = route == 0 ? client.OpenAsync(new(s.Capability, "/capacity", true)).AsTask()
                : client.OpenV2Async(new(kernel.UpgradeCapabilityV1(caller, s.Capability).Value, "/capacity", true)).AsTask();
        }
        _ = kernel.AllocateBuffer<byte>(caller, 4).Value!;
        Assert.Equal(KernelError.CapacityExhausted, dispatch().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);
        Assert.True(kernel.Processes.ResolveTerminalState(caller).IsSuccess);
        Assert.All(kernel.Regions.Snapshot(), region => Assert.Equal(RegionState.Released, region.State));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task InvalidSessionCloseCannotDrainOriginalAttachedChild(int fault)
    {
        var (kernel, host, caller, session, manager) = AttachedChildDrainScenario();
        var creating = manager.CreateAsync(TestFixtures.Manifest(2092, 20920)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await creating.WaitAsync(TimeSpan.FromSeconds(10));
        _ = kernel.AllocateBuffer<byte>(child.Authority.Process, 4).Value!;
        var calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ChildDrainParticipant(process =>
        {
            if (process == child.Authority.Process) calls++;
            return KernelResult.Ok();
        }));
        var records = (global::System.Collections.IDictionary)kernel.EndpointSessions.GetType()
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.EndpointSessions)!;
        var original = records[session.SessionId]!;
        var requestedCaller = caller;
        var requestedSession = session;
        if (fault == 0) requestedCaller = TestFixtures.Create(kernel, 2094, 20940).Handle;
        if (fault == 1) requestedSession = session with { Generation = new EndpointSessionGeneration(2) };
        if (fault == 2) records.Remove(session.SessionId);
        if (fault == 3) records[session.SessionId] = original.GetType()
            .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null);
        if (fault == 4) requestedCaller = new ProcessHandle(caller.ProcessId, caller.Generation + 1);
        Assert.False(kernel.CloseSession(requestedCaller, requestedSession).IsSuccess);
        Assert.Equal(0, calls);
        Assert.False(kernel.Processes.ResolveTerminalState(child.Authority.Process).IsSuccess);
        Assert.NotEqual(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        records[session.SessionId] = original;
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        Assert.Equal(1, calls);
        Assert.True(kernel.Processes.ResolveTerminalState(child.Authority.Process).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
    }

    [Fact]
    public async Task ExactNativeHostDrainConsumesOriginalRegistrationAfterLateSessionRouteLoss()
    {
        var (kernel, host, caller, session, manager) = AttachedChildDrainScenario();
        var creating = manager.CreateAsync(TestFixtures.Manifest(2092, 20920)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await creating.WaitAsync(TimeSpan.FromSeconds(10));
        var backing = kernel.AllocateBuffer<byte>(child.Authority.Process, 4).Value!;
        var index = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_nativeSessionDrains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        var calls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ChildDrainParticipant(process =>
        {
            if (process == child.Authority.Process) { calls++; index.Remove(session); }
            return KernelResult.Ok();
        }));
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        Assert.True(kernel.Processes.ResolveTerminalState(child.Authority.Process).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        var originals = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_nativeServiceDrains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        Assert.Empty(originals.Keys.Cast<object>());
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task OriginalNativeHostDrainCannotDisappearBeforeChildAndBackingCleanup(int fault)
    {
        var (kernel, host, caller, session, manager) = AttachedChildDrainScenario();
        var creating = manager.CreateAsync(TestFixtures.Manifest(2092, 20920)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await creating.WaitAsync(TimeSpan.FromSeconds(10));
        var backing = kernel.AllocateBuffer<byte>(child.Authority.Process, 4).Value!;
        var refuse = true;
        kernel.RegisterComposedWorkTeardownParticipant(new ChildDrainParticipant(process =>
            process == child.Authority.Process && refuse
                ? KernelResult.Fail(KernelError.ExternalEffectUncontained, "Original child closure refused.")
                : KernelResult.Ok()));
        var foreign = AttachedChildDrainScenario();
        var index = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_nativeSessionDrains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        var foreignIndex = (global::System.Collections.IDictionary)typeof(RuntimeKernel)
            .GetField("_nativeSessionDrains", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(foreign.Kernel)!;
        var registration = index[session]!;
        if (fault == 0) index.Remove(session);
        else index[session] = fault == 1 ? foreignIndex[foreign.Session]
            : registration.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(registration, null);
        Assert.False(kernel.CloseSession(caller, session).IsSuccess);
        Assert.False(kernel.TerminateProcess(caller).IsSuccess);
        Assert.False(kernel.QueryProcessTeardown(caller).Value.LocalReclaimCompleted);
        Assert.NotEqual(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.False(kernel.Processes.ResolveTerminalState(child.Authority.Process).IsSuccess);
        Assert.True(foreign.Kernel.EndpointSessions.Resolve(foreign.Session, foreign.Caller).IsSuccess);
        index[session] = registration;
        refuse = false;
        Assert.True(kernel.TerminateProcess(caller).IsSuccess);
        Assert.True(kernel.Processes.ResolveTerminalState(child.Authority.Process).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.False(index.Contains(session));
        Assert.True(foreign.Kernel.CloseSession(foreign.Caller, foreign.Session).IsSuccess);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 0)]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 1)]
    [InlineData(0, 2)] [InlineData(1, 2)] [InlineData(2, 2)]
    [InlineData(0, 3)] [InlineData(1, 3)] [InlineData(2, 3)]
    [InlineData(0, 4)] [InlineData(1, 4)] [InlineData(2, 4)]
    public async Task ObjectCandidatePrecedesActualMintAndCannotGrantClosure(int route, int fault)
    {
        RuntimeKernel kernel;
        ProcessHandle service, caller;
        EndpointSessionHandle session;
        Func<KernelResult> processNext;
        Func<Task> open;
        global::System.Collections.IDictionary entries;
        var prefix = route == 2 ? "socket-object:" : "file-object:";
        if (route == 2)
        {
            var s = NetworkDrainScenario();
            kernel = s.Kernel; service = s.Service; caller = s.Caller; session = s.Session;
            var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session).Value!;
            var client = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
            entries = NetworkEntries(host); processNext = host.ProcessNext;
            open = () => client.OpenAsync(new(s.Capability, "loopback:candidate")).AsTask();
        }
        else
        {
            var s = FileScenario();
            kernel = s.Kernel; service = s.Service; caller = s.Caller; session = s.Session;
            var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session).Value!;
            var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
            var opaque = kernel.UpgradeCapabilityV1(caller, s.Capability).Value;
            entries = (global::System.Collections.IDictionary)typeof(RuntimeFileServiceHost)
                .GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            processNext = host.ProcessNext;
            open = () => route == 1 ? client.OpenV2Async(new(opaque, "/candidate", true)).AsTask()
                : client.OpenAsync(new(s.Capability, "/candidate", true)).AsTask();
        }
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        typeof(CapabilityAuthority).GetField("_nonceFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(kernel.CapabilityAuthority, (Func<Guid>)(() =>
            {
                if (Interlocked.Increment(ref calls) != 1) return Guid.Empty;
                var candidate = entries.Values.Cast<object>().Single();
                Assert.Null(candidate.GetType().GetProperty("Capability")!.GetValue(candidate));
                Assert.Null(candidate.GetType().GetProperty("Handle")!.GetValue(candidate));
                Assert.False((bool)candidate.GetType().GetProperty("SealOutcomeKnown")!.GetValue(candidate)!);
                Assert.DoesNotContain(kernel.CapabilityAuthority.InspectionSnapshot(), record =>
                    record.Descriptor.ResourceId.StartsWith(prefix, StringComparison.Ordinal));
                if (fault == 1) throw new InvalidOperationException("Actual issuance outcome unknown.");
                if (fault is 2 or 3)
                {
                    entered.SetResult();
                    Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                }
                if (fault == 4)
                    kernel.CapabilityAuthority.RevokeAllForDomain(kernel.Processes.Resolve(caller).Value!.DomainId);
                return fault is 2 or 4 ? Guid.NewGuid() : Guid.Empty;
            }));
        var opening = open();
        if (fault == 1)
        {
            Assert.Throws<InvalidOperationException>(() => processNext());
            Assert.Single(entries.Keys.Cast<object>());
            Assert.Equal(KernelError.ExternalEffectUncontained, kernel.CloseSession(caller, session).Error);
            Assert.Equal(KernelError.ExternalEffectUncontained, kernel.FaultProcess(service).Error);
            Assert.False(kernel.QueryProcessTeardown(service).Value.LocalReclaimCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else
        {
            var dispatch = Task.Run(processNext);
            if (fault is 2 or 3)
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Single(entries.Keys.Cast<object>());
                    Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error);
                    Assert.Equal(KernelError.SessionDraining, processNext().Error);
                    Assert.False(dispatch.IsCompleted);
                }
                finally { release.Set(); }
            }
            var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(fault == 2 ? KernelError.SessionDraining : fault == 4 ? KernelError.CapabilityRevoked
                : KernelError.CapacityExhausted, result.Error);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
            var issued = kernel.CapabilityAuthority.InspectionSnapshot().Where(record =>
                record.Descriptor.ResourceId.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (fault == 2)
            {
                Assert.Single(issued);
                Assert.Single(entries.Keys.Cast<object>());
                Assert.Equal(CapabilityRights.Read | CapabilityRights.Write | CapabilityRights.Configure, issued[0].Descriptor.Rights);
            }
            else
            {
                Assert.Empty(issued);
                Assert.Empty(entries.Keys.Cast<object>());
            }
            Assert.True(kernel.CloseSession(caller, session).IsSuccess);
            Assert.Empty(entries.Keys.Cast<object>());
            Assert.All(issued, record => Assert.Equal(KernelError.CapabilityRevoked,
                kernel.ValidateCapability(caller, record.Descriptor.CapabilityId, CapabilityRights.Read).Error));
        }
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    public async Task ObjectCloseRetainsAccountingUntilActualSourceAndSealCleanup(bool network, int fault)
    {
        RuntimeKernel kernel;
        ProcessHandle service, caller;
        EndpointSessionHandle session;
        CapabilityId issued = default;
        Guid sealToken = default;
        Func<KernelResult> processNext, revokeSeal;
        global::System.Collections.IDictionary entries;
        Func<Task> close;
        var armed = false;
        RuntimeKernel? callbackKernel = null;
        Func<KernelResult>? callbackRevoke = null;
        var trace = new CallbackEventSink(point =>
        {
            if (!armed || point != NativeSipCompletedEvent.ImplementationEntered) return;
            armed = false;
            if (fault == 0)
            {
                var records = (global::System.Collections.IDictionary)typeof(CapabilityAuthority)
                    .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(callbackKernel!.CapabilityAuthority)!;
                records.Remove(issued);
            }
            else if (fault == 1) Assert.True(callbackRevoke!().IsSuccess);
            else if (fault == 2)
            {
                var records = (global::System.Collections.IDictionary)typeof(SealedObjectAuthority)
                    .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(callbackKernel!.SealedObjects)!;
                records.Remove(sealToken);
            }
            else Assert.True(callbackKernel!.RevokeCapability(issued).IsSuccess);
        });
        if (network)
        {
            var s = NetworkDrainScenario();
            kernel = s.Kernel; service = s.Service; caller = s.Caller; session = s.Session;
            var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session, trace, hook: null).Value!;
            var client = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
            var opening = client.OpenAsync(new(s.Capability, "loopback:close")).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);
            var control = (await opening).Authority;
            issued = control.Capability; sealToken = control.Socket.Seal.OpaqueToken;
            processNext = host.ProcessNext; entries = NetworkEntries(host);
            revokeSeal = () => kernel.SealedObjects.Revoke(control.Socket.Seal);
            close = () => client.CloseAsync(new(control.Socket, control.Capability)).AsTask();
        }
        else
        {
            var s = FileScenario();
            kernel = s.Kernel; service = s.Service; caller = s.Caller; session = s.Session;
            var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace).Value!;
            var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
            var opening = client.OpenAsync(new(s.Capability, "/close", true)).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);
            var control = (await opening).Authority;
            issued = control.Capability; sealToken = control.File.Seal.OpaqueToken;
            processNext = host.ProcessNext;
            entries = (global::System.Collections.IDictionary)typeof(RuntimeFileServiceHost)
                .GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            revokeSeal = () => kernel.SealedObjects.Revoke(control.File.Seal);
            close = () => client.CloseAsync(new(control.File, control.Capability)).AsTask();
        }
        callbackKernel = kernel; callbackRevoke = revokeSeal; armed = true;
        var closing = close();
        var result = processNext();
        Assert.False(armed);
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
        if (fault == 3)
        {
            Assert.True(result.IsSuccess, result.Message);
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(entries.Keys.Cast<object>());
            Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        }
        else
        {
            var expected = fault == 0 ? KernelError.CapabilityNotFound : KernelError.StaleHandle;
            Assert.Equal(expected, result.Error);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => closing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Single(entries.Keys.Cast<object>());
            Assert.Equal(KernelError.SessionDraining, processNext().Error);
            if (fault == 1)
            {
                // Existing real seal owner closes after the actual pin releases;
                // existing native retry consumes the retained entry.
                Assert.True(kernel.CloseSession(caller, session).IsSuccess);
                Assert.Empty(entries.Keys.Cast<object>());
            }
            else
            {
                Assert.Equal(expected, kernel.CloseSession(caller, session).Error);
                Assert.Equal(expected, kernel.CloseSession(caller, session).Error);
                Assert.Equal(expected, kernel.FaultProcess(service).Error);
                Assert.False(kernel.QueryProcessTeardown(service).Value.LocalReclaimCompleted);
                Assert.Single(entries.Keys.Cast<object>());
            }
        }
        if (fault != 0)
            Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, issued, CapabilityRights.Read).Error);
    }

    private sealed class CallbackEventSink(Action<NativeSipCompletedEvent> action) : INativeSipCompletedEventSink
    {
        public void Record(NativeSipCompletedEvent point) => action(point);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task SocketCreationTracksIssuedControlAcrossSealRefusalExceptionAndOverlap(int fault)
    {
        var (kernel, service, caller, capability, session) = NetworkDrainScenario();
        var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session).Value!;
        var client = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var sockets = (global::System.Collections.IDictionary)typeof(RuntimeNetworkServiceHost)
            .GetField("_sockets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        if (fault == 0)
        {
            var seed = client.OpenAsync(new(capability, "loopback:seed")).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);
            await seed;
            typeof(SealedObjectAuthority).GetField("_capacity", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(kernel.SealedObjects, 1);
        }
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CapabilityId issued = default;
        if (fault != 0)
            typeof(SealedObjectAuthority).GetField("_tokenFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(kernel.SealedObjects, (Func<Guid>)(() =>
                {
                    if (Interlocked.Increment(ref calls) != 1) return Guid.Empty;
                    var entry = sockets.Values.Cast<object>().Single();
                    Assert.Null(entry.GetType().GetProperty("Handle")!.GetValue(entry));
                    Assert.False((bool)entry.GetType().GetProperty("SealOutcomeKnown")!.GetValue(entry)!);
                    issued = kernel.CapabilityAuthority.InspectionSnapshot().Single(record =>
                        record.Descriptor.ResourceId.StartsWith("socket-object:", StringComparison.Ordinal)).Descriptor.CapabilityId;
                    Assert.Equal(issued, (CapabilityId)entry.GetType().GetProperty("Capability")!.GetValue(entry)!);
                    if (fault == 2)
                    {
                        var records = (global::System.Collections.IDictionary)typeof(CapabilityAuthority)
                            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.CapabilityAuthority)!;
                        records.Remove(issued);
                    }
                    if (fault == 3) throw new InvalidOperationException("Seal owner outcome unknown.");
                    if (fault == 4)
                    {
                        entered.SetResult();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    }
                    return Guid.Empty;
                }));
        var opening = client.OpenAsync(new(capability, "loopback:refused")).AsTask();
        if (fault == 3)
        {
            Assert.Throws<InvalidOperationException>(() => host.ProcessNext());
            Assert.Single(sockets.Keys.Cast<object>());
            Assert.Equal(KernelError.ExternalEffectUncontained, kernel.CloseSession(caller, session).Error);
            Assert.Equal(KernelError.ExternalEffectUncontained, kernel.FaultProcess(service).Error);
            Assert.False(kernel.QueryProcessTeardown(service).Value.LocalReclaimCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else
        {
            var dispatch = Task.Run(host.ProcessNext);
            if (fault == 4)
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error);
                    Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
                    Assert.Single(sockets.Keys.Cast<object>());
                    Assert.False(dispatch.IsCompleted);
                }
                finally { release.Set(); }
            }
            var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(fault == 2 ? KernelError.CapabilityNotFound : KernelError.CapacityExhausted, result.Error);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
            if (fault == 2)
            {
                Assert.Single(sockets.Keys.Cast<object>());
                var entry = sockets.Values.Cast<object>().Single();
                Assert.True((bool)entry.GetType().GetProperty("SealOutcomeKnown")!.GetValue(entry)!);
                Assert.Null(entry.GetType().GetProperty("Handle")!.GetValue(entry));
                Assert.Equal(KernelError.CapabilityNotFound, kernel.CloseSession(caller, session).Error);
                Assert.Equal(KernelError.CapabilityNotFound, kernel.CloseSession(caller, session).Error);
                Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
                Assert.Equal(KernelError.CapabilityNotFound, kernel.FaultProcess(service).Error);
                Assert.False(kernel.QueryProcessTeardown(service).Value.LocalReclaimCompleted);
            }
            else
            {
                Assert.True(kernel.CloseSession(caller, session).IsSuccess);
                Assert.Empty(sockets.Keys.Cast<object>());
                if (fault != 0)
                    Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, issued, CapabilityRights.Read).Error);
            }
        }
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    public async Task FileCreationTracksIssuedControlAcrossSealRefusalExceptionAndOverlap(bool v2, int fault)
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session).Value!;
        var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var opaque = kernel.UpgradeCapabilityV1(caller, capability).Value;
        var files = (global::System.Collections.IDictionary)typeof(RuntimeFileServiceHost)
            .GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        if (fault == 0)
        {
            var seed = client.OpenAsync(new(capability, "/seed", true)).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);
            await seed;
            typeof(SealedObjectAuthority).GetField("_capacity", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(kernel.SealedObjects, 1);
        }
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CapabilityId issued = default;
        if (fault != 0)
            typeof(SealedObjectAuthority).GetField("_tokenFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(kernel.SealedObjects, (Func<Guid>)(() =>
                {
                    if (Interlocked.Increment(ref calls) != 1) return Guid.Empty;
                    var entry = files.Values.Cast<object>().Single();
                    Assert.Null(entry.GetType().GetProperty("Handle")!.GetValue(entry));
                    Assert.False((bool)entry.GetType().GetProperty("SealOutcomeKnown")!.GetValue(entry)!);
                    issued = kernel.CapabilityAuthority.InspectionSnapshot().Single(record =>
                        record.Descriptor.ResourceId.StartsWith("file-object:", StringComparison.Ordinal)).Descriptor.CapabilityId;
                    Assert.Equal(issued, (CapabilityId)entry.GetType().GetProperty("Capability")!.GetValue(entry)!);
                    if (fault == 2)
                    {
                        var records = (global::System.Collections.IDictionary)typeof(CapabilityAuthority)
                            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.CapabilityAuthority)!;
                        records.Remove(issued);
                    }
                    if (fault == 3) throw new InvalidOperationException("Seal owner outcome unknown.");
                    if (fault == 4)
                    {
                        entered.SetResult();
                        Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                    }
                    return Guid.Empty;
                }));
        var opening = v2 ? client.OpenV2Async(new(opaque, "/refused", true)).AsTask()
            : client.OpenAsync(new(capability, "/refused", true)).AsTask();
        if (fault == 3)
        {
            Assert.Throws<InvalidOperationException>(() => host.ProcessNext());
            Assert.Single(files.Keys.Cast<object>());
            Assert.Equal(KernelError.ExternalEffectUncontained, kernel.CloseSession(caller, session).Error);
            Assert.Equal(KernelError.ExternalEffectUncontained, kernel.FaultProcess(service).Error);
            Assert.False(kernel.QueryProcessTeardown(service).Value.LocalReclaimCompleted);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        else
        {
            var dispatch = Task.Run(host.ProcessNext);
            if (fault == 4)
            {
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error);
                    Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
                    Assert.Single(files.Keys.Cast<object>());
                    Assert.False(dispatch.IsCompleted);
                }
                finally { release.Set(); }
            }
            var result = await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(fault == 2 ? KernelError.CapabilityNotFound : KernelError.CapacityExhausted, result.Error);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
            if (fault == 2)
            {
                Assert.Single(files.Keys.Cast<object>());
                var entry = files.Values.Cast<object>().Single();
                Assert.True((bool)entry.GetType().GetProperty("SealOutcomeKnown")!.GetValue(entry)!);
                Assert.Null(entry.GetType().GetProperty("Handle")!.GetValue(entry));
                Assert.Equal(KernelError.CapabilityNotFound, kernel.CloseSession(caller, session).Error);
                Assert.Equal(KernelError.CapabilityNotFound, kernel.CloseSession(caller, session).Error);
                Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
                Assert.Equal(KernelError.CapabilityNotFound, kernel.FaultProcess(service).Error);
                Assert.False(kernel.QueryProcessTeardown(service).Value.LocalReclaimCompleted);
            }
            else
            {
                Assert.True(kernel.CloseSession(caller, session).IsSuccess);
                Assert.Empty(files.Keys.Cast<object>());
                if (fault != 0)
                    Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, issued, CapabilityRights.Read).Error);
            }
        }
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    public async Task FileDrainRevokesEveryKnownSourceBeforeUncertainSealCleanup(bool v2, int fault)
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session).Value!;
        var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var opaque = kernel.UpgradeCapabilityV1(caller, capability).Value;
        var controls = new List<FileObjectAuthority>();
        for (var index = 0; index < 3; index++)
        {
            var pending = v2 ? client.OpenV2Async(new(opaque, $"/cleanup-{index}", true)).AsTask()
                : client.OpenAsync(new(capability, $"/cleanup-{index}", true)).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);
            controls.Add((await pending).Authority);
        }
        var files = (global::System.Collections.IDictionary)typeof(RuntimeFileServiceHost)
            .GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        if (fault == 0)
        {
            // Lose a real seal owner record; absence never becomes closure evidence.
            var records = (global::System.Collections.IDictionary)typeof(SealedObjectAuthority)
                .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.SealedObjects)!;
            records.Remove(controls[1].File.Seal.OpaqueToken);
        }
        else if (fault == 1)
        {
            // Lose a real capability record. Remaining sources still must be revoked.
            var records = (global::System.Collections.IDictionary)typeof(CapabilityAuthority)
                .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.CapabilityAuthority)!;
            records.Remove(controls[1].Capability);
        }
        else Assert.True(kernel.RevokeCapability(controls[1].Capability).IsSuccess);
        var result = kernel.CloseSession(caller, session);
        Assert.All(controls.Where((_, index) => fault != 1 || index != 1), control =>
            Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, control.Capability, CapabilityRights.Read).Error));
        if (fault == 2)
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Empty(files.Keys.Cast<object>());
        }
        else
        {
            var expected = fault == 0 ? KernelError.StaleHandle : KernelError.CapabilityNotFound;
            Assert.Equal(expected, result.Error);
            Assert.Equal(expected, kernel.CloseSession(caller, session).Error);
            var expectedIds = (fault == 0 ? controls.Skip(1) : controls).Select(control => control.File.ObjectId).ToArray();
            Assert.Equal(expectedIds, files.Keys.Cast<FileObjectId>().OrderBy(id => id.Value));
            Assert.True(kernel.EndpointSessions.Resolve(session, caller).IsSuccess);
            Assert.Equal(expected, kernel.FaultProcess(service).Error);
            Assert.False(kernel.QueryProcessTeardown(service).Value.LocalReclaimCompleted);
            Assert.True(kernel.Processes.Resolve(service).IsSuccess);
        }
        Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task NetworkHostPendingDispatchRetainsLateSocketForRealClosureRetry(int boundary)
    {
        var (kernel, service, caller, capability, session) = NetworkDrainScenario();
        var trace = new CompletedEventSink();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var point = boundary switch { 0 => NativeSipDeterministicPoint.BeforeCapabilityCommit,
            1 => NativeSipDeterministicPoint.BeforeImplementationEntry,
            2 => NativeSipDeterministicPoint.AfterImplementationExit,
            _ => NativeSipDeterministicPoint.BeforePublication };
        var hook = new PointHook(point, () => { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); });
        var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session, trace, hook).Value!;
        var client = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var opening = client.OpenAsync(new(capability, "loopback:pending")).AsTask();
        var queued = client.OpenAsync(new(capability, "loopback:queued")).AsTask();
        var dispatch = Task.Run(host.ProcessNext);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
            Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error);
            Assert.Equal(KernelError.SessionDraining, host.Drain().Error);
            Assert.True(kernel.EndpointSessions.Resolve(session, caller).IsSuccess);
            Assert.False(dispatch.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.SessionDraining, (await dispatch.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(NativeSipCompletedEvent.ResponsePublished, trace.Events);
        if (boundary == 0)
        {
            Assert.DoesNotContain(NativeSipCompletedEvent.CapabilityAdmitted, trace.Events);
            Assert.DoesNotContain(NativeSipCompletedEvent.ImplementationEntered, trace.Events);
        }
        var sockets = NetworkEntries(host);
        Assert.Equal(boundary == 0 ? 0 : 1, sockets.Count);
        var issued = kernel.CapabilityAuthority.InspectionSnapshot().Where(record =>
            record.Descriptor.ResourceKind == ResourceKind.Network &&
            record.Descriptor.ResourceId.StartsWith("socket-object:", StringComparison.Ordinal)).Select(record => record.Descriptor.CapabilityId).ToArray();
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        Assert.Empty(sockets.Keys.Cast<object>());
        Assert.All(issued, id => Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, id, CapabilityRights.Read).Error));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NetworkHostDispatchExceptionRetainsUnknownClosurePin(bool afterEffect)
    {
        var (kernel, service, caller, capability, session) = NetworkDrainScenario();
        var hook = new PointHook(afterEffect ? NativeSipDeterministicPoint.BeforePublication
            : NativeSipDeterministicPoint.AfterInvocationAccepted,
            () => throw new InvalidOperationException("Network dispatch outcome unknown."));
        var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session, trace: null, hook).Value!;
        var opening = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session))
            .OpenAsync(new(capability, "loopback:unknown")).AsTask();
        Assert.Throws<InvalidOperationException>(() => host.ProcessNext());
        Assert.Equal(KernelError.ExternalEffectUncontained, kernel.CloseSession(caller, session).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, kernel.CloseSession(caller, session).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, kernel.FaultProcess(service).Error);
        Assert.False(kernel.QueryProcessTeardown(service).Value.LocalReclaimCompleted);
        Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
        Assert.Equal(afterEffect ? 1 : 0, NetworkEntries(host).Count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static global::System.Collections.IDictionary NetworkEntries(RuntimeNetworkServiceHost host) =>
        (global::System.Collections.IDictionary)typeof(RuntimeNetworkServiceHost)
            .GetField("_sockets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NetworkPacketCommandKeepsDispatchPendingThroughLateEffect(bool receive)
    {
        var (kernel, service, caller, capability, session) = NetworkDrainScenario();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        var hook = new PacketAdmissionHook(() =>
        {
            if (!armed) return;
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session, trace: null, hook).Value!;
        var client = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var opening = client.OpenAsync(new(capability, "loopback:packet")).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var authority = (await opening).Authority;
        if (receive)
        {
            var seed = client.SendAsync(new(authority.Socket, authority.Capability, new BoundedBytes([1, 2]))).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);
            await seed;
        }
        armed = true;
        Task command = receive ? client.ReceiveAsync(new(authority.Socket, authority.Capability)).AsTask()
            : client.SendAsync(new(authority.Socket, authority.Capability, new BoundedBytes([3, 4]))).AsTask();
        var dispatch = Task.Run(host.ProcessNext);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error);
            Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
            Assert.False(dispatch.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.SessionDraining, (await dispatch.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.WaitAsync(TimeSpan.FromSeconds(10)));
        var sockets = NetworkEntries(host);
        var entry = sockets.Values.Cast<object>().Single();
        var packets = (Queue<byte[]>)entry.GetType().GetProperty("Packets")!.GetValue(entry)!;
        Assert.Equal(receive ? 0 : 1, packets.Count);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        Assert.Empty(sockets.Keys.Cast<object>());
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, authority.Capability, CapabilityRights.Read).Error);
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, kernel.EndpointSessions.ActivePinCount(session));
    }

    private sealed class PacketAdmissionHook(Action action) : INativeSipDeterministicHook
    {
        public void At(NativeSipDeterministicPoint point)
        {
            if (point == NativeSipDeterministicPoint.AfterCapabilityCommit) action();
        }
    }

    [Fact]
    public async Task NetworkDrainRetainsLostSealRecordAndConsumesEarlierSuccessfulSocketOnly()
    {
        var (kernel, service, caller, capability, session) = NetworkDrainScenario();
        var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session).Value!;
        var client = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var firstRequest = client.OpenAsync(new(capability, "loopback:first")).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var first = (await firstRequest).Authority;
        var secondRequest = client.OpenAsync(new(capability, "loopback:lost-seal")).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var second = (await secondRequest).Authority;
        var thirdRequest = client.OpenAsync(new(capability, "loopback:later")).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var third = (await thirdRequest).Authority;
        // Fault injection removes an existing real owner's record, never creates closure evidence.
        var records = (global::System.Collections.IDictionary)typeof(SealedObjectAuthority)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.SealedObjects)!;
        records.Remove(second.Socket.Seal.OpaqueToken);
        Assert.Equal(KernelError.StaleHandle, kernel.CloseSession(caller, session).Error);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, first.Capability, CapabilityRights.Read).Error);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, second.Capability, CapabilityRights.Read).Error);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, third.Capability, CapabilityRights.Read).Error);
        Assert.Equal(2, NetworkEntries(host).Count);
        Assert.Equal(KernelError.StaleHandle, kernel.CloseSession(caller, session).Error);
        Assert.Equal(2, NetworkEntries(host).Count);
        Assert.True(kernel.EndpointSessions.Resolve(session, caller).IsSuccess);
        Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Service, ProcessHandle Caller, CapabilityId Capability,
        EndpointSessionHandle Session) NetworkDrainScenario()
    {
        var kernel = new RuntimeKernel();
        var protocol = INetworkServiceProtocol.CreateDefinition();
        var service = AdmitNativeComponent(kernel, 2020, 20200, "network-drain", "network-drain-component",
            protocol, INetworkServiceResponseProtocol.Definition, ResourceKind.Network,
            CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Process;
        var caller = TestFixtures.Create(kernel, 2021, 20210).Handle;
        var capability = kernel.MintCapability(new DomainId(20210), caller, ResourceKind.Network,
            CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        var session = kernel.OpenSession(caller, kernel.ResolveByServiceName("network-drain").Value, [capability]).Value;
        return (kernel, service, caller, capability, session);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DirectFileSentryUsesSamePendingDrainInterlock(bool afterEffect)
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new PointHook(afterEffect ? NativeSipDeterministicPoint.AfterImplementationExit
            : NativeSipDeterministicPoint.BeforeCapabilityCommit, () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace: null, hook).Value!;
        var request = new OpenFileRequest(capability, "/direct-drain", true);
        var begun = kernel.BeginInlineSessionInvocation(caller, service, session, IFileServiceProtocol.Message_OpenAsync, request);
        Assert.True(begun.IsSuccess, begun.Message);
        using var invocation = begun.Value!;
        var sentry = Task.Run(() =>
        {
            var context = invocation.Context;
            return IFileServiceGeneratedOperationSentries.InvokeRuntime_OpenAsync(host, in context, request);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error);
            Assert.Equal(KernelError.SessionDraining, host.Drain().Error);
            Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
            Assert.False(sentry.IsCompleted);
        }
        finally { release.Set(); }
        var returned = await sentry.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(afterEffect, returned.IsSuccess);
        if (!afterEffect) Assert.Equal((int)KernelError.SessionDraining, returned.ErrorCode);
        Assert.True(kernel.SettleInlineSessionInvocation(service, invocation, returned.IsSuccess).IsSuccess);
        invocation.Dispose();
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        if (afterEffect)
            Assert.Equal(KernelError.CapabilityRevoked,
                kernel.ValidateCapability(caller, returned.Value!.Authority.Capability, CapabilityRights.Read).Error);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task FileHostPendingDispatchCannotCloseAndRealRetryConsumesLateFile(int boundary)
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new PointHook(boundary == 0 ? NativeSipDeterministicPoint.BeforeCapabilityCommit
            : boundary == 1 ? NativeSipDeterministicPoint.BeforeImplementationEntry
            : NativeSipDeterministicPoint.BeforePublication, () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace, hook).Value!;
        var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var opening = client.OpenAsync(new(capability, "/pending-file", true)).AsTask();
        var dispatch = Task.Run(host.ProcessNext);
        var queued = client.OpenAsync(new(capability, "/queued-file", true)).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
            Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error);
            Assert.Equal(KernelError.SessionDraining, host.Drain().Error);
            Assert.True(kernel.EndpointSessions.Resolve(session, caller).IsSuccess);
            Assert.False(dispatch.IsCompleted);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.SessionDraining, (await dispatch.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(NativeSipCompletedEvent.ResponsePublished, trace.Events);
        if (boundary == 0)
        {
            Assert.DoesNotContain(NativeSipCompletedEvent.CapabilityAdmitted, trace.Events);
            Assert.DoesNotContain(NativeSipCompletedEvent.ImplementationEntered, trace.Events);
        }
        var files = (global::System.Collections.IDictionary)typeof(RuntimeFileServiceHost)
            .GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        Assert.Equal(boundary == 0 ? 0 : 1, files.Count);
        var issued = kernel.CapabilityAuthority.InspectionSnapshot().Where(record =>
            record.Descriptor.ResourceKind == ResourceKind.File &&
            record.Descriptor.ResourceId.StartsWith("file-object:", StringComparison.Ordinal)).Select(record => record.Descriptor.CapabilityId).ToArray();
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        Assert.Empty(files.Keys.Cast<object>());
        Assert.All(issued, id => Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, id, CapabilityRights.Read).Error));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FileHostDispatchExceptionKeepsNativeClosurePinnedBeforeAndAfterEffect(bool afterEffect)
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var hook = new PointHook(afterEffect ? NativeSipDeterministicPoint.BeforePublication
            : NativeSipDeterministicPoint.AfterInvocationAccepted,
            () => throw new InvalidOperationException("File dispatch outcome unknown."));
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace: null, hook).Value!;
        var opening = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session))
            .OpenAsync(new(capability, "/unknown-file", true)).AsTask();
        Assert.Throws<InvalidOperationException>(() => host.ProcessNext());
        Assert.Equal(KernelError.ExternalEffectUncontained, kernel.CloseSession(caller, session).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, kernel.CloseSession(caller, session).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, kernel.FaultProcess(service).Error);
        Assert.False(kernel.QueryProcessTeardown(service).Value.LocalReclaimCompleted);
        Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
        var files = (global::System.Collections.IDictionary)typeof(RuntimeFileServiceHost)
            .GetField("_files", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        Assert.Equal(afterEffect ? 1 : 0, files.Count);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(0, 1)] [InlineData(0, 2)]
    [InlineData(1, 0)] [InlineData(1, 1)] [InlineData(1, 2)]
    [InlineData(2, 0)] [InlineData(2, 1)] [InlineData(2, 2)]
    [InlineData(3, 0)] [InlineData(3, 1)] [InlineData(3, 2)]
    public async Task NativeChildCreationRefusalTracksRealCompensationAndRetries(int contour, int closureOutcome)
    {
        var (kernel, host, caller, session, manager) = AttachedChildDrainScenario();
        var child = new ProcessHandle(new ProcessId(2092), 1);
        var relations = (global::System.Collections.IDictionary)typeof(RuntimeProcessServiceHost)
            .GetField("_children", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "creation-required", CapabilityRights.Read);
        CapabilityId delegatedSource = default;
        if (contour == 1)
        {
            // Fill actual sealed owner capacity with a real earlier child.
            typeof(SealedObjectAuthority).GetField("_capacity", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(kernel.SealedObjects, 1);
            var seeded = manager.CreateAsync(TestFixtures.Manifest(2094, 20940)).AsTask();
            Assert.True(host.ProcessNext().IsSuccess);
            await seeded;
        }
        if (contour == 2)
            typeof(CapabilityAuthority).GetField("_nonceFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(kernel.CapabilityAuthority, (Func<Guid>)(() => Guid.Empty));
        if (contour == 3)
        {
            // Real existing owner issuance with exhausted delegation depth;
            // rights-only preflight passes, actual delegation refuses.
            var minted = kernel.CapabilityAuthority.Mint(new DomainId(20910), new DomainId(20910),
                ResourceKind.KernelService, requirement.ResourceId, CapabilityRights.Read | CapabilityRights.Delegate,
                caller.Generation, 1, range: null, session: null, quota: ulong.MaxValue, delegationDepth: 0);
            Assert.True(minted.IsSuccess);
            delegatedSource = minted.Value!.CapabilityId;
            kernel.Processes.Resolve(caller).Value!.AddCapability(delegatedSource);
        }
        var calls = 0;
        var outcome = closureOutcome;
        kernel.RegisterComposedWorkTeardownParticipant(new ChildDrainParticipant(process =>
        {
            if (process != child) return KernelResult.Ok();
            calls++;
            Assert.True(relations.Contains(child));
            Assert.True(kernel.QueryProcessTeardown(child).Value.LocalAuthorizationRevoked);
            if (contour != 2)
            {
                var control = Assert.Single(kernel.CapabilityAuthority.InspectionSnapshot(), record =>
                    record.Descriptor.ResourceKind == ResourceKind.Process &&
                    record.Descriptor.ResourceId == CapabilityResourceIds.Process(child)).Descriptor.CapabilityId;
                Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, control, CapabilityRights.Configure).Error);
            }
            if (outcome == 2) throw new InvalidOperationException("Child compensation outcome unknown.");
            return outcome == 1 ? KernelResult.Fail(KernelError.ExternalEffectUncontained, "Child compensation refused.")
                : KernelResult.Ok();
        }));
        var manifest = TestFixtures.Manifest(2092, 20920, capabilities: contour is 0 or 3 ? [requirement] : null);
        var creating = (contour == 3 ? manager.CreateAsync(manifest, initialCapabilities: [new(delegatedSource, CapabilityRights.Read)])
            : manager.CreateAsync(manifest)).AsTask();
        var refused = host.ProcessNext();
        var original = contour == 0 ? KernelError.MissingCapability : contour == 3 ? KernelError.DelegationDenied : KernelError.CapacityExhausted;
        Assert.Equal(closureOutcome == 0 ? original : closureOutcome == 1 ? KernelError.ExternalEffectUncontained : KernelError.PlatformFaulted,
            refused.Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creating);
        Assert.Equal(1, calls);
        if (closureOutcome != 0)
        {
            Assert.True(relations.Contains(child));
            Assert.False(kernel.QueryProcessTeardown(child).Value.LocalReclaimCompleted);
            Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
            Assert.Equal(closureOutcome == 1 ? KernelError.ExternalEffectUncontained : KernelError.PlatformFaulted,
                kernel.CloseSession(caller, session).Error);
            Assert.Equal(2, calls);
            outcome = 0;
        }
        else Assert.False(relations.Contains(child));
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        Assert.Equal(closureOutcome == 0 ? 1 : 3, calls);
        Assert.Empty(relations.Keys.Cast<object>());
        Assert.True(kernel.Processes.ResolveTerminalState(child).IsSuccess);
    }

    private static (RuntimeKernel Kernel, RuntimeProcessServiceHost Host, ProcessHandle Caller,
        EndpointSessionHandle Session, ProcessManager Manager) AttachedChildDrainScenario(INativeSipDeterministicHook? hook = null)
    {
        var kernel = new RuntimeKernel();
        var service = TestFixtures.Create(kernel, 2090, 20900).Handle;
        var caller = TestFixtures.Create(kernel, 2091, 20910).Handle;
        var create = kernel.MintCapability(new DomainId(20910), caller, ResourceKind.Process,
            CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var protocol = IProcessServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "child-drain", Contract(protocol), protocol,
            IProcessServiceResponseProtocol.Definition,
            [new(ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute)]).Value!;
        var session = kernel.OpenSession(caller, descriptor, [create]).Value;
        var host = RuntimeProcessServiceHost.CreateForSession(kernel, service, session, hook).Value!;
        return (kernel, host, caller, session,
            new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), create));
    }

    private sealed class ChildDrainParticipant(Func<ProcessHandle, KernelResult> close) : IComposedWorkTeardownParticipant
    {
        public KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner) => close(process);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProcessHostDrainWaitsForPendingDispatchAndRejectsQueuedNewEffect(bool afterEffect)
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hook = new PointHook(afterEffect ? NativeSipDeterministicPoint.BeforePublication
            : NativeSipDeterministicPoint.AfterSessionResolved, () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var (kernel, host, caller, session, manager) = AttachedChildDrainScenario(hook);
        var creating = manager.CreateAsync(TestFixtures.Manifest(2092, 20920)).AsTask();
        var dispatch = Task.Run(host.ProcessNext);
        var queued = manager.CreateAsync(TestFixtures.Manifest(2093, 20930)).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
            Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error);
            Assert.Equal(KernelError.SessionDraining, host.Drain().Error);
            Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
            Assert.True(kernel.EndpointSessions.Resolve(session, caller).IsSuccess);
            Assert.False(dispatch.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True((await dispatch.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        var child = await creating.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        Assert.True(kernel.Processes.ResolveTerminalState(child.Authority.Process).IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new ProcessId(2093), 1)).Error);
    }

    [Fact]
    public async Task ProcessHostSourceRevokeBeforeEffectBalancesDispatchForActualDrainRetry()
    {
        RuntimeKernel? kernel = null;
        CapabilityId source = default;
        var hook = new PointHook(NativeSipDeterministicPoint.AfterSessionResolved,
            () => Assert.True(kernel!.RevokeCapability(source).IsSuccess));
        var scenario = AttachedChildDrainScenario(hook);
        kernel = scenario.Kernel;
        source = Assert.Single(kernel.EndpointSessions.Resolve(scenario.Session, scenario.Caller).Value!.Capabilities);
        var creating = scenario.Manager.CreateAsync(TestFixtures.Manifest(2092, 20920)).AsTask();
        Assert.Equal(KernelError.CapabilityRevoked, scenario.Host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creating);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new ProcessId(2092), 1)).Error);
        Assert.True(kernel.CloseSession(scenario.Caller, scenario.Session).IsSuccess);
        Assert.Equal(KernelError.SessionDraining, scenario.Host.ProcessNext().Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProcessHostDispatchExceptionPinsUnknownClosureBeforeAndAfterChildEffect(bool afterEffect)
    {
        var hook = new PointHook(afterEffect ? NativeSipDeterministicPoint.BeforePublication
            : NativeSipDeterministicPoint.AfterInvocationAccepted,
            () => throw new InvalidOperationException("Native dispatch outcome unknown."));
        var (kernel, host, caller, session, manager) = AttachedChildDrainScenario(hook);
        var provider = kernel.EndpointSessions.Resolve(session, caller).Value!.Service;
        var creating = manager.CreateAsync(TestFixtures.Manifest(2092, 20920)).AsTask();
        Assert.Throws<InvalidOperationException>(() => host.ProcessNext());
        Assert.Equal(KernelError.ExternalEffectUncontained, kernel.CloseSession(caller, session).Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, kernel.CloseSession(caller, session).Error);
        Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
        Assert.Equal(KernelError.ExternalEffectUncontained, kernel.TerminateProcess(provider).Error);
        Assert.False(kernel.QueryProcessTeardown(provider).Value.LocalReclaimCompleted);
        Assert.Equal(afterEffect, kernel.Processes.Resolve(new(new ProcessId(2092), 1)).IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creating.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ProcessHostDrainCallbackAllowsOwnerQueryAndRefusesOverlappingDrainAndDispatch()
    {
        var (kernel, host, caller, session, manager) = AttachedChildDrainScenario();
        var creating = manager.CreateAsync(TestFixtures.Manifest(2092, 20920)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await creating;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kernel.RegisterComposedWorkTeardownParticipant(new ChildDrainParticipant(process =>
        {
            if (process != child.Authority.Process) return KernelResult.Ok();
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return KernelResult.Ok();
        }));
        var drain = Task.Run(() => kernel.CloseSession(caller, session));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(KernelError.SessionDraining, host.Drain().Error);
            Assert.Equal(KernelError.SessionDraining, host.ProcessNext().Error);
            var pending = await Task.Run(() => kernel.QueryProcessTeardown(child.Authority.Process)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(pending.IsSuccess);
            Assert.False(pending.Value.LocalReclaimCompleted);
            Assert.False(drain.IsCompleted);
        }
        finally { release.Set(); }
        Assert.True((await drain.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        Assert.True(kernel.Processes.ResolveTerminalState(child.Authority.Process).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AttachedChildDrainConsumesSuccessfulRelationBeforeLaterFailureAndRetries(bool throws)
    {
        var (kernel, host, caller, session, manager) = AttachedChildDrainScenario();
        var creatingFirst = manager.CreateAsync(TestFixtures.Manifest(2092, 20920)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var first = await creatingFirst;
        var creatingSecond = manager.CreateAsync(TestFixtures.Manifest(2093, 20930)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var second = await creatingSecond;
        var refuse = true;
        var firstCalls = 0;
        var secondCalls = 0;
        kernel.RegisterComposedWorkTeardownParticipant(new ChildDrainParticipant(process =>
        {
            if (process == first.Authority.Process) firstCalls++;
            if (process != second.Authority.Process) return KernelResult.Ok();
            secondCalls++;
            if (!refuse) return KernelResult.Ok();
            if (throws) throw new InvalidOperationException("Child closure receipt unavailable.");
            return KernelResult.Fail(KernelError.ExternalEffectUncontained, "Child drain refused.");
        }));
        var refused = kernel.CloseSession(caller, session);
        Assert.Equal(throws ? KernelError.PlatformFaulted : KernelError.ExternalEffectUncontained, refused.Error);
        Assert.Equal(1, firstCalls);
        Assert.Equal(1, secondCalls);
        Assert.True(kernel.Processes.ResolveTerminalState(first.Authority.Process).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked,
            kernel.ValidateCapability(caller, first.Authority.ControlCapability, CapabilityRights.Configure).Error);
        var relations = (global::System.Collections.IDictionary)typeof(RuntimeProcessServiceHost)
            .GetField("_children", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        Assert.Single(relations.Keys.Cast<object>());
        Assert.True(relations.Contains(second.Authority.Process));
        Assert.False(kernel.QueryProcessTeardown(second.Authority.Process).Value.LocalReclaimCompleted);
        Assert.True(kernel.EndpointSessions.Resolve(session, caller).IsSuccess);
        refuse = false;
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
        Assert.Equal(1, firstCalls);
        Assert.Equal(2, secondCalls);
        Assert.Empty(relations.Keys.Cast<object>());
        Assert.True(kernel.Processes.ResolveTerminalState(second.Authority.Process).IsSuccess);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task AttachedChildDrainRequiresExactOwnerRetirementReceiptAcrossAba(int contour)
    {
        var (kernel, host, caller, session, manager) = AttachedChildDrainScenario();
        var creating = manager.CreateAsync(TestFixtures.Manifest(2092, 20920)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await creating;
        Assert.True((contour == 1 ? kernel.FaultProcess(child.Authority.Process)
            : kernel.TerminateProcess(child.Authority.Process)).IsSuccess);
        var fresh = TestFixtures.Create(kernel, 2092, 20920, 2).Handle;
        Assert.True(kernel.AdmitProcess(fresh).IsSuccess);
        if (contour == 2)
        {
            // Lose the authoritative receipt as well as its route: routing
            // loss alone must not erase the completed original lifetime fact.
            var receipts = (global::System.Collections.IDictionary)typeof(ProcessRegistry)
                .GetField("_terminalStates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Processes)!;
            receipts.Remove(child.Authority.Process);
            var ownedReceipts = (global::System.Collections.IDictionary)typeof(ProcessRegistry)
                .GetField("_ownedTerminalStates", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Processes)!;
            ownedReceipts.Remove(child.Authority.Process);
        }
        var drained = kernel.CloseSession(caller, session);
        var relations = (global::System.Collections.IDictionary)typeof(RuntimeProcessServiceHost)
            .GetField("_children", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        if (contour == 2)
        {
            Assert.Equal(KernelError.StaleHandle, drained.Error);
            Assert.Single(relations.Keys.Cast<object>());
            Assert.True(kernel.EndpointSessions.Resolve(session, caller).IsSuccess);
            Assert.Equal(KernelError.StaleHandle, kernel.CloseSession(caller, session).Error);
        }
        else
        {
            Assert.True(drained.IsSuccess, drained.Message);
            Assert.Empty(relations.Keys.Cast<object>());
        }
        Assert.True(kernel.Processes.Resolve(fresh).IsSuccess);
        Assert.Equal(ProcessState.Admitted, kernel.Processes.Resolve(fresh).Value!.State);
    }

    [Fact]
    public void GeneratedSentryAbiIsOpaqueOutsideFriendTcbAssemblies()
    {
        Assert.Empty(typeof(TrustedSipInvocationContext).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(TrustedSipInvocationContext).GetProperties(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(GeneratedSipSentryResult<FileObjectResponse>).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(GeneratedSipSentryResult<FileObjectResponse>).GetProperties(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(GeneratedSipSentryResult<FileObjectResponse>).GetFields(BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public void GeneratedFileSentryPossessionDoesNotBypassLiveSessionAuthority()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace).Value!;
        var staleContext = new TrustedSipInvocationContext(
            caller with { Generation = caller.Generation + 1 },
            service,
            session,
            default);

        var outcome = IFileServiceGeneratedOperationSentries.InvokeRuntime_OpenAsync(
            host,
            in staleContext,
            new OpenFileRequest(capability, "/must-not-open", true));

        Assert.False(outcome.IsSuccess);
        Assert.Equal((int)KernelError.StaleHandle, outcome.ErrorCode);
        Assert.DoesNotContain(NativeSipCompletedEvent.CapabilityAdmitted, trace.Events);
        Assert.DoesNotContain(NativeSipCompletedEvent.ImplementationEntered, trace.Events);
        Assert.Contains(typeof(IIFileServiceGeneratedSentryTarget_OpenAsync), typeof(RuntimeFileServiceHost).GetInterfaces());
    }

    [Fact]
    public async Task OrdinaryFileSipTraceRecordsOnlyCompletedOwnerLinearizations()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace).Value!;
        var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));

        var pending = client.OpenAsync(new(capability, "/trace", true)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        _ = await pending;

        Assert.Equal(
            [
                NativeSipCompletedEvent.RequestReceived,
                NativeSipCompletedEvent.InvocationAccepted,
                NativeSipCompletedEvent.SessionResolved,
                NativeSipCompletedEvent.CapabilityAdmitted,
                NativeSipCompletedEvent.ImplementationEntered,
                NativeSipCompletedEvent.ImplementationExited,
                NativeSipCompletedEvent.ResponsePublished,
            ],
            trace.Events);
    }

    [Fact]
    public async Task RevokedFileAuthorityNeverProducesAdmissionOrImplementationTrace()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace).Value!;
        var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var pending = client.OpenAsync(new(capability, "/revoked-trace", true)).AsTask();

        Assert.True(kernel.RevokeCapability(capability).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(
            [
                NativeSipCompletedEvent.RequestReceived,
                NativeSipCompletedEvent.InvocationAccepted,
                NativeSipCompletedEvent.SessionResolved,
                NativeSipCompletedEvent.ResponseCancelled,
            ],
            trace.Events);
        Assert.DoesNotContain(NativeSipCompletedEvent.CapabilityAdmitted, trace.Events);
        Assert.DoesNotContain(NativeSipCompletedEvent.ImplementationEntered, trace.Events);
    }

    [Fact]
    public async Task MalformedGeneratedFileProjectionNeverReachesAdmissionOrImplementation()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace).Value!;
        var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var pending = client.OpenAsync(new(capability, " ", true)).AsTask();

        Assert.Equal(KernelError.UnsupportedPayload, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.DoesNotContain(NativeSipCompletedEvent.CapabilityAdmitted, trace.Events);
        Assert.DoesNotContain(NativeSipCompletedEvent.ImplementationEntered, trace.Events);
        Assert.Equal(NativeSipCompletedEvent.ResponseCancelled, trace.Events[^1]);
    }

    [Fact]
    public async Task CancellationBeforeGeneratedFileSentryCommitNeverEntersSentryTarget()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace).Value!;
        using var cancellation = new CancellationTokenSource();
        var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session, cancellation.Token));
        var pending = client.OpenAsync(new(capability, "/cancelled-before-commit", true)).AsTask();
        cancellation.Cancel();

        Assert.True(host.ProcessNext().IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(
            [
                NativeSipCompletedEvent.RequestReceived,
                NativeSipCompletedEvent.CancellationCommitted,
                NativeSipCompletedEvent.ResponseCancelled,
            ],
            trace.Events);
        Assert.DoesNotContain(NativeSipCompletedEvent.InvocationAccepted, trace.Events);
        Assert.DoesNotContain(NativeSipCompletedEvent.ImplementationEntered, trace.Events);
    }

    [Fact]
    public async Task SessionCloseAfterExactResolutionPreventsGeneratedSentryAdmission()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        var hook = new PointHook(NativeSipDeterministicPoint.AfterSessionResolved, () =>
            Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error));
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace, hook).Value!;
        var pending = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session))
            .OpenAsync(new(capability, "/closed-after-resolution", true)).AsTask();

        var processed = host.ProcessNext();
        Assert.False(processed.IsSuccess);
        Assert.Equal(KernelError.SessionDraining, processed.Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(hook.Fired);
        Assert.DoesNotContain(NativeSipCompletedEvent.CapabilityAdmitted, trace.Events);
        Assert.DoesNotContain(NativeSipCompletedEvent.ImplementationEntered, trace.Events);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }

    [Fact]
    public async Task ServiceFaultAfterSessionResolutionPreventsGeneratedSentryAdmission()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        var hook = new PointHook(NativeSipDeterministicPoint.AfterSessionResolved, () =>
            Assert.Equal(KernelError.SessionDraining, kernel.FaultProcess(service).Error));
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace, hook).Value!;
        var pending = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session))
            .OpenAsync(new(capability, "/faulted-service", true)).AsTask();

        var processed = host.ProcessNext();
        Assert.False(processed.IsSuccess);
        Assert.Equal(KernelError.SessionDraining, processed.Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(hook.Fired);
        Assert.DoesNotContain(NativeSipCompletedEvent.CapabilityAdmitted, trace.Events);
        Assert.DoesNotContain(NativeSipCompletedEvent.ImplementationEntered, trace.Events);
        Assert.Equal(KernelError.PlatformBindingDraining, kernel.FaultProcess(service).Error);
        Assert.False(kernel.QueryProcessTeardown(service).Value.LocalReclaimCompleted);
    }

    [Fact]
    public async Task RevokeImmediatelyBeforeCapabilityCommitFailsClosed()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        var hook = new PointHook(NativeSipDeterministicPoint.BeforeCapabilityCommit, () =>
            Assert.True(kernel.RevokeCapability(capability).IsSuccess));
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace, hook).Value!;
        var pending = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session))
            .OpenAsync(new(capability, "/revoked-at-commit", true)).AsTask();

        Assert.Equal(KernelError.CapabilityRevoked, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(hook.Fired);
        Assert.DoesNotContain(NativeSipCompletedEvent.CapabilityAdmitted, trace.Events);
        Assert.DoesNotContain(NativeSipCompletedEvent.ImplementationEntered, trace.Events);
    }

    [Fact]
    public async Task RevokeAfterCapabilityCommitPreservesAdmittedOrdinaryOutcomeAndReleasesLease()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        var hook = new PointHook(NativeSipDeterministicPoint.AfterCapabilityCommit, () =>
            Assert.True(kernel.RevokeCapability(capability).IsSuccess));
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace, hook).Value!;
        var pending = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session))
            .OpenAsync(new(capability, "/revoked-after-commit", true)).AsTask();

        Assert.True(host.ProcessNext().IsSuccess);
        _ = await pending;
        Assert.True(hook.Fired);
        Assert.Contains(NativeSipCompletedEvent.CapabilityAdmitted, trace.Events);
        Assert.Contains(NativeSipCompletedEvent.ImplementationEntered, trace.Events);
        Assert.Contains(NativeSipCompletedEvent.ResponsePublished, trace.Events);
        Assert.Equal(0, kernel.CapabilityAuthority.ActiveOperationLeaseCount);
    }

    [Fact]
    public async Task SessionCloseBeforePublicationCannotPublishAndDrainsServiceState()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var trace = new CompletedEventSink();
        var hook = new PointHook(NativeSipDeterministicPoint.BeforePublication, () =>
            Assert.Equal(KernelError.SessionDraining, kernel.CloseSession(caller, session).Error));
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session, trace, hook).Value!;
        var pending = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session))
            .OpenAsync(new(capability, "/closed-before-publication", true)).AsTask();

        var processed = host.ProcessNext();
        Assert.False(processed.IsSuccess);
        Assert.Equal(KernelError.SessionDraining, processed.Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(hook.Fired);
        Assert.Contains(NativeSipCompletedEvent.ImplementationExited, trace.Events);
        Assert.DoesNotContain(NativeSipCompletedEvent.ResponsePublished, trace.Events);
        Assert.True(kernel.CloseSession(caller, session).IsSuccess);
    }

    [Fact]
    public async Task ProcessFacadeUsesGeneratedClientSessionAndExactChildAuthority()
    {
        var kernel = new RuntimeKernel();
        var protocol = IProcessServiceProtocol.CreateDefinition();
        var service = AdmitNativeComponent(kernel, 2000, 20000, "process", "process-positive-component", protocol, IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Process;
        var caller = TestFixtures.Create(kernel, 2001, 20010).Handle;
        var create = kernel.MintCapability(new DomainId(20010), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("process").Value;
        var session = kernel.OpenSession(caller, descriptor, [create]).Value;
        var host = RuntimeProcessServiceHost.CreateForSession(kernel, service, session).Value!;
        var manager = new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), create);
        var manifest = TestFixtures.Manifest(2002, 20020, 1, "native-child");

        var pendingCreate = manager.CreateAsync(manifest).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await pendingCreate;
        await Dispatch(child.StartAsync(), host);
        await Dispatch(child.ParkAsync(), host);
        await Dispatch(child.ResumeAsync(), host);
        await Dispatch(child.TerminateAsync(), host);
        var wait = child.WaitForExitAsync().AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        Assert.Equal(ProcessState.Exited, await wait);

        var forged = new ProcessCommand(child.Authority.Process, create);
        var forgedTask = IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)).StartAsync(forged).AsTask();
        Assert.Equal(KernelError.InsufficientRights, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forgedTask);
    }

    [Fact]
    public async Task ProcessCreationDelegatesExactSubsetBeforeAdmissionAndRejectsBroadeningBeforeChildEffect()
    {
        var kernel = new RuntimeKernel();
        var service = TestFixtures.Create(kernel, 2040, 20400).Handle;
        var caller = TestFixtures.Create(kernel, 2041, 20410).Handle;
        var create = kernel.MintCapability(new DomainId(20410), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var source = kernel.MintCapability(new DomainId(20410), caller, ResourceKind.KernelService, "child-clock", CapabilityRights.Read | CapabilityRights.Delegate).Value!.CapabilityId;
        var protocol = IProcessServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "process-admission", Contract(protocol), protocol, IProcessServiceResponseProtocol.Definition, [new(ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute)]).Value!;
        var session = kernel.OpenSession(caller, descriptor, [create]).Value;
        var host = RuntimeProcessServiceHost.CreateForSession(kernel, service, session).Value!;
        var manager = new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), create);
        var requirement = new CapabilityRequirementV1(ResourceKind.KernelService, "child-clock", CapabilityRights.Read);
        var manifest = TestFixtures.Manifest(2042, 20420, 1, "admitted-with-delegation", capabilities: [requirement]);

        var creating = manager.CreateAsync(manifest, initialCapabilities: [new(source, CapabilityRights.Read)]).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await creating;
        Assert.True(kernel.Processes.Resolve(child.Authority.Process).IsSuccess);

        var broadenedManifest = TestFixtures.Manifest(2043, 20430, 1, "rejected-broader-delegation", capabilities: [requirement]);
        var rejected = manager.CreateAsync(broadenedManifest, initialCapabilities: [new(source, CapabilityRights.Read | CapabilityRights.Write)]).AsTask();
        Assert.Equal(KernelError.DelegationDenied, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new ProcessId(2043), 1)).Error);
    }

    [Fact]
    public async Task WaitForExitPreservesExactFaultedTerminalReceipt()
    {
        var kernel = new RuntimeKernel();
        var service = TestFixtures.Create(kernel, 2050, 20500).Handle;
        var caller = TestFixtures.Create(kernel, 2051, 20510).Handle;
        var create = kernel.MintCapability(new DomainId(20510), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var protocol = IProcessServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "process-terminal", Contract(protocol), protocol, IProcessServiceResponseProtocol.Definition, [new(ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute)]).Value!;
        var session = kernel.OpenSession(caller, descriptor, [create]).Value;
        var host = RuntimeProcessServiceHost.CreateForSession(kernel, service, session).Value!;
        var manager = new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), create);
        var creating = manager.CreateAsync(TestFixtures.Manifest(2052, 20520, 1, "faulted-child")).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var child = await creating;

        Assert.True(kernel.FaultProcess(child.Authority.Process).IsSuccess);
        var waiting = child.WaitForExitAsync().AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        Assert.Equal(ProcessState.Faulted, await waiting);
    }

    [Fact]
    public async Task UnsupportedIndependentChildPolicyFailsBeforeProcessCreation()
    {
        var kernel = new RuntimeKernel();
        var service = TestFixtures.Create(kernel, 2060, 20600).Handle;
        var caller = TestFixtures.Create(kernel, 2061, 20610).Handle;
        var create = kernel.MintCapability(new DomainId(20610), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var protocol = IProcessServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "process-attached-only", Contract(protocol), protocol, IProcessServiceResponseProtocol.Definition, [new(ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute)]).Value!;
        var session = kernel.OpenSession(caller, descriptor, [create]).Value;
        var host = RuntimeProcessServiceHost.CreateForSession(kernel, service, session).Value!;
        var manager = new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), create);

        var rejected = manager.CreateAsync(TestFixtures.Manifest(2062, 20620, 1, "unsupported-independent"), ChildProcessPolicy.Independent).AsTask();

        Assert.Equal(KernelError.PlatformUnsupported, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rejected);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new ProcessId(2062), 1)).Error);
    }

    [Fact]
    public async Task FileFacadeUsesObjectGenerationCapabilityAndSanitizingCopy()
    {
        var (kernel, service, caller, capability, session, descriptor) = FileScenario();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session).Value!;
        var facade = new SingPlus.System.FileSystem(IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), capability);
        var open = facade.OpenAsync("/service-owned/path", create: true).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        var file = await open;
        byte[] source = [1, 2, 3];
        var write = file.WriteAsync(source).AsTask();
        source[0] = 99;
        Assert.True(host.ProcessNext().IsSuccess);
        await write;
        var read = file.ReadAsync().AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        Assert.Equal([1, 2, 3], await read);
        await Dispatch(file.CloseAsync(), host);

        var staleRead = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)).ReadAsync(new(file.Authority.File, file.Authority.Capability)).AsTask();
        Assert.Equal(KernelError.StaleHandle, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleRead);
        Assert.True(kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
        Assert.Equal(KernelError.ServiceUnavailable, kernel.OpenSession(caller, descriptor, [capability]).Error);
    }

    [Fact]
    public async Task FileSealAndCapabilityMustBothMatchExactObject()
    {
        var (kernel, service, caller, namespaceCapability, session, _) = FileScenario();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session).Value!;
        var client = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var firstTask = client.OpenAsync(new(namespaceCapability, "/sealed-a", true)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess); var first = (await firstTask).Authority;
        var secondTask = client.OpenAsync(new(namespaceCapability, "/sealed-b", true)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess); var second = (await secondTask).Authority;

        var guessed = client.ReadAsync(new(new(session, first.File.ObjectId, first.File.Generation), first.Capability)).AsTask();
        Assert.Equal(KernelError.InvalidMessage, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guessed);

        var substituted = client.ReadAsync(new(first.File with { ObjectId = second.File.ObjectId }, first.Capability)).AsTask();
        Assert.Equal(KernelError.StaleHandle, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => substituted);

        var wrongCapability = client.ReadAsync(new(first.File, namespaceCapability)).AsTask();
        Assert.Equal(KernelError.WrongCapabilityResource, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrongCapability);

        Assert.True(kernel.RevokeCapability(first.Capability).IsSuccess);
        var revoked = client.ReadAsync(new(first.File, first.Capability)).AsTask();
        Assert.Equal(KernelError.CapabilityRevoked, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => revoked);
    }

    [Fact]
    public async Task ProcessV2RequiresSealedIncarnationAndExactControlCapability()
    {
        var kernel = new RuntimeKernel();
        var protocol = IProcessServiceProtocol.CreateDefinition();
        var service = AdmitNativeComponent(kernel, 2110, 21100, "process-v2", "process-v2-component", protocol,
            IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Process;
        var caller = TestFixtures.Create(kernel, 2111, 21110).Handle;
        var create = kernel.MintCapability(new(21110), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("process-v2").Value;
        var session = kernel.OpenSession(caller, descriptor, [create]).Value;
        var host = RuntimeProcessServiceHost.CreateForSession(kernel, service, session).Value!;
        var client = IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session));
        var creating = client.CreateAsync(new(create, TestFixtures.Manifest(2112, 21120, 1, "process-v2-child"), ChildProcessPolicy.Attached)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess); var authority = (await creating).Authority;

        var guessed = client.StartV2Async(new(new(authority.Process, authority.Process.Generation, default), authority.ControlCapability)).AsTask();
        Assert.Equal(KernelError.InvalidMessage, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guessed);

        var wrong = client.StartV2Async(new(authority.Control, create)).AsTask();
        Assert.Equal(KernelError.WrongCapabilityResource, host.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wrong);

        var valid = client.StartV2Async(new(authority.Control, authority.ControlCapability)).AsTask();
        Assert.True(host.ProcessNext().IsSuccess);
        await valid;
    }

    [Fact]
    public async Task NetworkFacadeUsesSocketAuthorityAndNoDeviceAuthority()
    {
        var kernel = new RuntimeKernel();
        var protocol = INetworkServiceProtocol.CreateDefinition();
        var service = AdmitNativeComponent(kernel, 2020, 20200, "network", "network-positive-component", protocol, INetworkServiceResponseProtocol.Definition, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Process;
        var caller = TestFixtures.Create(kernel, 2021, 20210).Handle;
        var endpoint = kernel.MintCapability(new DomainId(20210), caller, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("network").Value;
        var session = kernel.OpenSession(caller, descriptor, [endpoint]).Value;
        var host = RuntimeNetworkServiceHost.CreateForSession(kernel, service, session).Value!;
        var network = new Network(INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), endpoint);
        var opening = network.OpenAsync("loopback:test").AsTask(); Assert.True(host.ProcessNext().IsSuccess); var socket = await opening;
        await Dispatch(socket.SendAsync(new byte[] { 7, 8, 9 }), host);
        var receive = socket.ReceiveAsync().AsTask(); Assert.True(host.ProcessNext().IsSuccess); Assert.Equal([7, 8, 9], await receive);
        Assert.DoesNotContain(kernel.CapabilityAuthority.SnapshotForDomain(new DomainId(20210)), capability => capability.ResourceKind == ResourceKind.Device);
    }

    [Fact]
    public async Task FileObjectAuthorityCannotCrossEndpointSessionsEvenWhenCallerAndObjectIdMatch()
    {
        var (kernel, service, caller, capability, firstSession, descriptor) = FileScenario();
        var secondSession = kernel.OpenSession(caller, descriptor, [capability]).Value;
        var firstHost = RuntimeFileServiceHost.CreateForSession(kernel, service, firstSession).Value!;
        var secondHost = RuntimeFileServiceHost.CreateForSession(kernel, service, secondSession).Value!;
        var firstClient = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, firstSession));
        var secondClient = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, secondSession));

        var firstOpen = firstClient.OpenAsync(new(capability, "/first", true)).AsTask();
        Assert.True(firstHost.ProcessNext().IsSuccess);
        var first = (await firstOpen).Authority;
        var secondOpen = secondClient.OpenAsync(new(capability, "/second", true)).AsTask();
        Assert.True(secondHost.ProcessNext().IsSuccess);
        var second = (await secondOpen).Authority;

        Assert.Equal(first.File.ObjectId, second.File.ObjectId);
        Assert.NotEqual(first.File.Session, second.File.Session);
        var crossSession = secondClient.ReadAsync(new(first.File, first.Capability)).AsTask();
        Assert.Equal(KernelError.WrongSessionOwner, secondHost.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crossSession);
    }

    [Fact]
    public async Task SocketAuthorityCannotCrossEndpointSessionsEvenWhenCallerAndObjectIdMatch()
    {
        var kernel = new RuntimeKernel();
        var service = TestFixtures.Create(kernel, 2030, 20300).Handle;
        var caller = TestFixtures.Create(kernel, 2031, 20310).Handle;
        var capability = kernel.MintCapability(new DomainId(20310), caller, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        var protocol = INetworkServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(service, "network-session-binding", Contract(protocol), protocol, INetworkServiceResponseProtocol.Definition, [new(ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure)]).Value!;
        var firstSession = kernel.OpenSession(caller, descriptor, [capability]).Value;
        var secondSession = kernel.OpenSession(caller, descriptor, [capability]).Value;
        var firstHost = RuntimeNetworkServiceHost.CreateForSession(kernel, service, firstSession).Value!;
        var secondHost = RuntimeNetworkServiceHost.CreateForSession(kernel, service, secondSession).Value!;
        var firstClient = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, firstSession));
        var secondClient = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, secondSession));

        var firstOpen = firstClient.OpenAsync(new(capability, "first")).AsTask();
        Assert.True(firstHost.ProcessNext().IsSuccess);
        var first = (await firstOpen).Authority;
        var secondOpen = secondClient.OpenAsync(new(capability, "second")).AsTask();
        Assert.True(secondHost.ProcessNext().IsSuccess);
        var second = (await secondOpen).Authority;

        Assert.Equal(first.Socket.ObjectId, second.Socket.ObjectId);
        Assert.NotEqual(first.Socket.Session, second.Socket.Session);
        var crossSession = secondClient.ReceiveAsync(new(first.Socket, first.Capability)).AsTask();
        Assert.Equal(KernelError.WrongSessionOwner, secondHost.ProcessNext().Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crossSession);
    }

    [Fact]
    public async Task LateSourceCancellationCannotOrphanCommittedObjectAuthority()
    {
        var (kernel, service, caller, capability, session, _) = FileScenario();
        var host = RuntimeFileServiceHost.CreateForSession(kernel, service, session).Value!;
        var facade = new SingPlus.System.FileSystem(IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, session)), capability);
        using var cancellation = new CancellationTokenSource();
        var wait = facade.OpenAsync("/still-commits", true, cancellation.Token).AsTask();
        cancellation.Cancel();
        Assert.False(wait.IsCompleted);
        Assert.True(host.ProcessNext().IsSuccess);
        var file = await wait;
        Assert.True(kernel.ValidateCapability(caller, file.Authority.Capability, CapabilityRights.Read).IsSuccess);
    }

    [Fact]
    public async Task ServiceCrashDrainsObjectsBeforeClosingSessionsAndReclaimsAttachedChildren()
    {
        var (kernel, fileService, caller, fileCapability, fileSession, _) = FileScenario();
        var fileHost = RuntimeFileServiceHost.CreateForSession(kernel, fileService, fileSession).Value!;
        var fileClient = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, fileSession));
        var opening = fileClient.OpenAsync(new(fileCapability, "/crash-owned", true)).AsTask();
        Assert.True(fileHost.ProcessNext().IsSuccess);
        var file = (await opening).Authority;

        var fileCrash = kernel.FaultComponent(new ComponentIdentity("file-positive-component"));
        Assert.True(fileCrash.IsSuccess, fileCrash.Message);
        Assert.Equal(ComponentLifecycleState.Reclaimable, fileCrash.Value!.State);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, file.Capability, CapabilityRights.Read).Error);
        var afterCrash = fileClient.ReadAsync(new(file.File, file.Capability)).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => afterCrash);

        var protocol = IProcessServiceProtocol.CreateDefinition();
        var processService = AdmitNativeComponent(kernel, 2060, 20600, "process-crash", "process-crash-component", protocol, IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Process;
        var create = kernel.MintCapability(new DomainId(20110), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("process-crash").Value;
        var processSession = kernel.OpenSession(caller, descriptor, [create]).Value;
        var processHost = RuntimeProcessServiceHost.CreateForSession(kernel, processService, processSession).Value!;
        var manager = new ProcessManager(IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, processSession)), create);
        var creating = manager.CreateAsync(TestFixtures.Manifest(2061, 20610, 1, "attached-crash-child")).AsTask();
        Assert.True(processHost.ProcessNext().IsSuccess);
        var child = await creating;

        var processCrash = kernel.FaultComponent(new ComponentIdentity("process-crash-component"));
        Assert.True(processCrash.IsSuccess, processCrash.Message);
        Assert.Equal(ComponentLifecycleState.Reclaimable, processCrash.Value!.State);
        Assert.Equal(ProcessState.Exited, kernel.Processes.ResolveTerminalState(child.Authority.Process).Value);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, child.Authority.ControlCapability, CapabilityRights.Configure).Error);
    }

    [Fact]
    public void AllNativeServiceFamiliesAreAdmittedAsDeclarativeComponents()
    {
        var kernel = new RuntimeKernel();
        var process = AdmitNativeComponent(kernel, 2070, 20700, "phase8-process", "process-component", IProcessServiceProtocol.CreateDefinition(), IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute);
        var file = AdmitNativeComponent(kernel, 2071, 20710, "phase8-file", "file-component", IFileServiceProtocol.CreateDefinition(), IFileServiceResponseProtocol.Definition, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write);
        var network = AdmitNativeComponent(kernel, 2072, 20720, "phase8-network", "network-component", INetworkServiceProtocol.CreateDefinition(), INetworkServiceResponseProtocol.Definition, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure);

        Assert.Equal(ComponentLifecycleState.Running, process.State);
        Assert.Equal(ComponentLifecycleState.Running, file.State);
        Assert.Equal(ComponentLifecycleState.Running, network.State);
        Assert.Equal("phase8-process", kernel.ResolveByServiceName("phase8-process").Value.Service.Name);
        Assert.Equal("phase8-file", kernel.ResolveByServiceName("phase8-file").Value.Service.Name);
        Assert.Equal("phase8-network", kernel.ResolveByServiceName("phase8-network").Value.Service.Name);
    }

    [Fact]
    public async Task PendingRequestsFaultWithoutEffectWhenEachServiceFamilyCrashesBeforeAcceptance()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 2080, 20800).Handle;

        var processProtocol = IProcessServiceProtocol.CreateDefinition();
        var processService = AdmitNativeComponent(kernel, 2081, 20810, "pending-process", "pending-process-component", processProtocol, IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Process;
        var processCapability = kernel.MintCapability(new DomainId(20800), caller, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute).Value!.CapabilityId;
        var processSession = kernel.OpenSession(caller, kernel.ResolveByServiceName("pending-process").Value, [processCapability]).Value;
        _ = RuntimeProcessServiceHost.CreateForSession(kernel, processService, processSession).Value!;
        var processClient = IProcessServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, processSession));
        var processRequest = processClient.CreateAsync(new(processCapability, TestFixtures.Manifest(2082, 20820, 1, "never-admitted"), ChildProcessPolicy.Attached)).AsTask();
        Assert.True(kernel.FaultComponent(new ComponentIdentity("pending-process-component")).IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processRequest);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new ProcessId(2082), 1)).Error);

        var fileProtocol = IFileServiceProtocol.CreateDefinition();
        var fileService = AdmitNativeComponent(kernel, 2083, 20830, "pending-file", "pending-file-component", fileProtocol, IFileServiceResponseProtocol.Definition, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Process;
        var fileCapability = kernel.MintCapability(new DomainId(20800), caller, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var fileSession = kernel.OpenSession(caller, kernel.ResolveByServiceName("pending-file").Value, [fileCapability]).Value;
        _ = RuntimeFileServiceHost.CreateForSession(kernel, fileService, fileSession).Value!;
        var fileRequest = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, fileSession)).OpenAsync(new(fileCapability, "/never-opened", true)).AsTask();
        Assert.True(kernel.FaultComponent(new ComponentIdentity("pending-file-component")).IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fileRequest);
        Assert.DoesNotContain(kernel.CapabilityAuthority.SnapshotForDomain(new DomainId(20800)), capability => capability.ResourceKind == ResourceKind.File && capability.ResourceId.StartsWith("file-object:", StringComparison.Ordinal));

        var networkProtocol = INetworkServiceProtocol.CreateDefinition();
        var networkService = AdmitNativeComponent(kernel, 2084, 20840, "pending-network", "pending-network-component", networkProtocol, INetworkServiceResponseProtocol.Definition, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Process;
        var networkCapability = kernel.MintCapability(new DomainId(20800), caller, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure).Value!.CapabilityId;
        var networkSession = kernel.OpenSession(caller, kernel.ResolveByServiceName("pending-network").Value, [networkCapability]).Value;
        _ = RuntimeNetworkServiceHost.CreateForSession(kernel, networkService, networkSession).Value!;
        var networkRequest = INetworkServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, networkSession)).OpenAsync(new(networkCapability, "never-opened")).AsTask();
        Assert.True(kernel.FaultComponent(new ComponentIdentity("pending-network-component")).IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => networkRequest);
        Assert.DoesNotContain(kernel.CapabilityAuthority.SnapshotForDomain(new DomainId(20800)), capability => capability.ResourceKind == ResourceKind.Network && capability.ResourceId.StartsWith("socket-object:", StringComparison.Ordinal));
    }

    [Fact]
    public void DrainingRejectsNewSessionsForEveryNativeServiceFamily()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 2090, 20900).Handle;
        var cases = new[]
        {
            (Name: "draining-process", Snapshot: AdmitNativeComponent(kernel, 2091, 20910, "draining-process", "draining-process-component", IProcessServiceProtocol.CreateDefinition(), IProcessServiceResponseProtocol.Definition, ResourceKind.Process, CapabilityResourceIds.ProcessCreate, CapabilityRights.Execute), Kind: ResourceKind.Process, Resource: CapabilityResourceIds.ProcessCreate, Rights: CapabilityRights.Execute),
            (Name: "draining-file", Snapshot: AdmitNativeComponent(kernel, 2092, 20920, "draining-file", "draining-file-component", IFileServiceProtocol.CreateDefinition(), IFileServiceResponseProtocol.Definition, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read), Kind: ResourceKind.File, Resource: CapabilityResourceIds.FileNamespace, Rights: CapabilityRights.Read),
            (Name: "draining-network", Snapshot: AdmitNativeComponent(kernel, 2093, 20930, "draining-network", "draining-network-component", INetworkServiceProtocol.CreateDefinition(), INetworkServiceResponseProtocol.Definition, ResourceKind.Network, CapabilityResourceIds.NetworkEndpoint, CapabilityRights.Configure), Kind: ResourceKind.Network, Resource: CapabilityResourceIds.NetworkEndpoint, Rights: CapabilityRights.Configure),
        };

        foreach (var item in cases)
        {
            var descriptor = kernel.ResolveByServiceName(item.Name).Value;
            var capability = kernel.MintCapability(new DomainId(20900), caller, item.Kind, item.Resource, item.Rights).Value!.CapabilityId;
            Assert.True(kernel.SetServiceAvailability(descriptor.Service, ServiceAvailability.Draining).IsSuccess);
            Assert.Equal(KernelError.ServiceUnavailable, kernel.OpenSession(caller, descriptor, [capability]).Error);
        }
    }

    [Fact]
    public async Task ExactSessionCloseDrainsItsObjectsWithoutAffectingSiblingSession()
    {
        var (kernel, service, caller, capability, firstSession, descriptor) = FileScenario();
        var secondSession = kernel.OpenSession(caller, descriptor, [capability]).Value;
        var firstHost = RuntimeFileServiceHost.CreateForSession(kernel, service, firstSession).Value!;
        var secondHost = RuntimeFileServiceHost.CreateForSession(kernel, service, secondSession).Value!;
        var firstClient = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, firstSession));
        var secondClient = IFileServiceRuntimeClient.Create(new RuntimeSipClientTransport(kernel, caller, secondSession));
        var firstOpen = firstClient.OpenAsync(new(capability, "/first-close", true)).AsTask();
        Assert.True(firstHost.ProcessNext().IsSuccess);
        var first = (await firstOpen).Authority;
        var secondOpen = secondClient.OpenAsync(new(capability, "/second-live", true)).AsTask();
        Assert.True(secondHost.ProcessNext().IsSuccess);
        var second = (await secondOpen).Authority;

        Assert.True(kernel.CloseSession(caller, firstSession).IsSuccess);
        Assert.Equal(KernelError.CapabilityRevoked, kernel.ValidateCapability(caller, first.Capability, CapabilityRights.Read).Error);
        Assert.True(kernel.ValidateCapability(caller, second.Capability, CapabilityRights.Read).IsSuccess);
        var readSecond = secondClient.ReadAsync(new(second.File, second.Capability)).AsTask();
        Assert.True(secondHost.ProcessNext().IsSuccess);
        Assert.Empty((await readSecond).Data.ToArray());
    }

    [Fact]
    public async Task GeneratedClientRejectsMalformedResponseBeforeFacadePublication()
    {
        var client = IFileServiceRuntimeClient.Create(new MalformedTransport());
        var facade = new SingPlus.System.FileSystem(client, new CapabilityId(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => facade.OpenAsync("/x").AsTask());
    }

    [Fact]
    public void SourceLibraryDependencyAndPublicSurfaceStayNeutral()
    {
        var root = FindRoot();
        var project = XDocument.Load(Path.Combine(root, "sdk", "SingPlus.System", "SingPlus.System.csproj"));
        var references = project.Descendants("ProjectReference").Select(x => ((string?)x.Attribute("Include") ?? "").Replace('\\', '/')).ToArray();
        Assert.Equal(2, references.Length);
        Assert.Contains(references, x => x.EndsWith("contracts/SingPlus.Contracts/SingPlus.Contracts.csproj", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(references, x => x.EndsWith("src/Sip/SingPlus.Sip/SingPlus.Sip.csproj", StringComparison.OrdinalIgnoreCase));
        var forbidden = new[] { "Runtime", "Platform", "HybridCpu", "Vmcs", "Vmx", "Mmio", "Irq", "Dma", "Provider" };
        foreach (var member in typeof(ProcessManager).Assembly.GetExportedTypes().SelectMany(t => t.GetMembers()))
        foreach (var word in forbidden) Assert.DoesNotContain(word, member.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Service, ProcessHandle Caller, CapabilityId Capability, EndpointSessionHandle Session, ServiceEndpointDescriptor Descriptor) FileScenario()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 2011, 20110).Handle;
        var protocol = IFileServiceProtocol.CreateDefinition();
        var service = AdmitNativeComponent(kernel, 2012, 20120, "file", "file-positive-component", protocol, IFileServiceResponseProtocol.Definition, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Process;
        var capability = kernel.MintCapability(new DomainId(20110), caller, ResourceKind.File, CapabilityResourceIds.FileNamespace, CapabilityRights.Read | CapabilityRights.Write).Value!.CapabilityId;
        var descriptor = kernel.ResolveByServiceName("file").Value;
        return (kernel, service, caller, capability, kernel.OpenSession(caller, descriptor, [capability]).Value, descriptor);
    }
    private static ServiceContractIdentity Contract(ProtocolDefinitionV1 protocol) => new(protocol.ContractName, "1", protocol.ContractDigest);
    private static ComponentLifecycleSnapshot AdmitNativeComponent(
        RuntimeKernel kernel,
        ulong processId,
        ulong domainId,
        string serviceName,
        string componentName,
        ProtocolDefinitionV1 protocol,
        ResponseProtocolDefinitionV1 response,
        ResourceKind admissionKind,
        string admissionResource,
        CapabilityRights admissionRights)
    {
        byte[] image = [(byte)(processId & 0xff), 0x53, 0x38];
        var contract = Contract(protocol);
        var provided = new ProvidedServiceManifestV1(serviceName, contract);
        var manifest = new ServiceManifestV1(
            new ComponentIdentity(componentName),
            new ComponentVersion("1"),
            Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(processId, domainId, 1, componentName),
            [provided]);
        var registration = new ComponentProvidedServiceRegistration(
            provided,
            protocol,
            response,
            [new CapabilityRequirementV1(admissionKind, admissionResource, admissionRights)]);
        var admitted = kernel.AdmitComponent(new ComponentAdmissionPlan(manifest, image, providedServices: [registration]));
        Assert.True(admitted.IsSuccess, admitted.Message);
        return admitted.Value!;
    }
    private static async Task Dispatch(ValueTask operation, dynamic host) { var task = operation.AsTask(); Assert.True(host.ProcessNext().IsSuccess); await task.WaitAsync(TimeSpan.FromSeconds(2)); }
    private static string FindRoot() { var directory = new DirectoryInfo(AppContext.BaseDirectory); while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SingNextOS.slnx"))) directory = directory.Parent; return directory!.FullName; }

    private sealed class MalformedTransport : ISipClientRuntimeTransport
    {
        public ResponseEnvelope Invoke(uint messageId, object? requestPayload = null) => throw new NotSupportedException();
        public ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? requestPayload = null) => ValueTask.FromResult(new ResponseEnvelope(1, messageId, ResponsePublicationStatus.Published, "wrong"));
        public ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? requestPayload, CancellationToken cancellationToken) => InvokeAsync(messageId, requestPayload);
        public ResponseEnvelope InvokeOwnershipPair(uint messageId, object firstOwnershipPayload, object secondOwnershipPayload) => throw new NotSupportedException();
        public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(uint messageId, object firstOwnershipPayload, object secondOwnershipPayload) => throw new NotSupportedException();
        public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(uint messageId, object firstOwnershipPayload, object secondOwnershipPayload, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CompletedEventSink : INativeSipCompletedEventSink
    {
        internal List<NativeSipCompletedEvent> Events { get; } = [];
        public void Record(NativeSipCompletedEvent completedEvent) => Events.Add(completedEvent);
    }

    private sealed class PointHook(NativeSipDeterministicPoint target, Action action) : INativeSipDeterministicHook
    {
        internal bool Fired { get; private set; }

        public void At(NativeSipDeterministicPoint point)
        {
            if (Fired || point != target) return;
            Fired = true;
            action();
        }
    }
}
