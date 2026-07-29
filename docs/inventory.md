# Inventory (по shallow-клону RobustToolbox)

Клон: `vendor/RobustToolbox` (depth 1, branch master).  
TFM движка: **net10.0** (`MSBuild/Robust.Engine.props`).

## Ключевые проекты

| Module | Role | Desktop deps | Android outlook | Notes |
|---|---|---|---|---|
| `Robust.Shared.Maths` | math | почти pure | **ok** | Probe: `probes/Probe.MathsCompile` собирается и запускается |
| `Robust.Shared` | core shared | Win registry, TerraFX Windows, management… | **compile OK** с Android ref | Нужны submodule Lidgren+NetSerializer; runtime desktop pkgs ещё не проверены (`docs/phase2.md`) |
| `Robust.Client` | game client | **SDL3**, OpenGL/EGL, OpenAL, Discord RPC, Avalonia.Base, Xlib… | **очень тяжёлый** | `OutputType=WinExe` |
| Clyde (внутри Client) | renderer | OpenGL + EGL + ANGLE | нужен GLES path | Есть `GLContextEgl`, `DisplayOpenGLVersion` ES |
| Clyde Windowing | окна/ввод | **`Sdl3WindowingImpl`** | потенциальный рычаг | SDL3 официально умеет Android — перспективнее писать WSI с нуля |
| `ClydeHeadless` | headless gfx | — | полезен для тестов без GPU UI | |
| `Lidgren.Network` | net | sockets | вероятно ok | submodule; Shared зависит |
| `NetSerializer` | serialization | managed | вероятно ok | submodule; Shared зависит |
| `Robust.Server` | server | — | не цель телефона | |
| Generators / Analyzers | AOT-направление | compile-time | важно | Upstream двигается к source gen (меньше runtime emit) |

## Выводы для порта

1. **Не начинать с полного `Robust.Client`.** Сначала Maths → Shared (с stubs) → сеть → Clyde/SDL3 Android.
2. **Окно/ввод:** существующий путь — SDL3 (`SpaceWizards.Sdl`). Исследовать Android-сборку SDL3 + EGL ES, а не GLFW.
3. **Рендер:** Clyde уже знает про OpenGL ES / EGL / ANGLE — хороший знак; всё равно нужны шейдеры/фичи GLES3.
4. **AOT:** `RobustILLink` уже есть на Client — Android publish будет ещё жёстче; держать probe на Debug без full trim, пока Shared не стабилен.
5. **Native packaging:** при линке Shared в Android host летят предупреждения XA0141 — в APK тянутся `linux-x64` `.so` (`libsodium`, `Tracy`). Нужна фильтрация Android ABI natives.
6. **Graphics:** Phase 4 clear-color через `GLSurfaceView`/`GLES20` работает без SDL (`docs/phase4.md`).

## Следующий spike (Phase 3)

Platform stubs + исследование SDL3 Android. Shared compile spike закрыт (`docs/phase2.md`).
