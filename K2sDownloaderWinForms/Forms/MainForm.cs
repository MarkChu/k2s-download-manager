using K2sDownloaderWinForms.Core;
using System.Linq;
using System.Diagnostics;

namespace K2sDownloaderWinForms.Forms;

public sealed class MainForm : Form
{
    // ── Controls ──────────────────────────────────────────────────────────────
    private ComboBox _urlEdit = null!;
    private TextBox _filenameEdit = null!;
    private NumericUpDown _threadSpin = null!;
    private NumericUpDown _splitSpin = null!;
    private CheckBox _ffmpegCheck = null!;

    private Label _proxySummaryLabel = null!;
    private Button _proxyRefreshButton = null!;
    private NumericUpDown _proxyLimitSpin = null!;
    private CheckBox _revalidateCheck = null!;

    private Label _statusLabel = null!;
    private Label _partsLabel = null!;
    private Label _sizeLabel = null!;
    private ProgressBar _progressBar = null!;
    private Label _metricsLabel = null!;
    private RichTextBox _logView = null!;
    private Button _startButton = null!;
    private Button _cancelButton = null!;

    private Button _devToggle = null!;
    private Panel  _devPanel = null!;
    private Panel  _devContainer = null!;
    private Label _devAvailableLabel = null!;
    private TextBox _devAvailableList = null!;
    private Label _devActiveLabel = null!;
    private TextBox _devActiveList = null!;
    private Label _infoRuntimeLabel = null!;
    private TextBox _devChunkList = null!;

    // ── State ─────────────────────────────────────────────────────────────────
    private Downloader? _downloader;
    private CancellationTokenSource? _cts;
    private readonly ProxyRefreshService _proxyRefreshService;

    private readonly List<string> _logBuffer = new();
    private (long downloaded, long total, int done, int totalParts)? _pendingProgress;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private double _smoothedRate;
    private double? _smoothedEta;
    private long _lastDownloaded;
    private DateTime _lastProgressTime;
    private DateTime _downloadStart;
    private readonly Queue<(DateTime time, long downloaded)> _speedSamples = new();
    private const int SpeedWindowSeconds = 30;

    private List<string?> _currentProxiesRaw = new();
    private List<string> _currentActive = new();
    private bool _devPanelVisible;

