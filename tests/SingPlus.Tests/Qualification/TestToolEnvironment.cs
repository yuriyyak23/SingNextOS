using System.Runtime.CompilerServices;

namespace SingPlus.Tests.Qualification;

internal static class TestToolEnvironment
{
    // VS testhost may inherit a PATH without Git even when VS ships Git itself.
    // Keep real Git-backed negative tests; change only this test process's PATH.
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (path.Split(Path.PathSeparator).Any(directory =>
            File.Exists(Path.Combine(directory.Trim('"'), "git.exe")))) return;

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        List<string> candidates = [Path.Combine(programFiles, "Git", "cmd")];
        string visualStudio = Path.Combine(programFiles, "Microsoft Visual Studio");
        if (Directory.Exists(visualStudio))
            foreach (string version in Directory.EnumerateDirectories(visualStudio))
                foreach (string edition in Directory.EnumerateDirectories(version))
                    candidates.Add(Path.Combine(edition, "Common7", "IDE", "CommonExtensions",
                        "Microsoft", "TeamFoundation", "Team Explorer", "Git", "cmd"));

        string? gitDirectory = candidates.FirstOrDefault(directory => File.Exists(Path.Combine(directory, "git.exe")));
        if (gitDirectory is not null)
            Environment.SetEnvironmentVariable("PATH", gitDirectory + Path.PathSeparator + path);
    }
}
