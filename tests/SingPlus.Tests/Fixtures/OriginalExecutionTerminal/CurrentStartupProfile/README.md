# Current startup-profile fixtures

These images are executing inputs for `OriginalExecutionTerminalConsumerTests`,
`ExactVirtualEventConsumerTests` and `SingPlus.GuestHost`. Parent-directory images
and manifests retain their historical provenance and are not rewritten.

Producer evidence: `artifacts/v6/iteration-20261020-child-factory/`:

- `native-compiler-current/` is an isolated existing compiler CLI/CIL closure,
  composed with the five machine/compiler DLLs from the actual immutable
  Runtime1.37.0-securecompute.software.20261020.1 package.
- `producer-run-inputs.json`, `terminal-methods.json`, `fixture-producer/Program.cs`
  and `producer-run.log` describe the actual PE→CIL→compiler runs. The existing
  native producer supplies exact body-presentation and metadata hashes for
  nominal/managed roots; no instruction bytes or package digests are hand patched.
- `producer-results.json` lists31 positive/materialized image hashes and source
  methods. The PE subject is the snapshotted native tests assembly, not an assumed
  source HEAD or upstream NativeAOT qualification.
- `longrun.hcexe` is the current unallocated compiler output and yields budget
  exhaustion on this tuple. Default failure tests instead use
  `decoder-failure.hcexe`: the base compiler-produced HCO has its first instruction
  word replaced with reserved all-one bits, then the actual object writer,
  static linker and startup image builder produce the container/checksums.
  `decoder-negative-inputs.json`, `negative-longrun.hco.json`,
  `fixture-producer/Program.cs` and `decoder-negative.log` record that mutation.

The malformed guest is an explicit negative input; opcode assignments, encoding
definitions, decoder, register state, pipeline and memory model are unchanged.
No ProductionQualified, physical isolation or upstream NativeAOT claim follows.
