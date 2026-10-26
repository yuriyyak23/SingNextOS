using SingPlus.Diagnostics;
try
{
    foreach (var schedule in SoftwareSecureArtifactOracle.Schedules) SoftwareSecureArtifactOracle.Run(schedule);
    foreach (var schedule in KernelArtifactScenarioOracle.Schedules) KernelArtifactScenarioOracle.Run(schedule);
    Console.WriteLine($"PASS artifact schedules={SoftwareSecureArtifactOracle.Schedules.Length+KernelArtifactScenarioOracle.Schedules.Length} neutral={SoftwareSecureArtifactOracle.Schedules.Length} kernel={KernelArtifactScenarioOracle.Schedules.Length}");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }
