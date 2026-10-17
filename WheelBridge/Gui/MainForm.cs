using System.Drawing;
using System.Windows.Forms;

namespace WheelBridge.Gui;

/// <summary>
/// Tuning window: every steering and FFB setting as live sliders/checkboxes, per-layer output meters,
/// named profiles, and the same telemetry monitor as the console dashboard. Changes apply immediately
/// (the FFB thread reads the shared config) and are autosaved to wheelbridge.json.
/// </summary>
public sealed class MainForm : Form
{
    public static void Run()
    {
        // WinForms needs an STA thread; the top-level program is async (MTA).
        var t = new Thread(() =>
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var host = new BridgeHost();
            Application.Run(new MainForm(host));
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    private readonly BridgeHost _host;
    private BridgeConfig Cfg => _host.Config;
    private readonly ToolTip _tips = new() { AutoPopDelay = 30000, InitialDelay = 500, ReshowDelay = 200 };
    private readonly List<Action> _refreshers = [];   // push config values back into the controls
    private readonly CenterBar[] _layerBars = new CenterBar[FfbModel.LayerOrder.Length];
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 50 };
    private readonly System.Windows.Forms.Timer _autosave = new() { Interval = 1000 };
    private bool _syncing;

    private const string SteamAppId = "4078430";               // STAR WARS: Galactic Racer
    private const string GameProcess = "SWGR-Win64-Shipping";   // the actual game exe (SWGR.exe is a stub)
    private DateTime _nextGameCheck, _launchedAt = DateTime.MinValue;
    private bool _gameRunning;

    private Button _launch = null!, _kill = null!, _center = null!, _motorTest = null!, _record = null!, _calibrate = null!, _getSdk = null!;
    private bool _sdkBusy;
    private bool _centerShown;
    private string? _lastFrameError;
    private ComboBox _profiles = null!;
    private Label _ffbStatus = null!, _profileLabel = null!;
    private CenterBar _output = null!;
    private MonitorView _monitor = null!;
    private SteeringCurve _curve = null!;
    private ToolStripStatusLabel _message = null!;

    public MainForm(BridgeHost host)
    {
        _host = host;
        Text = "SWGR Wheel Bridge";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(1500, 940);
        MinimumSize = new Size(1100, 700);
        StartPosition = FormStartPosition.CenterScreen;

        var status = new StatusStrip();
        _message = new ToolStripStatusLabel($"Config: {BridgeHost.ConfigPath}") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        status.Items.Add(_message);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 820, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(BuildControls());
        _monitor = new MonitorView { Dock = DockStyle.Fill };
        split.Panel2.Controls.Add(_monitor);

        Controls.Add(split);
        Controls.Add(BuildToolbar());
        Controls.Add(status);
        Load += (_, _) => split.SplitterDistance = (int)(ClientSize.Width * 0.56);

        LoadProfileList();
        UpdateProfileLabel();
        if (Cfg.LoadWarning is { } warning)
            Load += (_, _) => MessageBox.Show(this, warning, "SWGR Wheel Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        _ui.Tick += (_, _) => RefreshLive();
        _ui.Start();
        _autosave.Tick += (_, _) => { _autosave.Stop(); _host.SaveConfig(); };
        FormClosing += (_, _) => { _ui.Stop(); if (_autosave.Enabled) { _autosave.Stop(); _host.SaveConfig(); } };
    }

    // ───────────────────────────── layout ─────────────────────────────

    private Control BuildToolbar()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), WrapContents = true };

        _launch = new Button { Text = "▶ Launch game", Height = 34, AutoSize = true, Font = new Font(Font, FontStyle.Bold) };
        _launch.Click += (_, _) => LaunchGame();
        Tip(_launch, Tips.Launch);

        _kill = new Button { Text = "KILL FFB", Width = 150, Height = 34, Font = new Font(Font, FontStyle.Bold), FlatStyle = FlatStyle.Flat };
        _kill.Click += (_, _) => { _host.Ffb.Killed = !_host.Ffb.Killed; Say(_host.Ffb.Killed ? "FFB killed. Click again to resume" : "FFB resumed"); };
        Tip(_kill, Tips.Kill);

