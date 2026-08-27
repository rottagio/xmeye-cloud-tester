using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace XMEyeCloudTester;

internal static class XMEyeBridge
{
    // A lista oficial pode conter muitos dispositivos e compartilhamentos.
    private const int ResponseCapacity = 1048576;
    private static readonly object Sync = new();
    private static readonly StringBuilder SharedResponse = new(ResponseCapacity);
    private static string qtDiagnosticPath = string.Empty;

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int XMEye_EnableQtDiagnostics(string path);

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XMEye_EnableTransientDeviceStore();

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XMEye_ConfigureInMemoryDeviceStore();

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XMEye_HttpGet(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        int requestType,
        StringBuilder response,
        int responseCapacity);

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XMEye_HttpPost(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body,
        int requestType,
        StringBuilder response,
        int responseCapacity);

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XMEye_HttpPostAuthorized(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string body,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string authorization,
        int requestType,
        StringBuilder response,
        int responseCapacity);

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XMEye_InitAppInfo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string appId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string uuid,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string secret,
        int moveCard);

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XMEye_SetHttpApiUrl(
        int apiType,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string serviceHost,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string amsHost);

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XMEye_SetCloudToken(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string token);

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XMEye_QueryDeviceStatus(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string cloudId);

    [DllImport("XMEyeBridge.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XMEye_ConfigureRecording(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    internal static void EnableQtDiagnostics(string path)
    {
        int result = XMEye_EnableQtDiagnostics(path);
        if (result != 0)
            throw new InvalidOperationException($"O diagnostico Qt nao iniciou ({result}).");
        qtDiagnosticPath = path;
    }

    internal static IReadOnlyList<string> ReadQtDiagnostics()
    {
        if (qtDiagnosticPath.Length == 0 || !File.Exists(qtDiagnosticPath))
            return [];
        return File.ReadAllLines(qtDiagnosticPath, Encoding.Unicode).Distinct().ToArray();
    }

    internal static void EnableTransientDeviceStore()
    {
        int result = XMEye_EnableTransientDeviceStore();
        if (result != 0)
            throw new InvalidOperationException($"O armazenamento transitorio do CMS falhou ({result}).");
    }

    internal static void ConfigureInMemoryDeviceStore()
    {
        int result = XMEye_ConfigureInMemoryDeviceStore();
        if (result != 0)
            throw new InvalidOperationException($"O banco temporario do CMS falhou ({result}).");
    }

    internal static int InitAppInfo(string appId, string uuid, string secret, int moveCard)
    {
        lock (Sync)
            return XMEye_InitAppInfo(appId, uuid, secret, moveCard);
    }

    internal static void SetHttpApiUrl(int apiType, string serviceHost, string amsHost)
    {
        lock (Sync)
        {
            int result = XMEye_SetHttpApiUrl(apiType, serviceHost, amsHost);
            if (result != 0)
                throw new InvalidOperationException($"A configuração regional do CMS falhou ({result}).");
        }
    }

    internal static void SetCloudToken(string token)
    {
        lock (Sync)
        {
            int result = XMEye_SetCloudToken(token);
            if (result != 0)
                throw new InvalidOperationException($"A sessao Cloud nao foi aceita pelo CMS ({result}).");
        }
    }

    internal static int QueryDeviceStatus(string cloudId)
    {
        lock (Sync)
            return XMEye_QueryDeviceStatus(cloudId);
    }

    internal static int ConfigureRecording(string destination)
    {
        lock (Sync)
            return XMEye_ConfigureRecording(destination);
    }

    internal static string Get(string url, int requestType)
    {
        lock (Sync)
        {
            SharedResponse.Clear();
            int result = XMEye_HttpGet(
                url, requestType, SharedResponse, SharedResponse.Capacity);
            if (result != 1 || SharedResponse.Length == 0)
                throw new InvalidOperationException($"A consulta oficial do QR falhou ({result}).");
            return SharedResponse.ToString();
        }
    }

    internal static string Post(string url, string body, int requestType)
    {
        lock (Sync)
        {
            SharedResponse.Clear();
            int result = XMEye_HttpPost(
                url, body, requestType, SharedResponse, SharedResponse.Capacity);
            if (result != 1 || SharedResponse.Length == 0)
                throw new InvalidOperationException($"A consulta oficial do QR falhou ({result}).");
            return SharedResponse.ToString();
        }
    }

    internal static string PostAuthorized(
        string url, string authorization, int requestType, string body = "")
    {
        lock (Sync)
        {
            SharedResponse.Clear();
            int result = XMEye_HttpPostAuthorized(
                url, body, authorization, requestType,
                SharedResponse, SharedResponse.Capacity);
            if (result != 1 || SharedResponse.Length == 0)
                throw new InvalidOperationException($"A consulta autenticada do QR falhou ({result}).");
            return SharedResponse.ToString();
        }
    }
}
