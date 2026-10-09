using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private sealed record NativeServiceDrainRegistration(
        ProcessHandle Service,
        EndpointSessionHandle Session,
        Func<KernelResult> Drain)
    {
        // Admission interlock only, never permission or closure evidence.
        public bool DrainInProgress { get; set; }
    }

    private readonly object _nativeServiceDrainGate = new();
    private readonly Dictionary<ProcessHandle, List<NativeServiceDrainRegistration>> _nativeServiceDrains = [];
    private readonly Dictionary<EndpointSessionHandle, NativeServiceDrainRegistration> _nativeSessionDrains = [];

    internal void RegisterNativeServiceDrain(
        ProcessHandle service,
        EndpointSessionHandle session,
        Func<KernelResult> drain)
    {
        lock (_platformMemoryUseGate)
        {
        var provider = Processes.Resolve(service);
        var binding = EndpointSessions.ResolveForService(session, service);
        if (!provider.IsSuccess || !EnsureProcessAcceptsNewEffects(provider.Value!).IsSuccess || !binding.IsSuccess)
            throw new InvalidOperationException("Native host binding requires an exact live provider/session.");
        var caller = Processes.Resolve(binding.Value!.Caller);
        if (!caller.IsSuccess || !EnsureProcessAcceptsNewEffects(caller.Value!).IsSuccess)
            throw new InvalidOperationException("Native host binding requires an exact caller accepting new effects.");
        lock (_nativeServiceDrainGate)
        {
            if (_nativeSessionDrains.ContainsKey(session) || OriginalNativeDrains().Any(record => record.Session == session))
                throw new InvalidOperationException("A native service host is already bound to this exact endpoint session.");
            if (!_nativeServiceDrains.TryGetValue(service, out var drains))
            {
                drains = [];
                _nativeServiceDrains.Add(service, drains);
            }
            var registration = new NativeServiceDrainRegistration(service, session, drain);
            drains.Add(registration);
            _nativeSessionDrains.Add(session, registration);
        }
        }
    }

    private KernelResult DrainNativeServiceSession(EndpointSessionHandle session)
    {
        NativeServiceDrainRegistration registration;
        lock (_nativeServiceDrainGate)
        {
            registration = OriginalNativeDrains().SingleOrDefault(record => record.Session == session)!;
            if (registration is null)
                return _nativeSessionDrains.ContainsKey(session)
                    ? KernelResult.Fail(KernelError.StaleGeneration, "Native route has no original registration.")
                    : KernelResult.Ok();
            if (_nativeSessionDrains.TryGetValue(session, out var routed) && !ReferenceEquals(routed, registration))
                return KernelResult.Fail(KernelError.StaleGeneration, "Native session route is foreign to the original registration.");
        }
        return DrainNativeServiceRegistration(registration);
    }

    private KernelResult DrainNativeServicesForProcess(ProcessHandle service)
    {
        var sessions = EndpointSessions.CaptureHandlesForProcess(service).ToHashSet();
        NativeServiceDrainRegistration[] work;
        lock (_nativeServiceDrainGate)
        {
            work = OriginalNativeDrains()
                .Where(registration => registration.Service == service || sessions.Contains(registration.Session))
                .Reverse().ToArray();
        }

        foreach (var registration in work)
        {
            var result = DrainNativeServiceRegistration(registration);
            if (!result.IsSuccess) return result;
        }
        lock (_nativeServiceDrainGate)
            return OriginalNativeDrains().Any(registration =>
                registration.Service == service || sessions.Contains(registration.Session))
                ? KernelResult.Fail(KernelError.SessionDraining, "Native registrations remain after the captured process drain batch.")
                : KernelResult.Ok();
    }

    private KernelResult DrainNativeServiceRegistration(NativeServiceDrainRegistration registration)
    {
        lock (_nativeServiceDrainGate)
        {
            if (!OwnsOriginalNativeDrain(registration) ||
                _nativeSessionDrains.TryGetValue(registration.Session, out var current) && !ReferenceEquals(current, registration))
                return KernelResult.Fail(KernelError.StaleGeneration, "Captured native drain registration is no longer exact; reconcile through the existing consumer.");
            if (registration.DrainInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Exact native drain callback is already in progress.");
            registration.DrainInProgress = true;
        }
        var completed = false;
        try
        {
            var result = registration.Drain();
            completed = result.IsSuccess;
            return result;
        }
        finally
        {
            lock (_nativeServiceDrainGate)
            {
                registration.DrainInProgress = false;
                // A refused/throwing callback retains the real registration.
                // Only its own successful producer drain consumes this record.
                if (completed && OwnsOriginalNativeDrain(registration))
                {
                    if (_nativeSessionDrains.TryGetValue(registration.Session, out var current) && ReferenceEquals(current, registration))
                        _nativeSessionDrains.Remove(registration.Session);
                    if (_nativeServiceDrains.TryGetValue(registration.Service, out var drains))
                    {
                        var exact = drains.FindIndex(record => ReferenceEquals(record, registration));
                        if (exact >= 0) drains.RemoveAt(exact);
                        if (drains.Count == 0) _nativeServiceDrains.Remove(registration.Service);
                    }
                }
            }
        }
    }

    // Existing provider inventory retains the same registration objects; session map is routing.
    // Caller holds the native drain gate. No callback is enumerated or invoked here.
    private IEnumerable<NativeServiceDrainRegistration> OriginalNativeDrains() =>
        _nativeServiceDrains.Values.SelectMany(records => records);

    private bool OwnsOriginalNativeDrain(NativeServiceDrainRegistration registration) =>
        _nativeServiceDrains.TryGetValue(registration.Service, out var records) &&
        records.Any(record => ReferenceEquals(record, registration));
}
