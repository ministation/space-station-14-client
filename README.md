# Space Station 14 на Android (Mini Station)

Android-клиент Mini Station на настоящем движке SS14. Это не самописный UI и не заглушка: приложение
запускает [RobustToolbox](https://github.com/space-wizards/RobustToolbox) 295.0.2 (та же версия, что на
сервере Mini Station), подгружает готовый контент сервера с CDN и показывает штатный игровой интерфейс
Space Station 14, как на ПК. Сверху движок добавляет тач-управление и экранные кнопки, сам UI остаётся
родным.

| Что | Статус |
|---|---|
| Движок RobustToolbox 295.0.2 на Android | работает (android/patches/robust-android-v295.patch) |
| Сборка APK под arm64-v8a и x86_64 | работает |
| Загрузка контента Mini Station (SS14.Client.zip, около 1.27 ГБ) | работает |
| Загрузка Content.Client и всех контентных сборок движком | работает |
| Прототипы, локали, рендер (EGL/GLES) | работает |
| Главное меню и вход на сервер | проверяется на эмуляторе |
| Hub-лаунчер (выбор сервера и аккаунта) | есть в probes/Probe.AndroidHost, объединяется с игрой |

Код и сборка: [android/README.md](android/README.md). Устройство порта движка:
[android/ENGINE-PORT.md](android/ENGINE-PORT.md).

## История R&D

До появления рабочего клиента репозиторий был R&D-портом: probes/, src/, docs/ описывают фазы 0-7,
блокеры AOT/reflection и сетевые эксперименты. Это осталось как история исследований, рабочий клиент
живёт в android/.

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
