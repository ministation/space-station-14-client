# Robust Toolbox → Android (R&D port)

Экспериментальный порт игрового движка [Robust Toolbox](https://github.com/space-wizards/RobustToolbox) (движок Space Station 14) под Android.

Это **не готовый клиент**. Цель репозитория — идти маленькими шагами: понять блокеры, снять их по одному, в итоге получить возможность хотя бы observe/ghost на телефоне.

## Честные ожидания

| Что | Статус |
|---|---|
| APK-хост на .NET Android | ✅ probe собирается |
| Полный клиент SS14 на телефоне | ❌ далеко |
| Observe/ghost на сервер | ❌ после сети + рендера + контента |

Upstream (space-wizards) прямо писал: движок сильно завязан на **reflection и runtime codegen** (IoC, сериализация, сеть). На AOT-платформах (Android, консоли) это ломается, пока инфраструктуру не сделают AOT-совместимой.

## Структура

```
docs/                 план, блокеры, фазы
probes/
  Probe.AndroidHost/  минимальный Android APK (.NET 10)
  Probe.MathsCompile/
  Probe.SharedCompile/
  Probe.SharedOnAndroid/
src/
  Port.Platform.Android/  Phase 3 platform stubs
scripts/              клон Robust, проверки
vendor/               (gitignore) локальный shallow-клон RobustToolbox
docs/                 план, блокеры, фазы
```

## Быстрый старт

Требования: .NET 10 SDK, workload `android`, Android SDK / эмулятор или устройство.

```powershell
cd c:\ss14\bots\robust-android-port
dotnet build probes\Probe.AndroidHost\Probe.AndroidHost.csproj -c Debug

# поставить на устройство/эмулятор (если adb видит устройство):
dotnet build probes\Probe.AndroidHost\Probe.AndroidHost.csproj -t:Install -c Debug
```

Клон движка для анализа (не коммитится):

```powershell
.\scripts\clone-robust.ps1
```

## Фазы (кратко)

0. **Host** — Android APK, логи, статус порта  
1. **Inventory** — карта зависимостей Robust.Client (Clyde, GLFW/SDL, OpenGL, IoC)  
2. **AOT/reflection** — что нельзя trim/AOT, план замены  
3. **Window/input** — поверхность Android + тач вместо клавы/мыши  
4. **Render** — OpenGL ES / Vulkan вместо десктопного Clyde backend  
5. **Net** — подключение к игровому серверу  
6. **Content** — загрузка пакетов контента сервера  
7. **Observe** — ghost/spectator как первая игровая цель  

Подробности: [docs/roadmap.md](docs/roadmap.md), [docs/blockers.md](docs/blockers.md).

## Связь с сайтом

Сайт Mini Station (`token_site`) — отдельно. Этот репо — только движок/клиент. Companion-приложение сайта можно добавить позже, но это не замена порту.
