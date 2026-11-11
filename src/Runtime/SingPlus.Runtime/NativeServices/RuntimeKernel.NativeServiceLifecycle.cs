using SingPlus.Contracts;
using SingPlus.Sip;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    internal sealed class NativeServiceDrainRegistration
    {
        public NativeServiceDrainRegistration(RuntimeKernel kernel, ProcessHandle service, EndpointSessionRegistry.Record sessionRecord, Func<KernelResult> drain)
        { Self = this; Kernel = kernel; Service = service; SessionRecord = sessionRecord; Session = sessionRecord.Handle; Drain = drain; }
        public NativeServiceDrainRegistration Self { get; }
        public RuntimeKernel Kernel { get; }
        public ProcessHandle Service { get; }
        public EndpointSessionHandle Session { get; }
        public EndpointSessionRegistry.Record SessionRecord { get; }
        public Func<KernelResult> Drain { get; }
        public NativeDrainOutcome? OriginalOutcome { get; set; }
        public NativeDrainOutcome? Outcome { get; set; }
        public NativeDrainOutcome? ConsumedOutcome { get; set; }
        // Admission interlock only, never permission or closure evidence.
        public bool DrainInProgress { get; set; }
    }

    internal sealed class NativeDrainOutcome
    {
        public NativeDrainOutcome(NativeServiceDrainRegistration request) { Self = this; Request = request; }
        public NativeDrainOutcome Self { get; }
        public NativeServiceDrainRegistration Request { get; }
        public KernelResult? Raw { get; set; }
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
            KernelResult<SingProcess> provider = Processes.Resolve(service);
            KernelResult<EndpointSessionRegistry.Record> binding = EndpointSessions.ResolveForService(session, service);
        if (!provider.IsSuccess || !EnsureProcessAcceptsNewEffects(provider.Value!).IsSuccess || !binding.IsSuccess)
            throw new InvalidOperationException("Native host binding requires an exact live provider/session.");
            KernelResult<SingProcess> caller = Processes.Resolve(binding.Value!.Caller);
        if (!caller.IsSuccess || !EnsureProcessAcceptsNewEffects(caller.Value!).IsSuccess)
            throw new InvalidOperationException("Native host binding requires an exact caller accepting new effects.");
        lock (_nativeServiceDrainGate)
        {
            if (_nativeSessionDrains.ContainsKey(session) || OriginalNativeDrains().Any(record => record.Session == session))
                throw new InvalidOperationException("A native service host is already bound to this exact endpoint session.");
            if (!_nativeServiceDrains.TryGetValue(service, out List<NativeServiceDrainRegistration>? drains))
            {
                drains = [];
                _nativeServiceDrains.Add(service, drains);
            }
                NativeServiceDrainRegistration registration = new NativeServiceDrainRegistration(this, service, binding.Value!, drain);
            if (!EndpointSessions.AttachNativeDrain(binding.Value!, registration))
                throw new InvalidOperationException("Native host requires the original unbound session lifetime.");
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
            if (_nativeSessionDrains.TryGetValue(session, out NativeServiceDrainRegistration? routed) && !ReferenceEquals(routed, registration))
                return KernelResult.Fail(KernelError.StaleGeneration, "Native session route is foreign to the original registration.");
        }
        return DrainNativeServiceRegistration(registration);
    }

    private KernelResult DrainNativeServicesForProcess(ProcessHandle service)
    {
        HashSet<EndpointSessionHandle> sessions = EndpointSessions.CaptureHandlesForProcess(service).ToHashSet();
        NativeServiceDrainRegistration[] work;
        lock (_nativeServiceDrainGate)
        {
            work = OriginalNativeDrains()
                .Where(registration => registration.Service == service || sessions.Contains(registration.Session))
                .Reverse().ToArray();
        }

        foreach (NativeServiceDrainRegistration registration in work)
        {
            KernelResult result = DrainNativeServiceRegistration(registration);
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
        NativeDrainOutcome outcome;
        lock (_nativeServiceDrainGate)
        {
            if (!OwnsOriginalNativeDrain(registration) ||
                _nativeSessionDrains.TryGetValue(registration.Session, out NativeServiceDrainRegistration? current) && !ReferenceEquals(current, registration))
                return KernelResult.Fail(KernelError.StaleGeneration, "Captured native drain registration is no longer exact; reconcile through the existing consumer.");
            if (registration.DrainInProgress)
                return KernelResult.Fail(KernelError.SessionDraining, "Exact native drain callback is already in progress.");
            if (registration.OriginalOutcome is not null || registration.Outcome is not null)
            {
                if (registration.OriginalOutcome is not { } known || !ReferenceEquals(known.Self, known) ||
                    !ReferenceEquals(known.Request, registration) || !ReferenceEquals(registration.Outcome, known) || known.Raw is not { IsSuccess: true } raw)
                    return KernelResult.Fail(KernelError.StaleGeneration, "Original native drain outcome is unknown; callback cannot repeat.");
                RetireNativeDrainRegistration(registration);
                return raw;
            }
            outcome = new(registration); // Allocate before effect-capable cleanup callback.
            registration.DrainInProgress = true;
        }
        try
        {
            KernelResult result = registration.Drain();
            lock (_nativeServiceDrainGate)
            {
                outcome.Raw = result;
                if (result.IsSuccess) registration.OriginalOutcome = registration.Outcome = outcome;
                // Retain successful original closure even when final routing publication refuses.
                if (!OwnsOriginalNativeDrain(registration) ||
                    _nativeSessionDrains.TryGetValue(registration.Session, out NativeServiceDrainRegistration? current) && !ReferenceEquals(current, registration))
                    return KernelResult.Fail(KernelError.StaleGeneration, "Native drain source changed during callback; original outcome retained.");
                if (result.IsSuccess) RetireNativeDrainRegistration(registration);
                return result;
            }
        }
        finally
        {
            lock (_nativeServiceDrainGate)
            {
                registration.DrainInProgress = false;
            }
        }
    }

    // Existing owner/correlation retirement only; caller holds native drain gate.
    private void RetireNativeDrainRegistration(NativeServiceDrainRegistration registration)
    {
        registration.ConsumedOutcome = registration.OriginalOutcome;
        if (_nativeSessionDrains.TryGetValue(registration.Session, out NativeServiceDrainRegistration? current) && ReferenceEquals(current, registration))
            _nativeSessionDrains.Remove(registration.Session);
        if (_nativeServiceDrains.TryGetValue(registration.Service, out List<NativeServiceDrainRegistration>? drains))
        {
            int exact = drains.FindIndex(record => ReferenceEquals(record, registration));
            if (exact >= 0) drains.RemoveAt(exact);
            if (drains.Count == 0) _nativeServiceDrains.Remove(registration.Service);
        }
    }

    // Existing provider inventory retains the same registration objects; session map is routing.
    // Caller holds the native drain gate. No callback is enumerated or invoked here.
    private IEnumerable<NativeServiceDrainRegistration> OriginalNativeDrains()
    {
        NativeServiceDrainRegistration[] attached = EndpointSessions.CaptureOriginalNativeDrains();
        // The original session attachment selects work; inventory entries must still
        // match it before effect. A substituted inventory cannot create a second callback.
        return attached.Where(registration => !HasConsumedNativeDrain(registration))
            .Concat(_nativeServiceDrains.Values.SelectMany(records => records)
                .Where(registration => !attached.Any(original => original.Session == registration.Session)));
    }

    private bool HasConsumedNativeDrain(NativeServiceDrainRegistration registration) =>
        ReferenceEquals(registration.Self, registration) && ReferenceEquals(registration.Kernel, this) &&
        EndpointSessions.OwnsNativeDrain(registration.SessionRecord, registration) &&
        registration.OriginalOutcome is { } outcome && ReferenceEquals(outcome.Self, outcome) &&
        ReferenceEquals(outcome.Request, registration) && ReferenceEquals(registration.Outcome, outcome) &&
        ReferenceEquals(registration.ConsumedOutcome, outcome) && outcome.Raw is { IsSuccess: true };

    private bool OwnsOriginalNativeDrain(NativeServiceDrainRegistration registration) =>
        ReferenceEquals(registration.Self, registration) && ReferenceEquals(registration.Kernel, this) &&
        EndpointSessions.OwnsNativeDrain(registration.SessionRecord, registration) &&
        _nativeServiceDrains.TryGetValue(registration.Service, out List<NativeServiceDrainRegistration>? records) &&
        records.Any(record => ReferenceEquals(record, registration));
}
