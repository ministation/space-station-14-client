# Android-порт движка: RobustToolbox v295.0.2

Клиент собирается из двух частей: движок RobustToolbox и приложение (`android/host`).

- База: upstream space-wizards/RobustToolbox, тег `v295.0.2` (коммит a112cb8). Это ровно та версия,
  на которой работает сервер Mini Station, поэтому формат пакетов и протокол совпадают.
- Поверх тега применяется `android/patches/robust-android-v295.patch`. Патч это порт SolomonNumb1
  (репозиторий space-station-14-mobile) из версии движка 292, переложенный на 295.0.2: 112 файлов,
  Android-хост, SDL3, EGL/GLES-рендер, тач-управление, PathOffset, монтирование ресурсов из zip.
- Готовый движок лежит в `vendor/RobustToolbox` (в git не коммитится), его ставит
  `android/scripts/fetch-engine.sh`: клон тега, сабмодули, `git apply` патча.

## Сабмодули

Сабмодули стоят на коммитах, которые требует v295.0.2, и не поднимались:

| сабмодуль | коммит |
|---|---|
| Lidgren.Network/Lidgren.Network | 4a111ba2 |
| Robust.LoaderApi | 2b6a7db8 |
| XamlX | 8d9b5a8c |
| cefglue | f8f5135d |

Патч mobile-порта меняет указатель Lidgren с f7ecb5a (v292) на a22f2a9. Коммит a22f2a9 недоступен
ни в одном refs репозитория SpaceWizards.Lidgren.Network, а в патче это только строка указателя:
кода, который бы зависел от новых API Lidgren, нет. Поэтому оставлен указатель v295 (4a111ba2).

## Обязательный TargetOS=Android

`MSBuild/Robust.Platform.props` при пустом `TargetOS` подставляет ОС хоста, то есть Linux, даже
если приложение собрано как `net10.0-android`. Глобальное свойство `TargetOS` из app-проекта в
проекты движка не пробрасывается, поэтому его нужно передавать явно:

```bash
dotnet build android/host/Content.Android.csproj -f net10.0-android -c Release -p:TargetOS=Android
```

- Без параметра: `DefineConstants = ...;LINUX;UNIX;FREEDESKTOP`, движок подключает fontconfig и
  desktop-ветки. На Android это DllNotFoundException на libfontconfig в рантайме.
- С `-p:TargetOS=Android`: `DefineConstants = ...;LINUX;UNIX;ANDROID`, ветки Android включены,
  fontconfig не подключается.

`android/scripts/build-apk.sh` передаёт параметр и после сборки проверяет, что в
`Robust.Client.dll` нет символов `SystemFontManagerFontconfig` (то есть FREEDESKTOP-ветка не уехала
в APK). В CI референсного mobile-порта этого параметра нет, их APK собран с FREEDESKTOP-ветками.

## Правки поверх mobile.patch

Список пуст: патч применился к v295.0.2 целиком, Robust.Shared и Robust.Client компилируются в обоих
режимах без правок. Дрейф API версий 292 -> 295 в затронутых патчем файлах не проявился.

## Нативные библиотеки

SDL3 (f0e99e7c), FreeType (VER-2-13-3), OpenAL Soft (1.24.3), zstd (v1.5.7), libsodium (1.0.20),
сборка под arm64-v8a и x86_64: `android/scripts/build-native-libs.sh <abi>`, результат в
`android/native/<abi>`, оттуда `build-apk.sh` копирует их в `android/host/lib/<abi>` для упаковки в APK.
