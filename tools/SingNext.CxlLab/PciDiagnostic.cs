using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SingNext.CxlLab;

internal static class PciDiagnostic
{
    public static object Run(string configPath,string output,string efiImage,int timeout,int ramMiB=4096,int busNumber=12,int eduCount=0)
    {
        output=Path.GetFullPath(output);if(Directory.Exists(output)||File.Exists(output))throw new IOException("Diagnostic output must be new.");
        using var doc=JsonDocument.Parse(File.ReadAllText(configPath));var config=doc.RootElement;
        byte[] image=File.ReadAllBytes(efiImage);Directory.CreateDirectory(output);File.Copy(configPath,Path.Combine(output,"source-config.json"));
        InputEvidence.Record(output,configPath,config);
        string buildMetadata=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(efiImage))!,"build.json");
        if(File.Exists(buildMetadata))File.Copy(buildMetadata,Path.Combine(output,"efi-build.json"));
        var result=new Dictionary<string,object?>{{"Scope","ReadOnlyNativePciAcpiDiagnostic"},{"Passed",false},{"ActualRamMiB",ramMiB},{"ActualCxlBus",busNumber},{"EduDeviceCount",eduCount},{"QtestAttached",false},{"GuestCxlMemoryAccessible",null},{"GuestDmaIsolationQualified",false},{"RuntimeKernelExecuted",false},{"BootHandoffPerformed",false},{"EfiSha256",Convert.ToHexString(SHA256.HashData(image))}};
        result["EfiImagePath"]=Path.GetFullPath(efiImage);result["ActualMemoryBackend"]="memory-backend-ram";result["ActualAccelerator"]="tcg";
        try
        {
            var files=new Dictionary<string,byte[]>{{"startup.nsh",Encoding.ASCII.GetBytes("echo -off\r\necho CXL_LAB_READY\r\n")},{"CXLDISC.EFI",image}};
            using(var vm=new LabVm(config,Path.Combine(output,"vm"),files,timeout,ramMiB:ramMiB,cxlBusNumber:busNumber,eduCount:eduCount))
            {
                var d=Qualification.Discover(vm);File.WriteAllText(Path.Combine(output,"discovery.json"),JsonSerializer.Serialize(d,LabVm.JsonOptions));
                result["ConfigurationValid"]=true;result["QemuDeviceCreated"]=true;
                NativePciQualification.Run(vm,output,d,result);
            }
            result["Passed"]=true;return result;
        }
        catch(Exception e){result["Error"]=e.Message;throw;}
        finally{File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(result,LabVm.JsonOptions));}
    }
}
