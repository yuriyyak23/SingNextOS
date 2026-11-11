using System.Text;
using System.Text.Json;
using SingNext.CxlLab;

try
{
    if(args.Length==1 && args[0]=="selftest") { LabCapsule.SelfTest(); Qualification.TestDumpParser(); DiscoveryCompatibility.SelfTest(); CapsuleAdmission.SelfTest(); BootInfoConsumerQualification.SelfTest(); NativePreparationSession.SelfTest(); return 0; }
    var options=new Dictionary<string,string>(StringComparer.Ordinal);
    if(args.Length<1 || (args.Length-1)%2!=0) throw new ArgumentException("Commands: selftest | create --output NEWDIR [--payload FILE] | verify --capsule FILE --public-key PEM | qualify --config FILE --output NEWDIR");
    for(int i=1;i<args.Length;i+=2) if(!options.TryAdd(args[i],args[i+1])) throw new ArgumentException("Duplicate argument: "+args[i]);
    string[] allowed=args[0] switch {
        "identity-init"=>["--output"], "bootinfo-consume"=>["--input","--output"],
        "native-mailbox"=>["--config","--output","--efi-image","--timeout-seconds","--ram-mib","--cxl-bus","--edu-count"],
        "native-hdm" or "pci-diagnostic"=>["--config","--output","--efi-image","--timeout-seconds","--ram-mib","--cxl-bus","--edu-count","--capsule-file","--expect-rejection","--native-ram","--native-ram-mode","--file-failure","--capsule-offset","--expect-mailbox-rejection"],
        "create"=>["--output","--payload","--identity","--image-generation"],"verify"=>["--capsule","--public-key","--min-image-generation"],"decode-pci"=>["--input"],
        "qualify"=>["--config","--output","--payload","--timeout-seconds","--access","--offset-mib","--efi-builder","--efi-crypto","--efi-ram-copy","--managed-hdm","--efi-pci-image","--identity","--image-generation","--min-image-generation"],
        "investigate-hdm"=>["--config","--output","--timeout-seconds","--scenarios","--access-modes","--host-bridge-hdm"],_=>[]};
    if(allowed.Length==0||options.Keys.Except(allowed,StringComparer.Ordinal).Any())throw new ArgumentException("Unknown command or option.");
    if(options.TryGetValue("--efi-crypto",out var cryptoMode) &&
       (cryptoMode is not ("true" or "false") || (cryptoMode=="true"&&!options.ContainsKey("--efi-builder"))))
        throw new ArgumentException("EFI crypto requires true/false and an explicit EFI builder when enabled.");
    if(options.TryGetValue("--timeout-seconds",out var seconds)&&(!int.TryParse(seconds,out int parsedBudget)||parsedBudget<60||parsedBudget>600))throw new ArgumentException("Timeout must be 60..600 seconds per VM.");
    if(options.TryGetValue("--efi-ram-copy",out var ramMode)&&(ramMode is not ("true" or "false")||(ramMode=="true"&&options.GetValueOrDefault("--efi-crypto")!="true")))throw new ArgumentException("RAM preparation requires explicit guest crypto.");
    if(options.TryGetValue("--native-ram",out var nativeRamMode)&&nativeRamMode is not ("true" or "false"))throw new ArgumentException("Native RAM must be true/false.");
    if(options.TryGetValue("--expect-rejection",out var rejectionMode)&&rejectionMode is not ("true" or "false"))throw new ArgumentException("Expected rejection must be true/false.");
    if(options.TryGetValue("--managed-hdm",out var managedMode)&&managedMode is not ("true" or "false"))throw new ArgumentException("Managed HDM profile must be true/false.");
    if(options.TryGetValue("--host-bridge-hdm",out var hostHdmMode)&&hostHdmMode is not ("true" or "false"))throw new ArgumentException("Host HDM profile must be true/false.");
    ulong Generation(string name)=>options.TryGetValue(name,out string? text)?ulong.TryParse(text,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var number)&&number>0?number:throw new ArgumentException("Invalid nonzero generation: "+name):1;
    if(args[0]=="bootinfo-consume") {
        string output=Path.GetFullPath(options["--output"]); if(Directory.Exists(output)||File.Exists(output)) throw new IOException("Output already exists.");
        Directory.CreateDirectory(output); var result=BootInfoConsumerQualification.Inspect(File.ReadAllBytes(options["--input"]));
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(result,LabVm.JsonOptions)); Console.WriteLine(JsonSerializer.Serialize(result,LabVm.JsonOptions));
    }
    else if(args[0]=="pci-diagnostic")Console.WriteLine(JsonSerializer.Serialize(PciDiagnostic.Run(options["--config"],options["--output"],options["--efi-image"],options.TryGetValue("--timeout-seconds",out var diagnosticBudget)?int.Parse(diagnosticBudget):180,options.TryGetValue("--ram-mib",out var diagnosticRam)?int.Parse(diagnosticRam):4096,options.TryGetValue("--cxl-bus",out var diagnosticBus)?int.Parse(diagnosticBus):12,options.TryGetValue("--edu-count",out var diagnosticEdu)?int.Parse(diagnosticEdu):0),LabVm.JsonOptions));
    else if(args[0]=="native-hdm")Console.WriteLine(JsonSerializer.Serialize(NativeHdmDiagnostic.Run(options["--config"],options["--output"],options["--efi-image"],options.TryGetValue("--timeout-seconds",out var nativeBudget)?int.Parse(nativeBudget):180,options.TryGetValue("--ram-mib",out var nativeRam)?int.Parse(nativeRam):4096,options.TryGetValue("--cxl-bus",out var nativeBus)?int.Parse(nativeBus):12,options.TryGetValue("--edu-count",out var nativeEdu)?int.Parse(nativeEdu):0,options.GetValueOrDefault("--capsule-file"),options.GetValueOrDefault("--expect-rejection","false")=="true",options.GetValueOrDefault("--native-ram","false")=="true",options.GetValueOrDefault("--native-ram-mode","--ram-copy"),options.GetValueOrDefault("--file-failure"),options.TryGetValue("--capsule-offset",out var nativeOffset)?ulong.Parse(nativeOffset,System.Globalization.CultureInfo.InvariantCulture):0,options.GetValueOrDefault("--expect-mailbox-rejection","false")=="true"),LabVm.JsonOptions));
    else if(args[0]=="native-mailbox")Console.WriteLine(JsonSerializer.Serialize(NativeMailboxDiagnostic.Run(options["--config"],options["--output"],options["--efi-image"],options.TryGetValue("--timeout-seconds",out var mbBudget)?int.Parse(mbBudget):180,options.TryGetValue("--ram-mib",out var mbRam)?int.Parse(mbRam):4096,options.TryGetValue("--cxl-bus",out var mbBus)?int.Parse(mbBus):12,options.TryGetValue("--edu-count",out var mbEdu)?int.Parse(mbEdu):0),LabVm.JsonOptions));
    else if(args[0]=="identity-init")Console.WriteLine(JsonSerializer.Serialize(DevelopmentIdentity.Create(options["--output"]),LabVm.JsonOptions));
    else if(args[0]=="create")
    {
        string output=Path.GetFullPath(options["--output"]); if(Directory.Exists(output)||File.Exists(output)) throw new IOException("Output already exists.");
        using var identity=options.TryGetValue("--identity",out string? identityPath)?DevelopmentIdentity.Load(identityPath):null;
        var bundle=LabCapsule.Create(options.TryGetValue("--payload",out var payload)?File.ReadAllBytes(payload):LabCapsule.Fixture(),identity,Generation("--image-generation"));
        using(var root=System.Security.Cryptography.ECDsa.Create()){root.ImportFromPem(bundle.PublicKey);var point=root.ExportParameters(false);byte[] trust=new byte[129];trust[0]=4;point.Q.X!.CopyTo(trust,1);point.Q.Y!.CopyTo(trust,49);System.Security.Cryptography.SHA256.HashData(root.ExportSubjectPublicKeyInfo()).CopyTo(trust,97);Directory.CreateDirectory(output);File.WriteAllBytes(Path.Combine(output,"development-trust-root.bin"),trust);}
        Directory.CreateDirectory(output); File.WriteAllBytes(Path.Combine(output,"capsule.bin"),bundle.Bytes);File.WriteAllText(Path.Combine(output,"development-public.pem"),bundle.PublicKey);
        File.WriteAllText(Path.Combine(output,"capsule.json"),JsonSerializer.Serialize(LabCapsule.Verify(bundle.Bytes,bundle.PublicKey),new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine(output);
    }
    else if(args[0]=="verify") Console.WriteLine(JsonSerializer.Serialize(LabCapsule.Verify(File.ReadAllBytes(options["--capsule"]),File.ReadAllText(options["--public-key"]),Generation("--min-image-generation")),new JsonSerializerOptions{WriteIndented=true}));
    else if(args[0]=="qualify") Console.WriteLine(JsonSerializer.Serialize(Qualification.Run(options["--config"],options["--output"],options.GetValueOrDefault("--payload"),options.TryGetValue("--timeout-seconds",out var budget)?int.Parse(budget):180,options.GetValueOrDefault("--access","MEM"),options.TryGetValue("--offset-mib",out var offset)?int.Parse(offset):0,options.GetValueOrDefault("--efi-builder"),options.GetValueOrDefault("--efi-crypto","false")=="true",options.GetValueOrDefault("--identity"),Generation("--image-generation"),Generation("--min-image-generation"),options.GetValueOrDefault("--efi-ram-copy","false")=="true",options.GetValueOrDefault("--efi-pci-image"),options.GetValueOrDefault("--managed-hdm","false")=="true"),LabVm.JsonOptions));
    else if(args[0]=="investigate-hdm") {
        var report=(Dictionary<string,object?>)HdmInvestigation.Run(options["--config"],options["--output"],options.TryGetValue("--timeout-seconds",out var hdmBudget)?int.Parse(hdmBudget):180,options.GetValueOrDefault("--scenarios"),options.GetValueOrDefault("--access-modes"),options.GetValueOrDefault("--host-bridge-hdm","false")=="true");
        Console.WriteLine(JsonSerializer.Serialize(new{Scope=report["Scope"],InvestigationCompleted=report["InvestigationCompleted"],DecoderIsolationQualified=report["DecoderIsolationQualified"],Result=Path.Combine(Path.GetFullPath(options["--output"]),"result.json")},LabVm.JsonOptions));
    }
    else if(args[0]=="decode-pci") Console.WriteLine(JsonSerializer.Serialize(DiscoveryCompatibility.Decode(File.ReadAllBytes(options["--input"]),new SingNext.Boot.Core.BootPciAddress(0,0,0,0)).Headers,LabVm.JsonOptions));
    else throw new ArgumentException("Unknown command: "+args[0]);
    return 0;
}
catch(Exception e) { Console.Error.WriteLine(e.Message); return 1; }

