# Phase 3 — Android platform stubs

Дата: 2026-07-29

## Артефакты

| Компонент | Путь | Назначение |
|---|---|---|
| Platform lib | `src/Port.Platform.Android` | lifecycle, paths, clock, touch queue, surface stub |
| Host UI | `probes/Probe.AndroidHost` | показывает статус + touch pad |

## Что работает в stub

- **Lifecycle:** Created → Started → Resumed → Paused → Stopped → Destroyed
- **Clock:** тики пока Activity в Resumed
- **Paths:** `files/`, `cache/`, `content/`, `userdata/` под `Context.FilesDir`
- **Touch:** очередь событий с pad (Down/Move/Up), будущий маппинг в Robust input
- **Surface stub:** размер pad как placeholder под GL (Phase 4)

Это ещё **не** `IWindowingImpl` Clyde и не запуск Client.

## SDL3 investigation

NuGet `SpaceWizards.Sdl` **1.1.1**:

- есть только managed `lib/net9.0/SpaceWizards.Sdl.dll`
- **нет** `runtimes/*/native` с `.so` для Android

В Clyde windowing:

- основной путь — `Sdl3WindowingImpl`
- video drivers в коде явно: `windows`, `x11`, иначе `Other`
- Android driver (`android`) **не обработан отдельно**
- есть EGL hints (`SDL_HINT_OPENGL_ES_DRIVER`) — полезно для GLES

Вывод для Phase 4:

1. Нужны **Android builds SDL3** (arm64-v8a / x86_64) + binding load path.
2. Либо форк/расширение `Sdl3WindowingImpl` под `SdlVideoDriver.Android`.
3. Либо отдельный `AndroidWindowingImpl` на `GLSurfaceView` без SDL (дольше, но без native SDL).

Рекомендация: сначала GLES clear-color на `GLSurfaceView` (Phase 4a), параллельно искать/собирать SDL3 Android natives (Phase 4b).

## Packaging

В csproj host:

- `RuntimeIdentifiers=android-arm64;android-x64`
- target `StripNonAndroidNatives` — best-effort; **XA0141** на `libsodium`/`Tracy` `linux-x64` пока всё ещё всплывает при packaging (нужен более ранний hook / ExcludeAssets). Не блокирует Debug build.

## Следующее (Phase 4)

`GLSurfaceView` + clear color + (опционально) textured quad; документ API для будущего Clyde bridge.
