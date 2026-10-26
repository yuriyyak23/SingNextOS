using System.Globalization;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool RegisterShellService(EmulatorSession session, string[] args)
    {
        if (args.Length != 2 || !TryReadPid(args, 0, out ulong pid) || string.IsNullOrWhiteSpace(args[1]))
            return Fail(KernelError.InvalidTransition, "usage: service-register <pid> <name>");
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        KernelResult<ProcessHandle> owner = session.ResolvePid(pid);
        if (!owner.IsSuccess) return Fail(owner.Error, owner.Message);
        ProtocolDefinitionV1 protocol = EmulatorSession.CreatePingPongProtocol();
        KernelResult<ServiceEndpointDescriptor> registered = runtime.RegisterService(owner.Value, args[1],
            new ServiceContractIdentity(protocol.ContractName, "1", protocol.ContractDigest), protocol,
            requiredCapabilities: [new CapabilityRequirementV1(ResourceKind.KernelService, args[1], CapabilityRights.Read)]);
        if (!registered.IsSuccess) return Fail(registered.Error, registered.Message);
        session.ShellServices[args[1]] = registered.Value;
        WriteService(registered.Value);
        return true;
    }

    private static bool ShowShellService(EmulatorSession session, string[] args, bool setAvailability)
    {
        if (args.Length != (setAvailability ? 2 : 1))
            return Fail(KernelError.InvalidTransition, "usage: service <name> | service-state <name> <accepting|draining|unavailable>");
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        KernelResult<ServiceEndpointDescriptor> found = setAvailability && session.ShellServices.TryGetValue(args[0], out ServiceEndpointDescriptor tracked)
            ? KernelResult<ServiceEndpointDescriptor>.Ok(tracked)
            : runtime.ResolveByServiceName(args[0]);
        if (!found.IsSuccess) return Fail(found.Error, found.Message);
        if (setAvailability)
        {
            ServiceAvailability availability;
            if (string.Equals(args[1], "accepting", StringComparison.OrdinalIgnoreCase)) availability = ServiceAvailability.Accepting;
            else if (string.Equals(args[1], "draining", StringComparison.OrdinalIgnoreCase)) availability = ServiceAvailability.Draining;
            else if (string.Equals(args[1], "unavailable", StringComparison.OrdinalIgnoreCase)) availability = ServiceAvailability.Unavailable;
            else return Fail(KernelError.InvalidTransition, "Expected accepting, draining or unavailable.");
            KernelResult changed = runtime.SetServiceAvailability(found.Value.Service, availability);
            if (!changed.IsSuccess) return Fail(changed.Error, changed.Message);
            ServiceEndpointDescriptor updated = found.Value with { Availability = availability };
            session.ShellServices[args[0]] = updated;
            EmulatorConsole.Ok($"[kernel] service {updated.Service.Id.Value}:{updated.Generation.Value} availability set to {availability}; discovery excludes non-accepting services\r\n");
            return true;
        }
        WriteService(found.Value);
        return true;
    }

    private static bool OpenShellSession(EmulatorSession session, string[] args)
    {
        if (args.Length < 2 || !TryReadPid(args, 0, out ulong pid))
            return Fail(KernelError.InvalidTransition, "usage: session-open <pid> <serviceName> [capabilityId ...] [--lifetime-ms 1..86400000]");
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        KernelResult<ProcessHandle> caller = session.ResolvePid(pid);
        if (!caller.IsSuccess) return Fail(caller.Error, caller.Message);
        List<CapabilityId> capabilities = new List<CapabilityId>();
        TimeSpan? lifetime = null;
        for (int index = 2; index < args.Length; index++)
        {
            if (args[index] == "--lifetime-ms")
            {
                if (lifetime is not null || ++index >= args.Length ||
                    !long.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out long milliseconds) ||
                    milliseconds is < 1 or > 86_400_000)
                    return Fail(KernelError.InvalidTransition, "Session lifetime must be specified once as 1..86400000 milliseconds.");
                lifetime = TimeSpan.FromMilliseconds(milliseconds);
                continue;
            }
            if (!ulong.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
                return Fail(KernelError.InvalidTransition, "Capability ids must be numeric.");
            capabilities.Add(new CapabilityId(id));
        }
        KernelResult<ServiceEndpointDescriptor> found = runtime.ResolveByServiceName(args[1]);
        if (!found.IsSuccess) return Fail(found.Error, found.Message);
        KernelResult<EndpointSessionHandle> opened = runtime.OpenSession(caller.Value, found.Value, capabilities, lifetime: lifetime);
        if (!opened.IsSuccess) return Fail(opened.Error, opened.Message);
        session.ShellSessions.Add(opened.Value.SessionId.Value, opened.Value);
        EmulatorConsole.Ok($"[kernel] session {opened.Value.SessionId.Value}:{opened.Value.Generation.Value} opened for pid {pid}; lifetime-ms={lifetime?.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) ?? "none"}\r\n");
        return true;
    }

    private static bool CloseShellSession(EmulatorSession session, string[] args)
    {
        if (args.Length != 2 || !TryReadPid(args, 0, out ulong pid) ||
            !ulong.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id))
            return Fail(KernelError.InvalidTransition, "usage: session-close <pid> <sessionId>");
        if (session.Runtime is not { } runtime) return Fail(KernelError.PlatformUnavailable, "Runtime is unavailable.");
        if (!session.ShellSessions.TryGetValue(id, out EndpointSessionHandle handle))
            return Fail(KernelError.InvalidTransition, "Unknown shell session handle.");
        KernelResult<ProcessHandle> caller = session.ResolvePid(pid);
        if (!caller.IsSuccess) return Fail(caller.Error, caller.Message);
        KernelResult closed = runtime.CloseSession(caller.Value, handle);
        if (!closed.IsSuccess) return Fail(closed.Error, closed.Message);
        EmulatorConsole.Ok($"[kernel] session {id}:{handle.Generation.Value} closed by pid {pid}\r\n");
        return true;
    }

    private static void WriteService(ServiceEndpointDescriptor descriptor) =>
        EmulatorConsole.Info($"[kernel] service {descriptor.Service.Id.Value}:{descriptor.Generation.Value} '{descriptor.Service.Name}': {descriptor.Availability}; contract={descriptor.Contract.Name}; digest={descriptor.Contract.Digest}\r\n");
}
