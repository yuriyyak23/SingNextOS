using System.Diagnostics;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.SingCapQualification;

public static class V6P12EnergyPerformanceQualification
{
    private const int DefaultIterations = 1_000;
    private const int DefaultRounds = 7;
    private const ulong ReservedMicrojoules = 100;
    private const ulong SettledMicrojoules = 61;

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
        Scenario ledger = CreateScenario();
        Scenario measurement = CreateScenario();
        Scenario enforcement = CreateScenario();
        var measurementProvider = new V6ManagedEnergyCounterProvider();
        var capProvider = new V6ManagedEnergyCapProvider();

        RunLedger(ledger);
        RunMeasurement(measurement, measurementProvider, "warmup:measurement");
        RunEnforced(enforcement, capProvider, "warmup:enforced");

        var samples = new List<Sample>(checked(rounds * 3));
        for (int round = 0; round < rounds; round++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
            string[] order = (round % 3) switch
            {
                0 => ["ledger-only", "measurement-evidence", "managed-cap-evidence"],
                1 => ["measurement-evidence", "managed-cap-evidence", "ledger-only"],
                _ => ["managed-cap-evidence", "ledger-only", "measurement-evidence"],
            };
            foreach (string arm in order)
            {
                samples.Add(arm switch
                {
                    "ledger-only" => MeasureArm(arm, round, iterations,
                        index => RunLedger(ledger)),
                    "measurement-evidence" => MeasureArm(arm, round, iterations,
                        index => RunMeasurement(measurement, measurementProvider,
                            $"p12:m:{round}:{index}")),
                    _ => MeasureArm(arm, round, iterations,
                        index => RunEnforced(enforcement, capProvider,
                            $"p12:e:{round}:{index}")),
                });
            }
        }

