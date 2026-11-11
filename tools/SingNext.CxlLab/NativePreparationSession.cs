using System.Security.Cryptography;
using SingNext.Boot.Core;

namespace SingNext.CxlLab;

// Owns one host-observed native preparation attempt.  It is deliberately a lab
// owner: the local HDM/mtree observations below do not establish DMA, cache, or
// remote closure and therefore never become a boot or provider authority.
internal sealed class NativePreparationSession : IDisposable
{
    private readonly LabVm owner;
    private bool disposed;
    private bool quarantined;
    private byte[]? verifiedSnapshot;

    public NativePreparationSession(LabVm owner, Discovery discovery, ulong resetSequence)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        owner.Qmp("query-status");
        if (resetSequence == 0 || owner.ObservedResetSequence != resetSequence)
            throw new InvalidDataException("Native preparation started with a stale reset epoch.");
        Id = Guid.NewGuid();
        DiscoveryFingerprint = discovery.Fingerprint;
        ResetSequence = resetSequence;
        State = NativePreparationState.DiscoveryAdmitted;
    }

    public Guid Id { get; }
    public string DiscoveryFingerprint { get; }
    public ulong ResetSequence { get; }
    // A process-local receipt generation, not a platform mapping generation.
    public ulong MappingReceiptGeneration { get; private set; }
    public ulong? OwnedRamAddress { get; private set; }
    public ulong? OwnedRamPages { get; private set; }
    public string? SnapshotSha256 { get; private set; }
    public NativePreparationState State { get; private set; }
    public bool LocalCpuRouteRetired { get; private set; }
    public bool RemoteClosureProven => false;
    public string? QuarantineReason { get; private set; }
    public int OwnedRamReadEffects { get; private set; }
    public int FirmwareAckEffects { get; private set; }

    public string BeginPreparation(LabVm session, string mode)
    {
        RequireCurrent(session);
        if (State != NativePreparationState.DiscoveryAdmitted)
            throw new InvalidOperationException("Preparation already dispatched.");
        // Record possible mapping/allocation effect BEFORE dispatch, including
        // timeout/lost console response. No retry can assume no effect.
        State = NativePreparationState.EffectsPending;
        try {
            string text = owner.BeginShellUntil(@"fs0:\CXLHDM.EFI " + mode, "CXL_RAM_READY", 60);
            RequireCurrent(session);
            return text;
        } catch {
            Quarantine(BootFailure.AmbiguousState, "Preparation dispatch/observation failed; original VM pins retained.");
            throw;
        }
    }

    internal static bool ValidGeometry(ulong address, ulong pages) =>
        pages is > 0 and <= 17 && address != 0 && address % 4096 == 0 &&
        address <= ulong.MaxValue - pages * 4096;

    public void BindOwnedRam(LabVm session, ulong address, ulong pages)
    {
        RequireCurrent(session);
        if (State != NativePreparationState.SourceLocallyRetired || OwnedRamAddress is not null || !ValidGeometry(address, pages))
        {
            Quarantine(BootFailure.AmbiguousState, "Allocation binding is invalid or repeated.");
            throw new InvalidDataException(QuarantineReason);
        }
        OwnedRamAddress = address;
        OwnedRamPages = pages;
    }

    public byte[] ReadOwnedRam(LabVm session, ulong address, ulong pages)
    {
        RequireCurrent(session);
        if (State != NativePreparationState.SourceLocallyRetired || address != OwnedRamAddress || pages != OwnedRamPages)
            throw new InvalidDataException("Stale/foreign allocation read denied before effect.");
        byte[] bytes = owner.ReadBytes(address, checked((int)(pages * 4096)));
        OwnedRamReadEffects++;
        try { RequireCurrent(session); }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        return bytes;
    }

    private void RequireCurrent(LabVm session)
    {
        if (CheckCurrent(session) != BootFailure.None)
            throw new InvalidDataException("Native preparation session is not current.");
    }

    public string ReleaseFirmwareRam(LabVm session)
    {
        if (AdmitFirmwareRamRelease(session) != BootFailure.None)
            throw new InvalidDataException("Native preparation owner denied firmware RAM release.");
        State = NativePreparationState.ReleasePending;
        FirmwareAckEffects++;
        try {
            string receipt = owner.ContinueShell("c", 60);
            if (ObserveFirmwareRamRelease(session, receipt.Contains("CXL_RAM_RELEASED CLEARED=1")) != BootFailure.None)
                throw new InvalidDataException("Missing firmware clear/free receipt.");
            return receipt;
        } catch {
            Quarantine(BootFailure.AmbiguousState, "RAM release completion unknown; never retry ACK/free.");
            throw;
        }
    }

    public string ObserveMissingAckQuarantine(LabVm session, Action<string>? observeOriginalMetadata = null)
    {
        RequireCurrent(session);
        if (State != NativePreparationState.OwnedRamPublished)
            throw new InvalidDataException("Timeout observation requires original published RAM.");
        // Empty console input: observe firmware's own deadline without authorizing free.
        string text = owner.ContinueShell("", 60);
        RequireCurrent(session);
        observeOriginalMetadata?.Invoke(text);
        Quarantine(BootFailure.Quarantined, "Firmware observer expired without ACK; allocation retained.");
        return text;
    }

    public string ReleaseIntoExpectedClearQuarantine(LabVm session, Action<string> observeOriginalMetadata)
    {
        if (AdmitFirmwareRamRelease(session) != BootFailure.None)
            throw new InvalidDataException("Clear-failure workload has no current original RAM.");
        State = NativePreparationState.ReleasePending;
        FirmwareAckEffects++;
        string text = owner.ContinueShell("c", 60);
        RequireCurrent(session);
        if (!text.Contains("CXL_RAM_CLEAR_FAILURE_INJECTED") ||
            !text.Contains("CXL_RAM_QUARANTINED CLEAR_FAILED FREE_ATTEMPTS=0") || text.Contains("CXL_RAM_RELEASED"))
            throw new InvalidDataException("Clear failure did not deny native FreePages.");
        observeOriginalMetadata(text);
        Quarantine(BootFailure.Quarantined, "Native clear failed; original allocation remains pinned.");
        return text;
    }

    public BootFailure ObserveMappedAndLocallyRetired(LabVm session, Discovery discovery, bool localRouteRetired)
    {
        BootFailure current = CheckCurrent(session);
        if (current != BootFailure.None) return current;
        if (State != NativePreparationState.EffectsPending || discovery.Fingerprint != DiscoveryFingerprint || !localRouteRetired)
            return Quarantine(BootFailure.AmbiguousState, "Discovery changed or the local CPU route has no retire receipt.");
        MappingReceiptGeneration = checked(MappingReceiptGeneration + 1);
        LocalCpuRouteRetired = true;
        State = NativePreparationState.SourceLocallyRetired;
        return BootFailure.None;
    }

    public BootFailure PublishVerifiedOwnedRam(LabVm session, ulong address, ulong pages, ReadOnlySpan<byte> copiedRam)
    {
        BootFailure current = CheckCurrent(session);
        if (current != BootFailure.None) return current;
        if (State != NativePreparationState.SourceLocallyRetired || MappingReceiptGeneration == 0 ||
            !ValidGeometry(address, pages) || address != OwnedRamAddress || pages != OwnedRamPages || copiedRam.Length != checked((int)(pages * 4096)))
            return Quarantine(BootFailure.AmbiguousState, "RAM publication lacks a current local retire receipt or valid owned geometry.");

        verifiedSnapshot = copiedRam.ToArray();
        SnapshotSha256 = Convert.ToHexString(SHA256.HashData(verifiedSnapshot));
        OwnedRamAddress = address;
        OwnedRamPages = pages;
        State = NativePreparationState.OwnedRamPublished;
        return BootFailure.None;
    }

    public BootFailure ObserveClearedRamForCleanup(LabVm session, ulong address, ulong pages, ReadOnlySpan<byte> clearedRam)
    {
        BootFailure current = CheckCurrent(session);
        if (current != BootFailure.None) return current;
        if (State != NativePreparationState.SourceLocallyRetired || !ValidGeometry(address, pages) ||
            address != OwnedRamAddress || pages != OwnedRamPages || clearedRam.Length != checked((int)(pages * 4096)) ||
            clearedRam.IndexOfAnyExcept((byte)0) >= 0)
            return Quarantine(BootFailure.AmbiguousState, "Failed preparation did not provide a current cleared-RAM cleanup receipt.");
        OwnedRamAddress = address;
        OwnedRamPages = pages;
        State = NativePreparationState.ClearedRamCleanupPending;
        return BootFailure.None;
    }

    // This permits only the EFI lab's clear-and-FreePages path for the copied
    // System RAM.  It does not release/reuse the CXL mapping as a DMA/remote
    // resource; unknown remote closure remains represented by the retained
    // session receipt and cannot be consumed as production authority.
    public BootFailure AdmitFirmwareRamRelease(LabVm session)
    {
        BootFailure current = CheckCurrent(session);
        if (current != BootFailure.None) return current;
        return (State is NativePreparationState.OwnedRamPublished or NativePreparationState.ClearedRamCleanupPending) && LocalCpuRouteRetired
            ? BootFailure.None
            : Quarantine(BootFailure.Quarantined, "EFI RAM release denied outside the owned current session.");
    }

    public BootFailure ObserveFirmwareRamRelease(LabVm session, bool clearedAndFreed)
    {
        BootFailure current = CheckCurrent(session);
        if (current != BootFailure.None) return current;
        if (State != NativePreparationState.ReleasePending || !clearedAndFreed)
            return Quarantine(BootFailure.AmbiguousState, "EFI RAM release has no clear/free receipt.");
        State = NativePreparationState.RamReleased;
        return BootFailure.None;
    }

    public BootFailure CheckCurrent(LabVm session)
    {
        if (disposed || quarantined || !ReferenceEquals(owner, session))
            return BootFailure.Quarantined;
        try { owner.Qmp("query-status"); }
        catch {
            Quarantine(BootFailure.AmbiguousState, "Current reset observation unavailable; pins retained.");
            throw;
        }
        return owner.ObservedResetSequence == ResetSequence
            ? BootFailure.None
            : Quarantine(BootFailure.ResetObserved, "Reset epoch changed; session, mapping receipt, and RAM publication are stale.");
    }

    public BootFailure Quarantine(BootFailure failure, string reason)
    {
        quarantined = true;
        QuarantineReason = reason;
        State = NativePreparationState.Quarantined;
        return failure is BootFailure.None ? BootFailure.Quarantined : failure;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (State != NativePreparationState.RamReleased && !quarantined)
            Quarantine(BootFailure.Quarantined, "Session ended without an EFI RAM release receipt.");
        if (verifiedSnapshot is not null) CryptographicOperations.ZeroMemory(verifiedSnapshot);
    }

    internal static void SelfTest()
    {
        if (!ValidGeometry(4096, 17) || ValidGeometry(0, 1) || ValidGeometry(4097, 1) ||
            ValidGeometry(4096, 0) || ValidGeometry(4096, 18) ||
            ValidGeometry(4096, 1UL << 52) || ValidGeometry(ulong.MaxValue - 4095, 1))
            throw new InvalidOperationException("Native RAM bounded geometry validation failed.");
    }
}

internal enum NativePreparationState
{
    DiscoveryAdmitted,
    EffectsPending,
    SourceLocallyRetired,
    OwnedRamPublished,
    ClearedRamCleanupPending,
    ReleasePending,
    RamReleased,
    Quarantined,
}
