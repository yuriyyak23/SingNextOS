using System.Collections;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using SingPlus.Platform;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

namespace SingPlus.Platform.HybridCpu.Tests;

public sealed class SoftwareSecureAdmissionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ActualPackagedConsumer_ObservesNoEffectRetireAndClosesOriginalCustody()
    {
        var (adapter, child) = Setup();
        var request = Request(child);
        var observation = adapter.ObserveSoftwareSecureAdmission(request);
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.ExpectedDeny, observation.Status);
        Assert.False(observation.IsExecutionAllowed);
        Assert.NotEqual(0UL, observation.MachineAttempt);
        Assert.Equal(1UL, observation.NoEffectCommit);
        Assert.Same(request, observation.Request);
        Assert.Same(observation, adapter.ResolveSoftwareSecureAdmission(request));
        Assert.Same(observation, adapter.ObserveSoftwareSecureAdmission(request));
        var fact = Fact(adapter, request);
        var admission = (SecureComputeCanonicalAdmissionResult)Field(fact, "Admission")!;
        Assert.False(admission.IsAllowed);
        Assert.Null(admission.RegisterLocalRetirement);
        Assert.Equal(0, admission.NoEffectRetirement!.EffectCount);
        Assert.Equal(0, ((SoftwareSecureComputeProvider)Field(fact, "Provider")!).OutstandingReservations);
        Assert.Equal(ExternalSecureDomainProviderStatus.Receipt,
            ((ExternalSecureDomainCloseProviderResult)Field(fact, "CloseObservation")!).Status);
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.CleanupRequired,
            adapter.ObserveSoftwareSecureAdmission(new(child, request.Operation, 2, 99, 100)).Status);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.True(adapter.CloseExternalParent(new(Guid.NewGuid(), child.ParentLease)).IsSuccess);
        var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), new HybridCpuExecutableChildAdapter());
        Assert.Equal(PlatformFeatureAvailability.Unavailable, provider.QueryFeatures().Resolve(PlatformFeatureFamily.SecureDomains).Availability);
    }

    [Theory]
    [InlineData(NeutralSoftwareSecureProfile.RegisterLocalExecution)]
    [InlineData(NeutralSoftwareSecureProfile.ProductionSecure)]
    [InlineData((NeutralSoftwareSecureProfile)0)]
    public void StrongerOrUnknownProfile_IsNotInvoked(NeutralSoftwareSecureProfile profile)
    {
        var (adapter, child) = Setup();
        var request = new NeutralSoftwareSecureAdmissionRequest(child, Guid.NewGuid(), 1, 19, 23, profile);
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.Unsupported, adapter.ObserveSoftwareSecureAdmission(request).Status);
        Assert.Empty((IDictionary)adapter.GetType().GetField("softwareAdmissions", Private)!.GetValue(adapter)!);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.True(adapter.CloseExternalParent(new(Guid.NewGuid(), child.ParentLease)).IsSuccess);
    }

    [Fact]
    public void LostPublicationReply_QueryUsesOriginalAndChildCannotCloseDuringCallback()
    {
        var (adapter, child) = Setup();
        var request = Request(child);
        adapter.GetType().GetProperty("BeforeSoftwareSecureAdmissionPublication", Private)!.SetValue(adapter, (Action)(() =>
        {
            Assert.False(adapter.CloseChildDomain(child).IsSuccess);
            Assert.False(adapter.CloseExternalParent(new(Guid.NewGuid(), child.ParentLease)).IsSuccess);
            throw new IOException("Lost caller reply after actual no-effect admission/cleanup.");
        }));
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.CleanupRequired, adapter.ObserveSoftwareSecureAdmission(request).Status);
        adapter.GetType().GetProperty("BeforeSoftwareSecureAdmissionPublication", Private)!.SetValue(adapter, null);
        var recovered = adapter.ResolveSoftwareSecureAdmission(request);
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.ExpectedDeny, recovered.Status);
        Assert.Equal(1UL, recovered.NoEffectCommit);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.True(adapter.CloseExternalParent(new(Guid.NewGuid(), child.ParentLease)).IsSuccess);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)] [InlineData(null)]
    public void MissingOrCopiedRoute_PinsOriginalChildUntilExactRestoration(bool? copy)
    {
        var (adapter, child) = Setup();
        var request = Request(child);
        var observation = adapter.ObserveSoftwareSecureAdmission(request);
        var routes = (IDictionary)adapter.GetType().GetField("softwareAdmissions", Private)!.GetValue(adapter)!;
        var original = routes[request.Operation]!;
        if (copy is true) routes[request.Operation] = typeof(object).GetMethod("MemberwiseClone", Private)!.Invoke(original, null)!;
        else if (copy is false) routes.Remove(request.Operation);
        else routes[request.Operation] = null;
        Assert.False(adapter.CloseChildDomain(child).IsSuccess);
        Assert.False(adapter.CloseExternalParent(new(Guid.NewGuid(), child.ParentLease)).IsSuccess);
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.CleanupRequired, adapter.ResolveSoftwareSecureAdmission(request).Status);
        routes[request.Operation] = original;
        Assert.Same(observation, adapter.ResolveSoftwareSecureAdmission(request));
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.True(adapter.CloseExternalParent(new(Guid.NewGuid(), child.ParentLease)).IsSuccess);
    }

    [Fact]
    public void OwnerRevisionChangeBeforeObservationPublication_RefusesObservationWithoutBlockingCleanup()
    {
        var (adapter, child) = Setup();
        var request = Request(child);
        adapter.GetType().GetProperty("BeforeSoftwareSecureAdmissionPublication", Private)!.SetValue(adapter, (Action)(() =>
        {
            Assert.False(adapter.TransitionChildDomain(child, NeutralChildDomainTransition.BeginDrain).IsSuccess);
            var source = ((IDictionary)adapter.GetType().GetField("children", Private)!.GetValue(adapter)!)[child.Handle]!;
            var state = source.GetType().GetProperty("State")!;
            state.SetValue(source, state.GetValue(source)); // actual owner setter advances its source revision
        }));
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.Denied, adapter.ObserveSoftwareSecureAdmission(request).Status);
        Assert.Equal(0, ((SoftwareSecureComputeProvider)Field(Fact(adapter, request), "Provider")!).OutstandingReservations);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.True(adapter.CloseExternalParent(new(Guid.NewGuid(), child.ParentLease)).IsSuccess);
    }

    [Fact]
    public void ForeignCreationCache_CannotCloseForeignDomainOrReleaseOriginalChildPin()
    {
        var (adapter, child) = Setup();
        var request = Request(child);
        var observation = adapter.ObserveSoftwareSecureAdmission(request);
        var fact = Fact(adapter, request);
        var provider = (SoftwareSecureComputeProvider)Field(fact, "Provider")!;
        var create = (ExternalSecureDomainCreateRequest)Field(fact, "Create")!;
        var original = (ExternalSecureDomainCreateProviderResult)Field(fact, "Creation")!;
        var foreignRequest = new ExternalSecureDomainCreateRequest(create.Parent, new(Guid.NewGuid()),
            create.RequiredProperties, ExternalSecureComputeContract.Version);
        var foreign = provider.Create(foreignRequest).Receipt!;
        var forged = new ExternalSecureDomainCreateProviderResult(ExternalSecureDomainProviderStatus.Receipt,
            new(create, foreign.Domain, foreign.ProvenProperties, foreign.Proof));
        fact.GetType().GetField("Creation", Private)!.SetValue(fact, forged);
        Assert.False(adapter.CloseChildDomain(child).IsSuccess);
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.CleanupRequired, adapter.ResolveSoftwareSecureAdmission(request).Status);
        var close = (ExternalOperationIdentity)Field(fact, "CloseOperation")!;
        Assert.Null(provider.QueryClose(foreign.Domain, close).Receipt);
        Assert.Same(foreign, provider.QueryCreate(foreignRequest).Receipt);
        fact.GetType().GetField("Creation", Private)!.SetValue(fact, original);
        Assert.Same(observation, adapter.ResolveSoftwareSecureAdmission(request));
        Assert.NotNull(provider.Close(foreign.Domain, new(new(Guid.NewGuid()), new(1))).Receipt);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.True(adapter.CloseExternalParent(new(Guid.NewGuid(), child.ParentLease)).IsSuccess);
    }

    [Fact]
    public void DuplicateHistoryCannotHideAnotherOriginalAdmissionAndReleaseChild()
    {
        var (adapter, child) = Setup();
        var first = Request(child); var second = Request(child);
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.ExpectedDeny, adapter.ObserveSoftwareSecureAdmission(first).Status);
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.ExpectedDeny, adapter.ObserveSoftwareSecureAdmission(second).Status);
        var source = Field(Fact(adapter, first), "Child")!;
        var history = (IList)source.GetType().GetProperty("OriginalSoftwareAdmissions")!.GetValue(source)!;
        var originalSecond = history[1];
        history[1] = history[0]; // unchanged count cannot prove completeness
        Assert.False(adapter.CloseChildDomain(child).IsSuccess);
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.CleanupRequired, adapter.ResolveSoftwareSecureAdmission(second).Status);
        history[1] = originalSecond;
        Assert.Equal(NeutralSoftwareSecureAdmissionStatus.ExpectedDeny, adapter.ResolveSoftwareSecureAdmission(second).Status);
        Assert.True(adapter.CloseChildDomain(child).IsSuccess);
        Assert.True(adapter.CloseExternalParent(new(Guid.NewGuid(), child.ParentLease)).IsSuccess);
    }

    private static object Fact(HybridCpuExecutableChildAdapter adapter, NeutralSoftwareSecureAdmissionRequest request) =>
        ((IDictionary)adapter.GetType().GetField("softwareAdmissions", Private)!.GetValue(adapter)!)[request.Operation]!;
    private static object? Field(object value, string name) => value.GetType().GetField(name, Private)!.GetValue(value);
    private static NeutralSoftwareSecureAdmissionRequest Request(NeutralChildDomainLease child) => new(child, Guid.NewGuid(), 1, 19, 23);
    private static (HybridCpuExecutableChildAdapter, NeutralChildDomainLease) Setup()
    {
        var parent = new NeutralDomainRuntimeFacade().Bind(NeutralDomainProfile.OrdinaryService).Lease;
        var adapter = new HybridCpuExecutableChildAdapter();
        const NeutralChildAuthorityClass rights = NeutralChildAuthorityClass.Lifecycle;
        var result = adapter.CreateChildDomain(parent, new(new(1,4096), new(rights,rights)));
        Assert.True(result.IsSuccess, result.Reason);
        return (adapter, result.Value);
    }
}
