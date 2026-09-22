# External HybridCPU Feature Gates

No row authorizes a HybridCPU-v2 source change inside P15.

| Gate | Required external behavior | Exists now? | Evidence path | SingNext behavior until available | Affected PRs | Maximum claim |
|---|---|---|---|---|---|---|
| `HC-AOT-IMAGE` | compile/link capsule closure into reset-loadable image | revalidate current external baseline | HybridCPU qualification artifact | Direct Boot image path disabled | 08,09,13 | `AdapterQualified` |
| `HC-ENTRY` | stable capsule entry symbol/calling convention/input descriptor | revalidate | ABI vector + ISE entry test | fail build/feature gate | 08,13 | `AdapterQualified` |
| `HC-BOOTSTRAP` | required managed bootstrap services, including any GC/type/unwind registration used by selected profile | revalidate | executable ISE test | use stricter supported profile or remain blocked | 09,13 | `AdapterQualified` |
| `HC-RESET-ROM` | architectural reset/ROM transfers to signed local capsule under defined contract | revalidate | ISE/hardware reset evidence | no Direct Boot claim | 13,14 | evidence-dependent |
| `HC-ISE-CXL` | ISE exposes enough PCI/CXL/HDM behavior for claimed ISE lane | revalidate | ISE protocol trace | CXL path remains adapter-only | 06,07,13 | `AdapterQualified` |
| `HC-HW-CXL` | real platform exposes required config/MMIO/mailbox/decoder behavior to SingNext adapter | hardware dependency | hardware qualification | hardware Direct Boot disabled | 06,07,14 | at most `IseValidated` |
| `HC-PIN` | selected HybridCPU baseline passed SingNext qualification | verify for every pin change | `SingPlus.HybridCpuQualification` artifact | stale pin cannot promote claims | 09,13,14 | prior valid claim only |

## Gate naming policy

Do not invent external API/version names to make the roadmap look concrete. Until a current verified external contract provides a stable name/version, gates are defined by observable required behavior.

## Fail-closed policy

If a required gate is not satisfied:

- build may continue for lower evidence lanes where safe;
- the Direct Boot feature remains disabled for the blocked lane;
- qualification artifact records the gate as blocked;
- no optimistic fallback to unverified external behavior is allowed.
