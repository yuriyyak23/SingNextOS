using SingPlus.Platform;
using SingPlus.Contracts;
using SingPlus.Runtime;
using System.Reflection;
using HybridCPU.ExternalRuntime;
using SingPlus.Platform.HybridCpu;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;

foreach (string schedule in new[] { "normal", "lost-reply", "closed-child" })
{
    var host = new NeutralDomainRuntimeFacade();
    var parent = host.Bind(NeutralDomainProfile.OrdinaryService).Lease;
    var adapter = new HybridCpuExecutableChildAdapter();
    const NeutralChildAuthorityClass rights = NeutralChildAuthorityClass.Lifecycle;
    var childResult = adapter.CreateChildDomain(parent, new(new(1, 4096), new(rights, rights)));
    Require(childResult.IsSuccess);
    var child = childResult.Value;
    var request = new NeutralSoftwareSecureAdmissionRequest(child, Guid.NewGuid(), 1, 19, 23);
    if (schedule == "closed-child") Require(adapter.CloseChildDomain(child).IsSuccess);
    int calls = 0;
    NeutralSoftwareSecureAdmissionObservation? original = null;
    try
    {
        calls++; original = adapter.ObserveSoftwareSecureAdmission(request);
        if (schedule == "lost-reply") throw new IOException("Response lost after the actual consumer completed.");
    }
    catch (IOException) { }
    var observed = schedule == "closed-child" ? original! : adapter.ResolveSoftwareSecureAdmission(request);
    Require(!observed.IsExecutionAllowed);
    if (schedule == "closed-child") Require(observed.Status == NeutralSoftwareSecureAdmissionStatus.Denied);
    else
    {
        Require(observed.Status == NeutralSoftwareSecureAdmissionStatus.ExpectedDeny &&
            observed.MachineAttempt != 0 && observed.NoEffectCommit == 1 && ReferenceEquals(observed.Request, request));
        Require(ReferenceEquals(original, observed) && ReferenceEquals(observed, adapter.ObserveSoftwareSecureAdmission(request)));
        Require(adapter.ObserveSoftwareSecureAdmission(new(child, Guid.NewGuid(), 1, 19, 23,
            NeutralSoftwareSecureProfile.RegisterLocalExecution)).Status == NeutralSoftwareSecureAdmissionStatus.Unsupported);
        Require(adapter.ObserveSoftwareSecureAdmission(new(child with { Epoch = new(child.Epoch.Value + 1) },
            Guid.NewGuid(), 1, 19, 23)).Status == NeutralSoftwareSecureAdmissionStatus.Denied);
        Require(adapter.CloseChildDomain(child).IsSuccess);
    }
    Require(calls == 1 && adapter.CloseExternalParent(new(Guid.NewGuid(), parent)).IsSuccess);
    Require(host.Close(parent).IsClosed);
    Console.WriteLine($"PASS schedule={schedule} operation={request.Operation} generation={request.Generation} child={child.Handle.Value}/{child.Epoch.Value} " +
        $"status={observed.Status} attempt={observed.MachineAttempt} noEffectCommit={observed.NoEffectCommit} calls={calls} childClosed=true parentClosed=true executionAllowed={observed.IsExecutionAllowed}");
}
var platform = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), new HybridCpuExecutableChildAdapter());
Require(platform.QueryFeatures().Resolve(PlatformFeatureFamily.SecureDomains).Availability == PlatformFeatureAvailability.Unavailable);
Console.WriteLine("PASS schedules=3; actual packaged OS-neutral consumer; expected-deny only; ProductionSecure unavailable");

foreach (string schedule in new[] { "normal", "lost-reply", "revoke", "late-source" }) RunKernel(schedule);
Console.WriteLine("PASS kernelSchedules=4; semantic capability/domain/budget/operation -> packaged no-effect retire; cleanup independent of visibility");

