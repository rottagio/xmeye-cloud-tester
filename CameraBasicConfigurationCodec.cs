using System.Buffers.Binary;
using System.Text;

namespace XMEyeCloudTester;

/// <summary>
/// Layouts confirmed from the ConfigModule PDB shipped with VMS Pro. Every
/// mutation clones the full native block and changes only the documented field.
/// </summary>
internal static class CameraBasicConfigurationCodec
{
    internal const int GeneralType = 0x103ED;
    internal const int GeneralSize = 0x5C;
    internal const int LocationType = 0x103EE;
    internal const int LocationSize = 0x70;
    internal const int CameraParameterType = 0x5E;
    internal const int CameraParameterSize = 0x54;
    internal const int VolumeType = 0x1F8;
    internal const int VolumeSize = 0x1404;
    internal const int TimeZoneType = 0xA5;
    internal const int TimeZoneSize = 0x08;
    internal const int TimeReadType = 0x103F3;
    internal const int TimeWriteType = 0x2D;
    internal const int TimeSize = 0x20;

    private const int MachineNameOffset = 0x0C;
    private const int MachineNameLength = 0x40;
    private const int LanguageOffset = 0x08;
    private const int LanguageLength = 0x20;
    private const int PictureFlipOffset = 0x28;
    private const int PictureMirrorOffset = 0x2C;
    private const int DncThresholdOffset = 0x3C;
    private const int VolumeOutOffset = 0xA04;
    private const int AudioModeLength = 0x20;
    private const int LeftVolumeOffset = VolumeOutOffset + 0x20;
    private const int RightVolumeOffset = VolumeOutOffset + 0x24;

    internal sealed record Snapshot(
        byte[] General,
        byte[] Location,
        byte[] CameraParameters,
        byte[] Volume,
        byte[] TimeZone,
        byte[] CameraTime,
        string MachineName,
        string Language,
        bool PictureMirror,
        bool PictureFlip,
        int DayNightSensitivity,
        string AudioMode,
        int LeftVolume,
        int RightVolume,
        int MinutesWest,
        DateTime DeviceTime,
        int PtzSpeed);

    internal sealed record Changes(
        string? MachineName,
        bool? PictureMirror,
        bool? PictureFlip,
        int? DayNightSensitivity,
        int? SpeakerVolume,
        int? MinutesWest,
        int? PtzSpeed);

    internal static bool TryCreateSnapshot(
        byte[]? general,
        byte[]? location,
        byte[]? camera,
        byte[]? volume,
        byte[]? timeZone,
        byte[]? cameraTime,
        out Snapshot? snapshot,
        out string error)
    {
        snapshot = null;
        if (!HasSize(general, GeneralSize) || !HasSize(location, LocationSize) ||
            !HasSize(camera, CameraParameterSize) || !HasSize(volume, VolumeSize) ||
            !HasSize(timeZone, TimeZoneSize) || !HasSize(cameraTime, TimeSize))
        {
            error = "A câmera não devolveu todos os blocos básicos por completo.";
            return false;
        }

        string machineName = ReadString(general!, MachineNameOffset, MachineNameLength);
        string language = ReadString(location!, LanguageOffset, LanguageLength);
        int flip = ReadInt(camera!, PictureFlipOffset);
        int mirror = ReadInt(camera!, PictureMirrorOffset);
        int sensitivity = ReadInt(camera!, DncThresholdOffset);
        string audioMode = ReadString(volume!, VolumeOutOffset, AudioModeLength);
        int left = ReadInt(volume!, LeftVolumeOffset);
        int right = ReadInt(volume!, RightVolumeOffset);
        int minutesWest = ReadInt(timeZone!, 0);

        if (machineName.Length == 0 || language.Length == 0 || audioMode.Length == 0)
        {
            error = "A câmera devolveu texto obrigatório vazio em uma configuração básica.";
            return false;
        }
        if (flip is not (0 or 1) || mirror is not (0 or 1))
        {
            error = $"A câmera devolveu orientação inválida ({mirror}/{flip}).";
            return false;
        }
        if (sensitivity is < 0 or > 100)
        {
            error = $"A câmera devolveu sensibilidade dia/noite fora da faixa ({sensitivity}).";
            return false;
        }
        if (left is < 0 or > 100 || right is < 0 or > 100)
        {
            error = $"A câmera devolveu volume fora da faixa ({left}/{right}).";
            return false;
        }
        if (volume![1] == 0)
        {
            error = "A câmera informou que não possui saída de áudio configurável.";
            return false;
        }
        if (minutesWest is < -14 * 60 or > 14 * 60)
        {
            error = $"A câmera devolveu fuso horário inválido ({minutesWest} minutos).";
            return false;
        }
        if (!TryReadTime(cameraTime!, out DateTime interpretedTime))
        {
            error = "A câmera devolveu data ou hora inválida.";
            return false;
        }

        snapshot = new Snapshot(
            (byte[])general!.Clone(), (byte[])location!.Clone(), (byte[])camera!.Clone(),
            (byte[])volume!.Clone(), (byte[])timeZone!.Clone(), (byte[])cameraTime!.Clone(),
            machineName, language, mirror == 1, flip == 1, sensitivity, audioMode,
            left, right, minutesWest, interpretedTime, 5);
        error = string.Empty;
        return true;
    }

