namespace SingPlus.Boot.Shell;

public static partial class EmulatorCommands
{
    private static bool VerifySip(EmulatorSession session, string[] args)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
        {
            EmulatorConsole.Error("[emulator] usage: verify-sip <imagePath> <Type::Method>\r\n");
            return false;
        }
        var result = SipStaticVerifier.Verify(args[0], args[1], expectedDigest: null, out _);
        if (result.IsSuccess) return true;
        EmulatorConsole.Error($"[admission] {result.Error}\r\n");
        return false;
    }
}
