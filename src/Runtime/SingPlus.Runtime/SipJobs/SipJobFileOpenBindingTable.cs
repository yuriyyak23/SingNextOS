using SingPlus.Contracts;
using SingPlus.Sip.FileSystem;

namespace SingPlus.Runtime;

internal readonly record struct SipJobTrustedBindingHandle(Guid OpaqueToken);

internal readonly record struct SipJobFileOpenBindingKey(
    AuthorityRealmId Realm,
    ProcessHandle Caller,
    ProcessHandle ServiceProcess,
    ServiceIdentity Service,
    ServiceGeneration ServiceGeneration,
    EndpointSessionHandle Session,
    string ContractName,
    string ContractVersion,
    string ContractDigest,
    string ThunkId,
    string ThunkDigest);

internal readonly record struct SipJobResolvedBindingMetadata(
    SipJobFileOpenBindingKey Key,
    string RouteKind);

internal readonly record struct SipJobBindingRegistration(
    SipJobTrustedBindingHandle Handle,
    SipJobFileOpenBindingKey Key);

// Operation-specific TCB table. It caches an exact lookup route and a typed target,
// never an authorization result. No target reference leaves this class.
internal sealed class SipJobFileOpenBindingTable(RuntimeKernel kernel)
{
    private sealed class Entry
    {
        internal required SipJobFileOpenBindingKey Key { get; init; }
        internal required ServiceEndpointDescriptor Descriptor { get; init; }
        internal required IIFileServiceGeneratedSentryTarget_OpenAsync Target { get; init; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    internal KernelResult<SipJobBindingRegistration> Register(
        ServiceEndpointDescriptor descriptor,
        ProcessHandle serviceProcess,
        EndpointSessionHandle session,
        IIFileServiceGeneratedSentryTarget_OpenAsync target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var current = ResolveCurrent(descriptor, serviceProcess, session);
        if (!current.IsSuccess)
            return KernelResult<SipJobBindingRegistration>.Fail(current.Error, current.Message!);

        Guid token;
        lock (_gate)
        {
            do token = Guid.NewGuid(); while (token == Guid.Empty || _entries.ContainsKey(token));
            _entries.Add(token, new Entry { Key = current.Value!, Descriptor = descriptor, Target = target });
        }
        return KernelResult<SipJobBindingRegistration>.Ok(new(new(token), current.Value!));
    }

    internal KernelResult<SipJobResolvedBindingMetadata> ValidateRoute(
        SipJobTrustedBindingHandle handle,
        SipJobFileOpenBindingKey expected)
    {
        if (handle.OpaqueToken == Guid.Empty)
            return KernelResult<SipJobResolvedBindingMetadata>.Fail(KernelError.StaleHandle, "SipJob binding token is empty.");

        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(handle.OpaqueToken, out entry!))
                return KernelResult<SipJobResolvedBindingMetadata>.Fail(KernelError.StaleHandle, "SipJob binding is absent or retired.");
        }
        if (!Exact(entry.Key, expected))
            return KernelResult<SipJobResolvedBindingMetadata>.Fail(KernelError.ServiceContractMismatch, "SipJob binding identity does not match the requested route.");

        var current = ResolveCurrent(entry.Descriptor, entry.Key.ServiceProcess, entry.Key.Session);
        if (!current.IsSuccess)
            return KernelResult<SipJobResolvedBindingMetadata>.Fail(current.Error, current.Message!);
        if (!Exact(current.Value!, entry.Key))
            return KernelResult<SipJobResolvedBindingMetadata>.Fail(KernelError.StaleGeneration, "SipJob binding route is stale.");

        return KernelResult<SipJobResolvedBindingMetadata>.Ok(new(entry.Key, "Generated:IFileService.OpenAsync"));
    }

    internal KernelResult Retire(SipJobTrustedBindingHandle handle)
    {
        if (handle.OpaqueToken == Guid.Empty)
            return KernelResult.Fail(KernelError.StaleHandle, "SipJob binding token is empty.");
        lock (_gate)
            return _entries.Remove(handle.OpaqueToken)
                ? KernelResult.Ok()
                : KernelResult.Fail(KernelError.StaleHandle, "SipJob binding is absent or already retired.");
    }

    private KernelResult<SipJobFileOpenBindingKey> ResolveCurrent(
        ServiceEndpointDescriptor descriptor,
        ProcessHandle serviceProcess,
        EndpointSessionHandle session)
    {
        var process = kernel.Processes.Resolve(serviceProcess);
        if (!process.IsSuccess)
            return KernelResult<SipJobFileOpenBindingKey>.Fail(process.Error, process.Message!);
        var service = kernel.Services.Resolve(descriptor);
        if (!service.IsSuccess)
            return KernelResult<SipJobFileOpenBindingKey>.Fail(service.Error, service.Message!);
        if (service.Value!.Provider != serviceProcess)
            return KernelResult<SipJobFileOpenBindingKey>.Fail(KernelError.StaleGeneration, "Service provider incarnation does not match the binding.");
        var exactSession = kernel.EndpointSessions.ResolveForService(session, serviceProcess);
        if (!exactSession.IsSuccess)
            return KernelResult<SipJobFileOpenBindingKey>.Fail(exactSession.Error, exactSession.Message!);
        if (!string.Equals(descriptor.Contract.Name, IFileServiceProtocol.ContractName, StringComparison.Ordinal) ||
            !string.Equals(descriptor.Contract.Digest, IFileServiceProtocol.ContractDigest, StringComparison.Ordinal))
            return KernelResult<SipJobFileOpenBindingKey>.Fail(KernelError.ServiceContractMismatch, "Binding is not the exact generated FileService contract.");

        return KernelResult<SipJobFileOpenBindingKey>.Ok(new(
            kernel.CapabilityAuthority.RealmId,
            exactSession.Value!.Caller,
            serviceProcess,
            descriptor.Service,
            descriptor.Generation,
            session,
            descriptor.Contract.Name,
            descriptor.Contract.Version,
            descriptor.Contract.Digest,
            IFileServiceGeneratedOperationSentries.Thunk_OpenAsync,
            IFileServiceGeneratedOperationSentries.Thunk_OpenAsync_Digest));
    }

    private static bool Exact(SipJobFileOpenBindingKey left, SipJobFileOpenBindingKey right) =>
        left.Realm == right.Realm && left.Caller == right.Caller && left.ServiceProcess == right.ServiceProcess &&
        left.Service == right.Service && left.ServiceGeneration == right.ServiceGeneration && left.Session == right.Session &&
        string.Equals(left.ContractName, right.ContractName, StringComparison.Ordinal) &&
        string.Equals(left.ContractVersion, right.ContractVersion, StringComparison.Ordinal) &&
        string.Equals(left.ContractDigest, right.ContractDigest, StringComparison.Ordinal) &&
        string.Equals(left.ThunkId, right.ThunkId, StringComparison.Ordinal) &&
        string.Equals(left.ThunkDigest, right.ThunkDigest, StringComparison.Ordinal);
}
