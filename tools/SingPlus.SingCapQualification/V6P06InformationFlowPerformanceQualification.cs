using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;
using SingPlus.Sip;

namespace SingPlus.SingCapQualification;

public static class V6P06InformationFlowPerformanceQualification
{
    private const int DefaultIterations = 5_000;
    private const int DefaultRounds = 7;
    private const string SchemaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SchemaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static int Run(string[] args)
    {
        try
        {
            Options options = Parse(args);
            Report report = Measure(options.Iterations, options.Rounds);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(report,
                new JsonSerializerOptions { WriteIndented = true });
            string directory = Path.GetDirectoryName(options.OutputPath)
                ?? throw new ArgumentException("Output path must have a parent directory.");
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory,
                $".{Path.GetFileName(options.OutputPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, json);
                File.Move(temporary, options.OutputPath, true);
            }
            finally { File.Delete(temporary); }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static Report Measure(int iterations, int rounds)
    {
        var left = Label(ConfidentialityClassV1.Protected, IntegrityClassV1.Trusted);
        var right = Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted);
        var sink = new FlowPolicyV1(1,
            Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted));
        RegionScenario regions = CreateRegionScenario(910_000, 911_000);
        PlanScenario plan = CreatePlanScenario();
        Warmup(Math.Min(iterations, 1_000), left, right, sink);