        Summary[] summaries = [
            Summarize("measurement-evidence", samples),
            Summarize("managed-cap-evidence", samples),
        ];
        return new("singnext.v6.p12-energy-performance/1", DateTimeOffset.UtcNow,
            new(Environment.Version.ToString(), Environment.OSVersion.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                Environment.ProcessorCount, Stopwatch.Frequency, "Release JIT host"),
            new(iterations, rounds, ReservedMicrojoules, SettledMicrojoules,
                "Every timed operation executes reserve, bind, consumption admission, and exact settlement against one pre-created budget hierarchy. Evidence arms additionally create and validate provider evidence. Correlation construction, hierarchy/provider construction, forced GC between rounds, and post-operation invariants are outside the timer. Arm order rotates by round.",
                "Managed model providers only; elapsed time and managed-thread allocations are measured. No hardware energy counter, physical power cap, energy attribution, CPU affinity, isolated host, GC-pause subtraction, workload/SLO, or physical-energy inference."),
            samples, summaries,
            "Software accounting/evidence overhead characterization only. Stopwatch data is not energy or power evidence, does not establish a performance benefit, and does not support enabling V6-ENERGY-BUDGETS.");
    }

    private static Sample MeasureArm(string arm, int round, int iterations, Action<int> operation)
    {
        var elapsed = new long[iterations];
        long allocated = 0;
        var wall = Stopwatch.StartNew();
        for (int index = 0; index < iterations; index++)
        {
            long allocationStart = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            operation(index);
            elapsed[index] = Stopwatch.GetTimestamp() - started;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        }
        wall.Stop();
        Array.Sort(elapsed);
        return new(arm, round, iterations, iterations / wall.Elapsed.TotalSeconds,
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.50) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.95) - 1]),
            ToNanoseconds(elapsed[(int)Math.Ceiling(iterations * 0.99) - 1]),
            ToNanoseconds(elapsed[^1]), (double)allocated / iterations);
    }

    private static void RunLedger(Scenario scenario)
    {
        BudgetReservationHandle lease = ReserveAndBind(scenario);
        RequireValue(scenario.Authority.BeginConsumption(scenario.Process, lease));
        BudgetReservationSnapshot settled = RequireValue(scenario.Authority.SettleLease(
            scenario.Process, lease, [Amount(SettledMicrojoules)]));
        ValidateSettlement(settled);
    }

    private static void RunMeasurement(Scenario scenario, V6ManagedEnergyCounterProvider provider,
        string correlation)
    {
        BudgetReservationHandle lease = ReserveAndBind(scenario);
        V6EnergyOperationBinding operation = RequireValue(V6EnergyOperationSettlement.Begin(
            scenario.Authority, scenario.Process, lease, correlation, ReservedMicrojoules, provider));
        V6EnergySettlementReceipt receipt = RequireValue(V6EnergyOperationSettlement.CompleteAndSettle(
            scenario.Authority, scenario.Process, operation, provider, SettledMicrojoules,
            ThermalStateV1.Nominal, DvfsStateV1.Dynamic));
        if (receipt.Evidence.Claim != EnergyEvidenceClaimV1.MeasurementOnly)
            throw new InvalidOperationException("P12 measurement arm emitted the wrong evidence claim.");
        ValidateSettlement(receipt.Settlement);
    }

    private static void RunEnforced(Scenario scenario, V6ManagedEnergyCapProvider provider,
        string correlation)
    {
        BudgetReservationHandle lease = ReserveAndBind(scenario);
        V6EnergyOperationBinding operation = RequireValue(V6EnergyOperationSettlement.BeginEnforced(
            scenario.Authority, scenario.Process, lease, correlation, ReservedMicrojoules, provider));
        V6EnergySettlementReceipt receipt = RequireValue(
            V6EnergyOperationSettlement.CompleteAndSettleEnforced(scenario.Authority,
                scenario.Process, operation, provider, SettledMicrojoules,
                ThermalStateV1.Nominal, DvfsStateV1.Dynamic));
        if (receipt.Evidence.Claim != EnergyEvidenceClaimV1.EnforcedUpperBound ||
            EnergyEvidenceMatcherV1.Match(receipt.Binding, receipt.Evidence) != EnergyEvidenceMatchCodeV1.Exact)
            throw new InvalidOperationException("P12 cap arm emitted non-exact managed evidence.");
        ValidateSettlement(receipt.Settlement);
    }

    private static BudgetReservationHandle ReserveAndBind(Scenario scenario)
    {
        BudgetReservationSnapshot reserved = RequireValue(scenario.Authority.Reserve(scenario.Process,
            [Amount(ReservedMicrojoules)], BudgetReservationLifetime.ExternalEffect,
            AdmissionQosHint.None));
        return RequireValue(scenario.Authority.BindLease(scenario.Process, reserved.Reservation)).Reservation;
    }

    private static Scenario CreateScenario()
    {
        const ulong limit = 10_000_000;
        var authority = new ResourceBudgetAuthority();
        RequireValue(authority.ConfigureSystem([Amount(limit)]));
        BudgetAccountHandle service = RequireValue(authority.CreateChild(authority.SystemBudget,
            BudgetAccountLevel.Service, "p12-performance-service", [Amount(limit)])).Account;
        BudgetAccountHandle account = RequireValue(authority.CreateChild(service,
            BudgetAccountLevel.ProcessDomain, "p12-performance-process", [Amount(limit)])).Account;
        var process = new ProcessHandle(new(912_000), 1);
        Require(authority.AttachProcess(process, account));
        return new(authority, process);
    }

    private static void ValidateSettlement(BudgetReservationSnapshot settled)
    {
        if (settled.State != BudgetReservationState.Released ||
            settled.ChargedAmounts is not { Count: 1 } charged ||
            charged[0] != Amount(SettledMicrojoules))
            throw new InvalidOperationException("P12 measured operation violated exact settlement invariants.");
    }

    private static Summary Summarize(string arm, IReadOnlyList<Sample> samples)
    {
        double[] baseline = samples.Where(static sample => sample.Arm == "ledger-only")
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray();
        double[] candidate = samples.Where(sample => sample.Arm == arm)
            .Select(static sample => sample.MedianNanoseconds).Order().ToArray();
        double baselineMedian = Percentile(baseline, 0.5);
        double candidateMedian = Percentile(candidate, 0.5);
        double overhead = baselineMedian == 0 ? 0 :
            (candidateMedian - baselineMedian) / baselineMedian * 100;
        return new(arm, baselineMedian, candidateMedian, overhead,
            SupportsGatePromotion: false,
            "Delta versus the same ledger lifecycle without provider evidence; host-specific managed-software cost only.");
    }

    private static BudgetAmount Amount(ulong amount) =>
        new(ServiceBudgetDimension.EnergyMicrojoules, amount);
    private static void Require(KernelResult result)
    {
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    }
    private static T RequireValue<T>(KernelResult<T> result)
    {
        if (!result.IsSuccess || result.Value is null) throw new InvalidOperationException(result.Message);
        return result.Value;
    }
    private static double Percentile(double[] ordered, double percentile) =>
        ordered[(int)Math.Ceiling(percentile * ordered.Length) - 1];
    private static double ToNanoseconds(long ticks) => ticks * (1_000_000_000d / Stopwatch.Frequency);

    private sealed record Options(string OutputPath, int Iterations, int Rounds);
    private sealed record Scenario(ResourceBudgetAuthority Authority, ProcessHandle Process);
    public sealed record EnvironmentTuple(string Runtime, string OperatingSystem, string RuntimeIdentifier,
        int LogicalProcessorCount, long StopwatchFrequency, string ExecutionMode);
    public sealed record Methodology(int IterationsPerArmPerRound, int Rounds,
        ulong ReservedMicrojoules, ulong SettledMicrojoules, string TimingBoundary, string Limitations);
    public sealed record Sample(string Arm, int Round, int Operations, double ThroughputPerSecond,
        double MedianNanoseconds, double P95Nanoseconds, double P99Nanoseconds,
        double MaximumNanoseconds, double AllocatedBytesPerOperation);
    public sealed record Summary(string Arm, double LedgerOnlyMedianNanoseconds,
        double EvidenceArmMedianNanoseconds, double MedianOverheadPercent,
        bool SupportsGatePromotion, string Interpretation);
    public sealed record Report(string Schema, DateTimeOffset CapturedUtc, EnvironmentTuple Environment,
        Methodology Methodology, IReadOnlyList<Sample> Samples, IReadOnlyList<Summary> Summaries,
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
