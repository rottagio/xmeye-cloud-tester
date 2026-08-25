using System.Runtime.InteropServices;

namespace XMEyeCloudTester;

internal sealed class H264FilePlayer : IDisposable
{
    private int port = -1;
    private bool opened;
    private bool paused;
    private bool sounding;

    internal bool IsOpen => opened;
    internal bool IsPaused => paused;
    internal bool IsSounding => sounding;
    internal int DurationSeconds => opened ? H264_PLAY_GetFileTime(port) : 0;
    internal int PositionSeconds => opened ? H264_PLAY_GetPlayedTime(port) : 0;

    internal static int ReadDurationSeconds(string path)
    {
        if (H264_PLAY_GetPort(out int probePort) == 0 || probePort < 0)
            return 0;
        try
        {
            if (H264_PLAY_OpenFile(probePort, path) == 0)
                return 0;
            int seconds = H264_PLAY_GetFileTime(probePort);
            H264_PLAY_CloseFile(probePort);
            return Math.Max(0, seconds);
        }
        finally
        {
            H264_PLAY_FreePort(probePort);
        }
    }

    internal bool Open(string path, IntPtr windowHandle, bool playSound)
    {
        Close();
        if (H264_PLAY_GetPort(out port) == 0 || port < 0)
            return false;
        if (H264_PLAY_OpenFile(port, path) == 0)
        {
            H264_PLAY_FreePort(port);
            port = -1;
            return false;
        }
        opened = true;
        if (H264_PLAY_Play(port, windowHandle) == 0)
        {
            Close();
            return false;
        }
        paused = false;
        if (playSound)
            SetSound(true);
        return true;
    }

    internal bool TogglePause()
    {
        if (!opened)
            return false;
        paused = !paused;
        if (H264_PLAY_Pause(port, paused ? 1 : 0) == 0)
            paused = !paused;
        return paused;
    }

    internal bool SetSound(bool enabled)
    {
        if (!opened)
            return false;
        int result = enabled ? H264_PLAY_PlaySound(port) : H264_PLAY_StopSound(port);
        if (result != 0)
            sounding = enabled;
        return result != 0;
    }

    internal void Seek(double fraction)
    {
        if (opened)
            H264_PLAY_SetPlayPos(port, (float)Math.Clamp(fraction, 0, 1));
    }

    internal void Close()
    {
        if (port < 0)
            return;
        try { if (sounding) H264_PLAY_StopSound(port); } catch { }
        try { if (opened) H264_PLAY_Stop(port); } catch { }
        try { if (opened) H264_PLAY_CloseFile(port); } catch { }
        try { H264_PLAY_FreePort(port); } catch { }
        port = -1;
        opened = false;
        paused = false;
        sounding = false;
    }

    public void Dispose() => Close();

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_GetPort(out int port);

    [DllImport("H264Play.dll", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_OpenFile(int port, string fileName);

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_CloseFile(int port);

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_Play(int port, IntPtr windowHandle);

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_Stop(int port);

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_Pause(int port, int pause);

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_PlaySound(int port);

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_StopSound(int port);

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_GetFileTime(int port);

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_GetPlayedTime(int port);

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_SetPlayPos(int port, float position);

    [DllImport("H264Play.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int H264_PLAY_FreePort(int port);
}
