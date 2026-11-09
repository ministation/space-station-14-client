# Space Station 14 на Android

Android-клиент Space Station 14 на настоящем движке RobustToolbox 295.0.2. Лаунчер повторяет
официальный SS14: шапка с логотипом, вкладки Home, Servers, Options внизу, список серверов
зеброй, гостевой игры нет, подключение только с аккаунтом SS14. В игре тач-управление: стик в
левом нижнем углу и компактный блок кнопок в правом нижнем, центр экрана свободен для камеры.

За основу взят полный mobile-порт [SolomonNumb1/space-station-14-mobile](https://github.com/SolomonNumb1/space-station-14-mobile):
его лаунчер, настройки, логи и патч движка. От порта остались только эти слои, Mini Station-оболочки
и хаба больше нет.

## Что исправлено относительно порта-основы

| Проблема | Решение |
|---|---|
| На части телефонов разъезжались поля в диалогах | диалоги в ScrollView, поля растянуты по ширине, клавиатура не перекрывает ввод |
| Аккаунт SS14 не входил | вход через auth.spacestation14.com работает, токен уходит в игру (env + auth.json); поддерживается двухфакторный код в отдельном окне; ошибка входа показывается текстом прямо под полями (401, таймаут, HTTP-код), а не пустым Error; гостевой режим убран, без входа подключение не начинается |
| Неудобное управление | два стика в нижних углах (лево ходит, право целится), по четыре кнопки вокруг каждого стика, пиксельный вид как в Terraria и Minecraft, на кнопках символы клавиш (Z, E, X, Q, V, T, RMB); вне раунда (меню, лобби, экран подключения) панель скрыта |
| Ошибки прототипов и спрайтов | emergency fallback создаёт рабочие прототипы через приватные сеттеры: 0 ошибок proto в логе вместо 25110, лобби и игра рисуются без пропусков |
| При обновлении сервера качался весь контент (1.27 ГБ) | дельта-обновление по манифесту сервера: качаются только файлы с изменившимся BLAKE2B-хешем |
| Контент дублировался в приватной папке | контент лежит в отдельном каталоге и монтируется в движок (--mount-dir), а не распаковывается в FilesDir |

## Скачать

APK: [Release android-v0.6.5](https://github.com/ministation/space-station-14-client/releases/tag/android-v0.6.5).

Зеркало CDN: https://cdn.ministation.ru/android/latest/ru.ministation.client-Signed.apk
Кнопка Android есть на странице билдов: https://cdn.ministation.ru/fork/ministation
sha256 версии 0.6.3: df060c6897f7ed16e0c4e0d979fc8c8adc106f2fc591c28c86fd9e375007f8ed

Новые версии на CDN заливаются скриптом android/scripts/upload-cdn.sh
(ssh на CDN-хост, путь /var/robust-cdn/android/<версия>).
arm64-v8a, Android 8.0 и новее. AOT-компиляция в 0.6.2 на устройстве оказалась медленнее JIT, поэтому Release снова идёт через JIT. Начальный масштаб интерфейса 200%. Первый запуск скачивает контент сервера (около 1.27 ГБ),
дальше обновления сервера докачивают только изменённые файлы.

## Как это работает

1. APK содержит движок (Robust.Shared, Robust.Client) и нативные библиотеки SDL3, FreeType, OpenAL,
   zstd и libsodium.
2. Лаунчер получает у сервера `/info`: билд, манифест контента и адреса загрузки.
3. Контент лежит в `no_backup/content_cache/content_<сервер>/` рядом с манифестом. При смене билда
   сравниваются хеши файлов, недостающие качаются потоком `POST /download` с заголовком
   `X-Robust-Download-Protocol: 1` (zstd), старые zip-кеши распаковываются один раз и переиспользуются.
4. Игра запускается с `--mount-dir` на эту папку и подключается к серверу. Перед подключением
   лаунчер требует вход в аккаунт SS14, гостевого входа нет.

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

## Подпись APK

APK подписывается стабильным сертификатом Mini Station из android/host/keystore/release.keystore
(alias ministation, пароль ministation), скрипт android/scripts/sign-apk.sh ставит схемы v2 и v3.
Сертификат SHA-256: c5c81ad38d37a3dd772e416cb2132f75b52444bb087e038c631ad5767df58d42, действует до 2054 года.

Если Android пишет, что сертификат не подходит или приложение не устанавливается,
значит на телефоне стоит сборка, подписанная другим ключом (например 0.4.x).
Один раз удалите старую версию и поставьте новую, дальше обновления ставятся поверх без удаления.
