using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace XMEyeCloudTester;

internal enum QtPumpState
{
    Active,
    VisibleIdle,
    MinimizedIdle
}

internal sealed class QtRuntime : IDisposable
{
    private IntPtr application;
    private IntPtr argv;
    private IntPtr programName;
    private DispatcherTimer? eventPump;
    private Func<QtPumpState>? pumpStateProvider;

    internal void Initialize(Func<QtPumpState>? stateProvider = null)
    {
        if (application != IntPtr.Zero) return;
        pumpStateProvider = stateProvider;
        application = Marshal.AllocHGlobal(512);
        programName = Marshal.StringToHGlobalAnsi("XMEyeCloudTester");
        argv = Marshal.AllocHGlobal(IntPtr.Size * 2);
        Marshal.WriteIntPtr(argv, 0, programName);
        Marshal.WriteIntPtr(argv, IntPtr.Size, IntPtr.Zero);
        int argc = 1;
        QApplicationConstructor(application, ref argc, argv, 0);
        eventPump = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(25)
        };
        eventPump.Tick += PumpEvents;
        eventPump.Start();
    }

    internal void ProcessEvents() => QCoreApplicationProcessEvents(0, 1);

    internal static TimeSpan IntervalFor(QtPumpState state) => state switch
    {
        QtPumpState.Active => TimeSpan.FromMilliseconds(25),
        QtPumpState.VisibleIdle => TimeSpan.FromMilliseconds(100),
        _ => TimeSpan.FromMilliseconds(250)
    };

    private void PumpEvents(object? sender, EventArgs e)
    {
        ProcessEvents();
        if (eventPump is null || pumpStateProvider is null)
            return;

        TimeSpan desiredInterval = IntervalFor(pumpStateProvider());
        if (eventPump.Interval != desiredInterval)
            eventPump.Interval = desiredInterval;
    }

    public void Dispose()
    {
        if (eventPump != null)
        {
            eventPump.Stop();
            eventPump.Tick -= PumpEvents;
            eventPump = null;
        }
        pumpStateProvider = null;
        if (application != IntPtr.Zero)
        {
            try { QApplicationDestructor(application); } catch { }
            Marshal.FreeHGlobal(application);
            application = IntPtr.Zero;
        }
        if (argv != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(argv);
            argv = IntPtr.Zero;
        }
        if (programName != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(programName);
            programName = IntPtr.Zero;
        }
    }

    [DllImport("Qt5Widgets.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "??0QApplication@@QEAA@AEAHPEAPEADH@Z", ExactSpelling = true)]
    private static extern IntPtr QApplicationConstructor(IntPtr self, ref int argc, IntPtr argv, int flags);

    [DllImport("Qt5Widgets.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "??1QApplication@@UEAA@XZ", ExactSpelling = true)]
    private static extern void QApplicationDestructor(IntPtr self);

    [DllImport("Qt5Core.dll", CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "?processEvents@QCoreApplication@@SAXV?$QFlags@W4ProcessEventsFlag@QEventLoop@@@@H@Z",
        ExactSpelling = true)]
    private static extern void QCoreApplicationProcessEvents(int flags, int maximumTimeMilliseconds);
}
