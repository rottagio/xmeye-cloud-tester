using System.Runtime.InteropServices;

namespace XMEyeCloudTester;

internal static class CmsSdk
{
    internal enum MessageType
    {
        SearchDevice, DeviceControl, ChannelControl, VideoWindowControl,
        DeviceRemoteConfig, UserConfig, QueryRecords, AppError, Alarm
    }

    internal enum StreamType { Main, Extra, Close }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void MessageCallback(
        MessageType type, int p1, int p2, int p3, int p4,
        IntPtr text1, IntPtr text2, uint size, IntPtr user);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Size = 792)]
    internal struct DeviceInfo
    {
        public int ID;
        public int GroupID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string IP;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string UserName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Password;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Mac;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string CloudSn;
        public int CloudType;
        public int Port;
        public int AnalogChannels;
        public int DigitalChannels;
        public int AlarmInputs;
        public int AlarmChannels;
        public int Error;
        public int LoginType;
        public int Type;
        public int LoginHandle;
    }

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_Init(string configPath, MessageCallback callback, IntPtr user, int clientType);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_UnInit();

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_UserLogin(string user, string password, int type, IntPtr cloudIp);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_AddDeviceByID(
        string cloudId, string user, string password, int vendor,
        string name, int groupId, int loginType);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_GetDeviceByCloudID(string cloudId, int loginType, ref DeviceInfo info);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_DeviceLoginOrLogout(int deviceId, [MarshalAs(UnmanagedType.I1)] bool login);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_CreatePlayWindow(int windowNumber, int windowHandle, int type);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_StartPreview(
        int deviceId, int windowNumber, int channel, StreamType stream,
        [MarshalAs(UnmanagedType.I1)] bool openDevice);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_StopPreviewByWnd(int windowNumber, int userSource);
}
