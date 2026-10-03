# Build, install, and rollback

## Requirements

- Windows x64.
- RK M75 `VID_258A/PID_0163` in normal USB mode.
- Shanling UA4 `VID_20B1/PID_3026` with the existing Shanling CTUAC2 driver.
- No administrator rights.

The supplied executable is self-contained; a separate .NET installation is not required.

## Run

```powershell
& 'D:\Tools\Codex\Keyboard_volum_knob_for_Shanling_UA4\dist\Ua4KnobBridge\Ua4KnobBridge.exe'
```

The GUI reports both device states and the current UA4 device-side volume. It accepts only `0x02FE` and `0x02FF`, applies exactly `-1/+1`, and never calls the Windows master-volume API. Closing the window hides it in the notification area; **Exit** terminates it. A second launch activates the existing instance and does not create a duplicate.

The app polls every 1.5 seconds, re-enumerates USB paths after reconnects, and reads the UA4 volume before every change so physical UA4 changes are respected. The device screen range is `0..100`.

## Start with Windows

Checking **Start with Windows** writes only:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Ua4KnobBridge
```

The value is the exact current executable path followed by `--startup`. Startup is per-user, requires no elevation, initializes Raw Input and the UA4 bridge automatically, and suppresses the main window so the application starts in the tray. The checkbox is re-read from the registry during runtime and after reconnect polling.

Settings and logs:

```text
%LOCALAPPDATA%\Ua4KnobBridge\settings.json
%LOCALAPPDATA%\Ua4KnobBridge\bridge.log
```

## Build and smoke-test

```powershell
Set-Location 'D:\Tools\Codex\Keyboard_volum_knob_for_Shanling_UA4'
& '.\.tools\dotnet\dotnet.exe' build '.\src\Ua4KnobBridge\Ua4KnobBridge.csproj' -c Release
& '.\.tools\dotnet\dotnet.exe' run --project '.\tests\Ua4KnobBridge.Smoke\Ua4KnobBridge.Smoke.csproj' -c Release
& '.\.tools\dotnet\dotnet.exe' publish '.\src\Ua4KnobBridge\Ua4KnobBridge.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:PublishReadyToRun=true -o '.\dist\Ua4KnobBridge'
```

The smoke test validates the custom-usage state machine, rejection of native E9/EA/E2 usages, and a live read of the UA4 device-side vendor volume state.

## Uninstall / rollback

1. Choose **Exit** from the tray menu.
2. Uncheck **Start with Windows** before deleting or moving the executable.
3. Delete the portable application folder.
4. Optionally delete `%LOCALAPPDATA%\Ua4KnobBridge` to remove settings and logs.

Manual rollback, limited to this application's exact value:

```powershell
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$current = Get-ItemPropertyValue -Path $runKey -Name 'Ua4KnobBridge' -ErrorAction SilentlyContinue
if ($current -and $current -match 'Ua4KnobBridge\.exe') {
    Remove-ItemProperty -Path $runKey -Name 'Ua4KnobBridge'
}
```

The application installs no driver, service, scheduled task, firmware, or machine-wide setting. The executable is unsigned.
