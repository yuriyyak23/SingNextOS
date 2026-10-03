# ADR-009 — Host-debug static verifier and model composition

Date: 2026-10-03. Status: implemented bounded host-debug reference contour; all new v6 gates OFF.

The managed-GC SingPlus.Boot executable is the existing host-debug composition root. Its static verification consumer previously referenced the Admission CLI executable, which violated the privileged-to-tooling reference policy. The actual user-added HostModelPlatform consumer also constructs the existing Host model provider explicitly.

Decision: compile the five unchanged verifier/proof/IL/frontend source files once in SingPlus.StaticAdmission, with no SingPlus/runtime/provider dependencies or packages. Retain original source paths for canonical digest consumers; CLI excludes these compilation inputs and forwards the thirteen existing public types to the library. Boot references the library. Static proof remains evidence only; AuthorizesExecution, MaterializesCapability and IsRuntimeAuthority stay false.

The project-reference policy permits only the exact SingPlus.Boot.csproj -> SingPlus.Platform.Host.csproj composition edge while the source remains an Exe/User/ManagedGc project. Runtime/kernel privileged projects remain prohibited from direct ProviderAdapter/Tooling references. Capsule, HybridCPU and other provider references are not permitted by this exception. Actual host provider selection remains explicit HostModelPlatform; this ADR grants no capability or provider effect admission and enables no v6 gate.

Owners remain CapabilityAuthority for capability permission, existing runtime/process/domain owners for admission and lifecycle, existing provider for provider state, and the verifier library for static calculation. No new authority/ledger, no proof-to-permission shortcut. ModelOnly host execution is not physical qualification, containment, durability, reservation or WCET evidence.

V1 public names/signatures/proof canonicalization and CLI entry point are preserved via type forwarding. One new local library assembly/project; no NuGet/package/schema/ISA/opcode/CPU changes. Exact source/dependency/binary tuples, tests and limitations belong to the iteration evidence.
