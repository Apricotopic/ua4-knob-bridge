using System.Text.Json;

namespace Ua4KnobBridge;

public sealed class BridgeSettings
{
    public bool Enabled { get; set; } = true;

    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ua4KnobBridge");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static BridgeSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(SettingsPath)) ?? new BridgeSettings();
        }
        catch { }
        return new BridgeSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        string temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, true);
    }
}
