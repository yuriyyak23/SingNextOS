using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using YAKSys_Hybrid_CPU.Core;

namespace SingPlus.Tests.Platform;

public sealed class AdoptedRuntimeBundleCompatibilityTests
{
    private sealed class Provider(ExternalSecureDomainCreateReceipt receipt):IExternalSecureDomainProvider,IExternalSecureExecutionBindingProvider,IExternalSecureExecutionReservationProvider
    {
        public int Queries,Reserves,ReservationQueries,Closes;
        public ExternalSecureDomainCreateProviderResult Create(ExternalSecureDomainCreateRequest request)=>new(ExternalSecureDomainProviderStatus.Receipt,receipt);
        public ExternalSecureDomainCloseProviderResult Close(ExternalSecureDomainLease domain,ExternalOperationIdentity operation){Closes++;return new(ExternalSecureDomainProviderStatus.Receipt,new(domain,operation,true));}
        public ExternalSecureExecutionBindingObservation Revalidate(ExternalSecureRegisterLocalRequest request){Queries++;return new(request,ExternalSecureDomainProviderStatus.Receipt);}
        public ExternalSecureExecutionReservationResult Reserve(ExternalSecureRegisterLocalRequest request){Reserves++;throw new IOException("Original reservation reply lost; effect remains unknown.");}
        public ExternalSecureExecutionReservationResult QueryReservation(ExternalSecureRegisterLocalRequest request){ReservationQueries++;throw new IOException("Original reservation query reply remains unknown.");}
        public ExternalSecureExecutionReservationResult Release(ExternalSecureExecutionReservation reservation)=>throw new InvalidOperationException("No original reservation.");
        public ExternalSecureExecutionReservationResult QueryRelease(ExternalSecureExecutionReservation reservation)=>throw new InvalidOperationException("No original reservation.");
    }
    [Fact]
    public void AdoptedUnknownBackendReservationPinsDomainWithoutRepeatingEffect()
    {
        Assert.NotNull(typeof(SecureComputeCanonicalAdmissionResult).GetMethod("DeniedBackendCleanupRequired"));
        ExternalDomainLease parent =new ExternalDomainLease(new(Guid.NewGuid()),new(1));
        ExternalSecureDomainCreateRequest create =new ExternalSecureDomainCreateRequest(parent,new(Guid.NewGuid()),new(ExternalSecureAssuranceClass.High,true,true),ExternalSecureComputeContract.Version);
        ExternalSecureDomainLease domain =new ExternalSecureDomainLease(new(Guid.NewGuid()),new(1));
        Provider provider =new Provider(new(create,domain,create.RequiredProperties,new(Guid.NewGuid())));
        ExternalSecureDomainAdapterSession session =new ExternalSecureDomainAdapterSession(provider);
        Assert.Equal(ExternalSecureDomainAdapterStatus.Accepted,session.Create(create));
        ExternalSecureExecutionBinding binding =new ExternalSecureExecutionBinding(new(new(Guid.NewGuid()),new(1),parent),domain,parent,1,ExternalSecureComputeContract.Version);
        ExternalSecureRegisterLocalRequest request =new ExternalSecureRegisterLocalRequest(binding,new(new(Guid.NewGuid()),new(1)),19,23);
        SecureComputeCanonicalAdmissionResult denied =session.AdmitRegisterLocal(request);
        Assert.False(denied.IsAllowed);Assert.Same(request,denied.Request);
        Assert.Equal(SecureComputeCanonicalAdmissionDecision.DeniedBackendCleanupRequired,denied.Decision);
        Assert.Null(denied.Canonical); // No canonical machine permission was produced before the lost Reserve reply.
        Assert.Null(denied.BackendAttempt);Assert.Null(denied.BackendConsumption);Assert.Null(denied.MachinePermitConsumption);
        Assert.Equal(1,provider.Reserves);Assert.Equal(1,provider.Queries);
        SecureComputeCanonicalAdmissionResult repeated =session.AdmitRegisterLocal(request);
        Assert.False(repeated.IsAllowed);Assert.Same(request,repeated.Request);
        Assert.Equal(SecureComputeCanonicalAdmissionDecision.DeniedBackendCleanupRequired,repeated.Decision);
        Assert.Equal(1,provider.Reserves);Assert.Equal(1,provider.Queries);
        Assert.Equal(ExternalSecureDomainAdapterStatus.Rejected,session.ReconcileBackendReservation(new ExternalSecureRegisterLocalRequest(binding,request.Operation,19,23)));
        Assert.Equal(0,provider.ReservationQueries);
        Assert.Equal(ExternalSecureDomainAdapterStatus.Accepted,session.RevokeLocalBinding(domain));
        Assert.Equal(ExternalSecureDomainAdapterStatus.TransportUnavailable,session.ReconcileBackendReservation(request));
        Assert.Equal(1,provider.ReservationQueries);Assert.Equal(1,provider.Reserves);
        Assert.Equal(ExternalSecureDomainAdapterStatus.Rejected,session.Close(domain,new(new(Guid.NewGuid()),new(2))));
        Assert.Equal(0,provider.Closes); // Exception/absence is not no-effect or domain closure.
    }
}
