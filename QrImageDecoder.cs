using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace XMEyeCloudTester;

/// <summary>Leitor local de QR usando a libzbar distribuída pelo VMS oficial.</summary>
internal static class QrImageDecoder
{
    private const uint Y800 = (uint)'Y' | ((uint)'8' << 8) | ((uint)'0' << 16) | ((uint)'0' << 24);

    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr zbar_image_scanner_create();
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void zbar_image_scanner_destroy(IntPtr scanner);
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int zbar_image_scanner_set_config(IntPtr scanner, int symbol, int config, int value);
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr zbar_image_create();
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void zbar_image_destroy(IntPtr image);
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void zbar_image_set_format(IntPtr image, uint format);
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void zbar_image_set_size(IntPtr image, uint width, uint height);
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void zbar_image_set_data(IntPtr image, IntPtr data, UIntPtr length, IntPtr cleanup);
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int zbar_scan_image(IntPtr scanner, IntPtr image);
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr zbar_image_first_symbol(IntPtr image);
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr zbar_symbol_get_data(IntPtr symbol);
    [DllImport("libzbar64-0.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint zbar_symbol_get_data_length(IntPtr symbol);

    internal static string Decode(string path)
    {
        using var source = new Bitmap(path);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
            graphics.DrawImageUnscaled(source, 0, 0);

        byte[] gray = new byte[bitmap.Width * bitmap.Height];
        Rectangle rectangle = new(0, 0, bitmap.Width, bitmap.Height);
        BitmapData bits = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                byte* origin = (byte*)bits.Scan0;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    byte* row = origin + y * bits.Stride;
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        int offset = x * 3;
                        gray[y * bitmap.Width + x] = (byte)((row[offset] * 29 +
                            row[offset + 1] * 150 + row[offset + 2] * 77) >> 8);
                    }
                }
            }
        }
        finally { bitmap.UnlockBits(bits); }

        IntPtr scanner = zbar_image_scanner_create();
        IntPtr image = zbar_image_create();
        if (scanner == IntPtr.Zero || image == IntPtr.Zero)
            throw new InvalidOperationException("O leitor local de QR não pôde ser iniciado.");
        GCHandle pinned = default;
        try
        {
            zbar_image_scanner_set_config(scanner, 0, 0, 1);
            zbar_image_set_format(image, Y800);
            zbar_image_set_size(image, (uint)bitmap.Width, (uint)bitmap.Height);
            pinned = GCHandle.Alloc(gray, GCHandleType.Pinned);
            zbar_image_set_data(image, pinned.AddrOfPinnedObject(), (UIntPtr)gray.Length, IntPtr.Zero);
            if (zbar_scan_image(scanner, image) <= 0)
                return string.Empty;
            IntPtr symbol = zbar_image_first_symbol(image);
            if (symbol == IntPtr.Zero) return string.Empty;
            int length = checked((int)zbar_symbol_get_data_length(symbol));
            IntPtr data = zbar_symbol_get_data(symbol);
            if (data == IntPtr.Zero || length <= 0) return string.Empty;
            byte[] result = new byte[length];
            Marshal.Copy(data, result, 0, length);
            return System.Text.Encoding.UTF8.GetString(result).Trim();
        }
        finally
        {
            if (image != IntPtr.Zero)
            {
                zbar_image_set_data(image, IntPtr.Zero, UIntPtr.Zero, IntPtr.Zero);
                zbar_image_destroy(image);
            }
            if (scanner != IntPtr.Zero) zbar_image_scanner_destroy(scanner);
            if (pinned.IsAllocated) pinned.Free();
        }
    }
}
