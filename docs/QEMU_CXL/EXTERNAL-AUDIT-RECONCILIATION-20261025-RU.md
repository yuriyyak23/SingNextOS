# Аудит 25.10: принятые выводы и последующие решения

Исторический source-review input, актуализирован 26.10 только для устранения stale planning. Это не отдельная очередь и не новый executable pass. [Current state](CURRENT-STATE-RU.md), [primary blockers](blockers-singnextos/README-RU.md), [canonical queue](modernize/README-RU.md).

| Вывод аудита | Последующее состояние |
|---|---|
| Reuse NativePreparationOwner, не второй manager/EFI composition | Выполнено в existing native path |
|19-page record после EFI return не cross-reset retention | Сохраняется. Default unchanged-firmware evidence отличается от A exact-VM-terminal policy |
| Distinct VM/reset/isolation/mapping provenance; QMP/counter/nonce не authority | Сохраняется в A contract |
| Whole-VM termination с external supervisor/COM2 | Пользователь явно выбрал A; bounded lab implementation и consumer adoption выполнены |
| Existing external DMA enforcement и mapped retirement | Verified в отдельных input epochs; TE-off feasibility, enforcement и mapped allow не end-to-end сумма |
| HCI-01B low IOVA→high CXL HPA | Exact route verified26.10: default EDU mask, real read/write/integrity, original retire/invalidation/deny faults, ABA denial; native admission withdrawn до diagnostic grant |
| Reuse runtime adapters/importer/admission | APIs есть, actual native startup consumer отсутствует; N1 design completed, runtime substrate Missing/ExternalBlocked |
| Independent trust/protected durable state | Provider/provisioning отсутствуют; TPM2 NV/trusted storage остаются candidates, не принятое implementation решение |
| Decoder disable / CPU route / DMA+other closure | Разные факты; local alias/VT-d не general physical/cache/remote closure |
| Upstream source объясняет exact installed QEMU failure | Exact installed-source attribution не выполнена; сохраняется только local observation |
| EBS/retained RAM/kernel ABI | N1 разрешил design, не implementation/EBS/RuntimeKernel execution |

[Evidence index](evidence/README-RU.md) сохраняет positive/negative terminal records и failed first CCMD/CAIG0 history. Поздние callback/pending-DMA/provider-loss runs не переносятся на старые workloads и не дают mid-transaction physical DMA или same-VM recovery claims.

Рекомендация аудита сама не authorization. Действуют explicit A и N1 decisions; новые runtime-substrate/production trust scopes не выбраны. Эта сверка не заменяет actual callers, adopted binaries или original receipts.
