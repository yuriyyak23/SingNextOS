using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Tests.Capabilities;

public sealed class SingCapPhase04EffectAdmissionTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void CopiedPermissionOwnerCannotConsumeOriginalOneShotOrQuota(int boundary)
    {
        OperationClock clock = new OperationClock();
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) s = SessionScenario(CapabilityRights.Execute, 1, clock);
        IDictionary records = (global::System.Collections.IDictionary)typeof(CapabilityAuthority)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.CapabilityAuthority)!;
        object original = records[s.Capability]!;
        void Replace() => records[s.Capability] = original.GetType()
            .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
        if (boundary == 3)
        {
            CapabilityId other = s.Kernel.CapabilityAuthority.Mint(new(20), new(10), ResourceKind.File,
                "file:namespace", CapabilityRights.Execute, s.Caller.Generation, 1, null, null, 1, 16).Value!.CapabilityId;
            records[s.Capability] = records[other];
        }
        else if (boundary == 0) Replace();
        else if (boundary == 1) clock.Next = Replace;
        else s.Kernel.EffectAdmissionQualificationHook = new AdmissionHook(point =>
        {
            if (point == EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation) Replace();
        });
        KernelResult<EffectAdmissionLease> admitted = s.Kernel.AdmitSessionCapabilityEffect(s.Caller, s.Service, s.Session, s.Capability,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Execute, quotaAmount: 1, oneShot: true);
        Assert.False(admitted.IsSuccess);
        Assert.Equal(0, s.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, s.Kernel.EndpointSessions.ActivePinCount(s.Session));
        records[s.Capability] = original;
        Assert.Equal(1UL, s.Kernel.CapabilityAuthority.InspectConstraints(s.Capability)!.Value.SharedRemaining);
        Assert.Equal(0UL, s.Kernel.CapabilityAuthority.InspectConstraints(s.Capability)!.Value.HandleConsumed);
        Assert.True(s.Kernel.CapabilityAuthority.Validate(s.Capability, new(10), s.Caller.Generation, CapabilityRights.Execute).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DomainRevocationRetainsOriginalFactAndCompletedLeaseReleaseAfterRouteLoss(bool absent)
    {
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) s = SessionScenario(CapabilityRights.Execute, 1);
        EffectAdmissionLease admitted = s.Kernel.AdmitSessionCapabilityEffect(s.Caller, s.Service, s.Session, s.Capability,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Execute).Value!;
        IDictionary records = (global::System.Collections.IDictionary)typeof(CapabilityAuthority)
            .GetField("_records", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(s.Kernel.CapabilityAuthority)!;
        object original = records[s.Capability]!;
        object copy = original.GetType().GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(original, null)!;
        if (absent) records.Remove(s.Capability); else records[s.Capability] = copy;
        s.Kernel.CapabilityAuthority.RevokeAllForDomain(new(10));
        Assert.Equal(CapabilityRecordState.Revoked, original.GetType().GetProperty("State")!.GetValue(original));
        Assert.Equal(CapabilityRecordState.Active, copy.GetType().GetProperty("State")!.GetValue(copy));
        admitted.Dispose();
        Assert.Equal(0, s.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, s.Kernel.EndpointSessions.ActivePinCount(s.Session));
        records[s.Capability] = original;
        Assert.Equal(KernelError.CapabilityRevoked, s.Kernel.CapabilityAuthority.Validate(s.Capability,
            new(10), s.Caller.Generation, CapabilityRights.Execute).Error);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void FinalCommitRejectsProcessStopAfterPreparedSession(bool caller, bool fault)
    {
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) s = SessionScenario(CapabilityRights.Execute, 1);
        ProcessHandle target = caller ? s.Caller : s.Service;
        s.Kernel.RegisterComposedWorkTeardownParticipant(new RefusingParticipant());
        s.Kernel.EffectAdmissionQualificationHook = new AdmissionHook(point =>
        {
            if (point != EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation) return;
            KernelResult stopped = fault ? s.Kernel.FaultProcess(target) : s.Kernel.TerminateProcess(target);
            Assert.False(stopped.IsSuccess);
            Assert.False(s.Kernel.ObserveProcessTeardown(target).Value!.LocalReclaimCompleted);
        });
        KernelResult<EffectAdmissionLease> admitted = s.Kernel.AdmitSessionCapabilityEffect(s.Caller, s.Service, s.Session, s.Capability,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Execute, quotaAmount: 1, oneShot: true);
        Assert.Equal(KernelError.InvalidTransition, admitted.Error);
        Assert.Equal(1UL, s.Kernel.CapabilityAuthority.InspectConstraints(s.Capability)!.Value.SharedRemaining);
        Assert.Equal(0UL, s.Kernel.CapabilityAuthority.InspectConstraints(s.Capability)!.Value.HandleConsumed);
        Assert.Equal(0, s.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, s.Kernel.EndpointSessions.ActivePinCount(s.Session));
    }

    [Fact]
    public void FinalCommitRejectsCloseAfterPreparedSessionWithoutConsumption()
    {
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) s = SessionScenario(CapabilityRights.Execute, 1);
        s.Kernel.EffectAdmissionQualificationHook = new AdmissionHook(point =>
        {
            if (point == EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation)
                Assert.Equal(KernelError.SessionDraining, s.Kernel.EndpointSessions.Close(s.Session, s.Caller).Error);
        });
        KernelResult<EffectAdmissionLease> admitted = s.Kernel.AdmitSessionCapabilityEffect(s.Caller, s.Service, s.Session, s.Capability,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Execute, quotaAmount: 1, oneShot: true);
        Assert.Equal(KernelError.SessionClosed, admitted.Error);
        SingProcess process = s.Kernel.Processes.Resolve(s.Caller).Value!;
        Assert.True(s.Kernel.CapabilityAuthority.Validate(s.Capability, process.DomainId,
            s.Caller.Generation, CapabilityRights.Execute).IsSuccess);
        Assert.Equal(1UL, s.Kernel.CapabilityAuthority.InspectConstraints(s.Capability)!.Value.SharedRemaining);
        Assert.Equal(0, s.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, s.Kernel.EndpointSessions.ActivePinCount(s.Session));
    }

    [Fact]
    public void DisposedPinCannotCommitUsingAnotherLivePin()
    {
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) s = SessionScenario();
        EndpointSessionPin disposed = s.Kernel.EndpointSessions.AcquirePin(s.Session, s.Caller, s.Service).Value!;
        using EndpointSessionPin live = s.Kernel.EndpointSessions.AcquirePin(s.Session, s.Caller, s.Service).Value!;
        disposed.Dispose();
        bool called = false;
        KernelResult<int> result = s.Kernel.EndpointSessions.CommitPinnedEffectAdmission(disposed, () =>
        {
            called = true;
            return KernelResult<int>.Ok(1);
        });
        Assert.Equal(KernelError.SessionClosed, result.Error);
        Assert.False(called);
        Assert.Equal(1, s.Kernel.EndpointSessions.ActivePinCount(s.Session));
    }

    private sealed class RefusingParticipant : IComposedWorkTeardownParticipant
    {
        public KernelResult CloseComposedWorkForProcess(ProcessHandle process, RegionOwner owner) =>
            KernelResult.Fail(KernelError.ExternalEffectUncontained, "Exact provider closure remains unavailable.");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactProviderGenerationRejectsReplacementBeforeSourceConsumption(bool afterPreparation)
    {
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) s = SessionScenario(CapabilityRights.Execute, 1);
        void Replace()
        {
            Assert.True(s.Kernel.TerminateProcess(s.Service).IsSuccess);
            (SingProcess _, ProcessHandle replacement) = TestFixtures.Create(s.Kernel, s.Service.ProcessId.Value, 20,
                s.Service.Generation + 1);
            Assert.True(s.Kernel.Processes.Resolve(replacement).IsSuccess);
        }
        if (afterPreparation)
            s.Kernel.EffectAdmissionQualificationHook = new AdmissionHook(point =>
            {
                if (point == EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation) Replace();
            });
        else Replace();
        KernelResult<EffectAdmissionLease> admitted = s.Kernel.AdmitSessionCapabilityEffect(s.Caller, s.Service, s.Session, s.Capability,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Execute, quotaAmount: 1, oneShot: true);
        Assert.Equal(KernelError.StaleHandle, admitted.Error);
        Assert.Equal(1UL, s.Kernel.CapabilityAuthority.InspectConstraints(s.Capability)!.Value.SharedRemaining);
        Assert.Equal(0, s.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, s.Kernel.EndpointSessions.ActivePinCount(s.Session));
    }

    [Fact]
    public async Task SourceCommitFreezesProviderStopUntilItsLocalLinearization()
    {
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) s = SessionScenario(CapabilityRights.Execute, 1);
        object capGate = typeof(CapabilityAuthority).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(s.Kernel.CapabilityAuthority)!;
        object sessionGate = typeof(EndpointSessionRegistry).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(s.Kernel.EndpointSessions)!;
        using ManualResetEventSlim locked = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        Task? holder = null;
        s.Kernel.EffectAdmissionQualificationHook = new AdmissionHook(point =>
        {
            if (point != EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation) return;
            holder = Task.Run(() => { lock (capGate) { locked.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } });
            Assert.True(locked.Wait(TimeSpan.FromSeconds(10)));
        });
        Task<KernelResult<EffectAdmissionLease>> admission = Task.Run(() => s.Kernel.AdmitSessionCapabilityEffect(s.Caller, s.Service, s.Session,
            s.Capability, ResourceKind.File, "file:namespace", 1, CapabilityOperation.Execute, quotaAmount: 1, oneShot: true));
        Task<KernelResult>? stopping = null;
        try
        {
            Assert.True(locked.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (!Monitor.TryEnter(sessionGate)) return true;
                Monitor.Exit(sessionGate);
                return false;
            }, TimeSpan.FromSeconds(10)));
            TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stopping = Task.Run(() => { started.SetResult(); return s.Kernel.TerminateProcess(s.Service); });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(stopping.IsCompleted);
            Assert.False(admission.IsCompleted);
        }
        finally { release.Set(); }
        KernelResult<EffectAdmissionLease> admitted = await admission.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(admitted.IsSuccess, admitted.Message);
        admitted.Value!.Dispose();
        await holder!.WaitAsync(TimeSpan.FromSeconds(10));
        await stopping!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0UL, s.Kernel.CapabilityAuthority.InspectConstraints(s.Capability)!.Value.SharedRemaining);
        Assert.Equal(0, s.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, s.Kernel.EndpointSessions.ActivePinCount(s.Session));
    }

    [Fact]
    public void QualificationHookFollowsActualFinalSessionRevalidationAndClockObservation()
    {
        OperationClock clock = new OperationClock();
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) s = SessionScenario(CapabilityRights.Execute, 1, clock);
        s.Kernel.EffectAdmissionQualificationHook = new AdmissionHook(point =>
        {
            if (point == EffectAdmissionQualificationPoint.AfterFinalSessionRevalidation)
                clock.Next = () => throw new InvalidOperationException("hook-injected clock fault");
        });
        KernelResult<EffectAdmissionLease> admitted = s.Kernel.AdmitSessionCapabilityEffect(s.Caller, s.Service, s.Session, s.Capability,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Execute, quotaAmount: 1, oneShot: true);
        Assert.True(admitted.IsSuccess, admitted.Message);
        Assert.NotNull(clock.Next);
        clock.Next = null;
        admitted.Value!.Dispose();
        Assert.Equal(0, s.Kernel.EndpointSessions.ActivePinCount(s.Session));
        Assert.Equal(0, s.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0UL, s.Kernel.CapabilityAuthority.InspectConstraints(s.Capability)!.Value.SharedRemaining);
    }

    private sealed class AdmissionHook(Action<EffectAdmissionQualificationPoint> action) : IEffectAdmissionQualificationHook
    {
        public void At(EffectAdmissionQualificationPoint point) => action(point);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task BlockedOperationClockDoesNotHoldCapabilityGateAndRevokeDeniesAdmission(int path)
    {
        OperationClock clock = new OperationClock();
        CapabilityAuthority authority = new RuntimeKernel(null, clock).CapabilityAuthority;
        CapabilityId cap = authority.Mint(new(1), new(10), ResourceKind.File, "file:clock", CapabilityRights.Execute,
            1, 1, null, null, 1, 1).Value!.CapabilityId;
        CapabilityHandleV2 handle = authority.GetHandleV2(cap, new(10), 1).Value;
        using ManualResetEventSlim entered = new ManualResetEventSlim(false);
        using ManualResetEventSlim release = new ManualResetEventSlim(false);
        clock.Next = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        Task<KernelError> admission = Task.Run(() => path == 0
            ? authority.ValidateOperationAdmission(cap, new(10), 1, ResourceKind.File, "file:clock", 1, CapabilityOperation.Execute).Error
            : path == 1
                ? authority.AcquireOperationAuthority(cap, new(10), 1, ResourceKind.File, "file:clock", 1,
                    CapabilityOperation.Execute, quotaAmount: 1, oneShot: true).Error
                : authority.AcquireOperationAuthority(handle, new(10), 1, ResourceKind.File, "file:clock", 1,
                    CapabilityOperation.Execute, quotaAmount: 1, oneShot: true).Error);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True((await Task.Run(() => authority.Revoke(cap)).WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.CapabilityRevoked, await admission.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, authority.ActiveOperationLeaseCount);
        Assert.Equal(1UL, authority.InspectConstraints(cap)!.Value.SharedRemaining);
        Assert.Equal(0UL, authority.InspectConstraints(cap)!.Value.HandleConsumed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void OperationClockFailureCannotConsumeQuotaOrOneShot(int path)
    {
        OperationClock clock = new OperationClock();
        CapabilityAuthority authority = new RuntimeKernel(null, clock).CapabilityAuthority;
        CapabilityId cap = authority.Mint(new(1), new(10), ResourceKind.File, "file:clock", CapabilityRights.Execute,
            1, 1, null, null, 1, 1).Value!.CapabilityId;
        CapabilityHandleV2 handle = authority.GetHandleV2(cap, new(10), 1).Value;
        clock.Next = () => throw new InvalidOperationException("injected clock");
        KernelError error = path == 0
            ? authority.ValidateOperationAdmission(cap, new(10), 1, ResourceKind.File, "file:clock", 1, CapabilityOperation.Execute).Error
            : path == 1
                ? authority.AcquireOperationAuthority(cap, new(10), 1, ResourceKind.File, "file:clock", 1,
                    CapabilityOperation.Execute, quotaAmount: 1, oneShot: true).Error
                : authority.AcquireOperationAuthority(handle, new(10), 1, ResourceKind.File, "file:clock", 1,
                    CapabilityOperation.Execute, quotaAmount: 1, oneShot: true).Error;
        Assert.Equal(KernelError.PlatformFaulted, error);
        Assert.Equal(0, authority.ActiveOperationLeaseCount);
        Assert.Equal(1UL, authority.InspectConstraints(cap)!.Value.SharedRemaining);
        Assert.Equal(0UL, authority.InspectConstraints(cap)!.Value.HandleConsumed);
        using OperationAuthorityLease recovered = authority.AcquireOperationAuthority(cap, new(10), 1, ResourceKind.File, "file:clock", 1,
            CapabilityOperation.Execute, quotaAmount: 1, oneShot: true).Value!;
        Assert.NotNull(recovered);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ClockSessionCloseFaultOrRevokeRefusesBeforeConsumptiveCommitAndBalancesPins(int fault)
    {
        OperationClock clock = new OperationClock();
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) s = SessionScenario(CapabilityRights.Execute, 1, clock);
        clock.Next = () =>
        {
            if (fault == 0) Assert.Equal(KernelError.SessionDraining, s.Kernel.EndpointSessions.Close(s.Session, s.Caller).Error);
            else if (fault == 1) throw new InvalidOperationException("injected clock");
            else Assert.True(s.Kernel.CapabilityAuthority.Revoke(s.Capability).IsSuccess);
        };
        KernelResult<EffectAdmissionLease> refused = s.Kernel.AdmitSessionCapabilityEffect(s.Caller, s.Service, s.Session, s.Capability,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Execute, quotaAmount: 1, oneShot: true);
        Assert.Equal(fault == 0 ? KernelError.SessionClosed : fault == 1 ? KernelError.PlatformFaulted : KernelError.CapabilityRevoked,
            refused.Error);
        Assert.Equal(0, s.Kernel.EndpointSessions.ActivePinCount(s.Session));
        Assert.Equal(0, s.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(1UL, s.Kernel.CapabilityAuthority.InspectConstraints(s.Capability)!.Value.SharedRemaining);
        Assert.Equal(0UL, s.Kernel.CapabilityAuthority.InspectConstraints(s.Capability)!.Value.HandleConsumed);
    }

    private sealed class OperationClock : TimeProvider
    {
        public Action? Next { get; set; }
        public override DateTimeOffset GetUtcNow() { Action? next = Next; Next = null; next?.Invoke(); return DateTimeOffset.UtcNow; }
    }

    [Fact]
    public void RevokedAncestorInvalidatesDeepDescendantAndSnapshots()
    {
        CapabilityAuthority authority = Authority();
        CapabilityDescriptorV1 root = Mint(authority, new(10), CapabilityRights.Read | CapabilityRights.Delegate, depth: 4);
        CapabilityDescriptorV1 child = authority.Delegate(root.CapabilityId, new(10), new(20),
            CapabilityRights.Read | CapabilityRights.Delegate, 1).Value!;
        CapabilityDescriptorV1 grandchild = authority.Delegate(child.CapabilityId, new(20), new(30), CapabilityRights.Read, 1).Value!;

        Assert.True(authority.Revoke(root.CapabilityId).IsSuccess);

        Assert.Equal(KernelError.CapabilityRevoked,
            authority.Validate(grandchild.CapabilityId, new(30), 1, CapabilityRights.Read).Error);
        Assert.DoesNotContain(authority.SnapshotForDomain(new(30)), item => item.CapabilityId == grandchild.CapabilityId);
    }

    [Fact]
    public void ValidateThenRevokeCannotBeUsedToAcquireLaterEffectLease()
    {
        CapabilityAuthority authority = Authority();
        CapabilityDescriptorV1 capability = Mint(authority, new(10), CapabilityRights.Read);
        Assert.True(authority.Validate(capability.CapabilityId, new(10), 1, CapabilityRights.Read).IsSuccess);
        authority.Revoke(capability.CapabilityId);

        KernelResult<OperationAuthorityLease> lease = authority.AcquireOperationAuthority(capability.CapabilityId, new(10), 1,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Read);

        Assert.Equal(KernelError.CapabilityRevoked, lease.Error);
    }

    [Fact]
    public void LeaseAdmittedBeforeRevokeFollowsGrandfatherPolicyButNewAdmissionStops()
    {
        CapabilityAuthority authority = Authority();
        CapabilityDescriptorV1 capability = Mint(authority, new(10), CapabilityRights.Read);
        using OperationAuthorityLease lease = authority.AcquireOperationAuthority(capability.CapabilityId, new(10), 1,
            ResourceKind.File, "file:namespace", 1, CapabilityOperation.Read).Value!;

        authority.Revoke(capability.CapabilityId);

        Assert.Equal(EffectRevocationPolicy.GrandfatherAdmitted, lease.RevocationPolicy);
        Assert.Equal(1, authority.ActiveOperationLeaseCount);
        Assert.Equal(KernelError.CapabilityRevoked, authority.AcquireOperationAuthority(
            capability.CapabilityId, new(10), 1, ResourceKind.File, "file:namespace", 1,
            CapabilityOperation.Read).Error);
    }

    [Fact]
    public void OneShotAdmissionHasExactlyOneWinner()
    {
        CapabilityAuthority authority = Authority();
        CapabilityDescriptorV1 capability = Mint(authority, new(10), CapabilityRights.Execute);
        ConcurrentBag<KernelResult<OperationAuthorityLease>> results = new ConcurrentBag<KernelResult<OperationAuthorityLease>>();

        Parallel.For(0, 32, _ => results.Add(authority.AcquireOperationAuthority(
            capability.CapabilityId, new(10), 1, ResourceKind.File, "file:namespace", 1,
            CapabilityOperation.Execute, oneShot: true)));

        Assert.Single(results, static result => result.IsSuccess);
        Assert.Equal(31, results.Count(static result => result.Error == KernelError.CapabilityRevoked));
        foreach (KernelResult<OperationAuthorityLease> result in results.Where(static result => result.IsSuccess)) result.Value!.Dispose();
    }

    [Fact]
    public void ConcurrentDeriveAndRevokeNeverLeavesValidDescendant()
    {
        for (int iteration = 0; iteration < 100; iteration++)
        {
            CapabilityAuthority authority = Authority();
            CapabilityDescriptorV1 root = Mint(authority, new(10), CapabilityRights.Read | CapabilityRights.Delegate);
            KernelResult<CapabilityDescriptorV1> derived = default;
            Parallel.Invoke(
                () => derived = authority.Delegate(root.CapabilityId, new(10), new(20), CapabilityRights.Read, 1),
                () => authority.Revoke(root.CapabilityId));

            if (derived.IsSuccess)
                Assert.Equal(KernelError.CapabilityRevoked,
                    authority.Validate(derived.Value!.CapabilityId, new(20), 1, CapabilityRights.Read).Error);
            else
                Assert.Contains(derived.Error, new[] { KernelError.CapabilityRevoked, KernelError.InsufficientRights });
        }
    }

    [Fact]
    public void SessionCloseBetweenPrepareAndCommitDeniesAndCompensatesInReverse()
    {
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) scenario = SessionScenario();
        KernelResult<EndpointSessionRegistry.Record> close = default;

        KernelResult<EffectAdmissionLease> admitted = scenario.Kernel.AdmitSessionCapabilityEffect(scenario.Caller, scenario.Service,
            scenario.Session, scenario.Capability, ResourceKind.File, "file:namespace", 1,
            CapabilityOperation.Read, afterSessionPin: () =>
                close = scenario.Kernel.EndpointSessions.Close(scenario.Session, scenario.Caller));

        Assert.Equal(KernelError.SessionDraining, close.Error);
        Assert.Equal(KernelError.SessionClosed, admitted.Error);
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(EndpointSessionState.Closed,
            scenario.Kernel.EndpointSessions.SnapshotForProcess(scenario.Caller).Single().State);
    }

    [Fact]
    public void CapabilityPreparationFailureReleasesEarlierSessionPin()
    {
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) scenario = SessionScenario();

        KernelResult<EffectAdmissionLease> admitted = scenario.Kernel.AdmitSessionCapabilityEffect(scenario.Caller, scenario.Service,
            scenario.Session, scenario.Capability, ResourceKind.File, "file:other", 1,
            CapabilityOperation.Read);

        Assert.Equal(KernelError.WrongCapabilityResource, admitted.Error);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
    }

    [Fact]
    public void AdmittedPilotRunsEffectOutsideAuthorityRegistryLocks()
    {
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) scenario = SessionScenario();
        using EffectAdmissionLease admitted = scenario.Kernel.AdmitSessionCapabilityEffect(scenario.Caller, scenario.Service,
            scenario.Session, scenario.Capability, ResourceKind.File, "file:namespace", 1,
            CapabilityOperation.Read).Value!;

        // Re-entry into both registries completes while the admitted effect lease is live.
        Assert.True(scenario.Kernel.RevokeCapability(scenario.Capability).IsSuccess);
        Assert.Equal(KernelError.SessionDraining,
            scenario.Kernel.EndpointSessions.Close(scenario.Session, scenario.Caller).Error);
        Assert.Equal(EffectRevocationPolicy.GrandfatherAdmitted, admitted.Capability.RevocationPolicy);
    }

    [Fact]
    public void ExhaustedAttemptIdentityCannotConsumeOneShotOrQuotaAuthority()
    {
        (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service, EndpointSessionHandle Session, CapabilityId Capability) scenario = SessionScenario(CapabilityRights.Execute, quota: 1);
        typeof(RuntimeKernel).GetField("_nextEffectAdmissionAttemptId",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(scenario.Kernel, 0UL);

        KernelResult<EffectAdmissionLease> denied = scenario.Kernel.AdmitSessionCapabilityEffect(scenario.Caller, scenario.Service,
            scenario.Session, scenario.Capability, ResourceKind.File, "file:namespace", 1,
            CapabilityOperation.Execute, quotaAmount: 1, oneShot: true);
        SingProcess process = scenario.Kernel.Processes.Resolve(scenario.Caller).Value!;

        Assert.Equal(KernelError.CapacityExhausted, denied.Error);
        Assert.True(scenario.Kernel.CapabilityAuthority.Validate(scenario.Capability,
            process.DomainId, scenario.Caller.Generation, CapabilityRights.Execute).IsSuccess);
        Assert.Equal(1UL, scenario.Kernel.CapabilityAuthority
            .InspectConstraints(scenario.Capability)!.Value.SharedRemaining);
        Assert.Equal(0, scenario.Kernel.EndpointSessions.ActivePinCount(scenario.Session));
        Assert.Equal(0, scenario.Kernel.CapabilityAuthority.ActiveOperationLeaseCount);
    }

    private static (RuntimeKernel Kernel, ProcessHandle Caller, ProcessHandle Service,
        EndpointSessionHandle Session, CapabilityId Capability) SessionScenario(
        CapabilityRights rights = CapabilityRights.Read, ulong quota = ulong.MaxValue, TimeProvider? clock = null)
    {
        RuntimeKernel kernel = new RuntimeKernel(null, clock);
        (SingProcess _, ProcessHandle caller) = TestFixtures.Create(kernel, 1, 10);
        (SingProcess _, ProcessHandle service) = TestFixtures.Create(kernel, 2, 20);
        CapabilityId capability = kernel.CapabilityAuthority.Mint(new(20), new(10), ResourceKind.File,
            "file:namespace", rights, caller.Generation, resourceGeneration: 1,
            range: null, session: null, quota: quota, delegationDepth: 16).Value!.CapabilityId;
        EndpointSessionHandle session = kernel.EndpointSessions.Add(caller, service, [capability], [],
            new(new ChannelId(1), new EndpointId(1), 1), null).Value!.Handle;
        return (kernel, caller, service, session, capability);
    }

    private static CapabilityAuthority Authority() => new(new AuthorityRealmId(Guid.NewGuid()), new(4096, 4096));

    private static CapabilityDescriptorV1 Mint(CapabilityAuthority authority, DomainId subject,
        CapabilityRights rights, ushort depth = 16) => authority.Mint(new(1), subject,
        ResourceKind.File, "file:namespace", rights, 1, 1, null, null, ulong.MaxValue, depth).Value!;
}
