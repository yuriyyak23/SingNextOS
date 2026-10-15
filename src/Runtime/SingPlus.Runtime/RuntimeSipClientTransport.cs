using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed class RuntimeSipClientTransport : ISipClientRuntimeTransport
{
    private readonly RuntimeKernel _kernel;
    private readonly ProcessHandle _requester;
    private readonly ProcessHandle? _responder;
    private readonly ChannelEndpointHandle _requesterEndpoint;
    private readonly EndpointSessionHandle? _session;
    private readonly IReadOnlyCollection<CapabilityId>? _capabilities;
    private readonly CancellationToken _defaultCancellationToken;
    private OriginalSessionSendCustody? _pairSendCustody;
    private int _pairInvocationInFlight;

    public bool HasPendingOwnershipPair => Volatile.Read(ref _pairSendCustody) is { } original && _kernel.OriginalSessionSendIsPending(original);
    public bool HasPendingOwnershipSend => HasPendingOwnershipPair;
    public KernelResult WithdrawPendingOwnershipSend() => WithdrawPendingOwnershipPair();
    public KernelResult CleanupPendingOwnershipSend() => CleanupPendingOwnershipPair();

    public KernelResult WithdrawPendingOwnershipPair()
    {
        if(Interlocked.CompareExchange(ref _pairInvocationInFlight,1,0)!=0)
            return KernelResult.Fail(KernelError.RegionUseConflict,"Ownership-pair invocation is in progress.");
        try
        {
            return Volatile.Read(ref _pairSendCustody) is { } original
                ? _kernel.WithdrawOriginalSessionSend(original)
                : KernelResult.Fail(KernelError.ResponseNotPending,"No original ownership-pair send is retained.");
        }
        finally { Volatile.Write(ref _pairInvocationInFlight,0); }
    }

    public KernelResult CleanupPendingOwnershipPair()
    {
        if(Interlocked.CompareExchange(ref _pairInvocationInFlight,1,0)!=0)
            return KernelResult.Fail(KernelError.RegionUseConflict,"Ownership-pair invocation is in progress.");
        try { return Volatile.Read(ref _pairSendCustody) is { } original ? _kernel.CleanupOriginalSessionSend(original) :
            KernelResult.Fail(KernelError.ResponseNotPending,"No original pair send retained."); }
        finally { Volatile.Write(ref _pairInvocationInFlight,0); }
    }

    public RuntimeSipClientTransport(
        RuntimeKernel kernel,
        ProcessHandle requester,
        ProcessHandle responder,
        ChannelEndpointHandle requesterEndpoint,
        IReadOnlyCollection<CapabilityId>? capabilities = null,
        CancellationToken cancellationToken = default)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _requester = requester;
        _responder = responder;
        _requesterEndpoint = requesterEndpoint;
        _capabilities = capabilities;
        _defaultCancellationToken = cancellationToken;
    }

    public RuntimeSipClientTransport(
        RuntimeKernel kernel,
        ProcessHandle caller,
        EndpointSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _requester = caller;
        _session = session;
        _defaultCancellationToken = cancellationToken;
    }

    public ResponseEnvelope Invoke(uint messageId, object? requestPayload = null) =>
        InvokeAsync(messageId, requestPayload, _defaultCancellationToken).AsTask().GetAwaiter().GetResult();

    public ValueTask<ResponseEnvelope> InvokeAsync(uint messageId, object? requestPayload = null) =>
        InvokeAsync(messageId, requestPayload, _defaultCancellationToken);

    public async ValueTask<ResponseEnvelope> InvokeAsync(
        uint messageId,
        object? requestPayload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownership=requestPayload is SingPlus.Sip.ITransferableOwnedPayload;
        if(HasPendingOwnershipSend)
            throw new InvalidOperationException("Original ownership send remains pending; a new send cannot reconcile it.");
        if(ownership && Interlocked.CompareExchange(ref _pairInvocationInFlight,1,0)!=0)
            throw new InvalidOperationException("Ownership invocation is already in progress.");
        try
        {
        // Fresh check after admission to the shared single/pair custody interlock.
        if(HasPendingOwnershipSend)
            throw new InvalidOperationException("Original ownership send remains pending; a new send cannot reconcile it.");
        if (_session is { } session)
        {
            OriginalSessionSendCustody? custody=null;
            if(ownership)
            {
                custody=new(_kernel,_requester,session);
                Volatile.Write(ref _pairSendCustody,custody);
            }
            var result = await _kernel.InvokeSessionAsync(
                _requester,
                session,
                messageId,
                requestPayload,
                cancellationToken,custody).ConfigureAwait(false);
            if (!result.IsSuccess) throw Failure("session invoke", result.Error, result.Message);
            return result.Value!;
        }

        RejectCancellableLegacyInvocation(cancellationToken);
        var send = _kernel.Send(
            _requester,
            _responder!.Value,
            _requesterEndpoint,
            messageId,
            requestPayload,
            _capabilities);
        return await WaitForResponseAsync(send).ConfigureAwait(false);
        }
        finally { if(ownership) Volatile.Write(ref _pairInvocationInFlight,0); }
    }

    public ResponseEnvelope InvokeOwnershipPair(
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload) =>
        InvokeOwnershipPairAsync(
                messageId,
                firstOwnershipPayload,
                secondOwnershipPayload,
                _defaultCancellationToken)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload) =>
        InvokeOwnershipPairAsync(
            messageId,
            firstOwnershipPayload,
            secondOwnershipPayload,
            _defaultCancellationToken);

    public async ValueTask<ResponseEnvelope> InvokeOwnershipPairAsync(
        uint messageId,
        object firstOwnershipPayload,
        object secondOwnershipPayload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(firstOwnershipPayload);
        ArgumentNullException.ThrowIfNull(secondOwnershipPayload);
        cancellationToken.ThrowIfCancellationRequested();

        if(Interlocked.CompareExchange(ref _pairInvocationInFlight,1,0)!=0)
            throw new InvalidOperationException("Ownership-pair invocation is already in progress.");
        try
        {
        if(HasPendingOwnershipPair)
            throw new InvalidOperationException("Original ownership-pair send remains pending; a new send cannot reconcile it.");
        if (_session is { } session)
        {
            var custody=new OriginalSessionSendCustody(_kernel,_requester,session);
            Volatile.Write(ref _pairSendCustody,custody);
            var result = await _kernel.InvokeSessionOwnershipPairAsync(
                _requester,
                session,
                messageId,
                firstOwnershipPayload,
                secondOwnershipPayload,
                cancellationToken,custody).ConfigureAwait(false);
            if (!result.IsSuccess) throw Failure("session invoke", result.Error, result.Message);
            return result.Value!;
        }

        RejectCancellableLegacyInvocation(cancellationToken);
        var send = _kernel.SendOwnershipPair(
            _requester,
            _responder!.Value,
            _requesterEndpoint,
            messageId,
            firstOwnershipPayload,
            secondOwnershipPayload,
            _capabilities);
        return await WaitForResponseAsync(send).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _pairInvocationInFlight,0); }
    }

    private async ValueTask<ResponseEnvelope> WaitForResponseAsync(
        KernelResult<ChannelEnvelope> send)
    {
        if (!send.IsSuccess)
            throw Failure("send", send.Error, send.Message);

        var response = await _kernel.WaitForResponseAsync(
            _requester,
            _requesterEndpoint,
            send.Value!.Sequence).ConfigureAwait(false);
        if (!response.IsSuccess)
            throw Failure("response wait", response.Error, response.Message);

        return response.Value!;
    }

    private void RejectCancellableLegacyInvocation(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
            throw new InvalidOperationException("Cancellable SIP invocation requires an EndpointSessionHandle; legacy raw-channel transport cannot safely settle ownership or service work from caller cancellation.");
    }

    private static InvalidOperationException Failure(
        string stage,
        KernelError error,
        string? message) =>
        new($"SIP client runtime {stage} failed with {error}: {message ?? "no diagnostic"}");
}

