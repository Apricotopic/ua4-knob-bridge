namespace Ua4KnobBridge;

public enum KnobAction
{
    Clockwise,
    CounterClockwise
}

public interface IKeyboardKnobInput : IDisposable
{
    event EventHandler<KnobAction>? KnobActionReceived;
    event EventHandler<string>? Diagnostic;
    bool IsConnected { get; }
    string? DevicePath { get; }
    void Register(nint windowHandle);
    void HandleRawInput(nint rawInputHandle);
    void RefreshDevices();
}

public interface IUa4VolumeController : IDisposable
{
    bool IsConnected { get; }
    int? CurrentVolume { get; }
    string? DevicePath { get; }
    void Refresh();
    int ReadVolume();
    int ChangeVolume(int delta);
    int SetVolume(int volume);
}
