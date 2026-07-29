# Roadmap

## Phase 0 — Host shell ✅

- [x] Отдельный git-репозиторий
- [x] .NET 10 Android application template builds
- [x] Экран статуса порта / фаз
- [ ] `adb` install на реальное устройство
- [ ] CI: `dotnet build` probe на push

## Phase 1 — Inventory ✅ (черновик)

- [x] Shallow-клон RobustToolbox в `vendor/RobustToolbox`
- [x] Список проектов + Clyde/SDL3/EGL заметки → `docs/inventory.md`
- [x] Maths compile probe (`Probe.MathsCompile`) — OK
- [ ] Дополнить inventory после Shared spike

## Phase 2 — Shared-only compile spike ✅

- [x] Подключить `Robust.Shared` к console `net10.0` (`Probe.SharedCompile`)
- [x] Init submodules NetSerializer / Lidgren / LoaderApi
- [x] Ссылка Shared из `net10.0-android` (`Probe.SharedOnAndroid`)
- [x] Smoke в Android host UI
- [x] Заметки: `docs/phase2.md`
- [ ] Runtime smoke на реальном устройстве/эмуляторе (`dotnet build -t:Install`)

## Phase 3 — Platform layer stub ✅

- [x] `Port.Platform.Android`: lifecycle, paths, clock, touch, surface stub
- [x] Host UI: live status + touch pad
- [x] SDL3 Android investigation → `docs/phase3.md`
- [x] Strip non-Android natives from APK packaging (best-effort)
- [ ] Runtime verify на устройстве (`-t:Install`)

## Phase 4 — Graphics ✅ (clear-color)

- [x] GLES2 `GLSurfaceView` + clear-color (+ amber pulse)
- [x] Embed в Android host + touch на GL surface
- [x] Заметки: `docs/phase4.md`
- [ ] Textured quad / simple shader (optional 4b)
- [ ] (parallel) SDL3 Android natives research/build

## Phase 5 — Networking ✅ (Lidgren transport)

- [x] `Port.Net`: HTTP `/status` + Lidgren connect probe
- [x] Desktop probe → Mini Station **Connected**
- [x] Android host кнопка Probe network
- [ ] Full Robust handshake/auth (later, needs Client/Shared IoC)

## Phase 6 — Content download ✅ (ACZ sample)

- [x] `/info` + ACZ `/manifest.txt`
- [x] Sample blob download via `POST /download`
- [x] Desktop + Android host probe
- [ ] Full content sync + hash verify
- [ ] Engine build download (`283.1.0`)

## Phase 7 — Observe / ghost ⬅ in progress

- [x] Auth/handshake probe stub: `MsgLoginStart` / `MsgEncryptionRequest` / `api/session/join` / `MsgLoginSuccess`
- [x] SS14 username/password login (`api/auth/authenticate`) + Android UI
- [x] Engine CDN download (`283.1.0`, linux-arm64 fallback — no android RID yet)
- [x] Public-IP DNS preference + Cloudflare DoH (VPN RFC1918 workaround)
- [x] Fixed `api/session/join` hash encoding (standard Base64 — was Base64Url → HTTP 400)
- [x] One-shot **Connect to Mini Station** button (token ping/refresh → handshake)
- [x] Post-login lobby bootstrap (`GameSessionClient`): string table → mapstr skip → transfer → `MsgConVars` → `MsgPlayerList`
- [x] Deploy UI: SS14 icon, login + character lobby screens, stay connected
- [x] VPN DNS fix: drop private IPs + DoH + public fallback `138.124.14.77`
- [ ] Verify lobby on device (crew list + keep-alive)
- [x] Content ACZ sync with progress bar + resume; assemblies before join
- [x] Verbose join debug log on home/lobby
- [x] Smaller SS14 app icon (inset)
- [x] MsgState zstd inflate + uid scan hint
- [x] RobustSerializer + Content.Shared from disk → GameState / Eye / MsgStateAck (`0.0.14-gamestate`)
- [x] Fix ACZ OPTIONS Content-Type + libsodium Android natives (`0.0.15`)
- [x] Mini Station website-style UI (`0.0.15-ministation-ui`)
- [ ] Sprite draw from GameState (RSI / Clyde)
- [ ] Touch camera against real world coords

Каждая фаза должна заканчиваться **воспроизводимым артефактом** (build/log/screenshot), а не «почти готово».
