using System.Drawing;
using System.Windows.Forms;

namespace WheelBridge.Gui;

/// <summary>
/// Guided setup, shown by itself on the first run and from the Controls tab: the motor test (no force is sent until
/// it has found which way the motor turns), pedal calibration when pedals are there, then every control bound one
/// at a time. Settings apply as they are made; Finish only marks the setup as done.
/// </summary>
public sealed class SetupWizard : Form
{
    private enum Step { Motor, Pedals, Controls, Done }

    // The order the controls are asked for: what a race needs first, the ones the game doesn't use last.
    private static readonly string[] BindOrder = ["RT", "LT", "A", "X", "B", "LB", "RS", "RB", "Start", "Up", "Down", "Left", "Right", "Y", "LS", "Back"];
    private const int PedalMoved = 2000;   // raw travel that counts as "this pedal is there" (calibration needs the same)

    private readonly BridgeHost _host;
    private readonly Action _changed;
    private readonly Action<string> _say;
    private BridgeConfig Cfg => _host.Config;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };

    private readonly Label _title, _body, _result, _live;
    private readonly Button _action, _back, _skip, _next, _later;
    private Step _step;
    private bool _busy;

    // Pedals: the range each raw axis has covered since the step opened, and the calibration in progress.
    private int[] _pedalMin = [], _pedalMax = [];
    private (PedalRange?, PedalRange?, PedalRange?)? _calibratingFrom;

    // Controls: the list being asked, the current one, and the capture waiting for its input.
    private List<string> _controls = [];
    private int _index;
    private BindCapture? _capture;
    private DateTime _advanceAt = DateTime.MaxValue;

    public SetupWizard(BridgeHost host, Action changed, Action<string> say)
    {
        _host = host;
        _changed = changed;
        _say = say;
        Text = "Wheel Bridge setup";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 600);

        _title = new Label { AutoSize = true, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Margin = new Padding(3, 3, 3, 10) };
        _body = new Label { AutoSize = true, MaximumSize = new Size(680, 0), Margin = new Padding(3, 3, 3, 12) };
        _action = new Button { AutoSize = true, Height = 34, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 3, 3, 8) };
        _result = new Label { AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = Color.SeaGreen, Font = new Font(Font, FontStyle.Bold) };
        _live = new Label { AutoSize = true, MaximumSize = new Size(680, 0), Font = new Font("Consolas", 9.5f), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 12, 3, 3) };
        var content = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16) };
        content.Controls.AddRange([_title, _body, _action, _result, _live]);

        _next = new Button { AutoSize = true, Height = 32, MinimumSize = new Size(100, 0) };
        _skip = new Button { AutoSize = true, Height = 32, MinimumSize = new Size(100, 0) };
        _back = new Button { Text = "Back", AutoSize = true, Height = 32, MinimumSize = new Size(90, 0) };
        _later = new Button { Text = "Set up later", AutoSize = true, Height = 32 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        buttons.Controls.AddRange([_next, _skip, _back, _later]);
        _later.Margin = new Padding(3, 3, 60, 3);

        Controls.Add(content);
        Controls.Add(buttons);

        _action.Click += async (_, _) => await OnAction();
        _next.Click += (_, _) => OnNext();
        _skip.Click += (_, _) => OnSkip();
        _back.Click += (_, _) => OnBack();
        _later.Click += (_, _) => Close();
        FormClosing += (_, e) => { if (_busy) e.Cancel = true; };   // never close in the middle of the motor test
        FormClosed += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => Tick();

        GoTo(Step.Motor);
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    // ───────────────────────────── steps ─────────────────────────────

    private void GoTo(Step step)
    {
        _step = step;
        _capture = null;
        _advanceAt = DateTime.MaxValue;
        _result.Text = "";
        _result.ForeColor = Color.SeaGreen;
        // Each step's Tick decides what is enabled; start every step with everything usable.
        foreach (var b in new[] { _action, _skip, _next, _later }) b.Enabled = true;
        _back.Enabled = step != Step.Motor;
        _live.Text = "";
        _skip.Visible = true;
        _next.Visible = true;
        _action.Visible = true;
        switch (step)
        {
            case Step.Motor:
                _title.Text = "1 of 3: Motor test";
                _body.Text = "The bridge needs to know which way your wheelbase's motor turns. Until this test has run it sends no force feedback: " +
                             "with the wrong direction, every force would push the wheel away from center instead of toward it.\n\n" +
                             "Take your hands off the wheel and click Run motor test. The wheel turns a little to one side for a moment.";
                _action.Text = "Run motor test";
                _skip.Text = "Skip";
                _next.Text = "Next";
                if (!_host.Ffb.NeedsMotorTest)
                    _result.Text = $"Already done for this wheel (ForceSign {Cfg.Ffb.ForceSign:+0;-0}). Run it again if you changed wheelbase.";
                break;

            case Step.Pedals:
                _title.Text = "2 of 3: Pedals";
                _body.Text = "Press any pedal to check that it is detected. Then release all pedals, click Calibrate pedals, " +
                             $"and press each pedal fully once within {PedalCalibrator.WindowSeconds:F0} seconds.\n\n" +
                             "No pedals? Click No pedals. In the next step you bind accelerate and brake (RT and LT) to wheel buttons or paddles.";
                _action.Text = "Calibrate pedals";
                _skip.Text = "No pedals";
                _next.Text = "Next";
                var s = _host.Wheel.State;
                int[] raw = s is null ? [0, 0, 0] : [s.RawThrottle, s.RawBrake, s.RawClutch];
                _pedalMin = [.. raw];
                _pedalMax = [.. raw];
                if (Cfg.Throttle is not null || Cfg.Brake is not null) _result.Text = "Already calibrated. Calibrate again if a pedal doesn't reach 100% or 0%.";
                break;

            case Step.Controls:
                // A trigger driven by a calibrated pedal needs no button; one without a pedal (no pedals) does.
                _controls = BindOrder.Where(c => c switch
                {
                    "RT" => !HasPedal(Cfg.RightTriggerPedal),
                    "LT" => !HasPedal(Cfg.LeftTriggerPedal),
                    _ => true,
                }).ToList();
                _index = Math.Clamp(_index, 0, _controls.Count - 1);
                _action.Visible = false;
                _next.Visible = false;
                _skip.Text = "Skip";
                ShowControl();
                break;

            case Step.Done:
                _title.Text = "All set";
                _action.Visible = false;
                _skip.Visible = false;
                _next.Text = "Finish";
                string motor = _host.Ffb.NeedsMotorTest
                    ? "not tested yet: no force feedback until you run the Motor test (toolbar)"
                    : $"tested (ForceSign {Cfg.Ffb.ForceSign:+0;-0})";
                string pedals = HasPedal(Cfg.RightTriggerPedal) || HasPedal(Cfg.LeftTriggerPedal) ? "calibrated" : "none (triggers on buttons)";
                var lines = PadMapping.Triggers.Concat(PadMapping.Buttons)
                    .Select(c => $"  {c,-6} {PadMapping.GameFunction.GetValueOrDefault(c, ""),-22} {PadMapping.Describe(c, Cfg)}");
                _body.Text = $"Motor direction: {motor}\nPedals: {pedals}\n\nYou can change all of this later: bindings and Run setup on the Controls tab, " +
                             "Motor test and Calibrate pedals in the toolbar.";
                _live.Text = string.Join("\n", lines);
                break;
        }
    }

    private bool HasPedal(string pedal) => pedal switch
    {
        "Throttle" => Cfg.Throttle is not null,
        "Brake" => Cfg.Brake is not null,
        "Clutch" => Cfg.Clutch is not null,
        _ => false,
    };

    private void ShowControl()
    {
        string c = _controls[_index];
        string function = PadMapping.GameFunction.GetValueOrDefault(c, "");
        bool trigger = c is "RT" or "LT";
        bool pedals = Cfg.Throttle is not null || Cfg.Brake is not null || Cfg.Clutch is not null;
        _title.Text = $"3 of 3: Controls ({_index + 1} of {_controls.Count})";
        _body.Text = (function.Length > 0
                         ? $"Press the wheel button for {function} (Xbox {c})" + (!trigger && pedals ? ", or push a pedal past half way." : ".")
                         : $"Xbox {c} is not used by the game by default. Press a wheel button if you want it, or click Skip.")
                     + (trigger ? $"\n\nNo calibrated pedal drives {c}, so bind a button (a paddle, for example). It presses {c} fully while held." : "")
                     + $"\n\nNow: {PadMapping.Describe(c, Cfg)}. Pressing a button replaces that; Skip keeps it.";
        _result.Text = "";
        _capture = _host.Wheel.State is { } s ? new BindCapture(c, s, Cfg, timeout: null) : null;
        _advanceAt = DateTime.MaxValue;
    }

    // ───────────────────────────── buttons ─────────────────────────────

    private async Task OnAction()
    {
        if (_busy) return;
        if (_step == Step.Motor)
        {
            _busy = true;
            foreach (var b in new[] { _action, _back, _skip, _next, _later }) b.Enabled = false;
            _result.ForeColor = SystemColors.ControlText;
            _result.Text = "Running. Hands off the wheel…";
            var (ok, msg) = await _host.Ffb.RunMotorTestAsync();
            if (ok) { _host.SaveConfig(); _changed(); }
            _result.ForeColor = ok ? Color.SeaGreen : Color.Firebrick;
            _result.Text = ok ? msg + ". Saved." : msg;
            _say(msg);
            foreach (var b in new[] { _action, _skip, _next, _later }) b.Enabled = true;
            _busy = false;
        }
        else if (_step == Step.Pedals)
        {
            if (_host.Calibrator.Active) return;
            _calibratingFrom = (Cfg.Throttle, Cfg.Brake, Cfg.Clutch);
            _result.ForeColor = SystemColors.ControlText;
            _result.Text = _host.BeginCalibration();
        }
    }

    private void OnNext()
    {
        switch (_step)
        {
            case Step.Motor: GoTo(Step.Pedals); break;
            case Step.Pedals: GoTo(Step.Controls); break;
            case Step.Done:
                Cfg.SetupDone = true;
                _changed();
                DialogResult = DialogResult.OK;
                Close();
                break;
        }
    }

    private void OnSkip()
    {
        switch (_step)
        {
            case Step.Motor: GoTo(Step.Pedals); break;
            case Step.Pedals:
                // No pedals: the triggers go on buttons, asked for in the next step.
                Cfg.RightTriggerPedal = "None";
                Cfg.LeftTriggerPedal = "None";
                _changed();
                GoTo(Step.Controls);
                break;
            case Step.Controls: NextControl(); break;
        }
    }

    private void OnBack()
    {
        switch (_step)
        {
            case Step.Pedals: GoTo(Step.Motor); break;
            case Step.Controls:
                if (_index == 0) GoTo(Step.Pedals);
                else { _index--; ShowControl(); }
                break;
            case Step.Done: _index = _controls.Count - 1; GoTo(Step.Controls); break;
        }
    }

    private void NextControl()
    {
        if (_index + 1 >= _controls.Count) { GoTo(Step.Done); return; }
        _index++;
        ShowControl();
    }

    // ───────────────────────────── live ─────────────────────────────

    private void Tick()
    {
        var s = _host.Wheel.State;
        switch (_step)
        {
            case Step.Motor:
                _next.Enabled = !_busy && !_host.Ffb.NeedsMotorTest;
                _action.Enabled = !_busy && _host.Ffb.Ready && s is not null;
                _live.Text = s is null ? $"No wheel data: {_host.Wheel.Status}"
                    : $"Wheel {s.AngleDegrees,7:F1}°   FFB: {(_host.Ffb.Ready ? "ready" : _host.Ffb.DeviceStatus)}";
                break;

            case Step.Pedals:
                TickPedals(s);
                break;

            case Step.Controls:
                TickControl(s);
                break;
        }
    }

    private void TickPedals(WheelState? s)
    {
        if (_host.PollCalibration() is { } done) _say(done);
        if (_calibratingFrom is { } from && !_host.Calibrator.Active)
        {
            _calibratingFrom = null;
            bool changed = !ReferenceEquals(from.Item1, Cfg.Throttle) || !ReferenceEquals(from.Item2, Cfg.Brake) || !ReferenceEquals(from.Item3, Cfg.Clutch);
            _result.ForeColor = changed ? Color.SeaGreen : Color.Firebrick;
            _result.Text = changed ? "Calibrated and saved." : "No pedal moved enough. Release all pedals, click Calibrate pedals, then press each one fully.";
            if (changed)
            {
                // Pedals after all (No pedals was clicked earlier): put the triggers back on them.
                if (Cfg.RightTriggerPedal == "None" && Cfg.Throttle is not null) Cfg.RightTriggerPedal = "Throttle";
                if (Cfg.LeftTriggerPedal == "None" && Cfg.Brake is not null) Cfg.LeftTriggerPedal = "Brake";
                _changed();
            }
        }
        _action.Enabled = s is not null && !_host.Calibrator.Active;
        _action.Text = _host.Calibrator.Active ? $"Press each pedal fully… {_host.Calibrator.SecondsLeft:F0}s" : "Calibrate pedals";
        _next.Enabled = !_host.Calibrator.Active && (Cfg.Throttle is not null || Cfg.Brake is not null);
        _skip.Enabled = !_host.Calibrator.Active;
        if (s is null) { _live.Text = $"No wheel data: {_host.Wheel.Status}"; return; }

        int[] raw = [s.RawThrottle, s.RawBrake, s.RawClutch];
        var names = PadMapping.Pedals;
        var moved = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            _pedalMin[i] = Math.Min(_pedalMin[i], raw[i]);
            _pedalMax[i] = Math.Max(_pedalMax[i], raw[i]);
            if (_pedalMax[i] - _pedalMin[i] > PedalMoved) moved.Add(names[i].ToLowerInvariant());
        }
        string detected = moved.Count > 0 ? $"Pedals detected: {string.Join(", ", moved)}" : "No pedal movement yet: press a pedal";
        _live.Text = $"{detected}\n" + string.Join("\n", names.Select((n, i) =>
            $"{n,-9} raw {raw[i],7}   {(HasPedal(n) ? $"{PadMapping.PedalValue(n, s, Cfg),4:P0}" : "not calibrated")}"));
    }

    private void TickControl(WheelState? s)
    {
        if (s is null) { _live.Text = $"No wheel data: {_host.Wheel.Status}"; _capture = null; return; }
        string c = _controls[_index];
        if (DateTime.UtcNow >= _advanceAt) { NextControl(); return; }
        // (Re)arm after the wheel data comes back; not during the pause after a binding.
        if (_capture is null && _advanceAt == DateTime.MaxValue) _capture = new BindCapture(c, s, Cfg, timeout: null);
        if (_capture is { } cap)
        {
            var (button, pedal) = cap.Poll(s, Cfg);
            string? msg = button is { } b ? PadMapping.BindButton(Cfg, c, b, replace: true)
                        : pedal is not null ? PadMapping.BindPedal(Cfg, c, pedal, replace: true) : null;
            if (msg is not null)
            {
                _capture = null;
                _changed();
                _result.ForeColor = msg.Contains("note:") ? Color.DarkOrange : Color.SeaGreen;
                _result.Text = msg;
                _say(msg);
                // A short pause so the result can be read before the next control.
                _advanceAt = DateTime.UtcNow.AddMilliseconds(msg.Contains("note:") ? 1800 : 700);
            }
        }
        _live.Text = $"Wheel buttons held: {(s.Pressed.Count == 0 ? "none" : string.Join(' ', s.Pressed.Order()))}";
    }
}