// Managed custody only; the invocation registry retains the authoritative fact.
internal sealed class OriginalSessionSendCustody
{
    private readonly OriginalSessionSendCustody _original;
    private readonly RuntimeKernel _kernel;
    internal readonly ProcessHandle Caller;
    internal readonly EndpointSessionHandle Session;
    private EndpointSessionInvocationRegistry.OriginalSend? _fact;
    internal OriginalSessionSendCustody(RuntimeKernel kernel,ProcessHandle caller,EndpointSessionHandle session)
    { _original=this; _kernel=kernel; Caller=caller; Session=session; }
    internal bool Matches(RuntimeKernel kernel,ProcessHandle caller,EndpointSessionHandle session) =>
        ReferenceEquals(_original,this) && ReferenceEquals(_kernel,kernel) && Caller==caller && Session==session;
    internal void Retain(RuntimeKernel kernel,ProcessHandle caller,EndpointSessionHandle session,EndpointSessionInvocationRegistry.OriginalSend fact)
    {
        if(!Matches(kernel,caller,session) || Interlocked.CompareExchange(ref _fact,fact,null) is not null)
            throw new InvalidOperationException("Original send custody is copied, foreign or already populated.");
    }
    internal EndpointSessionInvocationRegistry.OriginalSend? Read(RuntimeKernel kernel) =>
        Matches(kernel,Caller,Session) ? Volatile.Read(ref _fact) : null;
}
