using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;
using SingPlus.Sip.Sdk;

namespace SingPlus.Tests.SipJobs;

[SipContract, InitialState("Ready")]
public interface ISipJobRegionBorrowQualificationService
{
    [Message(1), Transition("Ready", "Borrowed")]
    ValueTask<SipJobClosedInt> InspectAsync([Borrows] OwnedBuffer<byte> payload);
}

public sealed class Phase143InlineRegionBorrowTests
{
    [Fact]
    public void CloseBetweenPinAndRevalidationFailsClosedAndClaimsDeferredCleanup()
    {
        var scenario = CreateSessionScenario();
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 4).Value!;
        KernelResult close = default;
        scenario.Kernel.InlineBorrowQualificationHook = new PointHook(
            InlineBorrowQualificationPoint.BeforeSessionRevalidation,
            () => close = scenario.Kernel.CloseSession(scenario.Caller, scenario.Session));

        var begun = scenario.Kernel.BeginInlineBorrowSessionInvocation(
            scenario.Caller, scenario.Service, scenario.Session, 1, buffer);

        Assert.Equal(KernelError.SessionDraining, close.Error);
        Assert.Equal(KernelError.SessionClosed, begun.Error);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.Channels.GetEndpoint(scenario.CallerEndpoint).Error);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Empty(scenario.Kernel.Regions.SnapshotUses());
    }

    [Fact]
    public void ServiceTerminationBeforeFinalRevalidationInvalidatesBorrowAndUseWithoutRetry()
    {
        var scenario = CreateSessionScenario();
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 4).Value!;
        var begun = scenario.Kernel.BeginInlineBorrowSessionInvocation(
            scenario.Caller, scenario.Service, scenario.Session, 1, buffer).Value!;
        scenario.Kernel.InlineBorrowQualificationHook = new PointHook(
            InlineBorrowQualificationPoint.BeforeFinalRevalidation,
            () => Assert.True(scenario.Kernel.TerminateProcess(scenario.Service).IsSuccess));

        var current = scenario.Kernel.RevalidateInlineBorrowSessionInvocation(begun);

        Assert.False(current.IsSuccess);
        Assert.False(begun.Borrow.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = begun.Borrow.Span.Length);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal(KernelError.StaleHandle,
            scenario.Kernel.ValidateRegionUse(scenario.Service, begun.Use.Handle).Error);
        Assert.False(scenario.Kernel.SettleInlineBorrowSessionInvocation(
            scenario.Service, begun, succeeded: false).IsSuccess);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
    }

    [Fact]
    public void CancellationAfterAcceptanceCannotReopenAdmissionWindow()
    {
        var scenario = CreateSessionScenario();
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 4).Value!;
        var begun = scenario.Kernel.BeginInlineBorrowSessionInvocation(
            scenario.Caller, scenario.Service, scenario.Session, 1, buffer).Value!;
        KernelResult cancellation = default;
        scenario.Kernel.InlineBorrowQualificationHook = new PointHook(
            InlineBorrowQualificationPoint.BeforeFinalRevalidation,
            () => cancellation = scenario.Kernel.RequestSessionCancellation(
                scenario.Caller, begun.Context.Invocation));

        Assert.True(scenario.Kernel.RevalidateInlineBorrowSessionInvocation(begun).IsSuccess);
        Assert.Equal(KernelError.InvalidTransition, cancellation.Error);
        Assert.True(scenario.Kernel.SettleInlineBorrowSessionInvocation(
            scenario.Service, begun, succeeded: true).IsSuccess);
    }

    [Fact]
    public void CloseBetweenUseReleaseAndBorrowReturnSettlesThenPerformsDeferredCleanup()
    {
        var scenario = CreateSessionScenario();
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 4).Value!;
        var begun = scenario.Kernel.BeginInlineBorrowSessionInvocation(
            scenario.Caller, scenario.Service, scenario.Session, 1, buffer).Value!;
        KernelResult close = default;
        scenario.Kernel.InlineBorrowQualificationHook = new PointHook(
            InlineBorrowQualificationPoint.BeforeBorrowReturn,
            () => close = scenario.Kernel.CloseSession(scenario.Caller, scenario.Session));

        var settled = scenario.Kernel.SettleInlineBorrowSessionInvocation(
            scenario.Service, begun, succeeded: true);

        Assert.Equal(KernelError.SessionDraining, close.Error);
        Assert.True(settled.IsSuccess, settled.Message);
        Assert.False(begun.Borrow.IsValid);
        Assert.Equal(RegionUseState.Released, Assert.Single(scenario.Kernel.Regions.SnapshotUses()).State);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(KernelError.StaleGeneration,
            scenario.Kernel.Channels.GetEndpoint(scenario.CallerEndpoint).Error);
    }

    [Fact]
    public async Task GeneratedSentryContourMatchesOrdinaryBorrowAndKeepsAuthorityInOwners()
    {
        var ordinary = CreateGeneratedSessionScenario();
        var ordinaryBuffer = ordinary.Kernel.AllocateBuffer<byte>(ordinary.Caller, 4).Value!;
        ordinaryBuffer.Span[0] = 2;
        ordinaryBuffer.Span[1] = 3;
        ordinaryBuffer.Span[2] = 5;
        ordinaryBuffer.Span[3] = 7;
        var ordinaryPending = new RuntimeSipClientTransport(
            ordinary.Kernel, ordinary.Caller, ordinary.Session).InvokeAsync(
                ISipJobRegionBorrowQualificationServiceProtocol.Message_InspectAsync,
                ordinaryBuffer).AsTask();
        var processed = ordinary.Host.ProcessNext();
        Assert.True(processed.IsSuccess, processed.Message);
        var ordinaryResponse = await ordinaryPending.WaitAsync(TimeSpan.FromSeconds(10));

        var fused = CreateGeneratedSessionScenario();
        var fusedBuffer = fused.Kernel.AllocateBuffer<byte>(fused.Caller, 4).Value!;
        fusedBuffer.Span[0] = 2;
        fusedBuffer.Span[1] = 3;
        fusedBuffer.Span[2] = 5;
        fusedBuffer.Span[3] = 7;
        var binding = new GeneratedBorrowBinding(
            fused.Kernel, fused.Caller, fused.Service, fused.Session, fused.Host);
        var fusedResult = binding.Invoke(fusedBuffer);

        Assert.True(fusedResult.IsSuccess, fusedResult.Message);
        Assert.Equal(Assert.IsType<SipJobClosedInt>(ordinaryResponse.Payload), fusedResult.Value);
        Assert.Equal(RegionState.Owned, Assert.Single(fused.Kernel.Regions.Snapshot()).State);
        Assert.Equal(RegionUseState.Released, Assert.Single(fused.Kernel.Regions.SnapshotUses()).State);
        Assert.Equal(0, fused.Kernel.EndpointSessions.ActivePinCount(fused.Session));
        Assert.Equal(0, fused.Kernel.Channels.InspectionSummary(fused.Caller).QueuedMessages);
        Assert.DoesNotContain(
            typeof(GeneratedBorrowBinding).GetFields(global::System.Reflection.BindingFlags.Instance |
                global::System.Reflection.BindingFlags.NonPublic),
            field => field.FieldType == typeof(OwnedBuffer<byte>) ||
                     field.FieldType == typeof(BorrowLease<byte>) ||
                     typeof(Delegate).IsAssignableFrom(field.FieldType) ||
                     field.FieldType == typeof(object));
    }

    [Fact]
    public void SessionCompositePinsRevalidatesAndSettlesExistingOwnersInOrder()
    {
        var scenario = CreateSessionScenario();
        var buffer = scenario.Kernel.AllocateBuffer<int>(scenario.Caller, 2).Value!;
        buffer.Span[0] = 31;
        buffer.Span[1] = 37;

        var begun = scenario.Kernel.BeginInlineBorrowSessionInvocation(
            scenario.Caller, scenario.Service, scenario.Session, 1, buffer);

        Assert.True(begun.IsSuccess, begun.Message);
        Assert.Equal(1, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.True(scenario.Kernel.RevalidateInlineBorrowSessionInvocation(begun.Value!).IsSuccess);
        Assert.Equal(new[] { 31, 37 }, begun.Value!.Borrow.Span.ToArray());
        Assert.True(scenario.Kernel.SettleInlineBorrowSessionInvocation(
            scenario.Service, begun.Value, succeeded: true).IsSuccess);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.False(begun.Value.Borrow.IsValid);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal(RegionUseState.Released, Assert.Single(scenario.Kernel.Regions.SnapshotUses()).State);
    }

    [Fact]
    public void SessionCloseAfterBeginDefersCleanupUntilBorrowSettlement()
    {
        var scenario = CreateSessionScenario();
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 8).Value!;
        var begun = scenario.Kernel.BeginInlineBorrowSessionInvocation(
            scenario.Caller, scenario.Service, scenario.Session, 1, buffer).Value!;

        var close = scenario.Kernel.CloseSession(scenario.Caller, scenario.Session);
        Assert.Equal(KernelError.SessionDraining, close.Error);
        Assert.Equal(KernelError.SessionClosed,
            scenario.Kernel.RevalidateInlineBorrowSessionInvocation(begun).Error);
        Assert.True(begun.Borrow.IsValid);

        Assert.True(scenario.Kernel.SettleInlineBorrowSessionInvocation(
            scenario.Service, begun, succeeded: false).IsSuccess);
        Assert.False(begun.Borrow.IsValid);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.False(scenario.Kernel.Channels.GetEndpoint(scenario.CallerEndpoint).IsSuccess);
    }

    [Fact]
    public void WrongSettlementPrincipalCannotConsumeCompositeLease()
    {
        var scenario = CreateSessionScenario();
        var stranger = TestFixtures.Create(scenario.Kernel, 8199, 8199).Handle;
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 4).Value!;
        var begun = scenario.Kernel.BeginInlineBorrowSessionInvocation(
            scenario.Caller, scenario.Service, scenario.Session, 1, buffer).Value!;

        Assert.Equal(KernelError.WrongSessionOwner,
            scenario.Kernel.SettleInlineBorrowSessionInvocation(stranger, begun, succeeded: true).Error);
        Assert.True(scenario.Kernel.RevalidateInlineBorrowSessionInvocation(begun).IsSuccess);
        Assert.True(scenario.Kernel.SettleInlineBorrowSessionInvocation(
            scenario.Service, begun, succeeded: false).IsSuccess);
    }

    [Fact]
    public void StaleServiceDuringUseReleaseStillRunsReverseCleanupAndTerminatesLease()
    {
        var scenario = CreateSessionScenario();
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Caller, 4).Value!;
        var begun = scenario.Kernel.BeginInlineBorrowSessionInvocation(
            scenario.Caller, scenario.Service, scenario.Session, 1, buffer).Value!;
        var points = new List<InlineBorrowQualificationPoint>();
        scenario.Kernel.InlineBorrowQualificationHook = new RecordingHook(point =>
        {
            points.Add(point);
            if (point == InlineBorrowQualificationPoint.BeforeRegionUseRelease)
                Assert.True(scenario.Kernel.TerminateProcess(scenario.Service).IsSuccess);
        });

        var settled = scenario.Kernel.SettleInlineBorrowSessionInvocation(
            scenario.Service, begun, succeeded: true);

        Assert.False(settled.IsSuccess);
        Assert.Contains(InlineBorrowQualificationPoint.AfterRegionUseRelease, points);
        Assert.Contains(InlineBorrowQualificationPoint.BeforeBorrowReturn, points);
        Assert.Contains(InlineBorrowQualificationPoint.AfterBorrowReturn, points);
        Assert.Contains(InlineBorrowQualificationPoint.BeforeInvocationSettlement, points);
        Assert.Contains(InlineBorrowQualificationPoint.AfterInvocationSettlement, points);
        Assert.False(begun.Borrow.IsValid);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Throws<ObjectDisposedException>(() =>
            scenario.Kernel.SettleInlineBorrowSessionInvocation(
                scenario.Service, begun, succeeded: false));
    }

    [Fact]
    public void InlineBorrowMatchesOrdinaryOwnerStateWithoutQueueMaterialization()
    {
        var scenario = Create();
        var ordinary = Create();
        var buffer = scenario.Kernel.AllocateBuffer<int>(scenario.Owner, 2).Value!;
        var ordinaryBuffer = ordinary.Kernel.AllocateBuffer<int>(ordinary.Owner, 2).Value!;
        buffer.Span[0] = 17;
        buffer.Span[1] = 23;
        ordinaryBuffer.Span[0] = 17;
        ordinaryBuffer.Span[1] = 23;

        var ordinaryEnvelope = ordinary.Kernel.Channels.Send(
            ordinary.OwnerProcess, ordinary.BorrowerProcess, ordinary.Endpoints.Left, 1, ordinaryBuffer, null).Value!;
        var ordinaryLease = Assert.IsType<BorrowLease<int>>(ordinaryEnvelope.Payload);

        var begun = scenario.Kernel.Channels.BeginInlineBorrowInvocation(
            scenario.OwnerProcess, scenario.BorrowerProcess, scenario.Endpoints.Left, 1, buffer);

        Assert.True(begun.IsSuccess, begun.Message);
        Assert.Equal<ulong>(1, begun.Value.Sequence);
        Assert.Equal(new[] { 17, 23 }, begun.Value.Lease.Span.ToArray());
        Assert.Equal(RegionState.Loaned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        var inlineRegion = Assert.Single(scenario.Kernel.Regions.Snapshot());
        var ordinaryRegion = Assert.Single(ordinary.Kernel.Regions.Snapshot());
        Assert.Equal(ordinaryRegion.State, inlineRegion.State);
        Assert.Equal(ordinaryRegion.MutationEpoch, inlineRegion.MutationEpoch);
        Assert.Equal(ordinaryLease.Span.ToArray(), begun.Value.Lease.Span.ToArray());
        Assert.Equal(0, scenario.Kernel.Channels.InspectionSummary(scenario.Owner).QueuedMessages);
        Assert.Equal(1, ordinary.Kernel.Channels.InspectionSummary(ordinary.Owner).QueuedMessages);
        var endpoint = scenario.Kernel.Channels.GetEndpoint(scenario.Endpoints.Right).Value!;
        var ordinaryEndpoint = ordinary.Kernel.Channels.GetEndpoint(ordinary.Endpoints.Right).Value!;
        Assert.Equal<ulong>(1, endpoint.Sequence);
        Assert.Equal("Borrowed", endpoint.ProtocolState);
        Assert.Equal(ordinaryEndpoint.Sequence, endpoint.Sequence);
        Assert.Equal(ordinaryEndpoint.ProtocolState, endpoint.ProtocolState);
        Assert.Throws<InvalidOperationException>(() => buffer.Span[0] = 99);

        var use = scenario.Kernel.AcquireBorrowRegionUse(
            scenario.Owner, scenario.Borrower, begun.Value.Lease.Handle, RegionUseMode.ReadOnly, new(0, 8));
        Assert.True(use.IsSuccess, use.Message);
        Assert.True(scenario.Kernel.ReleaseRegionUse(scenario.Borrower, use.Value!.Handle).IsSuccess);
        Assert.True(scenario.Kernel.ReturnBorrow(scenario.Borrower, begun.Value.Lease.Handle).IsSuccess);
        Assert.False(begun.Value.Lease.IsValid);
        Assert.Throws<InvalidOperationException>(() => _ = begun.Value.Lease.Span.Length);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        buffer.Span[0] = 99;
        Assert.True(ordinary.Kernel.ReturnBorrow(ordinary.Borrower, ordinaryLease.Handle).IsSuccess);
    }

    [Fact]
    public void BorrowerTerminationInvalidatesLeaseAndUseWithoutReturningAuthorityToJobState()
    {
        var scenario = Create();
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 8).Value!;
        var begun = scenario.Kernel.Channels.BeginInlineBorrowInvocation(
            scenario.OwnerProcess, scenario.BorrowerProcess, scenario.Endpoints.Left, 1, buffer).Value;
        var use = scenario.Kernel.AcquireBorrowRegionUse(
            scenario.Owner, scenario.Borrower, begun.Lease.Handle, RegionUseMode.ReadOnly, new(0, 8)).Value!;

        Assert.True(scenario.Kernel.TerminateProcess(scenario.Borrower).IsSuccess);

        Assert.False(begun.Lease.IsValid);
        Assert.Equal(KernelError.StaleHandle, scenario.Kernel.ValidateRegionUse(scenario.Borrower, use.Handle).Error);
        Assert.Equal(RegionState.Owned, Assert.Single(scenario.Kernel.Regions.Snapshot()).State);
        Assert.True(buffer.IsValid);
    }

    [Fact]
    public void UnsupportedOrCapabilityBearingMessageFailsBeforeLoanOrProtocolCommit()
    {
        var copied = Create(RequestPayloadKind.Bounded);
        var copiedBuffer = copied.Kernel.AllocateBuffer<byte>(copied.Owner, 4).Value!;
        var wrongSemantic = copied.Kernel.Channels.BeginInlineBorrowInvocation(
            copied.OwnerProcess, copied.BorrowerProcess, copied.Endpoints.Left, 1, copiedBuffer);
        Assert.Equal(KernelError.UnsupportedPayload, wrongSemantic.Error);
        Assert.Equal(RegionState.Owned, Assert.Single(copied.Kernel.Regions.Snapshot()).State);
        Assert.Equal<ulong>(0, copied.Kernel.Channels.GetEndpoint(copied.Endpoints.Left).Value!.Sequence);

        var protectedScenario = Create(requireCapability: true);
        var protectedBuffer = protectedScenario.Kernel.AllocateBuffer<byte>(protectedScenario.Owner, 4).Value!;
        var missing = protectedScenario.Kernel.Channels.BeginInlineBorrowInvocation(
            protectedScenario.OwnerProcess, protectedScenario.BorrowerProcess, protectedScenario.Endpoints.Left, 1, protectedBuffer);
        Assert.Equal(KernelError.MissingCapability, missing.Error);
        Assert.Equal(RegionState.Owned, Assert.Single(protectedScenario.Kernel.Regions.Snapshot()).State);
        Assert.Equal<ulong>(0, protectedScenario.Kernel.Channels.GetEndpoint(protectedScenario.Endpoints.Left).Value!.Sequence);
    }

    [Fact]
    public void StaleOwnerAndSecondBorrowFailWithoutGuessingGeneration()
    {
        var scenario = Create();
        var buffer = scenario.Kernel.AllocateBuffer<byte>(scenario.Owner, 4).Value!;
        var first = scenario.Kernel.Channels.BeginInlineBorrowInvocation(
            scenario.OwnerProcess, scenario.BorrowerProcess, scenario.Endpoints.Left, 1, buffer);
        Assert.True(first.IsSuccess, first.Message);

        var second = scenario.Kernel.Channels.BeginInlineBorrowInvocation(
            scenario.OwnerProcess, scenario.BorrowerProcess, scenario.Endpoints.Left, 1, buffer);

        Assert.False(second.IsSuccess);
        Assert.Equal<ulong>(1, scenario.Kernel.Channels.GetEndpoint(scenario.Endpoints.Left).Value!.Sequence);
        Assert.True(scenario.Kernel.ReturnBorrow(scenario.Borrower, first.Value.Lease.Handle).IsSuccess);
    }

    private static Scenario Create(RequestPayloadKind kind = RequestPayloadKind.Ownership, bool requireCapability = false)
    {
        var kernel = new RuntimeKernel();
        var owner = TestFixtures.Create(kernel, 8101, 8111).Handle;
        var borrower = TestFixtures.Create(kernel, 8102, 8112).Handle;
        var payload = kind == RequestPayloadKind.Ownership
            ? new RequestPayloadDescriptorV1(kind, "payload", typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer",
                ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer)
            : new RequestPayloadDescriptorV1(kind, "payload", typeof(int).FullName, 8);
        var capabilities = requireCapability
            ? new[] { new CapabilityRequirementV1(ResourceKind.Device, "borrow:test", CapabilityRights.Read) }
            : null;
        var message = new ProtocolMessageDescriptorV1(1, "Borrow", capabilities, null,
            kind == RequestPayloadKind.Ownership ? ["payload"] : null, requestPayload: payload);
        var protocol = new ProtocolDefinitionV1("SipJobBorrowQualification", "1", "Ready", null,
            [message], [new ProtocolTransitionV1(1, "Ready", "Borrowed")]);
        var endpoints = kernel.CreateChannel(owner, borrower, protocol, 4).Value;
        return new(kernel, owner, borrower, kernel.Processes.Resolve(owner).Value!,
            kernel.Processes.Resolve(borrower).Value!, endpoints);
    }

    private static SessionScenario CreateSessionScenario()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 8121, 812_100).Handle;
        var service = TestFixtures.Create(kernel, 8122, 812_200).Handle;
        var payload = new RequestPayloadDescriptorV1(
            RequestPayloadKind.Ownership,
            "payload",
            typeof(OwnedBuffer<>).Namespace + ".OwnedBuffer",
            ownershipPayloadKind: OwnershipPayloadKind.OwnedBuffer);
        var message = new ProtocolMessageDescriptorV1(
            1, "Borrow", borrows: ["payload"], requestPayload: payload);
        var protocol = new ProtocolDefinitionV1(
            "SipJobBorrowSessionQualification", "1", "Ready", ["Borrowed"],
            [message], [new ProtocolTransitionV1(1, "Ready", "Borrowed")]);
        var descriptor = kernel.RegisterService(
            service, "p14-region-borrow", new(protocol.ContractName, "1", protocol.ContractDigest), protocol).Value!;
        var session = kernel.OpenSession(caller, descriptor).Value;
        var endpoint = kernel.EndpointSessions.Resolve(session, caller).Value!.Channel;
        return new(kernel, caller, service, session, endpoint);
    }

    private static GeneratedSessionScenario CreateGeneratedSessionScenario()
    {
        var kernel = new RuntimeKernel();
        var caller = TestFixtures.Create(kernel, 8131, 813_100).Handle;
        var service = TestFixtures.Create(kernel, 8132, 813_200).Handle;
        var protocol = ISipJobRegionBorrowQualificationServiceProtocol.CreateDefinition();
        var descriptor = kernel.RegisterService(
            service,
            "p14-generated-region-borrow",
            new(protocol.ContractName, "1", protocol.ContractDigest),
            protocol,
            ISipJobRegionBorrowQualificationServiceResponseProtocol.Definition).Value!;
        var session = kernel.OpenSession(caller, descriptor).Value;
        return new(kernel, caller, service, session, new GeneratedBorrowHost(kernel, service, session));
    }

    // TCB-private operation-specific binding. The target reference is never placed in
    // a plan, frame, descriptor, cache projection, or ManagedCap-visible result.
    private sealed class GeneratedBorrowBinding(
        RuntimeKernel kernel,
        ProcessHandle caller,
        ProcessHandle service,
        EndpointSessionHandle session,
        IISipJobRegionBorrowQualificationServiceGeneratedSentryTarget_InspectAsync target)
    {
        internal KernelResult<SipJobClosedInt> Invoke(OwnedBuffer<byte> payload)
        {
            var begun = kernel.BeginInlineBorrowSessionInvocation(
                caller,
                service,
                session,
                ISipJobRegionBorrowQualificationServiceProtocol.Message_InspectAsync,
                payload);
            if (!begun.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(begun.Error, begun.Message!);
            var lease = begun.Value!;
            var current = kernel.RevalidateInlineBorrowSessionInvocation(lease);
            if (!current.IsSuccess)
            {
                _ = kernel.SettleInlineBorrowSessionInvocation(service, lease, succeeded: false);
                return KernelResult<SipJobClosedInt>.Fail(current.Error, current.Message!);
            }

            var context = lease.Context;
            var generated = ISipJobRegionBorrowQualificationServiceGeneratedOperationSentries.InvokeRuntime_InspectAsync(
                target, in context, lease.Borrow);
            var settled = kernel.SettleInlineBorrowSessionInvocation(service, lease, generated.IsSuccess);
            if (!settled.IsSuccess)
                return KernelResult<SipJobClosedInt>.Fail(settled.Error, settled.Message!);
            return generated.IsSuccess
                ? KernelResult<SipJobClosedInt>.Ok(generated.Value)
                : KernelResult<SipJobClosedInt>.Fail((KernelError)generated.ErrorCode, generated.Message!);
        }
    }

    private sealed class GeneratedBorrowHost(
        RuntimeKernel kernel,
        ProcessHandle service,
        EndpointSessionHandle session) :
        IISipJobRegionBorrowQualificationServiceGeneratedSentryTarget_InspectAsync
    {
        internal KernelResult ProcessNext() => NativeServiceDispatch.Dispatch(
            kernel,
            service,
            session,
            (context, envelope) =>
            {
                if (envelope.MessageId != ISipJobRegionBorrowQualificationServiceProtocol.Message_InspectAsync ||
                    envelope.Payload is not BorrowLease<byte> borrow)
                    return KernelResult<object?>.Fail(KernelError.UnsupportedPayload, "BORROW request projection is invalid.");
                var use = kernel.AcquireBorrowRegionUse(
                    context.Caller,
                    service,
                    borrow.Handle,
                    RegionUseMode.ReadOnly,
                    new RegionUseRange(0, borrow.Length));
                if (!use.IsSuccess)
                    return KernelResult<object?>.Fail(use.Error, use.Message!);
                try
                {
                    var generated = ISipJobRegionBorrowQualificationServiceGeneratedOperationSentries.InvokeRuntime_InspectAsync(
                        this, in context, borrow);
                    return generated.IsSuccess
                        ? KernelResult<object?>.Ok(generated.Value)
                        : KernelResult<object?>.Fail((KernelError)generated.ErrorCode, generated.Message!);
                }
                finally
                {
                    _ = kernel.ReleaseRegionUse(service, use.Value!.Handle);
                    _ = kernel.ReturnBorrow(service, borrow.Handle);
                }
            });

        GeneratedSipSentryResult<SipJobClosedInt>
            IISipJobRegionBorrowQualificationServiceGeneratedSentryTarget_InspectAsync.Sentry_InspectAsync(
                in TrustedSipInvocationContext context,
                BorrowLease<byte> payload) =>
            GeneratedSipSentryResult<SipJobClosedInt>.Success(
                new SipJobClosedInt(payload.Span.ToArray().Sum(static value => value)));
    }

    private sealed record Scenario(
        RuntimeKernel Kernel,
        ProcessHandle Owner,
        ProcessHandle Borrower,
        SingProcess OwnerProcess,
        SingProcess BorrowerProcess,
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Endpoints);

    private sealed record SessionScenario(
        RuntimeKernel Kernel,
        ProcessHandle Caller,
        ProcessHandle Service,
        EndpointSessionHandle Session,
        ChannelEndpointHandle CallerEndpoint);

    private sealed record GeneratedSessionScenario(
        RuntimeKernel Kernel,
        ProcessHandle Caller,
        ProcessHandle Service,
        EndpointSessionHandle Session,
        GeneratedBorrowHost Host);

    private sealed class PointHook(
        InlineBorrowQualificationPoint selected,
        Action action) : IInlineBorrowQualificationHook
    {
        private int _fired;

        public void At(InlineBorrowQualificationPoint point)
        {
            if (point == selected && Interlocked.Exchange(ref _fired, 1) == 0)
                action();
        }
    }

    private sealed class RecordingHook(Action<InlineBorrowQualificationPoint> action) : IInlineBorrowQualificationHook
    {
        public void At(InlineBorrowQualificationPoint point) => action(point);
    }
}
