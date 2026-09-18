# HybridCpu_ExecutableAdapter

This project owns the executable child translation boundary. The legacy scaffold
remains diagnostic-only, while `HybridCpuExecutableChildAdapter` implements the
neutral child, guest-memory, executable-artifact and bounded virtual-I/O provider
contracts without implementing the ordinary `INeutralDomainRuntime` authority root.

The current project references the physically separate NeutralRuntime Contracts
and AuthorityCore assemblies plus the real
`HybridCPU.ExternalRuntime.Contracts` 1.14.0 and the compatible
`HybridCPU.ExternalRuntime` 1.3.0 package from the tracked repository-local
feed. It does not reference HybridCPU projects directly,
the compatibility aggregator, the Model assembly, or HybridCPU-v2 projects.

The earlier 1.1.0 baseline was audited at
`bdfe406d8ea241e46c95c56ebc93388768427df6`; the qualified V3 implementation is
commit `fd37b00a207a162baaa860f3f8b96c0c66d7e691`. The runtime 1.3.0 and Contracts
1.14.0 packages are exact-pinned and locked with content hashes. Executable
claims require explicit adapter composition; default composition
remains unavailable, and `RuntimeAdmission` is never promoted implicitly. The
operation schema is consumed only through the 1.14.0 Contracts package; the
child-domain executable adapter retains the established runtime implementation.
