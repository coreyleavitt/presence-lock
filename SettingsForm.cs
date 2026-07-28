namespace PresenceLock;

sealed class SettingsForm : Form
{
    readonly NumericUpDown away = new() { Minimum = 1, Maximum = 300, DecimalPlaces = 1, Increment = 0.5m, Width = 80 };
    readonly NumericUpDown inputIdle = new() { Minimum = 0, Maximum = 600, DecimalPlaces = 1, Increment = 0.5m, Width = 80 };
    readonly NumericUpDown grace = new() { Minimum = 0, Maximum = 600, DecimalPlaces = 1, Increment = 0.5m, Width = 80 };
    readonly NumericUpDown sampleMs = new() { Minimum = 100, Maximum = 10000, Increment = 100, Width = 80 };
    readonly NumericUpDown darkThreshold = new() { Minimum = 0, Maximum = 255, DecimalPlaces = 1, Width = 80 };
    readonly ComboBox camera = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 280 };

    public Config? Result { get; private set; }

    public SettingsForm(Config current, IReadOnlyList<string> cameraNames)
    {
        Text = "PresenceLock Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        away.Value = (decimal)Math.Clamp(current.AwayThresholdSeconds, 1, 300);
        inputIdle.Value = (decimal)Math.Clamp(current.InputIdleSeconds, 0, 600);
        grace.Value = (decimal)Math.Clamp(current.GraceSeconds, 0, 600);
        sampleMs.Value = Math.Clamp(current.SampleIntervalMs, 100, 10000);
        darkThreshold.Value = (decimal)Math.Clamp(current.DarkFrameMeanThreshold, 0, 255);
        camera.Items.AddRange(cameraNames.ToArray());
        camera.Text = current.CameraNameContains;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 8, 12, 3),
            });
            control.Anchor = AnchorStyles.Left;
            grid.Controls.Add(control);
        }
        Row("Lock after face gone (s)", away);
        Row("Require input idle (s)", inputIdle);
        Row("Grace after start/unlock (s)", grace);
        Row("Sample interval (ms)", sampleMs);
        Row("Dark-frame threshold (0–255)", darkThreshold);
        Row("Camera name filter", camera);

        var hint = new Label
        {
            Text = "Blank camera filter = automatic (prefers an external USB camera).",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(3, 0, 3, 10),
        };

        var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, _) => Result = new Config
        {
            AwayThresholdSeconds = (double)away.Value,
            InputIdleSeconds = (double)inputIdle.Value,
            GraceSeconds = (double)grace.Value,
            SampleIntervalMs = (int)sampleMs.Value,
            DarkFrameMeanThreshold = (double)darkThreshold.Value,
            CameraNameContains = camera.Text.Trim(),
            // Not dialog-editable in this RFC (rfc-core-brain.md: "SettingsForm.cs is not
            // extended") — carried forward unchanged so a Settings save can never silently
            // reset a file-tuned PolicyConfig value back to its built-in default.
            NoSignalReportAfterMs = current.NoSignalReportAfterMs,
            ReevaluateAfterMs = current.ReevaluateAfterMs,
            ReevaluateCooldownMs = current.ReevaluateCooldownMs,
            RecoveryFailureThreshold = current.RecoveryFailureThreshold,
            RecoveryCooldownMs = current.RecoveryCooldownMs,
        };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Bottom,
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
        };
        stack.Controls.Add(grid);
        stack.Controls.Add(hint);
        stack.Controls.Add(buttons);
        Controls.Add(stack);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}
