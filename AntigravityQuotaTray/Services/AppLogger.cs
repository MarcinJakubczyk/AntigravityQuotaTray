using System.IO;
using System.Text;

namespace AntigravityQuotaTray.Services;

public sealed class AppLogger
{
    private const long DefaultMaxBytes = 2 * 1024 * 1024; // 2 MB

    private readonly object _gate = new();
    private readonly long _maxBytes;

    public string DirectoryPath { get; }
    public string FilePath { get; }
    public string ArchiveFilePath { get; }

    public AppLogger(string? directoryPath = null, long maxBytes = DefaultMaxBytes)
    {
        DirectoryPath = directoryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AntigravityQuotaTray");
        FilePath = Path.Combine(DirectoryPath, "app.log");
        ArchiveFilePath = Path.Combine(DirectoryPath, "app.log.1");
        _maxBytes = Math.Max(4096, maxBytes);
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        try
        {
            var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
            var pid = Environment.ProcessId;
            var sb = new StringBuilder();
            sb.Append('[').Append(timestamp).Append("] [").Append(level).Append("] [pid:").Append(pid).Append("] ").AppendLine(message);
            if (ex is not null)
            {
                sb.AppendLine(ex.ToString());
            }

            lock (_gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();
                File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the application
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        var info = new FileInfo(FilePath);
        if (info.Length < _maxBytes)
        {
            return;
        }

        File.Move(FilePath, ArchiveFilePath, overwrite: true);
    }
}
