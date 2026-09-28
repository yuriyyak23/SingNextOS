using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Platform;
using SingPlus.Platform.Host;
using SingPlus.Runtime;

namespace SingPlus.SingCapQualification;

public static class V6P09DeviceTrustPerformanceQualification
{
    private const int DefaultIterations = 5_000;
    private const int DefaultRounds = 7;
    private const string DeviceIdentity = "device:cxl-model:p09";
    private const string Firmware = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const ulong PolicyGeneration = 17;

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
        Scenario scenario = CreateScenario();
        Warmup(Math.Min(iterations, 1_000), scenario);
        var samples = new List<Sample>(checked(rounds * 4));
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            foreach (string arm in Rotate(round))
            {
                samples.Add(arm switch
                {
                    "evidence-validation" => MeasureArm(arm, round, iterations,
                        () => ValidEvidence(scenario.Evidence.Validate())),
                    "predicate-evaluation" => MeasureArm(arm, round, iterations,
                        () => Satisfied(scenario.Obligation, scenario.Evidence)),
                    "sideband-projection-and-predicate" => MeasureArm(arm, round, iterations,
                        () => ProjectAndEvaluate(scenario.State, scenario.Assignment, scenario.Obligation)),
                    _ => MeasureArm(arm, round, iterations,
                        () => QueryProjectAndEvaluate(scenario.Provider, scenario.Endpoint,
                            scenario.DeviceGeneration, scenario.Assignment, scenario.Obligation)),
                });
            }
        }

        Summary summary = Summarize(samples);
        return new("singnext.v6.p09-device-trust-performance/1", DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds,
                "One deterministic model endpoint is registered before timing with an explicit firmware digest. Arms validate provider-neutral evidence, evaluate the exact nine-field trust predicate, project an already queried CXL security snapshot and evaluate it, or issue a fresh model-provider query before projection/evaluation. Arm order rotates; forced GC occurs only between rounds.",
                "Managed predicate and deterministic in-memory CXL model only. No network, certificate chain, revocation service, nonce exchange, cryptographic signature verification, SPDM/TDISP/IDE handshake, device reset, physical firmware, contention, CPU affinity, isolated host, hardware counter, product workload, or SLO is measured."),
            samples, summary,
            "Named-host policy/model evaluation overhead only. These observations do not characterize an attestation handshake, authorize device access, prove a hardware trust root, qualify physical firmware, establish production latency, or promote the gate.");
    }

    private static void Warmup(int operations, Scenario scenario)
    {
        for (int index = 0; index < operations; index++)
        {
            _ = ValidEvidence(scenario.Evidence.Validate());
            _ = Satisfied(scenario.Obligation, scenario.Evidence);
            _ = ProjectAndEvaluate(scenario.State, scenario.Assignment, scenario.Obligation);
            _ = QueryProjectAndEvaluate(scenario.Provider, scenario.Endpoint,
                scenario.DeviceGeneration, scenario.Assignment, scenario.Obligation);
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
            if (!valid) throw new InvalidOperationException("P09 measured trust invariant failed.");
        }
        wall.Stop();
        Array.Sort(elapsed);
        return new(arm, round, iterations, iterations / wall.Elapsed.TotalSeconds,
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.95) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.99) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / iterations);
    }

    private static bool ValidEvidence(TrustEvidenceV1 evidence) =>
        evidence.EvidenceSequence == 1 && !evidence.GrantsAuthority &&
        !evidence.AuthorizesExecution && !evidence.AuthorizesRegionAccess;

    private static bool Satisfied(TrustObligationV1 obligation, TrustEvidenceV1 evidence)
    {
        DeviceTrustPredicateDecisionV1 decision = DeviceTrustPredicateEvaluatorV1.Evaluate(obligation, evidence);
        return decision.IsSatisfied && !decision.GrantsAuthority;
    }

    private static bool ProjectAndEvaluate(CxlSecurityStateSnapshot state,
        PlatformDeviceLease assignment, TrustObligationV1 obligation)
    {
        KernelResult<TrustEvidenceV1> projected = CxlDeviceTrustEvidenceAdapter.Project(
            state, assignment, PolicyGeneration);
        return projected.IsSuccess && Satisfied(obligation, projected.Value);
    }

    private static bool QueryProjectAndEvaluate(CxlSecurityModelProvider provider, CxlEndpointId endpoint,
        CxlDeviceGeneration generation, PlatformDeviceLease assignment, TrustObligationV1 obligation)
    {
        PlatformAuthorityResult<CxlSecurityStateSnapshot> state = provider.QuerySecurityState(endpoint, generation);
        return state.IsSuccess && ProjectAndEvaluate(state.Value!, assignment, obligation);
    }

    private static Scenario CreateScenario()
    {
        var endpoint = new CxlEndpointId("cxl-model:p09");
        var generation = new CxlDeviceGeneration(7);
        var provider = new CxlSecurityModelProvider();
        CxlSecurityProperties properties = CxlSecurityProperties.LinkEncryptionAvailable |
            CxlSecurityProperties.IdeEnabled | CxlSecurityProperties.DeviceAuthenticated |
            CxlSecurityProperties.FirmwareMeasured | CxlSecurityProperties.TrustedExecutionCapable;
        PlatformAuthorityResult registered = provider.Register(endpoint, generation, properties,
            CxlSecurityHealth.Ready, Firmware);
        if (!registered.IsSuccess) throw new InvalidOperationException(registered.Message);
        CxlSecurityStateSnapshot state = provider.QuerySecurityState(endpoint, generation).Value
            ?? throw new InvalidOperationException("P09 model security state query failed.");
        var assignment = new PlatformDeviceLease(new(11), new(13), default,
            new(DeviceIdentity), PlatformDeviceRights.Read | PlatformDeviceRights.Write);
        KernelResult<TrustEvidenceV1> projected = CxlDeviceTrustEvidenceAdapter.Project(
            state, assignment, PolicyGeneration);
        if (!projected.IsSuccess) throw new InvalidOperationException(projected.Message);
        TrustEvidenceV1 evidence = projected.Value;
        var obligation = new TrustObligationV1(1, DeviceIdentity, Firmware,
            DeviceTrustAssuranceV1.ModelOnly, state.ProviderTrustGeneration,
            generation.Value, assignment.Generation.Value, state.ResetGeneration, PolicyGeneration);
        if (!Satisfied(obligation, evidence)) throw new InvalidOperationException("P09 trust scenario is not satisfied.");
        return new(provider, endpoint, generation, state, assignment, obligation, evidence);
    }

    private static Summary Summarize(IReadOnlyList<Sample> samples)
    {
        return new(Median("evidence-validation"), Median("predicate-evaluation"),
            Median("sideband-projection-and-predicate"), Median("model-query-projection-and-predicate"),
            SupportsGatePromotion: false,
            "Absolute host costs separate schema validation, predicate comparison, sideband projection, and the deterministic in-memory model query.");

        double Median(string arm) => Percentile(samples.Where(sample => sample.Arm == arm)
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray(), 0.5);
    }

    private static string[] Rotate(int round)
    {
        string[] arms = ["evidence-validation", "predicate-evaluation",
            "sideband-projection-and-predicate", "model-query-projection-and-predicate"];
        int offset = round % arms.Length;
        return arms.Skip(offset).Concat(arms.Take(offset)).ToArray();
    }

    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    private sealed record Scenario(CxlSecurityModelProvider Provider, CxlEndpointId Endpoint,
        CxlDeviceGeneration DeviceGeneration, CxlSecurityStateSnapshot State,
        PlatformDeviceLease Assignment, TrustObligationV1 Obligation, TrustEvidenceV1 Evidence);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int Round, int Operations, double ThroughputPerSecond,
        double MedianNanoseconds, double P95Nanoseconds, double P99Nanoseconds,
        double MaximumNanoseconds, double AllocatedBytesPerOperation);
    public sealed record Summary(double EvidenceValidationMedianNanoseconds,
        double PredicateEvaluationMedianNanoseconds, double SidebandProjectionAndPredicateMedianNanoseconds,
        double ModelQueryProjectionAndPredicateMedianNanoseconds,
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
