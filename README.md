# Space Station 14 на Android

Android-клиент Space Station 14 на настоящем движке RobustToolbox 295.0.2. Интерфейс лаунчера и
игры штатный, из SS14: список серверов, вход в аккаунт SS14, настройки, логи, песочница и обычный
экран меню/лобби. Сверху движок добавляет тач-управление и экранные кнопки.

За основу взят полный mobile-порт [SolomonNumb1/space-station-14-mobile](https://github.com/SolomonNumb1/space-station-14-mobile):
его лаунчер, настройки, логи и патч движка. От порта остались только эти слои, Mini Station-оболочки
и хаба больше нет.

## Что исправлено относительно порта-основы

| Проблема | Решение |
|---|---|
| На части телефонов разъезжались поля в диалогах | диалоги в ScrollView, поля растянуты по ширине, клавиатура не перекрывает ввод |
| Аккаунт SS14 не входил | вход через auth.spacestation14.com работает, токен уходит в игру (env + auth.json), у аккаунта есть гостевой режим |
| При обновлении сервера качался весь контент (1.27 ГБ) | дельта-обновление по манифесту сервера: качаются только файлы с изменившимся BLAKE2B-хешем |
| Контент дублировался в приватной папке | контент лежит в отдельном каталоге и монтируется в движок (--mount-dir), а не распаковывается в FilesDir |

## Скачать

APK: [Release android-v0.2.0](https://github.com/ministation/space-station-14-client/releases/tag/android-v0.2.0).
arm64-v8a и x86_64, Android 8.0 и новее. Первый запуск скачивает контент сервера (около 1.27 ГБ),
дальше обновления сервера докачивают только изменённые файлы.

## Как это работает

1. APK содержит движок (Robust.Shared, Robust.Client) и нативные библиотеки SDL3, FreeType, OpenAL,
   zstd и libsodium.
2. Лаунчер получает у сервера `/info`: билд, манифест контента и адреса загрузки.
3. Контент лежит в `no_backup/content_cache/content_<сервер>/` рядом с манифестом. При смене билда
   сравниваются хеши файлов, недостающие качаются потоком `POST /download` с заголовком
   `X-Robust-Download-Protocol: 1` (zstd), старые zip-кеши распаковываются один раз и переиспользуются.
4. Игра запускается с `--mount-dir` на эту папку и подключается к серверу. Авторизация SS14
   необязательна: сервер сам решает, пускать ли гостя.

Код и сборка: [android/README.md](android/README.md). Проверка на эмуляторе: [docs/android-status.md](docs/android-status.md).

## Структура

```
android/host/            Android-приложение (net10.0-android): лаунчер + boot движка
android/patches/         Android-патч RobustToolbox 295.0.2
android/scripts/         fetch-engine, build-native-libs, build-apk
docs/                    статус проверки и R&D-история порта
probes/, src/            ранние прототипы порта (история, в сборку не входят)
vendor/                  локальный клон RobustToolbox (gitignore)
```

## Быстрый старт сборки

```bash
android/scripts/fetch-engine.sh            # один раз: клон движка + патч
android/scripts/build-native-libs.sh arm64-v8a
android/scripts/build-native-libs.sh x86_64
android/scripts/build-apk.sh Release
```

APK: `android/host/bin/Release/net10.0-android/ru.ministation.client-Signed.apk`.