        _center = new Button { Text = "Force center", Height = 34, AutoSize = true, FlatStyle = FlatStyle.Flat };
        _center.Click += async (_, _) =>
        {
            var ffb = _host.Ffb;
            if (!ffb.CenterLock && ffb.Racing) { Say("Force center is only available outside a race"); return; }
            if (!ffb.CenterLock && ffb.NeedsMotorTest)
            {
                // With an untested motor direction the hold could push the wheel away from center.
                if (MessageBox.Show(this, "Force center needs to know which way your motor turns. Take your hands off the wheel and click OK to run the motor test first.",
                        "Force center", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                if (!await RunMotorTestAsync()) return;
            }
            ffb.CenterLock = !ffb.CenterLock;
            _centerShown = ffb.CenterLock;
            Say(ffb.CenterLock ? (ffb.Killed ? "Force center on, but FFB is KILLED, so the wheel stays free" : "Force center on: wheel pinned at 0°, stick held at 0")
                               : "Force center off");
        };
        Tip(_center, Tips.ForceCenter);

        _motorTest = new Button { Text = "Motor test", Height = 34, AutoSize = true };
        _motorTest.Click += async (_, _) => await RunMotorTestAsync();
        Tip(_motorTest, Tips.MotorTest);

        _record = new Button { Text = "● Record", Height = 34, AutoSize = true };
        _record.Click += (_, _) => Say(_host.ToggleRecording());
        Tip(_record, Tips.Record);

        _calibrate = new Button { Text = "Calibrate pedals", Height = 34, AutoSize = true };
        _calibrate.Click += (_, _) => Say(_host.BeginCalibration());
        Tip(_calibrate, Tips.Calibrate);

        _profiles = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180, Margin = new Padding(16, 8, 3, 3) };
        var load = new Button { Text = "Load", Height = 34, AutoSize = true };
        load.Click += (_, _) => LoadSelectedProfile();
        var save = new Button { Text = "Save", Height = 34, AutoSize = true };
        save.Click += (_, _) => SaveProfile(Cfg.ActiveProfile);
        var saveAs = new Button { Text = "Save as…", Height = 34, AutoSize = true };
        saveAs.Click += (_, _) => SaveProfile(null);
        var delete = new Button { Text = "Delete", Height = 34, AutoSize = true };
        delete.Click += (_, _) => DeleteSelectedProfile();
        _profileLabel = new Label { AutoSize = true, Margin = new Padding(8, 11, 3, 3), Font = new Font(Font, FontStyle.Bold) };
        foreach (var c in new Control[] { _profiles, load, save, saveAs, delete, _profileLabel }) Tip(c, Tips.Profiles);

        var auto = new CheckBox { Text = "Auto profile", AutoSize = true, Margin = new Padding(8, 11, 3, 3) };
        BindCheck(auto, () => Cfg.AutoSwitchProfiles, v => Cfg.AutoSwitchProfiles = v);
        Tip(auto, Tips.AutoProfile);

        var onTop = new CheckBox { Text = "Always on top", AutoSize = true, Margin = new Padding(16, 11, 3, 3) };
        onTop.CheckedChanged += (_, _) => TopMost = onTop.Checked;
        Tip(onTop, Tips.AlwaysOnTop);

        _getSdk = new Button { Text = "⬇ Get MOZA SDK", Height = 34, AutoSize = true, Font = new Font(Font, FontStyle.Bold), BackColor = Color.Gold, Visible = false };
        _getSdk.Click += async (_, _) => await GetSdkAsync();
        Tip(_getSdk, Tips.GetSdk);

        bar.Controls.AddRange([_getSdk, _launch, _kill, _center, _motorTest, _record, _calibrate,
            new Label { Text = "Profile:", AutoSize = true, Margin = new Padding(16, 11, 0, 3) },
            _profiles, load, save, saveAs, delete, _profileLabel, auto, onTop]);
        return bar;
    }

    /// <summary>Runs the motor test from the toolbar or Force center; true when it found the motor direction.</summary>
    private async Task<bool> RunMotorTestAsync()
    {
        _motorTest.Enabled = false;
        Say("Motor test running. Hands off the wheel…");
        var (ok, msg) = await _host.Ffb.RunMotorTestAsync();
        if (ok) _host.SaveConfig();
        Say(ok ? msg + " (saved)" : msg);
        _motorTest.Enabled = true;
        return ok;
    }

    private Control BuildControls()
    {
        // Two pages at the top of the left side: all the force feedback tuning, and the controls (bindings) on their own.
        var top = new TabControl { Dock = DockStyle.Fill, Padding = new Point(24, 6) };
        var ffbPage = new TabPage("Force feedback");
        ffbPage.Controls.Add(BuildFfbPage());
        top.TabPages.Add(ffbPage);
        top.TabPages.Add(BuildBindingsTab());
        return top;
    }

