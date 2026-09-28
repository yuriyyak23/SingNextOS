using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public readonly record struct IpcScatterGatherCopySegment(
    OwnedBuffer<byte> Buffer,
    long Offset,
    int Length,
    IpcSegmentTransferMode Mode = IpcSegmentTransferMode.CopyRead);

public sealed partial class RuntimeKernel
{
    public KernelResult<IpcV2SendReceipt> SendCopyV2(
        ProcessHandle sender,
        ProcessHandle receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        IpcCopyPayload payload,
        IReadOnlyCollection<CapabilityId>? capabilities = null,
        AdmissionQosHint qosHint = AdmissionQosHint.None,
        TraceCausalContext? traceContext = null,
        CancellationScopeHandle? cancellationScope = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var semantic = Channels.ValidateV2Semantic(endpoint, messageId, IpcTransferSemantic.Copy);
        if (!semantic.IsSuccess) return KernelResult<IpcV2SendReceipt>.Fail(semantic.Error, semantic.Message!);
        var temporal = PreflightIpcV2Cancellation(sender, endpoint, messageId, cancellationScope);
        if (!temporal.IsSuccess) return KernelResult<IpcV2SendReceipt>.Fail(temporal.Error, temporal.Message!);
        var sent = Send(sender, receiver, endpoint, messageId, payload.Copy(), capabilities, qosHint, traceContext);
        CompleteIpcV2Cancellation(sender, cancellationScope, sent.IsSuccess);
        return Receipt(sent, IpcTransferSemantic.Copy, IpcOwnershipDisposition.NotApplicable,
            payload.PayloadSize <= 256 ? IpcTransportPath.SmallCopy : IpcTransportPath.BoundedCopy, traceContext);
    }

    public KernelResult<IpcV2SendReceipt> SendMoveV2<T>(
        ProcessHandle sender,
        ProcessHandle receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        OwnedBuffer<T> payload,
        IReadOnlyCollection<CapabilityId>? capabilities = null,
        AdmissionQosHint qosHint = AdmissionQosHint.None,
        TraceCausalContext? traceContext = null,
        CancellationScopeHandle? cancellationScope = null) where T : unmanaged
    {
        var semantic = Channels.ValidateV2Semantic(endpoint, messageId, IpcTransferSemantic.Move);
        if (!semantic.IsSuccess) return KernelResult<IpcV2SendReceipt>.Fail(semantic.Error, semantic.Message!);
        var temporal = PreflightIpcV2Cancellation(sender, endpoint, messageId, cancellationScope);
        if (!temporal.IsSuccess) return KernelResult<IpcV2SendReceipt>.Fail(temporal.Error, temporal.Message!);
        var sent = Send(sender, receiver, endpoint, messageId, payload, capabilities, qosHint, traceContext);
        CompleteIpcV2Cancellation(sender, cancellationScope, sent.IsSuccess);
        return Receipt(sent, IpcTransferSemantic.Move, IpcOwnershipDisposition.ReceiverOwned,
            IpcTransportPath.OwnershipTransfer, traceContext);
    }

    public KernelResult<IpcV2SendReceipt> SendBorrowReadV2<T>(
        ProcessHandle sender,
        ProcessHandle receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        OwnedBuffer<T> payload,
        IReadOnlyCollection<CapabilityId>? capabilities = null,
        AdmissionQosHint qosHint = AdmissionQosHint.None,
        TraceCausalContext? traceContext = null,
        CancellationScopeHandle? cancellationScope = null) where T : unmanaged
    {
        var semantic = Channels.ValidateV2Semantic(endpoint, messageId, IpcTransferSemantic.BorrowRead);
        if (!semantic.IsSuccess) return KernelResult<IpcV2SendReceipt>.Fail(semantic.Error, semantic.Message!);
        var temporal = PreflightIpcV2Cancellation(sender, endpoint, messageId, cancellationScope);
        if (!temporal.IsSuccess) return KernelResult<IpcV2SendReceipt>.Fail(temporal.Error, temporal.Message!);
        var sent = Send(sender, receiver, endpoint, messageId, payload, capabilities, qosHint, traceContext);
        CompleteIpcV2Cancellation(sender, cancellationScope, sent.IsSuccess);
        return Receipt(sent, IpcTransferSemantic.BorrowRead, IpcOwnershipDisposition.BorrowActive,
            IpcTransportPath.ReadOnlyBorrow, traceContext);
    }