    internal static byte[] WithMachineName(byte[] original, string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("O nome não pode ficar vazio.", nameof(value));
        byte[] bytes = Encoding.UTF8.GetBytes(trimmed);
        if (bytes.Length >= MachineNameLength)
            throw new ArgumentException("O nome é grande demais para a câmera.", nameof(value));
        byte[] updated = CloneSized(original, GeneralSize);
        updated.AsSpan(MachineNameOffset, MachineNameLength).Clear();
        bytes.CopyTo(updated.AsSpan(MachineNameOffset));
        return updated;
    }

    internal static byte[] WithCameraParameters(
        byte[] original, bool mirror, bool flip, int sensitivity)
    {
        if (sensitivity is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(sensitivity));
        byte[] updated = CloneSized(original, CameraParameterSize);
        WriteInt(updated, PictureMirrorOffset, mirror ? 1 : 0);
        WriteInt(updated, PictureFlipOffset, flip ? 1 : 0);
        WriteInt(updated, DncThresholdOffset, sensitivity);
        return updated;
    }

    internal static byte[] WithVolume(byte[] original, int value)
    {
        if (value is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(value));
        byte[] updated = CloneSized(original, VolumeSize);
        WriteInt(updated, LeftVolumeOffset, value);
        WriteInt(updated, RightVolumeOffset, value);
        return updated;
    }

    internal static byte[] WithTimeZone(byte[] original, int minutesWest)
    {
        if (minutesWest is < -14 * 60 or > 14 * 60 || minutesWest % 30 != 0)
            throw new ArgumentOutOfRangeException(nameof(minutesWest));
        byte[] updated = CloneSized(original, TimeZoneSize);
        WriteInt(updated, 0, minutesWest);
        return updated;
    }

    internal static byte[] WithTime(byte[] original, DateTime localTime)
    {
        byte[] updated = CloneSized(original, TimeSize);
        WriteInt(updated, 0x00, localTime.Year);
        WriteInt(updated, 0x04, localTime.Month);
        WriteInt(updated, 0x08, localTime.Day);
        WriteInt(updated, 0x0C, (int)localTime.DayOfWeek);
        WriteInt(updated, 0x10, localTime.Hour);
        WriteInt(updated, 0x14, localTime.Minute);
        WriteInt(updated, 0x18, localTime.Second);
        return updated;
    }

    internal static string FormatTimeZone(int minutesWest)
    {
        int utcMinutes = -minutesWest;
        string sign = utcMinutes >= 0 ? "+" : "−";
        int absolute = Math.Abs(utcMinutes);
        return $"UTC{sign}{absolute / 60:00}:{absolute % 60:00}";
    }

    private static bool TryReadTime(byte[] value, out DateTime result)
    {
        try
        {
            result = new DateTime(
                ReadInt(value, 0x00), ReadInt(value, 0x04), ReadInt(value, 0x08),
                ReadInt(value, 0x10), ReadInt(value, 0x14), ReadInt(value, 0x18),
                DateTimeKind.Unspecified);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    private static bool HasSize(byte[]? value, int size) => value is { Length: >= 1 } && value.Length >= size;
    private static int ReadInt(byte[] value, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(offset, sizeof(int)));
    private static void WriteInt(byte[] value, int offset, int number) =>
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(offset, sizeof(int)), number);

    private static string ReadString(byte[] value, int offset, int length)
    {
        ReadOnlySpan<byte> bytes = value.AsSpan(offset, length);
        int terminator = bytes.IndexOf((byte)0);
        if (terminator >= 0)
            bytes = bytes[..terminator];
        return Encoding.UTF8.GetString(bytes).Trim();
    }

    private static byte[] CloneSized(byte[] original, int required)
    {
        if (original.Length < required)
            throw new ArgumentException("Bloco nativo incompleto.", nameof(original));
        return (byte[])original.Clone();
    }
}
