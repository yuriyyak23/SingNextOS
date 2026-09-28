using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;

namespace SingPlus.SingCapQualification;

public static class V6P05RefinementPerformanceQualification
{
    private const int DefaultIterations = 5_000;
    private const int DefaultRounds = 7;
    private static readonly string ReferenceTupleDigest = new('a', 64);
    private static readonly string CandidateTupleDigest = new('b', 64);

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
        SemanticTraceEventV1[] reference = SemanticTrace();
        SemanticTraceEventV1[] candidate = reference.Select(item => item with
        {
            OperationCorrelation = "candidate-operation",
            Source = "candidate-provider",
            Sequence = item.Sequence + 20,
            EvidenceDigest = new string('c', 64),
        }).ToArray();
        ProviderTraceEventV1[] provider = ProviderTrace();
        Warmup(Math.Min(iterations, 1_000), reference, candidate, provider);

        var samples = new List<Sample>(checked(rounds * 4));
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            foreach (string arm in Rotate(round))
            {
                samples.Add(arm switch
                {
                    "refinement-decision" => MeasureArm(arm, round, iterations, Refines),
                    "trace-validate" => MeasureArm(arm, round, iterations,
                        () => SemanticTraceValidatorV1.Validate(reference).IsValid),
                    "trace-differential" => MeasureArm(arm, round, iterations,
                        () => SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
                            ReferenceTupleDigest, CandidateTupleDigest).IsEquivalent),
                    _ => MeasureArm(arm, round, iterations, () => ProjectValidateAndCompare(provider, reference)),
                });
            }
        }

        Summary summary = Summarize(samples);
        return new("singnext.v6.p05-refinement-performance/1", DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds, reference.Length, provider.Length,
                "Inputs are constructed before timing. The four arms execute a supported mandatory refinement decision, validate a seven-event semantic lifecycle, compare two equivalent allowed projections, and project an eight-event provider trace (one provider-private event) before validation and differential comparison. Arm order rotates; forced GC occurs only between rounds.",
                "Checker/projection CPU cost only. Trace emission, sink dispatch, provider callbacks, external process execution, payload movement, contention, CPU affinity, isolated host, hardware counters, formal proof, product workload, and SLOs are outside the timing boundary."),
            samples, summary,
            "Named-host executable checker and in-memory trace projection overhead only. These observations do not prove physical instrumentation coverage, finite-model verification, hardware behavior, production readiness, or gate promotion.");
    }

    private static void Warmup(int operations, SemanticTraceEventV1[] reference,
        SemanticTraceEventV1[] candidate, ProviderTraceEventV1[] provider)
    {
        for (int index = 0; index < operations; index++)
        {
            _ = Refines();
            _ = SemanticTraceValidatorV1.Validate(reference).IsValid;
            _ = SemanticTraceDifferentialV1.CompareAllowedProjection(reference, candidate,
                ReferenceTupleDigest, CandidateTupleDigest).IsEquivalent;
            _ = ProjectValidateAndCompare(provider, reference);
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
            if (!valid) throw new InvalidOperationException("P05 measured refinement/trace invariant failed.");
        }
        wall.Stop();
        Array.Sort(elapsed);
        return new(arm, round, iterations, iterations / wall.Elapsed.TotalSeconds,
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.95) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.99) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / iterations);
    }

    private static bool Refines()
    {
        var requirement = new SemanticRequirementV1<IsolationClassV1>(1,
            SemanticRequirementStrengthV1.Mandatory, IsolationClassV1.DomainSeparated);
        var guarantee = new SemanticGuaranteeV1<IsolationClassV1>(1,
            SemanticGuaranteeSupportV1.Supported, IsolationClassV1.ConfidentialDomain);
        SemanticRefinementDecisionV1 decision = SemanticRefinementEvaluatorV1.Evaluate(requirement,
            guarantee, SemanticPartialOrdersV1.IsolationRefines);
        return decision.IsSatisfied && !decision.AuthorizesExecution && !decision.AuthorizesEffect;
    }

    private static bool ProjectValidateAndCompare(ProviderTraceEventV1[] provider,
        SemanticTraceEventV1[] reference)
    {
        IReadOnlyList<SemanticTraceEventV1> projected = SemanticTraceProjectionV1.Project(provider);
        return projected.Count == reference.Length &&
            SemanticTraceValidatorV1.Validate(projected).IsValid &&
            SemanticTraceDifferentialV1.CompareAllowedProjection(reference, projected,
                ReferenceTupleDigest, CandidateTupleDigest).IsEquivalent;
    }

    private static SemanticTraceEventV1[] SemanticTrace() =>
    [
        Event(1, SemanticTraceEventKindV1.Submit),
        Event(2, SemanticTraceEventKindV1.EffectPossible),
        Event(3, SemanticTraceEventKindV1.RetireOrComplete),
        Event(4, SemanticTraceEventKindV1.Visible),
        Event(5, SemanticTraceEventKindV1.Published),
        Event(6, SemanticTraceEventKindV1.Settled),
        Event(7, SemanticTraceEventKindV1.Released),
    ];

    private static ProviderTraceEventV1[] ProviderTrace() =>
    [
        Provider(1, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Submit),
        Provider(2, ProviderTraceDispositionV1.ProviderPrivate, default),
        Provider(3, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.EffectPossible),
        Provider(4, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.RetireOrComplete),
        Provider(5, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Visible),
        Provider(6, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Published),
        Provider(7, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Settled),
        Provider(8, ProviderTraceDispositionV1.SecurityRelevant, SemanticTraceEventKindV1.Released),
    ];

    private static SemanticTraceEventV1 Event(ulong sequence, SemanticTraceEventKindV1 kind) =>
        new(1, "reference-operation", sequence, kind, "reference-provider",
            new string('d', 64), new string('e', 64));
    private static ProviderTraceEventV1 Provider(ulong sequence, ProviderTraceDispositionV1 disposition,
        SemanticTraceEventKindV1 kind) => new(1, "provider-operation", sequence, disposition, kind,
            "provider-source", new string('f', 64), new string('1', 64));

    private static Summary Summarize(IReadOnlyList<Sample> samples)
    {
        double refinement = Median("refinement-decision");
        double validate = Median("trace-validate");
        double differential = Median("trace-differential");
        double projection = Median("provider-project-validate-differential");
        return new(refinement, validate, differential, projection, SupportsGatePromotion: false,
            "Absolute host costs separate the pure refinement decision, lifecycle validation, equivalent differential comparison, and provider-private erasure plus full projection checking.");

        double Median(string arm) => Percentile(samples.Where(sample => sample.Arm == arm)
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray(), 0.5);
    }

    private static string[] Rotate(int round)
    {
        string[] arms = ["refinement-decision", "trace-validate", "trace-differential",
            "provider-project-validate-differential"];
        int offset = round % arms.Length;
        return arms.Skip(offset).Concat(arms.Take(offset)).ToArray();
    }

    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        int SemanticEvents, int ProviderEvents, string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int Round, int Operations, double ThroughputPerSecond,
        double MedianNanoseconds, double P95Nanoseconds, double P99Nanoseconds,
        double MaximumNanoseconds, double AllocatedBytesPerOperation);
    public sealed record Summary(double RefinementDecisionMedianNanoseconds,
        double TraceValidateMedianNanoseconds, double TraceDifferentialMedianNanoseconds,
        double ProviderProjectValidateDifferentialMedianNanoseconds,
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