    public KernelResult<IpcV2SendReceipt> SendScatterGatherCopyV2(
        ProcessHandle sender,
        ProcessHandle receiver,
        ChannelEndpointHandle endpoint,
        uint messageId,
        IReadOnlyList<IpcScatterGatherCopySegment> segments,
        IpcScatterGatherOverlapPolicy overlapPolicy = IpcScatterGatherOverlapPolicy.RejectOverlap,
        IReadOnlyCollection<CapabilityId>? capabilities = null,
        AdmissionQosHint qosHint = AdmissionQosHint.None,
        TraceCausalContext? traceContext = null,
        CancellationScopeHandle? cancellationScope = null)
    {
        if (segments.Count == 0 || segments.Count > IpcV2Contract.MaximumScatterGatherSegments || !Enum.IsDefined(overlapPolicy))
            return KernelResult<IpcV2SendReceipt>.Fail(KernelError.InvalidMessage, "Scatter/gather segment count or overlap policy is invalid.");
        var semantic = Channels.ValidateV2Semantic(endpoint, messageId, IpcTransferSemantic.ScatterGatherCopy);
        if (!semantic.IsSuccess) return KernelResult<IpcV2SendReceipt>.Fail(semantic.Error, semantic.Message!);
        var temporal = PreflightIpcV2Cancellation(sender, endpoint, messageId, cancellationScope);
        if (!temporal.IsSuccess) return KernelResult<IpcV2SendReceipt>.Fail(temporal.Error, temporal.Message!);
        var source = Processes.Resolve(sender);
        if (!source.IsSuccess) return KernelResult<IpcV2SendReceipt>.Fail(source.Error, source.Message!);
        var copied = new List<ReadOnlyMemory<byte>>(segments.Count);
        var ranges = new Dictionary<RegionId, List<(long Start, long End)>>();
        long aggregate = 0;
        foreach (var segment in segments)
        {
            if (segment.Buffer is null)
                return KernelResult<IpcV2SendReceipt>.Fail(KernelError.InvalidMessage, "Scatter/gather source buffer is required.");
            if (segment.Mode != IpcSegmentTransferMode.CopyRead)
                return KernelResult<IpcV2SendReceipt>.Fail(KernelError.PlatformUnsupported, "This bounded scatter/gather path supports CopyRead segments only; MOVE and borrow retain their exact single-payload APIs.");
            var valid = Regions.Validate(segment.Buffer.Handle, new(source.Value!.DomainId, sender.Generation));
            if (!valid.IsSuccess) return KernelResult<IpcV2SendReceipt>.Fail(valid.Error, valid.Message!);
            if (segment.Offset < 0 || segment.Length <= 0 || segment.Offset > segment.Buffer.Length || segment.Length > segment.Buffer.Length - segment.Offset)
                return KernelResult<IpcV2SendReceipt>.Fail(KernelError.InvalidMessage, "Scatter/gather segment range is outside its source buffer.");
            var end = checked(segment.Offset + segment.Length);
            var list = ranges.GetValueOrDefault(segment.Buffer.Handle.RegionId);
            if (list is null) ranges.Add(segment.Buffer.Handle.RegionId, list = []);
            var overlap = list.Any(range => segment.Offset < range.End && end > range.Start);
            var exact = list.Any(range => segment.Offset == range.Start && end == range.End);
            if (overlap && (overlapPolicy == IpcScatterGatherOverlapPolicy.RejectOverlap || !exact))
                return KernelResult<IpcV2SendReceipt>.Fail(KernelError.InvalidMessage, "Scatter/gather ranges overlap contrary to policy.");
            list.Add((segment.Offset, end));
            if (aggregate > IpcV2Contract.MaximumScatterGatherBytes - segment.Length)
                return KernelResult<IpcV2SendReceipt>.Fail(KernelError.CapacityExhausted, "Scatter/gather aggregate exceeds the bounded contract maximum.");
            aggregate += segment.Length;
            copied.Add(segment.Buffer.Span.Slice(checked((int)segment.Offset), segment.Length).ToArray());
        }
        var payload = new IpcScatterGatherPayload(copied);
        var sent = Send(sender, receiver, endpoint, messageId, payload, capabilities, qosHint, traceContext);
        CompleteIpcV2Cancellation(sender, cancellationScope, sent.IsSuccess);
        return Receipt(sent, IpcTransferSemantic.ScatterGatherCopy, IpcOwnershipDisposition.NotApplicable,
            IpcTransportPath.ScatterGatherCopy, traceContext);
    }

    private static KernelResult<IpcV2SendReceipt> Receipt(
        KernelResult<ChannelEnvelope> sent,
        IpcTransferSemantic semantic,
        IpcOwnershipDisposition ownership,
        IpcTransportPath path,
        TraceCausalContext? context) => sent.IsSuccess
        ? KernelResult<IpcV2SendReceipt>.Ok(new(semantic, ownership, path, sent.Value!, context?.Correlation))
        : KernelResult<IpcV2SendReceipt>.Fail(sent.Error, sent.Message!);

    private KernelResult PreflightIpcV2Cancellation(
        ProcessHandle sender,
        ChannelEndpointHandle endpoint,
        uint messageId,
        CancellationScopeHandle? scope)
    {
        if (scope is not { } exact) return KernelResult.Ok();
        var bound = CancellationScopes.BindConsumer(sender, exact,
            $"ipc-v2:{endpoint.ChannelId.Value}:{endpoint.EndpointId.Value}:{messageId}");
        if (!bound.IsSuccess) return bound;
        var observed = CancellationScopes.Observe(sender, exact);
        if (!observed.IsSuccess) return KernelResult.Fail(observed.Error, observed.Message!);
        if (!observed.Value!.CancellationRequested) return KernelResult.Ok();
        _ = CancellationScopes.RecordDisposition(sender, exact, CancellationDisposition.CancelledBeforeEffect);
        return KernelResult.Fail(KernelError.DeadlineExpired, "IPC v2 cancellation was requested before transfer admission; sender authority is unchanged.");
    }

    private void CompleteIpcV2Cancellation(
        ProcessHandle sender,
        CancellationScopeHandle? scope,
        bool admitted)
    {
        if (scope is not { } exact || !admitted) return;
        var observed = CancellationScopes.Observe(sender, exact);
        if (!observed.IsSuccess) return;
        _ = CancellationScopes.RecordDisposition(sender, exact,
            observed.Value!.CancellationRequested
                ? CancellationDisposition.TooLateEffectMayExist
                : CancellationDisposition.CompletedBeforeCancellation);
    }
}
