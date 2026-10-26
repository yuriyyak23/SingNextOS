# QEMU CXL tooling

Каноническая документация перенесена в **[docs/QEMU_CXL](../../docs/QEMU_CXL/README.md)**.
Скрипты/config остаются здесь, runtime artifacts — в `CXL/`.

* [Окружение и запуск](../../docs/QEMU_CXL/ENVIRONMENT-RU.md).
* [Текущий статус](../../docs/QEMU_CXL/CURRENT-STATE-RU.md).
* [План модернизации native boot](../../docs/QEMU_CXL/MODERNIZATION-PLAN-RU.md).
* [Промт продолжения](../../docs/QEMU_CXL/CONTINUATION-PROMPT-RU.md).

`Show-SingNextCxlBootProgress.ps1` выводит текущую configuration/scope границу;
с `-RunVm` создаёт отдельный output и запускает существующую native EFI lab
демонстрацию. `-IncludeFaults` и `-IncludeReset` добавляют scoped failure/reset
cases. Это не запускает RuntimeKernel и не заявляет production boot.

Команды выполняются из корня SingNextOS. Установленный Windows QEMU не имеет
memory-backend-file; используйте явный NativeWindowsRam profile. Это volatile
backend под persistent-memdev, не файловая persistence. Отдельный native EFI
verifier проверен; полноценная загрузка SingBoot/RuntimeKernel не заявляется.

Прежняя подробная инструкция сохранена в [history](../../docs/QEMU_CXL/history/README-RU.md).
