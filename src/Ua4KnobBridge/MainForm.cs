using System.Runtime.InteropServices;

namespace Ua4KnobBridge;

public sealed class MainForm : Form
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const int SwRestore = 9;
    private const string WindowCaption = "UA4 Knob Bridge";
    private static readonly Color PageBackground = Color.FromArgb(245, 247, 250);
    private static readonly Color CardBackground = Color.White;
    private static readonly Color HeaderBackground = Color.FromArgb(27, 54, 72);
    private static readonly Color AccentColor = Color.FromArgb(24, 136, 194);

    private readonly BridgeSettings settings = BridgeSettings.Load();
    private readonly FileLogger logger = new();
    private readonly RawInputKeyboardKnob keyboard = new();
    private readonly Ua4VendorVolumeController ua4 = new();
    private readonly SingleInstanceCoordinator instanceCoordinator;
    private readonly NotifyIcon trayIcon;
    private readonly ToolStripMenuItem trayEnabled;
    private readonly ToolStripMenuItem trayKeyboardStatus = new() { Enabled = false };
    private readonly ToolStripMenuItem trayUa4Status = new() { Enabled = false };
    private readonly CheckBox enabledBox = new() { Text = "Enable knob bridge", AutoSize = true };
    private readonly CheckBox startupBox = new() { Text = "Start with Windows", AutoSize = true };
    private readonly Label keyboardIndicator = new() { AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Regular), Margin = new Padding(0) };
    private readonly Label ua4Indicator = new() { AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Regular), Margin = new Padding(0) };
    private readonly Label keyboardStatus = new() { AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Regular), Margin = new Padding(5, 0, 0, 0) };
    private readonly Label ua4Status = new() { AutoSize = true, Font = new Font("Segoe UI", 10, FontStyle.Regular), Margin = new Padding(5, 0, 0, 0) };
    private readonly Label volumeStatus = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 30, FontStyle.Regular) };
    private readonly ToolTip toolTip = new();
    private readonly object knobQueueGate = new();
    private readonly Queue<KnobAction> queuedKnobActions = new();
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 1500 };
    private readonly System.Windows.Forms.Timer instanceCommandTimer = new() { Interval = 250 };
    private readonly bool startedFromWindows;
    private bool allowInitialVisibility;
    private bool exiting;
    private bool initializing = true;
    private bool updatingStartupBox;
    private bool lastKeyboardConnected;
    private bool lastUa4Connected;
    private bool knobWorkerRunning;
    private string? lastKeyboardPath;
    private string? lastUa4Path;

    internal MainForm(bool startedFromWindows, SingleInstanceCoordinator instanceCoordinator)
    {
        this.startedFromWindows = startedFromWindows;
        this.instanceCoordinator = instanceCoordinator;

        Text = WindowCaption;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        BackColor = PageBackground;
        ForeColor = Color.FromArgb(30, 39, 49);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        // Fixed at the natural compact size: no fluid-card or clipping states.
        MinimumSize = Size.Empty;
        MaximumSize = Size.Empty;
        ClientSize = new Size(570, 590);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        // A login-started bridge owns a hidden native window solely for Raw
        // Input/device-change messages.  Do not advertise an unopened main
        // window in the taskbar; ShowWindow restores this when the user opens
        // the UI from the notification area.
        ShowInTaskbar = !startedFromWindows;

        BuildLayout();
        trayEnabled = new ToolStripMenuItem("Bridge enabled") { CheckOnClick = true };
        trayEnabled.Click += (_, _) => enabledBox.Checked = trayEnabled.Checked;

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open", null, (_, _) => ShowWindow());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(trayKeyboardStatus);
        trayMenu.Items.Add(trayUa4Status);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(trayEnabled);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApplication());

        trayIcon = new NotifyIcon
        {
            Icon = this.Icon,
            Text = "UA4 Knob Bridge",
            Visible = true,
            ContextMenuStrip = trayMenu
        };
        trayIcon.DoubleClick += (_, _) => ShowWindow();

        enabledBox.Checked = settings.Enabled;
        SetStartupCheckbox(ReadStartupState());
        ApplyEnabledState();
        initializing = false;

        keyboard.KnobActionReceived += OnKnobAction;
        keyboard.Diagnostic += (_, message) => logger.Write(message);
        refreshTimer.Tick += (_, _) => RefreshDevices();
        instanceCommandTimer.Tick += (_, _) => ProcessInstanceCommand();
        refreshTimer.Start();
        instanceCommandTimer.Start();
        Shown += (_, _) => RefreshDevices();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
        FormClosing += OnFormClosing;

        logger.Write(startedFromWindows
            ? "Started in the notification area; bridge initialization is active."
            : "Ready. Listening for RK M75 knob input.");

        // CreateControl() alone does not reliably create a top-level Form
        // handle while it is invisible.  Accessing Handle does, which ensures
        // OnHandleCreated registers Raw Input before the first knob detent.
        // The hidden handle keeps receiving WM_INPUT through RIDEV_INPUTSINK;
        // it is not necessary to open the main window after login.
        if (startedFromWindows) _ = Handle;
    }

    protected override void SetVisibleCore(bool value)
    {
        if (startedFromWindows && !allowInitialVisibility && value)
            value = false;
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            keyboard.Register(Handle);
            logger.Write($"Raw Input listener registered (window=0x{Handle.ToInt64():X}, hidden-start={startedFromWindows}).");
            RefreshDevices();
        }
        catch (Exception ex)
        {
            logger.Write($"Raw Input initialization failed: {ex.Message}");
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmInput)
        {
            try { keyboard.HandleRawInput(message.LParam); }
            catch (Exception ex) { logger.Write($"Raw Input error: {ex.Message}"); }
        }
        else if (message.Msg == WmInputDeviceChange && !IsDisposed)
        {
            try { BeginInvoke(RefreshDevices); }
            catch (InvalidOperationException) { }
        }

        base.WndProc(ref message);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = PageBackground,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 4
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(18, 10, 18, 10),
            BackColor = HeaderBackground,
            Margin = new Padding(0, 0, 0, 10),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        header.Controls.Add(new Label
        {
            Text = "UA4 Knob Bridge",
            Font = new Font("Segoe UI Semibold", 18, FontStyle.Regular),
            AutoSize = true,
            ForeColor = Color.White,
            Margin = new Padding(0)
        });
        header.Controls.Add(new Label
        {
            Text = "M75 knob → UA4 device volume",
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = Color.FromArgb(205, 224, 236),
            Margin = new Padding(2, 0, 0, 0)
        });
        root.Controls.Add(header, 0, 0);

        var statusCard = Card();
        var statusFlow = CardFlow();
        statusFlow.Controls.Add(SectionTitle("DEVICE STATUS"));
        var statusGrid = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 8, 0, 0),
            ColumnCount = 2,
            RowCount = 2
        };
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusGrid.Controls.Add(StatusName("RK M75"), 0, 0);
        statusGrid.Controls.Add(ConnectionStatus(keyboardIndicator, keyboardStatus), 1, 0);
        statusGrid.Controls.Add(StatusName("UA4"), 0, 1);
        statusGrid.Controls.Add(ConnectionStatus(ua4Indicator, ua4Status), 1, 1);

        var volumeFlow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 10, 0, 0)
        };
        volumeFlow.Controls.Add(new Label
        {
            Text = "Volume",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 9.5F),
            Margin = new Padding(0, 15, 8, 0)
        });
        Size volumeTextSize = TextRenderer.MeasureText("100", volumeStatus.Font);
        volumeStatus.AutoSize = false;
        volumeStatus.Size = new Size(volumeTextSize.Width, volumeTextSize.Height + 4);
        volumeStatus.TextAlign = ContentAlignment.MiddleLeft;
        volumeStatus.Margin = new Padding(0);
        volumeFlow.Controls.Add(volumeStatus);
        volumeFlow.Controls.Add(new Label { Text = "/ 100", AutoSize = true, Font = new Font("Segoe UI", 11), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 15, 0, 0) });
        statusFlow.Controls.Add(statusGrid);
        statusFlow.Controls.Add(volumeFlow);
        statusCard.Controls.Add(statusFlow);
        root.Controls.Add(statusCard, 0, 1);

        var bridgeCard = Card();
        var settingsFlow = CardFlow();
        var bridgeTitle = SectionTitle("BRIDGE");
        bridgeTitle.Margin = new Padding(0, 0, 0, 7);
        settingsFlow.Controls.Add(bridgeTitle);
        enabledBox.Margin = new Padding(0, 0, 0, 4);
        settingsFlow.Controls.Add(enabledBox);
        settingsFlow.Controls.Add(new Label
        {
            Text = "CCW  −1      •      CW  +1",
            AutoSize = true,
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 0, 8)
        });
        var startupTitle = SectionTitle("STARTUP");
        startupTitle.Margin = new Padding(0, 3, 0, 4);
        settingsFlow.Controls.Add(startupTitle);
        startupBox.Margin = new Padding(0, 0, 0, 1);
        settingsFlow.Controls.Add(startupBox);
        bridgeCard.Controls.Add(settingsFlow);
        root.Controls.Add(bridgeCard, 0, 2);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 15),
            Padding = new Padding(0, 2, 0, 0)
        };
        actions.Controls.Add(Button("Refresh devices", (_, _) => RefreshDevices(), primary: false));
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);

        toolTip.SetToolTip(keyboardStatus, "Accepts only the M75's custom CCW/CW reports.");
        toolTip.SetToolTip(ua4Status, "Controls the UA4 device-side volume shown on its screen.");

        enabledBox.CheckedChanged += (_, _) =>
        {
            if (!initializing)
            {
                ApplyEnabledState();
                SaveSettings();
            }
        };
        startupBox.CheckedChanged += (_, _) => ChangeStartup();
    }

    private static Panel Card() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        BackColor = CardBackground,
        Margin = new Padding(0, 0, 0, 10)
    };

    private static FlowLayoutPanel CardFlow() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Dock = DockStyle.Top,
        Padding = new Padding(16, 14, 16, 13),
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false
    };

    private static Label SectionTitle(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 8.5F),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 0, 0, 0)
    };

    private static Label StatusName(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 9.5F),
        Margin = new Padding(0, 3, 0, 0)
    };

    private static FlowLayoutPanel ConnectionStatus(Label indicator, Label text)
    {
        var flow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 3, 0, 0)
        };
        flow.Controls.Add(indicator);
        flow.Controls.Add(text);
        return flow;
    }

    private static Button Button(string text, EventHandler handler, bool primary)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Size = new Size(primary ? 122 : 126, 31),
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = primary ? 0 : 1, BorderColor = Color.FromArgb(210, 216, 222) },
            BackColor = primary ? AccentColor : CardBackground,
            ForeColor = primary ? Color.White : Color.FromArgb(45, 55, 65),
            Font = new Font("Segoe UI Semibold", 9F),
            Margin = new Padding(0, 0, 8, 0),
            UseVisualStyleBackColor = false
        };
        button.Click += handler;
        return button;
    }

    private void OnKnobAction(object? sender, KnobAction action)
    {
        if (!settings.Enabled) return;

        lock (knobQueueGate)
        {
            queuedKnobActions.Enqueue(action);
            if (knobWorkerRunning) return;
            knobWorkerRunning = true;
        }
        _ = Task.Run(ProcessKnobQueue);
    }

    private void ProcessKnobQueue()
    {
        while (true)
        {
            KnobAction action;
            lock (knobQueueGate)
            {
                if (queuedKnobActions.Count == 0)
                {
                    knobWorkerRunning = false;
                    return;
                }
                action = queuedKnobActions.Dequeue();
            }

            if (!settings.Enabled || exiting) continue;
            int delta = action == KnobAction.Clockwise ? 1 : -1;
            try
            {
                int verified = ua4.ChangeVolume(delta, DisplaySubmittedVolume);
                logger.Write($"{(delta > 0 ? "Clockwise" : "Counter-clockwise")}  →  UA4 volume {verified}");
            }
            catch (Exception ex)
            {
                logger.Write($"UA4 command failed: {ex.Message}");
                ua4.Refresh();
            }
            PostToUi(UpdateStatus);
        }
    }

    private void DisplaySubmittedVolume(int volume) => PostToUi(() => SetDisplayedVolume(volume));

    private void PostToUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void RefreshDevices()
    {
        if (IsDisposed) return;

        try { keyboard.RefreshDevices(); }
        catch (Exception ex) { logger.Write($"RK M75 scan failed: {ex.Message}"); }

        ua4.Refresh();

        if (keyboard.IsConnected != lastKeyboardConnected ||
            !string.Equals(keyboard.DevicePath, lastKeyboardPath, StringComparison.OrdinalIgnoreCase))
        {
            logger.Write(keyboard.IsConnected
                ? $"RK M75 connected: {keyboard.DevicePath}"
                : "RK M75 disconnected");
        }

        if (ua4.IsConnected != lastUa4Connected ||
            !string.Equals(ua4.DevicePath, lastUa4Path, StringComparison.OrdinalIgnoreCase))
        {
            logger.Write(ua4.IsConnected
                ? $"Shanling UA4 connected: {ua4.DevicePath}"
                : "Shanling UA4 disconnected");
        }

        lastKeyboardConnected = keyboard.IsConnected;
        lastUa4Connected = ua4.IsConnected;
        lastKeyboardPath = keyboard.DevicePath;
        lastUa4Path = ua4.DevicePath;
        SynchronizeStartupCheckbox();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        keyboardIndicator.Text = "●";
        ua4Indicator.Text = "●";
        keyboardStatus.Text = keyboard.IsConnected ? "Connected" : "Not detected";
        ua4Status.Text = ua4.IsConnected ? "Connected" : "Not detected";
        SetDisplayedVolume(ua4.CurrentVolume);
        keyboardIndicator.ForeColor = keyboard.IsConnected ? Color.DarkGreen : Color.DarkRed;
        ua4Indicator.ForeColor = ua4.IsConnected ? Color.DarkGreen : Color.DarkRed;
        keyboardStatus.ForeColor = SystemColors.ControlText;
        ua4Status.ForeColor = SystemColors.ControlText;
        toolTip.SetToolTip(keyboardStatus, keyboard.DevicePath ?? "Connect the RK M75 to begin listening for knob input.");
        toolTip.SetToolTip(ua4Status, ua4.DevicePath ?? "Connect the Shanling UA4 to control its device-side volume.");
        trayKeyboardStatus.Text = keyboard.IsConnected ? "RK M75: Connected" : "RK M75: Not detected";
    }

    private void SetDisplayedVolume(int? volume)
    {
        volumeStatus.Text = volume.HasValue ? volume.Value.ToString() : "—";
        // Keep the native GDI overhang padding here. The tighter metric can
        // clip the first digit at high DPI on some Windows font rasterizers.
        volumeStatus.Width = TextRenderer.MeasureText(volumeStatus.Text, volumeStatus.Font).Width;
        volumeStatus.ForeColor = volume.HasValue ? SystemColors.ControlText : SystemColors.GrayText;
        trayUa4Status.Text = volume.HasValue ? $"Shanling UA4: {volume} / 100" : "Shanling UA4: Not detected";
        trayIcon.Text = volume.HasValue
            ? $"UA4 Knob Bridge — UA4 {volume}/100"
            : "UA4 Knob Bridge — UA4 offline";
    }

    private void ApplyEnabledState()
    {
        settings.Enabled = enabledBox.Checked;
        trayEnabled.Checked = settings.Enabled;
        if (!settings.Enabled)
        {
            lock (knobQueueGate) queuedKnobActions.Clear();
        }
    }

    private void SaveSettings()
    {
        if (initializing) return;

        settings.Enabled = enabledBox.Checked;
        try { settings.Save(); }
        catch (Exception ex) { logger.Write($"Settings save failed: {ex.Message}"); }
    }

    private bool ReadStartupState()
    {
        try { return StartupManager.IsEnabled(); }
        catch (Exception ex)
        {
            logger.Write($"Could not read Windows startup state: {ex.Message}");
            return false;
        }
    }

    private void SynchronizeStartupCheckbox()
    {
        if (initializing) return;
        SetStartupCheckbox(ReadStartupState());
    }

    private void SetStartupCheckbox(bool value)
    {
        updatingStartupBox = true;
        try { startupBox.Checked = value; }
        finally { updatingStartupBox = false; }
    }

    private void ChangeStartup()
    {
        if (initializing || updatingStartupBox) return;

        bool requested = startupBox.Checked;
        try
        {
            StartupManager.SetEnabled(requested);
            bool actual = StartupManager.IsEnabled();
            if (actual != requested)
                throw new InvalidOperationException("Windows did not accept the requested startup setting.");

            logger.Write(actual
                ? $"Windows startup enabled for the current user: {StartupManager.Command}"
                : "Windows startup disabled.");
            SetStartupCheckbox(actual);
        }
        catch (Exception ex)
        {
            SetStartupCheckbox(ReadStartupState());
            logger.Write($"Startup setting unchanged: {ex.Message}");
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowWindow()
    {
        if (IsDisposed) return;

        allowInitialVisibility = true;
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Show();
        Activate();
        _ = ShowWindowNative(Handle, SwRestore);
        _ = SetForegroundWindow(Handle);
    }

    private void ProcessInstanceCommand()
    {
        string? command = instanceCoordinator.TakePendingCommand();
        if (command is null) return;
        logger.Write($"Instance command received: {command}");
        if (command.Equals("activate", StringComparison.OrdinalIgnoreCase)) ShowWindow();
        else if (command.Equals("exit", StringComparison.OrdinalIgnoreCase)) ExitApplication();
    }

    private void ExitApplication()
    {
        exiting = true;
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            trayIcon.ShowBalloonTip(1200, WindowCaption, "Still running in the notification area.", ToolTipIcon.Info);
            return;
        }

        refreshTimer.Stop();
        instanceCommandTimer.Stop();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        keyboard.Dispose();
        ua4.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindowNative(nint hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
