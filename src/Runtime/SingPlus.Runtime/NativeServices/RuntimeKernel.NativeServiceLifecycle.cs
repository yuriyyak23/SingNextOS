using SingPlus.Contracts;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    private sealed record NativeServiceDrainRegistration(
        ProcessHandle Service,
        EndpointSessionHandle Session,
        Func<KernelResult> Drain);

    private readonly Dictionary<ProcessHandle, List<NativeServiceDrainRegistration>> _nativeServiceDrains = [];
    private readonly Dictionary<EndpointSessionHandle, NativeServiceDrainRegistration> _nativeSessionDrains = [];

    internal void RegisterNativeServiceDrain(
        ProcessHandle service,
        EndpointSessionHandle session,
        Func<KernelResult> drain)
    {
        if (_nativeSessionDrains.ContainsKey(session))
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

    private KernelResult DrainNativeServiceSession(EndpointSessionHandle session)
    {
        if (!_nativeSessionDrains.TryGetValue(session, out var registration))
            return KernelResult.Ok();
        var result = registration.Drain();
        if (!result.IsSuccess) return result;
        _nativeSessionDrains.Remove(session);
        if (_nativeServiceDrains.TryGetValue(registration.Service, out var drains))
        {
            drains.Remove(registration);
            if (drains.Count == 0) _nativeServiceDrains.Remove(registration.Service);
        }
        return KernelResult.Ok();
    }

    private KernelResult DrainNativeServicesForProcess(ProcessHandle service)
    {
        if (!_nativeServiceDrains.TryGetValue(service, out var drains))
            return KernelResult.Ok();

        foreach (var registration in drains.AsEnumerable().Reverse().ToArray())
        {
            var result = registration.Drain();
            if (!result.IsSuccess) return result;
            _nativeSessionDrains.Remove(registration.Session);
        }
        _nativeServiceDrains.Remove(service);
        return KernelResult.Ok();
    }
}