    private Dictionary<int, long> _prevChunkBytes = new();
    private DateTime _prevChunkTime = DateTime.UtcNow;
    private readonly Dictionary<int, Queue<(DateTime time, long totalDone)>> _chunkSpeedSamples = new();

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainForm()
    {
        Text = "K2S Downloaderm";
        MinimumSize = new Size(960, 620);
        Size = new Size(1100, 720);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(245, 246, 250);

        BuildUi();

        _uiTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        _proxyRefreshService = new ProxyRefreshService(() => _downloader);
        _proxyRefreshService.StatusChanged += msg =>
        {
            if (IsHandleCreated) BeginInvoke(() => AppendLog(msg));
        };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Handle is now created — safe to call Invoke in callbacks
        _ = StartProxyLoadAsync(refresh: false);

        int interval = AppSettings.Current.ProxyRefreshIntervalMin;
        if (interval > 0)
        {
            _proxyRefreshService.Start(interval);
            AppendLog($"[AutoRefresh] Background proxy refresh started (every {interval} min).");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _uiTimer.Stop();
        _cts?.Cancel();
        _proxyRefreshService.Dispose();
        base.OnFormClosed(e);
    }

    // ── UI Construction ────────────────────────────────────────────────────────

    private void BuildUi()
    {
        // ── Toolbar ───────────────────────────────────────────────────────────
        var toolbar = new ToolStrip
        {
            Dock      = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = Color.FromArgb(235, 237, 245),
            Padding   = new Padding(4, 0, 4, 0),
        };

        var settingsBtn = new ToolStripButton
        {
            Text         = "⚙  Settings",
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText  = "Configure API keys and preferences",
        };
        settingsBtn.Click += (_, _) => OpenSettings();
        toolbar.Items.Add(settingsBtn);
        Controls.Add(toolbar);

        // ── Dev container (docked to bottom so it never shrinks main content) ──
        _devContainer = new Panel
        {
            Dock    = DockStyle.Bottom,
            Height  = 38,
            Padding = new Padding(16, 4, 16, 6),
        };

        _devToggle = new Button
        {
            Text      = "▶ Developer tools",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(230, 232, 240),
            AutoSize  = true,
            Padding   = new Padding(6, 2, 6, 2),
            Dock      = DockStyle.Top,
        };
        _devToggle.FlatAppearance.BorderSize = 0;
        _devToggle.Click += ToggleDevPanel;

        _devPanel = BuildDevPanel();
        _devPanel.Visible = false;
        _devPanel.Dock    = DockStyle.Fill;

        // Add Fill before Top so docking order is correct inside _devContainer
        _devContainer.Controls.Add(_devPanel);
        _devContainer.Controls.Add(_devToggle);
        Controls.Add(_devContainer);

        // ── Main content panel ────────────────────────────────────────────────
        var mainPanel = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 1,
            RowCount    = 2,
            Padding     = new Padding(16),
        };
        mainPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // header
        mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // content

        // ── Header ───────────────────────────────────────────────────────────
        var header = new Panel { Dock = DockStyle.Fill, Height = 52 };
        var titleLabel = MakeLabel("K2S Downloaderm", 16, FontStyle.Bold);
        titleLabel.Location = new Point(0, 0);
        var subtitleLabel = MakeLabel("Parallel Keep2Share downloader", 9.5f);
        subtitleLabel.ForeColor = Color.Gray;
        subtitleLabel.Location = new Point(0, 28);
        header.Controls.AddRange(new Control[] { titleLabel, subtitleLabel });
        mainPanel.Controls.Add(header, 0, 0);

        // ── Content ───────────────────────────────────────────────────────────
        var content = new TableLayoutPanel
        {
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            RowCount    = 1,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        content.Controls.Add(BuildSettingsCard(), 0, 0);
        content.Controls.Add(BuildProgressCard(), 1, 0);
        mainPanel.Controls.Add(content, 0, 1);

        Controls.Add(mainPanel);
    }

    private Panel BuildSettingsCard()
    {
        var card = MakeCard();

        // Main vertical layout — fill the card, rows auto-size to content
        var tbl = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(0),
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Helper: appends one row with AutoSize height
        void Row(Control c, int topMargin = 0)
        {
            c.Margin = new Padding(0, topMargin, 0, 0);
            tbl.RowCount++;
            tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tbl.Controls.Add(c);
        }

        // ── Download Source ───────────────────────────────────────────────────
        Row(MakeSectionLabel("Download Source"));

        _urlEdit = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDown,
            Dock          = DockStyle.Fill,
        };
        foreach (var recent in AppSettings.Current.RecentUrls)
            _urlEdit.Items.Add(recent);
        if (_urlEdit.Items.Count > 0)
            _urlEdit.Text = _urlEdit.Items[0]!.ToString();
        else
            _urlEdit.Text = string.Empty;
        Row(FieldRow("URL", _urlEdit), topMargin: 8);

        _filenameEdit = new TextBox { PlaceholderText = "Optional override name", Dock = DockStyle.Fill };
        Row(FieldRow("Output filename (optional)", _filenameEdit), topMargin: 10);

        // Threads + Split side-by-side
        _threadSpin = new NumericUpDown { Minimum = 1, Maximum = 128, Value = AppSettings.Current.Threads, Dock = DockStyle.Fill };
        _splitSpin  = new NumericUpDown { Minimum = 1, Maximum = 1024, Value = AppSettings.Current.SplitSizeMb, Dock = DockStyle.Fill };

        var spinPair = new TableLayoutPanel
        {
            ColumnCount = 2, Dock = DockStyle.Fill,
            Padding = new Padding(0), Margin = new Padding(0),
        };
        spinPair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        spinPair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        spinPair.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        spinPair.RowCount = 1;
        spinPair.Controls.Add(FieldRow("Threads",        _threadSpin, rightPad: 8), 0, 0);
        spinPair.Controls.Add(FieldRow("Split size (MB)", _splitSpin),               1, 0);
        Row(spinPair, topMargin: 10);

        _ffmpegCheck = new CheckBox
        {
            Text    = "Run ffmpeg integrity check when applicable",
            Checked = AppSettings.Current.FfmpegCheck,
            AutoSize = true,
        };
        Row(_ffmpegCheck, topMargin: 10);

        // ── Proxy ─────────────────────────────────────────────────────────────
        Row(MakeSectionLabel("Proxy"), topMargin: 18);

        // Summary label + Refresh button on the same row
        var proxyHeaderRow = new TableLayoutPanel
        {
            ColumnCount = 2, Dock = DockStyle.Fill,
            Padding = new Padding(0), Margin = new Padding(0),
        };
        proxyHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        proxyHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        proxyHeaderRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        proxyHeaderRow.RowCount = 1;

        _proxySummaryLabel = new Label
        {
            Text = "Available: 0 | Active: 0",
            ForeColor = Color.Gray, AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        proxyHeaderRow.Controls.Add(_proxySummaryLabel, 0, 0);

        _proxyRefreshButton = new Button
        {
            Text = "Refresh proxies",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(210, 222, 255),
            AutoSize = true,
            Padding = new Padding(8, 3, 8, 3),
            Margin = new Padding(6, 0, 0, 0),
        };
        _proxyRefreshButton.FlatAppearance.BorderSize = 0;
        _proxyRefreshButton.Click += (_, _) => _ = StartProxyLoadAsync(refresh: true);
        proxyHeaderRow.Controls.Add(_proxyRefreshButton, 1, 0);
        Row(proxyHeaderRow, topMargin: 6);

        _proxyLimitSpin = new NumericUpDown
        {
            Minimum = 0, Maximum = 100000, Value = AppSettings.Current.MaxProxies,
            Increment = 1000, Dock = DockStyle.Fill,
        };
        Row(FieldRow("Max proxies to validate (0 = all)", _proxyLimitSpin), topMargin: 10);

        _revalidateCheck = new CheckBox
        {
            Text    = "Revalidate cached proxies",
            Checked = AppSettings.Current.RevalidateProxies,
            AutoSize = true,
        };
        Row(_revalidateCheck, topMargin: 10);

        // Stretch spacer so content stays at the top
        tbl.RowCount++;
        tbl.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tbl.Controls.Add(new Panel());

        // ── Auto-save settings when any control changes ───────────────────────
        void SaveControls()
        {
            var s = AppSettings.Current;
            s.Threads           = (int)_threadSpin.Value;
            s.SplitSizeMb       = (int)_splitSpin.Value;
            s.FfmpegCheck       = _ffmpegCheck.Checked;
            s.MaxProxies        = (int)_proxyLimitSpin.Value;
            s.RevalidateProxies = _revalidateCheck.Checked;
            s.Save();
        }

        _threadSpin.ValueChanged      += (_, _) => SaveControls();
        _splitSpin.ValueChanged       += (_, _) => SaveControls();
        _ffmpegCheck.CheckedChanged   += (_, _) => SaveControls();
        _proxyLimitSpin.ValueChanged  += (_, _) => SaveControls();
        _revalidateCheck.CheckedChanged += (_, _) => SaveControls();

        card.Controls.Add(tbl);
        return card;
    }

    private Panel BuildProgressCard()
    {
        var card = MakeCard();
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // status block
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // progressbar
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // metrics
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // log
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // buttons

        // Status block
        var statusBlock = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
        };
        _statusLabel = MakeLabel("Idle", 12, FontStyle.Bold);
        _partsLabel = MakeLabel("0 / 0 parts");
        _partsLabel.ForeColor = Color.Gray;
        _sizeLabel = MakeLabel("0 / 0");
        _sizeLabel.ForeColor = Color.Gray;
        statusBlock.Controls.AddRange(new Control[] { _statusLabel, _partsLabel, _sizeLabel });
        layout.Controls.Add(statusBlock, 0, 0);

        _progressBar = new ProgressBar
        {
            Minimum = 0, Maximum = 1, Value = 0,
            Dock = DockStyle.Fill, Height = 20,
            Style = ProgressBarStyle.Continuous,
        };
        layout.Controls.Add(_progressBar, 0, 1);

        _metricsLabel = MakeLabel("Elapsed: 0s | ETA: -- | Speed: --");
        _metricsLabel.ForeColor = Color.Gray;
        layout.Controls.Add(_metricsLabel, 0, 2);

        _logView = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(250, 251, 255),
            Font = new Font("Consolas", 8.5f),
            BorderStyle = BorderStyle.FixedSingle,
            ScrollBars = RichTextBoxScrollBars.Vertical,
        };
        layout.Controls.Add(_logView, 0, 3);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 38,
            Padding = new Padding(0, 4, 0, 0),
        };
        _startButton = MakeButton("Start download", Color.FromArgb(60, 120, 220), Color.White);
        _startButton.Click += OnStartClicked;
        _cancelButton = MakeButton("Cancel", Color.FromArgb(200, 60, 60), Color.White);
        _cancelButton.Enabled = false;
        _cancelButton.Click += OnCancelClicked;
        buttonRow.Controls.AddRange(new Control[] { _startButton, _cancelButton });
        layout.Controls.Add(buttonRow, 0, 4);

        card.Controls.Add(layout);
        return card;
    }

    private Panel BuildDevPanel()
    {
        var card = MakeCard();
        var tabs = new TabControl { Dock = DockStyle.Fill };

        // Proxies tab
        var proxyTab = new TabPage("Proxies");
        var proxyLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        _devAvailableLabel = MakeLabel("Available proxies: 0");
        proxyLayout.Controls.Add(_devAvailableLabel);
        _devAvailableList = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, Height = 110, BackColor = Color.FromArgb(248, 249, 253),
        };
        proxyLayout.Controls.Add(_devAvailableList);
        _devActiveLabel = MakeLabel("Active proxies: 0");
        proxyLayout.Controls.Add(_devActiveLabel);
        _devActiveList = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, Height = 110, BackColor = Color.FromArgb(248, 249, 253),
        };
        proxyLayout.Controls.Add(_devActiveList);
        proxyTab.Controls.Add(proxyLayout);

        // Chunks tab
        var chunksTab = new TabPage("Chunks");
        _infoRuntimeLabel = new Label
        {
            Text      = "No active download.",
            ForeColor = Color.Gray,
            Dock      = DockStyle.Top,
            Padding   = new Padding(4, 3, 4, 3),
            Height    = 22,
            AutoSize  = false,
        };
        _devChunkList = new TextBox
        {
            Multiline   = true,
            ReadOnly    = true,
            ScrollBars  = ScrollBars.Vertical,
            Dock        = DockStyle.Fill,
            BackColor   = Color.FromArgb(248, 249, 253),
            Font        = new Font("Consolas", 9f),
        };
        chunksTab.Controls.Add(_devChunkList);

        // Add the Top label AFTER the Fill textbox so the docking engine processes
        // the label first (highest index → first docked) and the textbox fills the rest.
        chunksTab.Controls.Add(_infoRuntimeLabel);

        tabs.TabPages.AddRange(new[] { proxyTab, chunksTab });
        card.Controls.Add(tabs);
        return card;
    }

    // ── Download flow ─────────────────────────────────────────────────────────

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        if (_downloader != null) return;

        var url = _urlEdit.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show("Please enter a K2S download URL.", "Missing URL",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Persist URL to recent list and refresh ComboBox items
        AppSettings.Current.AddRecentUrl(url);
        _urlEdit.Items.Clear();
        foreach (var u in AppSettings.Current.RecentUrls)
            _urlEdit.Items.Add(u);

        var filename = _filenameEdit.Text.Trim();
        int threads = (int)_threadSpin.Value;
        int splitMb = (int)_splitSpin.Value;
        bool ensureCheck = _ffmpegCheck.Checked;

        _logView.Clear();
        AppendLog("Starting download...");
        _downloadStart = DateTime.UtcNow;
        _smoothedRate = 0;
        _smoothedEta = null;
        _lastProgressTime = _downloadStart;
        _lastDownloaded = 0;

        _startButton.Enabled = false;
        _cancelButton.Enabled = true;
        _statusLabel.Text = "Preparing...";

        _cts = new CancellationTokenSource();
        _downloader = new Downloader();
        _downloader.StatusChanged   += OnStatusChanged;
        _downloader.ProgressChanged += OnProgressChanged;
        _downloader.ProxyStateChanged += OnProxyStateChanged;

        // Seed with all proxies we already know (from initial load + any auto-refreshed ones).
        var seedProxies = _currentProxiesRaw
            .Where(p => p != null).Cast<string>()
            .Concat(_proxyRefreshService.KnownProxies)
            .Distinct()
            .ToList();
        if (seedProxies.Count > 0)
            _downloader.SetInitialProxies(seedProxies);

        try
        {
            var result = await Task.Run(() => _downloader.DownloadAsync(
                url,
                string.IsNullOrEmpty(filename) ? null : filename,
                threads,
                splitMb * 1024 * 1024,
                ensureCheck,
                CaptchaCallbackAsync,
                _cts.Token), _cts.Token);

            AppendLog($"Download finished: {result}", immediate: true);
            AppendLog($"Completed in {FormatDuration(DateTime.UtcNow - _downloadStart)}", immediate: true);
            MessageBox.Show($"Saved to:\n{result}", "Download complete",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Download cancelled.", immediate: true);
        }
        catch (DownloadCancelledException)
        {
            AppendLog("Download cancelled.", immediate: true);
        }
        catch (Exception ex)
        {
            AppendLog($"Download failed: {ex.Message}\n{ex}", immediate: true);
            MessageBox.Show(ex.Message, "Download failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            ResetState();
        }
    }

    private void OnCancelClicked(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        AppendLog("Cancellation requested...", immediate: true);
    }

    private void OpenSettings()
    {
        using var dlg = new SettingsForm();
        dlg.ShowDialog(this);
    }

    private Task<string> CaptchaCallbackAsync(byte[] imageBytes, string challenge, string captchaUrl)
    {
        return CaptchaCallbackInternalAsync(imageBytes, challenge, captchaUrl);
    }

    private async Task<string> CaptchaCallbackInternalAsync(byte[] imageBytes, string challenge, string captchaUrl)
    {
        var apiKey = AppSettings.Current.GeminiApiKey?.Trim();

        // If API key configured, try auto-solve up to 5 times before showing UI
            if (!string.IsNullOrEmpty(apiKey))
        {
            int maxAttempts = AppSettings.Current.AutoSolveAttempts;
            int perAttemptTimeoutSec = AppSettings.Current.AutoSolvePerAttemptTimeoutSec; // per-call timeout
            int baseDelayMs = AppSettings.Current.AutoSolveBaseDelayMs;
            int maxDelayMs = AppSettings.Current.AutoSolveMaxDelayMs;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                AppendLog($"[Auto-solve] Attempt {attempt}/{maxAttempts}...");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(perAttemptTimeoutSec));
                try
                {
                    var result = await GeminiClient.SolveCaptchaAsync(imageBytes, apiKey, cts.Token);
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        AppendLog($"[Auto-solve] Success: {result}", immediate: true);
                        return result;
                    }
                    AppendLog("[Auto-solve] Empty result, will retry...");
                }
                catch (OperationCanceledException)
                {
                    AppendLog($"[Auto-solve] Attempt {attempt} timed out after {perAttemptTimeoutSec}s.");
                }
                catch (Exception ex)
                {
                    AppendLog($"[Auto-solve] Error: {ex.Message}");
                }

                // Exponential backoff between attempts
                int delay = Math.Min(baseDelayMs * (1 << (attempt - 1)), maxDelayMs);
                AppendLog($"[Auto-solve] Waiting {delay} ms before next attempt...");
                try { await Task.Delay(delay, CancellationToken.None); } catch { }
            }

            AppendLog("[Auto-solve] All attempts failed — falling back to user input.", immediate: true);
        }

        // Fallback to UI dialog on UI thread
        var tcs = new TaskCompletionSource<string>();
        Invoke(() =>
        {
            using var dlg = new CaptchaForm(imageBytes, msg => AppendLog(msg, immediate: true));
            if (dlg.ShowDialog(this) == DialogResult.OK)
                tcs.TrySetResult(dlg.CaptchaResponse);
            else
                tcs.TrySetCanceled();
        });
        return await tcs.Task;
    }

    // ── Proxy loading ─────────────────────────────────────────────────────────

    private async Task StartProxyLoadAsync(bool refresh)
    {
        _proxyRefreshButton.Enabled = false;
        _proxyLimitSpin.Enabled = false;
        _revalidateCheck.Enabled = false;

        int limitValue = (int)_proxyLimitSpin.Value;
        int? maxCandidates = limitValue == 0 ? null : limitValue;
        bool recheck = _revalidateCheck.Checked;

        AppendLog(refresh
            ? $"Refreshing proxy list (limit={limitValue}, revalidate={recheck})..."
            : $"Loading proxy list (limit={limitValue}, revalidate={recheck})...");

        var loadStart = DateTime.UtcNow;

        try
        {
            var proxies = await ProxyManager.GetWorkingProxiesAsync(
                refresh, recheck, maxCandidates,
                msg => { if (IsHandleCreated) BeginInvoke(() => AppendLog(msg)); },
                CancellationToken.None);

            _currentProxiesRaw = proxies;
            UpdateProxyDisplay(proxies, new List<int>());

            var elapsed = DateTime.UtcNow - loadStart;
            AppendLog($"Proxy load completed in {FormatDuration(elapsed)}");
        }
        catch (Exception ex)
        {
            AppendLog($"Proxy load failed: {ex.Message}", immediate: true);
        }
        finally
        {
            _proxyRefreshButton.Enabled = true;
            _proxyLimitSpin.Enabled = true;
            _revalidateCheck.Enabled = true;
        }
    }

    // ── Event callbacks (cross-thread) ────────────────────────────────────────

    private void OnStatusChanged(string message)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => AppendLog(message));
        else AppendLog(message);
    }

    private void OnProgressChanged(long downloaded, long total, int done, int totalParts)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => _pendingProgress = (downloaded, total, done, totalParts));
        else _pendingProgress = (downloaded, total, done, totalParts);
    }

    private void OnProxyStateChanged(List<string?> proxies, List<int> activeIndexes)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(() => UpdateProxyDisplay(proxies, activeIndexes));
        else UpdateProxyDisplay(proxies, activeIndexes);
    }

    // ── UI Timer ──────────────────────────────────────────────────────────────

    private void OnUiTick(object? sender, EventArgs e)
    {
        if (_pendingProgress.HasValue)
        {
            var (d, t, done, total) = _pendingProgress.Value;
            _pendingProgress = null;
            RenderProgress(d, t, done, total);
        }
        FlushLogBuffer();

        if (_devPanelVisible)
            RefreshChunkDisplay();
    }

    private void RenderProgress(long downloaded, long total, int done, int totalParts)
    {
        const int scale = 1024;
        _progressBar.Maximum = Math.Max(1, (int)(total / scale));
        _progressBar.Value = Math.Max(0, Math.Min(_progressBar.Maximum, (int)(downloaded / scale)));

        _statusLabel.Text = "Downloading";
        _partsLabel.Text = $"{done}/{totalParts} parts";
        _sizeLabel.Text = $"{Downloader.HumanReadableBytes(downloaded)} / {Downloader.HumanReadableBytes(total)}";

        var now = DateTime.UtcNow;
        var elapsed = now - _downloadStart;

        // Record a timestamped sample and keep only the last SpeedWindowSeconds seconds
        _speedSamples.Enqueue((now, downloaded));
        while (_speedSamples.Count > 1 && (now - _speedSamples.Peek().time).TotalSeconds > SpeedWindowSeconds)
            _speedSamples.Dequeue();

        // Compute average rate over the window
        double windowRate = 0;
        if (_speedSamples.Count >= 2)
        {
            var earliest = _speedSamples.Peek();
            var windowSec = (now - earliest.time).TotalSeconds;
            var bytesDiff = downloaded - earliest.downloaded;
            if (windowSec > 0 && bytesDiff >= 0)
                windowRate = bytesDiff / windowSec;
        }

        _smoothedRate = windowRate; // use window average as the rate for ETA and display

        double? remaining = null;
        if (_smoothedRate > 0) remaining = (total - downloaded) / _smoothedRate;
        else if (elapsed.TotalSeconds > 0 && downloaded > 0)
        {
            double avg = downloaded / elapsed.TotalSeconds;
            if (avg > 0) remaining = (total - downloaded) / avg;
        }

        if (remaining.HasValue)
        {
            double beta = 0.9;
            _smoothedEta = _smoothedEta.HasValue
                ? (1 - beta) * _smoothedEta.Value + beta * remaining.Value
                : remaining.Value;
        }

        _metricsLabel.Text =
            $"Elapsed: {FormatDuration(elapsed)} | ETA: {(_smoothedEta.HasValue ? FormatDuration(TimeSpan.FromSeconds(_smoothedEta.Value)) : "--")} | Speed: {FormatSpeed(_smoothedRate)}";

        _infoRuntimeLabel.Text =
            $"Threads: {_threadSpin.Value} • Split: {_splitSpin.Value} MB • Active proxies: {_currentActive.Count} • {FormatSpeed(_smoothedRate)}";

        _lastProgressTime = now;
        _lastDownloaded = downloaded;
    }

    // ── Proxy display ─────────────────────────────────────────────────────────

    private void UpdateProxyDisplay(List<string?> proxies, List<int> activeIndexes)
    {
        _currentActive = activeIndexes
            .Where(i => i >= 0 && i < proxies.Count)
            .Select(i => $"[{i}] {proxies[i] ?? "LOCAL"}")
            .ToList();

        int availableCount = Math.Max(0, proxies.Count - 1); // exclude null (direct)
        _proxySummaryLabel.Text = $"Available: {availableCount} | Active: {activeIndexes.Count}";

        if (_devPanelVisible)
        {
            var labels = proxies.Select((p, i) => $"[{i}] {p ?? "LOCAL"}").ToList();
            _devAvailableLabel.Text = $"Available proxies: {availableCount}";
            _devActiveLabel.Text = $"Active proxies: {activeIndexes.Count}";
            _devAvailableList.Lines = labels.Any() ? labels.ToArray() : new[] { "(none)" };
            _devActiveList.Lines = _currentActive.Any() ? _currentActive.ToArray() : new[] { "(none)" };
        }
    }

    // ── Chunk display ─────────────────────────────────────────────────────────

    private void RefreshChunkDisplay()
    {
        var chunks = _downloader?.ActiveChunks;
        var now = DateTime.UtcNow;
        double dt = Math.Max(0.05, (now - _prevChunkTime).TotalSeconds);

        if (chunks == null || chunks.IsEmpty)
        {
            _prevChunkBytes.Clear();
            _chunkSpeedSamples.Clear();
            _prevChunkTime = now;
            return;
        }

        var lines  = new List<string>();
        var nextBytes = new Dictionary<int, long>();
        var activeIndexes = new HashSet<int>();

        foreach (var kvp in chunks.OrderBy(k => k.Key))
        {
            var act         = kvp.Value;
            long existing   = act.ExistingBytes;
            long done       = act.BytesDone;
            long totalDone  = existing + done;

            activeIndexes.Add(act.Index);
            nextBytes[act.Index] = done;

            // Maintain per-chunk time-series samples (totalDone) and compute windowed average
            if (!_chunkSpeedSamples.TryGetValue(act.Index, out var q))
            {
                q = new Queue<(DateTime time, long totalDone)>();
                _chunkSpeedSamples[act.Index] = q;
            }
            q.Enqueue((now, totalDone));
            while (q.Count > 1 && (now - q.Peek().time).TotalSeconds > SpeedWindowSeconds)
                q.Dequeue();

            double speed = 0;
            if (q.Count >= 2)
            {
                var earliest = q.Peek();
                var windowSec = (now - earliest.time).TotalSeconds;
                var bytesDiff = totalDone - earliest.totalDone;
                if (windowSec > 0 && bytesDiff >= 0) speed = bytesDiff / windowSec;
            }
            else
            {
                // Fallback to short-term delta to avoid 0 speed on very fresh chunks
                if (_prevChunkBytes.TryGetValue(act.Index, out long prev))
                {
                    long delta = done - prev;
                    if (delta >= 0) speed = delta / dt;
                }
            }

            string proxy    = act.Proxy != null
                ? (act.Proxy.Length > 24 ? act.Proxy[..24] : act.Proxy)
                : "LOCAL";
            string progress = $"{Downloader.HumanReadableBytes(totalDone),10} / {Downloader.HumanReadableBytes(act.BytesTotal),-10}";
            int pct = act.BytesTotal > 0 ? (int)(totalDone * 100 / act.BytesTotal) : 0;

            lines.Add($"Part {act.Index,3}  {proxy,-25}  {progress}  {pct,3}%  {FormatSpeed(speed),12}");
        }

        // Remove samples for chunks that are no longer active
        var toRemove = _chunkSpeedSamples.Keys.Except(activeIndexes).ToList();
        foreach (var k in toRemove) _chunkSpeedSamples.Remove(k);

        _prevChunkBytes = nextBytes;
        _prevChunkTime  = now;

        _devChunkList.Lines = lines.Count > 0 ? lines.ToArray() : new[] { "(no active chunks)" };
    }

    // ── Log ───────────────────────────────────────────────────────────────────

    private void AppendLog(string message, bool immediate = false)
    {
        if (immediate)
        {
            FlushLogBuffer();
            _logView.AppendText(message + Environment.NewLine);
            _logView.ScrollToCaret();
            return;
        }
        _logBuffer.Add(message);
    }

    private void FlushLogBuffer()
    {
        if (_logBuffer.Count == 0) return;
        const int maxPerTick = 40;
        var batch = _logBuffer.Take(maxPerTick).ToList();
        _logBuffer.RemoveRange(0, Math.Min(maxPerTick, _logBuffer.Count));
        _logView.AppendText(string.Join(Environment.NewLine, batch) + Environment.NewLine);
        _logView.ScrollToCaret();
    }

    // ── Dev panel toggle ──────────────────────────────────────────────────────

    private const int DevPanelExpandedHeight = 370;
    private const int DevPanelCollapsedHeight = 38;

    private void ToggleDevPanel(object? sender, EventArgs e)
    {
        _devPanelVisible = !_devPanelVisible;
        _devToggle.Text  = _devPanelVisible ? "▼ Developer tools" : "▶ Developer tools";

        _devPanel.Visible    = _devPanelVisible;
        _devContainer.Height = _devPanelVisible ? DevPanelExpandedHeight : DevPanelCollapsedHeight;

        if (_devPanelVisible)
            UpdateProxyDisplay(_currentProxiesRaw, new List<int>());
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    private void ResetState()
    {
        _pendingProgress = null;
        FlushLogBuffer();
        _startButton.Enabled = true;
        _cancelButton.Enabled = false;
        _statusLabel.Text = "Idle";
        _partsLabel.Text = "0 / 0 parts";
        _sizeLabel.Text = "0 / 0";
        _progressBar.Value = 0;
        _metricsLabel.Text = "Elapsed: 0s | ETA: -- | Speed: --";
        _smoothedRate = 0;
        _smoothedEta = null;
        _infoRuntimeLabel.Text = "No active download.";
        _devChunkList.Lines = Array.Empty<string>();
        _prevChunkBytes.Clear();
        _chunkSpeedSamples.Clear();
        _downloader = null;
        _cts?.Dispose();
        _cts = null;
    }

    // ── Formatting ────────────────────────────────────────────────────────────

    private static string FormatDuration(TimeSpan ts)
    {
        var s = Math.Max(0, (int)ts.TotalSeconds);
        int h = s / 3600, rem = s % 3600, m = rem / 60, sec = rem % 60;
        return h > 0 ? $"{h}:{m:D2}:{sec:D2}" : $"{m}:{sec:D2}";
    }

    private static string FormatSpeed(double bps)
    {
        if (bps <= 0) return "--";
        string[] units = { "B/s", "KiB/s", "MiB/s", "GiB/s" };
        double v = bps;
        foreach (var u in units)
        {
            if (v < 1024 || u == "GiB/s")
                return u == "B/s" ? $"{v:0} {u}" : $"{v:0.00} {u}";
            v /= 1024;
        }
        return $"{v:0.00} GiB/s";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Panel MakeCard()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(6),
            Padding = new Padding(14),
            BackColor = Color.White,
        };
    }

    private static Label MakeLabel(string text, float size = 9.5f, FontStyle style = FontStyle.Regular)
    {
        return new Label
        {
            Text = text,
            Font = new Font("Segoe UI", size, style),
            AutoSize = true,
        };
    }

    private static Label MakeSectionLabel(string text) =>
        new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 4),
        };

    private static Button MakeButton(string text, Color bg, Color fg)
    {
        var btn = new Button
        {
            Text = text,
            BackColor = bg,
            ForeColor = fg,
            FlatStyle = FlatStyle.Flat,
            Padding = new Padding(10, 4, 10, 4),
            AutoSize = true,
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    /// <summary>
    /// Returns a two-row TableLayoutPanel: caption label on top, control below.
    /// Properly reports its preferred height so parent AutoSize rows work correctly.
    /// </summary>
    private static TableLayoutPanel FieldRow(string caption, Control ctrl, int rightPad = 0)
    {
        var lbl = new Label
        {
            Text = caption,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.Gray,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 17,
        };
        ctrl.Dock = DockStyle.Fill;

        var tbl = new TableLayoutPanel
        {
            ColumnCount = 1, RowCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(0),
            Margin = new Padding(0, 0, rightPad, 0),
            AutoSize = true,
        };
        tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        tbl.RowStyles.Add(new RowStyle(SizeType.Absolute, 17));
        tbl.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tbl.Controls.Add(lbl,  0, 0);
        tbl.Controls.Add(ctrl, 0, 1);
        return tbl;
    }
}