    private Control BuildFfbPage()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(6) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Force feedback: master, output, layers.
        var ffb = new GroupBox { Text = "Force feedback", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var grid = NewGrid(withBar: true);
        _ffbStatus = new Label { AutoSize = true, Margin = new Padding(3, 4, 3, 6) };
        grid.Controls.Add(_ffbStatus, 0, grid.RowCount);
        grid.SetColumnSpan(_ffbStatus, 4);
        grid.RowCount++;

        _output = new CenterBar { Dock = DockStyle.Fill, Range = 1 };
        AddSlider(grid, "Master", Tips.Master, 0, 1, 0.01f, 2,
            () => Cfg.Ffb.MasterGain, v => Cfg.Ffb.MasterGain = v, bar: _output);
        AddSlider(grid, "Base strength (0 = auto)", Tips.BaseScale, 0, 1, 0.01f, 2,
            () => Cfg.Ffb.BaseScale, v =>
            {
                // Raising it above the automatic value on an untested base needs a deliberate yes.
                var ffbEngine = _host.Ffb;
                float auto = (ffbEngine.Base ?? Wheelbase.Unknown()).AutoScale;
                if (v > auto + 0.001f && Cfg.Ffb.BaseScale <= auto + 0.001f && ffbEngine.Base is not { Tested: true }
                    && MessageBox.Show(this, $"The automatic strength for this wheelbase is ×{auto:0.00}. Only the R3 has been tested.\n\n" +
                        "A higher value can make impacts and force center very strong on a powerful base. Keep your hands clear and raise it in small steps.\n\nContinue?",
                        "Base strength", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                {
                    BeginInvoke(new Action(RefreshAll));
                    return;
                }
                Cfg.Ffb.BaseScale = v;
            });
        AddCheckRow(grid, "FFB enabled", Tips.FfbEnabled,
            () => Cfg.Ffb.Enabled, v => Cfg.Ffb.Enabled = v);

        for (int i = 0; i < FfbModel.LayerOrder.Length; i++)
        {
            string name = FfbModel.LayerOrder[i];
            if (!Cfg.Ffb.Layers.TryGetValue(name, out var layer)) continue;
            var check = new CheckBox { Text = $"{name}", AutoSize = true, Anchor = AnchorStyles.Left };
            BindCheck(check, () => layer.Enabled, v => layer.Enabled = v);
            _layerBars[i] = new CenterBar { Dock = DockStyle.Fill, Range = 0.5f };
            Tip(_layerBars[i], Tips.LayerBar);
            AddSlider(grid, check, Tips.Layer.GetValueOrDefault(name, layer.Description), 0, 1.5f, 0.01f, 2, () => layer.Gain, v => layer.Gain = v, _layerBars[i]);
        }
        var resetLayers = new Button { Text = "Reset FFB to defaults", AutoSize = true };
        Tip(resetLayers, Tips.ResetFfb);
        resetLayers.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Reset every FFB gain, shape and behaviour setting to the built-in defaults? (Master stays as it is.)", "Reset FFB",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Cfg.Ffb.CopyFeelFrom(new FfbConfig());
            Cfg.Ffb.ResetNonFeel();
            RefreshAll();
            Changed();
            Say("FFB settings reset to defaults");
        };
        grid.Controls.Add(resetLayers, 0, grid.RowCount++);
        grid.SetColumnSpan(resetLayers, 2);
        ffb.Controls.Add(grid);
        root.Controls.Add(ffb, 0, 0);

        // Tabs: effect shape, behaviour, steering & pedals.
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildShapeTab());
        tabs.TabPages.Add(BuildSteeringTab());
        root.Controls.Add(tabs, 0, 1);
        return root;
    }

    private TabPage BuildShapeTab()
    {
        var page = new TabPage("Effect shape & behaviour") { AutoScroll = true, Padding = new Padding(6) };
        var grid = NewGrid(withBar: false);
        var f = Cfg.Ffb;
        AddSlider(grid, "Aligning peak slip °", Tips.AligningPeak,
            2, 40, 0.5f, 1, () => f.AligningPeakSlipDeg, v => f.AligningPeakSlipDeg = v);
        AddSlider(grid, "Load reference (g)", Tips.LoadReference,
            2, 40, 0.5f, 1, () => f.LoadReference, v => f.LoadReference = v);
        AddSlider(grid, "Lean reference", Tips.LeanReference,
            0.2f, 3, 0.05f, 2, () => f.LeanReference, v => f.LeanReference = v);
        AddSlider(grid, "Impact reference", Tips.ImpactReference,
            500, 10000, 100, 0, () => f.ImpactReference, v => f.ImpactReference = v);
        AddSlider(grid, "Impact decay ms", Tips.ImpactDecay,
            10, 300, 5, 0, () => f.ImpactDecayMs, v => f.ImpactDecayMs = v);
        AddSlider(grid, "Air centering", Tips.AirCentering,
            0, 1, 0.01f, 2, () => f.AirCentering, v => f.AirCentering = v);
        AddSlider(grid, "Drift lightening", Tips.DriftLightening,
            0, 1, 0.01f, 2, () => f.DriftLightening, v => f.DriftLightening = v);
        AddSlider(grid, "Wreck centering", Tips.WreckCentering,
            0, 1, 0.01f, 2, () => f.WreckCentering, v => f.WreckCentering = v);
        AddSlider(grid, "Stale telemetry ms", Tips.StaleMs,
            50, 500, 10, 0, () => f.StaleMs, v => f.StaleMs = v);
        AddSlider(grid, "Force center strength", Tips.CenterLockForce,
            0, 1, 0.05f, 2, () => f.CenterLockForce, v => f.CenterLockForce = v);
        AddSlider(grid, "Force center softness °", Tips.CenterLockFullAt,
            2, 60, 1, 0, () => f.CenterLockFullAtDeg, v => f.CenterLockFullAtDeg = v);
        AddSlider(grid, "Force center damping °/s", Tips.CenterLockDamping,
            50, 1500, 10, 0, () => f.CenterLockDampingDps, v => f.CenterLockDampingDps = v);
        AddCheckRow(grid, "Mute while the game is paused", Tips.MutePaused,
            () => f.MuteWhenPaused, v => f.MuteWhenPaused = v);
        AddCheckRow(grid, "Mute when not racing (menus, after finish)", Tips.MuteNotRacing,
            () => f.MuteWhenNotRacing, v => f.MuteWhenNotRacing = v);
        page.Controls.Add(grid);
        return page;
    }

