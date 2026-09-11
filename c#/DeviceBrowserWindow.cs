using System.Diagnostics;

namespace WiFiAutoStreamSync;

public sealed class DeviceBrowserWindow : Form
{
    private readonly SyncEngine _engine;
    private readonly ComboBox _deviceSelector;
    private readonly TextBox _pathBox;
    private readonly ListView _fileListView;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _speedLabel;
    private readonly ToolStripProgressBar _progressBar;
    private readonly ImageList _iconsList;

    private DeviceClient? _currentClient;
    private string _currentPath = "/storage/emulated/0";

    public DeviceBrowserWindow(SyncEngine engine)
    {
        _engine = engine;

        Text = "Android Wireless Device & Storage Explorer";
        Size = new Size(1160, 680);
        MinimumSize = new Size(920, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        TopMost = false;
        ShowInTaskbar = true;
        _iconsList = new ImageList { ImageSize = new Size(18, 18), ColorDepth = ColorDepth.Depth32Bit };
        _iconsList.Images.Add("folder", CreateFolderBitmap());
        _iconsList.Images.Add("file", SystemIcons.Application);

        var topPanel = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 8, 12, 8), BackColor = Color.FromArgb(241, 245, 249) };
        var lblDev = new Label { Text = "Connected Android Device:", AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 6, 8, 0), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        _deviceSelector = new ComboBox { Dock = DockStyle.Left, Width = 380, DropDownStyle = ComboBoxStyle.DropDownList };
        _deviceSelector.SelectedIndexChanged += async (s, e) => await OnDeviceSelectionChangedAsync();

        var btnRefreshDevs = new Button { Text = "🔄 Rescan Devices", Dock = DockStyle.Right, Width = 140 };
        btnRefreshDevs.Click += (s, e) => PopulateDeviceList();

        topPanel.Controls.Add(_deviceSelector);
        topPanel.Controls.Add(lblDev);
        topPanel.Controls.Add(btnRefreshDevs);

