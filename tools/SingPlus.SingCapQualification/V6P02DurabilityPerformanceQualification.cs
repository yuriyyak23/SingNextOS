using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.SingCapQualification;

public static class V6P02DurabilityPerformanceQualification
{
    private const int DefaultIterations = 1_000;
    private const int DefaultRounds = 7;
    private static readonly PersistenceSemanticsV1 Semantics = new(1,
        V6ManagedDurableOutputModel.QualifiedProviderIdentity, "media:model:performance",
        PersistenceDomainClassV1.NamedManagedModel,
        PersistenceOrderingV1.DataThenMetadataThenCommitRecord, 7, 11, true);
    private static readonly Func<ulong> ProviderGeneration = static () => 7;
    private static readonly Func<ulong> MediaGeneration = static () => 11;
    private static readonly Func<PersistenceDomainClassV1> Domain =
        static () => PersistenceDomainClassV1.NamedManagedModel;
    private static readonly Func<KernelResult> Publish = static () => KernelResult.Ok();

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
        var model = new V6ManagedDurableOutputModel(Semantics);
        DurableOutputBindingV1 warmup = Binding("warmup", 1);
        RunPublicationOnly(warmup);
        RunContractAndPublication(warmup);
        RunManagedDurability(model, Binding("warmup-managed", 2));

        var samples = new List<Sample>(checked(rounds * 3));
        ulong generation = 3;
        for (int round = 0; round < rounds; round++)
        {
            DurableOutputBindingV1[] bindings = new DurableOutputBindingV1[iterations];
            for (int index = 0; index < iterations; index++)
            {
                bindings[index] = Binding($"p02:{round}:{index}", generation);
                generation++;
            }
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            string[] order = (round % 3) switch
            {
                0 => ["publication-callback-only", "contract-validation-and-publication", "managed-persist-evidence-and-publication"],
                1 => ["contract-validation-and-publication", "managed-persist-evidence-and-publication", "publication-callback-only"],
                _ => ["managed-persist-evidence-and-publication", "publication-callback-only", "contract-validation-and-publication"],
            };
            foreach (string arm in order)
            {
                samples.Add(arm switch
                {
                    "publication-callback-only" => MeasureArm(arm, round, bindings,
                        RunPublicationOnly),
                    "contract-validation-and-publication" => MeasureArm(arm, round, bindings,
                        RunContractAndPublication),
                    _ => MeasureArm(arm, round, bindings,
                        binding => RunManagedDurability(model, binding)),
                });
            }
        }

        Summary summary = Summarize(samples);
        return new("singnext.v6.p02-durability-performance/1", DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds,
                "Canonical bindings and correlations are prepared before timing. Callback-only invokes the same successful publication callback. Contract-validation additionally validates the exact durable binding. Managed evidence validates the binding, advances the named model write/data/metadata/durable sequence, checks live provider/media/domain values, creates evidence, invokes publication, and records its sequence. Arm order rotates; forced GC occurs only between rounds.",
                "Named managed model only. Digests are precomputed and no payload hashing, byte copy, filesystem/media I/O, flush instruction, hardware barrier, power interruption, CPU affinity, isolated-host control, or product workload/SLO is measured."),
            samples, summary,
            "Software contract/evidence bookkeeping overhead only. Stopwatch data is not persistence evidence and cannot qualify ADR, eADR, CXL persistent memory, physical crash consistency, hardware, production, or gate promotion.");
    }

    private static Sample MeasureArm(string arm, int round,
        IReadOnlyList<DurableOutputBindingV1> bindings, Func<DurableOutputBindingV1, bool> operation)
    {
        var elapsed = new long[bindings.Count];
        long allocated = 0;
        var wall = Stopwatch.StartNew();
        for (int index = 0; index < bindings.Count; index++)
        {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            bool valid = operation(bindings[index]);
            elapsed[index] = Stopwatch.GetTimestamp() - started;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            if (!valid) throw new InvalidOperationException("P02 measured operation violated its durability invariant.");
        }
        wall.Stop();
        Array.Sort(elapsed);
        return new(arm, round, bindings.Count, bindings.Count / wall.Elapsed.TotalSeconds,
            ToNanoseconds(elapsed[(int)Math.Ceiling(bindings.Count * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(bindings.Count * 0.95) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(bindings.Count * 0.99) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / bindings.Count);
    }

    private static bool RunPublicationOnly(DurableOutputBindingV1 _) => Publish().IsSuccess;

    private static bool RunContractAndPublication(DurableOutputBindingV1 binding)
    {
        binding.Validate();
        return Publish().IsSuccess;
    }

    private static bool RunManagedDurability(V6ManagedDurableOutputModel model,
        DurableOutputBindingV1 binding)
    {
        KernelResult<ProviderPersistEvidenceV1> result = model.PersistAndPublish(binding,
            3, 5, 7, ProviderGeneration, MediaGeneration, Domain, Publish);
        return result.IsSuccess && result.Value is { } evidence &&
            evidence.Assurance == PersistEvidenceAssuranceV1.ModelOnly &&
            evidence.DurableSequence < evidence.PublishedSequence &&
            PersistEvidenceMatcherV1.Match(binding, evidence) == PersistEvidenceMatchCodeV1.Exact;
    }

    private static DurableOutputBindingV1 Binding(string correlation, ulong generation) =>
        new(1, correlation, generation, new string('a', 64), new string('b', 64), 17, Semantics);

    private static Summary Summarize(IReadOnlyList<Sample> samples)
    {
        double callback = Median("publication-callback-only");
        double contract = Median("contract-validation-and-publication");
        double managed = Median("managed-persist-evidence-and-publication");
        return new(callback, contract, managed, PercentDelta(callback, contract),
            PercentDelta(contract, managed), SupportsGatePromotion: false,
            "Contract delta isolates canonical validation; managed delta adds model sequencing, live tuple checks, evidence construction/matching, and publication recording. It excludes all physical persistence work.");

        double Median(string arm) => Percentile(samples.Where(sample => sample.Arm == arm)
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray(), 0.5);
    }

    private static double PercentDelta(double baseline, double candidate) =>
        baseline == 0 ? 0 : (candidate - baseline) / baseline * 100;
    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int Round, int Operations, double ThroughputPerSecond,
        double MedianNanoseconds, double P95Nanoseconds, double P99Nanoseconds,
        double MaximumNanoseconds, double AllocatedBytesPerOperation);
    public sealed record Summary(double PublicationCallbackMedianNanoseconds,
        double ContractValidationMedianNanoseconds, double ManagedPersistEvidenceMedianNanoseconds,
        double ContractValidationOverCallbackPercent, double ManagedEvidenceOverContractPercent,
        bool SupportsGatePromotion, string Interpretation);
    public sealed record Report(string Schema, DateTimeOffset CapturedUtc, EnvironmentTuple Environment,
        Methodology Methodology, IReadOnlyList<Sample> Samples, Summary Summary,
        string ClaimBoundary);

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
        if (iterations is < 100 or > 20_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        if (rounds is < 5 or > 25) throw new ArgumentOutOfRangeException(nameof(rounds));
        return new(Path.GetFullPath(output), iterations, rounds);
    }
}
