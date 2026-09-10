using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using UsbDocumentBackup.GoogleDrive;
using UsbDocumentBackup.Storage;

namespace UsbDocumentBackup.Windows;

/// <summary>
/// Writes a plain-text account of why the app is or is not doing what the user expects.
///
/// A tray app has nowhere to show this, and the interesting state lives in a SQLite file that no
/// tool on a stock Windows install can open. So the executable reports on itself: run it with
/// --diagnose and it writes the report and opens it.
///
/// Nothing secret is included. Token files are listed by name and size, the OAuth client by its
/// client id, and the client secret never appears.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiagnosticReport
{
    public static string Write(AppPaths paths, AppSettings settings, GoogleConnection google)
    {
        var report = new StringBuilder();
        void Line(string text = "") => report.AppendLine(text);

        Line($"UsbDocumentBackup 진단  ({DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss})");
        Line($"PC: {Environment.MachineName}   사용자: {Environment.UserName}");
        Line($"실행 파일: {Environment.ProcessPath}");
        Line($"데이터 폴더: {paths.StateRoot}");
        Line(new string('=', 70));

        // ---- Google ----
        Line();
        Line("[Google 연결]");
        Line($"  상태: {Describe(google.State)}");
        Line($"  클라이언트 설정: {(File.Exists(google.ClientSecretsFile) ? "가져온 파일 사용" : GoogleConnection.HasEmbeddedClientSecrets ? "실행 파일에 내장된 것 사용" : "없음")}");

        var tokens = SafeFiles(paths.CredentialsDirectory, "*.bin");
        if (tokens.Count > 0)
        {
            foreach (var t in tokens)
            {
                Line($"  저장된 토큰: {Path.GetFileName(t)} ({new FileInfo(t).Length} bytes, {File.GetLastWriteTime(t):yyyy-MM-dd HH:mm})");
            }
        }
        else
        {
            Line("  저장된 토큰: 없음");
            Line("  >> 이 PC에서 Google 연결을 한 적이 없습니다. 업로드는 전부 대기 상태로 남습니다.");
            Line("     토큰은 PC마다 따로 만들어집니다 (Windows DPAPI). 다른 PC에서 연결했어도");
            Line("     이 PC에는 적용되지 않습니다. 설정 화면에서 '연결'을 눌러야 합니다.");
        }

        Line($"  연결된 계정: {settings.GoogleAccountKey ?? "없음"}");
        Line($"  지정 계정:   {settings.ExpectedGoogleAccount ?? "없음"}");
        Line($"  Drive 폴더:  {settings.DriveFolderName} (id={settings.DriveFolderId ?? "아직 없음"})");

        // ---- backups and uploads ----
        Line();
        Line("[백업과 업로드]");
        try
        {
            var database = new BackupDatabase(paths.DatabaseFile);
            database.Migrate();
            var repository = new BackupRepository(database);

            var retained = repository.CountBackups(BackupState.Complete, BackupTier.Retained);
            var temporary = repository.CountBackups(BackupState.Complete, BackupTier.Temporary);
            Line($"  영구(Drive 대상) 백업: {retained}건");
            Line($"  임시 백업:             {temporary}건");
            Line($"  처리 중(미완료):        {repository.CountBackups(BackupState.Pending)}건");
            Line();
            Line($"  업로드 완료:   {repository.CountUploads(UploadState.Done)}건");
            Line($"  업로드 대기:   {repository.CountUploads(UploadState.Waiting)}건");
            Line($"  업로드 중:     {repository.CountUploads(UploadState.Uploading)}건");
            Line($"  조치 필요:     {repository.CountUploads(UploadState.NeedsAttention)}건");

            if (retained > 0 && repository.CountUploads(UploadState.Done) == 0 && tokens.Count == 0)
            {
                Line();
                Line("  >> 영구 백업은 있는데 업로드가 하나도 안 됐고 토큰도 없습니다.");
                Line("     원인은 이 PC에서 Google 연결을 하지 않은 것입니다.");
            }

            Line();
            Line("  최근 백업 10건:");
            foreach (var backup in repository.Search(null, 10))
            {
                var upload = repository.GetUpload(backup.Id);
                var uploadState = upload is null ? "큐 없음(임시 등급)" : upload.State.ToString();
                Line($"    {backup.BackedUpUtc.ToLocalTime():MM-dd HH:mm}  [{backup.Tier}]  {uploadState,-16}  {backup.FileName}");
                if (upload?.LastError is { Length: > 0 } error)
                {
                    Line($"        실패 사유: {Truncate(error, 100)}");
                }
            }

            Line();
            Line("  최근 문제 기록 10건:");
            var issues = repository.RecentIssues(10);
            if (issues.Count == 0)
            {
                Line("    (없음)");
            }

            foreach (var issue in issues)
            {
                Line($"    {issue.OccurredUtc.ToLocalTime():MM-dd HH:mm}  {issue.Kind}  {Truncate(issue.Path ?? string.Empty, 60)}");
            }
        }
        catch (Exception ex)
        {
            Line($"  상태 DB를 읽지 못했습니다: {ex.GetType().Name} {ex.Message}");
        }

        // ---- archive ----
        Line();
        Line("[보관함]");
        foreach (var (label, path) in new[] { ("영구", paths.RetainedRoot), ("임시", paths.TemporaryRoot) })
        {
            var files = SafeFiles(path, "*", recurse: true);
            var bytes = files.Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } });
            Line($"  {label}: {files.Count}개, {bytes / 1024.0 / 1024.0:N1} MB   ({path})");
        }

        // ---- startup ----
        Line();
        Line("[실행]");
        Line($"  자동 실행 등록: {(AutoStart.IsEnabled() ? "있음" : "없음")}");
        Line($"  일시정지: {settings.Paused}");
        Line($"  감시 시작: {(settings.MonitorSinceUtc is { } since ? since.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "전체")}");
        Line("    (이 시각 이전에 연 발표자료는 자동 대상이 아닙니다. 설정 창에서 따로 요청할 수 있습니다.)");
        Line($"  보관 기간: {settings.TemporaryRetentionDays}일");

        // ---- log ----
        Line();
        Line("[로그]");
        var logFile = Path.Combine(paths.LogDirectory, "app.log");
        if (File.Exists(logFile))
        {
            Line($"  마지막 기록: {File.GetLastWriteTime(logFile):yyyy-MM-dd HH:mm:ss}");
            Line();
            Line("  --- WARN / ERROR (최근 20건) ---");
            var lines = SafeReadLines(logFile);
            var problems = lines.Where(l => l.Contains("[WARN]", StringComparison.Ordinal) || l.Contains("[ERROR]", StringComparison.Ordinal))
                .TakeLast(20).ToList();
            foreach (var line in problems.Count > 0 ? problems : ["    (없음)"])
            {
                Line("    " + line);
            }

            Line();
            Line("  --- 마지막 15줄 ---");
            foreach (var line in lines.TakeLast(15))
            {
                Line("    " + line);
            }
        }
        else
        {
            Line("  로그 파일 없음");
        }

        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"UsbDocumentBackup-진단-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");

        File.WriteAllText(target, report.ToString(), new UTF8Encoding(true));
        return target;
    }

    /// <summary>Writes the report and opens it, for the --diagnose command line.</summary>
    public static void WriteAndShow(AppPaths paths, AppSettings settings, GoogleConnection google)
    {
        string target;
        try
        {
            target = Write(paths, settings, google);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "진단 정보를 만들지 못했습니다.\n\n" + ex.Message,
                "USB 문서 백업",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show($"진단 파일을 저장했습니다:\n\n{target}", "USB 문서 백업", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static string Describe(ConnectionState state) => state switch
    {
        ConnectionState.Connected => "연결됨",
        ConnectionState.ReconnectRequired => "재연결 필요 (권한 철회 또는 토큰 만료)",
        ConnectionState.NotConfigured => "클라이언트 설정 없음",
        _ => "연결 안 됨",
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "...";

    private static List<string> SafeFiles(string directory, string pattern, bool recurse = false)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, pattern, recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> SafeReadLines(string path)
    {
        try
        {
            // The log may be open for appending, so share the write handle.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
