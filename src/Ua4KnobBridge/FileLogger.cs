namespace Ua4KnobBridge;

public sealed class FileLogger
{
    private readonly object gate = new();
    public event EventHandler<string>? MessageWritten;
    public string LogPath => Path.Combine(BridgeSettings.DataDirectory, "bridge.log");

    public void Write(string message)
    {
        string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(BridgeSettings.DataDirectory);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch { }
        }
        MessageWritten?.Invoke(this, line);
    }
}
