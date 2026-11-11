using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SingPlus.Apps.Snake;

namespace SingPlus.Apps.Snake.Host;

// Windows host containment: low integrity, disabled privileges, explicit handle
// inheritance, one-process Job, committed-memory/CPU limits and kill-on-close.
// This is not AppContainer: reads/network access are not comprehensively denied.
internal sealed class RestrictedSnakeProcess : IDisposable
{
    private readonly SafeFileHandle _process;
    private readonly SafeFileHandle _job;
    private readonly BinaryReader _reader;
    private readonly BinaryWriter _writer;
    private long _sequence;
    private bool _disposed;
    public int ProcessId { get; }
    public bool IsAlive => !_disposed && WaitForSingleObject(_process, 0) == 258;
    public const ulong MemoryLimit = 512UL * 1024 * 1024;

    private RestrictedSnakeProcess(SafeFileHandle process, SafeFileHandle job, SafeFileHandle read, SafeFileHandle write, int pid)
    {
        _process = process;
        _job = job;
        ProcessId = pid;
        _reader = new BinaryReader(new FileStream(read, FileAccess.Read));
        _writer = new BinaryWriter(new FileStream(write, FileAccess.Write));
    }

    public static RestrictedSnakeProcess Start()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Restricted Snake requires Windows.");
        string child = Path.Combine(AppContext.BaseDirectory, "snake-child", "Snake.Child.dll");
        if (!File.Exists(child)) throw new FileNotFoundException("Build output for restricted Snake child is missing.", child);
        DirectoryInfo runtimeRoot = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar))!.Parent!.Parent!;
        string dotnet = Path.Combine(runtimeRoot.FullName, "dotnet.exe");
        if (!File.Exists(dotnet)) throw new FileNotFoundException("Trusted dotnet host is missing.", dotnet);
        SafeFileHandle? parentRead = null, parentWrite = null, childRead = null, childWrite = null, process = null, thread = null, job = null;
        IntPtr attributes = IntPtr.Zero, handleList = IntPtr.Zero, sid = IntPtr.Zero, environment = IntPtr.Zero;
        RestrictedSnakeProcess? result = null;
        bool started = false;
        bool attributesInitialized = false;
        try
        {
            SecurityAttributes sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
            Check(CreatePipe(out childRead, out parentWrite, ref sa, 0), "create request pipe");
            Check(CreatePipe(out parentRead, out childWrite, ref sa, 0), "create response pipe");
            Check(SetHandleInformation(parentRead, 1, 0), "clear parent read inheritance");
            Check(SetHandleInformation(parentWrite, 1, 0), "clear parent write inheritance");
            Check(OpenProcessToken(GetCurrentProcess(), 0x008B, out SafeFileHandle? original), "open host token");
            using (original)
            {
                Check(CreateRestrictedToken(original, 0x5, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out SafeFileHandle? token), "create restricted token");
                using (token)
                {
                    Check(ConvertStringSidToSid("S-1-16-4096", out sid), "create low-integrity SID");
                    TokenMandatoryLabel label = new TokenMandatoryLabel { Sid = sid, Attributes = 0x20 };
                    Check(SetTokenInformation(token, 25, ref label, Marshal.SizeOf<TokenMandatoryLabel>() + GetLengthSid(sid)), "set low integrity");
                    job = CreateJobObject(IntPtr.Zero, null);
                    Check(!job.IsInvalid, "create Job Object");
                    JobExtendedLimits limits = new JobExtendedLimits
                    {
                        Basic = new JobBasicLimits { Flags = 0x2000 | 0x8 | 0x100, ActiveProcessLimit = 1 },
                        ProcessMemoryLimit = (UIntPtr)MemoryLimit,
                    };
                    Check(SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobExtendedLimits>()), "set job lifetime/process/memory limits");
                    JobCpuRate cpu = new JobCpuRate { Flags = 1 | 4, Rate = 2000 };
                    Check(SetInformationJobObject(job, 15, ref cpu, Marshal.SizeOf<JobCpuRate>()), "set job CPU cap");
                    JobUiLimits ui = new JobUiLimits { Flags = 0xFF };
                    Check(SetInformationJobObject(job, 4, ref ui, Marshal.SizeOf<JobUiLimits>()), "set job UI restrictions");
                    IntPtr size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    attributes = Marshal.AllocHGlobal(size);
                    Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size), "initialize inherited handle list");
                    attributesInitialized = true;
                    handleList = Marshal.AllocHGlobal(IntPtr.Size * 2);
                    Marshal.WriteIntPtr(handleList, 0, childRead.DangerousGetHandle());
                    Marshal.WriteIntPtr(handleList, IntPtr.Size, childWrite.DangerousGetHandle());
                    Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handleList, (IntPtr)(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero), "restrict inherited handles");
                    StartupInfoEx start = new StartupInfoEx
                    {
                        Startup = new StartupInfo
                        {
                            Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                            Input = childRead.DangerousGetHandle(), Output = childWrite.DangerousGetHandle(), Error = childWrite.DangerousGetHandle(),
                        },
                        Attributes = attributes,
                    };
                    environment = Marshal.StringToHGlobalUni("DOTNET_EnableDiagnostics=0\0SystemRoot=" + Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\0\0");
                    StringBuilder command = new StringBuilder($"\"{dotnet}\" \"{child}\"");
                    // Suspended until the Job and token are verified. No unrestricted fallback.
                    Check(CreateProcessAsUser(token, dotnet, command, IntPtr.Zero, IntPtr.Zero, true,
                        0x4 | 0x08000000 | 0x00080000 | 0x400, environment, Path.GetDirectoryName(child), ref start, out ProcessInfo info), "launch restricted child suspended");
                    process = new SafeFileHandle(info.Process, true);
                    thread = new SafeFileHandle(info.Thread, true);
                    Check(AssignProcessToJobObject(job, process), "assign child to Job");
                    Check(OpenProcessToken(process.DangerousGetHandle(), 8, out SafeFileHandle? actual), "inspect child token");
                    using (actual) VerifyRestrictedToken(actual);
                    Check(ResumeThread(thread) != uint.MaxValue, "resume restricted child");
                    childRead.Dispose(); childRead = null;
                    childWrite.Dispose(); childWrite = null;
                    result = new(process, job, parentRead, parentWrite, checked((int)info.ProcessId));
                    process = null; job = null; parentRead = null; parentWrite = null;
                    result.Handshake();
                    started = true;
                    return result;
                }
            }
        }
        finally
        {
            if (!started)
            {
                result?.Dispose();
                if (process is not null && !process.IsInvalid) Check(TerminateProcess(process, 1), "terminate failed startup");
            }
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (attributes != IntPtr.Zero) { if (attributesInitialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (sid != IntPtr.Zero) LocalFree(sid);
            thread?.Dispose(); process?.Dispose(); job?.Dispose();
            parentRead?.Dispose(); parentWrite?.Dispose(); childRead?.Dispose(); childWrite?.Dispose();
        }
    }

    private void Handshake()
    {
        Task<(int, int)> read = Task.Run(() => (_reader.ReadInt32(), _reader.ReadInt32()));
        (int magic, int pid) = read.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        if (magic != 0x534E4B31 || pid != ProcessId) throw new InvalidOperationException("Restricted child handshake mismatch.");
    }

    public async Task<SnakeFrame> StepAsync(SnakeMove move, CancellationToken cancellation)
    {
        if (_disposed) throw new InvalidOperationException("Restricted child is closed.");
        long sequence = ++_sequence;
        Task<SnakeFrame> operation = Task.Run(() =>
        {
            _writer.Write(sequence); _writer.Write(move.Direction); _writer.Flush();
            long reply = _reader.ReadInt64();
            SnakeFrame frame = new SnakeFrame(_reader.ReadUInt64(), _reader.ReadUInt64(), _reader.ReadUInt64(),
                _reader.ReadInt32(), _reader.ReadInt32(), _reader.ReadInt32(), _reader.ReadInt32());
            if (reply != sequence || frame.Head is < 0 or >= 160 || frame.Food is < -1 or >= 160 ||
                frame.Score is < 0 or > 157 || frame.Status is < 0 or > 2 ||
                (frame.Cells2 >> 32) != 0)
                throw new InvalidOperationException("Restricted child response failed bounded validation.");
            return frame;
        });
        try { return await operation.WaitAsync(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false); }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            Check(TerminateProcess(_process, 1), "terminate interrupted child");
            try { await operation.ConfigureAwait(false); }
            catch (IOException) { /* Expected pipe closure; preserve cancellation/timeout. */ }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Closing the Job kills the process even if the child ignores its IPC.
        _job.Dispose();
        if (WaitForSingleObject(_process, 5000) != 0)
            throw new InvalidOperationException("Restricted child termination was not confirmed; retain runtime storage.");
        _disposed = true;
        _reader.Dispose(); _writer.Dispose(); _process.Dispose();
    }

    private static void VerifyRestrictedToken(SafeFileHandle token)
    {
        GetTokenInformation(token, 25, IntPtr.Zero, 0, out int length);
        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            Check(GetTokenInformation(token, 25, buffer, length, out _), "query child integrity");
            TokenMandatoryLabel label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
            byte count = Marshal.ReadByte(GetSidSubAuthorityCount(label.Sid));
            int integrity = Marshal.ReadInt32(GetSidSubAuthority(label.Sid, (uint)(count - 1)));
            if (integrity != 4096) throw new InvalidOperationException("Child is not low integrity.");
            VerifyDisabledPrivileges(token);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void VerifyDisabledPrivileges(SafeFileHandle token)
    {
        GetTokenInformation(token, 3, IntPtr.Zero, 0, out int length);
        nint buffer = Marshal.AllocHGlobal(length);
        try
        {
            Check(GetTokenInformation(token, 3, buffer, length, out _), "query child privileges");
            int count = Marshal.ReadInt32(buffer);
            for (int index = 0; index < count; index++)
            {
                int offset = 4 + index * 12;
                int low = Marshal.ReadInt32(buffer, offset);
                int high = Marshal.ReadInt32(buffer, offset + 4);
                int flags = Marshal.ReadInt32(buffer, offset + 8);
                // DISABLE_MAX_PRIVILEGE preserves SeChangeNotifyPrivilege (LUID 23).
                if ((flags & 2) != 0 && (low != 23 || high != 0))
                    throw new InvalidOperationException("Child retains an unexpected enabled privilege.");
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void Check(bool success, string action)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), action);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenMandatoryLabel { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct JobBasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobExtendedLimits
    {
        public JobBasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct JobCpuRate { public uint Flags, Rate; }
    [StructLayout(LayoutKind.Sequential)] private struct JobUiLimits { public uint Flags; }

    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool CreateRestrictedToken(SafeFileHandle token, uint flags, uint disabled, IntPtr sids, uint deleted, IntPtr privileges, uint restricted, IntPtr restrictedSids, out SafeFileHandle result);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool ConvertStringSidToSid(string text, out IntPtr sid);
    [DllImport("advapi32.dll")] private static extern int GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool SetTokenInformation(SafeFileHandle token, int kind, ref TokenMandatoryLabel information, int length);
    [DllImport("advapi32.dll", SetLastError=true)] private static extern bool GetTokenInformation(SafeFileHandle token, int kind, IntPtr information, int length, out int needed);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobExtendedLimits information, int length);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobCpuRate information, int length);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobUiLimits information, int length);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("advapi32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool CreateProcessAsUser(SafeFileHandle token, string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInfo information);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
}