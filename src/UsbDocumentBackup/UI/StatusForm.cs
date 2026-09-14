using System.Globalization;
using UsbDocumentBackup.Backup;
using UsbDocumentBackup.Storage;

namespace UsbDocumentBackup.UI;

/// <summary>
/// The one window a user needs day to day: what is backed up, what is waiting for Drive, what
/// needs attention, and a way to restore a chosen version.
/// </summary>
public sealed class StatusForm : Form
{
    private readonly AppHost _host;

    private readonly Label _summaryLabel = new() { AutoSize = false, Dock = DockStyle.Top, Height = 64, Padding = new Padding(4) };
    private readonly TextBox _searchBox = new() { Dock = DockStyle.Fill, PlaceholderText = "파일 이름 또는 경로 검색" };
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
    };

    private readonly Button _uploadButton = new() { Text = "선택 파일 Drive 업로드", AutoSize = true };
    private readonly Button _restoreButton = new() { Text = "선택 버전 복원...", AutoSize = true };
    private readonly Button _refreshButton = new() { Text = "새로 고침", AutoSize = true };
    private readonly Button _issuesButton = new() { Text = "문제 기록...", AutoSize = true };
    private readonly Label _resultLabel = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4) };

    public StatusForm(AppHost host)
    {
        _host = host;

        Text = "USB 문서 백업";
        Width = 900;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = true;

        _list.Columns.Add("파일", 220);
        _list.Columns.Add("보관", 90);
        _list.Columns.Add("원본 경로", 260);
        _list.Columns.Add("크기", 90, HorizontalAlignment.Right);
        _list.Columns.Add("백업 시각", 160);
        _list.Columns.Add("SHA-256", 120);

        var searchRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 34, ColumnCount = 2 };
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchRow.Controls.Add(_searchBox, 0, 0);
        searchRow.Controls.Add(_refreshButton, 1, 0);

        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight };
        buttonRow.Controls.Add(_uploadButton);
        buttonRow.Controls.Add(_restoreButton);
        buttonRow.Controls.Add(_issuesButton);

        Controls.Add(_list);
        Controls.Add(searchRow);
        Controls.Add(_summaryLabel);
        Controls.Add(buttonRow);
        Controls.Add(_resultLabel);

        _refreshButton.Click += (_, _) => Reload();
        _searchBox.TextChanged += (_, _) => Reload();
        _uploadButton.Click += (_, _) => UploadSelected();
        _restoreButton.Click += async (_, _) => await RestoreSelectedAsync().ConfigureAwait(true);
        _issuesButton.Click += (_, _) => ShowIssues();

        Reload();
    }

    public void Reload()
    {
        var status = _host.Coordinator.GetStatus();
        _summaryLabel.Text = string.Join(
            Environment.NewLine,
            $"연 발표자료(영구·Drive 대상): {status.RetainedComplete}건    임시 보관: {status.TemporaryComplete}건    처리 중: {status.LocalPending}건",
            $"Drive 업로드 완료: {status.UploadsDone}건    대기: {status.UploadsWaiting}건    조치 필요: {status.UploadsNeedAttention}건    상태: {(status.Paused ? "일시정지" : status.Activity)}",
            $"최근 문제 기록: {status.RecentIssues}건    마지막 검사: {Format(status.LastScanUtc)}");

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var backup in _host.Repository.Search(_searchBox.Text))
        {
            var item = new ListViewItem(backup.FileName) { Tag = backup };
            item.SubItems.Add(backup.Tier == Storage.BackupTier.Retained ? "영구·Drive" : "임시");
            item.SubItems.Add(backup.RelativePath);
            item.SubItems.Add(FormatSize(backup.SizeBytes));
            item.SubItems.Add(backup.BackedUpUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
            item.SubItems.Add(backup.Sha256[..12]);
            _list.Items.Add(item);
        }

        _list.EndUpdate();
    }

    /// <summary>
    /// Sends the selected backup to Drive now.
    ///
    /// Automatic uploading only covers presentations PowerPoint recorded as opened since monitoring
    /// began. Anything else -- a deck found on a stick but never opened here, or one opened before
    /// the app was installed -- sits in the temporary tier, and this is how it gets picked.
    /// </summary>
    private void UploadSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not BackupRecord record)
        {
            _resultLabel.Text = "업로드할 파일을 먼저 선택하세요.";
            return;
        }

        if (_host.Google.State != GoogleDrive.ConnectionState.Connected)
        {
            _resultLabel.Text = "Google에 연결되어 있지 않습니다. 설정에서 연결한 뒤 다시 시도하세요. (요청은 대기열에 남습니다)";
        }

        var result = _host.BackupService.RequestUpload(record.Id);
        _resultLabel.Text = result switch
        {
            UploadRequestResult.Queued =>
                $"'{record.FileName}' 을(를) 업로드 대기열에 넣었습니다. 잠시 후 상태가 바뀝니다.",
            UploadRequestResult.AlreadyUploaded =>
                $"'{record.FileName}' 은(는) 이미 Drive에 올라가 있습니다.",
            UploadRequestResult.NotReady =>
                "아직 백업이 끝나지 않은 파일입니다.",
            UploadRequestResult.NotFound =>
                "선택한 백업 기록을 찾을 수 없습니다. 목록을 새로 고쳐 주세요.",
            _ =>
                "업로드 대기열에 넣지 못했습니다. 문제 기록을 확인하세요.",
        };

        if (result == UploadRequestResult.Queued)
        {
            _host.Coordinator.RequestScan(ScanReason.ChangeEvent);
        }

        Reload();
    }

    private async Task RestoreSelectedAsync()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not BackupRecord record)
        {
            _resultLabel.Text = "복원할 버전을 먼저 선택하세요.";
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "복원할 폴더를 선택하세요. 같은 이름의 파일이 있으면 덮어쓰지 않고 새 이름으로 저장합니다.",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _restoreButton.Enabled = false;
        _resultLabel.Text = "복원 중...";
        try
        {
            var result = await _host.RestoreService.RestoreAsync(record.Id, dialog.SelectedPath).ConfigureAwait(true);
            _resultLabel.Text = result.Succeeded
                ? $"복원 완료 (SHA-256 확인됨): {result.RestoredPath}"
                : $"복원 실패 [{result.Status}] {result.Message}";
        }
        finally
        {
            _restoreButton.Enabled = true;
        }
    }

    private void ShowIssues()
    {
        var issues = _host.Repository.RecentIssues(50);
        var text = issues.Count == 0
            ? "기록된 문제가 없습니다."
            : string.Join(
                Environment.NewLine,
                issues.Select(i =>
                    $"{i.OccurredUtc.ToLocalTime():yyyy-MM-dd HH:mm} [{i.Kind}] {i.Path}{(i.Detail is null ? string.Empty : " - " + i.Detail)}"));

        using var dialog = new Form { Text = "문제 기록", Width = 800, Height = 460, StartPosition = FormStartPosition.CenterParent };
        dialog.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Text = text,
        });
        dialog.ShowDialog(this);
    }

    private static string Format(DateTimeOffset? value) =>
        value is null ? "없음" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
