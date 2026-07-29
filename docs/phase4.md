# Phase 4 — Graphics (GLES clear-color)

Дата: 2026-07-29

## Артефакт

| Компонент | Путь |
|---|---|
| GLES2 clear renderer | `src/Port.Platform.Android/Graphics/GlesClearRenderer.cs` |
| GLSurfaceView host | `src/Port.Platform.Android/Graphics/GlesClearSurfaceView.cs` |
| UI embed | `probes/Probe.AndroidHost` — `gl_container` |

## Что доказали

- EGL context client version **2** поднимается через `GLSurfaceView`
- Continuous `GlClear` работает (янтарный pulse = живой redraw)
- Touch на GL surface по-прежнему кормит Phase 3 touch queue
- Activity `OnResume`/`OnPause` прокидываются в `GLSurfaceView`

Это **не** Clyde и не шейдеры/спрайты SS14.

## Связь с Robust/Clyde

Clyde уже умеет ES/EGL на десктопе (`GLContextEgl`, `DisplayOpenGLVersion`).  
Следующий graphics-шаг (4b/позже): textured quad → простой shader pipeline ближе к Clyde draw primitives.

SDL3 Android natives по-прежнему отсутствуют в `SpaceWizards.Sdl` — GLES path через `GLSurfaceView` остаётся основным.

## Проверка

```powershell
dotnet build probes\Probe.AndroidHost\Probe.AndroidHost.csproj -c Debug
dotnet build probes\Probe.AndroidHost\Probe.AndroidHost.csproj -t:Install -c Debug
```

На экране: пульсирующая янтарная область + `gles: OK WxH frames=…` в статусе.

## Следующее (Phase 5)

Networking spike: Lidgren/Robust.Shared net connect к тестовому endpoint (хотя бы UDP handshake / лог), без полного Client boot.
