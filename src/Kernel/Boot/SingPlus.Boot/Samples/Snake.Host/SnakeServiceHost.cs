using SingPlus.Apps.Snake;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.Apps.Snake.Host;

// Trusted runtime broker. Only bounded Snake commands/frames cross the child process boundary.
// The worker receives from the runtime independently of the client invocation stack.
public sealed class SnakeServiceHost
{
    private readonly RuntimeKernel _runtime;
    private readonly ProcessHandle _owner;
    private readonly EndpointSessionHandle _session;
    private readonly OwnedBuffer<int> _storage;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly RestrictedSnakeProcess _child;
    private int _dispatchCount;
    private bool _stopped;
    private bool _released;
    private string? _failure;

    private SnakeServiceHost(RuntimeKernel runtime, ProcessHandle owner, EndpointSessionHandle session, OwnedBuffer<int> storage, RestrictedSnakeProcess child)
    {
        _runtime = runtime;
        _owner = owner;
        _session = session;
        _storage = storage;
        _child = child;
        _worker = Task.Run(ServeAsync);
    }

    public int DispatchCount => Volatile.Read(ref _dispatchCount);
    public RegionHandle Storage => _storage.Handle;
    public Task Completion => _worker;
    public int ChildProcessId => _child.ProcessId;

    public static KernelResult<SnakeServiceHost> Start(RuntimeKernel runtime, ProcessHandle owner, EndpointSessionHandle session)
    {
        var storage = runtime.AllocateBuffer<int>(owner, SnakeBindings.Width * SnakeBindings.Height);
        if (!storage.IsSuccess) return KernelResult<SnakeServiceHost>.Fail(storage.Error, storage.Message!);
        try { return KernelResult<SnakeServiceHost>.Ok(new(runtime, owner, session, storage.Value!, RestrictedSnakeProcess.Start())); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or PlatformNotSupportedException or TimeoutException)
        {
            var released = runtime.ReleaseRegion(owner, storage.Value!);
            var cleanup = released.IsSuccess ? "" : $" Region cleanup refused: {released.Error}: {released.Message}";
            return KernelResult<SnakeServiceHost>.Fail(KernelError.PlatformUnavailable, $"Restricted Snake launch failed: {exception.Message}.{cleanup}");
        }
    }

    private async Task ServeAsync()
    {

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (!_child.IsAlive) throw new InvalidOperationException("Restricted Snake child exited; dispatch refused.");
                var received = _runtime.ReceiveSessionRequest(_owner, _session);
                if (!received.IsSuccess)
                {
                    // Public receive is nonblocking; there is no public server wait API.
                    if (received.Error == KernelError.InvalidMessage && received.Message == "Channel queue is empty.")
                    {
                        await Task.Delay(2, _stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    throw new InvalidOperationException($"Snake receive: {received.Error}: {received.Message}");
                }
                var accepted = _runtime.AcceptSessionInvocation(_owner, received.Value!.Invocation, allowInFlightCancellation: false);
                if (!accepted.IsSuccess) throw new InvalidOperationException($"Snake accept: {accepted.Error}: {accepted.Message}");
                var live = _runtime.Processes.Resolve(_owner);
                if (!live.IsSuccess || live.Value!.State != ProcessState.Running)
                    throw new InvalidOperationException("Snake service owner is not Running; dispatch refused.");
                if (received.Value.Request.MessageId != SnakeBindings.StepMessageId ||
                    received.Value.Request.Payload is not SnakeMove move || move.Direction is < 0 or > 5)
                    throw new InvalidOperationException("Snake request shape or direction is invalid.");
                var frame = await _child.StepAsync(move, _stop.Token).ConfigureAwait(false);
                // Runtime owns only this parent snapshot, never child CLR memory.
                _storage.Span.Clear();
                for (var cell = 0; cell < SnakeBindings.Width * SnakeBindings.Height; cell++)
                {
                    var bits = cell < 64 ? frame.Cells0 : cell < 128 ? frame.Cells1 : frame.Cells2;
                    _storage.Span[cell] = (bits & (1UL << (cell % 64))) == 0 ? 0 : 1;
                }
                var published = _runtime.PublishSessionResponse(_owner, received.Value.Invocation, frame);
                if (!published.IsSuccess) throw new InvalidOperationException($"Snake publish: {published.Error}: {published.Message}");
                Interlocked.Increment(ref _dispatchCount);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or TimeoutException)
        { throw new InvalidOperationException($"Restricted child IPC failed: {exception.Message}", exception); }
    }

    // Join first: after Stop returns, no game instructions can use the Region.
    // A failed Region release retains storage for the exact retry/owner teardown.
    public KernelResult Stop()
    {
        if (!_stopped)
        {
            _stop.Cancel();
            try { _worker.GetAwaiter().GetResult(); }
            catch (InvalidOperationException exception) { _failure = exception.Message; }
            try { _child.Dispose(); } // Confirm actual process death before releasing runtime storage.
            catch (InvalidOperationException exception) { return KernelResult.Fail(KernelError.InvalidTransition, exception.Message); }
            _stopped = true;
            _stop.Dispose();
        }
        if (!_released)
        {
            var release = _runtime.ReleaseRegion(_owner, _storage);
            if (!release.IsSuccess) return release;
            _released = true;
        }
        return _failure is null ? KernelResult.Ok() : KernelResult.Fail(KernelError.InvalidTransition, _failure);
    }
}