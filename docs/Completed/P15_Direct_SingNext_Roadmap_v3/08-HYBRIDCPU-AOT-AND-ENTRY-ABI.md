# HybridCPU AOT, Image and Entry ABI

HybridCPU-v2 is read-only for P15.

## Current external drift

Observed current HybridCPU-v2 master: `794c4a53494f503855ac8cf209efab23fde083b2`.

Current SingNextOS qualification code still requires:

```text
AuditedHybridCpuRevision = 9e001bf29df06ad3d4ff7337f81d4e5bc0a62fc9
AuditedHybridCpuCompilerContractVersion = 6
```

The current HybridCPU-v2 master still documents `CompilerContract.Version = 6`, but equal contract version is **not** qualification equivalence. The SHA drift is an explicit `RepositoryDrift`/external requalification requirement.

## Required external behaviors

Verify on the requalified baseline:

- managed compiler pipeline;
- static linker/image builder;
- bootstrap descriptor/runtime bootstrap;
- reset-loadable image/BootBlob-equivalent;
- ISE loader/runtime binding;
- entry-symbol selection and calling convention;
- GC/type/unwind registration required by the selected capsule memory profile;
- reset transfer to capsule;
- ISE-visible PCI/CXL/HDM behavior sufficient for the claimed lane.

If any required behavior is absent: `ExternalBlocked`/`FutureGated`; Direct Boot remains disabled for that evidence lane.

## Entry ABI ownership

`BootCapsuleEntryAbiV1` and `KernelEntryAbiV1` are wire/entry contracts only. They carry addresses, sizes, versions and evidence; they never carry SingNext capabilities/provider handles.

## Pin policy

Never replace `9e001...` with `794c4a...` as a documentation-only edit. A new pin must be committed together with reproducible qualification evidence (or with an explicit blocked artifact if qualification cannot run).

P15 must not modify HybridCPU compiler/ISE source to satisfy a missing gate.