        var navPanel = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 6, 10, 6) };
        var btnUp = new Button { Text = "⬆ Up", Width = 65, Dock = DockStyle.Left };
        btnUp.Click += async (s, e) => await NavigateUpAsync();

        var btnRefresh = new Button { Text = "Refresh", Width = 75, Dock = DockStyle.Left };
        btnRefresh.Click += async (s, e) => await LoadDirectoryAsync(_currentPath);

        _pathBox = new TextBox { Dock = DockStyle.Fill, Text = _currentPath };
        _pathBox.KeyDown += async (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await LoadDirectoryAsync(_pathBox.Text.Trim());
            }
        };

        var btnSaveFile = new Button { Text = "💾 Save File to PC...", Width = 150, Dock = DockStyle.Right, BackColor = Color.FromArgb(2, 132, 199), ForeColor = Color.White };
        btnSaveFile.Click += async (s, e) => await SaveSelectedFileToFolderAsync();

        var btnSaveCurrentFolder = new Button { Text = "📁 Save Folder to PC...", Width = 160, Dock = DockStyle.Right, BackColor = Color.FromArgb(16, 185, 129), ForeColor = Color.White };
        btnSaveCurrentFolder.Click += async (s, e) => await SaveCurrentFolderToPcAsync();

        var btnUpload = new Button { Text = "⬆ Upload File...", Width = 115, Dock = DockStyle.Right, BackColor = Color.FromArgb(14, 165, 233), ForeColor = Color.White };
        btnUpload.Click += async (s, e) => await UploadFileAsync();

        var btnUploadFolder = new Button { Text = "📁 Upload Folder...", Width = 130, Dock = DockStyle.Right };
        btnUploadFolder.Click += async (s, e) => await UploadFolderAsync();

        var btnInspectManifest = new Button { Text = "📋 Manifest", Width = 95, Dock = DockStyle.Right };
        btnInspectManifest.Click += (s, e) => Program.ShowManifestInspector();

        var navMiddle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        navMiddle.Controls.Add(_pathBox);

        navPanel.Controls.Add(navMiddle);
        navPanel.Controls.Add(btnRefresh);
        navPanel.Controls.Add(btnUp);
        navPanel.Controls.Add(btnSaveFile);
        navPanel.Controls.Add(btnSaveCurrentFolder);
        navPanel.Controls.Add(btnUploadFolder);
        navPanel.Controls.Add(btnUpload);
        navPanel.Controls.Add(btnInspectManifest);

        _fileListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            GridLines = true,
            SmallImageList = _iconsList
        };
        _fileListView.Columns.Add("Name", 440);
        _fileListView.Columns.Add("Size", 120, HorizontalAlignment.Right);
        _fileListView.Columns.Add("Item Type", 120);
        _fileListView.Columns.Add("Date Modified", 180);

        _fileListView.ItemActivate += async (s, e) =>
        {
            if (_fileListView.SelectedItems.Count > 0 && _fileListView.SelectedItems[0].Tag is AndroidFileItem item)
            {
                if (item.IsDir)
                {
                    await LoadDirectoryAsync(item.Path);
                }
                else
                {
                    await DownloadAndOpenFileAsync(item);
                }
            }
        };

        var ctxMenu = new ContextMenuStrip();
        ctxMenu.Items.Add("💾 Save File to Folder / Directory...", null, async (s, e) => await SaveSelectedFileToFolderAsync());
        ctxMenu.Items.Add("💾 Save File As (Specific Name/Location)...", null, async (s, e) => await SaveSelectedFileToLocationAsync());
        ctxMenu.Items.Add("📁 Save Selected Folder to PC Location...", null, async (s, e) => await SaveSelectedFolderToLocationAsync());
        ctxMenu.Items.Add(new ToolStripSeparator());
        ctxMenu.Items.Add("👁 Open / Preview", null, async (s, e) =>
        {
            if (_fileListView.SelectedItems.Count > 0 && _fileListView.SelectedItems[0].Tag is AndroidFileItem item && !item.IsDir)
            {
                await DownloadAndOpenFileAsync(item);
            }
        });
        ctxMenu.Items.Add(new ToolStripSeparator());
        ctxMenu.Items.Add("🗑 Delete on Android", null, async (s, e) => await DeleteSelectedItemAsync());
        _fileListView.ContextMenuStrip = ctxMenu;

        var statusStrip = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _speedLabel = new ToolStripStatusLabel { Text = string.Empty, AutoSize = true, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        _progressBar = new ToolStripProgressBar
        {
            Width = 220,
            Visible = false,
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = 100
        };

        statusStrip.Items.Add(_statusLabel);
        statusStrip.Items.Add(_speedLabel);
        statusStrip.Items.Add(_progressBar);

        Controls.Add(_fileListView);
        Controls.Add(navPanel);
        Controls.Add(topPanel);
        Controls.Add(statusStrip);

        _engine.OnDevicesChanged += () =>
        {
            if (!IsDisposed && IsHandleCreated)
                Invoke((Action)PopulateDeviceList);
        };

        PopulateDeviceList();
    }

    private void PopulateDeviceList()
    {
        var clients = _engine.ConnectedClients.Values.Where(c => c.IsConnected).ToList();
        _deviceSelector.Items.Clear();

        if (clients.Count == 0)
        {
            _deviceSelector.Items.Add("No Android devices connected (Scanning Wi-Fi...)");
            _deviceSelector.SelectedIndex = 0;
            _currentClient = null;
            _fileListView.Items.Clear();
            _statusLabel.Text = "No Android devices active. Ensure the app is running on your phone.";
            return;
        }

        foreach (var client in clients)
        {
            string label = client.DeviceInfo != null
                ? $"{client.DeviceInfo.Manufacturer} {client.DeviceInfo.Model} ({client.RemoteIp})"
                : $"Android Phone ({client.RemoteIp})";
            _deviceSelector.Items.Add(new DeviceComboItem(client, label));
        }

        _deviceSelector.SelectedIndex = 0;
    }

    private async Task OnDeviceSelectionChangedAsync()
    {
        if (_deviceSelector.SelectedItem is DeviceComboItem selected)
        {
            _currentClient = selected.Client;
            if (_currentClient.DeviceInfo != null && _currentClient.DeviceInfo.RootDirs.Count > 0)
            {
                _currentPath = _currentClient.DeviceInfo.RootDirs[0].Path;
            }
            else
            {
                _currentPath = "/storage/emulated/0";
            }
            await LoadDirectoryAsync(_currentPath);
        }
    }

    private static Bitmap CreateFolderBitmap()
    {
        var bmp = new Bitmap(18, 18);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);

        using var tabBrush = new SolidBrush(Color.FromArgb(217, 119, 6));
        g.FillRectangle(tabBrush, 1, 2, 7, 4);

        using var bodyBrush = new SolidBrush(Color.FromArgb(245, 158, 11));
        g.FillRectangle(bodyBrush, 1, 5, 16, 11);

        using var borderPen = new Pen(Color.FromArgb(180, 83, 9), 1f);
        g.DrawRectangle(borderPen, 1, 5, 15, 10);

        return bmp;
    }

    private static string ShowInputDialog(IWin32Window owner, string text, string caption, string defaultValue = "")
    {
        using var prompt = new Form
        {
            Width = 400,
            Height = 170,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = caption,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            Font = new Font("Segoe UI", 9.5f)
        };

        var textLabel = new Label { Left = 20, Top = 15, Text = text, AutoSize = true };
        var textBox = new TextBox { Left = 20, Top = 45, Width = 340, Text = defaultValue };
        var confirmation = new Button { Text = "OK", Left = 180, Width = 85, Top = 85, DialogResult = DialogResult.OK, BackColor = Color.FromArgb(2, 132, 199), ForeColor = Color.White };
        var cancel = new Button { Text = "Cancel", Left = 275, Width = 85, Top = 85, DialogResult = DialogResult.Cancel };

        prompt.Controls.AddRange([textLabel, textBox, confirmation, cancel]);
        prompt.AcceptButton = confirmation;
        prompt.CancelButton = cancel;

        return prompt.ShowDialog(owner) == DialogResult.OK ? textBox.Text : string.Empty;
    }

    private async Task LoadDirectoryAsync(string path)
    {
        if (_currentClient == null || !_currentClient.IsConnected)
        {
            _statusLabel.Text = "Selected device is offline.";
            return;
        }

        _statusLabel.Text = $"Browsing: {path}...";
        _pathBox.Text = path;
        _currentPath = path;

        var listing = await _currentClient.ListDirectoryAsync(path);
        _fileListView.Items.Clear();

        if (listing == null || !listing.Exists)
        {
            _statusLabel.Text = $"Directory could not be read: {path}";
            return;
        }

        foreach (var item in listing.Items)
        {
            var lvi = new ListViewItem(item.Name, item.IsDir ? "folder" : "file") { Tag = item };
            lvi.SubItems.Add(item.IsDir ? "" : FormatBytes(item.Size));
            lvi.SubItems.Add(item.IsDir ? "Folder" : (Path.GetExtension(item.Name).ToUpperInvariant() + " File"));
            lvi.SubItems.Add(DateTimeOffset.FromUnixTimeMilliseconds(item.LastModified).LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
            _fileListView.Items.Add(lvi);
        }

        int folderCount = listing.Items.Count(i => i.IsDir);
        int fileCount = listing.Items.Count - folderCount;
        _statusLabel.Text = $"{listing.Items.Count} item(s) ({folderCount} folder(s), {fileCount} file(s))";
    }

    private async Task NavigateUpAsync()
    {
        if (string.IsNullOrEmpty(_currentPath) || _currentPath == "/" || _currentPath == "/storage/emulated/0")
            return;

        string parent = Path.GetDirectoryName(_currentPath)?.Replace('\\', '/') ?? "/";
        if (string.IsNullOrEmpty(parent)) parent = "/";
        await LoadDirectoryAsync(parent);
    }

    private async Task SaveSelectedFileToFolderAsync()
    {
        if (_currentClient == null || _fileListView.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Please select a file from the list first.", "No File Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_fileListView.SelectedItems[0].Tag is not AndroidFileItem item || item.IsDir)
        {
            MessageBox.Show(this, "The selected item is a directory. Use 'Save Folder to PC...' instead.", "Selection Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var fbd = new FolderBrowserDialog
        {
            Description = $"Select directory on any disk/drive to save '{item.Name}' directly:",
            UseDescriptionForTitle = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (fbd.ShowDialog(this) == DialogResult.OK)
        {
            string destinationFile = Path.Combine(fbd.SelectedPath, item.Name);
            await ExecuteDirectFileDownloadAsync(item, destinationFile);
        }
    }

    private async Task SaveSelectedFileToLocationAsync()
    {
        if (_currentClient == null || _fileListView.SelectedItems.Count == 0)
        {
            MessageBox.Show(this, "Please select a file from the list first.", "No File Selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_fileListView.SelectedItems[0].Tag is not AndroidFileItem item || item.IsDir)
        {
            MessageBox.Show(this, "Please select a file to save.", "Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = $"Save '{item.Name}' directly to chosen location",
            FileName = item.Name,
            Filter = "All Files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (sfd.ShowDialog(this) == DialogResult.OK)
        {
            await ExecuteDirectFileDownloadAsync(item, sfd.FileName);
        }
    }

    private async Task ExecuteDirectFileDownloadAsync(AndroidFileItem item, string destinationFile)
    {
        if (_currentClient == null || !_currentClient.IsConnected)
        {
            MessageBox.Show(this, "Device is disconnected.", "Connection Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _progressBar.Visible = true;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Value = 0;
        _speedLabel.Text = "⚡ Starting...";
        _statusLabel.Text = $"Saving '{item.Name}' directly to {destinationFile}...";

        var sw = Stopwatch.StartNew();
        var progress = new Progress<FileTransferProgress>(p =>
        {
            if (IsDisposed || !IsHandleCreated) return;

            _progressBar.Value = Math.Clamp((int)p.Percentage, 0, 100);
            _statusLabel.Text = $"Downloading: {item.Name} ({FormatBytes(p.BytesTransferred)} / {FormatBytes(p.TotalBytes)} - {p.Percentage:0.0}%)";

            string etaStr = p.EstimatedTimeRemaining.HasValue
                ? $"ETA: {p.EstimatedTimeRemaining.Value:mm\\:ss}"
                : "ETA: --";
            _speedLabel.Text = $"⚡ {FormatSpeed(p.BytesPerSecond)} | {etaStr}";
        });

        bool ok = await _currentClient.PullFileAsync(item.Path, destinationFile, progress);

        sw.Stop();
        _progressBar.Value = ok ? 100 : 0;

        if (ok)
        {
            double totalSec = sw.Elapsed.TotalSeconds;
            double avgSpeed = totalSec > 0 ? item.Size / totalSec : 0;
            _statusLabel.Text = $"Saved '{item.Name}' successfully.";
            _speedLabel.Text = $"✓ {FormatSpeed(avgSpeed)} avg";

            MessageBox.Show(
                this,
                $"File successfully saved directly to:\n{destinationFile}\n\nSize: {FormatBytes(item.Size)}\nDuration: {totalSec:0.1}s\nAverage Speed: {FormatSpeed(avgSpeed)}",
                "Saved Successfully",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
        else
        {
            _statusLabel.Text = "File download failed.";
            _speedLabel.Text = "❌ Failed";
            MessageBox.Show(this, "Unable to pull file from Android. Please check connection.", "Transfer Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        _progressBar.Visible = false;
        _speedLabel.Text = string.Empty;
    }

    private async Task SaveSelectedFolderToLocationAsync()
    {
        if (_currentClient == null || _fileListView.SelectedItems.Count == 0) return;
        if (_fileListView.SelectedItems[0].Tag is not AndroidFileItem item || !item.IsDir)
        {
            MessageBox.Show(this, "Please select a folder to save.", "Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await DownloadFolderTreeAsync(item.Path, item.Name);
    }

    private async Task SaveCurrentFolderToPcAsync()
    {
        if (_currentClient == null) return;
        string folderName = Path.GetFileName(_currentPath.TrimEnd('/'));
        if (string.IsNullOrEmpty(folderName)) folderName = "AndroidStorage";
        await DownloadFolderTreeAsync(_currentPath, folderName);
    }

    private async Task DownloadFolderTreeAsync(string androidFolderPath, string defaultFolderName)
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = $"Select Windows destination directory to save '{defaultFolderName}'",
            UseDescriptionForTitle = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (fbd.ShowDialog(this) == DialogResult.OK)
        {
            string finalTarget = Path.Combine(fbd.SelectedPath, defaultFolderName);

            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 100;
            _progressBar.Value = 0;

            var progress = new Progress<FileTransferProgress>(p =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                _progressBar.Value = Math.Clamp((int)p.Percentage, 0, 100);
                string etaStr = p.EstimatedTimeRemaining.HasValue
                    ? $"ETA: {p.EstimatedTimeRemaining.Value:mm\\:ss}"
                    : "--";
                _speedLabel.Text = $"⚡ {FormatSpeed(p.BytesPerSecond)} | {etaStr}";
            });

            int downloadedCount = await _currentClient!.PullFolderRecursiveAsync(
                androidFolderPath,
                finalTarget,
                msg => { Invoke((Action)(() => _statusLabel.Text = msg)); },
                progress
            );

            _progressBar.Visible = false;
            _speedLabel.Text = string.Empty;
            _statusLabel.Text = $"Folder transfer completed ({downloadedCount} files saved).";

            MessageBox.Show(
                this,
                $"Folder successfully downloaded!\nTotal files saved: {downloadedCount}\nSaved directly to: {finalTarget}",
                "Folder Download Complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
        }
    }

    private async Task DownloadAndOpenFileAsync(AndroidFileItem item)
    {
        if (_currentClient == null) return;
        string tempPath = Path.Combine(Path.GetTempPath(), "MirrorSync_" + item.Name);

        _statusLabel.Text = $"Fetching {item.Name} for preview...";
        _progressBar.Visible = true;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Value = 0;

        var progress = new Progress<FileTransferProgress>(p =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            _progressBar.Value = Math.Clamp((int)p.Percentage, 0, 100);
            _statusLabel.Text = $"Previewing: {item.Name} ({FormatBytes(p.BytesTransferred)} / {FormatBytes(p.TotalBytes)} - {p.Percentage:0.0}%)";
            _speedLabel.Text = $"⚡ {FormatSpeed(p.BytesPerSecond)}";
        });

        bool ok = await _currentClient.PullFileAsync(item.Path, tempPath, progress);
        _progressBar.Visible = false;
        _speedLabel.Text = string.Empty;

        if (ok)
        {
            _statusLabel.Text = $"Opened {item.Name}";
            Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
        }
        else
        {
            _statusLabel.Text = "Failed to preview file.";
        }
    }

    private async Task UploadFileAsync()
    {
        if (_currentClient == null || !_currentClient.IsConnected) return;

        using var ofd = new OpenFileDialog
        {
            Title = "Select File to Upload to Android",
            Multiselect = false
        };

        if (ofd.ShowDialog(this) == DialogResult.OK)
        {
            string fileName = Path.GetFileName(ofd.FileName);
            string dest = $"{_currentPath.TrimEnd('/')}/{fileName}";

            _statusLabel.Text = $"Uploading {fileName}...";
            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 100;
            _progressBar.Value = 0;

            var progress = new Progress<FileTransferProgress>(p =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                _progressBar.Value = Math.Clamp((int)p.Percentage, 0, 100);
                _statusLabel.Text = $"Uploading: {fileName} ({FormatBytes(p.BytesTransferred)} / {FormatBytes(p.TotalBytes)} - {p.Percentage:0.0}%)";
                string etaStr = p.EstimatedTimeRemaining.HasValue ? $"ETA: {p.EstimatedTimeRemaining.Value:mm\\:ss}" : "--";
                _speedLabel.Text = $"⚡ {FormatSpeed(p.BytesPerSecond)} | {etaStr}";
            });

            bool ok = await _currentClient.PushFileDirectAsync(ofd.FileName, dest, progress);
            _progressBar.Visible = false;
            _speedLabel.Text = string.Empty;
            _statusLabel.Text = ok ? $"Uploaded '{fileName}' successfully." : "Upload failed.";
            await LoadDirectoryAsync(_currentPath);
        }
    }

    private async Task UploadFolderAsync()
    {
        if (_currentClient == null || !_currentClient.IsConnected) return;

        using var fbd = new FolderBrowserDialog
        {
            Description = "Select a Windows folder to upload into current Android directory",
            UseDescriptionForTitle = true
        };

        if (fbd.ShowDialog(this) == DialogResult.OK)
        {
            string folderName = new DirectoryInfo(fbd.SelectedPath).Name;
            string dest = $"{_currentPath.TrimEnd('/')}/{folderName}";

            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;

            int count = await _currentClient.PushFolderRecursiveAsync(
                fbd.SelectedPath,
                dest,
                msg => { Invoke((Action)(() => _statusLabel.Text = msg)); }
            );

            _progressBar.Visible = false;
            _statusLabel.Text = $"Uploaded {count} file(s) into '{dest}'.";
            await LoadDirectoryAsync(_currentPath);
        }
    }

    private async Task DeleteSelectedItemAsync()
    {
        if (_currentClient == null || _fileListView.SelectedItems.Count == 0) return;
        if (_fileListView.SelectedItems[0].Tag is not AndroidFileItem item) return;

        if (MessageBox.Show(this, $"Are you sure you want to permanently delete:\n{item.Name}?", "Confirm Deletion", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            if (string.IsNullOrEmpty(_currentClient.DeletionAuthToken))
            {
                string token = ShowInputDialog(this, "Enter 6-digit Deletion PIN displayed in the Android app:", "Deletion Authentication Required", "");
                if (string.IsNullOrWhiteSpace(token))
                {
                    _statusLabel.Text = "Deletion cancelled: Authentication PIN required.";
                    return;
                }
                _currentClient.DeletionAuthToken = token.Trim();
            }

            _statusLabel.Text = $"Deleting {item.Name}...";
            bool ok = await _currentClient.DeletePathDirectAsync(item.Path);
            if (!ok)
            {
                _currentClient.DeletionAuthToken = string.Empty;
                MessageBox.Show(this, "Deletion rejected by Android device!\nEnsure 'Allow Remote Deletions' is enabled on your phone and the PIN is correct.", "Authentication Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _statusLabel.Text = "Deletion unauthorized.";
            }
            else
            {
                _statusLabel.Text = "Deleted successfully.";
            }
            await LoadDirectoryAsync(_currentPath);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0) return "0.0 B/s";
        string[] suffixes = ["B/s", "KB/s", "MB/s", "GB/s"];
        int counter = 0;
        double speed = bytesPerSecond;
        while (speed >= 1024.0 && counter < suffixes.Length - 1)
        {
            speed /= 1024.0;
            counter++;
        }
        return $"{speed:0.1} {suffixes[counter]}";
    }

    private sealed record DeviceComboItem(DeviceClient Client, string Display)
    {
        public override string ToString() => Display;
    }
}