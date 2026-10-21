namespace WyvernCenter.Core.Safety;

/// <summary>Append-only record of every hardware write attempt.</summary>
public sealed class WriteLog
{
    private readonly string _path;
    private readonly object _lock = new();

    public WriteLog(string path) => _path = path;

    public void Append(string line)
    {
        lock (_lock)
        {
            try
            {
                // Never creates the folder: the app prepares it (and leaves it absent on purpose, e.g. mid-migration).
                if (!Directory.Exists(Path.GetDirectoryName(_path))) return;
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}");
            }
            catch
            {
                // logging must never break a hardware operation
            }
        }
    }
}
