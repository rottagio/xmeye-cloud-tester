using System.Runtime.InteropServices;
using System.Text;

namespace XMEyeCloudTester;

internal static class XMEyeBridge
{
    private const int ResponseCapacity = 262144;
    private static readonly object Sync = new();

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

    internal static string Get(string url, int requestType)
    {
        lock (Sync)
        {
            var response = new StringBuilder(ResponseCapacity);
            int result = XMEye_HttpGet(url, requestType, response, response.Capacity);
            if (result != 1 || response.Length == 0)
                throw new InvalidOperationException($"A consulta oficial do QR falhou ({result}).");
            return response.ToString();
        }
    }

    internal static string Post(string url, string body, int requestType)
    {
        lock (Sync)
        {
            var response = new StringBuilder(ResponseCapacity);
            int result = XMEye_HttpPost(url, body, requestType, response, response.Capacity);
            if (result != 1 || response.Length == 0)
                throw new InvalidOperationException($"A consulta oficial do QR falhou ({result}).");
            return response.ToString();
        }
    }
}
