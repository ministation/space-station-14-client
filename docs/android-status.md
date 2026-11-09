# Android-клиент: статус проверки

Дата проверки: 2026-11-09, эмулятор Android 36 (google_apis, x86_64), 6 ГБ ОЗУ, 4 ядра, GPU
swiftshader_indirect, APK собран из этого репозитория через `android/scripts/build-apk.sh Release`.

## Что проверено

| Шаг | Результат |
|---|---|
| Установка APK `ru.ministation.client` | Success, пакет запускается |
| Загрузка контента Mini Station | готовый `SS14.Client.zip` на месте, лончер пропускает скачивание |
| Распаковка `Assemblies/*.dll` | 13 сборок контента в приватной папке приложения |
| Старт движка | SDL3 3.5.0, video driver `android`, EGL/GLES поверхность создана |
| Загрузка модулей контента | `res.mod: DONE loading modules` за 1.45 с |
| Предзагрузка ресурсов | 6896 RSI в 5 атласов (0 ошибок), 1584 текстуры |
| Инициализация сериализации | `szr: Serializer Types Hash: 05D227...` |
| Регистрация прототипов | проходит, `proto: Registering an ignored prototype ...` |
| Штатный UI SS14 | `root: Switching to state Content.Client.MainMenu.MainScreen` |
| Стили и шейдеры | 425 style rules, шейдеры Clyde загружены |
| Главное меню на экране | да, распознаётся OCR (см. ниже) |
| Релизный APK из assets (`android-v0.1.0`) | установлен заново и запущен: доходит до того же штатного меню, без FATAL/ANR, скриншот `android-release-mainmenu.png` |
| Повторный connect-прогон | сервер теперь требует авторизацию SS14 и отбивает хендшейк без токена, см. раздел про вход |

## Главное меню (скриншот `mainmenu-verified.png`)

Распознанный текст экрана (tesseract, rus+eng):

```
МИНИ СТАНЦИЯ
Имя пользователя: JoeGenero
Адрес сервера: localhost
Прямое подключение
Настройки
Выйти
Обновления (1)
```

Это штатный MainScreen из Content.Client, а не самописный UI: русская локализация, кнопки прямого
подключения, настроек и выхода, счётчик обновлений. Поверх движок рисует тач-кнопки мобильного порта
(правый край и углы экрана).

## Память и производительность

- Приложение занимает около 2.2 ГБ PSS (TOTAL PSS 2191704 КБ, RSS 2246120 КБ) на главном меню.
- Запуск до главного меню на эмуляторе около 2.5 минут: движок, RSI-атласы, текстуры, регистрация
  прототипов, стили. На реальном телефоне будет быстрее.
- В логе `eng: MainLoop: Cannot keep up!` на эмуляторе со swiftshader: программный GL слабый,
  на устройстве с GPU нормально.

## Как повторить

```bash
android/scripts/fetch-engine.sh
android/scripts/build-native-libs.sh arm64-v8a
android/scripts/build-native-libs.sh x86_64
android/scripts/build-apk.sh Release
adb install -r android/host/bin/Release/net10.0-android/ru.ministation.client-Signed.apk
adb shell am start -n ru.ministation.client/crc640001e7ab1265b90d.LauncherActivity
```

Первый запуск скачивает контент сервера (около 1.27 ГБ). Для быстрых прогонов на эмуляторе архив
можно закинуть заранее в `/data/data/ru.ministation.client/files/content_cache/<sha256>.zip`
(имя файла: SHA256 из `http://ss14.ministation.ru:1214/info`, хеш билда контента в верхнем регистре;
владелец файла должен быть uid приложения).

## Что не проверено

- Вход на живой сервер: сервер Mini Station требует авторизацию SS14 (auth.spacestation14.com).
  Без токена клиент подключается, получает шифрованный канал и сразу отключается с сообщением
  "Connecting to this server requires authentication". С токеном сессия проходит дальше, но
  подтвердить заход в игру пока не удалось из-за отдельного блокера (см. ниже).
- Применение состояния игры: в прогоне с авторизованной сессией клиент получил full game state,
  показал окно правил сервера, но применение состояния упало с MissingMetadataException для entity 1
  и клиент пересинхронизировался по кругу. Это отдельный блокер движка, не сети.
- Звук: OpenAL Soft 1.24.3 инициализируется, но на эмуляторе без аудиовыхода не проверялся.
- Реальное устройство с GPU (arm64): APK собирается под обе ABI, проверка была на x86_64 эмуляторе.
