using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XMEyeCloudTester;

internal static class CloudSessionStore
{
    internal sealed record Session(
        string AccessToken,
        string QrSecret,
        string AppInfoEnc,
        string LocalUser,
        string LocalPassword);

    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("XMEyeCloudTester.CloudSession.v1");

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int Size;
        internal IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    internal static bool Exists => File.Exists(SessionPath);

    internal static void Save(Session session)
    {
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            byte[] encrypted = Protect(clear);
            try
            {
                string temporary = SessionPath + ".new";
                File.WriteAllBytes(temporary, encrypted);
                File.Move(temporary, SessionPath, overwrite: true);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    internal static bool TryLoad(out Session? session)
    {
        session = null;
        if (!File.Exists(SessionPath))
            return false;

        byte[] encrypted = File.ReadAllBytes(SessionPath);
        byte[] clear = [];
        try
        {
            clear = Unprotect(encrypted);
            session = JsonSerializer.Deserialize<Session>(clear);
            return session is not null &&
                session.AccessToken.Length > 0 &&
                session.QrSecret.Length > 0 &&
                session.AppInfoEnc.Length > 0 &&
                session.LocalUser.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            if (clear.Length > 0)
                CryptographicOperations.ZeroMemory(clear);
        }
    }

    internal static void Delete()
    {
        if (File.Exists(SessionPath))
            File.Delete(SessionPath);
        string temporary = SessionPath + ".new";
        if (File.Exists(temporary))
            File.Delete(temporary);
    }

    private static string SessionPath
    {
        get
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XMEyeCloudAccountTester");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "cloud-session.bin");
        }
    }

    private static byte[] Protect(byte[] clear) => Transform(clear, protect: true);
    private static byte[] Unprotect(byte[] encrypted) => Transform(encrypted, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        GCHandle inputHandle = default;
        GCHandle entropyHandle = default;
        DataBlob output = default;
        try
        {
            inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
            entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
            var inputBlob = new DataBlob { Size = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(
                    ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(
                    ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);
            if (!ok)
                throw new InvalidOperationException(
                    "O Windows nao conseguiu proteger os dados da sessao.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));

            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
                LocalFree(output.Data);
            if (entropyHandle.IsAllocated)
                entropyHandle.Free();
            if (inputHandle.IsAllocated)
                inputHandle.Free();
        }
    }
}
