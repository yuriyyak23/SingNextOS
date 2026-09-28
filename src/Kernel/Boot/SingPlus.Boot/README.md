# SingPlus.Boot host-debug profile

Set `SingPlus.Boot` as the Visual Studio startup project and start it with F5. The process builds a local `RuntimeKernel`, verifies that no external platform backend is installed, starts the managed system-process lifecycle, and stops on `Console.ReadLine` while a debugger is attached.

An additional managed artifact can be admitted as a zero-authority SIP component:

```text
SingPlus.Boot --sip <absolute-or-relative-managed-assembly-path>
```

`--sip` may be repeated. Each artifact is size-bounded, checked as a managed assembly, hashed with SHA-256, assigned a fresh process/domain identity, and passed through `RuntimeKernel.AdmitComponent`. The admitted component reaches the runtime `Running` lifecycle state only after manifest/image validation and transactional admission.

This host-debug profile does **not** load or invoke the assembly entry point. The repository currently has no SIP binary execution/isolation contract, so reflection loading or launching the assembly as an ordinary child process would bypass SingPlus authority and isolation. CXL discovery, CXL Boot, HybridCPU execution, firmware evidence, and platform-provider admission are intentionally absent.
