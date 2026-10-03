namespace Ua4KnobBridge;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string ActivateEventName = @"Local\Ua4KnobBridge.Activate";
    private const string ExitEventName = @"Local\Ua4KnobBridge.Exit";
    private readonly EventWaitHandle activate = new(false, EventResetMode.AutoReset, ActivateEventName);
    private readonly EventWaitHandle exit = new(false, EventResetMode.AutoReset, ExitEventName);
    public string? TakePendingCommand()
    {
        if (exit.WaitOne(0)) return "exit";
        return activate.WaitOne(0) ? "activate" : null;
    }

    public static void NotifyExistingInstance(string command)
    {
        string eventName = command.Equals("exit", StringComparison.OrdinalIgnoreCase) ? ExitEventName : ActivateEventName;
        try
        {
            using EventWaitHandle signal = EventWaitHandle.OpenExisting(eventName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        activate.Dispose();
        exit.Dispose();
    }
}
