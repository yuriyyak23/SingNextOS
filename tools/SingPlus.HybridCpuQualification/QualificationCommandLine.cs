namespace SingPlus.HybridCpuQualification;

internal static class QualificationCommandLine
{
    private const string Verb = "record-external-blocked";

    private static readonly string[] RequiredOptions =
    [
        "sing-repository",
        "hybridcpu-repository",
        "expected-hybridcpu-revision",
        "dotnet-sdk-version",
        "kernel-assembly",
        "boot-assembly",
        "admission-proof",
        "first-pass-kernel-assembly",
        "first-pass-boot-assembly",
        "first-pass-admission-proof",
        "output"
    ];

    internal static int Run(string[] args)
    {
        try
        {
            var options = Parse(args);
            if (!string.Equals(
                    options["expected-hybridcpu-revision"],
                    QualificationRecorder.AuditedHybridCpuRevision,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandLineException(
                    $"This recorder is scoped to audited HybridCPU revision '{QualificationRecorder.AuditedHybridCpuRevision}'.");
            }

            var inputs = new QualificationInputs(
                options["sing-repository"],
                options["hybridcpu-repository"],
                options["expected-hybridcpu-revision"],
                options["dotnet-sdk-version"],
                options["kernel-assembly"],
                options["boot-assembly"],
                options["admission-proof"],
                options["first-pass-kernel-assembly"],
                options["first-pass-boot-assembly"],
                options["first-pass-admission-proof"]);

            var outputPath = QualificationRecorder.ResolveCanonicalReportPath(
                options["sing-repository"],
                options["output"]);
            var report = QualificationRecorder.RecordExternalBlocked(inputs);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
            File.WriteAllBytes(outputPath, report);
            return 0;
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Usage());
            return 64;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or QualificationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], Verb, StringComparison.Ordinal))
            throw new CommandLineException($"Expected command '{Verb}'.");

        if ((args.Length - 1) % 2 != 0)
            throw new CommandLineException("Every option requires exactly one value.");

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            var option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) || option.Length == 2)
                throw new CommandLineException($"Invalid option '{option}'.");

            var name = option[2..];
            if (!RequiredOptions.Contains(name, StringComparer.Ordinal))
                throw new CommandLineException($"Unknown option '--{name}'.");
            if (!options.TryAdd(name, args[index + 1]))
                throw new CommandLineException($"Option '--{name}' was provided more than once.");
        }

        foreach (var option in RequiredOptions)
        {
            if (!options.TryGetValue(option, out var value) || string.IsNullOrWhiteSpace(value))
                throw new CommandLineException($"Missing required option '--{option}'.");
        }

        return options;
    }

    private static string Usage() =>
        "Usage: SingPlus.HybridCpuQualification record-external-blocked " +
        "--sing-repository <path> --hybridcpu-repository <path> " +
        "--expected-hybridcpu-revision <40-hex> --dotnet-sdk-version <version> " +
        "--kernel-assembly <path> --boot-assembly <path> " +
        "--admission-proof <path> --first-pass-kernel-assembly <path> " +
        "--first-pass-boot-assembly <path> --first-pass-admission-proof <path> " +
        "--output <path>";

    private sealed class CommandLineException(string message) : Exception(message);
}
