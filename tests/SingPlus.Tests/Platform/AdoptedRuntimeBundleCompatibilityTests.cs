using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;

public sealed class AdoptedRuntimeBundleCompatibilityTests
{
    private sealed class Provider(ExternalSecureDomainCreateReceipt receipt):IExternalSecureDomainProvider,IExternalSecureExecutionBindingProvider,IExternalSecureExecutionReservationProvider
    {
        public int Queries,Reserves;
        public ExternalSecureDomainCreateProviderResult Create(ExternalSecureDomainCreateRequest request)=>new(ExternalSecureDomainProviderStatus.Receipt,receipt);
        public ExternalSecureDomainCloseProviderResult Close(ExternalSecureDomainLease domain,ExternalOperationIdentity operation)=>new(ExternalSecureDomainProviderStatus.Receipt,new(domain,operation,true));
        public ExternalSecureExecutionBindingObservation Revalidate(ExternalSecureRegisterLocalRequest request){Queries++;return new(request,ExternalSecureDomainProviderStatus.Receipt);}
        public ExternalSecureExecutionReservationResult Reserve(ExternalSecureRegisterLocalRequest request){Reserves++;throw new InvalidOperationException("Legacy machine cannot encode unknown reservation cleanup.");}
        public ExternalSecureExecutionReservationResult QueryReservation(ExternalSecureRegisterLocalRequest request)=>throw new InvalidOperationException("No original reservation.");
        public ExternalSecureExecutionReservationResult Release(ExternalSecureExecutionReservation reservation)=>throw new InvalidOperationException("No original reservation.");
        public ExternalSecureExecutionReservationResult QueryRelease(ExternalSecureExecutionReservation reservation)=>throw new InvalidOperationException("No original reservation.");
    }
    [Fact]
    public void AdoptedNoIssueMachineWithoutCleanupResultNeverCallsBackendReserve()
    {
        Assert.Null(typeof(SecureComputeCanonicalAdmissionResult).GetMethod("DeniedBackendCleanupRequired"));
        var parent=new ExternalDomainLease(new(Guid.NewGuid()),new(1));
        var create=new ExternalSecureDomainCreateRequest(parent,new(Guid.NewGuid()),new(ExternalSecureAssuranceClass.High,true,true),ExternalSecureComputeContract.Version);
        var domain=new ExternalSecureDomainLease(new(Guid.NewGuid()),new(1));
        var provider=new Provider(new(create,domain,create.RequiredProperties,new(Guid.NewGuid())));
        var session=new ExternalSecureDomainAdapterSession(provider);
        Assert.Equal(ExternalSecureDomainAdapterStatus.Accepted,session.Create(create));
        var binding=new ExternalSecureExecutionBinding(new(new(Guid.NewGuid()),new(1),parent),domain,parent,1,ExternalSecureComputeContract.Version);
        var request=new ExternalSecureRegisterLocalRequest(binding,new(new(Guid.NewGuid()),new(1)),19,23);
        var denied=session.AdmitRegisterLocal(request);
        Assert.False(denied.IsAllowed);Assert.Same(request,denied.Request);Assert.NotNull(denied.Canonical);
        Assert.False(denied.Canonical.IsReplayEligible);Assert.Equal(0,provider.Reserves);Assert.Equal(1,provider.Queries);
        Assert.False(session.AdmitRegisterLocal(request).IsAllowed);Assert.Equal(0,provider.Reserves);Assert.Equal(1,provider.Queries);
        Assert.Equal(ExternalSecureDomainAdapterStatus.Accepted,session.Close(domain,new(new(Guid.NewGuid()),new(2))));
    }
}
