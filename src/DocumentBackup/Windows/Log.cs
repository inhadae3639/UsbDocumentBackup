using System.Globalization;
using System.Text;

namespace DocumentBackup.Windows;

/// <summary>
/// Minimal size-capped file log. Never records tokens, authorization codes or upload session URLs.
/// </summary>
public sealed class Log
{
    private const long MaxBytes = 1024 * 1024;
    private const int MaxFiles = 3;

    private readonly string _directory;
    private readonly object _gate = new();

    public Log(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string CurrentFile => Path.Combine(_directory, "app.log");

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : message + " :: " + exception.GetType().Name + ": " + exception.Message);

    private void Write(string level, string message)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}");

        lock (_gate)
        {
            try
            {
                Roll();
                File.AppendAllText(CurrentFile, line, new UTF8Encoding(false));
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private void Roll()
    {
        var current = new FileInfo(CurrentFile);
        if (!current.Exists || current.Length < MaxBytes)
        {
            return;
        }

        var oldest = Path.Combine(_directory, $"app.{MaxFiles}.log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = MaxFiles - 1; i >= 1; i--)
        {
            var from = Path.Combine(_directory, $"app.{i}.log");
            if (File.Exists(from))
            {
                File.Move(from, Path.Combine(_directory, $"app.{i + 1}.log"), overwrite: true);
            }
        }

        File.Move(CurrentFile, Path.Combine(_directory, "app.1.log"), overwrite: true);
    }
}
