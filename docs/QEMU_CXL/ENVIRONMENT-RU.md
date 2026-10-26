# QEMU CXL: среда и profiles

Документальная сверка **26.10.2026** по saved launch/inputs и local scripts; environment probes/VM не перезапускались. Repos: `C:\Users\Yuriy\Desktop\SingNextOS` и `C:\Users\Yuriy\Desktop\HybridCPU ISE`.

| Параметр | Проверенное состояние |
|---|---|
| Host/tooling | Windows11 x64, PowerShell7; scripts также имеют PS5.1 paths; SDK pinned global.json (11.0.100-rc.2.26504.105 в recorded runs), existing MSVC x64 EFI compiler |
| Installed QEMU | C:\Program Files\qemu\qemu-system-x86_64.exe; verified11.1.0 v11.1.0-12130-ge470268ff4 |
| Baseline geometry | q35/TCG/4vCPU, RAM4GiB, CXL256MiB RAM + LSA1MiB RAM, CFMW4GiB |
| Backend | NativeWindowsRam, memory-backend-ram подключён как persistent-memdev; label persistent не persistence guarantee. File backend отсутствовал в checked installed build |
| Firmware | Installed edk2-x86_64-code.fd + private VARS from template, pflash, Secure Boot off. Firmware не менять/пересобирать |
| Acceleration | WHPX ранее не инициализировался; Windows settings не менять, accepted runs TCG |

RAM loss после QEMU exit принят пользователем. Файлы NativeWindowsFile не восстанавливают RAM backend; cold persistence/Variant C не обязательны. Installed secure firmware resources не означают Secure Boot on данного profile.

## Profiles не объединять

| Семейство | Отличие / scope |
|---|---|
| Native-owner / A V1 refusal | hdm_for_passthrough=on, CXL bus12; A private COM1/COM2, QMP stdio, -S до Job/channel attach, reboot=shutdown. V1 refusal не isolation admission |
| A V2/V3 и поздние workloads | Combined intel-iommu + EDU bus=pcie.0/00:03.0, private QTest. V3 mapping/RAM, callback observer, pending DMA и target-route — отдельные modes/epochs; GDB pipe только callback modes |
| External feasibility/enforce/grant | Отдельный CxlIommuFeasibility tool; root/hierarchy/relocation profiles. Relocation RAM6GiB/CXL bus24, EDU BDF отличается. Не переносить profile/claims на A |

Exact argv, initial private VARS/code/EFI/capsule/supervisor hashes — в конкретном [evidence report](evidence/README-RU.md), не в общей default command line. Поздние A launch records сохраняют initial private VARS hash; ограничения раннего V1 epoch не описывают весь current tool.

## Команды и эксплуатация

Текущие параметры/recipes: [qemu-cxl scripts](../../tools/qemu-cxl/README-RU.md), [SingNext.CxlLab](../../tools/SingNext.CxlLab/README.md), [external A tool](<../../../HybridCPU ISE/Tools/CxlLifecycleLab/README-RU.md>). Historical phase commands относятся только к своим outputs/tool inputs; не запускать всю кампанию автоматически.

Всегда explicit Profile и отдельный new scratch/output. Без BootImage installed EDK2 запускается без ОС; это не CXL failure. FAT capsule.bin provisions fresh CXL RAM, не native cold-persistent boot source. Lab fixture payload не исполняется.

Не Initialize/Stop чужой WorkDir, не terminate все QEMU/dotnet; cleanup только exact owned process. Не писать в real DirectBootCapsule. Restore/backing mutations только offline/exact owned paths и с требуемым разрешением. PowerShell argv arrays; не global ExecutionPolicy relaxation. Firmware/BCD/Group Policy/security/WSL install не входят в scope.

Docs-only не требует build/restore/VM. Source artifact/support changes перепроверять по необходимости; current hardware/software capability не выводится из upstream master examples.
