# Phase 5 — Networking

Дата: 2026-07-29

## Артефакты

| Компонент | Путь |
|---|---|
| Net lib | `src/Port.Net` |
| Desktop probe | `probes/Probe.NetCompile` |
| Android UI | кнопка «Probe network» в `Probe.AndroidHost` |

## Доказано на desktop

Цель: `ss14://ss14.ministation.ru:1214` (app id `RobustToolbox`)

```
HTTP OK — players/map/preset из /status
Lidgren Connected (~0.4s) — NetConnectionStatus.Connected
```

Это **транспортный** успех Lidgren. Полный SS14 login (MsgLoginStart, auth, encryption, content) ещё нет.

## Ограничения

- Нет Robust `NetManager` handshake / auth / sodium seal
- Нет загрузки content packs
- HTTP status = cleartext `http://…:1214/status` → на Android нужен cleartext (manifest)

## Android

Кнопка запускает `NetworkProbeSession.RunAsync()`:
1. HTTP status
2. Lidgren connect + poll status messages
3. Лог в UI

## Следующее (Phase 6)

Content packs: узнать, как клиент качает engine/content builds, минимальный download+verify на устройство (без запуска игры).
