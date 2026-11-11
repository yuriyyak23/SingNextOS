# QEMU / EDK2 laboratory

Windows profile использует установленный QEMU 11.1.0, q35/CXL, TCG, готовую
EDK2 CODE firmware и отдельную VARS copy. Single CXL Type-3 profile использует
256 MiB RAM-backed CXL memory, 1 MiB LSA и 4 GiB CFMW. Это рабочая guest
laboratory configuration, а не persistent disk substitute.

Native EFI code уже покрывает PCI/ACPI discovery, Identify `0x4000`, capacity
gate, HDM profile, CXL.mem access, development capsule verification и bounded
RAM preparation. Guest CXL.mem read/write — отдельное evidence от создания
QEMU device или host-file checks.

На native Windows нет `memory-backend-file`; RAM backend теряется при exit QEMU.
Пользователь принял это для текущего milestone. `hdm_for_passthrough=on` —
отдельный qualified profile: alias removal после host uncommit не доказывает
DMA/cache/remote closure. QMP reset также не является таким доказательством.

Команды, versions и live evidence: [QEMU_CXL](../../QEMU_CXL/README.md).
