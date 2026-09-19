# R5 Independent Audit Evidence — PCI/CXL Transport Model

Baseline HEAD before R5: `472b7c9345605f1558958f0d00e4e2f1b4177fa4`; dirty status recorded/preserved. Optional adapter plan absent. No destructive/remote Git, network, or QEMU action was used.

## Audit and remediation

- Bounded/aligned PCI extended-capability traversal, missing nodes, loops and budget exhaustion are implemented by `BoundedPciCapabilityIterator`.
- Defect fixed: an attacker/model fixture could duplicate a unique capability while toggling `RequiredUnique` between occurrences. The iterator now tracks all seen IDs plus IDs declared unique and rejects either ordering.
- Mailbox maximum payload, deadline and late reply semantics are deterministic; regression proves a second completion after the deadline is rejected.
- LSA is optional; unsupported/invalid/timed-out locator remains eligible for canonical persistent-header fallback and never becomes a trust root.
- Defect fixed: HBLR anchor used `BootWire.TryRange`, whose `int` outputs incorrectly rejected valid 64-bit offsets/lengths above 2 GiB. The locator now performs pure unsigned 64-bit overflow checking. A large valid offset passes; a wrapping range fails.

Physical identifiers and locator fields are evidence only; they expose no authority object. Locator generation is not image, mapping, provider, or region generation. Timeout/invalid/unsupported outcomes are typed and do not imply release/reclaim.

## Command and result

`dotnet test tools/HybridCpu_ExecutableAdapter.Tests/HybridCpu_ExecutableAdapter.Tests.csproj --no-restore --filter "FullyQualifiedName~PciCxlTransportModelTests" --verbosity minimal` — passed 4, failed 0.

Claim level: `ModelValidated`. Real ECAM/CCI/mailbox timing and hardware behavior are `FutureGatedRequiresHardware`; owner: platform CXL backend; missing prerequisite: selected platform/device transport, deadlines and ownership contract.

No HybridCPU core/ISE/ISA/compiler/register/fetch/pipeline/replay/memory-controller/retire/scheduler/runtime-legality/microarchitecture implementation changed.
