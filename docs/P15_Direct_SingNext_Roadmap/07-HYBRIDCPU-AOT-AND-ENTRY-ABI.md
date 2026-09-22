# P15.6 — HybridCPU AOT/image path and entry ABI

## Current blocker

The SingNextOS qualification path currently records:

```text
ManagedAssemblyToHybridCpuAot = ExternalBlocked
ImageGeneration = NotProduced
IseLoaderAcceptance = NotAttempted
```

Meanwhile current HybridCPU-v2 already contains managed bootstrap/image infrastructure. P15 must replace the stale cross-repo assumption with a pinned, reproducible compiler/linker/bootstrap integration.

## Required artifact pipeline

```text
C# source
  -> net11 managed assembly
  -> SingNext static admission proof
  -> HybridCPU managed compiler frontend
  -> HybridCPU object artifacts
  -> static linker
  -> managed bootstrap descriptor
  -> bootable HybridCPU image
  -> ISE loader acceptance
```

The same high-level pipeline is required for:

1. `SingNext.Boot.Capsule`;
2. `SingPlus.Kernel`.

The two artifacts may have different runtime/helper profiles.

## Toolchain identity evidence

Qualification output MUST bind:

- exact SingNextOS revision/tree;
- exact HybridCPU revision/tree;
- .NET SDK identity;
- compiler contract version;
- managed ABI digest;
- platform contract digest;
- linker/image format version;
- admission proof digest;
- final image SHA-256/SHA-384;
- ISE command and result.

No JSON report or digest is authority; it is qualification evidence only.

## ROM -> capsule entry ABI

The exact ABI should be small and fixed. Suggested registers/fields are platform-specific, but semantics should include:

```text
arg0: HybridPlatformDescriptorV1 physical address
arg1: descriptor length
arg2: boot epoch/reset sequence
arg3: optional ROM evidence page address
```

Capsule entry starts in a precisely specified privilege/execution domain with interrupts and translation state defined by the reset/ROM profile.

## Capsule -> kernel entry ABI

`KernelEntryAbiV1` MUST be contract-owned and include:

- ABI major/minor or version;
- kernel entry address;
- BootInfo physical address/length;
- reset sequence;
- optional platform descriptor address/length;
- flags/reserved fields.

The kernel performs range/version/reserved validation before consuming data.

## Required ISE milestones

### M1 — trivial capsule

Execute a capsule that validates a platform descriptor and returns a known status.

### M2 — trivial kernel chainload

Capsule copies a small admitted kernel image, creates minimal BootInfo and enters the real kernel entry ABI.

### M3 — CXL-backed kernel

Kernel image is read through the temporary CXL aperture, destination-verified and then entered.

### M4 — fresh takeover

Kernel fresh-admits current CXL endpoint and retires the boot aperture.

No later milestone should be claimed if an earlier artifact boundary is still simulated by host process invocation.
