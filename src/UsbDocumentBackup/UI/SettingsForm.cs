using UsbDocumentBackup.GoogleDrive;
using UsbDocumentBackup.Windows;

namespace UsbDocumentBackup.UI;

/// <summary>
/// First-run and ongoing settings. Shows plainly which folders are covered and which Google
/// account backups would go to, so neither is a surprise.
/// </summary>
public sealed class SettingsForm : Form
{
    private const int MiB = 1024 * 1024;

    private readonly AppHost _host;

    private readonly TextBox _archiveBox = new() { Width = 420, ReadOnly = true };
    private readonly Button _browseButton = new() { Text = "변경...", AutoSize = true };
    private readonly NumericUpDown _readLimit = new() { Minimum = 0, Maximum = 1024, Width = 90 };
    private readonly NumericUpDown _uploadLimit = new() { Minimum = 0, Maximum = 1024, Width = 90 };
    private readonly CheckBox _runAtLogin = new() { Text = "Windows 로그인 시 자동 실행", AutoSize = true };
    private readonly Label _googleLabel = new() { AutoSize = false, Width = 520, Height = 44 };
    private readonly Button _importSecretsButton = new() { Text = "클라이언트 설정 파일 선택...", AutoSize = true };
    private readonly Button _connectButton = new() { Text = "연결", AutoSize = true };
    private readonly Button _disconnectButton = new() { Text = "연결 해제", AutoSize = true };
    private readonly Label _monitorSinceLabel = new() { AutoSize = false, Width = 330, Height = 20 };
    private readonly Button _historyButton = new() { Text = "이전 발표자료도 백업...", AutoSize = true };
    private readonly Button _diagnoseButton = new() { Text = "진단 정보 저장...", AutoSize = true };
    private readonly Button _saveButton = new() { Text = "저장", AutoSize = true };
    private readonly Button _closeButton = new() { Text = "닫기", AutoSize = true };
    private readonly Label _noticeLabel = new() { AutoSize = false, Width = 520, Height = 40 };

    private string? _pendingArchiveRoot;

    public SettingsForm(AppHost host)
    {
        _host = host;

        Text = "USB 문서 백업 설정";
        Width = 660;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoSize = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var archiveRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        archiveRow.Controls.Add(_archiveBox);
        archiveRow.Controls.Add(_browseButton);

        layout.Controls.Add(new Label { Text = "백업 대상:", AutoSize = true }, 0, 0);
        layout.Controls.Add(
            new Label { Text = "USB의 .ppt / .pptx (하위 폴더 포함) + 이 PC에서 연 모든 .ppt / .pptx", AutoSize = true },
            1,
            0);

        layout.Controls.Add(new Label { Text = "보관 위치:", AutoSize = true }, 0, 1);
        layout.Controls.Add(archiveRow, 1, 1);

        layout.Controls.Add(new Label { Text = "USB 읽기 제한 (MiB/s):", AutoSize = true }, 0, 2);
        layout.Controls.Add(_readLimit, 1, 2);

        layout.Controls.Add(new Label { Text = "업로드 제한 (MiB/s):", AutoSize = true }, 0, 3);
        layout.Controls.Add(_uploadLimit, 1, 3);

        layout.Controls.Add(new Label { Text = "자동 실행:", AutoSize = true }, 0, 4);
        layout.Controls.Add(_runAtLogin, 1, 4);

        var monitorRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        monitorRow.Controls.Add(_monitorSinceLabel);
        monitorRow.Controls.Add(_historyButton);

        layout.Controls.Add(new Label { Text = "감시 시작:", AutoSize = true }, 0, 5);
        layout.Controls.Add(monitorRow, 1, 5);

        var googleRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        googleRow.Controls.Add(_importSecretsButton);
        googleRow.Controls.Add(_connectButton);
        googleRow.Controls.Add(_disconnectButton);

        layout.Controls.Add(new Label { Text = "Google 연결:", AutoSize = true }, 0, 6);
        layout.Controls.Add(_googleLabel, 1, 6);

        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 7);
        layout.Controls.Add(googleRow, 1, 7);

