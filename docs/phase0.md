# Phase 0 / 1 notes

## Toolchain (проверено локально)

- .NET SDK 10.0.x
- Workload `android` установлен
- `Probe.AndroidHost` собирается: `dotnet build probes/Probe.AndroidHost`

## Следующий конкретный шаг

1. Запустить `scripts/clone-robust.ps1` (нужен git + сеть).
2. Просмотреть:
   - `Robust.Client/GameController*.cs`
   - Clyde / graphics backends
   - `Robust.Shared` IoC
3. Заполнить `docs/inventory.md` таблицей модулей.

## Правило работы

Не пытаться «подключить весь Robust.Client к Android» одним коммитом.  
Каждый PR/сессия: один probe, один новый факт (собралось / не собралось / почему).
