using System.Security.Cryptography;
using SingPlus.Admission;
using SingPlus.Kernel;

namespace SingPlus.Boot;

/// <summary>Static proof generation only; never grants runtime authority or executes images.</summary>
public static class SipStaticVerifier
{
    private const long MaximumImageBytes = 64 * 1024 * 1024;

    public static KernelBootStageResult Verify(string path, string root, string? expectedDigest,
        out AdmissionVerificationResult? result)
    {
        result = null;
        try
        {
            path = Path.GetFullPath(path);
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length is <= 0 or > MaximumImageBytes)
                return KernelBootStageResult.Failure($"Verification image must be readable and 1..{MaximumImageBytes} bytes.");
            // Hold the root image against ordinary writers while the path-based verifier reads it.
            using FileStream image = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (image.Length is <= 0 or > MaximumImageBytes)
                return KernelBootStageResult.Failure("Verification image changed to an invalid length.");
            string digest = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            if (expectedDigest is not null && !string.Equals(digest, expectedDigest, StringComparison.Ordinal))
                return KernelBootStageResult.Failure("SIP image changed before static verification.");
            result = AdmissionVerifier.Verify(path, root, "ManagedCap");
            if (!string.Equals(result.Proof.AssemblyDigest, digest, StringComparison.Ordinal))
                return KernelBootStageResult.Failure("Static verification proof does not match the SIP image bytes.");
            EmulatorConsole.Info($"[admission] ManagedCap static {(result.IsAdmitted ? "PASS" : "REFUSED")}: root={root}, methods={result.Proof.ReachableMethodCount}, violations={result.Violations.Count}\r\n");
            EmulatorConsole.Detail($"[admission] image={digest}\r\n[admission] proof={result.Proof.ProofDigest}\r\n[admission] dependencies={result.Proof.DependencyDigest}\r\n[admission] ruleset={result.Proof.RulesetDigest}\r\n");
            foreach (AdmissionViolation? violation in result.Violations.Take(20))
                EmulatorConsole.Error($"[admission] {violation.Method}: {violation.Operation}: {violation.Detail}\r\n");
            if (result.Violations.Count > 20)
                EmulatorConsole.Error($"[admission] {result.Violations.Count - 20} additional violations omitted\r\n");
            return result.IsAdmitted
                ? KernelBootStageResult.Success()
                : KernelBootStageResult.Failure("ManagedCap static verification refused the image; runtime admission was not attempted.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            BadImageFormatException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return KernelBootStageResult.Failure($"Static verification failed: {exception.Message}");
        }
    }
}
