using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SingPlus.Contracts;
using SingPlus.Runtime;

namespace SingPlus.SingCapQualification;

public static class VNextControlledTopologyQualification
{
    public static int Run(string[] args)
    {
        try
        {
            int iterations = 10_000;
            string? output = null;
            for (int index = 0; index < args.Length; index++)
            {
                if (args[index] == "--iterations" && index + 1 < args.Length)
                    iterations = int.Parse(args[++index], System.Globalization.CultureInfo.InvariantCulture);
                else if (args[index] == "--output" && index + 1 < args.Length)
                    output = args[++index];
                else throw new ArgumentException($"Unknown controlled-topology argument '{args[index]}'.");
            }
            if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));

            VNextControlledTopologyReport report = Measure(iterations);
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            if (output is not null)
            {
                string path = Path.GetFullPath(output);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
            Console.WriteLine(json);
            return report.Qualified ? 0 : 3;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    public static VNextControlledTopologyReport Measure(int iterations)
    {
        if (!OperatingSystem.IsWindows())
            return Unavailable("Windows processor-topology and affinity APIs are unavailable.");
        ulong[] cores = ReadCoreMasks();
        ulong sibling = cores.FirstOrDefault(static mask => BitOperations(mask) >= 2);
        ulong[] separate = cores.Where(static mask => mask != 0).Take(2).ToArray();
        if (sibling == 0 || separate.Length < 2)
            return Unavailable("The active processor group does not expose both an SMT sibling pair and two physical cores.", cores);

        int[] siblingBits = Bits(sibling).Take(2).ToArray();
        int[] separateBits = new[] { Bits(separate[0]).First(), Bits(separate[1]).First() };
        VNextTopologyMeasurement[] samples = new[]
        {
            MeasurePair("same-core-smt-siblings", siblingBits, iterations),
            MeasurePair("separate-physical-cores", separateBits, iterations),
        };
        return new VNextControlledTopologyReport(
            "SingNextOS.VNext.ControlledTopology.v1", DateTimeOffset.UtcNow,
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(), Environment.ProcessorCount,
            cores.Select(mask => $"0x{mask:X}").ToArray(), samples, true,
            "AccountingOnly controlled-topology contention evidence for ResourceBudgetAuthority on this exact host/JIT profile.",
            ["No provider-count, HybridCPU, NativeAOT, deadline, EnforcedUpperBound, GuaranteedReservation or ProductionQualified claim."]);
    }

    private static VNextTopologyMeasurement MeasurePair(string scenario, int[] logicalProcessors, int iterations)
    {
        ResourceBudgetAuthority authority = new ResourceBudgetAuthority();
        BudgetAmount amount = new BudgetAmount(ServiceBudgetDimension.ComputeTimeNanoseconds, 1);
        ulong limit = checked((ulong)iterations * 2 + 16);
        if (!authority.ConfigureSystem([new(amount.Dimension, limit)]).IsSuccess)
            throw new InvalidOperationException("Failed to configure topology qualification budget.");
        BudgetAccountHandle service = authority.CreateChild(authority.SystemBudget, BudgetAccountLevel.Service,
            "topology", [new(amount.Dimension, limit)]).Value!.Account;
        BudgetAccountHandle processAccount = authority.CreateChild(service, BudgetAccountLevel.ProcessDomain,
            "topology-process", [new(amount.Dimension, limit)]).Value!.Account;
        ProcessHandle process = new ProcessHandle(new ProcessId(991), 1);
        if (!authority.AttachProcess(process, processAccount).IsSuccess)
            throw new InvalidOperationException("Failed to attach topology qualification process.");

        using CountdownEvent ready = new CountdownEvent(2);
        using ManualResetEventSlim start = new ManualResetEventSlim(false);
        int[] errors = new int[2];
        Thread[] threads = logicalProcessors.Select((processor, worker) => new Thread(() =>
        {
            nuint prior = SetThreadAffinityMask(GetCurrentThread(), new UIntPtr(1UL << processor));
            if (prior == UIntPtr.Zero)
            {
                errors[worker] = Marshal.GetLastPInvokeError();
                ready.Signal();
                return;
            }
            try
            {
                ready.Signal();
                start.Wait();
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    KernelResult<BudgetReservationSnapshot> reserved = authority.Reserve(process, [amount], BudgetReservationLifetime.LocalResource,
                        AdmissionQosHint.None);
                    if (!reserved.IsSuccess || !authority.Release(process, reserved.Value!.Reservation).IsSuccess)
                    {
                        errors[worker] = -1;
                        return;
                    }
                }
            }
            finally { _ = SetThreadAffinityMask(GetCurrentThread(), prior); }
        }) { IsBackground = true, Name = $"vnext-topology-{scenario}-{worker}" }).ToArray();
        foreach (Thread? thread in threads) thread.Start();
        ready.Wait();
        Stopwatch timer = Stopwatch.StartNew();
        start.Set();
        foreach (Thread? thread in threads) thread.Join();
        timer.Stop();
        if (errors.Any(static error => error != 0))
            throw new InvalidOperationException($"Thread affinity or budget operation failed: {string.Join(',', errors)}.");
        ulong used = authority.Query(processAccount).Value!.Usage.Single(usage => usage.Dimension == amount.Dimension).Used;
        if (used != 0) throw new InvalidOperationException("Topology measurement leaked reserved capacity.");
        return new VNextTopologyMeasurement(scenario, logicalProcessors, iterations,
            checked(iterations * 2), timer.Elapsed.TotalMilliseconds,
            iterations * 2 / timer.Elapsed.TotalSeconds, used, errors);
    }

