using System.Diagnostics;
using SingPlus.Admission;

if (args.Length == 3 && string.Equals(args[0], "inspect-trace", StringComparison.Ordinal) &&
    string.Equals(args[1], "--archive", StringComparison.Ordinal))
{
    try
    {
        TelemetryZstdArchive archive = TelemetryZstdStorage.Deserialize(File.ReadAllBytes(args[2]));
        byte[] raw = TelemetryZstdStorage.Unpack(archive);
        Console.WriteLine($"RawSha256={archive.RawDigest} RawLength={raw.Length}");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
    {
        Console.Error.WriteLine($"Trace archive rejected: {ex.Message}");
        return 2;
    }
}

if (args.Length == 0 || !string.Equals(args[0], "verify", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: SingPlus.Admission verify --assembly <path> --root <Type::Method> --profile <profile> --proof <path> [--telemetry <ndjson-path> [--telemetry-zstd <archive-path>]]");
    return 64;
}

Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.Ordinal);
if ((args.Length - 1) % 2 != 0) return 64;
for (int i = 1; i + 1 < args.Length; i += 2)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal)) return 64;
    options[args[i][2..]] = args[i + 1];
}

if (!options.TryGetValue("assembly", out string? assembly) || !options.TryGetValue("root", out string? root) ||
    !options.TryGetValue("profile", out string? profile) || !options.TryGetValue("proof", out string? proofPath))
{
    Console.Error.WriteLine("Missing required verifier option.");
    return 64;
}
if (options.ContainsKey("telemetry-zstd") && !options.ContainsKey("telemetry"))
{
    Console.Error.WriteLine("--telemetry-zstd requires --telemetry.");
    return 64;
}

// Observation destinations must not overwrite canonical evidence or local importer inputs.
if (options.TryGetValue("telemetry", out string? observationPath))
{
    try
    {
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string inputPath = Path.GetFullPath(assembly);
        HashSet<string> protectedPaths = new HashSet<string>(comparer) { inputPath, Path.GetFullPath(proofPath) };
        string inputDirectory = Path.GetDirectoryName(inputPath)!;
        if (Directory.Exists(inputDirectory))
            foreach (string dependency in Directory.EnumerateFiles(inputDirectory, "*.dll"))
                protectedPaths.Add(Path.GetFullPath(dependency));
        string observationFullPath = Path.GetFullPath(observationPath);
        if (protectedPaths.Contains(observationFullPath) ||
            (options.TryGetValue("telemetry-zstd", out string? compressedPath) &&
             (protectedPaths.Contains(Path.GetFullPath(compressedPath)) || comparer.Equals(observationFullPath, Path.GetFullPath(compressedPath)))))
            throw new ArgumentException("Observation destination conflicts with a canonical artifact or importer input.");
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
        Console.Error.WriteLine($"Telemetry output failed ({ex.GetType().Name}); conflicting or invalid observation destinations are disabled.");
        options.Remove("telemetry");
        options.Remove("telemetry-zstd");
    }
}

AdmissionVerificationResult result = AdmissionVerifier.Verify(assembly, root, profile);
string? proofDirectory = Path.GetDirectoryName(Path.GetFullPath(proofPath));
if (!string.IsNullOrEmpty(proofDirectory)) Directory.CreateDirectory(proofDirectory);
File.WriteAllBytes(proofPath, result.Proof.SerializeCanonical(result.Violations));
if (options.TryGetValue("telemetry", out string? telemetryPath))
{
    try
    {
        using Activity activity = AdmissionTelemetry.StartObservation();
        string? directory = Path.GetDirectoryName(Path.GetFullPath(telemetryPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        // Exclusive creation cannot truncate an existing artifact through a file alias.
        // A reused destination is an observation failure, never an admission failure.
        await using (FileStream output = new FileStream(telemetryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await AdmissionTelemetry.WriteNdjsonAsync(output, AdmissionTelemetry.Observe(result));
        if (options.TryGetValue("telemetry-zstd", out string? archivePath))
        {
            TelemetryZstdArchive archive = TelemetryZstdStorage.Pack(File.ReadAllBytes(telemetryPath));
            string? archiveDirectory = Path.GetDirectoryName(Path.GetFullPath(archivePath));
            if (!string.IsNullOrEmpty(archiveDirectory)) Directory.CreateDirectory(archiveDirectory);
            await using FileStream archiveOutput = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await archiveOutput.WriteAsync(TelemetryZstdStorage.Serialize(archive));
        }
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
    {
        // Observation failure cannot replace canonical admission diagnostics or its exit disposition.
        Console.Error.WriteLine($"Telemetry output failed ({ex.GetType().Name}); canonical admission result is preserved.");
    }
}

if (!result.IsAdmitted)
{
    foreach (AdmissionViolation violation in result.Violations)
        Console.Error.WriteLine($"{violation.Method}: {violation.Operation}: {violation.Detail}");
    return 2;
}

return 0;