        var samples = new List<Sample>(checked(rounds * 5));
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            foreach (string arm in Rotate(round))
            {
                samples.Add(arm switch
                {
                    "lattice-join" => MeasureArm(arm, round, iterations,
                        () => DataLabelLatticeV1.Join(left, right) == right),
                    "flow-policy-check" => MeasureArm(arm, round, iterations,
                        () => DataLabelLatticeV1.CanFlowTo(right, sink)),
                    "region-label-query" => MeasureArm(arm, round, iterations,
                        () => regions.Kernel.Regions.QueryProtectedLabel(regions.First, regions.Owner).IsSuccess),
                    "two-input-region-propagation" => MeasureArm(arm, round, iterations,
                        () => regions.Kernel.Regions.PropagateProtectedLabel(
                            [regions.First, regions.Second], regions.Output, regions.Owner).IsSuccess),
                    _ => MeasureArm(arm, round, iterations,
                        () => SipJobProtectedLabelFlow.Verify(1, plan.Plan, plan.Verified, plan.Sidecars).IsSuccess),
                });
            }
        }

        Summary summary = Summarize(samples);
        return new("singnext.v6.p06-information-flow-performance/1", DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds, 2, 2,
                "All labels, Regions, plan descriptors, and sidecars are prepared before timing. Arms execute lattice join, flow-policy validation/check, an existing protected Region label query, two-input protected Region propagation into an already reusable output, and exact two-edge SipJob plan/sidecar verification. Region arms exercise the existing owner validation and Region locks. Arm order rotates; forced GC occurs only between rounds.",
                "Single-thread named-host managed cost only. Capability-authorized transitions, transport callbacks, CXL/provider submission, physical accelerators, contention, CPU affinity, isolated host, hardware counters, whole-system noninterference, product workload, and SLOs are outside the timing boundary."),
            samples, summary,
            "Protected-contour policy and managed tracking overhead only. These observations do not grant authority or prove physical-provider enforcement, whole-system noninterference, hardware behavior, production readiness, or gate promotion.");
    }

    private static void Warmup(int operations, DataLabelV1 left, DataLabelV1 right, FlowPolicyV1 sink)
    {
        RegionScenario regions = CreateRegionScenario(920_000, 921_000);
        PlanScenario plan = CreatePlanScenario();
        for (int index = 0; index < operations; index++)
        {
            _ = DataLabelLatticeV1.Join(left, right);
            _ = DataLabelLatticeV1.CanFlowTo(right, sink);
            _ = regions.Kernel.Regions.QueryProtectedLabel(regions.First, regions.Owner).IsSuccess;
            _ = regions.Kernel.Regions.PropagateProtectedLabel(
                [regions.First, regions.Second], regions.Output, regions.Owner).IsSuccess;
            _ = SipJobProtectedLabelFlow.Verify(1, plan.Plan, plan.Verified, plan.Sidecars).IsSuccess;
        }
    }

    private static Sample MeasureArm(string arm, int round, int iterations, Func<bool> operation)
    {
        var elapsed = new long[iterations];
        long allocated = 0;
        var wall = Stopwatch.StartNew();
        for (int index = 0; index < iterations; index++)
        {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            bool valid = operation();
            elapsed[index] = Stopwatch.GetTimestamp() - started;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            if (!valid) throw new InvalidOperationException("P06 measured information-flow invariant failed.");
        }
        wall.Stop();
        Array.Sort(elapsed);
        return new(arm, round, iterations, iterations / wall.Elapsed.TotalSeconds,
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.95) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.99) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / iterations);
    }

    private static RegionScenario CreateRegionScenario(ulong processId, ulong domainId)
    {
        var kernel = new RuntimeKernel();
        var manifest = new SingProcessManifestV1(new(processId), new(domainId), 1,
            $"p06-{processId}", ExecutionRole.Sip, MemoryProfile.SipRegion);
        var created = kernel.CreateProcess(manifest);
        if (!created.IsSuccess) throw new InvalidOperationException(created.Message);
        var process = new ProcessHandle(manifest.ProcessId, manifest.Generation);
        var owner = new RegionOwner(manifest.DomainId, manifest.Generation);
        OwnedBuffer<byte> first = kernel.AllocateBuffer<byte>(process, 64).Value
            ?? throw new InvalidOperationException("P06 first Region allocation failed.");
        OwnedBuffer<byte> second = kernel.AllocateBuffer<byte>(process, 64).Value
            ?? throw new InvalidOperationException("P06 second Region allocation failed.");
        OwnedBuffer<byte> output = kernel.AllocateBuffer<byte>(process, 64).Value
            ?? throw new InvalidOperationException("P06 output Region allocation failed.");
        if (!kernel.Regions.AttachProtectedLabel(first.Handle, owner,
                Label(ConfidentialityClassV1.Protected, IntegrityClassV1.Trusted)).IsSuccess ||
            !kernel.Regions.AttachProtectedLabel(second.Handle, owner,
                Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Untrusted)).IsSuccess)
            throw new InvalidOperationException("P06 input label attachment failed.");
        return new(kernel, owner, first.Handle, second.Handle, output.Handle);
    }

    private static PlanScenario CreatePlanScenario()
    {
        SipJobStageDescriptor[] stages = [Stage("stage-a"), Stage("stage-b"), Stage("stage-c")];
        SipJobEdgeDescriptor[] edges =
        [
            Edge("edge-a", "stage-a", "stage-b", SchemaA, SipJobBarrierClass.None),
            Edge("edge-b", "stage-b", "stage-c", SchemaB, SipJobBarrierClass.Publication),
        ];
        var plan = new SipJobPlanDescriptor(1, stages, edges, ["FG-JOB-LINEAR"], string.Empty).WithDigest();
        var verified = new VerifiedPlanMetadata(plan.PlanDigest,
            stages.Select(stage => stage.StageId).ToImmutableArray(), 1);
        ProtectedValueLabelSidecarV1[] sidecars =
        [
            new(1, plan.PlanDigest, "edge-a", SchemaA,
                Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Validated), 7),
            new(1, plan.PlanDigest, "edge-b", SchemaB,
                Label(ConfidentialityClassV1.Restricted, IntegrityClassV1.Validated), 8),
        ];
        return new(plan, verified, sidecars);
    }

    private static SipJobStageDescriptor Stage(string id) => new(1, id, "contract", SchemaA, "operation",
        "request", SchemaA, "response", SchemaB, "thunk", SchemaA, [], [], "transition",
        SipJobInvocationObservability.HiddenIntermediate, "cancel", SipJobEffectClass.None,
        SipJobExecutionClass.ManagedDefault, SipJobStageMode.Synchronous);
    private static SipJobEdgeDescriptor Edge(string id, string from, string to, string schema,
        SipJobBarrierClass barrier) => new(1, id, from, to, SipJobEdgeKind.ClosedCopiedValue,
            "schema", schema, "copy", "managed", "hidden", barrier, "contour");
    private static DataLabelV1 Label(ConfidentialityClassV1 confidentiality, IntegrityClassV1 integrity) =>
        new(1, confidentiality, integrity);

    private static Summary Summarize(IReadOnlyList<Sample> samples)
    {
        return new(Median("lattice-join"), Median("flow-policy-check"), Median("region-label-query"),
            Median("two-input-region-propagation"), Median("two-edge-sipjob-verification"),
            SupportsGatePromotion: false,
            "Absolute host costs separate the small lattice/policy primitives from live managed Region tracking and exact protected SipJob admission.");

        double Median(string arm) => Percentile(samples.Where(sample => sample.Arm == arm)
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray(), 0.5);
    }

    private static string[] Rotate(int round)
    {
        string[] arms = ["lattice-join", "flow-policy-check", "region-label-query",
            "two-input-region-propagation", "two-edge-sipjob-verification"];
        int offset = round % arms.Length;
        return arms.Skip(offset).Concat(arms.Take(offset)).ToArray();
    }

    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    private sealed record RegionScenario(RuntimeKernel Kernel, RegionOwner Owner,
        RegionHandle First, RegionHandle Second, RegionHandle Output);
    private sealed record PlanScenario(SipJobPlanDescriptor Plan, VerifiedPlanMetadata Verified,
        ProtectedValueLabelSidecarV1[] Sidecars);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        int RegionInputCount, int SipJobEdgeCount, string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int Round, int Operations, double ThroughputPerSecond,
        double MedianNanoseconds, double P95Nanoseconds, double P99Nanoseconds,
        double MaximumNanoseconds, double AllocatedBytesPerOperation);
    public sealed record Summary(double LatticeJoinMedianNanoseconds,
        double FlowPolicyCheckMedianNanoseconds, double RegionLabelQueryMedianNanoseconds,
        double TwoInputRegionPropagationMedianNanoseconds, double TwoEdgeSipJobVerificationMedianNanoseconds,
        bool SupportsGatePromotion, string Interpretation);
    public sealed record Report(string Schema, DateTimeOffset CapturedUtc, EnvironmentTuple Environment,
        Methodology Methodology, IReadOnlyList<Sample> Samples, Summary Summary, string ClaimBoundary);

    private static Options Parse(string[] args)
    {
        string? output = null;
        int iterations = DefaultIterations;
        int rounds = DefaultRounds;
        for (int index = 0; index < args.Length; index++)
        {
            string value = index + 1 < args.Length ? args[index + 1] : string.Empty;
            switch (args[index])
            {
                case "--output": output = value; index++; break;
                case "--iterations" when int.TryParse(value, out int parsed): iterations = parsed; index++; break;
                case "--rounds" when int.TryParse(value, out int parsed): rounds = parsed; index++; break;
                default: throw new ArgumentException($"Unknown or malformed option: {args[index]}");
            }
        }
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("--output is required.");
        if (iterations is < 500 or > 100_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (rounds is < 5 or > 25) throw new ArgumentOutOfRangeException(nameof(rounds));
        return new(Path.GetFullPath(output), iterations, rounds);
    }
}
