using Microsoft.Win32;

namespace Ua4KnobBridge;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Ua4KnobBridge";
    private const string StartupArgument = "--startup";

    public static string Command
    {
        get
        {
            string? processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
                throw new InvalidOperationException("Windows did not provide the current executable path.");
            return $"{Quote(processPath)} {StartupArgument}";
        }
    }

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        string? current = key.GetValue(ValueName) as string;
        if (enabled)
        {
            if (current is not null && !IsOurCommand(current))
                throw new InvalidOperationException($"The startup value '{ValueName}' is already owned by another command and was left unchanged.");
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else if (current is not null && IsOurCommand(current))
        {
            key.DeleteValue(ValueName, false);
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static bool IsOurCommand(string value) =>
        value.Contains("Ua4KnobBridge.exe", StringComparison.OrdinalIgnoreCase) &&
        value.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase);
}
