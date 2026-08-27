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

    [StructLayout(LayoutKind.Explicit, Size = 0x44)]
    internal struct GroupInfo
    {
        [FieldOffset(0)]
        public int ID;
    }

    // Estrutura reconstruída a partir do construtor RecordPlanUnit do VMS Pro.
    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    internal struct RecordPlanUnit
    {
        [FieldOffset(0x00)] public int Window;
        [FieldOffset(0x04)] public byte Enabled;
        [FieldOffset(0x06)] public ushort DaysMask;
        [FieldOffset(0x08)] public int StartSecond;
        [FieldOffset(0x0C)] public int EndSecond;
        [FieldOffset(0x10)] public byte Day0;
        [FieldOffset(0x11)] public byte Day1;
        [FieldOffset(0x12)] public byte Day2;
        [FieldOffset(0x13)] public byte Day3;
        [FieldOffset(0x14)] public byte Day4;
        [FieldOffset(0x15)] public byte Day5;
        [FieldOffset(0x16)] public byte Day6;
        [FieldOffset(0x17)] public byte Day7;

        internal static RecordPlanUnit Create(int window, bool enabled) => new()
        {
            Window = window,
            Enabled = enabled ? (byte)1 : (byte)0,
            DaysMask = 1,
            EndSecond = 0x1517F,
            Day0 = 1, Day1 = 1, Day2 = 1, Day3 = 1,
            Day4 = 1, Day5 = 1, Day6 = 1, Day7 = 1
        };
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void MessageCallback(
        MessageType type, int p1, int p2, int p3, int p4,
        IntPtr text1, IntPtr text2, uint size, IntPtr user);

    // Esta CMSClient.dll copia 0x578 bytes para a estrutura.
    [StructLayout(LayoutKind.Explicit, Size = 0x578)]
    internal unsafe struct DeviceInfo
    {
        [FieldOffset(0x000)]
        public int ID;
        [FieldOffset(0x168)]
        public fixed byte AdminToken[0x100];
        [FieldOffset(0x280)]
        public int Error;
        [FieldOffset(0x284)]
        public int LoginType;
        [FieldOffset(0x28C)]
        public int LoginHandle;
        [FieldOffset(0x29C)]
        public int ConnectionType;
        [FieldOffset(0x3D1)]
        public fixed byte OemId[0x10];
        // Flags por canal preenchidas por Uart.PTZControlCmd no VMS Pro.
        [FieldOffset(0x4A1)]
        public fixed byte FlipOperation[0x40];
        [FieldOffset(0x4E1)]
        public fixed byte MirrorOperation[0x40];
        [FieldOffset(0x56D)]
        public byte Shared;
        [FieldOffset(0x56E)]
        public byte LoginProbeComplete;
        [FieldOffset(0x56F)]
        public byte RpsHint;
    }

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_Init(string configPath, MessageCallback callback, IntPtr user, int clientType);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_UnInit();

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CMS_Client_IsFinishReadLocalDevInfo();

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_UserLogin(string user, string password, int type, IntPtr cloudIp);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_InitMqtt(string cloudToken);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_SetAutoCheckDevStatus(
        [MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_SetBindCloudState(
        [MarshalAs(UnmanagedType.I1)] bool bound);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_StartCheckDevLink();

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CMS_Client_EnableAutoModDeviceIP(
        [MarshalAs(UnmanagedType.I1)] bool enabled);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_AddGroup(string name);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_GetGroupInfoByName(string name, ref GroupInfo info);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_AddDeviceByID(
        string cloudId, string user, string password, string adminToken, int vendor,
        string name, int groupId, [MarshalAs(UnmanagedType.I1)] bool shared);

    // Assinatura x64 confirmada no wrapper CGlobalLogic::addDevice do VMS Pro.
    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_AddDeviceByIP(
        string ip, int port, string user, string password, int vendor,
        string name, int groupId, int channelCount, int deviceType, int protocol);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_GetDeviceByIP(string ip, ref DeviceInfo info);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_GetDeviceByCloudID(string cloudId, int loginType, ref DeviceInfo info);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_EditDevice(int deviceId, ref DeviceInfo info);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_GetDeviceConfig(
        int deviceId, int channel, int commandId,
        IntPtr buffer, int bufferSize, int timeout);

    // Assinatura confirmada no wrapper CGlobalLogic::saveDeviceConfig do VMS Pro x64.
    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_SetDeviceConfig(
        int deviceId, int channel, int commandId,
        IntPtr buffer, int bufferSize);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_RemoveDevice(int deviceId);

    internal static unsafe void SetAdminToken(ref DeviceInfo info, string token)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(token);
        fixed (byte* destination = info.AdminToken)
        {
            int length = Math.Min(bytes.Length, 0xFF);
            for (int index = 0; index < length; index++)
                destination[index] = bytes[index];
            destination[length] = 0;
        }
    }

    internal static unsafe bool HasOemId(ref DeviceInfo info)
    {
        fixed (byte* value = info.OemId)
        {
            for (int index = 0; index < 0x10; index++)
                if (value[index] != 0)
                    return true;
        }
        return false;
    }

    internal static unsafe string GetOemId(ref DeviceInfo info)
    {
        fixed (byte* value = info.OemId)
        {
            int length = 0;
            while (length < 0x10 && value[length] != 0)
                length++;
            if (length == 0)
                return string.Empty;
            ReadOnlySpan<byte> bytes = new(value, length);
            bool printable = true;
            foreach (byte item in bytes)
                printable &= item is >= 0x20 and <= 0x7E;
            return printable
                ? System.Text.Encoding.ASCII.GetString(bytes)
                : Convert.ToHexString(bytes);
        }
    }

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

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_OpenSound(int windowNumber);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_CloseSound(int windowNumber);

    // Assinaturas confirmadas nos wrappers CGlobalLogic do VMS Pro x64.
    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_OpenTalk(
        int audioInputDevice, [MarshalAs(UnmanagedType.I1)] bool open);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CMS_Client_StartTalk(int deviceId, int channel);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CMS_Client_StopTalk(int deviceId, int channel);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_SendPTZCommand(
        int windowNumber, int command, int speed, int stop);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CMS_Client_isSounding(int windowNumber);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CMS_Client_isRecording(int windowNumber);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_GetRecordPlan(int windowNumber, ref RecordPlanUnit plan);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_SetRecordPlan(ref RecordPlanUnit plan);
}
