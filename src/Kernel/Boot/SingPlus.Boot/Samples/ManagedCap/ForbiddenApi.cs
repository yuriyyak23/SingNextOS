namespace SingPlus.Boot.Samples;

// Opt-in negative IDE/build example. No suppression or execution.
public static class ForbiddenApi
{
    public static System.Guid AmbientIdentity() => System.Guid.NewGuid();
    public static string ReadAmbientFile() => System.IO.File.ReadAllText("ambient.txt");
}
