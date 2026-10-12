namespace StateMachine;

internal static class AppShutdown
{
    private static int _requested;
    private static Timer? _deadline;
    public static bool Requested => Volatile.Read(ref _requested) != 0;
    public static void Begin()
    {
        if (Interlocked.Exchange(ref _requested,1)!=0)return;
        // Cleanup can stall on Explorer IPC, script restore or a WebView close callback.
        _deadline=new Timer(_=>Environment.Exit(0),null,3000,Timeout.Infinite);
        DesktopCli.CancelAll();
    }
    public static void Complete()
    {
        _deadline?.Dispose();
        Environment.Exit(0);
    }
}
