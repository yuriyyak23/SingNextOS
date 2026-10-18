using SingPlus.Contracts;
using SingPlus.Platform;

namespace SingPlus.Runtime;

public sealed partial class RuntimeKernel
{
    internal KernelResult<EndpointSessionInvocationRegistry.OriginalPublication> CaptureOriginalVirtualizationRequestLifetime(
        EndpointSessionInvocationHandle invocation,ProcessHandle service,ChannelEnvelope request,bool refusedBeforeAcceptance=false)
    {
        lock(_requestResponseCorrelationGate)
        {
            var accepted=refusedBeforeAcceptance ? SessionInvocations.BindOriginalVirtualizationInputRefusal(invocation,service,request) :
                SessionInvocations.BindOriginalServiceRequest(invocation,service,request);
            if(!accepted.IsSuccess)return KernelResult<EndpointSessionInvocationRegistry.OriginalPublication>.Fail(accepted.Error,accepted.Message!);
            var session=EndpointSessions.ResolveForService(invocation.Session,service);
            if(!session.IsSuccess)return KernelResult<EndpointSessionInvocationRegistry.OriginalPublication>.Fail(session.Error,session.Message!);
            var response=Responses.CapturePublication(service,session.Value!.ServiceEndpoint,invocation.InvocationId.Value);
            if(!response.IsSuccess)return KernelResult<EndpointSessionInvocationRegistry.OriginalPublication>.Fail(response.Error,response.Message!);
            var bound=SessionInvocations.BindOriginalResponse(invocation,service,response.Value!);
            return bound.IsSuccess ? SessionInvocations.CapturePublication(invocation,service,null!) :
                KernelResult<EndpointSessionInvocationRegistry.OriginalPublication>.Fail(bound.Error,bound.Message!);
        }
    }

    internal KernelResult ReadOriginalSessionCancelledLifetime(EndpointSessionInvocationRegistry.OriginalPublication original,
        EndpointSessionInvocationHandle invocation,ProcessHandle service,ChannelEnvelope request)
    {
        lock(_requestResponseCorrelationGate)
        {
        var cancelled=SessionInvocations.ReadOriginalCancelledLifetime(original,invocation,service,request);
        return cancelled.IsSuccess || cancelled.Error!=KernelError.ExternalEffectUncontained ? cancelled :
            ReadOriginalSessionChannelCancellation(original);
        }
    }

    /// <summary>Returns this caller's existing admitted parent correlation; not permission.</summary>
    public KernelResult<PlatformDomainBinding> QueryVirtualDomainPlatformBinding(ProcessHandle owner,VirtualDomainHandle domain)
    {
        var original=_virtualDomains.ResolveAdmission(owner,domain);
        return original.IsSuccess && original.Value!.ParentBinding is { } parent
            ? KernelResult<PlatformDomainBinding>.Ok(parent)
            : KernelResult<PlatformDomainBinding>.Fail(original.IsSuccess?KernelError.PlatformUnsupported:original.Error,original.Message??"Virtual parent binding unavailable.");
    }

    private readonly VirtualDomainAuthority _virtualDomains = new();

    public KernelResult<VirtualDomainAuthoritySet> CreateVirtualDomain(ProcessHandle owner, CapabilityId createCapability, VirtualDomainProfile profile)
        => CreateVirtualDomainCore(owner,createCapability,profile,null);

    internal KernelResult<VirtualDomainAuthoritySet> CreateSessionVirtualDomain(ProcessHandle owner,EndpointSessionInvocationHandle invocation,CapabilityId capability,VirtualDomainProfile profile)
        => CreateVirtualDomainCore(owner,capability,profile,invocation);

    private KernelResult<VirtualDomainAuthoritySet> CreateVirtualDomainCore(ProcessHandle owner,CapabilityId createCapability,VirtualDomainProfile profile,EndpointSessionInvocationHandle? invocation)
    {
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return RefuseBeforeProvider(process.Error, process.Message!);
        var capability = ValidateCapability(owner, createCapability, CapabilityRights.Configure);
        if (!capability.IsSuccess) return RefuseBeforeProvider(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.Virtualization || capability.Value.ResourceId != VirtualizationResourceIds.Create)
            return RefuseBeforeProvider(KernelError.WrongCapabilityResource, "Capability does not authorize virtual-domain creation.");
        if (profile.VirtualProcessorCount <= 0 || profile.VirtualProcessorCount > 256 || profile.MaximumGuestMemoryBytes <= 0)
            return RefuseBeforeProvider(KernelError.PlatformDenied, "Virtual-domain profile is outside local bounds.");

        var ownerProcess = process.Value!;

        PlatformFeatureDescriptor childFeature = PlatformAuthority.FeatureManifest.Resolve(PlatformFeatureFamily.ChildDomainLifecycle);
        // Phase5-03 is a lifecycle RuntimeAdmission contour only. An Executable claim must not
        // reuse the entryless child path: it requires the separate Phase5-04 artifact-admission
        // contract before Start can have executable semantics.
        if (childFeature.ContractVersion == PlatformChildDomainContract.ContractVersion &&
            childFeature.Availability is PlatformFeatureAvailability.RuntimeAdmission or PlatformFeatureAvailability.Executable)
        {
            return CreateChildVirtualDomain(owner, ownerProcess, createCapability, profile,invocation);
        }
        return CreateModelVirtualDomain(owner, ownerProcess, createCapability, profile,invocation);

        KernelResult<VirtualDomainAuthoritySet> RefuseBeforeProvider(KernelError error,string message)
        {
            if(invocation is { } original)
            {
                var consumed=SessionInvocations.ConsumeVirtualCreationNeverInvoked(original,owner);
                if(!consumed.IsSuccess)return KernelResult<VirtualDomainAuthoritySet>.Fail(consumed.Error,consumed.Message!);
            }
            return KernelResult<VirtualDomainAuthoritySet>.Fail(error,message);
        }
    }

    private KernelResult<VirtualDomainAuthoritySet> CreateChildVirtualDomain(
        ProcessHandle owner, SingPlus.Sip.SingProcess process, CapabilityId source, VirtualDomainProfile profile,EndpointSessionInvocationHandle? sessionInvocation=null)
    {
        var prepared = _virtualDomains.PrepareIdentity(owner);
        if (!prepared.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(prepared.Error, prepared.Message!);
        using var creation = prepared.Value!;
        var identity = PlatformIdentity(process);
        VirtualDomainAuthority.Record? record = null;
        var originalNeverInvoked=false;
        var originalNoEffect=false;
        KernelResult FreshSubject()
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (!ReferenceEquals(current.Value, process))
                return KernelResult.Fail(KernelError.StaleHandle, "Virtual creation subject changed.");
            return EnsureProcessAcceptsNewEffects(process);
        }
        KernelResult Admit(Func<KernelResult> begin)
        {
            lock (_platformMemoryUseGate)
            {
                var fresh = FreshSubject();
                return fresh.IsSuccess ? begin() : fresh;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainCreation(source, process.DomainId, owner.Generation, commit);

        lock (_platformMemoryUseGate)
        {
            var fresh = FreshSubject();
            if (!fresh.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(fresh.Error, fresh.Message!);
            if (_executionTransactions.Contains(owner) || !_pendingPlatformDomainBinds.Add(owner))
                return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformBindingActive, "Root/virtual creation is already pending.");
        }
        try
        {
            var parent = PlatformAuthority.BindDomain(identity, admissionConsumer: Admit, sourceCommitConsumer: SourceCommit,
                completionConsumer: (binding, commit) =>
                {
                    var attachmentSource = PlatformAuthority.ValidateRootAttachmentSource(binding);
                    lock (_platformMemoryUseGate)
                    {
                        // Receipt is retained for actual teardown even if local admission stopped.
                        TrackPlatformBinding(owner, binding, executionAttached: false);
                        var fresh = FreshSubject();
                        if (fresh.IsSuccess) fresh = attachmentSource;
                        return fresh.IsSuccess ? commit(() => SourceCommit(KernelResult.Ok)) : fresh;
                    }
                },originalCreationConsumer:sessionInvocation is { } invocation?original=>SessionInvocations.BindVirtualCreation(invocation,owner,original):null,
                originalCreationClosureConsumer:sessionInvocation is { } cleanupInvocation?original=>SessionInvocations.ConsumeVirtualCreationClosure(cleanupInvocation,owner,original):null,
                originalCreationNeverInvokedConsumer:original=>
                {
                    var consumed=sessionInvocation is { } neverInvocation?SessionInvocations.ConsumeVirtualCreationNeverInvoked(neverInvocation,owner,original):KernelResult.Ok();
                    if(consumed.IsSuccess)originalNeverInvoked=true;
                    return consumed;
                },originalCreationNoEffectConsumer:original=>
                {
                    var consumed=sessionInvocation is { } noEffectInvocation?SessionInvocations.ConsumeVirtualCreationNoEffect(noEffectInvocation,owner,original):KernelResult.Ok();
                    if(consumed.IsSuccess)originalNoEffect=true;
                    return consumed;
                });
            if (!parent.IsSuccess)
            {
                _=PlatformAuthority.ResolveOriginalAdmissionsForCleanup(identity);
                if (PlatformAuthority.TryGetQuarantinedDomainBinding(identity, out var cleanup))
                    TrackPlatformBinding(owner, cleanup, executionAttached: false);
                return KernelResult<VirtualDomainAuthoritySet>.Fail(parent.Error, parent.Message!);
            }
            var authority = PlatformChildAuthorityClass.Lifecycle | PlatformChildAuthorityClass.Execution |
                PlatformChildAuthorityClass.GuestMemory | PlatformChildAuthorityClass.Events | PlatformChildAuthorityClass.Traps;
            if (PlatformAuthority.FeatureManifest.Resolve(PlatformFeatureFamily.BoundedVirtualIo).Availability is PlatformFeatureAvailability.RuntimeAdmission or PlatformFeatureAvailability.Executable)
                authority |= PlatformChildAuthorityClass.Io;
            var intent = new PlatformChildDomainIntent(new(profile.VirtualProcessorCount, profile.MaximumGuestMemoryBytes), new(authority, authority));
            var child = PlatformAuthority.CreateChildDomain(parent.Value!, identity, intent, Admit, SourceCommit,
                cleanupConsumer: binding =>
                {
                    lock (_platformMemoryUseGate)
                        record = _virtualDomains.AddChild(creation, owner, parent.Value!, binding, profile, [], authority, authorityPublished: false);
                });
            if (!child.IsSuccess)
            {
                if (record is not null) record.State = VirtualDomainState.Quarantined;
                var closed = PlatformAuthority.RevokeDomain(parent.Value!, identity);
                if (closed.IsSuccess) ConsumeTrackedVirtualRootClosure(owner, parent.Value!);
                // Failed compensation keeps the exact root in the actual process consumer.
                return KernelResult<VirtualDomainAuthoritySet>.Fail(child.Error, child.Message!);
            }
            if (record is null) throw new InvalidOperationException("Known child cleanup owner was not delivered.");
            lock (_platformMemoryUseGate)
            {
                var fresh = FreshSubject();
                var permitted = fresh.IsSuccess ? SourceCommit(KernelResult.Ok) : fresh;
                if (!permitted.IsSuccess)
                {
                    record.State = VirtualDomainState.Quarantined;
                    return KernelResult<VirtualDomainAuthoritySet>.Fail(permitted.Error, permitted.Message!);
                }
            }

            return IssueVirtualDomainAuthority(owner, process, source, record,
                commit => PlatformAuthority.CommitActiveDomainConsumer(parent.Value!, identity, commit),
                publicationAdmission: () => PlatformAuthority.ValidateRootAttachmentSource(parent.Value!));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (record is not null) record.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformFaulted,
                $"Virtual creation did not publish a complete result; known cleanup remains tracked: {exception.Message}");
        }
        finally
        {
            lock(_platformMemoryUseGate)
            {
                if(originalNeverInvoked || originalNoEffect)ConsumeNeverInvokedVirtualCreationTeardown(owner,process,sessionInvocation);
                _pendingPlatformDomainBinds.Remove(owner);
            }
        }
    }

    private KernelResult<VirtualDomainAuthoritySet> CreateModelVirtualDomain(
        ProcessHandle owner, SingPlus.Sip.SingProcess process, CapabilityId source, VirtualDomainProfile profile,EndpointSessionInvocationHandle? sessionInvocation=null)
    {
        var prepared = _virtualDomains.PrepareIdentity(owner);
        if (!prepared.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(prepared.Error, prepared.Message!);
        using var creation = prepared.Value!;
        VirtualDomainAuthority.Record? record = null;
        var originalNeverInvoked=false;
        KernelResult Fresh()
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            return ReferenceEquals(current.Value, process) ? EnsureProcessAcceptsNewEffects(process) :
                KernelResult.Fail(KernelError.StaleHandle, "Model creation subject changed.");
        }
        KernelResult Admit(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var fresh = Fresh();
                return fresh.IsSuccess ? commit() : fresh;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainCreation(source, process.DomainId, owner.Generation, commit);
        lock (_platformMemoryUseGate)
        {
            var fresh = Fresh();
            if (!fresh.IsSuccess) return KernelResult<VirtualDomainAuthoritySet>.Fail(fresh.Error, fresh.Message!);
            if (_executionTransactions.Contains(owner) || !_pendingPlatformDomainBinds.Add(owner))
                return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformBindingActive, "Model/root creation is pending.");
        }
        try
        {
            var model = PlatformAuthority.CreateVirtualDomain(PlatformIdentity(process),
                new(profile.VirtualProcessorCount, profile.MaximumGuestMemoryBytes), Admit, SourceCommit,
                completionConsumer: (binding, commit) =>
                {
                    lock (_platformMemoryUseGate)
                    {
                        var fresh = Fresh();
                        return fresh.IsSuccess ? commit(() => SourceCommit(KernelResult.Ok)) : fresh;
                    }
                }, cleanupConsumer: binding =>
                {
                    lock (_platformMemoryUseGate)
                        record = _virtualDomains.AddModel(creation, owner, binding, profile, [], authorityPublished: false);
                },originalCreationConsumer:sessionInvocation is { } invocation?original=>SessionInvocations.BindVirtualCreation(invocation,owner,original):null,
                originalCreationClosureConsumer:sessionInvocation is { } cleanupInvocation?original=>SessionInvocations.ConsumeVirtualCreationClosure(cleanupInvocation,owner,original):null,
                originalCreationNoEffectConsumer:sessionInvocation is { } noEffectInvocation?original=>SessionInvocations.ConsumeVirtualCreationNoEffect(noEffectInvocation,owner,original):null,
                originalCreationNeverInvokedConsumer:original=>
                {
                    var consumed=sessionInvocation is { } neverInvocation?SessionInvocations.ConsumeVirtualCreationNeverInvoked(neverInvocation,owner,original):KernelResult.Ok();
                    if(consumed.IsSuccess)originalNeverInvoked=true;
                    return consumed;
                });
            if (!model.IsSuccess)
            {
                if (record is not null) record.State = VirtualDomainState.Quarantined;
                return KernelResult<VirtualDomainAuthoritySet>.Fail(model.Error, model.Message!);
            }
            return IssueVirtualDomainAuthority(owner, process, source, record!,
                commit => PlatformAuthority.CommitModelDomainConsumer(model.Value!, commit));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            if (record is not null) record.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualDomainAuthoritySet>.Fail(KernelError.PlatformFaulted,
                $"Model creation did not publish authority; cleanup remains owned: {exception.Message}");
        }
        finally
        {
            lock (_platformMemoryUseGate)
            {
                if(originalNeverInvoked)ConsumeNeverInvokedVirtualCreationTeardown(owner,process,sessionInvocation);
                _pendingPlatformDomainBinds.Remove(owner);
            }
        }
    }

    // Caller holds platformMemoryUseGate; invoked only from same-original pre-effect retirement.
    private void ConsumeNeverInvokedVirtualCreationTeardown(ProcessHandle owner,SingPlus.Sip.SingProcess process,EndpointSessionInvocationHandle? invocation)
    {
        if(_processTeardowns.TryGetValue(owner,out var teardown) && teardown.DomainBinding is null &&
            !_processPlatformBindings.ContainsKey(owner) && !PlatformAuthority.HasUnresolvedDomainBindEffect(PlatformIdentity(process)) &&
            !PlatformAuthority.HasActiveAuthority(PlatformIdentity(process)))
        {
            teardown.PlatformDomainClosed=true;
            if(teardown.ChannelsClosed && invocation is { } stoppedInvocation)
                _=SessionInvocations.RetireNoEffectVirtualCreationReply(stoppedInvocation,owner);
        }
    }

    internal Action? BeforeVirtualAuthorityPublicationQualificationHook { get; set; }

    private KernelResult<VirtualDomainAuthoritySet> IssueVirtualDomainAuthority(
        ProcessHandle owner, SingPlus.Sip.SingProcess process, CapabilityId source,
        VirtualDomainAuthority.Record record, Func<Func<KernelResult>, KernelResult> bindingCommit,
        Func<Func<KernelResult>, KernelResult>? sourceCommit = null,
        Func<KernelResult>? publicationAdmission = null)
    {
        List<CapabilityDescriptorV1> mintObservations = [];
        var authorityPublished = false;
        try
        {
        BeforeVirtualAuthorityPublicationQualificationHook?.Invoke();
        var localSource = _virtualDomains.ValidateActiveAncestors(record);
        if (!localSource.IsSuccess)
        {
            record.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualDomainAuthoritySet>.Fail(localSource.Error, localSource.Message!);
        }
        var publicationSource = publicationAdmission?.Invoke() ?? KernelResult.Ok();
        if (!publicationSource.IsSuccess)
        {
            record.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualDomainAuthoritySet>.Fail(publicationSource.Error, publicationSource.Message!);
        }
        KernelResult<CapabilityDescriptorV1> CommitDerived(Func<KernelResult<CapabilityDescriptorV1>> commit)
        {
            var issued = KernelResult<CapabilityDescriptorV1>.Fail(KernelError.PlatformFaulted, "Derived source commit did not run.");
            var boundary = bindingCommit(() =>
            {
                var localAdmission = _virtualDomains.ValidateActiveAncestors(record);
                if (!localAdmission.IsSuccess) return localAdmission;
                if (sourceCommit is null)
                    issued = CapabilityAuthority.CommitVirtualDomainCreation(source, process.DomainId, owner.Generation, commit);
                else
                {
                    var accepted = sourceCommit(() =>
                    {
                        issued = commit();
                        return issued.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(issued.Error, issued.Message!);
                    });
                    if (!accepted.IsSuccess) issued = KernelResult<CapabilityDescriptorV1>.Fail(accepted.Error, accepted.Message!);
                }
                return issued.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(issued.Error, issued.Message!);
            });
            return boundary.IsSuccess ? issued : KernelResult<CapabilityDescriptorV1>.Fail(boundary.Error, boundary.Message!);
        }
        var requests = new (string Resource, CapabilityRights Rights)[]
        {
            (VirtualizationResourceIds.Domain(record.Handle.DomainId), CapabilityRights.Configure),
            (VirtualizationResourceIds.Memory(record.Handle.DomainId), CapabilityRights.Map | CapabilityRights.Read | CapabilityRights.Write),
            (VirtualizationResourceIds.Domain(record.Handle.DomainId), CapabilityRights.Execute),
            (VirtualizationResourceIds.Events(record.Handle.DomainId), CapabilityRights.Signal),
            (VirtualizationResourceIds.Traps(record.Handle.DomainId), CapabilityRights.Read)
        };
        var capabilities = new List<CapabilityId>();
        var returned = new CapabilityId[requests.Length];
        for (var index = 0; index < requests.Length; index++)
        {
            var request = requests[index];
            var needed = index switch
            {
                1 => PlatformChildAuthorityClass.GuestMemory,
                2 => PlatformChildAuthorityClass.Execution,
                3 => PlatformChildAuthorityClass.Events,
                4 => PlatformChildAuthorityClass.Traps,
                _ => PlatformChildAuthorityClass.Lifecycle,
            };
            if (record.ParentDomain is not null && !record.Authority.HasFlag(needed)) continue;
            var minted = MintCapabilityCore(process.DomainId, owner, ResourceKind.Virtualization,
                request.Resource, request.Rights, sourceCommitConsumer: CommitDerived,
                mintObservations: mintObservations);
            if (!minted.IsSuccess)
            {
                record.State = VirtualDomainState.Quarantined;
                return KernelResult<VirtualDomainAuthoritySet>.Fail(minted.Error, minted.Message!);
            }
            returned[index] = minted.Value!.CapabilityId;
            capabilities.Add(minted.Value!.CapabilityId);
            record.Capabilities = capabilities.ToArray();
        }
        publicationSource = publicationAdmission?.Invoke() ?? KernelResult.Ok();
        KernelResult published;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            var fresh = current.IsSuccess && ReferenceEquals(current.Value, process)
                ? EnsureProcessAcceptsNewEffects(process) :
                KernelResult.Fail(current.IsSuccess ? KernelError.StaleHandle : current.Error, "Virtual publication subject changed.");
            if (fresh.IsSuccess) fresh = publicationSource;
            KernelResult CommitPublication() => _virtualDomains.CommitAuthorityPublication(record);
            published = fresh.IsSuccess ? bindingCommit(() => sourceCommit is null
                ? CapabilityAuthority.CommitVirtualDomainCreation(source, process.DomainId, owner.Generation, CommitPublication)
                : sourceCommit(CommitPublication)) : fresh;
        }
        if (!published.IsSuccess)
        {
            record.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualDomainAuthoritySet>.Fail(published.Error, published.Message!);
        }
        authorityPublished = true;
        return KernelResult<VirtualDomainAuthoritySet>.Ok(new(record.Handle, record.AddressSpace,
            returned[0], returned[1], returned[2], returned[3], returned[4]));
        }
        finally
        {
            foreach (var minted in mintObservations)
                RecordTrace(owner, TraceEventKind.CapabilityMinted, null, "capability",
                    minted.CapabilityId.Value.ToString(), "active", "minted");
            if (authorityPublished)
                RecordTrace(owner, TraceEventKind.VirtualDomainLifecycle, null, "virtual-domain",
                    record.Handle.DomainId.Value.ToString(), VirtualDomainState.Created.ToString(), "created");
        }
    }