    private TabPage BuildSteeringTab()
    {
        var page = new TabPage("Steering & pedals") { Padding = new Padding(6) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        var grid = NewGrid(withBar: false);
        AddSlider(grid, "Steering lock °", Tips.SteeringLock,
            90, 1080, 10, 0, () => Cfg.SteeringLockDegrees, v => Cfg.SteeringLockDegrees = v);
        AddSlider(grid, "Anti-deadzone", Tips.AntiDeadzone,
            0, 0.6f, 0.01f, 2, () => Cfg.SteeringAntiDeadzone, v => Cfg.SteeringAntiDeadzone = v);
        AddSlider(grid, "Linearity", Tips.Linearity,
            0.3f, 3, 0.05f, 2, () => Cfg.SteeringLinearity, v => Cfg.SteeringLinearity = v);
        AddSlider(grid, "Pedal deadzone", Tips.PedalDeadzone,
            0, 0.2f, 0.01f, 2, () => Cfg.PedalDeadzone, v => Cfg.PedalDeadzone = v);
        AddCheckRow(grid, "Invert steering", Tips.Invert, () => Cfg.InvertSteering, v => Cfg.InvertSteering = v);
        AddCheckRow(grid, "Send wheel to game (virtual pad output)", Tips.PadOutput,
            () => Cfg.PadOutputEnabled, v => Cfg.PadOutputEnabled = v);
        AddCheckRow(grid, "Pause wheel while Shade is on foot (hub)", Tips.PausePadOnFoot,
            () => Cfg.PausePadOnFoot, v => Cfg.PausePadOnFoot = v);
        _curve = new SteeringCurve(Cfg, () => _host.Wheel.State?.AngleDegrees) { Dock = DockStyle.Fill, MinimumSize = new Size(200, 160) };
        layout.Controls.Add(grid, 0, 0);
        layout.Controls.Add(_curve, 1, 0);
        page.Controls.Add(layout);
        return page;
    }

    private static TableLayoutPanel NewGrid(bool withBar)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = withBar ? 4 : 3, Padding = new Padding(2) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        if (withBar) t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        return t;
    }

    // ───────────────────────────── bindings ─────────────────────────────

    private void AddSlider(TableLayoutPanel grid, string label, string tip, float min, float max, float step, int decimals,
                           Func<float> get, Action<float> set, CenterBar? bar = null) =>
        AddSlider(grid, new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, tip, min, max, step, decimals, get, set, bar);

