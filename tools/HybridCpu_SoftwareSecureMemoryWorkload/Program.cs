using SingPlus.Diagnostics;
try
{
    string[] selected = args.Where(value => !value.StartsWith('-')).ToArray();
    if (selected.Length != 0)
    {
        foreach (string scenario in selected)
        {
            if (!KernelSoftwareMemoryOracle.Scenarios.Contains(scenario)) throw new ArgumentException("Unknown Kernel memory scenario.");
            KernelSoftwareMemoryOracle.Run(scenario);
        }
        Console.WriteLine($"PASS selected kernel memory schedules={selected.Length}"); return 0;
    }
    foreach (string scenario in SoftwareSecureMemoryOracle.Scenarios) SoftwareSecureMemoryOracle.Run(scenario);
    foreach (string scenario in PlatformSoftwareMemoryOracle.Scenarios) PlatformSoftwareMemoryOracle.Run(scenario);
    foreach (string scenario in BridgeSoftwareMemoryOracle.Scenarios) BridgeSoftwareMemoryOracle.Run(scenario);
    foreach (string scenario in KernelSoftwareMemoryOracle.Scenarios) KernelSoftwareMemoryOracle.Run(scenario);
    Console.WriteLine($"PASS memory schedules={SoftwareSecureMemoryOracle.Scenarios.Length+PlatformSoftwareMemoryOracle.Scenarios.Length+BridgeSoftwareMemoryOracle.Scenarios.Length+KernelSoftwareMemoryOracle.Scenarios.Length} neutral={SoftwareSecureMemoryOracle.Scenarios.Length} platform={PlatformSoftwareMemoryOracle.Scenarios.Length} bridge={BridgeSoftwareMemoryOracle.Scenarios.Length} kernel={KernelSoftwareMemoryOracle.Scenarios.Length}"); return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
