using System.Security.Cryptography;
using System.Reflection;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;
using SingPlus.Sip;
using System.Collections;

namespace SingPlus.Tests.Runtime;

public sealed class Phase05ResourceBudgetTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void OriginalChannelOwnerControlsAdmissionAndProcessQueueCleanup(bool absentRoute)
    {
        (RuntimeKernel Kernel, ComponentLifecycleSnapshot Sender, ProcessHandle Receiver,
            (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel) Create()
        {
            RuntimeKernel kernel = new RuntimeKernel();
            ComponentLifecycleSnapshot sender = Admit(kernel, "original-channel-route", 616, 5916,
                [new(ServiceBudgetDimension.IpcMessages, 2), new(ServiceBudgetDimension.IpcBytes, 64)]).Value!;
            ProcessHandle receiver = TestFixtures.Create(kernel, 617, 5917).Handle;
            (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
            Assert.True(kernel.Send(sender.Process, receiver, channel.Left, 1).IsSuccess);
            return (kernel, sender, receiver, channel);
        }
        (RuntimeKernel Kernel, ComponentLifecycleSnapshot Sender, ProcessHandle Receiver, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel) original = Create();
        (RuntimeKernel Kernel, ComponentLifecycleSnapshot Sender, ProcessHandle Receiver, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel) foreign = Create();
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(ChannelRegistry).GetField("_channels", flags)!.GetValue(original.Kernel.Channels)!;
        IDictionary foreignRoutes = (global::System.Collections.IDictionary)typeof(ChannelRegistry).GetField("_channels", flags)!.GetValue(foreign.Kernel.Channels)!;
        ChannelId id = original.Channel.Left.ChannelId;
        object originalOwner = routes[id]!;
        object foreignOwner = foreignRoutes[id]!;
        if (absentRoute) routes.Remove(id); else routes[id] = foreignOwner;
        Assert.False(original.Kernel.Send(original.Sender.Process, original.Receiver, original.Channel.Left, 1).IsSuccess);
        Assert.Equal(1UL, (ulong)originalOwner.GetType().GetProperty("Sequence")!.GetValue(originalOwner)!);
        Assert.Equal(1UL, (ulong)foreignOwner.GetType().GetProperty("Sequence")!.GetValue(foreignOwner)!);
        Assert.Equal(1UL, Usage(original.Kernel.QueryBudget(original.Sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
        Assert.True(original.Kernel.TerminateProcess(original.Receiver).IsSuccess);
        Assert.True((bool)originalOwner.GetType().GetProperty("Closed")!.GetValue(originalOwner)!);
        Assert.False((bool)foreignOwner.GetType().GetProperty("Closed")!.GetValue(foreignOwner)!);
        Assert.Equal(0UL, Usage(original.Kernel.QueryBudget(original.Sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
        Assert.Equal(1UL, Usage(foreign.Kernel.QueryBudget(foreign.Sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
        Assert.True(foreign.Kernel.Channels.Close(foreign.Channel.Left).IsSuccess);
        routes[id] = foreignOwner;
        Assert.False(original.Kernel.Channels.Close(original.Channel.Left, allowAlreadyClosed: true).IsSuccess);
        routes[id] = originalOwner;
        Assert.True(original.Kernel.Channels.Close(original.Channel.Left, allowAlreadyClosed: true).IsSuccess);
        Assert.Equal(0UL, Usage(original.Kernel.QueryBudget(original.Sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
    }

    [Fact]
    public void ForeignCompletedChannelOwnerCannotConsumeOriginalQueuedCharge()
    {
        (RuntimeKernel Kernel, ComponentLifecycleSnapshot Sender, ProcessHandle Receiver,
            (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ChannelEnvelope Envelope) Create()
        {
            RuntimeKernel kernel = new RuntimeKernel();
            ComponentLifecycleSnapshot sender = Admit(kernel, "same-ipc-owner", 614, 5914,
                [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!;
            ProcessHandle receiver = TestFixtures.Create(kernel, 615, 5915).Handle;
            (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
            ChannelEnvelope envelope = kernel.Send(sender.Process, receiver, channel.Left, 1).Value!;
            return (kernel, sender, receiver, channel, envelope);
        }
        (RuntimeKernel Kernel, ComponentLifecycleSnapshot Sender, ProcessHandle Receiver, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ChannelEnvelope Envelope) original = Create();
        (RuntimeKernel Kernel, ComponentLifecycleSnapshot Sender, ProcessHandle Receiver, (ChannelEndpointHandle Left, ChannelEndpointHandle Right) Channel, ChannelEnvelope Envelope) foreign = Create();
        Assert.Equal(original.Channel.Left, foreign.Channel.Left);
        Assert.Equal(original.Envelope.Sequence, foreign.Envelope.Sequence);
        (ChannelId ChannelId, ulong Sequence) key = (original.Channel.Left.ChannelId, original.Envelope.Sequence);
        (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) charge = IpcCharges(original.Kernel)[key];
        (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) foreignCharge = IpcCharges(foreign.Kernel)[key];
        Assert.Equal(charge, foreignCharge);
        Assert.True(foreign.Kernel.Budgets.QuarantineLease(foreign.Sender.Process, foreignCharge.Reservation).IsSuccess);
        Assert.True(foreign.Kernel.Channels.Close(foreign.Channel.Left).IsSuccess);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary routes = (global::System.Collections.IDictionary)typeof(ChannelRegistry).GetField("_channels", flags)!.GetValue(original.Kernel.Channels)!;
        IDictionary foreignRoutes = (global::System.Collections.IDictionary)typeof(ChannelRegistry).GetField("_channels", flags)!.GetValue(foreign.Kernel.Channels)!;
        object originalOwner = routes[key.ChannelId]!;
        IDictionary reservations = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations", flags)!.GetValue(original.Kernel.Budgets)!;
        IDictionary foreignReservations = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations", flags)!.GetValue(foreign.Kernel.Budgets)!;
        object record = reservations[charge.Reservation.ReservationId]!;
        PropertyInfo source = record.GetType().GetProperty("OriginalQueuedLifetime")!;
        object originalFact = source.GetValue(record)!;
        source.SetValue(record, source.GetValue(foreignReservations[foreignCharge.Reservation.ReservationId]!));
        routes[key.ChannelId] = foreignRoutes[key.ChannelId];
        Assert.True(original.Kernel.TerminateProcess(original.Receiver).IsSuccess);
        Assert.Equal(BudgetReservationState.Active, original.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal(1UL, Usage(original.Kernel.QueryBudget(original.Sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
        Assert.True((bool)originalFact.GetType().GetProperty("Ended")!.GetValue(originalFact)!);
        source.SetValue(record, originalFact);
        routes[key.ChannelId] = originalOwner;
        Assert.True(original.Kernel.TerminateProcess(original.Sender.Process).IsSuccess);
        Assert.Equal(BudgetReservationState.Released, original.Kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal(0UL, Usage(original.Kernel.QueryBudget(original.Sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
        Assert.Equal(BudgetReservationState.Quarantined, foreign.Kernel.QueryBudget(foreignCharge.Reservation).Value!.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void QueuedChargeTeardownTraceRunsAfterOwnerCommitWithoutRepeatingDebit(bool throws)
    {
        QueuedChargeClock clock = new QueuedChargeClock();
        RuntimeKernel kernel = new RuntimeKernel(platformProvider: null, timeProvider: clock);
        ComponentLifecycleSnapshot sender = Admit(kernel, "ipc-trace-owner", 612, 5912,
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!;
        ProcessHandle receiver = TestFixtures.Create(kernel, 613, 5913).Handle;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        ChannelEnvelope sent = kernel.Send(sender.Process, receiver, channel.Left, 1).Value!;
        (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) charge = IpcCharges(kernel)[(channel.Left.ChannelId, sent.Sequence)];
        Assert.True(kernel.StartTraceSession(sender.Process, 32).IsSuccess);
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        object memoryGate = typeof(RuntimeKernel).GetField("_platformMemoryUseGate", flags)!.GetValue(kernel)!;
        object correlationGate = typeof(RuntimeKernel).GetField("_requestResponseCorrelationGate", flags)!.GetValue(kernel)!;
        object budgetGate = typeof(ResourceBudgetAuthority).GetField("_gate", flags)!.GetValue(kernel.Budgets)!;
        int observed = 0;
        clock.Callback = () =>
        {
            if (kernel.QueryBudget(charge.Reservation).Value!.State != BudgetReservationState.Released) return;
            Assert.False(Monitor.IsEntered(memoryGate));
            Assert.False(Monitor.IsEntered(correlationGate));
            Assert.False(Monitor.IsEntered(budgetGate));
            observed++;
            clock.Callback = null;
            if (throws) throw new InvalidOperationException("Observation cannot undo exact charge release.");
            Assert.True(kernel.Channels.Close(channel.Left, allowAlreadyClosed: true).IsSuccess);
        };
        Assert.True(kernel.TerminateProcess(receiver).IsSuccess);
        Assert.Equal(1, observed);
        Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
        Assert.Empty(IpcCharges(kernel));
    }

    private sealed class QueuedChargeClock : TimeProvider
    {
        public Action? Callback { get; set; }
        public override long GetTimestamp() { Callback?.Invoke(); return 1; }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void ForeignQueuedChargeRouteCannotConsumeAnotherOriginalMessage(int fault)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot sender = Admit(kernel, "foreign-ipc-route", 610, 5910,
            [new(ServiceBudgetDimension.IpcMessages, 2), new(ServiceBudgetDimension.IpcBytes, 64)]).Value!;
        ProcessHandle receiver = TestFixtures.Create(kernel, 611, 5911).Handle;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) first = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) second = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        ChannelEnvelope one = kernel.Send(sender.Process, receiver, first.Left, 1).Value!;
        ChannelEnvelope two = kernel.Send(sender.Process, receiver, second.Left, 1).Value!;
        (ChannelId ChannelId, ulong Sequence) key = (first.Left.ChannelId, one.Sequence);
        (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) original = IpcCharges(kernel)[key];
        (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) other = IpcCharges(kernel)[(second.Left.ChannelId, two.Sequence)];
        BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        IDictionary reservations = (global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_reservations", flags)!.GetValue(kernel.Budgets)!;
        object record = reservations[original.Reservation.ReservationId]!;
        PropertyInfo lifetime = record.GetType().GetProperty("OriginalQueuedLifetime")!;
        object fact = lifetime.GetValue(record)!;
        if (fault == 0) IpcCharges(kernel)[key] = other;
        if (fault == 1) lifetime.SetValue(record, typeof(object).GetMethod("MemberwiseClone", flags)!.Invoke(fact, null));
        if (fault == 2) lifetime.SetValue(record, null);
        Assert.True(kernel.Receive(receiver, first.Right).IsSuccess);
        Assert.Equal(BudgetReservationState.Active, kernel.QueryBudget(original.Reservation).Value!.State);
        Assert.Equal(BudgetReservationState.Active, kernel.QueryBudget(other.Reservation).Value!.State);
        Assert.Equal(2UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
        IpcCharges(kernel)[key] = original;
        lifetime.SetValue(record, fact);
        Assert.True(kernel.Channels.Close(first.Left).IsSuccess);
        Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(original.Reservation).Value!.State);
        Assert.Equal(BudgetReservationState.Active, kernel.QueryBudget(other.Reservation).Value!.State);
        Assert.True(kernel.Receive(receiver, second.Right).IsSuccess);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AbsentIpcChargeRouteStillConsumesOriginalEndedQueueLifetime(int path)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot sender = Admit(kernel, "lost-ipc-route", 598, 5908,
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!;
        ProcessHandle receiver = TestFixtures.Create(kernel, 599, 5909).Handle;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        KernelResult<ChannelEnvelope> sent = kernel.Send(sender.Process, receiver, channel.Left, 1);
        Assert.True(sent.IsSuccess, sent.Message);
        (ChannelId ChannelId, ulong Sequence) key = (channel.Left.ChannelId, sent.Value!.Sequence);
        (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) original = IpcCharges(kernel)[key];
        IpcCharges(kernel).Remove(key);
        if (path == 0) Assert.True(kernel.Receive(receiver, channel.Right).IsSuccess);
        else if (path == 1) Assert.True(kernel.TerminateProcess(sender.Process).IsSuccess);
        else if (path == 2) Assert.True(kernel.TerminateProcess(receiver).IsSuccess);
        else Assert.True(kernel.Channels.Close(channel.Left).IsSuccess);
        Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(original.Reservation).Value!.State);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcBytes).Used);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IpcTransferRechecksOriginalChargeAfterPreparation(bool foreignRoute)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "callback-source", 596, 5906,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!;
        ComponentLifecycleSnapshot target = Admit(kernel, "callback-target", 597, 5907,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        OwnedBuffer<byte> outside = kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> routes = RegionCharges(kernel);
        (ProcessHandle Owner, BudgetReservationHandle Reservation) original = routes[buffer.Handle];
        RegionDescriptor[] before = kernel.Regions.Snapshot().ToArray();
        using PreparedOwnedTransfer prepared = ((SingPlus.Sip.ITransferableOwnedPayload)buffer).PrepareTransferForRuntime();
        int noEffectCalls = 0;
        MethodInfo call = typeof(RuntimeKernel).GetMethod("TransferPreparedRegionForIpc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        KernelResult<RegionHandle> result = (KernelResult<RegionHandle>)call.Invoke(kernel,
            [kernel.Processes.Resolve(source.Process).Value!, kernel.Processes.Resolve(target.Process).Value!, buffer.Handle,
                (Func<KernelResult>)(() =>
                {
                    if (foreignRoute) routes[buffer.Handle] = routes[outside.Handle];
                    else routes.Remove(buffer.Handle);
                    return KernelResult.Ok();
                }), (Action)(() => { noEffectCalls++; prepared.Dispose(); }), prepared])!;
        Assert.Equal(foreignRoute ? 1 : 0, noEffectCalls);
        if (foreignRoute)
        {
            Assert.Equal(KernelError.PlatformFaulted, result.Error);
            Assert.Equal(before, kernel.Regions.Snapshot());
            Assert.Equal(0UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(8UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
            routes[buffer.Handle] = original;
        }
        else
        {
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(4UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(4UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.Equal(target.Process, routes[result.Value].Owner);
            Assert.False(routes.ContainsKey(buffer.Handle));
            Assert.False(buffer.IsValid);
            Assert.Same(prepared.Projection, kernel.Regions.CaptureTransferPayload(result.Value,
                new(target.Domain, target.Process.Generation)).Value);
        }
        Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        if (!foreignRoute) Assert.True(((SingPlus.Sip.ITransferableOwnedPayload)prepared.Projection).IsValidForRuntime);
        Assert.True(kernel.TerminateProcess(target.Process).IsSuccess);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ForeignRegionChargeRouteCannotRefundAnotherLiveRegion(bool scalar)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "foreign-route-source", 594, 5904,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!;
        ComponentLifecycleSnapshot target = Admit(kernel, "foreign-route-target", 595, 5905,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        OwnedBuffer<byte> outside = kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        OwnedBuffer<byte>? buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        OwnedRegion<int>? region = scalar ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        RegionHandle handle = region?.Handle ?? buffer!.Handle;
        Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> routes = RegionCharges(kernel);
        (ProcessHandle Owner, BudgetReservationHandle Reservation) original = routes[handle];
        (ProcessHandle Owner, BudgetReservationHandle Reservation) foreign = routes[outside.Handle];
        routes[handle] = foreign;
        RegionDescriptor[] before = kernel.Regions.Snapshot().ToArray();
        KernelError transfer = scalar ? kernel.TransferRegion(source.Process, target.Process, region!).Error
            : kernel.TransferRegion(source.Process, target.Process, buffer!).Error;
        Assert.Equal(KernelError.PlatformFaulted, transfer);
        Assert.Equal(before, kernel.Regions.Snapshot());
        Assert.Equal(0UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True((scalar ? kernel.ReleaseRegion(source.Process, region!) : kernel.ReleaseRegion(source.Process, buffer!)).IsSuccess);
        Assert.Equal(BudgetReservationState.Reserved, kernel.QueryBudget(foreign.Reservation).Value!.State);
        Assert.Equal(8UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        routes[handle] = original;
        Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void LostOriginalRegionChargeRouteCannotAdmitUnbudgetedTransfer(int path)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "lost-transfer-source", 591, 5901,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        (SingProcess Process, ProcessHandle Handle) target = TestFixtures.Create(kernel, 592, 5902);
        OwnedBuffer<byte>? buffer = path == 0 ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        OwnedRegion<int>? scalar = path == 0 ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        RegionHandle region = scalar?.Handle ?? buffer!.Handle;
        RegionCharges(kernel).Remove(region);
        RegionDescriptor before = Assert.Single(kernel.Regions.Snapshot());
        KernelError error;
        if (path == 0) error = kernel.TransferRegion(source.Process, target.Handle, scalar!).Error;
        else if (path == 1) error = kernel.TransferRegion(source.Process, target.Handle, buffer!).Error;
        else
        {
            SingProcess sourceProcess = kernel.Processes.Resolve(source.Process).Value!;
            MethodInfo call = typeof(RuntimeKernel).GetMethod("TransferRegionForIpc", BindingFlags.Instance | BindingFlags.NonPublic)!;
            KernelResult<RegionHandle> result = (KernelResult<RegionHandle>)call.Invoke(kernel,
                [sourceProcess, target.Process, region, null])!;
            error = result.Error;
        }
        Assert.Equal(KernelError.BudgetNotConfigured, error);
        Assert.Equal(before, Assert.Single(kernel.Regions.Snapshot()));
        Assert.Equal(4UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DirectRegionReleaseConsumesOriginalChargeWithoutRoutingIndex(bool scalar)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "lost-release-source", 593, 5903,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        OwnedBuffer<byte>? buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        OwnedRegion<int>? region = scalar ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        RegionHandle handle = region?.Handle ?? buffer!.Handle;
        RegionCharges(kernel).Remove(handle);
        Assert.True((scalar ? kernel.ReleaseRegion(source.Process, region!) : kernel.ReleaseRegion(source.Process, buffer!)).IsSuccess);
        Assert.Equal(RegionState.Released, Assert.Single(kernel.Regions.Snapshot()).State);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void SettlementCapturesCallerOnceOutsideLockAndRechecksOwnerBeforeCharge(int fault)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot component = Admit(kernel, "settlement-input", 1093, 1094,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 100)]).Value!;
        BudgetReservationHandle lease = kernel.Budgets.Reserve(component.Process,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 40)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(component.Process, lease).IsSuccess);
        Assert.True(kernel.Budgets.BeginConsumption(component.Process, lease).IsSuccess);
        ChangingSettlementAmounts input = new ChangingSettlementAmounts(() =>
        {
            Task<KernelResult<BudgetAccountSnapshot>> query = Task.Run(() => kernel.QueryBudget(component.ProcessBudget));
            Assert.True(query.Wait(TimeSpan.FromSeconds(5)), "Caller enumeration ran under budget owner lock.");
            Assert.True(query.Result.IsSuccess);
            if (fault == 1) Assert.True(kernel.Budgets.QuarantineLease(component.Process, lease).IsSuccess);
            if (fault == 2) throw new InvalidOperationException("Caller usage unavailable.");
        });
        if (fault == 2)
            Assert.Throws<InvalidOperationException>(() => kernel.Budgets.SettleLease(component.Process, lease, input));
        else
            Assert.Equal(fault == 0, kernel.Budgets.SettleLease(component.Process, lease, input).IsSuccess);
        Assert.Equal(1, input.Enumerations);
        if (fault != 0)
        {
            Assert.Equal(40UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
            if (fault == 1) Assert.True(kernel.Budgets.ReconcileLease(component.Process, lease).IsSuccess);
        }
        BudgetReservationSnapshot settled = kernel.Budgets.SettleLease(component.Process, lease,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 7)]).Value!;
        Assert.Equal(new BudgetAmount(ServiceBudgetDimension.OwnedMemoryBytes, 7), Assert.Single(settled.ChargedAmounts!));
        Assert.Equal(7UL, Usage(kernel.QueryBudget(component.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(kernel.DrainComponent(new("settlement-input"), component.Process).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(new("settlement-input"), component.Process).IsSuccess);
        Assert.Equal(7UL, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    private sealed class ChangingSettlementAmounts(Action callback) : IReadOnlyList<BudgetAmount>
    {
        public int Enumerations { get; private set; }
        public int Count => 1;
        public BudgetAmount this[int index] => new(ServiceBudgetDimension.OwnedMemoryBytes, 7);
        public IEnumerator<BudgetAmount> GetEnumerator()
        {
            Enumerations++;
            callback();
            yield return new(ServiceBudgetDimension.OwnedMemoryBytes, Enumerations == 1 ? 7UL : 90UL);
        }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Theory]
    [InlineData(0UL)] [InlineData(10UL)]
    public void ActualComponentRetirementPreservesTerminalChargeWithoutLiveReservationPin(ulong charge)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot component = Admit(kernel, "terminal-retirement", 1091, 1092,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 100)]).Value!;
        BudgetReservationHandle lease = kernel.Budgets.Reserve(component.Process,
            [new(ServiceBudgetDimension.ComputeTimeNanoseconds, 40)],
            BudgetReservationLifetime.ExternalEffect, AdmissionQosHint.None).Value!.Reservation;
        Assert.True(kernel.Budgets.BindLease(component.Process, lease).IsSuccess);
        Assert.True(kernel.Budgets.BeginConsumption(component.Process, lease).IsSuccess);
        BudgetReservationSnapshot settled = kernel.Budgets.SettleLease(component.Process, lease,
            charge == 0 ? [] : [new(ServiceBudgetDimension.ComputeTimeNanoseconds, charge)]).Value!;
        ulong rootBefore = Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.ComputeTimeNanoseconds).Used;
        Assert.Equal(charge, rootBefore);
        Assert.True(kernel.DrainComponent(new("terminal-retirement"), component.Process).IsSuccess);
        Assert.Equal(ComponentLifecycleState.Reclaimable, kernel.ObserveComponentTeardown(new("terminal-retirement")).Value!.State);
        Assert.Equal(KernelError.StaleGeneration,
            kernel.RetireReclaimableComponent(new("terminal-retirement"), component.Process with { Generation = component.Process.Generation + 1 }).Error);
        Assert.True(kernel.QueryBudget(component.ProcessBudget).IsSuccess);
        Assert.True(kernel.RetireReclaimableComponent(new("terminal-retirement"), component.Process).IsSuccess);
        Assert.False(kernel.Budgets.HasProcessAccount(component.Process));
        Assert.Equal(KernelError.StaleGeneration, kernel.QueryBudget(component.ProcessBudget).Error);
        Assert.Equal(KernelError.StaleGeneration, kernel.QueryBudget(component.ServiceBudget).Error);
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(new("terminal-retirement")).Error);
        BudgetSnapshotAssertions.Equal(settled, kernel.Budgets.Query(lease).Value!);
        Assert.Equal(rootBefore, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.ComputeTimeNanoseconds).Used);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task ReturnedRegionOwnerCannotMakeOldChargeCleanupConsumeNewGeneration(bool scalar, bool reverseSettlement)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "aba-source", 601, 6001,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!;
        ComponentLifecycleSnapshot peer = Admit(kernel, "aba-peer", 602, 6002,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!;
        OwnedRegion<int>? scalarRegion = scalar ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        OwnedBuffer<byte>? buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        RegionHandle old = scalar ? scalarRegion!.Handle : buffer!.Handle;
        Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> routes = RegionCharges(kernel);
        (ProcessHandle Owner, BudgetReservationHandle Reservation) oldCharge = routes[old];
        Assert.True(kernel.Budgets.QuarantineLease(source.Process, oldCharge.Reservation).IsSuccess);
        if (scalar) scalarRegion = kernel.TransferRegion(source.Process, peer.Process, scalarRegion!).Value!;
        else buffer = kernel.TransferRegion(source.Process, peer.Process, buffer!).Value!;
        RegionHandle middle = scalar ? scalarRegion!.Handle : buffer!.Handle;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) middleCharge = routes[middle];
        Assert.True(kernel.Budgets.QuarantineLease(peer.Process, middleCharge.Reservation).IsSuccess);
        if (scalar) scalarRegion = kernel.TransferRegion(peer.Process, source.Process, scalarRegion!).Value!;
        else buffer = kernel.TransferRegion(peer.Process, source.Process, buffer!).Value!;
        RegionHandle current = scalar ? scalarRegion!.Handle : buffer!.Handle;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) currentCharge = routes[current];
        RegionDescriptor descriptor = kernel.Regions.Snapshot().Single(item => item.Handle == current);
        Assert.Equal(old.RegionId, current.RegionId);
        Assert.Equal(old.Generation.Value + 2, current.Generation.Value);
        Assert.NotEqual(oldCharge.Reservation, currentCharge.Reservation);
        Assert.False(kernel.Regions.HasEndedAccountingLifetime(current));
        Assert.True(kernel.Regions.HasEndedAccountingLifetime(old));
        Assert.True(kernel.Regions.HasEndedAccountingLifetime(middle));
        (ProcessHandle Owner, BudgetReservationHandle Reservation)[] order = reverseSettlement ? new[] { middleCharge, oldCharge } : new[] { oldCharge, middleCharge };
        foreach ((ProcessHandle Owner, BudgetReservationHandle Reservation) charge in order)
        {
            Assert.False(kernel.ReleaseBudget(charge.Owner, charge.Reservation).IsSuccess);
            Assert.True(kernel.Budgets.ReconcileLease(charge.Owner, charge.Reservation).IsSuccess);
            Assert.True(kernel.Budgets.SettleLease(charge.Owner, charge.Reservation, []).IsSuccess);
        }
        KernelResult<BudgetReservationSnapshot>[] released = await Task.WhenAll(order.Select(charge => Task.Run(() =>
            kernel.ReleaseBudget(charge.Owner, charge.Reservation)))).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(released, result => Assert.True(result.IsSuccess));
        Assert.False(routes.ContainsKey(old));
        Assert.False(routes.ContainsKey(middle));
        Assert.Equal(currentCharge, routes[current]);
        Assert.Equal(descriptor, kernel.Regions.Snapshot().Single(item => item.Handle == current));
        Assert.Equal(4UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(peer.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(kernel.ReleaseBudget(source.Process, oldCharge.Reservation).IsSuccess);
        Assert.Equal(currentCharge, routes[current]);
        Assert.False(kernel.ReleaseBudget(source.Process, currentCharge.Reservation).IsSuccess);
        Assert.True(kernel.TerminateProcess(peer.Process).IsSuccess);
        Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        Assert.False(routes.ContainsKey(current));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SettledTransferredGenerationRetryCannotReleaseCurrentRegionOrCharge(bool scalar)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "old-routing", 591, 5901,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        ComponentLifecycleSnapshot target = Admit(kernel, "new-routing", 592, 5902,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        OwnedRegion<int>? scalarRegion = scalar ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        OwnedBuffer<byte>? buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        RegionHandle old = scalar ? scalarRegion!.Handle : buffer!.Handle;
        Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> routes = RegionCharges(kernel);
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge = routes[old];
        Assert.False(kernel.Regions.HasEndedAccountingLifetime(old));
        Assert.False(kernel.Regions.HasEndedAccountingLifetime(new(old.RegionId, new(0))));
        Assert.True(kernel.Budgets.QuarantineLease(source.Process, charge.Reservation).IsSuccess);
        RegionHandle moved = scalar ? kernel.TransferRegion(source.Process, target.Process, scalarRegion!).Value!.Handle
            : kernel.TransferRegion(source.Process, target.Process, buffer!).Value!.Handle;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) currentCharge = routes[moved];
        RegionDescriptor current = kernel.Regions.Snapshot().Single(item => item.Handle == moved);
        Assert.True(kernel.Regions.HasEndedAccountingLifetime(old));
        Assert.False(kernel.Regions.HasEndedAccountingLifetime(moved));
        Assert.False(kernel.Regions.HasEndedAccountingLifetime(new(moved.RegionId, new(moved.Generation.Value + 1))));
        Assert.False(kernel.ReleaseBudget(source.Process, charge.Reservation).IsSuccess);
        Assert.Equal(charge, routes[old]);
        Assert.True(kernel.Budgets.ReconcileLease(source.Process, charge.Reservation).IsSuccess);
        Assert.True(kernel.Budgets.SettleLease(source.Process, charge.Reservation, []).IsSuccess);
        Assert.False(kernel.ReleaseBudget(target.Process, charge.Reservation).IsSuccess);
        Assert.Equal(charge, routes[old]);
        for (int retry = 0; retry < 2; retry++)
        {
            Assert.True(kernel.ReleaseBudget(source.Process, charge.Reservation).IsSuccess);
            Assert.False(routes.ContainsKey(old));
            Assert.Equal(currentCharge, routes[moved]);
            Assert.Equal(current, kernel.Regions.Snapshot().Single(item => item.Handle == moved));
            Assert.Equal(4UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        Assert.True(kernel.TerminateProcess(target.Process).IsSuccess);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactReleasedBudgetRetryRetiresOnlyEndedRegionRouting(bool reclaim)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "retry-source", 581, 5801,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        ComponentLifecycleSnapshot other = Admit(kernel, "retry-other", 582, 5802,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        RegionHandle region = kernel.AllocateBuffer<byte>(source.Process, 4).Value!.Handle;
        RegionHandle outside = kernel.AllocateBuffer<byte>(other.Process, 4).Value!.Handle;
        Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> routes = RegionCharges(kernel);
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge = routes[region];
        (ProcessHandle Owner, BudgetReservationHandle Reservation) outsideCharge = routes[outside];
        Assert.True(kernel.Budgets.QuarantineLease(source.Process, charge.Reservation).IsSuccess);
        if (reclaim) Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        Assert.False(kernel.ReleaseBudget(source.Process, charge.Reservation).IsSuccess);
        Assert.Equal(charge, routes[region]);
        Assert.True(kernel.Budgets.ReconcileLease(source.Process, charge.Reservation).IsSuccess);
        Assert.False(kernel.ReleaseBudget(source.Process, charge.Reservation).IsSuccess);
        Assert.Equal(charge, routes[region]);
        Assert.True(kernel.Budgets.SettleLease(source.Process, charge.Reservation, []).IsSuccess);
        ProcessHandle wrong = source.Process with { Generation = source.Process.Generation + 1 };
        Assert.False(kernel.ReleaseBudget(wrong, charge.Reservation).IsSuccess);
        Assert.Equal(charge, routes[region]);
        for (int retry = 0; retry < 2; retry++)
        {
            Assert.True(kernel.ReleaseBudget(source.Process, charge.Reservation).IsSuccess);
            Assert.Equal(!reclaim, routes.ContainsKey(region));
            Assert.Equal(outsideCharge, routes[outside]);
            Assert.Equal(4UL, Usage(kernel.QueryBudget(other.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        Assert.Equal(reclaim ? RegionState.Released : RegionState.Owned,
            kernel.Regions.Snapshot().Single(item => item.Handle == region).State);
        if (!reclaim) Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        Assert.True(kernel.TerminateProcess(other.Process).IsSuccess);
    }
    [Fact]
    public void HierarchyRetirementCannotRetireUnrelatedServiceOrSharedChild()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessBudgetAdmission first = kernel.Budgets.CreateProcessHierarchy(new(new(1007), 1), "first", "first-process", []).Value;
        ProcessBudgetAdmission second = kernel.Budgets.CreateProcessHierarchy(new(new(1008), 1), "second", "second-process", []).Value;
        Assert.Equal(KernelError.StaleGeneration, kernel.Budgets.RetireProcessHierarchy(first.Process,
            first.ProcessBudget, second.ServiceBudget).Error);
        Assert.True(kernel.Budgets.HasProcessAccount(first.Process));
        Assert.True(kernel.QueryBudget(first.ProcessBudget).IsSuccess);
        Assert.True(kernel.QueryBudget(second.ServiceBudget).IsSuccess);
        Assert.True(kernel.Budgets.AttachProcess(new(new(1009), 1), first.ProcessBudget).IsSuccess);
        Assert.True(kernel.Budgets.HasProcessAccount(new(new(1009), 1)));
        Assert.Equal(KernelError.ReplacementBlocked, kernel.Budgets.RetireProcessHierarchy(first.Process,
            first.ProcessBudget, first.ServiceBudget).Error);
        KernelResult<BudgetAccountSnapshot> sibling = kernel.Budgets.CreateChild(first.ServiceBudget, BudgetAccountLevel.ProcessDomain, "sibling", []);
        Assert.True(sibling.IsSuccess);
        Assert.Equal(KernelError.ReplacementBlocked, kernel.Budgets.RetireProcessHierarchy(first.Process,
            first.ProcessBudget, first.ServiceBudget).Error);
        Assert.True(kernel.Budgets.HasProcessAccount(first.Process));
        Assert.True(kernel.QueryBudget(sibling.Value!.Account).IsSuccess);
        Assert.True(kernel.Budgets.RetireProcessHierarchy(second.Process, second.ProcessBudget, second.ServiceBudget).IsSuccess);
        Assert.False(kernel.Budgets.HasProcessAccount(second.Process));
        Assert.True(kernel.QueryBudget(first.ServiceBudget).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ReservationInputFaultOrOwnerAbaDoesNotChargeOldHierarchy(bool aba)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle admin) = TestFixtures.Create(kernel, 1003, 2003);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 1004, 2004);
        CapabilityId cap = kernel.MintCapability(new(2003), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        ProcessBudgetAdmission hierarchy = kernel.AdmitProcessBudget(admin, cap, target, "reserve-input", []).Value;
        CallbackBudgetAmounts input = new CallbackBudgetAmounts(() =>
        {
            if (!aba) throw new InvalidOperationException("reservation input fault");
            Assert.True(kernel.TerminateProcess(target).IsSuccess);
            TestFixtures.Create(kernel, 1004, 2004, 2);
        });
        KernelResult<BudgetReservationSnapshot> refused = kernel.ReserveBudget(target, input, BudgetReservationLifetime.LocalResource);
        Assert.Equal(aba ? KernelError.StaleHandle : KernelError.PlatformFaulted, refused.Error);
        Assert.Equal(0ul, Usage(kernel.QueryBudget(hierarchy.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0ul, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (!aba)
            Assert.True(kernel.ReserveBudget(target, [new(ServiceBudgetDimension.OwnedMemoryBytes, 7)], BudgetReservationLifetime.LocalResource).IsSuccess);
    }

    [Fact]
    public async Task BlockedReservationInputAllowsConcurrentOwnerExitWithoutLateCharge()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle admin) = TestFixtures.Create(kernel, 1005, 2005);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 1006, 2006);
        CapabilityId cap = kernel.MintCapability(new(2005), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        ProcessBudgetAdmission hierarchy = kernel.AdmitProcessBudget(admin, cap, target, "blocked-reserve", []).Value;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        CallbackBudgetAmounts input = new CallbackBudgetAmounts(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        Task<KernelResult<BudgetReservationSnapshot>> pending = Task.Run(() => kernel.ReserveBudget(target, input, BudgetReservationLifetime.LocalResource));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True((await Task.Run(() => kernel.TerminateProcess(target)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.StaleHandle, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Error);
        Assert.Equal(0ul, Usage(kernel.QueryBudget(hierarchy.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(false, 1)] [InlineData(false, 2)] [InlineData(false, 3)]
    [InlineData(true, 0)] [InlineData(true, 1)] [InlineData(true, 2)] [InlineData(true, 3)]
    public void BudgetInputCallbackCannotCommitAfterSourceLossOrObservationFault(bool configure, int fault)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle admin) = TestFixtures.Create(kernel, 997, 1997);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 998, 1998);
        CapabilityId cap = kernel.MintCapability(new(1997), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        CallbackBudgetAmounts limits = new CallbackBudgetAmounts(() =>
        {
            if (fault == 0) Assert.True(kernel.CapabilityAuthority.Revoke(cap).IsSuccess);
            else if (fault == 1) kernel.CapabilityAuthority.RevokeAllForDomain(new(1997));
            else if (fault == 2) Assert.True(kernel.TerminateProcess(admin).IsSuccess);
            else throw new InvalidOperationException("input observation fault");
        });
        KernelError error = configure ? kernel.ConfigureSystemBudget(admin, cap, limits).Error
            : kernel.AdmitProcessBudget(admin, cap, target, "callback", limits).Error;
        Assert.Equal(fault switch { 2 => KernelError.StaleHandle, 3 => KernelError.PlatformFaulted,
            _ => KernelError.CapabilityRevoked }, error);
        Assert.Equal(1, AccountCount(kernel.Budgets));
        Assert.False(kernel.Budgets.HasProcessAccount(target));
        Assert.Equal(ulong.MaxValue, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Limit);
        if (fault == 3)
        {
            BudgetAmount[] recovered = [new(ServiceBudgetDimension.OwnedMemoryBytes, 7)];
            if (configure)
            {
                KernelResult<BudgetAccountSnapshot> configured = kernel.ConfigureSystemBudget(admin, cap, recovered);
                Assert.True(configured.IsSuccess);
                Assert.Equal(7ul, Usage(configured.Value!, ServiceBudgetDimension.OwnedMemoryBytes).Limit);
            }
            else
            {
                KernelResult<ProcessBudgetAdmission> admitted = kernel.AdmitProcessBudget(admin, cap, target, "recovered-input", recovered);
                Assert.True(admitted.IsSuccess);
                Assert.Equal(7ul, Usage(kernel.QueryBudget(admitted.Value.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Limit);
                Assert.Equal(3, AccountCount(kernel.Budgets));
            }
        }
    }

    [Fact]
    public void BudgetInputCallbackCannotAttachToReusedTargetProcessId()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle admin) = TestFixtures.Create(kernel, 999, 1999);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 1000, 2000);
        CapabilityId cap = kernel.MintCapability(new(1999), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        CallbackBudgetAmounts limits = new CallbackBudgetAmounts(() =>
        {
            Assert.True(kernel.TerminateProcess(target).IsSuccess);
            TestFixtures.Create(kernel, 1000, 2000, 2);
        });
        Assert.Equal(KernelError.StaleHandle, kernel.AdmitProcessBudget(admin, cap, target, "stale-target", limits).Error);
        Assert.Equal(1, AccountCount(kernel.Budgets));
        Assert.False(kernel.Budgets.HasProcessAccount(target));
        Assert.False(kernel.Budgets.HasProcessAccount(new(target.ProcessId, 2)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BlockedBudgetInputObservationHoldsNoBudgetOrCapabilityGate(bool configure)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle admin) = TestFixtures.Create(kernel, 1001, 2001);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 1002, 2002);
        CapabilityId cap = kernel.MintCapability(new(2001), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        using ManualResetEventSlim entered = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        CallbackBudgetAmounts limits = new CallbackBudgetAmounts(() =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        Task<KernelError> pending = Task.Run(() => configure ? kernel.ConfigureSystemBudget(admin, cap, limits).Error
            : kernel.AdmitProcessBudget(admin, cap, target, "blocked-input", limits).Error);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True((await Task.Run(() => kernel.QueryBudget(kernel.Budgets.SystemBudget)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.True((await Task.Run(() => kernel.CapabilityAuthority.Revoke(cap)).WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
        }
        finally { release.Set(); }
        Assert.Equal(KernelError.CapabilityRevoked, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, AccountCount(kernel.Budgets));
    }

    private sealed class CallbackBudgetAmounts(Action callback) : IReadOnlyList<BudgetAmount>
    {
        public int Count => 1;
        public BudgetAmount this[int index] => index == 0 ? new(ServiceBudgetDimension.OwnedMemoryBytes, 7)
            : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<BudgetAmount> GetEnumerator()
        {
            callback();
            yield return this[0];
        }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void RefusedHierarchyLimitDoesNotAllocateAccountsOrChargeRoot()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        Assert.True(kernel.Budgets.ConfigureSystem([new(ServiceBudgetDimension.OwnedMemoryBytes, 10)]).IsSuccess);
        Assert.Equal(KernelError.BudgetExceeded, Admit(kernel, "excessive-hierarchy", 996, 1996,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 11)]).Error);
        Assert.Equal(1, AccountCount(kernel.Budgets));
        Assert.Equal(2ul, typeof(ResourceBudgetAuthority).GetField("_nextAccountId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Budgets));
        Assert.Equal(0ul, Usage(kernel.QueryBudget(kernel.Budgets.SystemBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(kernel.Budgets.HasProcessAccount(new(new(996), 1)));
    }

    [Theory]
    [InlineData(0ul)] [InlineData(ulong.MaxValue)]
    public void ComponentHierarchyNeedsTwoIdsWithoutPartialAllocation(ulong next)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        FieldInfo field = typeof(ResourceBudgetAuthority).GetField("_nextAccountId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(kernel.Budgets, next);
        KernelResult<ComponentLifecycleSnapshot> result = Admit(kernel, "pair-exhausted", 991, 1991, []);
        Assert.Equal(KernelError.CapacityExhausted, result.Error);
        Assert.Equal(1, AccountCount(kernel.Budgets));
        Assert.Equal(next, field.GetValue(kernel.Budgets));
        Assert.False(kernel.Budgets.HasProcessAccount(new(new(991), 1)));
        Assert.Equal(KernelError.ComponentNotFound, kernel.QueryComponent(new("pair-exhausted")).Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new(991), 1)).Error);
    }

    [Fact]
    public void FinalHierarchyPairCommitsOnceAndDoesNotWrapToReusableIds()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        FieldInfo field = typeof(ResourceBudgetAuthority).GetField("_nextAccountId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(kernel.Budgets, ulong.MaxValue - 1);
        KernelResult<ComponentLifecycleSnapshot> admitted = Admit(kernel, "last-pair", 992, 1992, []);
        Assert.True(admitted.IsSuccess, admitted.Message);
        Assert.Equal(ulong.MaxValue - 1, admitted.Value!.ServiceBudget.AccountId.Value);
        Assert.Equal(ulong.MaxValue, admitted.Value.ProcessBudget.AccountId.Value);
        Assert.Equal(0ul, field.GetValue(kernel.Budgets));
        Assert.Equal(3, AccountCount(kernel.Budgets));
        Assert.Equal(KernelError.CapacityExhausted, Admit(kernel, "after-last-pair", 993, 1993, []).Error);
        Assert.Equal(3, AccountCount(kernel.Budgets));
        Assert.True(kernel.QueryBudget(admitted.Value.ProcessBudget).IsSuccess);
    }

    [Fact]
    public async Task OverlappingBudgetAdmissionHasOneHierarchyAndNoOrphanAccounts()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        (SingProcess _, ProcessHandle admin) = TestFixtures.Create(kernel, 994, 1994);
        (SingProcess _, ProcessHandle target) = TestFixtures.Create(kernel, 995, 1995);
        CapabilityId cap = kernel.MintCapability(new(1994), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        using ManualResetEventSlim start = new ManualResetEventSlim();
        Task<KernelResult<ProcessBudgetAdmission>>[] workers = Enumerable.Range(0, 2).Select(i => Task.Run(() =>
        {
            Assert.True(start.Wait(TimeSpan.FromSeconds(10)));
            return kernel.AdmitProcessBudget(admin, cap, target, $"overlap-{i}", []);
        })).ToArray();
        start.Set();
        KernelResult<ProcessBudgetAdmission>[] results = await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
        KernelResult<ProcessBudgetAdmission> committed = Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(KernelError.DuplicateIdentity, Assert.Single(results, result => !result.IsSuccess).Error);
        Assert.Equal(3, AccountCount(kernel.Budgets));
        object? next = typeof(ResourceBudgetAuthority).GetField("_nextAccountId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Budgets);
        Assert.Equal(KernelError.DuplicateIdentity, kernel.AdmitProcessBudget(admin, cap, target, "duplicate", []).Error);
        Assert.Equal(3, AccountCount(kernel.Budgets));
        Assert.Equal(next, typeof(ResourceBudgetAuthority).GetField("_nextAccountId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel.Budgets));
        Assert.True(kernel.QueryBudget(committed.Value.ProcessBudget).IsSuccess);
    }

    private static int AccountCount(ResourceBudgetAuthority owner) =>
        ((global::System.Collections.IDictionary)typeof(ResourceBudgetAuthority).GetField("_accounts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!).Count;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    public void IpcQueueLifetimeCannotEraseRefusedExactBudgetAssociation(int path, int fault)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot sender = Admit(kernel, "ipc-retention", 571, 5701,
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!;
        ProcessHandle receiver = TestFixtures.Create(kernel, 572, 5702).Handle;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        KernelResult<ChannelEnvelope> sent = kernel.Send(sender.Process, receiver, channel.Left, 1);
        Assert.True(sent.IsSuccess, sent.Message);
        (ChannelId ChannelId, ulong Sequence) key = (channel.Left.ChannelId, sent.Value!.Sequence);
        (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) charge = IpcCharges(kernel)[key];
        if (fault == 1 || fault == 3)
        {
            Assert.True(kernel.Budgets.QuarantineLease(sender.Process, charge.Reservation).IsSuccess);
            if (fault == 3) Assert.True(kernel.Budgets.ReconcileLease(sender.Process, charge.Reservation).IsSuccess);
        }
        else if (fault == 2)
        {
            Assert.True(kernel.Budgets.BindLease(sender.Process, charge.Reservation).IsSuccess);
            Assert.True(kernel.Budgets.BeginConsumption(sender.Process, charge.Reservation).IsSuccess);
        }
        BudgetReservationSnapshot before = kernel.QueryBudget(charge.Reservation).Value!;
        if (path == 0)
        {
            Assert.True(kernel.Receive(receiver, channel.Right).IsSuccess);
            Assert.False(kernel.Receive(receiver, channel.Right).IsSuccess);
        }
        else if (path == 1) Assert.True(kernel.TerminateProcess(sender.Process).IsSuccess);
        else if (path == 2) Assert.True(kernel.TerminateProcess(receiver).IsSuccess);
        else Assert.True(kernel.Channels.Close(channel.Left).IsSuccess);
        if (fault == 0)
        {
            Assert.Empty(IpcCharges(kernel));
            Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(charge.Reservation).Value!.State);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcBytes).Used);
        }
        else
        {
            Assert.Equal(charge, Assert.Single(IpcCharges(kernel)).Value);
            Assert.Equal(before.State, kernel.QueryBudget(charge.Reservation).Value!.State);
            Assert.Equal(1UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
            Assert.Equal(before.Amounts.Single(a => a.Dimension == ServiceBudgetDimension.IpcBytes).Amount,
                Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcBytes).Used);
            Assert.False(kernel.ReleaseBudget(sender.Process, charge.Reservation).IsSuccess);
        }
    }

    [Fact]
    public void IpcReceiveAndClosureRetainOnlyRefusedExactSequenceAcrossChannels()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot sender = Admit(kernel, "ipc-exact-sequences", 573, 5703,
            [new(ServiceBudgetDimension.IpcMessages, 2), new(ServiceBudgetDimension.IpcBytes, 64)]).Value!;
        ProcessHandle receiver = TestFixtures.Create(kernel, 574, 5704).Handle;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) first = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) second = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        ChannelEnvelope sent1 = kernel.Send(sender.Process, receiver, first.Left, 1).Value!;
        ChannelEnvelope sent2 = kernel.Send(sender.Process, receiver, second.Left, 1).Value!;
        Assert.Equal(sent1.Sequence, sent2.Sequence);
        (ChannelId ChannelId, ulong Sequence) key1 = (first.Left.ChannelId, sent1.Sequence);
        (ChannelId ChannelId, ulong Sequence) key2 = (second.Left.ChannelId, sent2.Sequence);
        (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) retained = IpcCharges(kernel)[key1];
        Assert.True(kernel.Budgets.QuarantineLease(sender.Process, retained.Reservation).IsSuccess);
        Assert.True(kernel.Receive(receiver, first.Right).IsSuccess);
        Assert.Equal(retained, IpcCharges(kernel)[key1]);
        Assert.Equal(KernelError.BudgetExceeded, kernel.Send(sender.Process, receiver, second.Left, 1).Error);
        Assert.True(kernel.Receive(receiver, second.Right).IsSuccess);
        Assert.False(IpcCharges(kernel).ContainsKey(key2));
        ChannelEnvelope sent3 = kernel.Send(sender.Process, receiver, second.Left, 1).Value!;
        Assert.NotEqual(sent2.Sequence, sent3.Sequence);
        Assert.True(kernel.Channels.Close(second.Left).IsSuccess);
        Assert.Equal(retained, Assert.Single(IpcCharges(kernel)).Value);
        Assert.True(kernel.Channels.Close(first.Left).IsSuccess);
        Assert.False(kernel.Receive(receiver, first.Right).IsSuccess);
        Assert.Equal(retained, Assert.Single(IpcCharges(kernel)).Value);
        Assert.True(kernel.TerminateProcess(receiver).IsSuccess);
        ProcessHandle replacement = TestFixtures.Create(kernel, 574, 5705, generation: 2).Handle;
        Assert.False(kernel.ReleaseBudget(replacement, retained.Reservation).IsSuccess);
        Assert.Equal(retained, Assert.Single(IpcCharges(kernel)).Value);
        Assert.Equal(1UL, Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingIpcReceiveAndReceiverTeardownKeepQuantitativeDecisionWithBudgetOwner(bool quarantine)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot sender = Admit(kernel, "ipc-overlap", 575, 5705,
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!;
        ProcessHandle receiver = TestFixtures.Create(kernel, 576, 5706).Handle;
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel = kernel.CreateChannel(sender.Process, receiver, IpcBudgetProtocol(), 2).Value!;
        ChannelEnvelope sent = kernel.Send(sender.Process, receiver, channel.Left, 1).Value!;
        (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation) charge = IpcCharges(kernel)[(channel.Left.ChannelId, sent.Sequence)];
        if (quarantine) Assert.True(kernel.Budgets.QuarantineLease(sender.Process, charge.Reservation).IsSuccess);
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        Task<KernelResult<ChannelEnvelope>> receive = Task.Run(() => { start.Wait(); return kernel.Receive(receiver, channel.Right); });
        Task<KernelResult> teardown = Task.Run(() => { start.Wait(); return kernel.TerminateProcess(receiver); });
        start.Set();
        await receive;
        Assert.True((await teardown).IsSuccess);
        Assert.Equal(quarantine ? BudgetReservationState.Quarantined : BudgetReservationState.Released,
            kernel.QueryBudget(charge.Reservation).Value!.State);
        if (quarantine) Assert.Equal(charge, Assert.Single(IpcCharges(kernel)).Value);
        else Assert.Empty(IpcCharges(kernel));
        Assert.Equal(quarantine ? 1UL : 0UL,
            Usage(kernel.QueryBudget(sender.ProcessBudget).Value!, ServiceBudgetDimension.IpcMessages).Used);
    }

    private static ProtocolDefinitionV1 IpcBudgetProtocol() => new("BudgetIpc", "budget-ipc", "Idle", null,
        [new(1, "Ping")], [new(1, "Idle", "Idle")]);

    private static Dictionary<(ChannelId Channel, ulong Sequence), (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation)> IpcCharges(RuntimeKernel kernel) =>
        (Dictionary<(ChannelId Channel, ulong Sequence), (ProcessHandle Sender, ProcessHandle Receiver, BudgetReservationHandle Reservation)>)typeof(RuntimeKernel)
            .GetField("_ipcBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, false, 3)]
    [InlineData(false, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(false, true, 2)]
    [InlineData(false, true, 3)]
    [InlineData(true, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, false, 3)]
    [InlineData(true, true, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, true, 2)]
    [InlineData(true, true, 3)]
    public void FinalDomainReclaimReleasesExactCohortChargesAndPreservesRefusedAccounting(bool scalar, bool reverseExit, int fault)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot left = Admit(kernel, "cohort-left", 561, 5601,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        ComponentLifecycleSnapshot right = Admit(kernel, "cohort-right", 562, 5601,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        ComponentLifecycleSnapshot outside = Admit(kernel, "cohort-outside", 563, 5602,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        RegionHandle leftHandle = scalar ? kernel.AllocateRegion(left.Process, 7).Value!.Handle
            : kernel.AllocateBuffer<byte>(left.Process, 4).Value!.Handle;
        RegionHandle rightHandle = scalar ? kernel.AllocateRegion(right.Process, 7).Value!.Handle
            : kernel.AllocateBuffer<byte>(right.Process, 4).Value!.Handle;
        RegionHandle outsideHandle = kernel.AllocateBuffer<byte>(outside.Process, 4).Value!.Handle;
        Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> charges = RegionCharges(kernel);
        (ProcessHandle Owner, BudgetReservationHandle Reservation) leftCharge = charges[leftHandle];
        (ProcessHandle Owner, BudgetReservationHandle Reservation) rightCharge = charges[rightHandle];
        (ProcessHandle Owner, BudgetReservationHandle Reservation) outsideCharge = charges[outsideHandle];
        if (fault == 1 || fault == 3)
        {
            Assert.True(kernel.Budgets.QuarantineLease(left.Process, leftCharge.Reservation).IsSuccess);
            if (fault == 3) Assert.True(kernel.Budgets.ReconcileLease(left.Process, leftCharge.Reservation).IsSuccess);
        }
        else if (fault == 2)
        {
            Assert.True(kernel.Budgets.BindLease(left.Process, leftCharge.Reservation).IsSuccess);
            Assert.True(kernel.Budgets.BeginConsumption(left.Process, leftCharge.Reservation).IsSuccess);
        }
        BudgetReservationState leftState = kernel.QueryBudget(leftCharge.Reservation).Value!.State;
        ComponentLifecycleSnapshot first = reverseExit ? right : left;
        ComponentLifecycleSnapshot last = reverseExit ? left : right;
        Assert.True(kernel.TerminateProcess(first.Process).IsSuccess);
        Assert.True(kernel.Domains.Contains(new(5601)));
        Assert.Equal(KernelError.StaleHandle, kernel.Processes.Resolve(first.Process).Error);
        Assert.Equal(leftCharge, charges[leftHandle]);
        Assert.Equal(rightCharge, charges[rightHandle]);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == leftHandle).State);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(left.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(right.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(kernel.TerminateProcess(last.Process).IsSuccess);
        Assert.False(kernel.Domains.Contains(new(5601)));
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == leftHandle).State);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == rightHandle).State);
        Assert.False(charges.ContainsKey(rightHandle));
        Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(rightCharge.Reservation).Value!.State);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(right.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (fault == 0)
        {
            Assert.False(charges.ContainsKey(leftHandle));
            Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(leftCharge.Reservation).Value!.State);
            Assert.Equal(0UL, Usage(kernel.QueryBudget(left.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        else
        {
            Assert.Equal(leftCharge, charges[leftHandle]);
            Assert.Equal(leftState, kernel.QueryBudget(leftCharge.Reservation).Value!.State);
            Assert.Equal(4UL, Usage(kernel.QueryBudget(left.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        }
        Assert.Equal(outsideCharge, charges[outsideHandle]);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == outsideHandle).State);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(outside.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CohortReclaimCannotSubstituteTransferredRegionGenerationOrReusedProcessId(bool scalar)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "cohort-transfer-source", 564, 5604,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        ComponentLifecycleSnapshot sibling = Admit(kernel, "cohort-transfer-sibling", 565, 5604,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        OwnedBuffer<byte>? buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        OwnedRegion<int>? region = scalar ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        RegionHandle old = scalar ? region!.Handle : buffer!.Handle;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge = RegionCharges(kernel)[old];
        Assert.True(kernel.Budgets.QuarantineLease(source.Process, charge.Reservation).IsSuccess);
        RegionHandle movedHandle = scalar
            ? kernel.TransferRegion(source.Process, sibling.Process, region!).Value!.Handle
            : kernel.TransferRegion(source.Process, sibling.Process, buffer!).Value!.Handle;
        Assert.Equal(old.RegionId, movedHandle.RegionId);
        Assert.NotEqual(old.Generation, movedHandle.Generation);
        (ProcessHandle Owner, BudgetReservationHandle Reservation) movedCharge = RegionCharges(kernel)[movedHandle];
        Assert.Equal(charge, RegionCharges(kernel)[old]);
        Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        // The domain survives but the process generation does not. A reused PID
        // must not resolve the retired generation's accounting or Region authority.
        ProcessHandle replacement = TestFixtures.Create(kernel, 564, 5605, generation: 2).Handle;
        ProcessHandle admin = TestFixtures.Create(kernel, 566, 5606).Handle;
        CapabilityId cap = kernel.MintCapability(new(5606), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        BudgetAccountHandle budget = kernel.AdmitProcessBudget(admin, cap, replacement, "cohort-replacement",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!.ProcessBudget;
        OwnedBuffer<byte> fresh = kernel.AllocateBuffer<byte>(replacement, 4).Value!;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) freshCharge = RegionCharges(kernel)[fresh.Handle];
        Assert.False(kernel.ReleaseBudget(replacement, charge.Reservation).IsSuccess);
        Assert.True(kernel.TerminateProcess(sibling.Process).IsSuccess);
        Assert.False(RegionCharges(kernel).ContainsKey(movedHandle));
        Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(movedCharge.Reservation).Value!.State);
        Assert.Equal(charge, RegionCharges(kernel)[old]);
        Assert.Equal(freshCharge, RegionCharges(kernel)[fresh.Handle]);
        Assert.Equal(BudgetReservationState.Quarantined, kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(budget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(RegionState.Owned, kernel.Regions.Snapshot().Single(r => r.Handle == fresh.Handle).State);
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(false, 0, 1)]
    [InlineData(false, 0, 2)]
    [InlineData(false, 1, 0)]
    [InlineData(false, 1, 1)]
    [InlineData(false, 1, 2)]
    [InlineData(false, 2, 0)]
    [InlineData(false, 2, 1)]
    [InlineData(false, 2, 2)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 0, 1)]
    [InlineData(true, 0, 2)]
    [InlineData(true, 1, 0)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 1, 2)]
    [InlineData(true, 2, 0)]
    [InlineData(true, 2, 1)]
    [InlineData(true, 2, 2)]
    public void RefusedRegionAccountingReleaseRetainsEveryExactGeneration(bool scalar, int path, int fault)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "retained-source", 551, 5501,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        ComponentLifecycleSnapshot target = Admit(kernel, "retained-target", 552, 5502,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        OwnedBuffer<byte>? buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        OwnedRegion<int>? region = scalar ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        RegionHandle old = scalar ? region!.Handle : buffer!.Handle;
        Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> associations = RegionCharges(kernel);
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge = associations[old];
        if (fault == 0)
        {
            Assert.True(kernel.Budgets.BindLease(source.Process, charge.Reservation).IsSuccess);
            Assert.True(kernel.Budgets.BeginConsumption(source.Process, charge.Reservation).IsSuccess);
        }
        else
        {
            Assert.True(kernel.Budgets.QuarantineLease(source.Process, charge.Reservation).IsSuccess);
            if (fault == 2) Assert.True(kernel.Budgets.ReconcileLease(source.Process, charge.Reservation).IsSuccess);
        }
        BudgetReservationSnapshot before = kernel.QueryBudget(charge.Reservation).Value!;
        ProcessHandle stale = source.Process with { Generation = source.Process.Generation + 1 };
        Assert.False((scalar ? kernel.ReleaseRegion(stale, region!) : kernel.ReleaseRegion(stale, buffer!)).IsSuccess);
        Assert.Equal(charge, associations[old]);
        if (path == 0)
            Assert.True((scalar ? kernel.ReleaseRegion(source.Process, region!) : kernel.ReleaseRegion(source.Process, buffer!)).IsSuccess);
        else if (path == 1)
            Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        else
        {
            RegionHandle movedHandle;
            if (scalar)
            {
                KernelResult<OwnedRegion<int>> moved = kernel.TransferRegion(source.Process, target.Process, region!);
                Assert.True(moved.IsSuccess, moved.Message);
                movedHandle = moved.Value!.Handle;
                Assert.Equal(charge, associations[old]);
                Assert.True(kernel.ReleaseRegion(target.Process, moved.Value).IsSuccess);
            }
            else
            {
                KernelResult<OwnedBuffer<byte>> moved = kernel.TransferRegion(source.Process, target.Process, buffer!);
                Assert.True(moved.IsSuccess, moved.Message);
                movedHandle = moved.Value!.Handle;
                Assert.Equal(charge, associations[old]);
                Assert.True(kernel.ReleaseRegion(target.Process, moved.Value).IsSuccess);
            }
            Assert.Equal(old.RegionId, movedHandle.RegionId);
            Assert.NotEqual(old.Generation, movedHandle.Generation);
            Assert.False(associations.ContainsKey(movedHandle));
            Assert.Equal(0UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.False((scalar ? kernel.ReleaseRegion(source.Process, region!) : kernel.ReleaseRegion(source.Process, buffer!)).IsSuccess);
            Assert.True(kernel.TerminateProcess(source.Process).IsSuccess);
        }
        Assert.Equal(charge, Assert.Single(associations).Value);
        Assert.Equal(before.State, kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.False(kernel.ReleaseBudget(source.Process, charge.Reservation).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulRegionTransferRoutesOnlyNewGenerationCharge(bool scalar)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "transfer-source", 553, 5503,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        ComponentLifecycleSnapshot target = Admit(kernel, "transfer-target", 554, 5504,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        RegionHandle old;
        if (scalar)
        {
            OwnedRegion<int> region = kernel.AllocateRegion(source.Process, 7).Value!;
            old = region.Handle;
            OwnedRegion<int> moved = kernel.TransferRegion(source.Process, target.Process, region).Value!;
            Assert.False(RegionCharges(kernel).ContainsKey(old));
            Assert.Equal(target.Process, RegionCharges(kernel)[moved.Handle].Owner);
            Assert.True(kernel.ReleaseRegion(target.Process, moved).IsSuccess);
            Assert.False(kernel.ReleaseRegion(source.Process, region).IsSuccess);
        }
        else
        {
            OwnedBuffer<byte> buffer = kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
            old = buffer.Handle;
            OwnedBuffer<byte> moved = kernel.TransferRegion(source.Process, target.Process, buffer).Value!;
            Assert.False(RegionCharges(kernel).ContainsKey(old));
            Assert.Equal(target.Process, RegionCharges(kernel)[moved.Handle].Owner);
            Assert.True(kernel.ReleaseRegion(target.Process, moved).IsSuccess);
            Assert.False(kernel.ReleaseRegion(source.Process, buffer).IsSuccess);
        }
        Assert.Empty(RegionCharges(kernel));
        Assert.Equal(0UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRegionTransferLeavesSourceAssociationAndRefundsOnlyProvisionalTarget(bool scalar)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot source = Admit(kernel, "denied-source", 555, 5505,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        ComponentLifecycleSnapshot target = Admit(kernel, "denied-target", 556, 5506,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        OwnedBuffer<byte>? buffer = scalar ? null : kernel.AllocateBuffer<byte>(source.Process, 4).Value!;
        OwnedRegion<int>? region = scalar ? kernel.AllocateRegion(source.Process, 7).Value! : null;
        RegionHandle handle = scalar ? region!.Handle : buffer!.Handle;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge = RegionCharges(kernel)[handle];
        RegionUseDescriptor use = kernel.AcquireRegionUse(source.Process, handle, RegionUseMode.ExclusiveWrite, new(0, 4)).Value!;
        KernelError denied = scalar ? kernel.TransferRegion(source.Process, target.Process, region!).Error
            : kernel.TransferRegion(source.Process, target.Process, buffer!).Error;
        Assert.Equal(KernelError.RegionUseConflict, denied);
        Assert.Equal(charge, Assert.Single(RegionCharges(kernel)).Value);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(source.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(target.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        Assert.True(kernel.ReleaseRegionUse(source.Process, use.Handle).IsSuccess);
        Assert.True((scalar ? kernel.ReleaseRegion(source.Process, region!) : kernel.ReleaseRegion(source.Process, buffer!)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingRegionReleasesCannotRemoveOrRefundAnotherGeneration(bool scalar)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot owner = Admit(kernel, "overlap-region", 557, 5507,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        OwnedBuffer<byte>? buffer = scalar ? null : kernel.AllocateBuffer<byte>(owner.Process, 4).Value!;
        OwnedRegion<int>? region = scalar ? kernel.AllocateRegion(owner.Process, 7).Value! : null;
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        Task<KernelResult>[] calls = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return scalar ? kernel.ReleaseRegion(owner.Process, region!) : kernel.ReleaseRegion(owner.Process, buffer!);
        })).ToArray();
        start.Set();
        KernelResult[] results = await Task.WhenAll(calls);
        Assert.Single(results, r => r.IsSuccess);
        Assert.Empty(RegionCharges(kernel));
        Assert.Equal(0UL, Usage(kernel.QueryBudget(owner.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        OwnedBuffer<byte> fresh = kernel.AllocateBuffer<byte>(owner.Process, 4).Value!;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge = RegionCharges(kernel)[fresh.Handle];
        Assert.False((scalar ? kernel.ReleaseRegion(owner.Process, region!) : kernel.ReleaseRegion(owner.Process, buffer!)).IsSuccess);
        Assert.Equal(charge, Assert.Single(RegionCharges(kernel)).Value);
        Assert.Equal(4UL, Usage(kernel.QueryBudget(owner.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void CohortRegionReleaseRoutesOriginalPayerAndRetainsRefusedCharge(bool scalar, bool quarantine)
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot payer = Admit(kernel, "cohort-release-payer", 574, 5704,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        ComponentLifecycleSnapshot peer = Admit(kernel, "cohort-release-peer", 575, 5704,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 4)]).Value!;
        OwnedBuffer<byte>? buffer = scalar ? null : kernel.AllocateBuffer<byte>(payer.Process, 4).Value!;
        OwnedRegion<int>? region = scalar ? kernel.AllocateRegion(payer.Process, 7).Value! : null;
        RegionHandle handle = scalar ? region!.Handle : buffer!.Handle;
        (ProcessHandle Owner, BudgetReservationHandle Reservation) charge = RegionCharges(kernel)[handle];
        Assert.Equal(payer.Process, charge.Owner);
        if (quarantine) Assert.True(kernel.Budgets.QuarantineLease(payer.Process, charge.Reservation).IsSuccess);
        KernelResult released = scalar ? kernel.ReleaseRegion(peer.Process, region!) : kernel.ReleaseRegion(peer.Process, buffer!);
        Assert.True(released.IsSuccess, released.Message);
        Assert.Equal(RegionState.Released, kernel.Regions.Snapshot().Single(r => r.Handle == handle).State);
        Assert.False((scalar ? kernel.ReleaseRegion(peer.Process, region!) : kernel.ReleaseRegion(peer.Process, buffer!)).IsSuccess);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(peer.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
        if (quarantine)
        {
            Assert.Equal(charge, Assert.Single(RegionCharges(kernel)).Value);
            Assert.Equal(BudgetReservationState.Quarantined, kernel.QueryBudget(charge.Reservation).Value!.State);
            Assert.Equal(4UL, Usage(kernel.QueryBudget(payer.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
            Assert.False(kernel.ReleaseBudget(peer.Process, charge.Reservation).IsSuccess);
            Assert.True(kernel.Budgets.ReconcileLease(payer.Process, charge.Reservation).IsSuccess);
            Assert.False(kernel.ReleaseBudget(payer.Process, charge.Reservation).IsSuccess);
            Assert.Equal(BudgetReservationState.Reconciled, kernel.QueryBudget(charge.Reservation).Value!.State);
            Assert.Equal(charge, Assert.Single(RegionCharges(kernel)).Value);
            Assert.Equal(4UL, Usage(kernel.QueryBudget(payer.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
            return; // Region release does not manufacture quantitative settlement.
        }
        Assert.Empty(RegionCharges(kernel));
        Assert.Equal(BudgetReservationState.Released, kernel.QueryBudget(charge.Reservation).Value!.State);
        Assert.Equal(0UL, Usage(kernel.QueryBudget(payer.ProcessBudget).Value!, ServiceBudgetDimension.OwnedMemoryBytes).Used);
    }

    private static Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)> RegionCharges(RuntimeKernel kernel) =>
        (Dictionary<RegionHandle, (ProcessHandle Owner, BudgetReservationHandle Reservation)>)typeof(RuntimeKernel)
            .GetField("_regionBudgetReservations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;

    [Fact]
    public void ChildServiceBudgetCannotExceedConfiguredSystemParent()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ProcessHandle admin = TestFixtures.Create(kernel, 501, 5001).Handle;
        CapabilityId capability = kernel.MintCapability(new(5001), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.ConfigureSystemBudget(admin, capability,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 64)]).IsSuccess);

        KernelResult<ComponentLifecycleSnapshot> denied = Admit(kernel, "over-parent", 502, 5002,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 128)]);

        Assert.Equal(KernelError.BudgetExceeded, denied.Error);
        Assert.Equal(KernelError.ProcessNotFound, kernel.Processes.Resolve(new(new(502), 1)).Error);
    }

    [Fact]
    public async Task ConcurrentReservationsCannotOvercommitAndStaleReleaseCannotFreeCurrentCharge()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot component = Admit(kernel, "race", 503, 5003,
            [new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!;
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        Task<KernelResult<BudgetReservationSnapshot>>[] attempts = Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            start.Wait();
            return kernel.ReserveBudget(component.Process,
                [new(ServiceBudgetDimension.ExternalOperations, 1)],
                BudgetReservationLifetime.ExternalEffect,
                index == 0 ? AdmissionQosHint.LatencySensitive : AdmissionQosHint.Background);
        })).ToArray();
        start.Set();
        KernelResult<BudgetReservationSnapshot>[] results = await Task.WhenAll(attempts);

        BudgetReservationSnapshot winner = Assert.Single(results, result => result.IsSuccess).Value!;
        Assert.Single(results, result => result.Error == KernelError.BudgetExceeded);
        Assert.True(kernel.ReleaseBudget(component.Process, winner.Reservation).IsSuccess);
        BudgetReservationSnapshot current = kernel.ReserveBudget(component.Process,
            [new(ServiceBudgetDimension.ExternalOperations, 1)],
            BudgetReservationLifetime.ExternalEffect).Value!;
        BudgetReservationHandle stale = current.Reservation with { Generation = new(current.Reservation.Generation.Value + 1) };

        Assert.Equal(BudgetReservationState.Stale,
            kernel.ReleaseBudget(component.Process, stale).Value!.State);
        Assert.Equal(KernelError.BudgetExceeded,
            kernel.ReserveBudget(component.Process,
                [new(ServiceBudgetDimension.ExternalOperations, 1)],
                BudgetReservationLifetime.ExternalEffect,
                AdmissionQosHint.ThroughputOriented).Error);
        Assert.True(kernel.ReleaseBudget(component.Process, current.Reservation).IsSuccess);
    }

    [Fact]
    public void OwnedMemoryBudgetGatesAllocationAndRecoversOnExactRelease()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot component = Admit(kernel, "memory", 504, 5004,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 8)]).Value!;
        KernelResult<OwnedBuffer<byte>> first = kernel.AllocateBuffer<byte>(component.Process, 8);
        Assert.True(first.IsSuccess, first.Message);

        Assert.Equal(KernelError.BudgetExceeded,
            kernel.AllocateBuffer<byte>(component.Process, 1).Error);
        Assert.True(kernel.ReleaseRegion(component.Process, first.Value!).IsSuccess);
        Assert.True(kernel.AllocateBuffer<byte>(component.Process, 8).IsSuccess);
    }

    [Fact]
    public void IpcMessageAndByteBudgetsReleaseWhenQueueLifetimeEnds()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot sender = Admit(kernel, "ipc", 505, 5005,
            [new(ServiceBudgetDimension.IpcMessages, 1), new(ServiceBudgetDimension.IpcBytes, 32)]).Value!;
        ProcessHandle receiver = TestFixtures.Create(kernel, 506, 5006).Handle;
        ProtocolDefinitionV1 protocol = new ProtocolDefinitionV1("BudgetIpc", "budget-ipc", "Idle", null,
            [new(1, "Ping")], [new(1, "Idle", "Idle")]);
        (ChannelEndpointHandle Left, ChannelEndpointHandle Right) channel = kernel.CreateChannel(sender.Process, receiver, protocol, 4).Value;

        Assert.True(kernel.Send(sender.Process, receiver, channel.Left, 1).IsSuccess);
        Assert.Equal(KernelError.BudgetExceeded,
            kernel.Send(sender.Process, receiver, channel.Left, 1).Error);
        Assert.True(kernel.Receive(receiver, channel.Right).IsSuccess);
        Assert.True(kernel.Send(sender.Process, receiver, channel.Left, 1).IsSuccess);
    }

    [Fact]
    public void MappedMemoryBudgetIsIndependentAndReleasesOnlyAfterMappingClosure()
    {
        RuntimeKernel kernel = new RuntimeKernel(new HostPlatformAuthorityProvider());
        ProcessHandle admin = TestFixtures.Create(kernel, 507, 5007).Handle;
        ProcessHandle owner = TestFixtures.Create(kernel, 509, 5009).Handle;
        CapabilityId budgetCapability = kernel.MintCapability(new(5007), admin, ResourceKind.KernelService,
            CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
        Assert.True(kernel.AdmitProcessBudget(admin, budgetCapability, owner, "mapping",
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 256), new(ServiceBudgetDimension.PinnedMappedMemoryBytes, 64)]).IsSuccess);
        PlatformDomainBinding binding = kernel.BindPlatformDomain(owner).Value!;
        OwnedBuffer<byte> first = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        OwnedBuffer<byte> second = kernel.AllocateBuffer<byte>(owner, 64).Value!;
        CapabilityId firstCapability = kernel.MintCapability(new(5009), owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(first.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        CapabilityId secondCapability = kernel.MintCapability(new(5009), owner,
            ResourceKind.MemoryRegion, CapabilityResourceIds.MemoryRegion(second.Handle.RegionId),
            CapabilityRights.Map | CapabilityRights.Read).Value!.CapabilityId;
        KernelResult<PlatformRegionMapping> mapped = kernel.MapPlatformOwnedRegion(owner, binding, firstCapability,
            first.Handle, PlatformMemoryAccess.Read);
        Assert.True(mapped.IsSuccess, mapped.Message);

        Assert.Equal(KernelError.BudgetExceeded,
            kernel.MapPlatformOwnedRegion(owner, binding, secondCapability,
                second.Handle, PlatformMemoryAccess.Read).Error);
        Assert.True(kernel.RevokePlatformRegionMapping(owner, mapped.Value!).IsSuccess);
        Assert.True(kernel.MapPlatformOwnedRegion(owner, binding, secondCapability,
            second.Handle, PlatformMemoryAccess.Read).IsSuccess);
    }

    [Fact]
    public void CrashWithSubmittedExternalEffectKeepsChargeUntilExactClosure()
    {
        RuntimeKernel kernel = new RuntimeKernel();
        ComponentLifecycleSnapshot component = Admit(kernel, "external", 508, 5008,
            [new(ServiceBudgetDimension.OwnedMemoryBytes, 16), new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!;
        OwnedBuffer<byte> input = kernel.AllocateBuffer<byte>(component.Process, 8).Value!;
        OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(component.Process, 8).Value!;
        OperationPreparation prepared = kernel.PrepareExternalOperation(component.Process,
            [new(input.Handle, RegionUseMode.ReadOnly, new(0, 8)), new(output.Handle, RegionUseMode.StagedOutput, new(0, 8))],
            ExternalVisibilityRequirement.PublicationFence, ExternalPublicationPolicy.Staged).Value!;
        OperationDependencySnapshot dependencies = new OperationDependencySnapshot(1, 1);
        Assert.True(kernel.AdmitExternalOperation(component.Process, prepared.Operation, dependencies).IsSuccess);
        OperationBinding binding = kernel.RecordExternalOperationSubmission(component.Process, prepared.Operation, dependencies).Value!;

        KernelResult<ComponentLifecycleSnapshot> faulted = kernel.FaultComponent(component.Identity);
        Assert.True(faulted.IsSuccess, faulted.Message);
        BudgetAccountSnapshot charged = kernel.QueryBudget(component.ProcessBudget).Value!;
        Assert.Equal(BudgetPressureState.PinnedByExternalEffect, charged.Pressure);
        Assert.Equal(1UL, Usage(charged, ServiceBudgetDimension.ExternalOperations).Used);

        Assert.True(kernel.RecordExternalOperationCompletion(component.Process,
            new(binding, ExternalOperationCompletionDisposition.Cancelled)).IsSuccess);
        Assert.True(kernel.ObserveComponentTeardown(component.Identity).IsSuccess);
        BudgetAccountSnapshot closurePending = kernel.QueryBudget(component.ProcessBudget).Value!;
        Assert.Equal(BudgetPressureState.PinnedByExternalEffect, closurePending.Pressure);
        Assert.Equal(1UL, Usage(closurePending, ServiceBudgetDimension.ExternalOperations).Used);

        Assert.True(kernel.ReleaseExternalOperation(component.Process, prepared.Operation, new(true, false)).IsSuccess);
        Assert.True(kernel.ObserveComponentTeardown(component.Identity).IsSuccess);
        BudgetAccountSnapshot closed = kernel.QueryBudget(component.ProcessBudget).Value!;
        Assert.Equal(0UL, Usage(closed, ServiceBudgetDimension.ExternalOperations).Used);
    }

    [Fact]
    public void BudgetDtosAndQosHintsAreNotAuthority()
    {
        BudgetReservationSnapshot observation = new BudgetReservationSnapshot(default, default, default, [],
            BudgetReservationLifetime.LocalResource, AdmissionQosHint.LatencySensitive,
            BudgetReservationState.Active);
        Assert.False(observation.AuthorizesEffect);
        Assert.False(observation.AuthorizesReclaim);
        Assert.False(new BudgetAccountSnapshot(default, BudgetAccountLevel.System, null, "system", [],
            BudgetPressureState.Normal).MaterializesAuthority);
        string[] vocabulary = typeof(AdmissionQosHint).GetEnumNames();
        Assert.DoesNotContain(vocabulary, name => name.Contains("Cxl", StringComparison.OrdinalIgnoreCase) ||
                                                   name.Contains("Lane", StringComparison.OrdinalIgnoreCase) ||
                                                   name.Contains("Hybrid", StringComparison.OrdinalIgnoreCase));
    }

    private static BudgetUsage Usage(BudgetAccountSnapshot account, ServiceBudgetDimension dimension) =>
        Assert.Single(account.Usage, usage => usage.Dimension == dimension);

    private static KernelResult<ComponentLifecycleSnapshot> Admit(
        RuntimeKernel kernel, string name, ulong processId, ulong domainId,
        IReadOnlyList<ServiceBudgetRequestV1> budgets)
    {
        byte[] image = [(byte)(processId & 0xff), 0x05];
        ServiceManifestV1 manifest = new ServiceManifestV1(
            new(name), new("1"), Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            TestFixtures.Manifest(processId, domainId, identity: name), budgetRequests: budgets);
        return kernel.AdmitComponent(new(manifest, image));
    }
}
