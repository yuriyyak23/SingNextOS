using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SingNext.Boot.Capsule;
using SingNext.Boot.Core;
using SingPlus.Platform.HybridCpu.Boot;
using YAKSys_Hybrid_CPU.Boot.Contracts;

namespace SingNext.CxlLab;

internal static class InputEvidence
{
    public static void Record(string output,string configPath,JsonElement config)
    {
        string[] paths=[configPath,config.GetProperty("QemuPath").GetString()!,
            config.GetProperty("Firmware").GetProperty("Code").GetString()!,config.GetProperty("Firmware").GetProperty("VarsTemplate").GetString()!,
            Assembly.GetExecutingAssembly().Location,typeof(CxlPciDvsecParser).Assembly.Location,
            typeof(BootManifestVerifier).Assembly.Location,typeof(DirectBootCapsule).Assembly.Location,typeof(BootAbiV1).Assembly.Location];
        var evidence=paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path=> {
            var info=new FileInfo(Path.GetFullPath(path));using var stream=File.OpenRead(info.FullName);
            return new{Path=info.FullName,Bytes=info.Length,Sha256=Convert.ToHexString(SHA256.HashData(stream))};
        }).ToArray();
        File.WriteAllText(Path.Combine(output,"inputs.json"),JsonSerializer.Serialize(evidence,LabVm.JsonOptions));
    }
}
