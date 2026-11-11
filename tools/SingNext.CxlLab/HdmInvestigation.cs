using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SingNext.CxlLab;

internal static class HdmInvestigation
{
    const ulong Size = 256UL << 20;
    const ulong Offset = 0x00100000;
    const ulong Original = 0x619b32d4e7a058cf, Attempt = 0xbfa0314965d2e807, Third = 0x35910cd27a8ef640;
    static byte[] Startup => Encoding.ASCII.GetBytes("echo -off\r\necho CXL_LAB_READY\r\n");
    public static object Run(string configPath, string output, int timeout,string? scenarioFilter=null,string? accessFilter=null,bool hostBridgeHdm=false)
    {
        output=Path.GetFullPath(output);
        if(Directory.Exists(output)||File.Exists(output)) throw new IOException("Output must be new.");
        using var document=JsonDocument.Parse(File.ReadAllText(configPath));
        var config=document.RootElement;
        foreach(var (k,v) in new[]{("CxlMiB",256),("LsaMiB",1),("WindowMiB",4096),("RamMiB",4096),("CpuCount",4)})
            if(config.GetProperty(k).GetInt32()!=v) throw new InvalidDataException("Unsupported baseline: "+k);
        Directory.CreateDirectory(output);File.Copy(configPath,Path.Combine(output,"source-config.json"));InputEvidence.Record(output,configPath,config);
        string[] trace=ProbeTrace(config.GetProperty("QemuPath").GetString()!,output);
        var cases=new List<Dictionary<string,object?>>();
        var report=new Dictionary<string,object?> {
            ["Scope"]="HdmDecoderGateInvestigation",["InvestigationCompleted"]=false,["DecoderIsolationQualified"]=false,
            ["TransactionalHdmMappingRetirementQualified"]=false,["QtestAttached"]=false,["TraceEventsEnabled"]=trace,
            ["Cases"]=cases,["GuestSignatureVerification"]=false,["RuntimeKernelExecuted"]=false
        };
        report["HostBridgeHdmForPassthroughRequested"]=hostBridgeHdm;
        string[] scenarios=["before-first-commit","committed-control","endpoint-uncommit","rootport-uncommit","both-uncommit","endpoint-global-disable","rootport-global-disable","both-global-disable","hpa-remap","dpa-remap","boundary-range"];
        if(hostBridgeHdm)scenarios=[..scenarios,"hostbridge-uncommit","hostbridge-global-disable","host-first-uncommit","leaf-first-uncommit","transactional-map-retire"];
        string[] accesses=["MEM","MMIO"];
        if(scenarioFilter is not null){var selected=scenarioFilter.Split(',');var optional=hostBridgeHdm?new[]{"warm-reset","warm-reset-host-uncommit","transactional-commit-fault","transactional-reset-fault"}:new[]{"warm-reset"};if(selected.Length==0||selected.Distinct().Count()!=selected.Length||selected.Except(scenarios.Concat(optional)).Any())throw new ArgumentException("Unknown/duplicate scenario.");scenarios=selected;}
        if(accessFilter is not null){var selected=accessFilter.Split(',');if(selected.Length==0||selected.Distinct().Count()!=selected.Length||selected.Except(accesses).Any())throw new ArgumentException("Unknown/duplicate access path.");accesses=selected;}
        report["RequestedScenarios"]=scenarios;report["RequestedAccesses"]=accesses;
        try {
            foreach(string access in accesses) foreach(string scenario in scenarios) {
                Console.WriteLine($"HDM investigation: {access} / {scenario}");
                var row=new Dictionary<string,object?>{["Scenario"]=scenario,["GuestAccess"]=access,["Completed"]=false,["BackingMiB"]=scenario=="dpa-remap"?512:256};
                cases.Add(row);
                string dir=Path.Combine(output,access.ToLowerInvariant()+"-"+scenario);
                try {
                    using var vm=new LabVm(config,dir,new Dictionary<string,byte[]>{{"startup.nsh",Startup}},timeout,cxlMiB:scenario=="dpa-remap"?512:256,traceEvents:trace,hostBridgeHdm:hostBridgeHdm);
                    var liveOptions=new{HostHdmForPassthrough=vm.Qmp("qom-get",new{path="/machine/peripheral/cxl.1",property="hdm_for_passthrough"}),EndpointHdmDb=vm.Qmp("qom-get",new{path="/machine/peripheral/cxl0",property="hdm-db"}),FixedWindows=vm.Qmp("qom-get",new{path="/machine",property="cxl-fmw"})};
                    Save(dir,"live-cxl-options.json",liveOptions);
                    if(liveOptions.HostHdmForPassthrough.GetBoolean()!=hostBridgeHdm||liveOptions.EndpointHdmDb.GetBoolean())throw new InvalidDataException("Unexpected live host HDM / endpoint HDM-DB profile.");
                    var d=Qualification.Discover(vm);Save(dir,"discovery.json",d);
                    row["HostHdmAdvertised"]=d.Host.HdmAddress is not null;
                    if((scenario.StartsWith("hostbridge-",StringComparison.Ordinal)||scenario is "host-first-uncommit" or "leaf-first-uncommit")&&d.Host.HdmAddress is null)throw new InvalidDataException("Host bridge HDM capability is not advertised; gate not exercised.");
                    ulong target=d.Hpa+Offset;row["Hpa"]=$"0x{d.Hpa:x}";
                    var snapshots=new List<object>();row["Snapshots"]=snapshots;
                    snapshots.Add(Capture(vm,d,"01-before-commit",target));
                    if(scenario is "transactional-commit-fault" or "transactional-reset-fault") {
                        bool resetFault=scenario=="transactional-reset-fault";
                        var hardware=new LabHdmHardware(vm,d);
                        var injected=new FaultInjectedHdmHardware(hardware,vm,resetFault,()=>{
                            Write(vm,target,Original,access);
                            if(Read(vm,target)!=Original)throw new InvalidDataException("Pre-fault committed witness not writable.");
                            snapshots.Add(Capture(vm,d,"02-before-injected-fault",target));
                        });
                        var mapping=new SingPlus.Platform.HybridCpu.Boot.TransactionalHdmMapping(injected);
                        var reset=new SingNext.Boot.Core.BootResetSnapshot(vm.ObservedResetSequence,vm.ObservedResetSequence);
                        var request=new SingNext.Boot.Core.BootMappingRequest(d.Hpa,Size,0,Size,Size);
                        var failed=mapping.Map(request,reset);
                        row["FaultInjection"]="Lab transport failure after actual host commit; not a QEMU hardware fault.";
                        row["Injected"]=injected.Injected;row["MapFailure"]=failed.Failure.ToString();
                        row["MappingEvidenceDenied"]=!failed.IsSuccess;
                        snapshots.Add(Capture(vm,d,"03-after-fault-compensation",target));
                        if(resetFault){
                            row["ResetSequenceBefore"]=injected.ResetBefore;row["ResetSequenceAfter"]=injected.ResetAfter;
                            row["RealResetObserved"]=injected.ResetAfter>injected.ResetBefore;
                            var now=new SingNext.Boot.Core.BootResetSnapshot(vm.ObservedResetSequence,vm.ObservedResetSequence);
                            row["QuarantinedRemapRejected"]=mapping.Map(request,now).Failure==SingNext.Boot.Core.BootFailure.Quarantined;
                            row["OldRouteReadableAfterReset"]=Read(vm,target)==Original;
                            row["GuestResetAuthorityQualified"]=false;
                        }else{
                            Write(vm,target,Attempt,access);
                            var retry=mapping.Map(request,reset);
                            row["CompensatedRetrySucceeded"]=retry.IsSuccess;
                            row["WriteBlocked"]=retry.IsSuccess&&Read(vm,target)==Original;
                            if(retry.IsSuccess)row["FinalRetireSucceeded"]=mapping.Retire(retry.Value,reset)==SingNext.Boot.Core.BootFailure.None;
                        }
                        row["HardwareOperations"]=hardware.Operations;
                    } else if(scenario=="transactional-map-retire") {
                        var hardware=new LabHdmHardware(vm,d);
                        var mapping=new SingPlus.Platform.HybridCpu.Boot.TransactionalHdmMapping(hardware);
                        var reset=new SingNext.Boot.Core.BootResetSnapshot(vm.ObservedResetSequence,vm.ObservedResetSequence);
                        var request=new SingNext.Boot.Core.BootMappingRequest(d.Hpa,Size,0,Size,Size);
                        var mapped=mapping.Map(request,reset);
                        if(!mapped.IsSuccess)throw new InvalidDataException("Managed Map: "+mapped.Failure);
                        Write(vm,target,Original,access);if(Read(vm,target)!=Original)throw new InvalidDataException("Mapped write failed.");
                        snapshots.Add(Capture(vm,d,"02-managed-map",target));
                        var retired=mapping.Retire(mapped.Value,reset);
                        if(retired!=SingNext.Boot.Core.BootFailure.None)throw new InvalidDataException("Managed Retire: "+retired);
                        snapshots.Add(Capture(vm,d,"03-managed-retire",target));
                        Write(vm,target,Attempt,access);
                        var remapped=mapping.Map(request,reset);
                        if(!remapped.IsSuccess)throw new InvalidDataException("Managed remap: "+remapped.Failure);
                        row["WriteBlocked"]=Read(vm,target)==Original;
                        row["ManagedMapRetireExecuted"]=true;
                        row["MappingGenerationAdvanced"]=remapped.Value.BootMappingGeneration>mapped.Value.BootMappingGeneration;
                        vm.WarmResetToShell();
                        var stale=mapping.Retire(remapped.Value,reset);
                        row["StaleEpochRetireFailure"]=stale.ToString();
                        row["StaleEpochQuarantined"]=stale==SingNext.Boot.Core.BootFailure.Quarantined;
                        var now=new SingNext.Boot.Core.BootResetSnapshot(vm.ObservedResetSequence,vm.ObservedResetSequence);
                        row["QuarantinedRemapRejected"]=mapping.Map(request,now).Failure==SingNext.Boot.Core.BootFailure.Quarantined;
                        row["HardwareOperations"]=hardware.Operations;
                        row["DpaReadbackScope"]="Inferred only for zero-offset whole-device single-target lab profile; not general DPA translation.";
                        snapshots.Add(Capture(vm,d,"04-quarantined-after-reset",target));
                    } else if(scenario=="before-first-commit") {
                        Write(vm,target,Attempt,access);snapshots.Add(Capture(vm,d,"02-uncommitted-write",target));
                        Map(vm,d,d.Hpa,0);ulong observed=Read(vm,target);
                        row["ObservedAfterFirstCommit"]=Hex(observed);row["WriteBlocked"]=observed==0;
                        snapshots.Add(Capture(vm,d,"03-after-first-commit",target));
                    } else {
                        Map(vm,d,d.Hpa,0);Write(vm,target,Original,access);
                        if(Read(vm,target)!=Original) throw new InvalidDataException("Committed sentinel write failed.");
                        snapshots.Add(Capture(vm,d,"02-committed",target));
                        if(scenario is "warm-reset" or "warm-reset-host-uncommit") {
                            Write(vm,target+8,Third,access);if(Read(vm,target+8)!=Third)throw new InvalidDataException("Warm reset retention witness write failed.");
                            ulong before=vm.ObservedResetSequence;string reboot=vm.WarmResetToShell();
                            File.WriteAllText(Path.Combine(dir,"logs","warm-reset-firmware.txt"),reboot);
                            row["ResetSequenceBefore"]=before;row["ResetSequenceAfter"]=vm.ObservedResetSequence;
                            var after=Qualification.Discover(vm);Save(dir,"after-reset-discovery.json",after);
                            if(after.Fingerprint!=d.Fingerprint)throw new InvalidDataException("Topology moved after warm reset; no stale-address write attempted.");
                            snapshots.Add(Capture(vm,after,"03-after-warm-reset",target,target+8));
                            if(scenario=="warm-reset-host-uncommit"){
                                row["GuestReadBeforeExplicitHostUncommit"]=Hex(GuestRead(vm,target,access));
                                if(after.Host.HdmAddress is null)throw new InvalidDataException("Host HDM absent after reset.");
                                Uncommit(vm,after.Host);row["ExplicitHostUncommitAfterReset"]=true;
                                snapshots.Add(Capture(vm,after,"03b-explicit-host-uncommit",target,target+8));
                            }
                            row["GuestReadBeforeRecommit"]=Hex(GuestRead(vm,target,access));
                            Write(vm,target,Attempt,access);Map(vm,after,after.Hpa,0);
                            ulong observed=Read(vm,target),witness=Read(vm,target+8);
                            row["ObservedAfterRecommit"]=Hex(observed);row["RetentionWitnessAfterRecommit"]=Hex(witness);
                            row["WriteBlocked"]=observed!=Attempt;row["WarmResetSentinelRetentionVerified"]=witness==Third;
                            row["QmpWarmResetObserved"]=true;row["GuestResetAuthorityQualified"]=false;
                            snapshots.Add(Capture(vm,after,"04-after-warm-reset-recommit",target,target+8));
                        } else if(scenario=="boundary-range") {
                            ulong end=d.Hpa+Size-8,outside=d.Hpa+Size;
                            Write(vm,end,Original,access);row["LastQwordWritable"]=Read(vm,end)==Original;
                            Write(vm,outside,Attempt,access);row["OutsideReadAfterAttempt"]=Hex(Read(vm,outside));
                            row["AdjacentValidDataPreserved"]=Read(vm,end)==Original&&Read(vm,target)==Original;
                            row["OutOfBackingWriteRejected"]=Read(vm,outside)!=Attempt;
                            snapshots.Add(Capture(vm,d,"03-boundary-probed",target,end,outside));
                        } else if(scenario=="hpa-remap") {
                            Uncommit(vm,d.Device);Uncommit(vm,d.Port);snapshots.Add(Capture(vm,d,"03-both-uncommitted",target));
                            ulong next=d.Hpa+Size;Map(vm,d,next,0);
                            row["NewHpa"] = Hex(next);row["OldReadBeforeAttempt"]=Hex(Read(vm,target));
                            row["NewReadBeforeAttempt"]=Hex(Read(vm,next+Offset));
                            snapshots.Add(Capture(vm,d,"04-remapped",target,next+Offset));
                            Write(vm,target,Attempt,access);ulong nextAfterOld=Read(vm,next+Offset);
                            row["NewReadAfterOldHpaWrite"]=Hex(nextAfterOld);row["OldRouteRemoved"]=nextAfterOld==Original&&Read(vm,target)!=Attempt;
                            Write(vm,next+Offset,Third,access);row["NewRouteWritable"]=Read(vm,next+Offset)==Third;
                            snapshots.Add(Capture(vm,d,"05-remap-probed",target,next+Offset));
                            Uncommit(vm,d.Device);Uncommit(vm,d.Port);Map(vm,d,d.Hpa,0);
                            row["OriginalHpaReadAfterRestore"]=Hex(Read(vm,target));
                            snapshots.Add(Capture(vm,d,"06-restored",target));
                        } else if(scenario=="dpa-remap") {
                            Uncommit(vm,d.Device);Uncommit(vm,d.Port);Map(vm,d,d.Hpa,Size);
                            row["NewDpaBase"]=Hex(Size);row["NewDpaInitiallyZero"]=Read(vm,target)==0;
                            snapshots.Add(Capture(vm,d,"03-dpa-rebound",target));
                            Write(vm,target,Attempt,access);row["NewDpaWritable"]=Read(vm,target)==Attempt;
                            Uncommit(vm,d.Device);Uncommit(vm,d.Port);Map(vm,d,d.Hpa,0);
                            row["OriginalDpaReadAfterRestore"]=Hex(Read(vm,target));row["OriginalDpaPreserved"]=Read(vm,target)==Original;
                            snapshots.Add(Capture(vm,d,"04-dpa-restored",target));
                        } else {
                            switch(scenario) {
                                case "endpoint-uncommit":Uncommit(vm,d.Device);break;
                                case "rootport-uncommit":Uncommit(vm,d.Port);break;
                                case "hostbridge-uncommit":Uncommit(vm,d.Host);break;
                                case "hostbridge-global-disable":DisableGlobal(vm,d.Host);break;
                                case "host-first-uncommit":Uncommit(vm,d.Host);Uncommit(vm,d.Port);Uncommit(vm,d.Device);row["DisableOrder"]=new[]{"HostBridge","RootPort","Endpoint"};break;
                                case "leaf-first-uncommit":Uncommit(vm,d.Device);Uncommit(vm,d.Port);Uncommit(vm,d.Host);row["DisableOrder"]=new[]{"Endpoint","RootPort","HostBridge"};break;
                                case "both-uncommit":Uncommit(vm,d.Device);Uncommit(vm,d.Port);break;
                                case "endpoint-global-disable":DisableGlobal(vm,d.Device);break;
                                case "rootport-global-disable":DisableGlobal(vm,d.Port);break;
                                case "both-global-disable":DisableGlobal(vm,d.Device);DisableGlobal(vm,d.Port);break;
                            }
                            row["GuestReadBeforeAttempt"]=Hex(GuestRead(vm,target,access));
                            snapshots.Add(Capture(vm,d,"03-gate-state",target));
                            Write(vm,target,Attempt,access);snapshots.Add(Capture(vm,d,"04-after-attempt",target));
                            row["GuestReadAfterAttempt"]=Hex(GuestRead(vm,target,access));
                            Map(vm,d,d.Hpa,0);ulong observed=Read(vm,target);
                            row["ObservedAfterRestore"]=Hex(observed);row["WriteBlocked"]=observed==Original;
                            if(scenario=="committed-control")row["ControlWriteSucceeded"]=observed==Attempt;
                            snapshots.Add(Capture(vm,d,"05-restored",target));
                        }
                    }
                    row["ExpectationMet"]=scenario switch {
                        "transactional-commit-fault"=>Equals(row.GetValueOrDefault("Injected"),true)&&Equals(row.GetValueOrDefault("MapFailure"),"PartialCommit")&&Equals(row.GetValueOrDefault("CompensatedRetrySucceeded"),true)&&Equals(row.GetValueOrDefault("WriteBlocked"),true)&&Equals(row.GetValueOrDefault("FinalRetireSucceeded"),true),
                        "transactional-reset-fault"=>Equals(row.GetValueOrDefault("Injected"),true)&&Equals(row.GetValueOrDefault("MapFailure"),"Quarantined")&&Equals(row.GetValueOrDefault("RealResetObserved"),true)&&Equals(row.GetValueOrDefault("QuarantinedRemapRejected"),true),
                        "transactional-map-retire"=>Equals(row.GetValueOrDefault("WriteBlocked"),true)&&Equals(row.GetValueOrDefault("MappingGenerationAdvanced"),true)&&Equals(row.GetValueOrDefault("StaleEpochQuarantined"),true)&&Equals(row.GetValueOrDefault("QuarantinedRemapRejected"),true),
                        "committed-control"=>Equals(row.GetValueOrDefault("ControlWriteSucceeded"),true),
                        "warm-reset" or "warm-reset-host-uncommit"=>Equals(row.GetValueOrDefault("WriteBlocked"),true)&&Equals(row.GetValueOrDefault("WarmResetSentinelRetentionVerified"),true),
                        "hpa-remap"=>Equals(row.GetValueOrDefault("OldRouteRemoved"),true)&&Equals(row.GetValueOrDefault("NewRouteWritable"),true),
                        "dpa-remap"=>Equals(row.GetValueOrDefault("NewDpaInitiallyZero"),true)&&Equals(row.GetValueOrDefault("NewDpaWritable"),true)&&Equals(row.GetValueOrDefault("OriginalDpaPreserved"),true),
                        "boundary-range"=>Equals(row.GetValueOrDefault("LastQwordWritable"),true)&&Equals(row.GetValueOrDefault("OutOfBackingWriteRejected"),true)&&Equals(row.GetValueOrDefault("AdjacentValidDataPreserved"),true),
                        _=>Equals(row.GetValueOrDefault("WriteBlocked"),true)
                    };
                    row["Completed"]=true;
                } catch(Exception e) {row["Error"]=e.Message;Console.WriteLine("Scenario failed: "+e.Message);}
                Save(output,"result.json",report);
            }
            report["InvestigationCompleted"]=cases.All(x=>Equals(x["Completed"],true));
            var aliasFindings=new List<object>();
            foreach(var row in cases.Where(x=>Equals(x["Completed"],true)&&x["Scenario"]!.ToString()!.EndsWith("uncommit",StringComparison.Ordinal))) {
                string gateStage=row["Scenario"]!.ToString()=="warm-reset-host-uncommit"?"03b-explicit-host-uncommit":"03-gate-state";
                var shots=JsonSerializer.SerializeToElement(row["Snapshots"]);var gated=shots.EnumerateArray().First(x=>x.GetProperty("Stage").GetString()==gateStage);
                aliasFindings.Add(new{Scenario=row["Scenario"],GuestAccess=row["GuestAccess"],DirectRamAliasRetained=gated.GetProperty("DirectAliases").GetArrayLength()>0,FlatRamRouteRetained=gated.GetProperty("FlatCxlLines").EnumerateArray().Any(x=>x.GetString()!.Contains("nv-ram")),WriteBlocked=row.GetValueOrDefault("WriteBlocked")});
            }
            report["DirectAliasFindings"]=aliasFindings;
            report["SourceLevelRootCauseVerified"]=false;
            report["ObservableMechanism"]="After commit, a direct RAM alias may remain in FlatView despite decoder changes. Read the per-case register/alias/data evidence; this is not a source-line attribution.";
            report["ExpectationsMet"]=cases.Count(x=>Equals(x.GetValueOrDefault("ExpectationMet"),true));
            report["DecoderIsolationQualified"]=false; // functional matrix is not a DMA/cache quiescence proof
            report["ScopeNotes"]="pmemsave observes the same QEMU physical address space, not an independent CXL controller. MEM and MMIO refer to guest Shell paths.";
            Save(output,"result.json",report);return report;
        } catch(Exception e){report["Error"]=e.Message;Save(output,"result.json",report);throw;}
    }
    static string[] ProbeTrace(string exe,string output) {
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("-trace");start.ArgumentList.Add("help");
        using var p=Process.Start(start)!;var o=p.StandardOutput.ReadToEndAsync();var e=p.StandardError.ReadToEndAsync();if(!p.WaitForExit(10000)){p.Kill();throw new TimeoutException("Trace capability probe timed out.");}
        string text=o.Result+e.Result;File.WriteAllText(Path.Combine(output,"trace-capabilities.txt"),text);
        return text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).Where(x=>Regex.IsMatch(x,@"^cxl_[a-zA-Z0-9_]*(hdm|decoder|cfmw)[a-zA-Z0-9_]*$")).Distinct().ToArray();
    }
    static string Hex(ulong value)=>$"0x{value:x16}";
    static void Save(string dir,string name,object data)=>File.WriteAllText(Path.Combine(dir,name),JsonSerializer.Serialize(data,LabVm.JsonOptions));
    internal static void WriteRegister(LabVm vm,ulong address,uint value)=>vm.Shell($"mm {address:x} {value:x} -w 4 -MMIO -n");
    internal static void Uncommit(LabVm vm,Component c) {
        if(c.HdmAddress is not ulong h)return;
        uint ctrl=vm.Read32(h+0x20);if((ctrl&0x100)!=0)throw new InvalidDataException("Locked decoder.");
        WriteRegister(vm,h+0x20,ctrl&~0xe00u);
        if((vm.Read32(h+0x20)&0x600)!=0)throw new InvalidDataException("Commit/status did not clear: "+c.Name);
    }
    static void DisableGlobal(LabVm vm,Component c) {
        if(c.HdmAddress is not ulong h)return;WriteRegister(vm,h+4,vm.Read32(h+4)&~2u);
        if((vm.Read32(h+4)&2)!=0)throw new InvalidDataException("Global enable did not clear.");
    }
    internal static void Map(LabVm vm,Discovery d,ulong hpa,ulong dpa) {
        if(hpa<d.Hpa||hpa>d.Hpa+d.WindowBytes-Size||hpa%Size!=0||dpa%Size!=0)throw new InvalidDataException("Mapping outside test window.");
        foreach(var c in new[]{d.Device,d.Port,d.Host}) if(c.HdmAddress is ulong h) {
            uint old=vm.Read32(h+0x20);if((old&0x100)!=0)throw new InvalidDataException("Locked decoder.");
            if((old&0x600)!=0)Uncommit(vm,c);
            foreach(var (off,value) in new (uint,uint)[]{(0x10,(uint)hpa),(0x14,(uint)(hpa>>32)),(0x18,(uint)Size),(0x1c,0),(0x24,c.Name=="Endpoint"?(uint)dpa:0),(0x28,c.Name=="Endpoint"?(uint)(dpa>>32):0),(4,2),(0x20,0x1200)})WriteRegister(vm,h+off,value);
            uint ctrl=vm.Read32(h+0x20);ulong actual=((ulong)vm.Read32(h+0x14)<<32)|(vm.Read32(h+0x10)&0xf0000000u);
            if((ctrl&0xc00)!=0x400||actual!=hpa||(ctrl&0xff)!=0)throw new InvalidDataException("Commit failed: "+c.Name);
        }
    }
    static void Write(LabVm vm,ulong address,ulong value,string access)=>vm.Shell($"mm {address:x} {value:x} -w 8 -{access} -n");
    static ulong Read(LabVm vm,ulong address)=>BinaryPrimitives.ReadUInt64LittleEndian(vm.ReadBytes(address,8));
    static ulong GuestRead(LabVm vm,ulong address,string access) {
        string text=vm.Shell($"mm {address:x} -w 8 -{access} -n");var m=Regex.Match(text,Regex.Escape(access)+@"\s+0x([0-9a-fA-F]+)\s*:\s*0x([0-9a-fA-F]+)");
        if(!m.Success||ulong.Parse(m.Groups[1].Value,NumberStyles.HexNumber)!=address)throw new InvalidDataException("Guest sentinel read incomplete.");return ulong.Parse(m.Groups[2].Value,NumberStyles.HexNumber);
    }
    static object Capture(LabVm vm,Discovery d,string stage,params ulong[] addresses) {
        vm.Qmp("stop");try {
            string tree=vm.Hmp("info mtree"),flat=vm.Hmp("info mtree -f");
            File.WriteAllText(Path.Combine(vm.DirectoryPath,"logs",stage+".mtree.txt"),tree);File.WriteAllText(Path.Combine(vm.DirectoryPath,"logs",stage+".flatview.txt"),flat);
            var registers=new List<object>();foreach(var c in new[]{d.Host,d.Port,d.Device}) {
                if(c.HdmAddress is not ulong h){registers.Add(new{Name=c.Name,AdvertisesHdm=false});continue;}
                registers.Add(new{Name=c.Name,AdvertisesHdm=true,Capability=Hex(vm.Read32(h)),GlobalControl=Hex(vm.Read32(h+4)),BaseLow=Hex(vm.Read32(h+0x10)),BaseHigh=Hex(vm.Read32(h+0x14)),SizeLow=Hex(vm.Read32(h+0x18)),SizeHigh=Hex(vm.Read32(h+0x1c)),Control=Hex(vm.Read32(h+0x20)),TargetsOrDpaSkipLow=Hex(vm.Read32(h+0x24)),TargetsOrDpaSkipHigh=Hex(vm.Read32(h+0x28))});
            }
            var data=addresses.Select(a=>new{Hpa=Hex(a),Value=Hex(Read(vm,a))}).ToArray();
            var state=new{Stage=stage,GuestQuiescedForSnapshot=true,Registers=registers,Memory=data,
                DirectAliases=tree.Split('\n').Where(x=>x.Contains("cxl-direct-mapping")||x.Contains("@cxlmem")).Select(x=>x.Trim()).Distinct().ToArray(),
                FlatCxlLines=flat.Split('\n').Where(x=>x.Contains("cxlmem")||x.Contains("cxl-fixed")).Select(x=>x.Trim()).ToArray(),
                MemoryTreeSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tree))),FlatViewSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(flat)))};
            Save(vm.DirectoryPath,"logs/"+stage+".json",state);return state;
        } finally {vm.Qmp("cont");}
    }
}
