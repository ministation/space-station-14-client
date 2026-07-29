# Phase 7 — Mini Station UI + join fixes

## APK

`artifacts/RobustAndroidPort-0.0.16-join-fix.apk` (`0.0.16-join-fix`)

## 0.0.16 fixes

- **Content `Java.RuntimeException`**: skip OPTIONS; use `SocketsHttpHandler` (not Android Java HTTP); richer inner-exception logs; batch retries; no `Progress<T>` UI re-entry.
- **UDP timeout ~15s**: Lidgren handshake 25×1s; bind `IPAddress.Any`; join **in parallel** with content download.
- Clearer fail text if UDP still blocked (VPN / Private DNS).

## 0.0.15

- OPTIONS Content-Type, libsodium Android natives, Mini Station UI.
