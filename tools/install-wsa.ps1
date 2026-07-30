# Install SS14 Hub into WSA (MagiskOnWSA)
$ErrorActionPreference = "Stop"
$WsaRoot = "C:\Users\Egor Romanovich\Downloads\WSA"
$Adb = "c:\ss14\bots\robust-android-port\tools\platform-tools\adb.exe"
$Apk = "c:\ss14\bots\robust-android-port\SS14-Hub-0.2.8-Signed.apk"
$Package = "org.ss14.mobilehub"
$AumidApp = "MicrosoftCorporationII.WindowsSubsystemForAndroid_8wekyb3d8bbwe!App"
$AumidSettings = "MicrosoftCorporationII.WindowsSubsystemForAndroid_8wekyb3d8bbwe!SettingsApp"

Write-Host "== WSA launch (AppX, not raw exe) ==" -ForegroundColor Cyan
# IMPORTANT: never start WsaClient.exe / WsaSettings.exe directly from the folder —
# they crash with STATUS_DLL_NOT_FOUND / 0xC000027B outside the AppX container.

$code = @"
using System;
using System.Runtime.InteropServices;
public static class WsaActivate {
  [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IApplicationActivationManager {
    IntPtr ActivateApplication([In] String appUserModelId, [In] String arguments, [In] int options, [Out] out uint processId);
  }
  [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
  class ApplicationActivationManager {}
  public static uint Launch(string aumid, string args) {
    var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
    uint pid; mgr.ActivateApplication(aumid, args ?? "", 0, out pid); return pid;
  }
}
"@
Add-Type -TypeDefinition $code -ErrorAction SilentlyContinue
[void][WsaActivate]::Launch($AumidSettings, "")
[void][WsaActivate]::Launch($AumidApp, "/launch wsa://system")
CheckNetIsolation.exe LoopbackExempt -a -n="microsoftcorporationii.windowssubsystemforandroid_8wekyb3d8bbwe" | Out-Null

Write-Host ""
Write-Host "В окне 'Подсистема Windows для Android' включи:" -ForegroundColor Yellow
Write-Host "  1) Режим разработчика = Вкл"
Write-Host "  2) (опционально) Дополнительные параметры → доступ к локальной сети"
Write-Host "Затем нажми Enter здесь..."
$null = Read-Host

if (-not (Test-Path $Adb)) { throw "adb not found: $Adb" }
if (-not (Test-Path $Apk)) { throw "APK not found: $Apk" }

& $Adb start-server | Out-Null
$connected = $false
foreach ($t in @("127.0.0.1:58526","127.0.0.1:5555")) {
  Write-Host "adb connect $t"
  $r = & $Adb connect $t 2>&1 | Out-String
  Write-Host $r
  if ($r -match "connected|already") { $connected = $true; break }
}
if (-not $connected) {
  Write-Host "Не вижу ADB. Проверь IP в настройках WSA Developer и введи host:port (например 172.x.x.x:5555):"
  $manual = Read-Host "endpoint"
  if ($manual) { & $Adb connect $manual }
}

& $Adb devices -l
Write-Host "Installing $Apk ..."
& $Adb install -r $Apk
Write-Host "Launching..."
& $Adb shell am start -n "$Package/crc64e1fb32118d275fd8.MainActivity"
Write-Host "Done." -ForegroundColor Green
