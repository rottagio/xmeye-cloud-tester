using System.Text.Json;
using QRCoder;

namespace XMEyeCloudTester;

internal static class QrCloudApi
{
    private const string AmsHost = "sa-ams.jftechws.com";
    private const string QrCipherKey = "SFpASnVGZW5nMjAy";

    internal sealed record Challenge(string Token, string QrUrl);
    internal sealed record PollResult(bool Completed, bool Expired, bool Limited, string AccessToken);

    internal static Challenge CreateChallenge()
    {
        string response = XMEyeBridge.Get($"{AmsHost}/qr/generate/v1", 0);
        using JsonDocument document = JsonDocument.Parse(response);
        EnsureSuccess(document.RootElement);
        JsonElement data = document.RootElement.GetProperty("data");
        string token = GetString(data, "token");
        if (token.Length == 0)
            throw new InvalidOperationException("O serviço oficial não retornou o token do QR.");

        string payload = JsonSerializer.Serialize(new { loginOs = "win", isoCode = "BRA", token });
        string encrypted = XMEyeBridge.Post(payload, QrCipherKey, 10);
        return new Challenge(token, "https://d.xmeye.net/CSee?qrLogin=" + encrypted);
    }

    internal static PollResult Poll(string token)
    {
        string response = XMEyeBridge.Post(
            $"{AmsHost}/qr/code2u/v1",
            "token=" + Uri.EscapeDataString(token),
            1);
        using JsonDocument document = JsonDocument.Parse(response);
        EnsureSuccess(document.RootElement);
        JsonElement data = document.RootElement.GetProperty("data");
        bool expired = GetBool(data, "expired");
        bool limited = GetBool(data, "limit");
        string accessToken = GetString(data, "accessToken");
        return new PollResult(accessToken.Length > 0, expired, limited, accessToken);
    }

    internal static byte[] RenderQr(string content)
    {
        using QRCodeData data = QRCodeGenerator.GenerateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(7, new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 }, true);
    }

    private static void EnsureSuccess(JsonElement root)
    {
        int code = root.TryGetProperty("code", out JsonElement value) && value.TryGetInt32(out int parsed)
            ? parsed : int.MinValue;
        if (code != 2000)
            throw new InvalidOperationException($"O serviço oficial do QR retornou {code}.");
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
}
