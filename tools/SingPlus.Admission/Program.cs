using SingPlus.Admission;

if (args.Length == 0 || !string.Equals(args[0], "verify", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: SingPlus.Admission verify --assembly <path> --root <Type::Method> --profile <profile> --proof <path>");
    return 64;
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i + 1 < args.Length; i += 2)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal)) return 64;
    options[args[i][2..]] = args[i + 1];
}

if (!options.TryGetValue("assembly", out var assembly) || !options.TryGetValue("root", out var root) ||
    !options.TryGetValue("profile", out var profile) || !options.TryGetValue("proof", out var proofPath))
{
    Console.Error.WriteLine("Missing required verifier option.");
    return 64;
}

var result = AdmissionVerifier.Verify(assembly, root, profile);
var proofDirectory = Path.GetDirectoryName(Path.GetFullPath(proofPath));
if (!string.IsNullOrEmpty(proofDirectory)) Directory.CreateDirectory(proofDirectory);
File.WriteAllBytes(proofPath, result.Proof.SerializeCanonical(result.Violations));

if (!result.IsAdmitted)
{
    foreach (var violation in result.Violations)
        Console.Error.WriteLine($"{violation.Method}: {violation.Operation}: {violation.Detail}");
    return 2;
}

return 0;
