# Картина системы

Цель направления: получить проверенный boot path от UEFI/QEMU CXL Type-3 media
к kernel-owned System RAM и затем к обычной runtime authority SingNextOS.

```text
QEMU + EDK2
  -> native EFI laboratory
  -> CXL discovery / mailbox / HDM / capsule read
  -> verified copy to System RAM + BootInfo
  -> future actual SingBoot entry
  -> fresh runtime discovery and authority admission
```

Сейчас верхняя лабораторная часть существует как отдельный EFI verifier. Runtime
имеет own CXL provider/authority contracts. Между ними остаются реальные seams;
они не закрываются совпадением структуры данных или наличием model tests.

Физические identity, BootInfo и firmware mapping являются evidence. Runtime
authority создаётся только через fresh provider discovery, owner validation и
existing Region/operation lifecycle.
