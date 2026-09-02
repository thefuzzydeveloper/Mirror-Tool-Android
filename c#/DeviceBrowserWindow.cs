using System.Diagnostics;

namespace WiFiAutoStreamSync;

public sealed class DeviceBrowserWindow : Form
{
    private readonly SyncEngine _engine;
    private readonly ComboBox _deviceSelector;
    private readonly TextBox _pathBox;
    private readonly ListView _fileListView;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripProgressBar _progressBar;
    private readonly ImageList _iconsList;

    private DeviceClient? _currentClient;
    private string _currentPath = "/storage/emulated/0";

    public DeviceBrowserWindow(SyncEngine engine)
    {
        _engine = engine;

        Text = "Android Wireless Device & Storage Explorer";
        Size = new Size(1060, 680);
        MinimumSize = new Size(860, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        TopMost = false;
        ShowInTaskbar = true;
        _iconsList = new ImageList { ImageSize = new Size(18, 18), ColorDepth = ColorDepth.Depth32Bit };
        _iconsList.Images.Add("folder", CreateFolderBitmap());
        _iconsList.Images.Add("file", SystemIcons.Application);

        // Top Control: Device Selector
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 8, 12, 8), BackColor = Color.FromArgb(241, 245, 249) };
        var lblDev = new Label { Text = "Connected Android Device:", AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(0, 6, 8, 0), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        _deviceSelector = new ComboBox { Dock = DockStyle.Left, Width = 380, DropDownStyle = ComboBoxStyle.DropDownList };
        _deviceSelector.SelectedIndexChanged += async (s, e) => await OnDeviceSelectionChangedAsync();

        var btnRefreshDevs = new Button { Text = "🔄 Rescan Devices", Dock = DockStyle.Right, Width = 140 };
        btnRefreshDevs.Click += (s, e) => PopulateDeviceList();

        topPanel.Controls.Add(_deviceSelector);
        topPanel.Controls.Add(lblDev);
        topPanel.Controls.Add(btnRefreshDevs);

        // Navigation & Action Toolbar
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

        var btnSaveCurrentFolder = new Button { Text = "💾 Save Folder to PC...", Width = 170, Dock = DockStyle.Right, BackColor = Color.FromArgb(16, 185, 129), ForeColor = Color.White };
        btnSaveCurrentFolder.Click += async (s, e) => await SaveCurrentFolderToPcAsync();

        var btnUpload = new Button { Text = "⬆ Upload File...", Width = 120, Dock = DockStyle.Right, BackColor = Color.FromArgb(2, 132, 199), ForeColor = Color.White };
        btnUpload.Click += async (s, e) => await UploadFileAsync();

        var btnUploadFolder = new Button { Text = "📁 Upload Folder...", Width = 135, Dock = DockStyle.Right };
        btnUploadFolder.Click += async (s, e) => await UploadFolderAsync();

        var btnInspectManifest = new Button { Text = "📋 Manifest", Width = 100, Dock = DockStyle.Right };
        btnInspectManifest.Click += (s, e) => Program.ShowManifestInspector();

        var navMiddle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0) };
        navMiddle.Controls.Add(_pathBox);

        navPanel.Controls.Add(navMiddle);
        navPanel.Controls.Add(btnRefresh);
        navPanel.Controls.Add(btnUp);
        navPanel.Controls.Add(btnSaveCurrentFolder);
        navPanel.Controls.Add(btnUploadFolder);
        navPanel.Controls.Add(btnUpload);
        navPanel.Controls.Add(btnInspectManifest);

        // File List Details
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

        // Context Menu for Items
        var ctxMenu = new ContextMenuStrip();
        ctxMenu.Items.Add("💾 Save File to Specific PC Location...", null, async (s, e) => await SaveSelectedFileToLocationAsync());
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

        // Bottom Status Bar
        var statusStrip = new StatusStrip();
        _statusLabel = new ToolStripStatusLabel { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _progressBar = new ToolStripProgressBar { Width = 160, Visible = false };
        statusStrip.Items.Add(_statusLabel);
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

    private async Task CreateNewFolderAsync()
    {
        if (_currentClient == null || !_currentClient.IsConnected) return;

        string folderName = ShowInputDialog(this, "Enter new folder name:", "Create Folder on Android", "NewFolder");
        if (string.IsNullOrWhiteSpace(folderName)) return;

        string target = $"{_currentPath.TrimEnd('/')}/{folderName.Trim()}";
        bool ok = await _currentClient.CreateDirectoryDirectAsync(target);
        if (ok) await LoadDirectoryAsync(_currentPath);
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

        // Folder tab
        using var tabBrush = new SolidBrush(Color.FromArgb(217, 119, 6)); // amber-600
        g.FillRectangle(tabBrush, 1, 2, 7, 4);

        // Folder body
        using var bodyBrush = new SolidBrush(Color.FromArgb(245, 158, 11)); // amber-500
        g.FillRectangle(bodyBrush, 1, 5, 16, 11);

        // Border outline
        using var borderPen = new Pen(Color.FromArgb(180, 83, 9), 1f); // amber-700
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

    /// <summary>
    /// Prompts user with a standard Windows SaveFileDialog to save any Android file to an exact PC location.
    /// </summary>
    private async Task SaveSelectedFileToLocationAsync()
    {
        if (_currentClient == null || _fileListView.SelectedItems.Count == 0) return;
        if (_fileListView.SelectedItems[0].Tag is not AndroidFileItem item || item.IsDir)
        {
            MessageBox.Show(this, "Please select a file to save.", "Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Title = $"Save '{item.Name}' to Windows PC",
            FileName = item.Name,
            Filter = "All Files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (sfd.ShowDialog(this) == DialogResult.OK)
        {
            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;
            _statusLabel.Text = $"Downloading {item.Name} to PC...";

            bool ok = await _currentClient.PullFileAsync(item.Path, sfd.FileName);
            _progressBar.Visible = false;

            if (ok)
            {
                _statusLabel.Text = $"Saved {item.Name} successfully.";
                MessageBox.Show(this, $"File successfully saved to:\n{sfd.FileName}", "Saved Successfully", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _statusLabel.Text = "File download failed.";
                MessageBox.Show(this, "Unable to pull file from Android. Please check connection.", "Transfer Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    /// <summary>
    /// Prompts user with a standard FolderBrowserDialog to save an Android folder and its contents to Windows.
    /// </summary>
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
            Description = $"Select Windows destination folder to save '{defaultFolderName}'",
            UseDescriptionForTitle = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (fbd.ShowDialog(this) == DialogResult.OK)
        {
            string finalTarget = Path.Combine(fbd.SelectedPath, defaultFolderName);

            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;

            int downloadedCount = await _currentClient!.PullFolderRecursiveAsync(
                androidFolderPath,
                finalTarget,
                msg => { Invoke((Action)(() => _statusLabel.Text = msg)); }
            );

            _progressBar.Visible = false;
            _statusLabel.Text = $"Folder transfer completed ({downloadedCount} files saved).";

            MessageBox.Show(
                this,
                $"Folder successfully downloaded!\nTotal files saved: {downloadedCount}\nSaved to: {finalTarget}",
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

        _statusLabel.Text = $"Fetching {item.Name}...";
        bool ok = await _currentClient.PullFileAsync(item.Path, tempPath);
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
            string dest = $"{_currentPath.TrimEnd('/')}/{Path.GetFileName(ofd.FileName)}";
            _statusLabel.Text = $"Uploading {Path.GetFileName(ofd.FileName)}...";
            _progressBar.Visible = true;
            _progressBar.Style = ProgressBarStyle.Marquee;

            bool ok = await _currentClient.PushFileDirectAsync(ofd.FileName, dest);
            _progressBar.Visible = false;
            _statusLabel.Text = ok ? "Upload completed." : "Upload failed.";
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
            _statusLabel.Text = $"Deleting {item.Name}...";
            bool ok = await _currentClient.DeletePathDirectAsync(item.Path);
            _statusLabel.Text = ok ? "Deleted successfully." : "Deletion failed.";
            await LoadDirectoryAsync(_currentPath);
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    private sealed record DeviceComboItem(DeviceClient Client, string Display)
    {
        public override string ToString() => Display;
    }
}