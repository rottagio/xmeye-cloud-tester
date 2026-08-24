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

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_AddGroup(string name);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_GetGroupInfoByName(string name, ref GroupInfo info);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_AddDeviceByID(
        string cloudId, string user, string password, int vendor,
        string name, int groupId, int loginType);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int CMS_Client_GetDeviceByCloudID(string cloudId, int loginType, ref DeviceInfo info);

    [DllImport("CMSClient.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int CMS_Client_EditDevice(int deviceId, ref DeviceInfo info);

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