    private void AddSlider(TableLayoutPanel grid, Control lead, string tip, float min, float max, float step, int decimals,
                           Func<float> get, Action<float> set, CenterBar? bar)
    {
        int Ticks(float v) => (int)Math.Round(Math.Clamp(v, min, max) / step);
        var track = new TrackBar
        {
            Minimum = Ticks(min), Maximum = Ticks(max), TickStyle = TickStyle.None, AutoSize = false, Height = 26,
            Dock = DockStyle.Fill, SmallChange = 1, LargeChange = Math.Max(1, (Ticks(max) - Ticks(min)) / 20),
        };
        var num = new NumericUpDown
        {
            Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, DecimalPlaces = decimals,
            Width = 74, Anchor = AnchorStyles.Left, TextAlign = HorizontalAlignment.Right,
        };
        void Show(float v)
        {
            _syncing = true;
            track.Value = Math.Clamp(Ticks(v), track.Minimum, track.Maximum);
            num.Value = Math.Clamp((decimal)MathF.Round(v, decimals), num.Minimum, num.Maximum);
            _syncing = false;
        }
        track.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            float v = MathF.Round(track.Value * step, decimals);
            set(v);
            Show(v);
            Changed();
        };
        num.ValueChanged += (_, _) =>
        {
            if (_syncing) return;
            float v = (float)num.Value;
            set(v);
            Show(v);
            Changed();
        };
        Show(get());
        _refreshers.Add(() => Show(get()));
        Tip(lead, tip);
        Tip(track, tip);
        Tip(num, tip);
        if (bar is not null && string.IsNullOrEmpty(_tips.GetToolTip(bar))) Tip(bar, tip);

        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(lead, 0, row);
        grid.Controls.Add(track, 1, row);
        grid.Controls.Add(num, 2, row);
        if (bar is not null) { bar.Margin = new Padding(3, 5, 3, 5); grid.Controls.Add(bar, 3, row); }
    }

    private void AddCheckRow(TableLayoutPanel grid, string text, string tip, Func<bool> get, Action<bool> set)
    {
        var check = new CheckBox { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 6) };
        BindCheck(check, get, set);
        Tip(check, tip);
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(check, 0, row);
        grid.SetColumnSpan(check, 3);
    }

    private void BindCheck(CheckBox check, Func<bool> get, Action<bool> set)
    {
        check.Checked = get();
        check.CheckedChanged += (_, _) => { if (_syncing) return; set(check.Checked); Changed(); };
        _refreshers.Add(() => { _syncing = true; check.Checked = get(); _syncing = false; });
    }

    private void RefreshAll()
    {
        foreach (var r in _refreshers) r();
        _curve.Invalidate();
    }

    /// <summary>A setting changed: it is already live; schedule the autosave and update the profile state.</summary>
    private void Changed()
    {
        _autosave.Stop();
        _autosave.Start();
        UpdateProfileLabel();
        _curve.Invalidate();
    }

    // ───────────────────────────── profiles ─────────────────────────────

    private void LoadProfileList()
    {
        var names = _host.Profiles.Names();
        _profiles.Items.Clear();
        _profiles.Items.AddRange(names);
        if (Cfg.ActiveProfile is { } a && names.Contains(a)) _profiles.SelectedItem = a;
        else if (names.Length > 0) _profiles.SelectedIndex = 0;
    }

    private void LoadSelectedProfile()
    {
        if (_profiles.SelectedItem is not string name) { Say("No profile selected. Use Save as… to create one"); return; }
        if (IsModified() && MessageBox.Show(this, $"Discard the unsaved changes to \"{Cfg.ActiveProfile}\" and load \"{name}\"?",
                "Load profile", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        Say(_host.Profiles.Load(name));
        RefreshAll();
        Changed();
    }

    private void SaveProfile(string? name)
    {
        name ??= Prompt.Show(this, "Save profile", "Profile name (e.g. Speeder Bike):", Cfg.ActiveProfile ?? "");
        if (name is null) return;
        Say(_host.Profiles.Save(name));
        LoadProfileList();
        Changed();
    }

    private void DeleteSelectedProfile()
    {
        if (_profiles.SelectedItem is not string name) return;
        if (MessageBox.Show(this, $"Delete profile \"{name}\"? (The current settings stay as they are.)", "Delete profile",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        Say(_host.Profiles.Delete(name));
        LoadProfileList();
        Changed();
    }

    private bool IsModified() => _host.Profiles.Modified;

    private void UpdateProfileLabel()
    {
        _profileLabel.Text = Cfg.ActiveProfile is null ? "(no profile)" : Cfg.ActiveProfile + (IsModified() ? " (modified)" : "");
        _profileLabel.ForeColor = IsModified() ? Color.DarkOrange : SystemColors.ControlText;
    }

    // ───────────────────────────── live view ─────────────────────────────

    private void RefreshLive()
    {
        var ffb = _host.Ffb;
        if (_host.PollCalibration() is { } cal) { Say(cal); RefreshAll(); }
        if (_host.PollAutoProfile() is { } switched)
        {
            Say(switched);
            if (_profiles.Items.Contains(Cfg.ActiveProfile ?? "")) _profiles.SelectedItem = Cfg.ActiveProfile;
            RefreshAll();
            UpdateProfileLabel();
        }
        if ((_host.FrameError ?? ffb.LastError) is { } fe && fe != _lastFrameError) { _lastFrameError = fe; Say(fe); }
        _getSdk.Visible = _host.Wheel.SdkMissing || _sdkBusy;
        if (!_sdkPrompted && _host.Wheel.SdkMissing) { _sdkPrompted = true; BeginInvoke(new Action(async () => await GetSdkAsync())); }
        // First run: once the wheel is readable, walk the user through the motor test, pedals and controls.
        if (!Cfg.SetupDone && !_setupShown && !_sdkBusy && _host.Wheel.State is not null) { _setupShown = true; BeginInvoke(new Action(ShowSetup)); }
        if (ffb.CenterLockFault is { } fault)
        {
            ffb.CenterLockFault = null;
            Say(fault);
            BeginInvoke(new Action(() => MessageBox.Show(this, fault, "Force center", MessageBoxButtons.OK, MessageBoxIcon.Warning)));
        }
        if (_host.Pad.SlotWarning != _slotWarning)
        {
            Say(_host.Pad.SlotWarning ?? "The wheel is player 1 again");
            _slotWarning = _host.Pad.SlotWarning;
        }
        UpdateLaunchButton();

        _kill.Text = ffb.Killed ? "FFB KILLED: resume" : "&KILL FFB";
        _kill.BackColor = ffb.Killed ? Color.Gold : Color.IndianRed;
        _kill.ForeColor = ffb.Killed ? Color.Black : Color.White;
        _center.Text = ffb.CenterLock ? "&CENTER PINNED: release" : "Force &center";
        _center.BackColor = ffb.CenterLock ? Color.SteelBlue : SystemColors.Control;
        _center.ForeColor = ffb.CenterLock ? Color.White : SystemColors.ControlText;
        if (_centerShown && !ffb.CenterLock && ffb.CenterLockAutoReleased) Say("Force center released: race started");
        _centerShown = ffb.CenterLock;
        _record.Text = _host.Recorder is { } rec ? $"■ Stop ({rec.Rows})" : "● Record";
        _calibrate.Text = _host.Calibrator.Active ? $"Calibrating… {_host.Calibrator.SecondsLeft:F0}s" : "Calibrate pedals";

        string flags = (ffb.Airborne ? "  AIRBORNE" : "") + (ffb.Wrecked ? "  WRECKED" : "");
        string power = ffb.Killed ? "KILLED" : !Cfg.Ffb.Enabled ? "disabled" : ffb.NeedsMotorTest ? "OFF until the Motor test has run" : "on";
        _ffbStatus.Text = $"{power} | {ffb.DeviceStatus} | {ffb.LoopHz:F0} Hz | " +
                          $"sign {Cfg.Ffb.ForceSign:+0;-0} | state: {ffb.ModelState}{flags}" + Environment.NewLine +
                          $"Wheelbase: {(ffb.Base is { } wb ? wb.Describe(ffb.BaseScale) : "not detected yet")}" +
                          (Cfg.Ffb.BaseScale > 0 ? " (set by you)" : " (auto)");
        _output.Value = ffb.LastTorque * Cfg.Ffb.ForceSign;   // show in "positive = right" terms
        for (int i = 0; i < _layerBars.Length; i++)
            if (_layerBars[i] is { } b) b.Value = ffb.Contributions[i];

        var vehicle = _host.Dashboard.CurrentVehicle;
        Text = $"SWGR Wheel Bridge | {(vehicle is { } v ? v.ToString() : "no vehicle")} | profile: {Cfg.ActiveProfile ?? "none"}";

        _monitor.Content = _host.Dashboard.StatusText(_host.Receiver, _host.Recorder) + Environment.NewLine
            + WheelPanel.Render(_host.Wheel, _host.Pad, Cfg, _host.Calibrator, _host.Dashboard.GameSteer) + Environment.NewLine
            + _host.Dashboard.TelemetryText();
        _curve.Invalidate();
        TickBindings();
    }

    // ───────────────────────────── MOZA SDK ─────────────────────────────

    private bool _sdkPrompted;

    /// <summary>
    /// The MOZA SDK can't be shipped with the bridge, so the user gets it from MOZA: download (from MOZA's own
    /// server) or install from a MOZA_SDK.zip they downloaded themselves.
    /// </summary>
    private async Task GetSdkAsync()
    {
        if (_sdkBusy || MozaSdk.Available) return;
        var choice = MessageBox.Show(this,
            "The bridge needs the MOZA SDK to read your wheel. MOZA does not allow it to be shipped with the bridge, " +
            "so you get it from MOZA:\n\n" +
            $"Yes: download it now from MOZA ({MozaSdk.DownloadUrl}, about 57 MB). Only the three 64-bit SDK files are kept.\n" +
            $"No: choose a MOZA_SDK.zip that you downloaded from {MozaSdk.SdkPage}.\n" +
            "Cancel: not now.\n\nBy installing it you use MOZA's software under MOZA's terms.",
            "Get the MOZA SDK", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
        if (choice == DialogResult.Cancel) return;

        bool Confirm(string what) => MessageBox.Show(this, what + "\n\nMOZA may have released a newer SDK. It might not work with the bridge. Install it anyway?",
            "MOZA SDK version", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;

        MozaSdk.InstallResult result;
        _sdkBusy = true;
        _getSdk.Enabled = false;
        try
        {
            if (choice == DialogResult.Yes)
                result = await MozaSdk.DownloadAsync(Confirm, new Progress<string>(Say));
            else
            {
                using var open = new OpenFileDialog { Title = "Choose MOZA_SDK.zip", Filter = "MOZA SDK (*.zip)|*.zip", FileName = "MOZA_SDK.zip" };
                if (open.ShowDialog(this) != DialogResult.OK) return;
                result = MozaSdk.InstallFromZip(open.FileName, Confirm);
            }
        }
        finally { _sdkBusy = false; _getSdk.Enabled = true; }
        Say(result.Message);
        if (!result.Ok) MessageBox.Show(this, result.Message, "MOZA SDK", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ───────────────────────────── controls (bindings) ─────────────────────────────

    private sealed class BindRow
    {
        public required string Control;
        public required Label Bound, Live;
        public required Button Bind;
    }

    private readonly List<BindRow> _bindRows = [];
    private BindCapture? _capture;
    private Label _bindHint = null!;
    private static readonly TimeSpan CaptureTime = TimeSpan.FromSeconds(6);
    private bool _setupShown;
    private string? _slotWarning;

    private TabPage BuildBindingsTab()
    {
        var page = new TabPage("Controls") { AutoScroll = true, Padding = new Padding(6) };
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 6, Padding = new Padding(2) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));    // control
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));   // game function
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));   // pedal (triggers only)
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));    // bound to
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // bind / clear
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));    // live

        var setup = new Button { Text = "Run setup…", AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
        setup.Click += (_, _) => ShowSetup();
        Tip(setup, Tips.RunSetup);
        t.Controls.Add(setup, 0, t.RowCount);
        t.SetColumnSpan(setup, 2);
        t.RowCount++;

        _bindHint = new Label { AutoSize = true, Margin = new Padding(3, 4, 3, 8), Text = Tips.Format(Tips.BindHint, 120) };
        t.Controls.Add(_bindHint, 0, t.RowCount);
        t.SetColumnSpan(_bindHint, 6);
        t.RowCount++;

        void Header(params string[] cols)
        {
            for (int c = 0; c < cols.Length; c++)
                t.Controls.Add(new Label { Text = cols[c], AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) }, c, t.RowCount);
            t.RowCount++;
        }
        Header("Control", "In game", "Pedal", "Bound to", "", "");

        foreach (var control in PadMapping.Triggers.Concat(PadMapping.Buttons))
        {
            bool trigger = control is "RT" or "LT";
            int row = t.RowCount++;
            t.Controls.Add(new Label { Text = control, AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 0, row);
            t.Controls.Add(new Label { Text = PadMapping.GameFunction.GetValueOrDefault(control, ""), AutoSize = true, Anchor = AnchorStyles.Left }, 1, row);
            if (trigger)
            {
                var pedal = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, Anchor = AnchorStyles.Left };
                pedal.Items.AddRange(new object[] { "Throttle", "Brake", "Clutch", "None" });
                Func<string> get = control == "RT" ? () => Cfg.RightTriggerPedal : () => Cfg.LeftTriggerPedal;
                Action<string> set = control == "RT" ? v => Cfg.RightTriggerPedal = v : v => Cfg.LeftTriggerPedal = v;
                void ShowPedal() { _syncing = true; pedal.SelectedItem = pedal.Items.Contains(get()) ? get() : "None"; _syncing = false; }
                ShowPedal();
                _refreshers.Add(ShowPedal);
                pedal.SelectedIndexChanged += (_, _) => { if (_syncing) return; set((string)pedal.SelectedItem!); Changed(); };
                Tip(pedal, Tips.TriggerPedal);
                t.Controls.Add(pedal, 2, row);
            }
            var bound = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
            t.Controls.Add(bound, 3, row);

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            var bind = new Button { Text = "Bind…", AutoSize = true };
            var clear = new Button { Text = "Clear", AutoSize = true };
            bind.Click += (_, _) => StartCapture(control);
            clear.Click += (_, _) =>
            {
                PadMapping.ClearBinding(Cfg, control);
                if (_capture?.Control == control) _capture = null;
                Changed();
                Say($"{control}: cleared{(trigger ? " (the pedal stays; set it to None to remove it)" : "")}");
            };
            Tip(bind, trigger ? Tips.BindTrigger : Tips.BindButton);
            Tip(clear, Tips.ClearBinding);
            buttons.Controls.AddRange([bind, clear]);
            t.Controls.Add(buttons, 4, row);

            var live = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.SeaGreen, Font = new Font(Font, FontStyle.Bold) };
            t.Controls.Add(live, 5, row);
            _bindRows.Add(new BindRow { Control = control, Bound = bound, Bind = bind, Live = live });
        }

        var reset = new Button { Text = "Clear all bindings", AutoSize = true, Margin = new Padding(3, 10, 3, 3) };
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Remove every button binding? (The trigger pedals stay as they are.)", "Clear all bindings",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            Cfg.Buttons = PadMapping.Buttons.ToDictionary(b => b, _ => Array.Empty<int>());
            Cfg.TriggerButtons = PadMapping.Triggers.ToDictionary(b => b, _ => Array.Empty<int>());
            Cfg.PedalButtons = [];
            Changed();
            Say("All button bindings cleared");
        };
        t.Controls.Add(reset, 0, t.RowCount++);
        t.SetColumnSpan(reset, 3);
        page.Controls.Add(t);
        return page;
    }

    private void StartCapture(string control)
    {
        if (_host.Wheel.State is not { } s) { Say("Can't bind: no wheel data. Is Pit House running?"); return; }
        _capture = new BindCapture(control, s, Cfg, CaptureTime);
        bool trigger = control is "RT" or "LT";
        Say(trigger ? $"{control}: press the wheel button that should press {control} fully…"
                    : $"{control}: press a wheel button, or push a pedal past half way…");
    }

    /// <summary>Runs at the UI rate: live indicators, binding text, and the capture of a new binding.</summary>
    private void TickBindings()
    {
        var s = _host.Wheel.State;
        var down = new bool[PadMapping.Buttons.Length];
        float rt = 0, lt = 0;
        if (s is not null) (_, rt, lt) = PadMapping.Map(s, Cfg, down);

        if (_capture is { } cap && s is not null)
        {
            var (button, pedal) = cap.Poll(s, Cfg);
            if (button is { } nb) { _capture = null; Say(PadMapping.BindButton(Cfg, cap.Control, nb)); Changed(); }
            else if (pedal is not null) { _capture = null; Say(PadMapping.BindPedal(Cfg, cap.Control, pedal)); Changed(); }
            else if (cap.TimedOut) { _capture = null; Say($"{cap.Control}: nothing pressed, binding cancelled"); }
        }

        foreach (var r in _bindRows)
        {
            r.Bound.Text = PadMapping.Describe(r.Control, Cfg);
            r.Bind.Text = _capture is { Until: { } until } c && c.Control == r.Control
                ? $"Press… {Math.Max(0, (until - DateTime.UtcNow).TotalSeconds):F0}s" : "Bind…";
            int bi = Array.IndexOf(PadMapping.Buttons, r.Control);
            r.Live.Text = r.Control switch
            {
                "RT" => rt > 0.02f ? $"{rt:P0}" : "",
                "LT" => lt > 0.02f ? $"{lt:P0}" : "",
                _ => bi >= 0 && down[bi] ? "●" : "",
            };
        }
    }

    /// <summary>The guided setup: motor test, pedal calibration, every control. Shown by itself on the first run.</summary>
    private void ShowSetup()
    {
        _setupShown = true;
        _capture = null;
        using var wizard = new SetupWizard(_host, () => { RefreshAll(); Changed(); }, Say);
        wizard.ShowDialog(this);
        _host.SaveConfig();
        RefreshAll();
        UpdateProfileLabel();
    }

    private void LaunchGame()
    {
        if (IsGameRunning()) { Say("The game is already running"); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"steam://rungameid/{SteamAppId}") { UseShellExecute = true });
            _launchedAt = DateTime.UtcNow;
            Say("Launching STAR WARS: Galactic Racer through Steam…");
        }
        catch (Exception ex) { Say("Couldn't launch the game: " + ex.Message); }
    }

    private bool IsGameRunning()
    {
        var procs = System.Diagnostics.Process.GetProcessesByName(GameProcess);
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    private void UpdateLaunchButton()
    {
        if (DateTime.UtcNow < _nextGameCheck) return;   // process scan once a second, not at the 20 Hz UI rate
        _nextGameCheck = DateTime.UtcNow.AddSeconds(1);
        _gameRunning = IsGameRunning();
        bool starting = !_gameRunning && (DateTime.UtcNow - _launchedAt).TotalSeconds < 30;
        _launch.Enabled = !_gameRunning && !starting;
        _launch.Text = _gameRunning ? "Game running" : starting ? "Starting…" : "▶ Launch game";
    }

    private void Tip(Control c, string text) => _tips.SetToolTip(c, Tips.Format(text));

    private void Say(string msg) => _message.Text = $"{DateTime.Now:HH:mm:ss}  {msg}";
}
