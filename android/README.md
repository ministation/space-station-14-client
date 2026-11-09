# Android-клиент Mini Station

Здесь лежит Android-приложение, которое запускает **настоящий клиент Space Station 14**:
движок [RobustToolbox](https://github.com/space-wizards/RobustToolbox) 295.0.2 (та же версия, что
на сервере Mini Station) и контент Mini Station, скачанный с CDN сервера. UI в игре штатный, из
Content.Client, поэтому выглядит как на ПК.

```
android/
  host/                    Android-проект приложения (net10.0-android)
  patches/                 Android-патч движка (из mobile-порта SolomonNumb1, адаптирован под 295.0.2)
  scripts/fetch-engine.sh  клон RobustToolbox v295.0.2 + патч в vendor/RobustToolbox
  scripts/build-native-libs.sh  сборка SDL3, FreeType, OpenAL, zstd, libsodium под Android
  scripts/build-apk.sh     сборка APK
  native/<abi>/*.so        результат build-native-libs.sh (gitignore)
```

## Как это работает

1. APK содержит движок (Robust.Shared, Robust.Client) и нативные библиотеки для arm64-v8a и x86_64,
   плюс маленький boot-пак ресурсов движка (engine-resources.zip).
2. При первом запуске приложение скачивает с CDN Mini Station готовый SS14.Client.zip
   (сборка контента под текущий билд сервера, примерно 1.27 ГБ), распаковывает из него
   Assemblies/*.dll в приватную папку приложения.
3. Движок монтирует скачанный архив как ресурсы (--mount-zip), грузит Content.Client.dll
   из распакованных ассемблей и запускает обычный клиент SS14. Дальше это штатный UI и штатный
   сетевой стек: вход через auth.spacestation14.com, сервер ss14.ministation.ru:1214.
4. Контент кэшируется по хешу сборки: при обновлении сервера скачивается только новая сборка.

## Сборка

Нужны: .NET 10 SDK с workload android, Android SDK (platform 36, build-tools 36), JDK 21,
Android NDK 27.2.12479018.

```bash
android/scripts/fetch-engine.sh            # один раз, клонирует движок и применяет патч
android/scripts/build-native-libs.sh arm64-v8a
android/scripts/build-native-libs.sh x86_64
android/scripts/build-apk.sh Release
```

APK: android/host/bin/Release/net10.0-android/ru.ministation.client-Signed.apk.

Важно: -p:TargetOS=Android обязателен при сборке (скрипт его передаёт). Без него движок
собирается с desktop-ветками (FREEDESKTOP, fontconfig) и на Android ведёт себя неправильно.

Установка на устройство или эмулятор:

```bash
adb install -r android/host/bin/Release/net10.0-android/ru.ministation.client-Signed.apk
adb shell am start -n ru.ministation.client/ru.ministation.client.LauncherActivity
```

## Что уже проверено

| Что | Статус |
|---|---|
| Сборка движка под Android (net10.0-android) | работает |
| Сборка APK (arm64-v8a + x86_64) | работает |
| Загрузка и распаковка контента Mini Station | работает |
| Загрузка Content.Client и всех контентных сборок движком | работает |
| Инициализация сериализации и прототипов | работает |
| Рендер EGL/GLES, шейдеры Clyde | работает |
| Штатное главное меню Content.Client | работает, см. [docs/android-status.md](../docs/android-status.md) |
| Вход на живой сервер | не проверен, нужен аккаунт SS14 |
| Звук (OpenAL Soft) | инициализируется, на эмуляторе не слушали |

## Ограничения

- Приложению нужно примерно 3 ГБ ОЗУ: движок плюс смонтированный контент. На телефоне с 6-8 ГБ
  это нормально, на слабом эмуляторе андроидный lowmemorykiller убивает процесс.
- Первый запуск требует интернета: контент не входит в APK (он большой и привязан к билду сервера).
- Пока это отдельное приложение с простым экраном запуска. Hub-лаунчер из probes/Probe.AndroidHost
  будет подключён к нему как экран выбора серверов и аккаунта.
