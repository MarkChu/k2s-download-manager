using K2sDownloaderWinForms.Core;

namespace K2sDownloaderWinForms.Forms;

public sealed class CaptchaForm : Form
{
    private readonly byte[]        _imageBytes;
    private readonly TextBox       _inputBox;
    private readonly Button        _submitButton;
    private readonly Button        _cancelButton;
    private          Button?       _autoSolveButton;
    private readonly Action<string>? _log;

    public string CaptchaResponse { get; private set; } = string.Empty;

    public CaptchaForm(byte[] imageBytes, Action<string>? log = null)
    {
        _imageBytes = imageBytes;
        _log        = log;

        Text            = "Captcha required";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;
        Padding         = new Padding(20);
        Font            = new Font("Segoe UI", 9.5f);

        var layout = new TableLayoutPanel
        {
            ColumnCount  = 1,
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock         = DockStyle.Fill,
            Padding      = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Row(Control c, int topMargin = 0)
        {
            c.Margin = new Padding(0, topMargin, 0, 0);
            layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(c);
        }

        // Instruction label
        Row(new Label
        {
            Text      = "Enter the text shown in the image below:",
            AutoSize  = true,
            ForeColor = Color.FromArgb(60, 60, 60),
        });

        // Captcha image
        using var ms = new MemoryStream(imageBytes);
        var img = Image.FromStream(ms);
        var imageBox = new PictureBox
        {
            Image       = img,
            SizeMode    = PictureBoxSizeMode.AutoSize,
            BorderStyle = BorderStyle.FixedSingle,
        };
        Row(imageBox, topMargin: 10);

        int minWidth = Math.Max(img.Width, 260);

        // Input box
        _inputBox = new TextBox
        {
            Width  = minWidth,
            Font   = new Font("Segoe UI", 12f),
        };
        _inputBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Return) Submit();
            if (e.KeyCode == Keys.Escape) CancelDialog();
        };
        Row(_inputBox, topMargin: 10);

        // ── Button row ────────────────────────────────────────────────────────
        // Layout: [Auto-solve (optional)]  ----stretch----  [Cancel]  [Submit]
        var buttonRow = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize    = true,
            Width       = minWidth,
            Margin      = new Padding(0, 10, 0, 0),
            Padding     = new Padding(0),
        };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // auto-solve
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); // stretch
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));   // cancel+submit
        buttonRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        buttonRow.RowCount = 1;

        // Auto-solve button (only if API key is configured)
        if (!string.IsNullOrWhiteSpace(AppSettings.Current.GeminiApiKey))
        {
            _autoSolveButton = new Button
            {
                Text      = "🤖 Auto-solve",
                AutoSize  = true,
                Padding   = new Padding(10, 4, 10, 4),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(230, 245, 235),
                ForeColor = Color.FromArgb(30, 120, 60),
            };
            _autoSolveButton.FlatAppearance.BorderSize = 1;
            _autoSolveButton.FlatAppearance.BorderColor = Color.FromArgb(150, 200, 160);
            _autoSolveButton.Click += AutoSolve_Click;
            buttonRow.Controls.Add(_autoSolveButton, 0, 0);
        }
        else
        {
            buttonRow.Controls.Add(new Panel(), 0, 0); // empty placeholder
        }

        // Stretch spacer
        buttonRow.Controls.Add(new Panel(), 1, 0);

        // Cancel + Submit in a right-aligned flow panel
        var rightButtons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize      = true,
        };

        _cancelButton = new Button
        {
            Text      = "Cancel",
            AutoSize  = true,
            Padding   = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(230, 232, 240),
        };
        _cancelButton.FlatAppearance.BorderSize = 0;
        _cancelButton.Click += (_, _) => CancelDialog();

        _submitButton = new Button
        {
            Text      = "Submit",
            AutoSize  = true,
            Padding   = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 120, 220),
            ForeColor = Color.White,
            Margin    = new Padding(6, 0, 0, 0),
        };
        _submitButton.FlatAppearance.BorderSize = 0;
        _submitButton.Click += (_, _) => Submit();

        rightButtons.Controls.Add(_cancelButton);
        rightButtons.Controls.Add(_submitButton);
        buttonRow.Controls.Add(rightButtons, 2, 0);

        Row(buttonRow, topMargin: 10);

        AcceptButton = _submitButton;
        CancelButton = _cancelButton;

        Controls.Add(layout);

        Shown += (_, _) => _inputBox.Focus();
    }

    // ── Auto-solve ────────────────────────────────────────────────────────────

    private async void AutoSolve_Click(object? sender, EventArgs e)
    {
        if (_autoSolveButton == null) return;

        _autoSolveButton.Enabled = false;
        _autoSolveButton.Text    = "⏳ Solving...";
        _submitButton.Enabled    = false;

        try
        {
            _log?.Invoke("[Gemini] Sending captcha image...");

            var result = await GeminiClient.SolveCaptchaAsync(
                _imageBytes, AppSettings.Current.GeminiApiKey);

            if (string.IsNullOrWhiteSpace(result))
            {
                _log?.Invoke("[Gemini] No answer extracted from response.");
                _autoSolveButton.Text = "🤖 Auto-solve (no result)";
            }
            else
            {
                _log?.Invoke($"[Gemini] Extracted answer: {result}");
                _autoSolveButton.Text = "🤖 Auto-solve";
            }

            _inputBox.Text = result ?? string.Empty;
            _inputBox.Focus();
            _inputBox.SelectAll();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[Gemini] Error: {ex.Message}");
            _autoSolveButton.Text = "🤖 Auto-solve (failed)";
            MessageBox.Show($"Gemini error:\n{ex.Message}", "Auto-solve failed",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _autoSolveButton.Enabled = true;
            _submitButton.Enabled    = true;
        }
    }

    // ── Submit / Cancel ───────────────────────────────────────────────────────

    private void Submit()
    {
        var text = _inputBox.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            MessageBox.Show("Please enter the captcha text.", "Captcha",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        CaptchaResponse = text;
        DialogResult    = DialogResult.OK;
        Close();
    }

    private void CancelDialog()
    {
        CaptchaResponse = string.Empty;
        DialogResult    = DialogResult.Cancel;
        Close();
    }
}
