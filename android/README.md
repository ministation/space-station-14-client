# Android-клиент Space Station 14

Здесь лежит Android-приложение, которое запускает **настоящий клиент Space Station 14**:
движок [RobustToolbox](https://github.com/space-wizards/RobustToolbox) 295.0.2 и контент сервера,
скачанный с его CDN. Лаунчер и игровой UI штатные, из SS14, поэтому выглядят как на ПК.

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
2. Лаунчер берёт у сервера `/info` и манифест контента (18144 файла с BLAKE2B-256-хешами).
3. Контент лежит в `no_backup/content_cache/content_<сервер>/`. При первом запуске он качается
   потоком `POST /download` с заголовком `X-Robust-Download-Protocol: 1` (zstd), при обновлении
   сервера качаются только файлы с другим хешем, старые zip-кеши распаковываются один раз.
4. Игра запускается с `--mount-dir` на папку контента: движок грузит Content.Client и штатный UI,
   подключается к серверу. Вход через auth.spacestation14.com опционален, гость тоже проходит,
   если сервер это разрешает.

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
adb shell am start -n ru.ministation.client/crc640001e7ab1265b90d.LauncherActivity
```

Подключение к серверу сразу при старте, в том числе с аккаунтом SS14 (если сервер требует авторизацию):

```bash
adb shell am start -n ru.ministation.client/crc640001e7ab1265b90d.LauncherActivity \\
  --es connect_addr "ss14.ministation.ru:1214" \\
  --es username "<ник>" --es userid "<uuid>" --es token "<токен SS14>"
```

Без токена подключение работает, если у сервера `auth.mode` равен `Optional` (так на
ss14.ministation.ru); если сервер требует вход, он отбивает хендшейк до появления лобби.

## Что уже проверено

| Что | Статус |
|---|---|
| Сборка движка под Android (net10.0-android) | работает |
| Сборка APK (arm64-v8a + x86_64) | работает |
| Загрузка контента по манифесту и дельта-обновления | работает, при смене билда качаются только изменения |
| Загрузка Content.Client и всех контентных сборок движком | работает |
| Инициализация сериализации и прототипов | работает |
| Рендер EGL/GLES, шейдеры Clyde | работает |
| Лаунчер SS14: серверы, фильтры, аккаунт, настройки, логи | работает |
| Вход на живой сервер | работает гостем при auth.mode Optional, доходит до лобби |
| Звук (OpenAL Soft) | инициализируется, на эмуляторе не слушали |

## Бенчмарк мобильного лайтмапа

CPU-часть лайтмапа (Clyde.MobileLighting.cs, блок между маркерами `mobile-lightmap-cpu-core`)
не зависит от движка, поэтому её можно проверять на ПК без Android SDK. Проект
tools/LightmapBench при каждой сборке берёт этот блок прямо из патча, сверяет результат
с замороженной копией версии 0.9.1 (tools/LightmapBench/Baseline.cs) бит в бит
и меряет время кадра по фазам: окклюдеры, источники, пиксели.

```bash
dotnet run -c Release --project tools/LightmapBench
```

Код выхода 1, если хоть один кадр отличается от 0.9.1. Если лайтмап меняется намеренно
(другая картинка), обнови Baseline.cs вместе с патчем.

## Ограничения

- Приложению нужно примерно 3 ГБ ОЗУ: движок плюс смонтированный контент. На телефоне с 6-8 ГБ
  это нормально, на слабом эмуляторе андроидный lowmemorykiller убивает процесс.
- Первый запуск требует интернета: контент не входит в APK (он большой и привязан к билду сервера).
- Hub-лаунчер из probes/Probe.AndroidHost больше не нужен: выбор серверов и аккаунт есть
  в штатном лаунчере приложения.