    private static ulong[] ReadCoreMasks()
    {
        uint length = 0;
        _ = GetLogicalProcessorInformation(IntPtr.Zero, ref length);
        int error = Marshal.GetLastPInvokeError();
        if (length == 0 || error != 122)
            throw new InvalidOperationException($"Processor topology size query failed with Win32 error {error}.");
        nint buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformation(buffer, ref length))
                throw new InvalidOperationException($"Processor topology query failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            int entrySize = IntPtr.Size == 8 ? 32 : 24;
            if (length % entrySize != 0)
                throw new InvalidOperationException("Processor topology response has an unexpected shape.");
            List<ulong> masks = new List<ulong>();
            for (int offset = 0; offset < length; offset += entrySize)
            {
                int relationship = Marshal.ReadInt32(buffer, offset + IntPtr.Size);
                if (relationship != 0) continue; // RelationProcessorCore
                ulong mask = IntPtr.Size == 8
                    ? unchecked((ulong)Marshal.ReadInt64(buffer, offset))
                    : unchecked((uint)Marshal.ReadInt32(buffer, offset));
                if (mask != 0) masks.Add(mask);
            }
            return masks.Order().ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IEnumerable<int> Bits(ulong mask)
    {
        for (int bit = 0; bit < 64; bit++)
            if ((mask & (1UL << bit)) != 0) yield return bit;
    }

    private static int BitOperations(ulong value) => System.Numerics.BitOperations.PopCount(value);

    private static VNextControlledTopologyReport Unavailable(string reason, ulong[]? cores = null) =>
        new("SingNextOS.VNext.ControlledTopology.v1", DateTimeOffset.UtcNow,
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(), Environment.ProcessorCount,
            (cores ?? []).Select(mask => $"0x{mask:X}").ToArray(), [], false,
            "Unavailable", [reason, "No topology claim was promoted."]);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformation(IntPtr buffer, ref uint returnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr affinityMask);
}

public sealed record VNextControlledTopologyReport(
    string Schema, DateTimeOffset CapturedUtc, string OperatingSystem, string Architecture,
    string Runtime, int ActiveLogicalProcessorCount, IReadOnlyList<string> PhysicalCoreMasks,
    IReadOnlyList<VNextTopologyMeasurement> Measurements, bool Qualified, string Claim,
    IReadOnlyList<string> Exclusions);

public sealed record VNextTopologyMeasurement(
    string Scenario, IReadOnlyList<int> LogicalProcessors, int IterationsPerWorker,
    int TotalOperations, double ElapsedMilliseconds, double OperationsPerSecond,
    ulong FinalUsedAmount, IReadOnlyList<int> WorkerErrors);
