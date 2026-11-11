# SingNextOS CXL: действующие runtime seams

Сверено по локальному коду 24.10.2026. Старый последовательный план создания
подсистем заменён картой существующих consumers и оставшихся границ. Универсальный
статус «software roadmap complete» снят: source presence не qualification текущих
inputs. CXL 3.x/4.x в прежнем названии не означало реализацию всех возможностей
этих спецификаций; эта документация такого claim не делает.

## Разделы

* [Baseline и границы CXL](00-gap-matrix-and-decisions.md)
* [MutationEpoch и диапазоны RegionUse](01-mutation-epoch-and-region-use.md)
* [Общий external-operation lifecycle](02-common-external-operation-lifecycle.md)
* [Provider selection и dependency model](03-provider-selection-and-dependency-model.md)
* [CXL provider decomposition и authority](04-cxl-authority-and-provider-decomposition.md)
* [Type-3 backing, placement и health consequences](05-cxl-type3-memory-provider.md)
* [Type-2 staged accelerator service](06-cxl-type2-accelerator-service.md)
* [Visibility, publication и replay](07-visibility-publication-and-replay-contract.md)
* [QEMU lab и runtime/physical backend](08-real-hardware-and-qemu-backend.md)
* [Fabric Manager, pooling и reconfiguration](09-fabric-manager-pooling-and-reconfiguration.md)
* [Security и multi-host retention](10-security-securecompute-and-multihost.md)
* [Validation по actual consumer](11-validation-and-negative-test-matrix.md)
* [Cross-project contracts и non-goals](12-cross-project-contracts-and-non-goals.md)
* [Exact lifetime и reclaim closure](14-audit-hardening-and-reclaim-closure.md)
* [Ambiguous provider effects и FM atomicity](15-provider-effect-containment-and-fm-atomicity.md)
* [Fabric original sources и effect containment](16-fabric-exactness-and-generic-effect-containment.md)

## Оставшиеся границы

DirectCoherentWrite/SharedReadMostly admission закрыты; remote no-further-use
producer не установлен. Model Type-3 placement не physical/QEMU runtime provider.
QEMU lab доступен и имеет native evidence, но full SingBoot/kernel handoff и
production trust/DMA/reset не квалифицированы этим source review.

[Общий индекс](../README.md) · [QEMU lab](../../QEMU_CXL/README.md) ·
[Единственная runtime очередь](../../SingNextOS-v6-modernize/01-STATE-AND-BACKLOG.md).
