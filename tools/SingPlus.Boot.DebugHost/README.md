# SingPlus.Boot.DebugHost

This is a **debug-only tooling boundary**, not a SingPlus execution provider and not an OS security boundary. It first executes the normal host-debug boot and SIP component-admission sequence, with CXL and HybridCPU platform backends absent.

Every invocation also runs a platform-neutral RuntimeKernel smoke path using public contracts: component admission, capability mint/validation/revocation, root-process lifecycle, a minimal `ExecutionRole.Sip` child (start, park, resume, terminate), and owned-buffer transfer to that child. The SIP-role child is a runtime model/lifecycle exercise; it does not execute a SIP binary.

If `--sip` is supplied, the host additionally runs the admitted managed assembly with ordinary .NET authority outside SingPlus capabilities.

```text
SingPlus.Boot.DebugHost --mode in-process --sip path\\to\\SipApp.dll -- arg1 arg2
SingPlus.Boot.DebugHost --mode child-process --sip path\\to\\SipApp.dll -- arg1 arg2
```

`in-process` uses a collectible `AssemblyLoadContext` and invokes the managed assembly entry point. `child-process` starts the supplied `.dll` with `dotnet`, or a supplied executable directly. Both modes run under the user's ordinary Windows/.NET identity and can access resources allowed to that identity. They must never be used as evidence of SingPlus capability isolation, SIP sandboxing, firmware, CXL, HybridCPU, or hardware execution.

For Visual Studio, set `SingPlus.Boot.DebugHost` as the startup project and place, for example, `--mode in-process --sip C:\\path\\to\\SipApp.dll -- arg1` in the Debug command-line arguments. Running with no arguments validates the boot path and the debug-local runtime exercise only.

The RuntimeKernel used by the smoke path is intentionally debug-local; the Boot pipeline keeps its own authority graph private. No firmware evidence, boot metadata, CXL mapping, device identity, or host-debug result is converted into runtime region/capability authority.
