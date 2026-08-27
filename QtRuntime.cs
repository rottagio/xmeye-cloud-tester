using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace XMEyeCloudTester;

internal sealed class QtRuntime : IDisposable
{
    private IntPtr application;
    private IntPtr argv;
    private IntPtr programName;
    private DispatcherTimer? eventPump;

    internal void Initialize()
    {
        if (application != IntPtr.Zero) return;
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

    internal void SetPumpInterval(TimeSpan interval)
    {
        if (eventPump is not null && eventPump.Interval != interval)
            eventPump.Interval = interval;
    }

    private void PumpEvents(object? sender, EventArgs e) => ProcessEvents();

    public void Dispose()
    {
        if (eventPump != null)
        {
            eventPump.Stop();
            eventPump.Tick -= PumpEvents;
            eventPump = null;
        }
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
