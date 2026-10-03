using System.Security.Cryptography;
using HybridCPU.Compiler.Core.Target.Runtime;
using SingPlus.Runtime;

namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool VerifyGuestArtifact(EmulatorSession session, string[] args)
    {
        if (args.Length != 1) return Fail(KernelError.InvalidTransition, "usage: verify-guest <HCEXE path>");
        byte[] package;
        try
        {
            using var image = new FileStream(args[0], FileMode.Open, FileAccess.Read, FileShare.Read);
            if (image.Length is <= 0 or > 65536)
                return Fail(KernelError.InvalidMessage, "Guest artifact must contain 1..65536 bytes.");
            package = new byte[checked((int)image.Length)];
            image.ReadExactly(package);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return Fail(KernelError.InvalidMessage, $"Guest artifact read failed: {exception.Message}"); }
        var inspected = new HybridCpuRestrictedImageBuilderV1().Inspect(package);
        EmulatorConsole.Info($"[guest] status={inspected.Status}; bytes={package.Length}; sha256={Convert.ToHexStringLower(SHA256.HashData(package))}\r\n");
        foreach (var diagnostic in inspected.Diagnostics.Take(100))
            EmulatorConsole.Info($"[guest] {diagnostic.Code}: {diagnostic.Message}\r\n");
        if (inspected.Diagnostics.Count > 100)
            EmulatorConsole.Detail($"[guest] {inspected.Diagnostics.Count - 100} diagnostics omitted\r\n");
        EmulatorConsole.Info("[guest] format/startup inspection grants no runtime execution, mapping or publication authority\r\n");
        return inspected.StartupContractSatisfied;
    }
}