foreach (var vector in new (string Schedule, ulong Left, ulong Right)[]
{
    ("normal", 19, 23), ("wrap", ulong.MaxValue, 1), ("high-bit", 1UL << 63, 1UL << 63), ("zero", 0, 0),
    ("lost-native-reply", 19, 23), ("lost-os-reply", 19, 23), ("revoke-before-publication", 19, 23),
    ("revoke-after-staging", 19, 23), ("bridge-aba", 19, 23), ("bridge-root-aba", 19, 23), ("late-source", 19, 23),
}) RunArithmetic(vector.Schedule, vector.Left, vector.Right);
Console.WriteLine("PASS arithmeticSchedules=11; actual native x3/effect1; separate final OS publication; exact query/cleanup; ProductionSecure unavailable");

static void RunArithmetic(string schedule, ulong left, ulong right)
{
    const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    var adapter = new HybridCpuExecutableChildAdapter();
    var kernel = new RuntimeKernel(new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), adapter));
    Require(kernel.CreateProcess(new(new(301), new(302), 1, "secure-arithmetic-workload", ExecutionRole.Sip, MemoryProfile.SipRegion)).IsSuccess);
    var owner = new ProcessHandle(new(301), 1);
    var create = kernel.MintCapability(new(302), owner, ResourceKind.Virtualization,
        VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
    var vm = kernel.CreateVirtualDomain(owner, create, new(1, 4096)).Value;
    Require(kernel.ConfigureVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
    var execute = kernel.MintCapability(new(302), owner, ResourceKind.SecureCompute,
        RuntimeKernel.SoftwareSecureRegisterLocalResource(vm.Domain), CapabilityRights.Execute).Value!.CapabilityId;
    var budgetCap = kernel.MintCapability(new(302), owner, ResourceKind.KernelService,
        CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
    var budget = kernel.AdmitProcessBudget(owner, budgetCap, owner, "secure-arithmetic", [new(ServiceBudgetDimension.ExternalOperations, 1)]).Value!;
    int nativeCallbacks = 0, stages = 0; object? original = null; string identities = "";
    adapter.GetType().GetProperty("BeforeSoftwareSecureAdmissionPublication", hidden)!.SetValue(adapter, (Action)(() =>
    {
        nativeCallbacks++;
        var nativeFacts = (System.Collections.IDictionary)adapter.GetType().GetField("softwareAdmissions", hidden)!.GetValue(adapter)!;
        object native = nativeFacts.Values.Cast<object>().Single();
        var admission = (SecureComputeCanonicalAdmissionResult)native.GetType().GetField("Admission", hidden)!.GetValue(native)!;
        Require(admission.IsAllowed && admission.RegisterLocalRetirement is { EffectCount: 1 } retirement &&
            retirement.Register3Value == unchecked(left + right));
        var attempt = admission.BackendAttempt!;
        var host = (SoftwareSecureRegisterLocalCoreHost)native.GetType().GetField("CoreHost", hidden)!.GetValue(native)!;
        var coreObservation = (SoftwareSecureRegisterLocalCoreObservation)native.GetType().GetField("CoreObservation", hidden)!.GetValue(native)!;
        Require(ReferenceEquals(coreObservation, host.QueryCoreObservation(admission.Request, admission)) &&
            ReferenceEquals(coreObservation.Request, admission.Request) && ReferenceEquals(coreObservation.Admission, admission) &&
            coreObservation.CoreValue == unchecked(left + right) && attempt.OwnerContext == 0 && attempt.SourceSlot == 2);
        var retired = admission.RegisterLocalRetirement!;
        Require(ReferenceEquals(admission.Request, attempt.Request) && ReferenceEquals(attempt, retired.Submission.Attempt));
        identities = $"nativeOperation={attempt.Request.Operation.Handle.Value}/{attempt.Request.Operation.Generation.Value} " +
            $"child={attempt.Request.Binding.Child.Handle.Value}/{attempt.Request.Binding.Child.Epoch.Value} " +
            $"domain={attempt.Request.Binding.SecureDomain.Handle.Value}/{attempt.Request.Binding.SecureDomain.Generation.Value} policy={attempt.Request.Binding.PolicyGeneration} " +
            $"provider={retired.Submission.Consumption.ProviderScope} machine={attempt.MachineScope}/{attempt.MachineGeneration} " +
            $"attempt={attempt.CpuAttempt} placement={attempt.WorkingBundle}:{attempt.SourceSlot}/{attempt.WorkingSlot}/{attempt.PhysicalLane} commit={retired.CommitSequence}";
        var routes = (System.Collections.IDictionary)typeof(RuntimeKernel).GetField("_softwareSecureRoutes", hidden)!.GetValue(kernel)!;
        original = routes.Values.Cast<object>().Single();
        Require(original.GetType().GetField("PublishedResult", hidden)!.GetValue(original) is null);
        Require(kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.ExternalOperations).Used == 1);
        if (schedule == "lost-native-reply") throw new IOException("Native reply lost after actual retire.");
    }));
    typeof(RuntimeKernel).GetProperty("BeforeSoftwareSecureRegisterLocalPublication", hidden)!.SetValue(kernel, (Action)(() =>
    {
        Require(original!.GetType().GetField("PublishedResult", hidden)!.GetValue(original) is null);
        if (schedule == "revoke-before-publication") Require(kernel.RevokeCapability(execute).IsSuccess);
        if (schedule == "late-source") Require(!kernel.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess);
    }));
    typeof(RuntimeKernel).GetProperty("AfterSoftwareSecureRegisterLocalStaging", hidden)!.SetValue(kernel, (Action)(() =>
    {
        stages++;
        Require(!Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", hidden)!.GetValue(kernel)!));
        Require(original!.GetType().GetField("PublishedResult", hidden)!.GetValue(original) is null);
        if (schedule == "revoke-after-staging") Require(kernel.RevokeCapability(execute).IsSuccess);
        if (schedule is "bridge-aba" or "bridge-root-aba")
        {
            var bindings = (System.Collections.IDictionary)typeof(PlatformAuthorityBridge).GetField(
                schedule == "bridge-aba" ? "_childBindings" : "_domains", hidden)!.GetValue(kernel.PlatformAuthority)!;
            object source = bindings.Values.Cast<object>().Single();
            var state = source.GetType().GetProperty(schedule == "bridge-aba" ? "State" : "AuthorityState")!;
            state.SetValue(source, state.GetValue(source));
        }
    }));
    var result = kernel.ExecuteSoftwareSecureRegisterLocal(owner, vm.Domain, vm.ConfigureCapability, execute, left, right);
    Require(result.IsSuccess);
    if (schedule == "lost-os-reply")
        try { throw new IOException("Caller lost response after OS publication."); } catch (IOException) { }
    adapter.GetType().GetProperty("BeforeSoftwareSecureAdmissionPublication", hidden)!.SetValue(adapter, null);
    var resolved = kernel.ResolveSoftwareSecureRegisterLocal(owner, result.Value!.Operation);
    Require(resolved.IsSuccess && resolved.Value!.ResourcesClosed && nativeCallbacks == 1);
    bool positive = schedule is "normal" or "wrap" or "high-bit" or "zero" or "lost-os-reply";
    var published = resolved.Value!;
    Require(published.WasPublished == positive && published.Value == (positive ? unchecked(left + right) : (ulong?)null));
    Require(published.RegisterLocalCommit == (positive ? 1UL : 0UL));
    Require(stages == (positive || schedule is "revoke-after-staging" or "bridge-aba" or "bridge-root-aba" ? 1 : 0));
    Require(kernel.QueryExternalOperation(owner, published.Operation).Value!.State == ExternalOperationState.Released);
    Require(kernel.QueryBudget(budget.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.ExternalOperations).Used == 0);
    Require(kernel.ResolveSoftwareSecureRegisterLocal(owner, published.Operation).Value == published && nativeCallbacks == 1);
    Require(kernel.DestroyVirtualDomain(owner, vm.Domain, vm.ConfigureCapability).IsSuccess && kernel.TerminateProcess(owner).IsSuccess);
    Console.WriteLine($"PASS arithmeticSchedule={schedule} operation={published.Operation.OperationId.Value}/{published.Operation.Generation.Value} " +
        $"left={left} right={right} value={published.Value} published={published.WasPublished} nativeCallbacks={nativeCallbacks} stages={stages} budget=0 closed=true {identities}");
}

static void RunKernel(string schedule)
{
    var adapter = new HybridCpuExecutableChildAdapter();
    var provider = new HybridCpuPlatformAuthorityProvider(new NeutralDomainRuntimeFacade(), adapter);
    var kernel = new RuntimeKernel(provider);
    var process = kernel.CreateProcess(new(new(201), new(202), 1, "secure-software-workload", ExecutionRole.Sip, MemoryProfile.SipRegion));
    Require(process.IsSuccess);
    var owner = new ProcessHandle(new(201), 1);
    var create = kernel.MintCapability(new(202), owner, ResourceKind.Virtualization,
        VirtualizationResourceIds.Create, CapabilityRights.Configure).Value!.CapabilityId;
    var vm = kernel.CreateVirtualDomain(owner, create, new(1, 4096));
    Require(vm.IsSuccess && kernel.ConfigureVirtualDomain(owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
    var capability = kernel.MintCapability(new(202), owner, ResourceKind.SecureCompute,
        RuntimeKernel.SoftwareSecureAdmissionResource(vm.Value.Domain), CapabilityRights.Configure).Value!.CapabilityId;
    var budgetCap = kernel.MintCapability(new(202), owner, ResourceKind.KernelService,
        CapabilityResourceIds.BudgetAdministration, CapabilityRights.Configure).Value!.CapabilityId;
    var budget = kernel.AdmitProcessBudget(owner, budgetCap, owner, "secure-software", [new(ServiceBudgetDimension.ExternalOperations, 1)]);
    Require(budget.IsSuccess);
    int callbacks = 0;
    const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    adapter.GetType().GetProperty("BeforeSoftwareSecureAdmissionPublication", hidden)!.SetValue(adapter, (Action)(() =>
    {
        callbacks++;
        Require(!Monitor.IsEntered(typeof(RuntimeKernel).GetField("_platformMemoryUseGate", hidden)!.GetValue(kernel)!));
        Require(kernel.QueryBudget(budget.Value!.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.ExternalOperations).Used == 1);
        if (schedule == "revoke") Require(kernel.RevokeCapability(capability).IsSuccess);
        if (schedule == "late-source") Require(!kernel.DestroyVirtualDomain(owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
        if (schedule == "lost-reply") throw new IOException("Lost reply after no-effect retire.");
    }));
    var result = kernel.ObserveSoftwareSecureAdmission(owner, vm.Value.Domain, vm.Value.ConfigureCapability, capability, 19, 23);
    Require(result.IsSuccess && callbacks == 1 && !result.Value!.IsExecutionAllowed);
    var resolved = kernel.ResolveSoftwareSecureAdmission(owner, result.Value!.Operation);
    Require(resolved.IsSuccess && resolved.Value!.ResourcesClosed && callbacks == 1);
    Require(resolved.Value!.ExpectedDeny == (schedule is "normal" or "lost-reply"));
    Require(resolved.Value.NoEffectCommit == (schedule is "normal" or "lost-reply" ? 1UL : 0UL));
    Require(kernel.QueryExternalOperation(owner, result.Value.Operation).Value!.State == ExternalOperationState.Released);
    Require(kernel.QueryBudget(budget.Value!.ProcessBudget).Value!.Usage.Single(x => x.Dimension == ServiceBudgetDimension.ExternalOperations).Used == 0);
    Require(kernel.ResolveSoftwareSecureAdmission(owner, result.Value.Operation).Value!.ResourcesClosed && callbacks == 1);
    Require(kernel.DestroyVirtualDomain(owner, vm.Value.Domain, vm.Value.ConfigureCapability).IsSuccess);
    Require(kernel.TerminateProcess(owner).IsSuccess);
    Console.WriteLine($"PASS kernelSchedule={schedule} operation={result.Value.Operation.OperationId.Value}/{result.Value.Operation.Generation.Value} " +
        $"attempt={resolved.Value.MachineAttempt} commit={resolved.Value.NoEffectCommit} callbacks={callbacks} budget=0 operationReleased=true domainClosed=true executionAllowed=false");
}

static void Require(bool value) { if (!value) throw new InvalidOperationException("Software admission workload oracle failed."); }
