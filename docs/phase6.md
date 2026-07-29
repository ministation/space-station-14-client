# Phase 6 — Content packs (ACZ)

Дата: 2026-07-29

## Артефакты

| Компонент | Путь |
|---|---|
| Content lib | `src/Port.Content` |
| Desktop probe | `probes/Probe.ContentCompile` |
| Android UI | кнопка «Probe content» |

## Mini Station `/info`

- `acz: true` (контент с самого игрового сервера, не внешний CDN zip)
- `engine_version: 283.1.0`
- `auth.mode: Required`
- endpoints: `GET /manifest.txt`, `OPTIONS|POST /download`

## Доказано на desktop

```
manifest: 16918 files (~2 MB manifest.txt)
sample: 3 Assemblies/*.dll через POST /download
```

Файлы кладутся в:

`{ContentRoot}/{fork}/{manifest_hash}/files/...`

плюс `manifest.txt` и `build-info.json`.

## Что ещё не сделано

- Полная загрузка всех ~17k blobs (долго/много места)
- Проверка BLAKE2b hash каждого файла
- Скачивание **engine** build `283.1.0` (отдельный CDN/launcher pipeline)
- Распаковка pre-compressed zstd blobs при необходимости
- Запуск Client / observe

## Android

Кнопка пишет sample в `Context.FilesDir/content/...`.

## Следующее (Phase 7)

Observe/ghost — самый тяжёлый кусок: нужен engine + полный content + handshake/auth + Clyde viewport. Реалистичный промежуточный шаг: headless net handshake после auth stub, или документированный gap-анализ.
