# SingNextOS external requirements

This directory records capabilities that SingNextOS may require from external toolchains or platform integrations while keeping all Sing+ architecture, contracts, admission rules, runtime semantics, tests, and CI changes inside `yuriyyak23/SingNextOS`.

An item in this directory is **External Blocked** only for the named external integration outcome. It must not be interpreted as permission or a requirement to modify HybridCPU-v2, HybridCPU ISE, compiler/backend/linker/loader/runtime/GC/EH, SDK packs, NativeAOT/ILCompiler, Roslyn, or the .NET runtime.

Each requirement uses this format:

- **ID**
- **Required external capability**
- **Why SingNextOS needs it**
- **Existing interface expected**
- **Minimal reproduction**
- **SingNextOS component blocked**
- **Fallback/mock used**

Local Definition of Done remains limited to SingNextOS architecture + contracts + verifier + runtime semantics. End-to-end `SingNextOS -> HybridCPU AOT -> HybridCPU image -> ISE` qualification is a separate integration stage.
