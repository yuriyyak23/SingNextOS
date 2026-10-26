using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SingNext.CxlLab;

internal sealed class LabVm : IDisposable
{
    private readonly Process process;
    private readonly TcpClient qmp, serial;
    private readonly TcpClient? qtest;
    private readonly bool useQtest;
    private readonly StreamReader qr;
    private readonly StreamReader? tr;
    private readonly StreamWriter qw;
    private readonly StreamWriter? tw;
    private readonly NetworkStream ss;
    private readonly StreamWriter raw,plain,qmpLog;
    private readonly Stopwatch lifetime=Stopwatch.StartNew();
    private readonly int timeout;
    private int requestId;
    public ulong ObservedResetSequence {get;private set;}=1;
    public string DirectoryPath {get;}
    public LabVm(JsonElement config,string directory,IReadOnlyDictionary<string,byte[]> diskFiles,int timeoutSeconds,bool useQtest=false,int cxlMiB=256,IReadOnlyList<string>? traceEvents=null,int ramMiB=4096,int cxlBusNumber=12,int eduCount=0,bool hostBridgeHdm=false)
    {
        this.useQtest=useQtest;
        if(ramMiB<4096||ramMiB>8192||cxlBusNumber<8||cxlBusNumber>240||eduCount<0||eduCount>8)throw new ArgumentException("Diagnostic topology outside bounded lab profile.");
        DirectoryPath=directory;timeout=timeoutSeconds;Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory,"logs"));Directory.CreateDirectory(Path.Combine(directory,"nvram"));
        string code=config.GetProperty("Firmware").GetProperty("Code").GetString()!,vars=config.GetProperty("Firmware").GetProperty("VarsTemplate").GetString()!;
        File.Copy(vars,Path.Combine(directory,"nvram","uefi-vars.fd"),false);FatImage.Create(Path.Combine(directory,"guest-fat.img"),diskFiles);
        int qp=FreePort(),tp=FreePort(),sp=FreePort();
        string[] args=["-machine","q35,cxl=on,cxl-fmw.0.targets.0=cxl.1,cxl-fmw.0.size=4096M","-accel","tcg","-m",$"{ramMiB}M","-smp","4",
            "-object",$"memory-backend-ram,id=cxlmem,size={cxlMiB}M","-object","memory-backend-ram,id=cxllsa,size=1M",
            "-device",$"pxb-cxl,bus_nr={cxlBusNumber},bus=pcie.0,id=cxl.1{(hostBridgeHdm?",hdm_for_passthrough=on":"")}","-device","cxl-rp,port=0,bus=cxl.1,id=cxl-rp0,chassis=0,slot=2",
            "-device","cxl-type3,bus=cxl-rp0,persistent-memdev=cxlmem,lsa=cxllsa,id=cxl0,sn=0x1",
            "-drive",$"if=pflash,format=raw,unit=0,readonly=on,file={code.Replace(",",",,")}","-drive","if=pflash,format=raw,unit=1,file=nvram/uefi-vars.fd",
            "-drive","file=guest-fat.img,format=raw,if=ide","-net","none","-display","none","-monitor","none",
            "-qmp",$"tcp:127.0.0.1:{qp},server=on,wait=off","-serial",$"tcp:127.0.0.1:{sp},server=on,wait=off",
            "-qtest",$"tcp:127.0.0.1:{tp},server=on,wait=off","-qtest-log","logs/qtest.log","-name","SingNextOS-CXL-GuestQualification"];
        for(int n=0;n<eduCount;n++)args=[..args,"-device",$"edu,bus=pcie.0,id=lab-edu{n}"];
        if(!useQtest) {
            var filtered=new List<string>();for(int i=0;i<args.Length;i++){if(args[i] is "-qtest" or "-qtest-log"){i++;continue;}filtered.Add(args[i]);}args=filtered.ToArray();
        }
        if(traceEvents is {Count:>0}) {
            File.WriteAllLines(Path.Combine(directory,"trace-events.txt"),traceEvents);
            args=[..args,"-trace","events=trace-events.txt,file=logs/hdm-trace.log"];
        }
        string exe=config.GetProperty("QemuPath").GetString()!;
        var info=new ProcessStartInfo(exe){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};foreach(string a in args)info.ArgumentList.Add(a);
        process=Process.Start(info)??throw new IOException("QEMU failed to start.");
        var stderr=process.StandardError.ReadToEndAsync();var stdout=process.StandardOutput.ReadToEndAsync();
        process.Exited+=(_,_)=>{};
        File.WriteAllText(Path.Combine(directory,"logs","launch.json"),JsonSerializer.Serialize(new{Executable=exe,Arguments=args,Pid=process.Id,StartTime=process.StartTime},JsonOptions));
        try
        {
            qmp=Connect(qp);if(useQtest){qtest=Connect(tp);tr=new(qtest.GetStream());tw=new(qtest.GetStream()){AutoFlush=true,NewLine="\n"};}serial=Connect(sp);qr=new(qmp.GetStream());qw=new(qmp.GetStream()){AutoFlush=true};ss=serial.GetStream();
            raw=new(Path.Combine(directory,"logs","serial.raw.txt"));plain=new(Path.Combine(directory,"logs","serial.txt"));qmpLog=new(Path.Combine(directory,"logs","qmp.jsonl"));
            string greeting=qr.ReadLine()??throw new IOException("No QMP greeting.");File.WriteAllText(Path.Combine(directory,"logs","qmp-greeting.json"),greeting);
            Qmp("qmp_capabilities");ReadSerial(x=>x.Contains("CXL_LAB_READY")&&x.LastIndexOf("Shell>",StringComparison.Ordinal)>x.LastIndexOf("CXL_LAB_READY",StringComparison.Ordinal),45);
            File.WriteAllText(Path.Combine(directory,"logs","info-pci.txt"),Hmp("info pci"));
            File.WriteAllText(Path.Combine(directory,"logs","query-pci.json"),Qmp("query-pci").GetRawText());
        }
        catch { if(!process.HasExited){process.Kill();process.WaitForExit(5000);}File.WriteAllText(Path.Combine(directory,"logs","stderr.txt"),stderr.GetAwaiter().GetResult());throw; }
        // Output draining is independent of how often the controller polls QMP.
        Stderr=stderr;Stdout=stdout;
    }
    private Task<string> Stderr {get;}
    private Task<string> Stdout {get;}
    public static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    static int FreePort(){using var l=new TcpListener(IPAddress.Loopback,0);l.Start();return ((IPEndPoint)l.LocalEndpoint).Port;}
    TcpClient Connect(int port)
    {
        for(int i=0;i<100;i++)
        {
            CheckDeadline();if(process.HasExited)throw new IOException("QEMU exited during startup.");
            var c=new TcpClient();try{c.ConnectAsync(IPAddress.Loopback,port).Wait(TimeSpan.FromSeconds(1));if(c.Connected){c.NoDelay=true;c.GetStream().ReadTimeout=5000;c.GetStream().WriteTimeout=5000;return c;}}catch(AggregateException){}c.Dispose();Thread.Sleep(50);
        }
        throw new TimeoutException("QEMU socket unavailable.");
    }
    public void CheckDeadline(){if(lifetime.Elapsed.TotalSeconds>timeout)throw new TimeoutException("VM qualification time budget exceeded.");}
    public JsonElement Qmp(string command,object? arguments=null)
    {
        CheckDeadline();int id=++requestId;string request=JsonSerializer.Serialize(new{execute=command,arguments=arguments??new{},id});qmpLog.WriteLine(request);qmpLog.Flush();qw.WriteLine(request);
        for(int n=0;n<100;n++)
        {
            string line=qr.ReadLine()??throw new IOException("QMP disconnected.");qmpLog.WriteLine(line);qmpLog.Flush();using var doc=JsonDocument.Parse(line);var r=doc.RootElement;
            if(r.TryGetProperty("event",out var ev) && ev.GetString() is "RESET" or "SHUTDOWN")
                ObservedResetSequence=checked(ObservedResetSequence+1);
            if(!r.TryGetProperty("id",out var rid)||rid.GetInt32()!=id)continue;
            if(r.TryGetProperty("error",out var error))throw new IOException(error.ToString());return r.GetProperty("return").Clone();
        }
        throw new IOException("Excessive QMP events.");
    }
    public string Hmp(string command)=>Qmp("human-monitor-command",new Dictionary<string,string>{{"command-line",command}}).GetString()!;
    string Qtest(string cmd)
    {
        CheckDeadline();if(tw is null || tr is null)throw new InvalidOperationException("QTest disabled for this VM.");tw.WriteLine(cmd);
        for(int n=0;n<100;n++){string line=tr.ReadLine()??throw new IOException("QTest disconnected.");if(line.StartsWith("IRQ "))continue;if(!line.StartsWith("OK"))throw new IOException("QTest: "+line);return line[2..].Trim();}
        throw new IOException("Excessive QTest IRQ messages.");
    }
    public uint Read32(ulong address)
    {
        if(useQtest)return uint.Parse(Qtest($"readl 0x{address:x}")[2..],NumberStyles.HexNumber);
        var match=Regex.Match(Hmp($"xp /1wx 0x{address:x}"),@":\s*0x([0-9a-fA-F]+)");
        if(!match.Success)throw new IOException("HMP physical register read failed.");return uint.Parse(match.Groups[1].Value,NumberStyles.HexNumber);
    }
    public byte[] ReadBytes(ulong address,int length)
    {
        if(!useQtest){string file=$"logs/physical-read-{++requestId}.bin";Qmp("pmemsave",new{val=address,size=length,filename=file});byte[] bytes=File.ReadAllBytes(Path.Combine(DirectoryPath,file));if(bytes.Length!=length)throw new IOException("Short QMP physical read.");return bytes;}
        byte[] b=new byte[length];for(int i=0;i<length;i+=1024){int n=Math.Min(1024,length-i);byte[] part=Convert.FromBase64String(Qtest($"b64read 0x{address+(uint)i:x} {n}"));if(part.Length!=n)throw new IOException("QTest short read.");part.CopyTo(b,i);}return b;
    }
    public uint PciRead(byte bus,byte dev,byte fn,byte offset)
    {
        if(!useQtest){ulong a2=((ulong)bus<<24)|((ulong)dev<<16)|((ulong)fn<<8)|offset;string output=Shell($"mm {a2:x} -w 4 -PCI -n");var match=Regex.Match(output,@"PCI\s+0x[0-9a-fA-F]+\s*:\s*0x([0-9a-fA-F]+)");if(!match.Success)throw new IOException("Guest PCI read failed.");return uint.Parse(match.Groups[1].Value,NumberStyles.HexNumber);}
        uint a=0x80000000u|((uint)bus<<16)|((uint)dev<<11)|((uint)fn<<8)|offset;Qtest($"outl 0xcf8 0x{a:x}");return uint.Parse(Qtest("inl 0xcfc")[2..],NumberStyles.HexNumber);
    }
    static readonly Regex Ansi=new("\\x1b\\[[0-?]*[ -/]*[@-~]",RegexOptions.Compiled);
    string ReadSerial(Func<string,bool> done,int seconds)
    {
        var b=new StringBuilder();byte[] bytes=new byte[16384];var timer=Stopwatch.StartNew();ss.ReadTimeout=200;
        while(timer.Elapsed.TotalSeconds<seconds)
        {
            CheckDeadline();int n;try{n=ss.Read(bytes);}catch(IOException e) when(e.InnerException is SocketException se && se.SocketErrorCode==SocketError.TimedOut){continue;}
            if(n==0)throw new IOException("Guest console disconnected.");string chunk=Encoding.ASCII.GetString(bytes,0,n);raw.Write(chunk);raw.Flush();b.Append(chunk);
            string text=Ansi.Replace(b.ToString(),"").Replace("\r","");if(done(text)){plain.Write(text);plain.Flush();return text;}
        }
        throw new TimeoutException("Guest command timeout; inspect serial.raw.txt. Tail: "+b.ToString()[Math.Max(0,b.Length-200)..]);
    }
    public string Shell(string command,int seconds=15,string? completionMarker=null)
    {
        byte[] bytes=Encoding.ASCII.GetBytes(command+"\r");ss.Write(bytes);ss.Flush();return ReadSerial(x=>x.Contains("Shell>") && (completionMarker is null || (x.Contains(completionMarker) && x.LastIndexOf("Shell>",StringComparison.Ordinal)>x.LastIndexOf(completionMarker,StringComparison.Ordinal))),seconds);
    }
    public string BeginShellUntil(string command,string marker,int seconds=45)
    {
        byte[] bytes=Encoding.ASCII.GetBytes(command+"\r");ss.Write(bytes);ss.Flush();
        return ReadSerial(x=>(x.Contains(marker)&&x.IndexOf('\n',x.IndexOf(marker,StringComparison.Ordinal))>=0)||x.Contains("Shell>"),seconds);
    }
    public string ContinueShell(string input,int seconds=45)
    {
        byte[] bytes=Encoding.ASCII.GetBytes(input);ss.Write(bytes);ss.Flush();
        return ReadSerial(x=>x.Contains("Shell>"),seconds);
    }
    public string WarmResetToShell()
    {
        Qmp("query-status");ulong before=ObservedResetSequence;
        Qmp("system_reset");
        string text=ReadSerial(x=>x.Contains("CXL_LAB_READY")&&x.LastIndexOf("Shell>",StringComparison.Ordinal)>x.LastIndexOf("CXL_LAB_READY",StringComparison.Ordinal),60);
        Qmp("query-status");
        if(ObservedResetSequence<=before)throw new InvalidDataException("Fresh firmware prompt arrived without observed QMP reset event.");
        return text;
    }
    public void Dispose()
    {
        try{if(!process.HasExited){try{Qmp("quit");process.WaitForExit(5000);}catch(Exception){}if(!process.HasExited){process.Kill();process.WaitForExit(5000);}}}
        finally
        {
            qmp?.Dispose();qtest?.Dispose();serial?.Dispose();raw?.Dispose();plain?.Dispose();qmpLog?.Dispose();
            File.WriteAllText(Path.Combine(DirectoryPath,"logs","stderr.txt"),Stderr.GetAwaiter().GetResult());File.WriteAllText(Path.Combine(DirectoryPath,"logs","stdout.txt"),Stdout.GetAwaiter().GetResult());process.Dispose();
        }
    }
}


