# UA4 Knob Bridge

`Ua4KnobBridge.exe` maps the RK M75 knob's confirmed Consumer reports to the Shanling UA4's device-side volume shown on its screen:

- `0x02FE` → UA4 volume −1
- `0x02FF` → UA4 volume +1
- one physical detent → exactly one UA4 level

The application does not change Windows master volume, install a driver, or use a global media-key hook. The keyboard firmware is not modified by this application.

## Use

1. Connect the RK M75 by wired USB or its 2.4 GHz receiver, plus the Shanling UA4.
2. Run [`dist/Ua4KnobBridge/Ua4KnobBridge.exe`](dist/Ua4KnobBridge/Ua4KnobBridge.exe).
3. Confirm both devices show **Connected** and the displayed UA4 volume is correct.

The window can be closed to the notification area. The tray menu provides **Open**, **Bridge enabled**, and **Exit**. **Start with Windows** stores a per-user Run entry, requires no administrator rights, and launches silently into the tray. The app reconnects automatically after either device is unplugged or its USB path changes. Its embedded Shanling icon is taken from the supplied Shanling upgrade tool.

Supported M75 knob collections are deliberately narrow: wired `VID 258A / PID 0163` and 2.4 GHz receiver `VID 258A / PID 0150`, both on Consumer Control `MI_01 / COL02`. Other keyboards and consumer-control devices are ignored.

Settings and logs are stored under `%LOCALAPPDATA%\Ua4KnobBridge`.

## Install / uninstall

This is a portable, self-contained Windows x64 application: the distribution folder contains exactly one file, `Ua4KnobBridge.exe`. Copy that file to a stable location and run it. If the executable is moved, disable and re-enable **Start with Windows**.

To uninstall:

1. Choose **Exit** from the tray menu.
2. Uncheck **Start with Windows** first, if enabled.
3. Delete the copied application folder. Optionally delete `%LOCALAPPDATA%\Ua4KnobBridge` to remove settings and logs.

## Build

From the project root:

```powershell
& '.\.tools\dotnet\dotnet.exe' build '.\src\Ua4KnobBridge\Ua4KnobBridge.csproj' -c Release
& '.\.tools\dotnet\dotnet.exe' run --project '.\tests\Ua4KnobBridge.Smoke\Ua4KnobBridge.Smoke.csproj' -c Release
& '.\.tools\dotnet\dotnet.exe' publish '.\src\Ua4KnobBridge\Ua4KnobBridge.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:PublishReadyToRun=true -o '.\dist\Ua4KnobBridge'
```

The smoke test validates the custom-usage state machine and a live read of the UA4 device-side vendor volume state.

## Source and notes

- [`src/Ua4KnobBridge`](src/Ua4KnobBridge)
- [`tests/Ua4KnobBridge.Smoke`](tests/Ua4KnobBridge.Smoke)
- [`docs/BUILD_INSTALL_ROLLBACK.md`](docs/BUILD_INSTALL_ROLLBACK.md)
- [`docs/REVERSE_ENGINEERING_FINDINGS.md`](docs/REVERSE_ENGINEERING_FINDINGS.md)
