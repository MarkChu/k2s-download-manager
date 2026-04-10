using K2sDownloaderWinForms.Core;

namespace K2sDownloaderWinForms.Forms;

public sealed class SettingsForm : Form
{
    private readonly TextBox _apiKeyBox;
    private readonly Button  _toggleBtn;
    private readonly TextBox _downloadDirBox;
    private readonly Label   _statusLabel;

    public SettingsForm()
    {
        Text            = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        Size            = new Size(480, 310);
        Font            = new Font("Segoe UI", 9.5f);
        Padding         = new Padding(20);

        var layout = new TableLayoutPanel
        {
            Dock       = DockStyle.Fill,
            ColumnCount = 1,
            Padding    = new Padding(0),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Row(Control c, int top = 0)
        {
            c.Margin = new Padding(0, top, 0, 0);
            layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(c);
        }

        // ── Gemini section ────────────────────────────────────────────────────
        Row(new Label
        {
            Text      = "Gemini API Key",
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            AutoSize  = true,
        });

        Row(new Label
        {
            Text      = "If set, captcha images will be sent to Gemini for automatic solving.",
            ForeColor = Color.Gray,
            AutoSize  = true,
        }, top: 2);

        // Key input row (TextBox + show/hide button side by side)
        var keyRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock        = DockStyle.Fill,
            Padding     = new Padding(0),
            Margin      = new Padding(0),
            AutoSize    = true,
        };
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        keyRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        keyRow.RowCount = 1;

        _apiKeyBox = new TextBox
        {
            Text         = AppSettings.Current.GeminiApiKey,
            PasswordChar = '●',
            Dock         = DockStyle.Fill,
        };
        keyRow.Controls.Add(_apiKeyBox, 0, 0);

        _toggleBtn = new Button
        {
            Text      = "Show",
            Width     = 56,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(230, 232, 240),
            Margin    = new Padding(6, 0, 0, 0),
            Height    = _apiKeyBox.Height,
        };
        _toggleBtn.FlatAppearance.BorderSize = 0;
        _toggleBtn.Click += ToggleVisibility;
        keyRow.Controls.Add(_toggleBtn, 1, 0);
        Row(keyRow, top: 10);

        _statusLabel = new Label
        {
            AutoSize  = true,
            ForeColor = Color.Gray,
            Text      = string.Empty,
        };
        Row(_statusLabel, top: 6);

        // ── Download directory section ─────────────────────────────────────────
        Row(new Label
        {
            Text     = "Download Directory",
            Font     = new Font("Segoe UI", 10f, FontStyle.Bold),
            AutoSize = true,
        }, top: 16);

        Row(new Label
        {
            Text      = "Where downloaded files are saved. Leave blank to use the app folder.",
            ForeColor = Color.Gray,
            AutoSize  = true,
        }, top: 2);

        var dirRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock        = DockStyle.Fill,
            Padding     = new Padding(0),
            Margin      = new Padding(0),
            AutoSize    = true,
        };
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        dirRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dirRow.RowCount = 1;

        _downloadDirBox = new TextBox
        {
            Text         = AppSettings.Current.DownloadDirectory,
            PlaceholderText = AppContext.BaseDirectory,
            Dock         = DockStyle.Fill,
        };
        dirRow.Controls.Add(_downloadDirBox, 0, 0);

        var browseBtn = new Button
        {
            Text      = "Browse…",
            Width     = 72,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(230, 232, 240),
            Margin    = new Padding(6, 0, 0, 0),
            Height    = _downloadDirBox.Height,
        };
        browseBtn.FlatAppearance.BorderSize = 0;
        browseBtn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description         = "Select download directory",
                UseDescriptionForTitle = true,
                SelectedPath        = string.IsNullOrWhiteSpace(_downloadDirBox.Text)
                                          ? AppContext.BaseDirectory
                                          : _downloadDirBox.Text,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _downloadDirBox.Text = dlg.SelectedPath;
        };
        dirRow.Controls.Add(browseBtn, 1, 0);
        Row(dirRow, top: 10);

        // Stretch spacer
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Panel());

        // ── Buttons ───────────────────────────────────────────────────────────
        var btnRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize      = true,
            Dock          = DockStyle.Fill,
        };

        var cancelBtn = new Button
        {
            Text      = "Cancel",
            AutoSize  = true,
            Padding   = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(230, 232, 240),
        };
        cancelBtn.FlatAppearance.BorderSize = 0;
        cancelBtn.Click += (_, _) => Close();

        var saveBtn = new Button
        {
            Text      = "Save",
            AutoSize  = true,
            Padding   = new Padding(10, 4, 10, 4),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 120, 220),
            ForeColor = Color.White,
            Margin    = new Padding(6, 0, 0, 0),
        };
        saveBtn.FlatAppearance.BorderSize = 0;
        saveBtn.Click += Save;

        btnRow.Controls.Add(cancelBtn);
        btnRow.Controls.Add(saveBtn);
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(btnRow);

        AcceptButton = saveBtn;
        CancelButton = cancelBtn;
        Controls.Add(layout);

        Shown += (_, _) => _apiKeyBox.Focus();
    }

    private void ToggleVisibility(object? sender, EventArgs e)
    {
        bool hidden = _apiKeyBox.PasswordChar != '\0';
        _apiKeyBox.PasswordChar = hidden ? '\0' : '●';
        _toggleBtn.Text         = hidden ? "Hide" : "Show";
    }

    private void Save(object? sender, EventArgs e)
    {
        var cur = AppSettings.Current;
        var settings = new AppSettings
        {
            GeminiApiKey      = _apiKeyBox.Text.Trim(),
            DownloadDirectory = _downloadDirBox.Text.Trim(),
            RecentUrls        = cur.RecentUrls,
            Threads           = cur.Threads,
            SplitSizeMb       = cur.SplitSizeMb,
            FfmpegCheck       = cur.FfmpegCheck,
            MaxProxies        = cur.MaxProxies,
            RevalidateProxies = cur.RevalidateProxies,
        };
        settings.Save();

        _statusLabel.ForeColor = Color.FromArgb(30, 130, 60);
        _statusLabel.Text      = "Saved.";
    }
}
