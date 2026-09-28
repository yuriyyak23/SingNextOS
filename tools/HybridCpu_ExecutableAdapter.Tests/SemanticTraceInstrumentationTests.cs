using SingPlus.Contracts;
using HybridCPU.ExternalRuntime;
using HybridCPU.ExternalRuntime.Contracts;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using YAKSys_Hybrid_CPU.Core;
using YAKSys_Hybrid_CPU.ExecutableAdapter;
using Xunit;

namespace HybridCpu_ExecutableAdapter.Tests;

public sealed class SemanticTraceInstrumentationTests
{
    [Fact]
    public void ExecutableStartEmitsCanonicalNonAuthoritativeLifecycleDirectly()
    {
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(sink);
        var request = PrepareStart(adapter);

        var result = adapter.StartExecutableArtifact(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous, result.Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(
            [SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
             SemanticTraceEventKindV1.Quarantined],
            sink.Events.Select(static item => item.Kind));
        Assert.True(SemanticTraceValidatorV1.Validate(sink.Events).IsValid);
        Assert.All(sink.Events, item =>
        {
            Assert.False(item.AuthorizesExecution);
            Assert.False(item.AuthorizesEffect);
            Assert.False(item.AuthorizesPublication);
            Assert.Equal("HybridCPU.ExternalRuntime.V3", item.Source);
        });
    }

    [Fact]
    public void ObservationFailureCannotChangeExecutableOutcome()
    {
        var baselineAdapter = new HybridCpuExecutableChildAdapter();
        var baseline = baselineAdapter.StartExecutableArtifact(PrepareStart(baselineAdapter));
        var observedAdapter = new HybridCpuExecutableChildAdapter(new ThrowingSink());
        var observed = observedAdapter.StartExecutableArtifact(PrepareStart(observedAdapter));

        Assert.Equal(baseline.Status, observed.Status);
        Assert.Equal(baseline.Reason, observed.Reason);
    }

    [Fact]
    public void ForgedSuccessfulStartReceiptIsQuarantinedAndCannotBeRetried()
    {
        var runtime = new HybridCpuExternalRuntime();
        var proxy = DispatchProxy.Create<IHybridCpuChildDomainRuntimeV3,
            AdapterBoundaryTests.FaultingChildRuntimeProxy>();
        ((AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy).Inner = runtime;
        ((AdapterBoundaryTests.FaultingChildRuntimeProxy)proxy).ForgeStart = true;
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(runtime, proxy, sink);
        var request = PrepareStart(adapter);

        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal(NeutralVirtualizationStatus.Ambiguous,
            adapter.StartExecutableArtifact(request).Status);
        Assert.Equal([SemanticTraceEventKindV1.Submit, SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined], sink.Events.Select(static item => item.Kind));
    }

    [Fact]
    public void ExecutableStartMatchesReferenceQuarantineOwnerProjection()
    {
        var sink = new CollectingSink();
        var adapter = new HybridCpuExecutableChildAdapter(sink);

        var result = adapter.StartExecutableArtifact(PrepareStart(adapter));

        Assert.False(result.IsSuccess);
        SemanticTraceEventKindV1[] referenceKinds =
        [
            SemanticTraceEventKindV1.Submit,
            SemanticTraceEventKindV1.EffectPossible,
            SemanticTraceEventKindV1.Quarantined,
        ];
        var reference = referenceKinds.Select((kind, index) => new SemanticTraceEventV1(
            SemanticTraceEventV1.CurrentVersion, "reference-child-start:17:3", checked((ulong)index + 1),
            kind, "SingNext.Reference.V1", Digest("reference-admitted-generation"),
            Digest($"reference-evidence:{kind}"))).ToArray();
        var comparison = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, sink.Events,
            Digest("singnext-reference-source-tuple"), Digest("hybridcpu-executable-source-tuple"));

        Assert.True(comparison.IsEquivalent);
        Assert.Null(comparison.Difference);
    }

    private static NeutralChildExecutionStartRequest PrepareStart(HybridCpuExecutableChildAdapter adapter)
    {
        var parent = new NeutralDomainBindingLease(new(1), new(1));
        const NeutralChildAuthorityClass authority = NeutralChildAuthorityClass.Lifecycle |
            NeutralChildAuthorityClass.Execution | NeutralChildAuthorityClass.GuestMemory;
        var childResult = adapter.CreateChildDomain(parent,
            new(new(1, 8192), new(authority, authority)));
        Assert.True(childResult.IsSuccess, childResult.Reason);
        var parentMapping = new NeutralOwnedRegionMappingLease(parent,
            new(0, 8192, NeutralMemoryAccess.Read), NeutralMemoryCoherenceModel.NonCoherent, new(1), new(1));
        var mapResult = adapter.MapGuestRegion(new(childResult.Value, parentMapping,
            new(0, 8192), NeutralGuestMemoryAccess.Read | NeutralGuestMemoryAccess.Execute));
        Assert.True(mapResult.IsSuccess, mapResult.Reason);
        var artifactResult = adapter.BindExecutableArtifact(new(childResult.Value, mapResult.Value,
            CreateExecutablePackage(), 3));
        Assert.True(artifactResult.IsSuccess, artifactResult.Reason);
        return new(childResult.Value, artifactResult.Value, new(17), new(3));
    }

    private static byte[] CreateExecutablePackage()
    {
        Assembly compiler = Assembly.Load("HybridCPU.Compiler.Core");
        Type optionsType = Required("HybridCPU.Compiler.Core.Target.Runtime.HybridCpuRestrictedStartupOptionsV1");
        object options = optionsType.GetProperty("Production", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        object linkOptions = compiler.GetType("HybridCPU.Compiler.Core.Target.Link.HybridCpuStaticLinkOptionsV1")!
            .GetProperty("Production", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        ulong imageBase = (ulong)linkOptions.GetType().GetProperty("ImageBase")!.GetValue(linkOptions)!;
        string optionsDigest = (string)linkOptions.GetType().GetProperty("OptionsDigest")!.GetValue(linkOptions)!;
        byte[] image = new byte[1024];
        string imageDigest = Convert.ToHexStringLower(SHA256.HashData(image));
        string mapDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("p05-hybridcpu-trace")));

        Type sectionType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkedSectionV1");
        Type sectionKind = Required("HybridCPU.Compiler.Core.Target.HybridCpuObjectSectionKind");
        object section = Activator.CreateInstance(sectionType, "p05", ".text", Enum.Parse(sectionKind, "Code"),
            imageBase, (ulong)image.Length, 256)!;
        Type symbolType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkedSymbolV1");
        Type bindingType = Required("HybridCPU.Compiler.Core.Target.HybridCpuSymbolBinding");
        Type visibilityType = Required("HybridCPU.Compiler.Core.Target.HybridCpuSymbolVisibility");
        object symbol = Activator.CreateInstance(symbolType, "main", "p05", Enum.Parse(bindingType, "Global"),
            Enum.Parse(visibilityType, "Default"), imageBase, 256UL)!;

        Type artifactType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuStaticLinkArtifactV1");
        Type statusType = Required("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkStatusV1");
        object artifact = Activator.CreateInstance(artifactType, Enum.Parse(statusType, "Success"), imageBase, image,
            imageDigest, mapDigest, optionsDigest, ArrayOf(sectionType, section), ArrayOf(symbolType, symbol),
            Empty("HybridCPU.Compiler.Core.Target.Link.HybridCpuAppliedRelocationV1"),
            Empty("HybridCPU.Compiler.Core.Target.Link.HybridCpuLinkDiagnosticV1"))!;
        Type requestType = Required("HybridCPU.Compiler.Core.Target.Runtime.HybridCpuRestrictedStartupRequestV1");
        object request = Activator.CreateInstance(requestType, artifact, "main", 0, null, null)!;
        Type builderType = Required("HybridCPU.Compiler.Core.Target.Runtime.HybridCpuRestrictedImageBuilderV1");
        object built = builderType.GetMethod("Build")!.Invoke(Activator.CreateInstance(builderType), [request, options])!;
        return (byte[])built.GetType().GetProperty("PackageBytes")!.GetValue(built)!;

        Type Required(string name) => compiler.GetType(name) ?? throw new TypeLoadException(name);
        Array Empty(string name) => Array.CreateInstance(Required(name), 0);
        static Array ArrayOf(Type type, object value)
        {
            Array array = Array.CreateInstance(type, 1);
            array.SetValue(value, 0);
            return array;
        }
    }

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class CollectingSink : ISemanticTraceSinkV1
    {
        public List<SemanticTraceEventV1> Events { get; } = [];
        public bool TryRecord(SemanticTraceEventV1 traceEvent)
        {
            Events.Add(traceEvent);
            return true;
        }
    }

    private sealed class ThrowingSink : ISemanticTraceSinkV1
    {
        public bool TryRecord(SemanticTraceEventV1 traceEvent) =>
            throw new InvalidOperationException("observation channel unavailable");
    }
}
