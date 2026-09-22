# HybridCPU AOT, Image, Loader and Entry ABI

HybridCPU-v2 is read-only for P15.

## External behavior that must be verified

Using the current qualified HybridCPU baseline, verify the existence and actual shape of:

- managed compiler pipeline;
- managed static linker/image builder;
- managed bootstrap descriptor;
- managed runtime bootstrap;
- BootBlob or equivalent image/bootstrap contract;
- ISE image loader/runtime binding;
- entry-symbol selection/encoding;
- required GC registration;
- unwind/type registration;
- reset-time transfer to the SingNext capsule.

The roadmap intentionally does not invent external API names. If a required behavior exists under a different current contract, bind to that contract. If it does not exist, P15 is `ExternalBlocked`/`FutureGated` for the affected claim.

## `BootCapsuleEntryAbiV1`

Must define:

- ABI version and total structure size;
- pointer width and alignment;
- immutable input descriptor address/length;
- boot RAM bounds;
- reset reason/evidence only when supplied by a verified external contract;
- allowed terminal outcomes: kernel transfer, local recovery/reset, fail-stop;
- no implicit runtime authority handles.

## `KernelEntryAbiV1`

Must define:

- calling convention;
- kernel image base and entry;
- BootInfo address/length;
- ownership of the handoff buffer;
- no authority transfer by virtue of address identity.

## External pin policy

Any change to:

- HybridCPU baseline SHA/version;
- compiler/static-linker pin;
- image format/bootstrap descriptor;
- ISE package;
- qualification package;

invalidates the previous external qualification artifact.

A documentation-only SHA update is forbidden. The pin update and qualification rerun must be one auditable change set or an explicitly blocked pair.

## Required gate behavior

If the current HybridCPU external contract does not provide a required image/entry/reset capability:

```text
P15 requirement
    -> ExternalBlocked / FutureGated
    -> exact required observable behavior
    -> Direct Boot feature disabled
```

Do not “fix” the problem by changing HybridCPU source inside P15.