        layout.Controls.Add(new Label { Text = string.Empty, AutoSize = true }, 0, 8);
        layout.Controls.Add(_noticeLabel, 1, 8);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.Add(_closeButton);
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_diagnoseButton);

        Controls.Add(layout);
        Controls.Add(buttons);

        _browseButton.Click += (_, _) => ChooseArchiveRoot();
        _importSecretsButton.Click += (_, _) => ImportClientSecrets();
        _connectButton.Click += async (_, _) => await ConnectAsync().ConfigureAwait(true);
        _disconnectButton.Click += async (_, _) => await DisconnectAsync().ConfigureAwait(true);
        _historyButton.Click += (_, _) => BackUpOlderPresentations();
        _diagnoseButton.Click += (_, _) => SaveDiagnostics();
        _saveButton.Click += (_, _) => Save();
        _closeButton.Click += (_, _) => Close();

        Load();
    }

    private new void Load()
    {
        _archiveBox.Text = _host.Paths.ArchiveRoot;
        _readLimit.Value = Math.Clamp(_host.Settings.LocalReadBytesPerSecond / MiB, 0, 1024);
        _uploadLimit.Value = Math.Clamp(_host.Settings.UploadBytesPerSecond / MiB, 0, 1024);
        _runAtLogin.Checked = AutoStart.IsEnabled();

        RefreshGoogle();

        _monitorSinceLabel.Text = _host.Settings.MonitorSinceUtc is { } since
            ? $"{since.ToLocalTime():yyyy-MM-dd HH:mm} 이후 연 자료"
            : "전체";

        // Backup rows store a path relative to the archive root, so moving the root would leave
        // every existing record pointing at a file that is not there: the list would still show
        // them but every restore would fail. Until a verified migration exists, the root is fixed
        // once anything has been backed up.
        if (_host.Repository.CountBackups(Storage.BackupState.Complete) > 0
            || _host.Repository.CountBackups(Storage.BackupState.Pending) > 0)
        {
            _browseButton.Enabled = false;
            _noticeLabel.Text = "이미 보관 중인 백업이 있어 보관 위치를 바꿀 수 없습니다. "
                + "기존 기록이 새 위치를 가리키게 되어 복원이 실패하기 때문입니다.";
        }

        if (AutoStart.StableExecutablePath() is null)
        {
            _runAtLogin.Enabled = false;
            _noticeLabel.Text = "현재 실행 파일이 빌드/임시 경로에 있어 자동 실행을 등록하지 않습니다. 배포된 실행 파일에서 설정하세요.";
        }
    }

    private void RefreshGoogle()
    {
        var account = _host.Settings.GoogleAccountKey;
        _googleLabel.Text = _host.Google.State switch
        {
            ConnectionState.Connected => $"연결됨: {account}. 연 발표자료만 이 계정의 'USB Document Backups' 폴더로 올라갑니다.",
            ConnectionState.ReconnectRequired => $"재연결 필요 ({account}). 업로드만 멈추고 PC 백업과 복원은 계속됩니다.",
            ConnectionState.NotConfigured => "클라이언트 설정이 없습니다. Google Cloud에서 받은 데스크톱 클라이언트 JSON을 선택하세요 (docs/google-setup.md).",
            _ => "연결되지 않음. 업로드는 대기 상태로 남고 PC 백업과 복원은 그대로 동작합니다.",
        };

        _connectButton.Enabled = _host.Google.IsConfigured;
        _importSecretsButton.Text = _host.Google.IsConfigured
            ? "클라이언트 설정 교체..."
            : "클라이언트 설정 파일 선택...";
        _disconnectButton.Enabled = _host.Google.State is ConnectionState.Connected or ConnectionState.ReconnectRequired;
    }

    private void ImportClientSecrets()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Google Cloud에서 받은 OAuth 데스크톱 클라이언트 JSON을 선택하세요",
            Filter = "JSON 파일 (*.json)|*.json",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _host.Google.ImportClientSecrets(dialog.FileName);
            _noticeLabel.Text = "클라이언트 설정을 가져왔습니다. 이제 '연결'을 누르면 브라우저가 열립니다.";
        }
        catch (ClientSecretsRejectedException ex)
        {
            // A setup step the user just started, and picking the wrong file in the Cloud console
            // is easy. A dialog they have to dismiss beats a line of small text they can miss.
            MessageBox.Show(this, ex.Message, "이 파일은 쓸 수 없습니다", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _noticeLabel.Text = "OAuth 데스크톱 클라이언트 JSON이 필요합니다. docs/google-setup.md 참고.";
        }

        RefreshGoogle();
    }

    /// <summary>
    /// The only place a browser is ever launched. Token expiry never triggers this on its own.
    /// </summary>
    private async Task ConnectAsync()
    {
        _connectButton.Enabled = false;
        _noticeLabel.Text = "브라우저에서 Google 로그인과 권한 동의를 진행하세요...";
        try
        {
            var account = await _host.Google.ConnectAsync(CancellationToken.None).ConfigureAwait(true);

            // An installation can be pinned to one account ahead of time, so a stray login on a
            // shared PC cannot send someone's presentations to the wrong Drive.
            var expected = _host.Settings.ExpectedGoogleAccount;
            if (expected is { Length: > 0 } && !expected.Equals(account, StringComparison.OrdinalIgnoreCase))
            {
                await _host.Google.DisconnectAsync().ConfigureAwait(true);
                MessageBox.Show(
                    this,
                    $"이 설치본은 {expected} 계정 전용으로 지정돼 있는데 {account} 로 로그인했습니다."
                        + Environment.NewLine + Environment.NewLine
                        + "연결을 취소했습니다. 지정 계정으로 다시 로그인하거나, settings.json의 "
                        + "ExpectedGoogleAccount 값을 바꾸세요.",
                    "지정된 계정이 아닙니다",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                _noticeLabel.Text = $"{expected} 계정으로만 연결할 수 있습니다.";
                RefreshGoogle();
                return;
            }

            var previous = _host.Settings.GoogleAccountKey;
            if (previous is { Length: > 0 } && !previous.Equals(account, StringComparison.OrdinalIgnoreCase))
            {
                // Confirm before a backlog queued for one account starts flowing into another.
                var answer = MessageBox.Show(
                    this,
                    $"이전에 연결한 계정은 {previous} 인데 지금 {account} 로 로그인했습니다."
                        + Environment.NewLine + Environment.NewLine
                        + "대기 중인 백업을 새 계정으로 올릴까요?",
                    "계정이 다릅니다",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes)
                {
                    await _host.Google.DisconnectAsync().ConfigureAwait(true);
                    _noticeLabel.Text = "계정이 달라 연결을 취소했습니다.";
                    RefreshGoogle();
                    return;
                }

                _host.Settings.DriveFolderId = null;
            }

            _host.Settings.GoogleAccountKey = account;
            _host.SettingsStore.Save(_host.Settings);
            _host.Repository.RequeueAllNeedingAttention(DateTimeOffset.UtcNow);
            _noticeLabel.Text = $"{account} 계정에 연결했습니다. 대기 중인 업로드를 시작합니다.";
            _host.Coordinator.RequestScan(Backup.ScanReason.ChangeEvent);
        }
        catch (ClientSecretsRejectedException ex)
        {
            // A console misconfiguration the user has to go and fix, so it gets a dialog with the
            // actual steps rather than a raw OAuth error code in small text.
            MessageBox.Show(this, ex.Message, "Google 연결 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _noticeLabel.Text = "Google 연결에 실패했습니다. 방금 뜬 창과 docs/google-setup.md를 참고하세요.";
        }
        catch (Exception ex)
        {
            _noticeLabel.Text = "연결에 실패했습니다: " + ex.Message;
        }
        finally
        {
            RefreshGoogle();
        }
    }

    private async Task DisconnectAsync()
    {
        await _host.Google.DisconnectAsync().ConfigureAwait(true);
        _noticeLabel.Text = "연결을 해제했습니다. 보관된 백업과 Drive의 파일은 그대로 남습니다.";
        RefreshGoogle();
    }

    /// <summary>
    /// Backs up presentations opened before this app was installed. Off by default because
    /// PowerPoint's recent list can hold months of material, and uploading all of it the moment
    /// the app is installed is rarely what someone wants.
    /// </summary>
    private void BackUpOlderPresentations()
    {
        var count = _host.Coordinator.CountHistoricalCandidates();
        if (count == 0)
        {
            MessageBox.Show(
                "감시 시작 이전에 열었던 발표자료 중 지금도 남아 있는 파일이 없습니다.",
                "이전 발표자료",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"PowerPoint 최근 문서 목록에서 감시 시작 이전에 열었던 발표자료 {count}건을 찾았습니다."
                + Environment.NewLine + Environment.NewLine
                + "지금 백업하고 Drive에 올릴까요? 파일이 크면 시간이 걸릴 수 있습니다."
                + Environment.NewLine
                + "이번 한 번만 적용되며, 이후에는 다시 감시 시작 이후 자료만 처리합니다.",
            "이전 발표자료도 백업",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
        {
            return;
        }

        _host.Coordinator.RequestHistoricalScan();
        _noticeLabel.Text = $"이전 발표자료 {count}건을 처리합니다. 상태 창에서 진행 상황을 볼 수 있습니다.";
    }

    /// <summary>
    /// Writes the report to the desktop and opens it. Sits here because this is the window someone
    /// is already looking at when uploads are not happening, and it saves them a command line.
    /// </summary>
    private void SaveDiagnostics()
    {
        _diagnoseButton.Enabled = false;
        _noticeLabel.Text = "진단 정보를 만드는 중...";
        try
        {
            DiagnosticReport.WriteAndShow(_host.Paths, _host.Settings, _host.Google);
            _noticeLabel.Text = "바탕화면에 진단 파일을 저장했습니다. 비밀번호나 토큰은 들어 있지 않습니다.";
        }
        finally
        {
            _diagnoseButton.Enabled = true;
        }
    }

    private void ChooseArchiveRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "백업 보관 위치를 선택하세요. 이미 보관된 파일은 자동으로 옮기지 않습니다.",
            UseDescriptionForTitle = true,
            SelectedPath = _host.Paths.ArchiveRoot,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _pendingArchiveRoot = dialog.SelectedPath;
            _archiveBox.Text = dialog.SelectedPath;
        }
    }

    private void Save()
    {
        _host.Settings.LocalReadBytesPerSecond = (int)_readLimit.Value * MiB;
        _host.Settings.UploadBytesPerSecond = (int)_uploadLimit.Value * MiB;

        if (_pendingArchiveRoot is not null)
        {
            _host.Settings.ArchiveRoot = _pendingArchiveRoot;
        }

        var message = "저장했습니다.";

        if (_runAtLogin.Checked != AutoStart.IsEnabled())
        {
            if (_runAtLogin.Checked)
            {
                if (!AutoStart.Enable())
                {
                    _runAtLogin.Checked = false;
                    message = "자동 실행 등록에 실패했습니다: 실행 파일 경로가 안정적이지 않습니다.";
                }
            }
            else
            {
                AutoStart.Disable();
            }
        }

        _host.Settings.RunAtLogin = _runAtLogin.Checked;
        _host.SettingsStore.Save(_host.Settings);

        if (_pendingArchiveRoot is not null)
        {
            message += " 보관 위치와 읽기 제한 변경은 앱을 다시 시작하면 적용됩니다.";
            _pendingArchiveRoot = null;
        }

        _noticeLabel.Text = message;
    }
}