    private void ConsumeTrackedVirtualRootClosure(ProcessHandle owner, PlatformDomainBinding binding)
    {
        lock (_platformMemoryUseGate)
        {
            UntrackPlatformBinding(owner, binding);
            if (_processTeardowns.TryGetValue(owner, out var teardown) && teardown.DomainBinding == binding)
                teardown.PlatformDomainClosed = true;
        }
    }

    public KernelResult<VirtualDomainAuthoritySet> CreateNestedVirtualDomain(
        ProcessHandle owner, VirtualDomainHandle parentDomain, CapabilityId parentCapability,
        NestedVirtualDomainRequestProfile requested)
        => CreateNestedVirtualDomainCore(owner,parentDomain,parentCapability,requested,null);

    internal KernelResult<VirtualDomainAuthoritySet> CreateSessionNestedVirtualDomain(ProcessHandle owner,
        EndpointSessionInvocationHandle invocation,VirtualDomainHandle parentDomain,CapabilityId parentCapability,
        NestedVirtualDomainRequestProfile requested)
        => CreateNestedVirtualDomainCore(owner,parentDomain,parentCapability,requested,invocation);

    private KernelResult<VirtualDomainAuthoritySet> CreateNestedVirtualDomainCore(
        ProcessHandle owner,VirtualDomainHandle parentDomain,CapabilityId parentCapability,
        NestedVirtualDomainRequestProfile requested,EndpointSessionInvocationHandle? sessionInvocation)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: parentDomain);
        if (!composedUse.Result.IsSuccess) return RefuseBeforeProvider(composedUse.Result.Error, composedUse.Result.Message!);
        var parent = ResolveVirtualCapability(owner, parentDomain, parentCapability,
            VirtualizationResourceIds.Domain(parentDomain.DomainId), CapabilityRights.Configure);
        if (!parent.IsSuccess)
            return RefuseBeforeProvider(parent.Error, parent.Message!);
        if (composedUse.HasBindings)
            return RefuseBeforeProvider(KernelError.PlatformUnsupported, "Nested secure execution is not admitted by Phase 00.");
        if (parent.Value!.State is not VirtualDomainState.Configured and not VirtualDomainState.Parked)
            return RefuseBeforeProvider(KernelError.InvalidTransition,
                "Nested domains may be admitted only while the exact parent is configured or parked.");
        if (parent.Value.ChildBinding is not { } parentChild || parent.Value.ParentBinding is null)
            return RefuseBeforeProvider(KernelError.PlatformUnsupported,
                "The exact parent has no provider child authority for nesting.");

        var nestedFeature = PlatformAuthority.FeatureManifest.Resolve(PlatformFeatureFamily.NestedDomains);
        if (nestedFeature.ContractVersion < PlatformNestedDomainContract.ContractVersion ||
            nestedFeature.Availability is not (PlatformFeatureAvailability.RuntimeAdmission or PlatformFeatureAvailability.Executable))
            return RefuseBeforeProvider(KernelError.PlatformUnsupported,
                "Nested-domain provider admission is unavailable.");
        if (requested.Profile.VirtualProcessorCount <= 0 || requested.Profile.VirtualProcessorCount > 256 ||
            requested.Profile.MaximumGuestMemoryBytes <= 0 ||
            requested.Profile.MaximumGuestMemoryBytes > parent.Value.Profile.MaximumGuestMemoryBytes)
            return RefuseBeforeProvider(KernelError.PlatformDenied,
                "Nested-domain profile exceeds the exact parent bounds.");

        const VirtualDomainAuthorityClass knownAuthority = VirtualDomainAuthorityClass.Lifecycle | VirtualDomainAuthorityClass.Execution |
            VirtualDomainAuthorityClass.GuestMemory | VirtualDomainAuthorityClass.Events | VirtualDomainAuthorityClass.Traps | VirtualDomainAuthorityClass.Io;
        if ((requested.Authority & ~knownAuthority) != 0)
            return RefuseBeforeProvider(KernelError.PlatformDenied, "Unknown mandatory nested authority.");
        PlatformChildAuthorityClass authority = ToPlatformAuthority(requested.Authority);
        if ((authority & PlatformChildAuthorityClass.Lifecycle) == 0 ||
            (authority & parent.Value.Authority) != authority)
            return RefuseBeforeProvider(KernelError.PlatformDenied,
                "Nested-domain authority must include lifecycle and remain an exact parent subset.");

        var intent = new PlatformChildDomainIntent(
            new(requested.Profile.VirtualProcessorCount, requested.Profile.MaximumGuestMemoryBytes),
            new(authority, authority));
        SingPlus.Sip.SingProcess process;
        var preparedIdentity = _virtualDomains.PrepareIdentity(owner);
        if (!preparedIdentity.IsSuccess) return RefuseBeforeProvider(preparedIdentity.Error, preparedIdentity.Message!);
        using var creation = preparedIdentity.Value!;
        var capturedParent = parent.Value;
        var parentState = capturedParent.State;
        var effectSource = _virtualDomains.CaptureEffectSource(capturedParent, parentState);
        if (!effectSource.IsSuccess) return RefuseBeforeProvider(effectSource.Error, effectSource.Message!);
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return RefuseBeforeProvider(current.Error, current.Message!);
            process = current.Value!;
            if (!_pendingPlatformDomainBinds.Add(owner))
                return RefuseBeforeProvider(KernelError.PlatformBindingActive, "Virtual creation is pending for this owner.");
        }
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(owner);
                if (!current.IsSuccess || !ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleHandle, "Nested process identity changed.");
                var accepts = EnsureProcessAcceptsNewEffects(process);
                if (!accepts.IsSuccess) return accepts;
                var local = _virtualDomains.ResolveAdmission(owner, parentDomain);
                if (!local.IsSuccess) return KernelResult.Fail(local.Error, local.Message!);
                if (!ReferenceEquals(local.Value, capturedParent) || capturedParent.State != parentState ||
                    capturedParent.ChildBinding != parentChild)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Exact nested local parent changed.");
                var lineage = _virtualDomains.ValidateActiveAncestors(capturedParent);
                return lineage.IsSuccess ? commit() : lineage;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainEffect(parentCapability, process.DomainId, owner.Generation,
                CapabilityRights.Configure, VirtualizationResourceIds.Domain(parentDomain.DomainId), () => _virtualDomains.CommitEffectAdmission(capturedParent, parentState, commit, effectSource.Value!));
        VirtualDomainAuthority.Record? record = null;
        try
        {
            var nested = PlatformAuthority.CreateNestedChildDomain(parentChild, owner, intent, Fresh, SourceCommit,
                cleanupConsumer: binding =>
                {
                    lock (_platformMemoryUseGate)
                        record = _virtualDomains.AddNested(creation, owner, capturedParent, binding, requested.Profile, [], authority, authorityPublished: false);
                },originalCreationConsumer:sessionInvocation is { } admittedInvocation ? original=>SessionInvocations.BindVirtualCreation(admittedInvocation,owner,original):null,
                originalCreationClosureConsumer:sessionInvocation is { } cleanupInvocation ? original=>SessionInvocations.ConsumeVirtualCreationClosure(cleanupInvocation,owner,original):null,
                originalCreationNoEffectConsumer:sessionInvocation is { } noEffectInvocation ? original=>SessionInvocations.ConsumeVirtualCreationNoEffect(noEffectInvocation,owner,original):null,
                originalCreationNeverInvokedConsumer:sessionInvocation is { } neverInvocation ? original=>SessionInvocations.ConsumeVirtualCreationNeverInvoked(neverInvocation,owner,original):null);
            if (!nested.IsSuccess)
            {
                if (record is not null) record.State = VirtualDomainState.Quarantined;
                if (RequiresVirtualDomainQuarantine(nested.Error)) capturedParent.State = VirtualDomainState.Quarantined;
                return KernelResult<VirtualDomainAuthoritySet>.Fail(nested.Error, nested.Message!);
            }
            if (record is null) throw new InvalidOperationException("Known nested cleanup owner was not delivered.");
            KernelResult BindingCommit(Func<KernelResult> commit) =>
                Fresh(() => PlatformAuthority.CommitActiveChildDomainConsumer(nested.Value!, commit));
            return IssueVirtualDomainAuthority(owner, process, parentCapability, record, BindingCommit, SourceCommit);
        }
        finally { lock (_platformMemoryUseGate) _pendingPlatformDomainBinds.Remove(owner); }

        KernelResult<VirtualDomainAuthoritySet> RefuseBeforeProvider(KernelError error,string message)
        {
            var consumed=sessionInvocation is { } original ? SessionInvocations.ConsumeVirtualCreationNeverInvoked(original,owner) : KernelResult.Ok();
            return consumed.IsSuccess ? KernelResult<VirtualDomainAuthoritySet>.Fail(error,message) :
                KernelResult<VirtualDomainAuthoritySet>.Fail(consumed.Error,consumed.Message!);
        }
    }

    public KernelResult ConfigureVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId configureCapability)
        => ConfigureVirtualDomainCore(owner,domain,configureCapability,null);

    internal KernelResult ConfigureSessionVirtualDomain(ProcessHandle owner,EndpointSessionInvocationHandle invocation,
        VirtualDomainHandle domain,CapabilityId capability) => ConfigureVirtualDomainCore(owner,domain,capability,invocation);

    private KernelResult ConfigureVirtualDomainCore(ProcessHandle owner,VirtualDomainHandle domain,CapabilityId configureCapability,
        EndpointSessionInvocationHandle? invocation)
    {
        KernelResult Refuse(KernelError error,string message) => RefuseVirtualTransitionBeforeEffect(owner,invocation,null,error,message);
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return Refuse(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, configureCapability, VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Configure);
        if (!record.IsSuccess) return Refuse(record.Error, record.Message!);
        if (record.Value!.State != VirtualDomainState.Created) return Refuse(KernelError.InvalidTransition, "Virtual domain can only be configured from Created.");
        if(record.Value.ModelBinding is null && record.Value.ChildBinding is null)return Refuse(KernelError.PlatformBindingNotFound,"Virtual domain has no child binding.");
        var bound=invocation is { } original ? SessionInvocations.BindVirtualTransition(original,owner,record.Value,domain) : KernelResult.Ok();
        if(!bound.IsSuccess)return bound;
        if (record.Value.ModelBinding is { } model)
        {
            return TransitionModelVirtualDomain(owner, domain, configureCapability, record.Value, model,
                VirtualDomainState.Created, VirtualDomainState.Configured, PlatformVirtualDomainTransition.Configure, CapabilityRights.Configure,invocation);
        }
        if (record.Value.ChildBinding is not { } child)
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Virtual domain has no child binding.");
        return TransitionChildVirtualDomain(owner, domain, configureCapability, record.Value, child,
            VirtualDomainState.Created, VirtualDomainState.Configured, null,invocation);
    }

    public KernelResult<GuestRegionMapping> MapGuestRegion(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId memoryCapability, CapabilityId regionCapability, RegionHandle region, GuestAddressRange range, GuestMemoryAccess access)
        => MapGuestRegion(owner, domain, memoryCapability, regionCapability, region, range, access, 0);

    public KernelResult<GuestRegionMapping> MapGuestRegion(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId memoryCapability, CapabilityId regionCapability, RegionHandle region, GuestAddressRange range, GuestMemoryAccess access, ulong maximumPrivateExecutableDataBytes)
        => MapGuestRegionInvocation(owner,domain,memoryCapability,regionCapability,region,range,access,maximumPrivateExecutableDataBytes,null);

    internal KernelResult<GuestRegionMapping> MapSessionGuestRegion(ProcessHandle owner,EndpointSessionInvocationHandle invocation,
        VirtualDomainHandle domain,CapabilityId memoryCapability,CapabilityId regionCapability,RegionHandle region,
        GuestAddressRange range,GuestMemoryAccess access,ulong quota)
        => MapGuestRegionInvocation(owner,domain,memoryCapability,regionCapability,region,range,access,quota,invocation);

    private KernelResult<GuestRegionMapping> MapGuestRegionInvocation(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId memoryCapability, CapabilityId regionCapability, RegionHandle region, GuestAddressRange range, GuestMemoryAccess access, ulong maximumPrivateExecutableDataBytes,EndpointSessionInvocationHandle? invocation)
    {
        using var pin = PinComposedDomain(owner, virtualDomain: domain);
        if (!pin.Result.IsSuccess) return KernelResult<GuestRegionMapping>.Fail(pin.Result.Error, pin.Result.Message!);
        var resolved = ResolveVirtualCapability(owner, domain, memoryCapability, VirtualizationResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!resolved.IsSuccess) return KernelResult<GuestRegionMapping>.Fail(resolved.Error, resolved.Message!);
        var captured = resolved.Value!;
        if (captured.State is not (VirtualDomainState.Configured or VirtualDomainState.Parked))
            return KernelResult<GuestRegionMapping>.Fail(KernelError.InvalidTransition, "Guest mapping requires configured or parked local state.");
        var rights = CapabilityRights.Map |
            ((access & GuestMemoryAccess.Read) != 0 ? CapabilityRights.Read : CapabilityRights.None) |
            ((access & GuestMemoryAccess.Write) != 0 ? CapabilityRights.Write : CapabilityRights.None) |
            ((access & GuestMemoryAccess.Execute) != 0 ? CapabilityRights.Execute : CapabilityRights.None);
        var current = Processes.Resolve(owner);
        if (!current.IsSuccess) return KernelResult<GuestRegionMapping>.Fail(current.Error, current.Message!);
        KernelResult<GuestRegionMapping> result = default;
        var operation = UseVirtualDeliverySource(owner, domain, memoryCapability, captured, captured.ChildBinding,
            CapabilityRights.Map, VirtualizationResourceIds.Memory(domain.DomainId), captured.State, (fresh, memoryCommit) =>
        {
            KernelResult DualCommit(Func<KernelResult> commit) => CapabilityAuthority.CommitGuestRegionSource(
                regionCapability, current.Value!.DomainId, owner.Generation, rights,
                CapabilityResourceIds.MemoryRegion(region.RegionId), () => memoryCommit(commit));
            result = MapGuestRegionCore(owner, domain, memoryCapability, regionCapability, region, range, access, fresh, DualCommit, maximumPrivateExecutableDataBytes,invocation);
            return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
        });
        return operation.IsSuccess ? result : KernelResult<GuestRegionMapping>.Fail(operation.Error, operation.Message!);
    }

    private KernelResult<GuestRegionMapping> MapGuestRegionCore(ProcessHandle owner, VirtualDomainHandle domain,
        CapabilityId memoryCapability, CapabilityId regionCapability, RegionHandle region, GuestAddressRange range,
        GuestMemoryAccess access, Func<Func<KernelResult>, KernelResult> fresh, Func<Func<KernelResult>, KernelResult> sourceCommit, ulong maximumPrivateExecutableDataBytes = 0,EndpointSessionInvocationHandle? sessionInvocation=null)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult<GuestRegionMapping>.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, memoryCapability, VirtualizationResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return KernelResult<GuestRegionMapping>.Fail(record.Error, record.Message!);
        if (maximumPrivateExecutableDataBytes > 0x3000_0000 || maximumPrivateExecutableDataBytes % 4096 != 0 ||
            maximumPrivateExecutableDataBytes != 0 && (record.Value!.ChildBinding is null || (access & GuestMemoryAccess.Execute) == 0))
            return KernelResult<GuestRegionMapping>.Fail(KernelError.PlatformDenied, "Private data quota requires a page-aligned bounded executable provider mapping.");
        if (record.Value!.State is not VirtualDomainState.Configured and not VirtualDomainState.Parked)
            return KernelResult<GuestRegionMapping>.Fail(KernelError.InvalidTransition, "Guest memory can only be mapped while configured or parked.");
        const GuestMemoryAccess supportedAccess = GuestMemoryAccess.Read | GuestMemoryAccess.Write | GuestMemoryAccess.Execute;
        if (range.ByteLength <= 0 || range.GuestAddress > ulong.MaxValue - (ulong)range.ByteLength || range.GuestAddress + (ulong)range.ByteLength > (ulong)record.Value.Profile.MaximumGuestMemoryBytes || access == GuestMemoryAccess.None || (access & ~supportedAccess) != 0)
            return KernelResult<GuestRegionMapping>.Fail(KernelError.PlatformDenied, "Guest range or access is invalid.");

        CapabilityRights requiredRegionRights = CapabilityRights.Map;
        if ((access & GuestMemoryAccess.Read) != 0) requiredRegionRights |= CapabilityRights.Read;
        if ((access & GuestMemoryAccess.Write) != 0) requiredRegionRights |= CapabilityRights.Write;
        if ((access & GuestMemoryAccess.Execute) != 0) requiredRegionRights |= CapabilityRights.Execute;
        var regionAuthority = ValidateCapability(owner, regionCapability, requiredRegionRights);
        if (!regionAuthority.IsSuccess || regionAuthority.Value!.ResourceKind != ResourceKind.MemoryRegion || regionAuthority.Value.ResourceId != CapabilityResourceIds.MemoryRegion(region.RegionId))
            return KernelResult<GuestRegionMapping>.Fail(regionAuthority.IsSuccess ? KernelError.WrongCapabilityResource : regionAuthority.Error, regionAuthority.Message ?? "Region mapping capability does not match the exact region and requested access.");
        var process = Processes.Resolve(owner).Value!;
        var staged = _virtualDomains.AddMapping(record.Value, range, region, access, memoryCapability, regionCapability);
        if (!staged.IsSuccess) return staged;
        var local = staged.Value!;
        var budget = record.Value.ChildBinding is not null && record.Value.ParentBinding is not null
            ? ReserveAttachedBudget(owner,
                maximumPrivateExecutableDataBytes == 0
                    ? [new(ServiceBudgetDimension.PinnedMappedMemoryBytes, checked((ulong)range.ByteLength)), new(ServiceBudgetDimension.GuestMemoryBytes, checked((ulong)range.ByteLength))]
                    : [new(ServiceBudgetDimension.PinnedMappedMemoryBytes, checked((ulong)range.ByteLength)), new(ServiceBudgetDimension.GuestMemoryBytes, checked((ulong)range.ByteLength)), new(ServiceBudgetDimension.OwnedMemoryBytes, maximumPrivateExecutableDataBytes)],
                BudgetReservationLifetime.PlatformMapping)
            : KernelResult<BudgetReservationHandle?>.Ok(null);
        if (!budget.IsSuccess)
        {
            _ = _virtualDomains.RemoveMapping(record.Value, local.Mapping, local);
            return KernelResult<GuestRegionMapping>.Fail(budget.Error, budget.Message!);
        }
        var originalBudgetOwner = Budgets.CaptureOriginalMappingReservationOwner(owner, budget.Value);
        var reservation = fresh(() => sourceCommit(() => record.Value.ChildBinding is null
            ? ReserveLocalGuest(record.Value, local, owner, new(process.DomainId, owner.Generation))
            : Regions.ReservePlatformMapping(region, new RegionOwner(process.DomainId, owner.Generation))));
        if (!reservation.IsSuccess)
        {
            if (record.Value.ChildBinding is null)
            {
                var cleanup = RemoveClosedGuestMapping(record.Value, local, owner, false);
                return KernelResult<GuestRegionMapping>.Fail(cleanup.IsSuccess ? reservation.Error : cleanup.Error,
                    cleanup.IsSuccess ? reservation.Message! : cleanup.Message!);
            }
            _ = ReleaseAttachedBudget(owner, budget.Value);
            var rollback = _virtualDomains.RemoveMapping(record.Value, local.Mapping, local);
            return KernelResult<GuestRegionMapping>.Fail(rollback.IsSuccess ? reservation.Error : rollback.Error,
                rollback.IsSuccess ? reservation.Message! : rollback.Message!);
        }
        if (record.Value.ChildBinding is { } child && record.Value.ParentBinding is { } parent)
        {
            if(sessionInvocation is { } invocation)
            {
                var linked=fresh(()=>sourceCommit(()=>_virtualDomains.CommitStagedGuestInvocation(record.Value,local,
                    ()=>SessionInvocations.BindVirtualGuestMapping(invocation,owner,record.Value,local.Mapping))));
                if(!linked.IsSuccess)
                {
                    var released=Regions.ReleasePlatformMappingReservation(region,new(process.DomainId,owner.Generation));
                    if(!released.IsSuccess)return KernelResult<GuestRegionMapping>.Fail(released.Error,released.Message!);
                    released=ReleaseAttachedBudget(owner,budget.Value);
                    if(!released.IsSuccess)return KernelResult<GuestRegionMapping>.Fail(released.Error,released.Message!);
                    _=_virtualDomains.RemoveMapping(record.Value,local.Mapping,local);
                    return KernelResult<GuestRegionMapping>.Fail(linked.Error,linked.Message!);
                }
            }
            PlatformMemoryAccess parentAccess = PlatformMemoryAccess.None;
            if ((access & (GuestMemoryAccess.Read | GuestMemoryAccess.Execute)) != 0)
                parentAccess |= PlatformMemoryAccess.Read;
            if ((access & GuestMemoryAccess.Write) != 0)
                parentAccess |= PlatformMemoryAccess.Write;
            var preparedParent=_virtualDomains.CommitLocalGuestLifetime(record.Value,local,lifetime=>
            {
                lifetime.Reservation=budget.Value;lifetime.ChargeOwner=originalBudgetOwner;
                lifetime.RegionInvoked=true;lifetime.RegionInvocationKnown=true;lifetime.RegionReserved=true;
                lifetime.ParentAdmissionInvoked=true;
                return KernelResult.Ok();
            });
            if(!preparedParent.IsSuccess)return KernelResult<GuestRegionMapping>.Fail(preparedParent.Error,preparedParent.Message!);
            KernelResult<PlatformRegionMapping> parentMap;
            bool retainParentReservation;
            PlatformRegionMapping? parentCleanup=null;
            if(PlatformAuthority.SupportsOriginalOwnedRegionAdmission)
            {
                var descriptor=Regions.Validate(region,new(process.DomainId,owner.Generation));
                if(!descriptor.IsSuccess)return KernelResult<GuestRegionMapping>.Fail(descriptor.Error,descriptor.Message!);
                var slice=new PlatformRegionSlice(new(region,new(process.DomainId,owner.Generation),descriptor.Value!.ByteLength),0,range.ByteLength,parentAccess);
                var exact=PlatformAuthority.MapOwnedRegionSlice(parent,PlatformIdentity(process),regionCapability,slice,true,
                    out retainParentReservation,out parentCleanup,fresh,sourceCommit);
                parentMap=exact.IsSuccess?KernelResult<PlatformRegionMapping>.Ok(exact.Value.Mapping):KernelResult<PlatformRegionMapping>.Fail(exact.Error,exact.Message!);
            }
            else parentMap=PlatformAuthority.MapOwnedRegion(parent,PlatformIdentity(process),regionCapability,
                new(region,new(process.DomainId,owner.Generation),range.ByteLength),parentAccess,out retainParentReservation,fresh,sourceCommit);
            var retainedParent=_virtualDomains.CommitLocalGuestLifetime(record.Value,local,lifetime=>
            {
                lifetime.ParentMapping=parentMap.IsSuccess?parentMap.Value:parentCleanup;
                lifetime.ParentAdmissionKnown=lifetime.ParentMapping is not null || !retainParentReservation;
                return KernelResult.Ok();
            });
            if(!retainedParent.IsSuccess)return KernelResult<GuestRegionMapping>.Fail(retainedParent.Error,retainedParent.Message!);
            if (!parentMap.IsSuccess)
            {
                if (!retainParentReservation)
                {
                    var observations=new List<BudgetTraceObservation>();
                    var cleanup=ConsumeProviderGuestParent(record.Value,local,owner,new(process.DomainId,owner.Generation),observations);
                    EmitBudgetObservations(observations);
                    if(!cleanup.IsSuccess)return KernelResult<GuestRegionMapping>.Fail(cleanup.Error,cleanup.Message!);
                    _=_virtualDomains.RemoveMapping(record.Value,local.Mapping,local);
                }
                else
                {
                    if(parentCleanup is { } cleanup)BindGuestParentCharge(owner,cleanup,budget.Value,originalBudgetOwner,maximumPrivateExecutableDataBytes);
                    if(parentCleanup is { } originalParent)
                    {
                        var resolved=PlatformAuthority.ResolveOriginalAdmissionsForCleanup(PlatformIdentity(process));
                        var state=resolved.IsSuccess?PlatformAuthority.QueryRegionMappingLifecycle(originalParent,PlatformIdentity(process)):
                            KernelResult<PlatformRegionMappingLifecycle>.Fail(resolved.Error,resolved.Message!);
                        if(state.IsSuccess && state.Value!.AdmissionNoEffectProven)
                        {
                            var closed=CloseParentPlatformMapping(originalParent,PlatformIdentity(process));
                            if(!closed.IsSuccess)return KernelResult<GuestRegionMapping>.Fail(closed.Error,closed.Message!);
                            var observations=new List<BudgetTraceObservation>();
                            var consumed=ConsumeProviderGuestParent(record.Value,local,owner,new(process.DomainId,owner.Generation),observations);
                            EmitBudgetObservations(observations);
                            if(!consumed.IsSuccess)return KernelResult<GuestRegionMapping>.Fail(consumed.Error,consumed.Message!);
                            _=_virtualDomains.RemoveMapping(record.Value,local.Mapping,local);
                            return KernelResult<GuestRegionMapping>.Fail(parentMap.Error,parentMap.Message!);
                        }
                    }
                    record.Value.State = VirtualDomainState.Quarantined;
                }
                return KernelResult<GuestRegionMapping>.Fail(parentMap.Error, parentMap.Message!);
            }
            lock (_platformMemoryUseGate)
            {
                TrackPlatformMapping(owner, parentMap.Value!);
                lock (Budgets.MappingReservationPublicationGate)
                {
                    var bound = PlatformAuthority.BindOriginalMappingBudgetOwner(parentMap.Value!, budget.Value is not null, originalBudgetOwner, maximumPrivateExecutableDataBytes);
                    if (!bound.IsSuccess || budget.Value is not null && !Budgets.BindOriginalMappingChargeOwnerLocked(originalBudgetOwner, bound.Value!).IsSuccess ||
                        !Budgets.ValidateMappingReservationPublicationLocked(owner, budget.Value).IsSuccess)
                        _ = PlatformAuthority.RevokeExactMappingLocalAuthorization(parentMap.Value!, PlatformIdentity(process));
                    if (budget.Value is { } charge)
                        _mappingBudgetReservations.Add(parentMap.Value.MappingId, (owner, charge));
                }
            }
            var composedMap = RevalidateComposedDomain(owner, virtualDomain: domain);
            if (!composedMap.IsSuccess)
            {
                var cleanup = CloseParentPlatformMapping(parentMap.Value!, PlatformIdentity(process));
                if (!cleanup.IsSuccess) record.Value.State = VirtualDomainState.Quarantined;
                else _ = _virtualDomains.RemoveMapping(record.Value, local.Mapping);
                return KernelResult<GuestRegionMapping>.Fail(composedMap.Error, composedMap.Message!);
            }
            var guestMap = PlatformAuthority.MapChildGuestRegion(child, parentMap.Value!,
                new(range.GuestAddress, range.ByteLength), ToPlatformAccess(access), out var knownGuestMapping, fresh, sourceCommit,
                completionConsumer: (known, bridgeCommit) =>
                {
                    var composed = RevalidateComposedDomain(owner, virtualDomain: domain);
                    return composed.IsSuccess ? fresh(() => bridgeCommit(() => sourceCommit(() =>
                    {
                        record.Value.PlatformMappings.Add(local.Mapping.MappingId, (known, parentMap.Value!));
                        return _virtualDomains.PublishMapping(record.Value, local);
                    }))) : composed;
                }, localMappingOwner: record.Value, localMapping: local.Mapping);
            if (!guestMap.IsSuccess)
            {
                if (knownGuestMapping is { } known)
                {
                    // Retain exact cleanup facts independently of denied publication.
                    record.Value.PlatformMappings.TryAdd(local.Mapping.MappingId, (known, parentMap.Value!));
                    var guestCleanup = CloseChildPlatformMapping(record.Value, owner, local.Mapping.MappingId);
                    if (!guestCleanup.IsSuccess)
                    {
                        record.Value.State = VirtualDomainState.Quarantined;
                        return KernelResult<GuestRegionMapping>.Fail(KernelError.PlatformFaulted,
                            $"Late guest mapping refusal retains exact cleanup facts: {guestCleanup.Message}");
                    }
                    return KernelResult<GuestRegionMapping>.Fail(guestMap.Error, guestMap.Message!);
                }
                var cleanup = CloseParentPlatformMapping(parentMap.Value!, PlatformIdentity(process));
                if (!cleanup.IsSuccess)
                {
                    record.Value.State = VirtualDomainState.Quarantined;
                    return KernelResult<GuestRegionMapping>.Fail(
                        KernelError.PlatformFaulted,
                        $"Child guest mapping was rejected ({guestMap.Message}) and exact parent-mapping cleanup was not proven: {cleanup.Message}");
                }
                _ = _virtualDomains.RemoveMapping(record.Value, local.Mapping);
                return KernelResult<GuestRegionMapping>.Fail(guestMap.Error, guestMap.Message!);
            }
            return KernelResult<GuestRegionMapping>.Ok(local); // committed while bridge pending remained held
        }
        var composedPublication = RevalidateComposedDomain(owner, virtualDomain: domain);
        var publication = composedPublication.IsSuccess ? fresh(() =>
            record.Value.ChildBinding is { } publicationChild
                ? PlatformAuthority.CommitActiveChildDomainConsumer(publicationChild, () => sourceCommit(() => _virtualDomains.PublishMapping(record.Value, local)))
                : sourceCommit(() => PublishLocalGuest(record.Value, local, owner))) : composedPublication;
        if (publication.IsSuccess) return KernelResult<GuestRegionMapping>.Ok(local);
        // Provider facts remain owned even when late source permission rejects publication.
        var hasPlatformCleanup = record.Value.PlatformMappings.ContainsKey(local.Mapping.MappingId);
        var publicationCleanup = hasPlatformCleanup
            ? CloseChildPlatformMapping(record.Value, owner, local.Mapping.MappingId)
            : RemoveClosedGuestMapping(record.Value, local, owner, false);
        if (!publicationCleanup.IsSuccess) record.Value.State = VirtualDomainState.Quarantined;
        return KernelResult<GuestRegionMapping>.Fail(publication.Error, publication.Message!);
    }

    public KernelResult CloseGuestRegionMapping(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId memoryCapability, GuestRegionMappingHandle mapping)
        => CloseGuestRegionMappingCore(owner,domain,memoryCapability,mapping,null);

    internal KernelResult CloseSessionGuestRegionMapping(ProcessHandle owner,EndpointSessionInvocationHandle invocation,
        VirtualDomainHandle domain,CapabilityId memoryCapability,GuestRegionMappingHandle mapping)
        => CloseGuestRegionMappingCore(owner,domain,memoryCapability,mapping,invocation);

    private KernelResult CloseGuestRegionMappingCore(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId memoryCapability,
        GuestRegionMappingHandle mapping,EndpointSessionInvocationHandle? invocation)
    {
        KernelResult Refuse(KernelError error,string message) => RefuseVirtualTransitionBeforeEffect(owner,invocation,null,error,message);
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain, allowDraining: true);
        if (!composedUse.Result.IsSuccess) return Refuse(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, memoryCapability, VirtualizationResourceIds.Memory(domain.DomainId), CapabilityRights.Map);
        if (!record.IsSuccess) return Refuse(record.Error, record.Message!);
        if (record.Value!.State is VirtualDomainState.Quarantined or VirtualDomainState.Faulted)
            return Refuse(KernelError.PlatformFaulted, "A quarantined virtual-domain mapping remains pinned and cannot be locally reclaimed.");

        var exactMapping = _virtualDomains.ResolveMapping(record.Value, mapping, allowLocalCleanup: true);
        if (!exactMapping.IsSuccess)
            return Refuse(exactMapping.Error, exactMapping.Message!);
        if (HasVirtualComputePin(mapping))
            return Refuse(KernelError.PlatformBindingDraining, "Guest mapping remains pinned by virtualized provider work.");

        var bound=invocation is { } original ? SessionInvocations.BindVirtualArtifact(original,owner,record.Value,exactMapping.Value!.Mapping) : KernelResult.Ok();
        if(!bound.IsSuccess)return bound;

        var secureClose = CloseSecureGuestRegionsForGuestMapping(owner, mapping);
        if (!secureClose.IsSuccess) return secureClose;

        bool childPlatformMapping = record.Value.PlatformMappings.ContainsKey(mapping.MappingId);
        if (childPlatformMapping)
        {
            var platformClose = CloseChildPlatformMapping(record.Value, owner, mapping.MappingId);
            if (!platformClose.IsSuccess)
            {
                record.Value.State = VirtualDomainState.Quarantined;
                return platformClose;
            }
        }

        return childPlatformMapping ? KernelResult.Ok() : RemoveClosedGuestMapping(record.Value, exactMapping.Value!, owner, false);
    }

    public KernelResult BindVirtualDomainExecutable(
        ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability,
        GuestRegionMappingHandle mapping, ReadOnlyMemory<byte> immutablePackage, int maximumExecutionSteps)
        => BindVirtualDomainExecutableCore(owner,domain,executeCapability,mapping,immutablePackage,maximumExecutionSteps,null);

    internal KernelResult BindSessionVirtualDomainExecutable(ProcessHandle owner,EndpointSessionInvocationHandle invocation,
        VirtualDomainHandle domain,CapabilityId executeCapability,GuestRegionMappingHandle mapping,ReadOnlyMemory<byte> package,int steps)
        => BindVirtualDomainExecutableCore(owner,domain,executeCapability,mapping,package,steps,invocation);

    private KernelResult BindVirtualDomainExecutableCore(
        ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability,
        GuestRegionMappingHandle mapping, ReadOnlyMemory<byte> immutablePackage, int maximumExecutionSteps,
        EndpointSessionInvocationHandle? sessionInvocation)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, executeCapability,
            VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State != VirtualDomainState.Configured || record.Value.ChildBinding is not { } child)
            return KernelResult.Fail(KernelError.InvalidTransition, "Executable artifacts require a configured provider child domain.");
        if (record.Value.ExecutableArtifact is not null)
            return KernelResult.Fail(KernelError.PlatformDenied, "An executable artifact is already bound.");
        var localMapping = _virtualDomains.ResolveMapping(record.Value, mapping);
        if (!localMapping.IsSuccess) return KernelResult.Fail(localMapping.Error, localMapping.Message!);
        var local = localMapping.Value!;
        if ((local.Access & GuestMemoryAccess.Execute) == 0 ||
            !record.Value.PlatformMappings.TryGetValue(mapping.MappingId, out var platform))
            return KernelResult.Fail(KernelError.StaleGeneration, "Exact executable guest mapping is absent, stale, or lacks execute access.");
        var captured = record.Value;
        var privateCharge = PlatformAuthority.QueryOriginalMappingBudgetOwner(platform.Parent);
        if (!privateCharge.IsSuccess || !privateCharge.Value.Known)
            return KernelResult.Fail(KernelError.PlatformFaulted, "Original executable data quota admission is absent.");
        var quota = privateCharge.Value.PrivateDataQuota;
        KernelResult ValidatePrivateQuota()
        {
            var current = PlatformAuthority.QueryOriginalMappingBudgetOwner(platform.Parent);
            if (!current.IsSuccess || !current.Value.Known || current.Value.PrivateDataQuota != quota ||
                current.Value.Expected != privateCharge.Value.Expected || !ReferenceEquals(current.Value.Owner, privateCharge.Value.Owner) ||
                !ReferenceEquals(current.Value.MappingOwner, privateCharge.Value.MappingOwner))
                return KernelResult.Fail(KernelError.PlatformFaulted, "Original private executable data quota source changed.");
            return Budgets.ValidateOriginalPrivateDataQuota(owner, current.Value.Owner, current.Value.MappingOwner, current.Value.Expected, quota);
        }
        var privateAdmission = ValidatePrivateQuota();
        if (!privateAdmission.IsSuccess) return privateAdmission;
        return UseVirtualExecutableSource(owner, domain, executeCapability, captured, child, (fresh, sourceCommit) =>
        {
            if(sessionInvocation is { } invocation)
            {
                var correlation=_virtualDomains.CommitExecutableMappingAdmission(captured,local,platform,
                    ()=>SessionInvocations.BindVirtualArtifact(invocation,owner,captured,local.Mapping));
                if(!correlation.IsSuccess)return correlation;
            }
            KernelResult MappingCommit(Func<KernelResult> commit) =>
                PlatformAuthority.CommitGuestLocalMappingSource(platform.Guest, captured, local.Mapping, () => sourceCommit(() =>
                    _virtualDomains.CommitExecutableMappingAdmission(captured, local, platform, commit)));
            KernelResult Admission(Func<KernelResult> commit) => fresh(() =>
                ValidatePrivateQuota().IsSuccess && captured.ExecutableArtifact is null &&
                _virtualDomains.CommitExecutableMappingAdmission(captured, local, platform, KernelResult.Ok).IsSuccess
                    ? commit() : KernelResult.Fail(KernelError.StaleGeneration, "Executable local mapping/artifact changed."));
            var bound = PlatformAuthority.BindChildExecutableArtifact(child, platform.Guest, immutablePackage, maximumExecutionSteps,
                Admission, MappingCommit, (binding, commit) =>
                {
                    // Retain known receipt for actual cleanup even after local stop/revoke.
                    KernelResult retained;
                    lock (_platformMemoryUseGate) retained = _virtualDomains.RetainExecutableArtifact(captured, local.Mapping, binding);
                    if (!retained.IsSuccess) return retained;
                    var quotaCurrent = ValidatePrivateQuota();
                    if (!quotaCurrent.IsSuccess) return quotaCurrent;
                    return fresh(() => commit(() => PlatformAuthority.CommitGuestLocalMappingSource(platform.Guest, captured, local.Mapping,
                        () => sourceCommit(() => _virtualDomains.CommitExecutableArtifactAdmission(captured, binding, local, KernelResult.Ok)))));
                }, quota);
            return bound.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(bound.Error, bound.Message!);
        });
    }

    public KernelResult StartVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability)
        => StartVirtualDomainCore(owner,domain,executeCapability,null);

    internal KernelResult StartSessionVirtualDomain(ProcessHandle owner,EndpointSessionInvocationHandle invocation,
        VirtualDomainHandle domain,CapabilityId executeCapability)
        => StartVirtualDomainCore(owner,domain,executeCapability,invocation);

    private KernelResult StartVirtualDomainCore(ProcessHandle owner,VirtualDomainHandle domain,CapabilityId executeCapability,
        EndpointSessionInvocationHandle? sessionInvocation)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, executeCapability,
            VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!record.IsSuccess) return KernelResult.Fail(record.Error, record.Message!);
        if (record.Value!.State != VirtualDomainState.Configured)
            return KernelResult.Fail(KernelError.InvalidTransition, $"Virtual domain cannot start from {record.Value.State}.");
        if (record.Value.ChildBinding is not null &&
            PlatformAuthority.FeatureManifest.Resolve(PlatformFeatureFamily.ChildExecutableArtifact).Availability ==
                PlatformFeatureAvailability.Executable)
        {
            if (record.Value.ExecutableArtifact is not { } artifact)
                return KernelResult.Fail(KernelError.PlatformDenied, "Executable child Start requires exact artifact admission.");
            var captured = record.Value;
            if (captured.ExecutableMapping is not { } mapping)
                return KernelResult.Fail(KernelError.StaleGeneration, "Original executable local mapping association is unavailable.");
            var localMapping = _virtualDomains.ResolveMapping(captured, mapping);
            if (!localMapping.IsSuccess) return KernelResult.Fail(localMapping.Error, localMapping.Message!);
            var local = localMapping.Value!;
            return UseVirtualExecutableSource(owner, domain, executeCapability, captured, captured.ChildBinding.Value,
                (fresh, sourceCommit) =>
                {
                    if(sessionInvocation is { } invocation)
                    {
                        var correlation=_virtualDomains.CommitExecutableArtifactAdmission(captured,artifact,local,
                            ()=>SessionInvocations.BindVirtualArtifact(invocation,owner,captured,local.Mapping));
                        if(!correlation.IsSuccess)return correlation;
                    }
                    KernelResult ArtifactCommit(Func<KernelResult> commit) =>
                        PlatformAuthority.CommitGuestLocalMappingSource(artifact.GuestMapping, captured, local.Mapping, () => sourceCommit(() =>
                            _virtualDomains.CommitExecutableArtifactAdmission(captured, artifact, local, commit)));
                    return PlatformAuthority.StartChildExecutableArtifact(artifact,
                        admissionConsumer: commit => fresh(() =>
                            _virtualDomains.CommitExecutableArtifactAdmission(captured, artifact, local, KernelResult.Ok).IsSuccess
                                ? commit() : KernelResult.Fail(KernelError.StaleGeneration, "Executable local artifact or mapping changed.")),
                        sourceCommitConsumer: ArtifactCommit,
                        terminalCompletionConsumer: (terminal, commit) => fresh(() => commit(() => ArtifactCommit(() =>
                            _virtualDomains.CommitExecutionPublication(captured, terminal)))));
                });
        }
        return TransitionVirtualDomain(owner, domain, executeCapability, VirtualDomainState.Configured,
            VirtualDomainState.Running, PlatformVirtualDomainTransition.Start);
    }
    public KernelResult ParkVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability) => TransitionVirtualDomain(owner, domain, executeCapability, VirtualDomainState.Running, VirtualDomainState.Parked, PlatformVirtualDomainTransition.Park);
    public KernelResult ResumeVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId executeCapability) => TransitionVirtualDomain(owner, domain, executeCapability, VirtualDomainState.Parked, VirtualDomainState.Running, PlatformVirtualDomainTransition.Resume);

    internal KernelResult ParkSessionVirtualDomain(ProcessHandle owner,EndpointSessionInvocationHandle invocation,VirtualDomainHandle domain,CapabilityId capability)
        => TransitionVirtualDomain(owner,domain,capability,VirtualDomainState.Running,VirtualDomainState.Parked,PlatformVirtualDomainTransition.Park,invocation);
    internal KernelResult ResumeSessionVirtualDomain(ProcessHandle owner,EndpointSessionInvocationHandle invocation,VirtualDomainHandle domain,CapabilityId capability)
        => TransitionVirtualDomain(owner,domain,capability,VirtualDomainState.Parked,VirtualDomainState.Running,PlatformVirtualDomainTransition.Resume,invocation);

    public KernelResult InjectVirtualEvent(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId eventCapability, KernelEventEndpoint endpoint)
        => InjectVirtualEvent(owner, domain, eventCapability, endpoint, null);

    internal KernelResult InjectSessionVirtualEvent(ProcessHandle owner,EndpointSessionInvocationHandle invocation,
        VirtualDomainHandle domain,CapabilityId eventCapability,KernelEventEndpoint endpoint)
        => InjectVirtualEvent(owner,domain,eventCapability,endpoint,null,invocation);

    private KernelResult InjectVirtualEvent(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId eventCapability,
        KernelEventEndpoint endpoint, Func<Func<KernelResult>, KernelResult>? additionalSourceCommit,
        EndpointSessionInvocationHandle? invocation=null)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return RefuseVirtualTransitionBeforeEffect(owner,invocation,null,composedUse.Result.Error,composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, eventCapability, VirtualizationResourceIds.Events(domain.DomainId), CapabilityRights.Signal);
        if (!record.IsSuccess) return RefuseVirtualTransitionBeforeEffect(owner,invocation,null,record.Error,record.Message!);
        if (record.Value!.State is VirtualDomainState.Draining or VirtualDomainState.Closed or VirtualDomainState.Faulted or VirtualDomainState.Quarantined)
            return RefuseVirtualTransitionBeforeEffect(owner,invocation,null,KernelError.InvalidTransition, "Virtual domain does not accept event injection while terminal or draining.");
        var captured = record.Value;
        var bound=invocation is { } original ? SessionInvocations.BindVirtualTransition(original,owner,captured,domain) : KernelResult.Ok();
        if(!bound.IsSuccess)return bound;
        Action? notify = null;
        KernelEvent stagedEvent = default;
        bool localCommitted = false;
        bool deliveryMayHaveEffect = false;
        KernelResult Publish(Func<Func<KernelResult>, KernelResult> bridgeCommit,
            Func<Func<KernelResult>, KernelResult> fresh, Func<Func<KernelResult>, KernelResult> sourceCommit)
        {
            var composed = RevalidateComposedDomain(owner, virtualDomain: domain);
            var committed = composed.IsSuccess ? fresh(() => bridgeCommit(() => sourceCommit(() =>
            {
                var result = _kernelEvents.CommitExactDeferred(owner, stagedEvent, out notify);
                localCommitted = result.IsSuccess;
                return result.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(result.Error, result.Message!);
            }))) : composed;
            return committed;
        }
        var publication = UseVirtualDeliverySource(owner, domain, eventCapability, captured, captured.ChildBinding,
            CapabilityRights.Signal, VirtualizationResourceIds.Events(domain.DomainId), captured.State, (fresh, sourceCommit) =>
        {
            var staged = _kernelEvents.Stage(owner, endpoint, KernelEventClass.ExternalSignal,
                VirtualizationResourceIds.Events(domain.DomainId), out var rollback);
            if (!staged.IsSuccess) return KernelResult.Fail(staged.Error, staged.Message!);
            stagedEvent = staged.Value!;
            KernelResult Sources(Func<KernelResult> commit) => sourceCommit(() =>
                additionalSourceCommit is null ? _kernelEvents.CommitStagedAdmission(owner, stagedEvent, commit) :
                    additionalSourceCommit(() => _kernelEvents.CommitStagedAdmission(owner, stagedEvent, commit)));
            try
            {
                // Retain the domain correlation before native delivery or local
                // event publication. Local rollback cannot contain native delivery.
                deliveryMayHaveEffect=true;
                if (captured.ChildBinding is not { } child) return Publish(commit => commit(), fresh, Sources);
                var delivered = PlatformAuthority.InjectChildEvent(child, PlatformVirtualEventClass.ExternalSignal,
                    VirtualizationResourceIds.Events(domain.DomainId), fresh, Sources,
                    completionConsumer: commit => Publish(commit, fresh, Sources));
                return delivered.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(delivered.Error, delivered.Message!);
            }
            finally
            {
                // This only rolls back local visibility. The bridge retains any
                // unpublished/unknown original provider invocation independently.
                if (!localCommitted) _ = rollback!();
            }
        });
        if(!publication.IsSuccess && !deliveryMayHaveEffect)
            return RefuseVirtualTransitionBeforeEffect(owner,invocation,captured,publication.Error,publication.Message!);
        notify?.Invoke();
        if (!publication.IsSuccess && RequiresVirtualDomainQuarantine(publication.Error)) captured.State = VirtualDomainState.Quarantined;
        return publication;
    }

    public KernelResult<VirtualTrapObservation> ObserveVirtualTrap(
        ProcessHandle owner, VirtualDomainHandle domain, CapabilityId trapCapability)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return KernelResult<VirtualTrapObservation>.Fail(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, trapCapability,
            VirtualizationResourceIds.Traps(domain.DomainId), CapabilityRights.Read);
        if (!record.IsSuccess)
            return KernelResult<VirtualTrapObservation>.Fail(record.Error, record.Message!);
        var terminalRead = record.Value!.State == VirtualDomainState.Faulted && PlatformAuthority.HasOriginalExecutionTerminalProducer;
        if (terminalRead)
        {
            var original = _virtualDomains.ValidateOriginalTerminalFailurePublication(record.Value);
            if (!original.IsSuccess) return KernelResult<VirtualTrapObservation>.Fail(original.Error, original.Message!);
        }
        else if (record.Value.State != VirtualDomainState.Running)
            return KernelResult<VirtualTrapObservation>.Fail(KernelError.InvalidTransition,
                "Virtual traps may be observed only for the exact running domain.");
        if (record.Value.ChildBinding is not { } child)
            return KernelResult<VirtualTrapObservation>.Fail(KernelError.PlatformUnsupported,
                "The virtual domain has no neutral trap-delivery provider.");
        KernelResult<PlatformVirtualTrapEvidence> observed = default;
        var expected = terminalRead ? VirtualDomainState.Faulted : VirtualDomainState.Running;
        var publication = UseVirtualDeliverySource(owner, domain, trapCapability, record.Value, child, CapabilityRights.Read,
            VirtualizationResourceIds.Traps(domain.DomainId), expected, (fresh, sourceCommit) =>
        {
            observed = PlatformAuthority.ObserveChildTrap(child, fresh, sourceCommit,
                completionConsumer: commit =>
                {
                    // Composition may consult provider state; keep it outside source and owner locks.
                    var composed = RevalidateComposedDomain(owner, virtualDomain: domain);
                    return composed.IsSuccess ? fresh(() => commit(() => sourceCommit(() =>
                        terminalRead ? _virtualDomains.ValidateOriginalTerminalFailurePublication(record.Value) : PlatformAuthority.HasOriginalExecutionTerminalProducer
                            ? _virtualDomains.CommitStateTransition(record.Value, VirtualDomainState.Running, VirtualDomainState.Faulted)
                            : KernelResult.Ok()))) : composed;
                });
            return observed.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(observed.Error, observed.Message!);
        });
        if (!publication.IsSuccess)
        {
            // A failed pure read does not create a new execution lifetime. Preserve
            // its already faulted original publication only while that exact local
            // owner/revision remains current; future reads still validate all sources.
            if (RequiresVirtualDomainQuarantine(publication.Error) &&
                (!terminalRead || !_virtualDomains.ValidateOriginalTerminalFailurePublication(record.Value).IsSuccess))
                record.Value.State = VirtualDomainState.Quarantined;
            return KernelResult<VirtualTrapObservation>.Fail(publication.Error, publication.Message!);
        }
        return KernelResult<VirtualTrapObservation>.Ok(new(domain, observed.Value!.Sequence,
            (VirtualTrapKind)(int)observed.Value.Kind));
    }

    public KernelResult DestroyVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId configureCapability)
        => DestroyVirtualDomainCore(owner,domain,configureCapability,null);

    internal KernelResult DestroySessionVirtualDomain(ProcessHandle owner,EndpointSessionInvocationHandle invocation,
        VirtualDomainHandle domain,CapabilityId configureCapability)
        => DestroyVirtualDomainCore(owner,domain,configureCapability,invocation);

    private KernelResult DestroyVirtualDomainCore(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId configureCapability,
        EndpointSessionInvocationHandle? invocation)
    {
        var record = ResolveVirtualCapability(owner, domain, configureCapability, VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Configure);
        if (!record.IsSuccess) return RefuseVirtualTransitionBeforeEffect(owner,invocation,null,record.Error,record.Message!);
        // Correlate the accepted request before any dependent drain can have an
        // effect. Only validated domain cleanup consumes this existing lifetime;
        // a failed response or an unknown provider reply cannot close it.
        var bound=invocation is { } original ? SessionInvocations.BindVirtualTransition(original,owner,record.Value!,domain) : KernelResult.Ok();
        if(!bound.IsSuccess)return bound;
        lock (_secureExecutionGate)
            if (record.Value!.State is not (VirtualDomainState.Quarantined or VirtualDomainState.Faulted or VirtualDomainState.Closed))
                record.Value.State = VirtualDomainState.Draining;
        var virtualIoDrain = CloseVirtualIoForDomain(owner, domain);
        if (!virtualIoDrain.IsSuccess) return virtualIoDrain;
        var secureGuestDrain = CloseSecureGuestRegionsForVirtualDomain(owner, domain);
        if (!secureGuestDrain.IsSuccess) return secureGuestDrain;
        var guestMappingDrain = CloseGuestMappingsForVirtualTree(record.Value!, owner);
        if (!guestMappingDrain.IsSuccess) return guestMappingDrain;
        var composedDrain = DrainSecureExecutions(owner, virtualDomain: domain);
        if (!composedDrain.IsSuccess) return composedDrain;
        if (record.Value!.State is VirtualDomainState.Quarantined or VirtualDomainState.Faulted)
        {
            var retry = record.Value.ModelBinding is not null
                ? ClosePlatformVirtualDomain(record.Value, owner)
                : RetryQuarantinedPlatformChildClosure(record.Value, owner);
            if (!retry.IsSuccess) return record.Value.ModelBinding is not null
                ? KernelResult.Fail(KernelError.PlatformFaulted, retry.Message ?? "Exact model cleanup remains fault-pinned.")
                : retry;
            record.Value.State = VirtualDomainState.Draining;
        }
        if (record.Value.State != VirtualDomainState.Draining)
        {
            if (record.Value.State is not VirtualDomainState.Created and not VirtualDomainState.Configured and
                not VirtualDomainState.Running and not VirtualDomainState.Parked and not VirtualDomainState.Completed)
                return KernelResult.Fail(KernelError.InvalidTransition, $"Virtual domain cannot begin destroy from {record.Value.State}.");
            record.Value.State = VirtualDomainState.Draining;
        }
        var nestedClose = CloseNestedDomainsForParent(record.Value, owner);
        if (!nestedClose.IsSuccess) return nestedClose;
        if (record.Value.Mappings.Count != 0) return KernelResult.Fail(KernelError.PlatformBindingDraining, "Guest mappings must close before virtual-domain authority.");
        KernelResult closed = ClosePlatformVirtualDomain(record.Value, owner);
        if (!closed.IsSuccess) { record.Value.State = VirtualDomainState.Quarantined; return closed; }
        var consumedModelClosure = ConsumeVirtualDomainClosure(record.Value);
        if (!consumedModelClosure.IsSuccess) { record.Value.State = VirtualDomainState.Quarantined; return consumedModelClosure; }
        record.Value.State = VirtualDomainState.Closed;
        foreach (var capability in record.Value.Capabilities) _ = RevokeCapability(capability);
        return KernelResult.Ok();
    }

    public KernelResult<VirtualDomainState> QueryVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain)
    {
        var record = _virtualDomains.Resolve(owner, domain);
        return record.IsSuccess ? KernelResult<VirtualDomainState>.Ok(record.Value!.State) : KernelResult<VirtualDomainState>.Fail(record.Error, record.Message!);
    }

    private KernelResult TransitionVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId capability, VirtualDomainState from, VirtualDomainState to, PlatformVirtualDomainTransition transition,
        EndpointSessionInvocationHandle? invocation=null)
    {
        KernelResult Refuse(KernelError error,string message) => RefuseVirtualTransitionBeforeEffect(owner,invocation,null,error,message);
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return Refuse(composedUse.Result.Error, composedUse.Result.Message!);
        var record = ResolveVirtualCapability(owner, domain, capability, VirtualizationResourceIds.Domain(domain.DomainId), CapabilityRights.Execute);
        if (!record.IsSuccess) return Refuse(record.Error, record.Message!);
        if (record.Value!.State != from) return Refuse(KernelError.InvalidTransition, $"Virtual domain cannot transition from {record.Value.State} to {to}.");
        if(record.Value.ModelBinding is null && record.Value.ChildBinding is null)return Refuse(KernelError.PlatformBindingNotFound,"Virtual domain has no executable child binding.");
        var bound=invocation is { } original ? SessionInvocations.BindVirtualTransition(original,owner,record.Value,domain) : KernelResult.Ok();
        if(!bound.IsSuccess)return bound;
        if (record.Value.ModelBinding is { } model)
            return TransitionModelVirtualDomain(owner, domain, capability, record.Value, model,
                from, to, transition, CapabilityRights.Execute,invocation);
        if (record.Value.ChildBinding is not { } child)
            return KernelResult.Fail(KernelError.PlatformBindingNotFound, "Virtual domain has no executable child binding.");
        return TransitionChildVirtualDomain(owner, domain, capability, record.Value, child, from, to,
            transition switch
            {
                PlatformVirtualDomainTransition.Start => PlatformChildDomainTransition.Start,
                PlatformVirtualDomainTransition.Park => PlatformChildDomainTransition.Park,
                PlatformVirtualDomainTransition.Resume => PlatformChildDomainTransition.Resume,
                _ => PlatformChildDomainTransition.BeginDrain,
            },invocation);
    }

    private KernelResult TransitionChildVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain,
        CapabilityId source, VirtualDomainAuthority.Record record, PlatformChildBinding binding,
        VirtualDomainState from, VirtualDomainState to, PlatformChildDomainTransition? transition,EndpointSessionInvocationHandle? invocation=null)
    {
        var secureSources = CaptureComposedSecureSources(owner, domain);
        if (!secureSources.IsSuccess) return RefuseVirtualTransitionBeforeEffect(owner,invocation,record,secureSources.Error, secureSources.Message!);
        var effectSource = _virtualDomains.CaptureEffectSource(record, from);
        if (!effectSource.IsSuccess) return RefuseVirtualTransitionBeforeEffect(owner,invocation,record,effectSource.Error, effectSource.Message!);
        SingPlus.Sip.SingProcess process;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return RefuseVirtualTransitionBeforeEffect(owner,invocation,record,current.Error, current.Message!);
            process = current.Value!;
            if (!_pendingVirtualTransitions.Add(domain))
                return RefuseVirtualTransitionBeforeEffect(owner,invocation,record,KernelError.PlatformBindingActive, "A virtual state transaction is pending.");
        }
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(owner);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleHandle, "Child process identity changed.");
                var accepts = EnsureProcessAcceptsNewEffects(process);
                if (!accepts.IsSuccess) return accepts;
                var local = _virtualDomains.ResolveAdmission(owner, domain);
                if (!local.IsSuccess) return KernelResult.Fail(local.Error, local.Message!);
                if (!ReferenceEquals(local.Value, record) || record.State != from || record.ChildBinding != binding)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Child local source state changed.");
                var lineage = _virtualDomains.ValidateActiveAncestors(record);
                return lineage.IsSuccess ? CommitCapturedComposedSecureRevisions(owner, domain, secureSources.Value!, commit) : lineage;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainEffect(source, process.DomainId, owner.Generation,
                transition is null ? CapabilityRights.Configure : CapabilityRights.Execute,
                VirtualizationResourceIds.Domain(domain.DomainId), () => _virtualDomains.CommitEffectAdmission(record, from, commit, effectSource.Value!));
        try
        {
            if (transition is null)
            {
                var possibleEffect=false;
                var configured=Fresh(() => PlatformAuthority.CommitActiveChildDomainConsumer(binding, () =>
                    SourceCommit(() => {possibleEffect=true;return _virtualDomains.CommitStateTransition(record, from, to);} )));
                if(!configured.IsSuccess && !possibleEffect)
                    return RefuseVirtualTransitionBeforeEffect(owner,invocation,record,configured.Error,configured.Message!);
                return configured;
            }
            return PlatformAuthority.TransitionChildDomain(binding, transition.Value,
                admissionConsumer: Fresh, sourceCommitConsumer: SourceCommit,
                completionConsumer: commit => Fresh(() => commit(() =>
                    SourceCommit(() => _virtualDomains.CommitStateTransition(record, from, to)))),
                noEffectConsumer:invocation is { } original ? ()=>SessionInvocations.ConsumeVirtualTransitionNoEffect(original,owner,record):null);
        }
        finally { lock (_platformMemoryUseGate) _pendingVirtualTransitions.Remove(domain); }
    }

    private KernelResult UseVirtualExecutableSource(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId source,
        VirtualDomainAuthority.Record record, PlatformChildBinding child,
        Func<Func<Func<KernelResult>, KernelResult>, Func<Func<KernelResult>, KernelResult>, KernelResult> operation)
    {
        var secureSources = CaptureComposedSecureSources(owner, domain);
        if (!secureSources.IsSuccess) return KernelResult.Fail(secureSources.Error, secureSources.Message!);
        var effectSource = _virtualDomains.CaptureEffectSource(record, VirtualDomainState.Configured);
        if (!effectSource.IsSuccess) return KernelResult.Fail(effectSource.Error, effectSource.Message!);
        SingPlus.Sip.SingProcess process;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            process = current.Value!;
            if (!_pendingVirtualTransitions.Add(domain))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "A virtual state transaction is pending.");
        }
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(owner);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleHandle, "Executable process identity changed.");
                var accepts = EnsureProcessAcceptsNewEffects(process);
                if (!accepts.IsSuccess) return accepts;
                var local = _virtualDomains.ResolveAdmission(owner, domain);
                if (!local.IsSuccess) return KernelResult.Fail(local.Error, local.Message!);
                if (!ReferenceEquals(local.Value, record) || record.ChildBinding != child || record.State != VirtualDomainState.Configured)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Executable local owner/from state changed.");
                var lineage = _virtualDomains.ValidateActiveAncestors(record);
                return lineage.IsSuccess ? CommitCapturedComposedSecureRevisions(owner, domain, secureSources.Value!, commit) : lineage;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainEffect(source, process.DomainId, owner.Generation, CapabilityRights.Execute,
                VirtualizationResourceIds.Domain(domain.DomainId), () => _virtualDomains.CommitEffectAdmission(record, VirtualDomainState.Configured, commit, effectSource.Value!));
        try { return operation(Fresh, SourceCommit); }
        finally { lock (_platformMemoryUseGate) _pendingVirtualTransitions.Remove(domain); }
    }

    private KernelResult UseVirtualDeliverySource(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId source,
        VirtualDomainAuthority.Record record, PlatformChildBinding? child, CapabilityRights rights, string resource,
        VirtualDomainState expected,
        Func<Func<Func<KernelResult>, KernelResult>, Func<Func<KernelResult>, KernelResult>, KernelResult> operation,
        bool deviceSource = false)
    {
        using var composedUse = PinComposedDomain(owner, virtualDomain: domain);
        if (!composedUse.Result.IsSuccess) return composedUse.Result;
        var secureSources = CaptureComposedSecureSources(owner, domain);
        if (!secureSources.IsSuccess) return KernelResult.Fail(secureSources.Error, secureSources.Message!);
        var effectSource = _virtualDomains.CaptureEffectSource(record, expected);
        if (!effectSource.IsSuccess) return KernelResult.Fail(effectSource.Error, effectSource.Message!);
        SingPlus.Sip.SingProcess process;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            process = current.Value!;
            if (!_pendingVirtualTransitions.Add(domain))
                return KernelResult.Fail(KernelError.PlatformBindingActive, "A virtual state transaction is pending.");
        }
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(owner);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleHandle, "Trap process identity changed.");
                var accepts = EnsureProcessAcceptsNewEffects(process);
                if (!accepts.IsSuccess) return accepts;
                var local = _virtualDomains.ResolveAdmission(owner, domain);
                if (!local.IsSuccess) return KernelResult.Fail(local.Error, local.Message!);
                if (!ReferenceEquals(local.Value, record) || record.ChildBinding != child || record.State != expected)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Trap local owner/from state changed.");
                var lineage = _virtualDomains.ValidateActiveAncestors(record);
                return lineage.IsSuccess ? CommitComposedDomainSources(owner, domain, commit, originalSources: secureSources.Value!) : lineage;
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            deviceSource
                ? CapabilityAuthority.CommitDeviceLeaseAdmission(source, process.DomainId, owner.Generation, rights,
                    resource, () => _virtualDomains.CommitEffectAdmission(record, expected, commit, effectSource.Value!))
                : CapabilityAuthority.CommitVirtualDomainEffect(source, process.DomainId, owner.Generation, rights,
                    resource, () => _virtualDomains.CommitEffectAdmission(record, expected, commit, effectSource.Value!));
        try { return operation(Fresh, SourceCommit); }
        finally { lock (_platformMemoryUseGate) _pendingVirtualTransitions.Remove(domain); }
    }

    // Admission only; VirtualDomainAuthority remains the local state owner.
    private readonly HashSet<VirtualDomainHandle> _pendingVirtualTransitions = [];

    private KernelResult TransitionModelVirtualDomain(ProcessHandle owner, VirtualDomainHandle domain,
        CapabilityId source, VirtualDomainAuthority.Record record, PlatformAuthorityBridge.VirtualDomainBinding binding,
        VirtualDomainState from, VirtualDomainState to, PlatformVirtualDomainTransition transition, CapabilityRights rights,EndpointSessionInvocationHandle? invocation=null)
    {
        var effectSource = _virtualDomains.CaptureEffectSource(record, from);
        if (!effectSource.IsSuccess) return RefuseVirtualTransitionBeforeEffect(owner,invocation,record,effectSource.Error, effectSource.Message!);
        SingPlus.Sip.SingProcess process;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return RefuseVirtualTransitionBeforeEffect(owner,invocation,record,current.Error, current.Message!);
            process = current.Value!;
            if (!_pendingVirtualTransitions.Add(domain))
                return RefuseVirtualTransitionBeforeEffect(owner,invocation,record,KernelError.PlatformBindingActive, "A model state transaction is pending.");
        }
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(owner);
                if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
                if (!ReferenceEquals(current.Value, process))
                    return KernelResult.Fail(KernelError.StaleHandle, "Model process identity changed.");
                var accepts = EnsureProcessAcceptsNewEffects(process);
                if (!accepts.IsSuccess) return accepts;
                var local = _virtualDomains.ResolveAdmission(owner, domain);
                if (!local.IsSuccess) return KernelResult.Fail(local.Error, local.Message!);
                if (!ReferenceEquals(local.Value, record) || record.State != from || record.ModelBinding != binding)
                    return KernelResult.Fail(KernelError.InvalidTransition, "Model local source state changed.");
                return commit();
            }
        }
        KernelResult SourceCommit(Func<KernelResult> commit) =>
            CapabilityAuthority.CommitVirtualDomainEffect(source, process.DomainId, owner.Generation, rights,
                VirtualizationResourceIds.Domain(domain.DomainId), () => _virtualDomains.CommitEffectAdmission(record, from, commit, effectSource.Value!));
        try
        {
            return PlatformAuthority.TransitionVirtualDomain(binding, transition, Fresh, SourceCommit,
                completionConsumer: commit => Fresh(() => commit(() =>
                    SourceCommit(() => _virtualDomains.CommitStateTransition(record, from, to)))),
                noEffectConsumer:invocation is { } original ? ()=>SessionInvocations.ConsumeVirtualTransitionNoEffect(original,owner,record):null);
        }
        finally { lock (_platformMemoryUseGate) _pendingVirtualTransitions.Remove(domain); }
    }

    private KernelResult<VirtualDomainAuthority.Record> ResolveVirtualCapability(ProcessHandle owner, VirtualDomainHandle domain, CapabilityId capabilityId, string resourceId, CapabilityRights rights)
    {
        var record = _virtualDomains.ResolveAdmission(owner, domain);
        if (!record.IsSuccess) return record;
        var capability = ValidateCapability(owner, capabilityId, rights);
        if (!capability.IsSuccess) return KernelResult<VirtualDomainAuthority.Record>.Fail(capability.Error, capability.Message!);
        if (capability.Value!.ResourceKind != ResourceKind.Virtualization || capability.Value.ResourceId != resourceId)
            return KernelResult<VirtualDomainAuthority.Record>.Fail(KernelError.WrongCapabilityResource, "Capability does not match the exact virtual-domain resource.");
        if (rights != CapabilityRights.Configure)
        {
            var composed = RevalidateComposedDomain(owner, virtualDomain: domain);
            if (!composed.IsSuccess) return KernelResult<VirtualDomainAuthority.Record>.Fail(composed.Error, composed.Message!);
        }
        return record;
    }

    private KernelResult CloseVirtualDomainsForProcess(ProcessHandle owner)
    {
        var localDomains = _virtualDomains.ForOwner(owner);
        foreach (var record in localDomains
                     .Where(domain => domain.State != VirtualDomainState.Closed && (domain.ParentDomain is null ||
                         !localDomains.Any(parent => parent.Handle == domain.ParentDomain && parent.State != VirtualDomainState.Closed)))
                     .OrderBy(static domain => domain.Handle.DomainId.Value))
        {
            var originalOwner = _virtualDomains.ValidateCleanupOwner(record);
            if (!originalOwner.IsSuccess) return originalOwner;
            record.State = VirtualDomainState.Draining;
            var nestedClose = CloseNestedDomainsForParent(record, owner);
            if (!nestedClose.IsSuccess) { record.State = VirtualDomainState.Quarantined; return nestedClose; }
            if (record.ModelBinding is not null)
            {
                var modelClosed = ClosePlatformVirtualDomain(record, owner);
                if (!modelClosed.IsSuccess) { record.State = VirtualDomainState.Quarantined; return modelClosed; }
            }
            foreach (var mapping in record.Mappings.Values.OrderBy(static mapping => mapping.Mapping.MappingId.Value).ToArray())
            {
                var secureClose = CloseSecureGuestRegionsForGuestMapping(owner, mapping.Mapping);
                if (!secureClose.IsSuccess) { record.State = VirtualDomainState.Quarantined; return secureClose; }
                bool childPlatformMapping = record.PlatformMappings.ContainsKey(mapping.Mapping.MappingId);
                if (childPlatformMapping)
                {
                    var platformClose = CloseChildPlatformMapping(record, owner, mapping.Mapping.MappingId);
                    if (!platformClose.IsSuccess) { record.State = VirtualDomainState.Quarantined; return platformClose; }
                }
                if (childPlatformMapping) continue;
                var removed = RemoveClosedGuestMapping(record, mapping, owner, false);
                if (!removed.IsSuccess) { record.State = VirtualDomainState.Quarantined; return removed; }
            }
            if (record.ChildBinding is not null)
            {
                var childClosed = ClosePlatformVirtualDomain(record, owner);
                if (!childClosed.IsSuccess) { record.State = VirtualDomainState.Quarantined; return childClosed; }
            }
            var consumedModelClosure = ConsumeVirtualDomainClosure(record);
            if (!consumedModelClosure.IsSuccess) { record.State = VirtualDomainState.Quarantined; return consumedModelClosure; }
            record.State = VirtualDomainState.Closed;
        }

        return KernelResult.Ok();
    }

    private KernelResult CloseChildPlatformMapping(VirtualDomainAuthority.Record record, ProcessHandle owner, GuestRegionMappingId mappingId)
    {
        var originalOwner = _virtualDomains.ValidateCleanupOwner(record);
        if (!originalOwner.IsSuccess) return originalOwner;
        if (!record.PlatformMappings.TryGetValue(mappingId, out var platformMapping))
            return KernelResult.Ok();
        if (!record.Mappings.TryGetValue(mappingId, out var originalMapping))
            return KernelResult.Fail(KernelError.StaleGeneration, "Original local guest mapping is unavailable to cleanup.");
        var correlation = PlatformAuthority.CommitGuestLocalMappingSource(platformMapping.Guest, record, originalMapping.Mapping, KernelResult.Ok);
        if (!correlation.IsSuccess) return correlation;
        var unmap = PlatformAuthority.UnmapChildGuestRegion(platformMapping.Guest, allowCommittedClosure: true);
        if (!unmap.IsSuccess) return unmap;
        originalOwner = _virtualDomains.ValidateCleanupOwner(record);
        if (!originalOwner.IsSuccess) return originalOwner;
        correlation = PlatformAuthority.CommitGuestLocalMappingSource(platformMapping.Guest, record, originalMapping.Mapping, KernelResult.Ok);
        if (!correlation.IsSuccess) return correlation;
        var process = Processes.Resolve(owner);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        var parentClose = CloseParentPlatformMapping(platformMapping.Parent, PlatformIdentity(process.Value!));
        if (!parentClose.IsSuccess) return parentClose;
        lock (_platformMemoryUseGate)
        {
            var current = Processes.Resolve(owner);
            if (!current.IsSuccess) return KernelResult.Fail(current.Error, current.Message!);
            if (record.Owner != owner)
                return KernelResult.Fail(KernelError.StaleGeneration, "Platform mapping cleanup process owner changed.");
            return PlatformAuthority.CommitGuestLocalMappingSource(platformMapping.Guest, record, originalMapping.Mapping,
                () => _virtualDomains.RemovePlatformMapping(record, originalMapping, platformMapping,
                    ()=>SessionInvocations.ConsumeVirtualArtifactMappingClosure(owner,record,originalMapping.Mapping)));
        }
    }

    private KernelResult CloseParentPlatformMapping(PlatformRegionMapping mapping, PlatformDomainIdentity identity)
    {
        var existing = PlatformAuthority.QueryRegionMappingLifecycle(mapping, identity);
        if (!existing.IsSuccess) return KernelResult.Fail(existing.Error, existing.Message!);
        var revoke = existing.Value!.PlatformClosure switch
        {
            PlatformExternalClosureState.Active => PlatformAuthority.BeginRegionMappingRevocation(
                mapping, identity, PlatformRegionRevocationPolicy.DrainBeforeRevoke),
            PlatformExternalClosureState.Faulted when existing.Value.AdmissionNoEffectProven => existing,
            PlatformExternalClosureState.Draining => PlatformAuthority.ObserveRegionMappingRevocation(mapping, identity),
            // Exact existing cleanup fact only; finalizer still observes source
            // and revalidates local owners before any reclaim.
            PlatformExternalClosureState.Closed => existing,
            _ => KernelResult<PlatformRegionMappingLifecycle>.Fail(KernelError.PlatformFaulted,
                "Parent mapping closure remains fault-pinned; guest cleanup cannot license reclaim.")
        };
        if (!revoke.IsSuccess) return KernelResult.Fail(revoke.Error, revoke.Message!);
        return FinalizePlatformRegionMappingClosure(mapping, identity, revoke.Value!);
    }
    internal KernelResult CloseUnpublishedSessionVirtualCreation(ProcessHandle owner,EndpointSessionInvocationRegistry.OriginalPublication publication)
    {
        var original=SessionInvocations.ReadUnpublishedVirtualCreation(publication);
        if(!original.IsSuccess)return KernelResult.Fail(original.Error,original.Message!);
        if(original.Value is null)return KernelResult.Fail(KernelError.PlatformFaulted,"Original nested creation fact unavailable; child remains pinned.");
        var child=PlatformAuthority.ReadOriginalNestedChild(original.Value);
        if(!child.IsSuccess)
        {
            var root=PlatformAuthority.ReadOriginalRootCreation(original.Value);
            if(!root.IsSuccess)
            {
                var nested=PlatformAuthority.ContainOriginalUnreturnedNestedCreation(original.Value);
                return nested.IsSuccess ? nested : PlatformAuthority.CloseOriginalFailedRootCreation(original.Value);
            }
            var created=_virtualDomains.ForOwner(owner).SingleOrDefault(r=>r.ParentDomain is null &&
                (root.Value.Model is { } model && r.ModelBinding==model || root.Value.Child is { } native && r.ChildBinding==native));
            if(created is null)return KernelResult.Fail(KernelError.PlatformFaulted,"Original local root/model creation unavailable.");
            var valid=_virtualDomains.ValidateCleanupOwner(created);
            if(!valid.IsSuccess)return valid;
            created.State=VirtualDomainState.Draining;
            if(created.ChildBinding is not null)
            {
                var continued=RetryQuarantinedPlatformChildClosure(created,owner);
                if(!continued.IsSuccess)return continued;
            }
            var closedRoot=ClosePlatformVirtualDomain(created,owner);
            return closedRoot.IsSuccess ? ConsumeVirtualDomainClosure(created) : closedRoot;
        }
        var record=_virtualDomains.ForOwner(owner).SingleOrDefault(r=>r.ChildBinding==child.Value && r.ParentDomain is not null);
        if(record is null)return KernelResult.Fail(KernelError.PlatformFaulted,"Original unpublished local child unavailable.");
        var exact=_virtualDomains.ValidateCleanupOwner(record);
        if(!exact.IsSuccess)return exact;
        record.State=VirtualDomainState.Draining;
        // Reconcile a retained original Close before another drain; an unknown
        // close reply cannot authorize a second native close or fresh transition.
        var closed=RetryQuarantinedPlatformChildClosure(record,owner);
        return closed.IsSuccess ? ConsumeVirtualDomainClosure(record) : closed;
    }

    internal KernelResult CloseFailedSessionNestedCreations(ProcessHandle owner,ProcessHandle service,EndpointSessionHandle session)
    {
        foreach(var original in SessionInvocations.CaptureCancelledNestedCreations(owner,service,session))
        {
            var closed=CloseUnpublishedSessionVirtualCreation(owner,original);
            if(!closed.IsSuccess)return closed;
            var consumed=SessionInvocations.CompleteCancelledVirtualCreation(original);
            if(!consumed.IsSuccess)return consumed;
        }
        return KernelResult.Ok();
    }

    internal KernelResult CompleteSessionVirtualCreationCleanup(EndpointSessionInvocationRegistry.OriginalPublication original)
        => SessionInvocations.CompleteCancelledVirtualCreation(original);

    private KernelResult ClosePlatformVirtualDomain(VirtualDomainAuthority.Record record, ProcessHandle owner)
    {
        var mappingsClosed = _virtualDomains.ValidateMappingCleanupComplete(record);
        if (!mappingsClosed.IsSuccess) return mappingsClosed;
        if (record.ChildBinding is { } child && record.ParentBinding is { } parent)
        {
            var draining = PlatformAuthority.TransitionChildDomain(child, PlatformChildDomainTransition.BeginDrain, allowCommittedDrain: true);
            if (!draining.IsSuccess) return draining;
            var closed = PlatformAuthority.CloseChildDomain(child, allowCommittedClosure: true);
            if (!closed.IsSuccess) return closed;
            if (record.ParentDomain is not null)
                return KernelResult.Ok();
            var process = Processes.Resolve(owner);
            if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
            var rootClosed = PlatformAuthority.RevokeDomain(parent, PlatformIdentity(process.Value!));
            if (rootClosed.IsSuccess) ConsumeTrackedVirtualRootClosure(owner, parent);
            return rootClosed;
        }
        var modelDrain = PlatformAuthority.TransitionVirtualDomain(record.ModelBinding!.Value, PlatformVirtualDomainTransition.BeginDrain);
        if (!modelDrain.IsSuccess) return modelDrain;
        return PlatformAuthority.RevokeVirtualDomain(record.ModelBinding.Value, retainCleanupReceipt: true);
    }

    // Reuse exact revoke continuation after local mapping cleanup; a known receipt never repeats provider revoke.
    private KernelResult ConsumeVirtualDomainClosure(VirtualDomainAuthority.Record record)
    {
        var process = Processes.Resolve(record.Owner);
        if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
        KernelResult Fresh(Func<KernelResult> commit)
        {
            lock (_platformMemoryUseGate)
            {
                var current = Processes.Resolve(record.Owner);
                if (!current.IsSuccess || !ReferenceEquals(current.Value, process.Value))
                    return KernelResult.Fail(KernelError.StaleGeneration, "Exact model cleanup process changed.");
                if (_pendingVirtualTransitions.Contains(record.Handle))
                    return KernelResult.Fail(KernelError.PlatformBindingActive, "Model state transaction is pending.");
                return commit();
            }
        }
        if (record.ModelBinding is { } model)
            return PlatformAuthority.RevokeVirtualDomain(model, cleanupConsumer: commit =>
                Fresh(() => commit(release => _virtualDomains.CommitVirtualCleanup(record,()=>Consume(release)))));
        if (record.ChildBinding is { } child)
            return PlatformAuthority.CloseChildDomain(child, allowCommittedClosure: true,
                cleanupCompletionConsumer: commit => Fresh(() =>
                    commit(() => _virtualDomains.CommitVirtualCleanup(record,()=>Consume(KernelResult.Ok)))));
        return KernelResult.Fail(KernelError.PlatformFaulted, "Exact virtual cleanup binding is unavailable.");

        KernelResult Consume(Func<KernelResult> release)
        {
            var consumed=SessionInvocations.ConsumeVirtualTransitionDomainClosure(record.Owner,record,record.Handle);
            return consumed.IsSuccess ? release() : consumed;
        }
    }

    private KernelResult RefuseVirtualTransitionBeforeEffect(ProcessHandle owner,EndpointSessionInvocationHandle? invocation,
        object? original,KernelError error,string message)
    {
        var consumed=invocation is { } handle ? SessionInvocations.ConsumeVirtualTransitionNoEffect(handle,owner,original,neverInvoked:true) : KernelResult.Ok();
        return consumed.IsSuccess ? KernelResult.Fail(error,message) : consumed;
    }

    private static PlatformGuestMemoryAccess ToPlatformAccess(GuestMemoryAccess access) =>
        ((access & GuestMemoryAccess.Read) != 0 ? PlatformGuestMemoryAccess.Read : PlatformGuestMemoryAccess.None) |
        ((access & GuestMemoryAccess.Write) != 0 ? PlatformGuestMemoryAccess.Write : PlatformGuestMemoryAccess.None) |
        ((access & GuestMemoryAccess.Execute) != 0 ? PlatformGuestMemoryAccess.Execute : PlatformGuestMemoryAccess.None);

    private KernelResult CloseNestedDomainsForParent(VirtualDomainAuthority.Record parent, ProcessHandle owner)
    {
        foreach (var child in _virtualDomains.ChildrenOf(parent).OrderBy(static x => x.Handle.DomainId.Value))
        {
            var originalOwner = _virtualDomains.ValidateCleanupOwner(child);
            if (!originalOwner.IsSuccess) return originalOwner;
            if (child.State is VirtualDomainState.Quarantined or VirtualDomainState.Faulted)
            {
                var retry = RetryQuarantinedPlatformChildClosure(child, owner);
                if (!retry.IsSuccess) return retry;
            }
            child.State = VirtualDomainState.Draining;
            var descendants = CloseNestedDomainsForParent(child, owner);
            if (!descendants.IsSuccess) return descendants;
            foreach (var mapping in child.Mappings.Values.OrderBy(static x => x.Mapping.MappingId.Value).ToArray())
            {
                var secureClose = CloseSecureGuestRegionsForGuestMapping(owner, mapping.Mapping);
                if (!secureClose.IsSuccess) { child.State = VirtualDomainState.Quarantined; return secureClose; }
                bool platformMapping = child.PlatformMappings.ContainsKey(mapping.Mapping.MappingId);
                if (platformMapping)
                {
                    var close = CloseChildPlatformMapping(child, owner, mapping.Mapping.MappingId);
                    if (!close.IsSuccess) { child.State = VirtualDomainState.Quarantined; return close; }
                }
                if (platformMapping) continue;
                var removed = RemoveClosedGuestMapping(child, mapping, owner, false);
                if (!removed.IsSuccess) return removed;
            }
            var closed = ClosePlatformVirtualDomain(child, owner);
            if (!closed.IsSuccess) { child.State = VirtualDomainState.Quarantined; return closed; }
            var consumedModelClosure = ConsumeVirtualDomainClosure(child);
            if (!consumedModelClosure.IsSuccess) { child.State = VirtualDomainState.Quarantined; return consumedModelClosure; }
            child.State = VirtualDomainState.Closed;
            foreach (var capability in child.Capabilities) _ = RevokeCapability(capability);
        }
        return KernelResult.Ok();
    }

    private KernelResult RetryQuarantinedPlatformChildClosure(VirtualDomainAuthority.Record record, ProcessHandle owner)
    {
        var mappingsClosed = _virtualDomains.ValidateMappingCleanupComplete(record);
        if (!mappingsClosed.IsSuccess) return mappingsClosed;
        if (record.ModelBinding is not null || record.ChildBinding is not { } child ||
            record.ParentBinding is not { } parent || child.ParentBinding != parent || child.Owner != owner)
            return KernelResult.Fail(KernelError.PlatformFaulted,
                "Exact platform child cleanup owner is unavailable; virtual domain remains pinned.");
        // Cleanup only: the existing bridge checks original source, dependents,
        // state and receipt. No local quarantine state grants closure or permission.
        if (PlatformAuthority.HasOriginalChildCloseContinuation(child))
        {
            // An uncertain original Close must be reconciled before attempting
            // another drain. Drain cannot manufacture its missing closure.
            var continued = PlatformAuthority.CloseChildDomain(child, allowCommittedClosure: true);
            return continued.IsSuccess ? continued : KernelResult.Fail(KernelError.PlatformFaulted,
                continued.Message ?? "Original child Close remains unresolved; no effect repeat.");
        }
        var drained = PlatformAuthority.TransitionChildDomain(child,
            PlatformChildDomainTransition.BeginDrain, allowCommittedDrain: true);
        if (!drained.IsSuccess) return KernelResult.Fail(KernelError.PlatformFaulted,
            drained.Message ?? "Exact original child drain is unavailable; cleanup remains pinned.");
        var closed = PlatformAuthority.CloseChildDomain(child, allowCommittedClosure: true);
        return closed.IsSuccess ? closed : KernelResult.Fail(KernelError.PlatformFaulted,
            closed.Message ?? "Exact child closure is unavailable; virtual domain remains pinned.");
    }

    private KernelResult CloseGuestMappingsForProcess(ProcessHandle owner)
    {
        foreach (var record in _virtualDomains.ForOwner(owner).Where(static x => x.State != VirtualDomainState.Closed).OrderByDescending(x => x.Handle.DomainId.Value))
        {
            var originalOwner = _virtualDomains.ValidateCleanupOwner(record);
            if (!originalOwner.IsSuccess) return originalOwner;
            record.State = VirtualDomainState.Draining;
            var close = CloseGuestMappingsForRecord(record, owner);
            if (!close.IsSuccess) { record.State = VirtualDomainState.Quarantined; return close; }
        }
        return KernelResult.Ok();
    }

    private KernelResult CloseGuestMappingsForVirtualTree(VirtualDomainAuthority.Record record, ProcessHandle owner)
    {
        foreach (var child in _virtualDomains.ChildrenOf(record))
        {
            var descendants = CloseGuestMappingsForVirtualTree(child, owner);
            if (!descendants.IsSuccess) return descendants;
        }
        return CloseGuestMappingsForRecord(record, owner);
    }

    private KernelResult CloseGuestMappingsForRecord(VirtualDomainAuthority.Record record, ProcessHandle owner)
    {
        var originalOwner = _virtualDomains.ValidateCleanupOwner(record);
        if (!originalOwner.IsSuccess) return originalOwner;
        if (record.ChildBinding is { } child)
        {
            var pendingLocal = record.Mappings.Values.Where(x => !record.PlatformMappings.ContainsKey(x.Mapping.MappingId)).Select(x => x.Mapping).ToArray();
            var resolved = PlatformAuthority.ResolveGuestMapCleanupForLocalOwner(child, record, pendingLocal);
            if (!resolved.IsSuccess) return KernelResult.Fail(resolved.Error, resolved.Message!);
            foreach (var cleanup in resolved.Value!)
            {
                if (cleanup.Guest is { } guest)
                {
                    var retained = PlatformAuthority.CommitGuestLocalMappingSource(guest, record, cleanup.Local,
                        () => _virtualDomains.RetainGuestCleanupMapping(record, cleanup.Local, (guest, cleanup.Parent)));
                    if (!retained.IsSuccess) return retained;
                }
                else
                {
                    var process = Processes.Resolve(owner);
                    if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
                    var closed = CloseParentPlatformMapping(cleanup.Parent, PlatformIdentity(process.Value!));
                    if (!closed.IsSuccess) return closed;
                }
            }
        }
        foreach (var mapping in record.Mappings.Values.OrderBy(x => x.Mapping.MappingId.Value).ToArray())
        {
            if(record.ChildBinding is not null && !record.PlatformMappings.ContainsKey(mapping.Mapping.MappingId))
            {
                PlatformRegionMapping? parent=null;
                var known=_virtualDomains.CommitLocalGuestLifetime(record,mapping,lifetime=>
                {
                    if(!lifetime.ParentAdmissionKnown)return KernelResult.Fail(KernelError.PlatformFaulted,"Original parent admission outcome unknown.");
                    parent=lifetime.ParentMapping;return KernelResult.Ok();
                });
                if(!known.IsSuccess)return known;
                if(parent is { } originalParent)
                {
                    var current=Processes.Resolve(owner);
                    if(!current.IsSuccess)return KernelResult.Fail(current.Error,current.Message!);
                    var resolvedParent=PlatformAuthority.ResolveOriginalAdmissionsForCleanup(PlatformIdentity(current.Value!));
                    if(!resolvedParent.IsSuccess)return resolvedParent;
                    var closedParent=CloseParentPlatformMapping(originalParent,PlatformIdentity(current.Value!));
                    if(!closedParent.IsSuccess)return closedParent;
                }
            }
            if (HasVirtualComputePin(mapping.Mapping))
                return KernelResult.Fail(KernelError.PlatformBindingDraining, "Guest mapping remains pinned by virtualized provider work.");
            var secureClose = CloseSecureGuestRegionsForGuestMapping(owner, mapping.Mapping);
            if (!secureClose.IsSuccess) return secureClose;
            var childPlatformMapping = record.PlatformMappings.ContainsKey(mapping.Mapping.MappingId);
            if (childPlatformMapping)
            {
                var platformClose = CloseChildPlatformMapping(record, owner, mapping.Mapping.MappingId);
                if (!platformClose.IsSuccess) return platformClose;
            }
            if (childPlatformMapping) continue;
            var removed = RemoveClosedGuestMapping(record, mapping, owner, false);
            if (!removed.IsSuccess) return removed;
        }
        return KernelResult.Ok();
    }

    private KernelResult RemoveClosedGuestMapping(VirtualDomainAuthority.Record record, GuestRegionMapping mapping,
        ProcessHandle owner, bool platformMapping)
    {
        var observations=new List<BudgetTraceObservation>();
        try
        {
        lock (_platformMemoryUseGate)
        {
            var process = Processes.Resolve(owner);
            if (!process.IsSuccess) return KernelResult.Fail(process.Error, process.Message!);
            if (record.Owner != owner)
                return KernelResult.Fail(KernelError.StaleGeneration, "Guest mapping cleanup process changed.");
            var removed = _virtualDomains.RemoveMapping(record, mapping.Mapping, expected: mapping,
                cleanupConsumer: exact => platformMapping ? KernelResult.Ok() :
                    record.ChildBinding is null?ConsumeLocalGuest(record, exact, owner, new(process.Value!.DomainId, owner.Generation)):
                    ConsumeProviderGuestParent(record,exact,owner,new(process.Value!.DomainId,owner.Generation),observations));
            return removed.IsSuccess ? KernelResult.Ok() : KernelResult.Fail(removed.Error, removed.Message!);
        }
        }
        finally{EmitBudgetObservations(observations);}
    }

    private static PlatformChildAuthorityClass ToPlatformAuthority(VirtualDomainAuthorityClass authority) =>
        (PlatformChildAuthorityClass)(int)authority;

    private static bool RequiresVirtualDomainQuarantine(KernelError error) => error is
        KernelError.PlatformFaulted or KernelError.PlatformBindingRevoked or KernelError.StaleGeneration or
        KernelError.WrongPlatformDomain;
}





