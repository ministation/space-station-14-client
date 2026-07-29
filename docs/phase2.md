# Phase 2 — Shared compile spike

Дата: 2026-07-29

## Результаты

| Probe | Результат |
|---|---|
| `Probe.SharedCompile` (`net10.0`) | ✅ build + run |
| `Probe.SharedOnAndroid` (`net10.0-android` → ref Shared) | ✅ build |
| `Probe.AndroidHost` + Shared smoke | подключено (runtime на устройстве проверить через Install) |

## Блокер, который сняли

Первый fail Shared был **не Android**, а пустые git submodules:

- `NetSerializer`
- `Lidgren.Network/Lidgren.Network`
- (также полезен) `Robust.LoaderApi`

После `git submodule update --init --depth 1` Shared собрался.

Скрипт `scripts/clone-robust.ps1` обновлён: тянет эти submodule'ы сразу.

## Замечания (ещё не блокеры сборки)

- NU1510: `Microsoft.Win32.Registry`, `System.Reflection.Metadata` — desktop-ish deps, на Android могут мешать в runtime/trim.
- В `Robust.Shared.csproj` также: `System.Management`, `TerraFX.Interop.Windows` — потенциальные runtime issues при реальном IoC/init, не при простом `Angle.Zero`.
- Full trim на Android host ослаблен до `partial` — Shared reflection-heavy.
- XA0141: в APK пытаются попасть `linux-x64` native libs (`libsodium`, `TracyClient`) — отдельный cleanup в Phase 3.

## Следующее (Phase 3)

Минимальный platform stub без полного Client:

1. Activity lifecycle + лог тиков
2. Исследовать SDL3 Android native libs / `SpaceWizards.Sdl`
3. Не поднимать весь `Robust.Client` одним куском
