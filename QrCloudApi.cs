using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QRCoder;

namespace XMEyeCloudTester;

internal static class QrCloudApi
{
    // Endpoints gravados pelo VMS Pro oficial quando a regiao Brasil e selecionada.
    private const string AmsHost = "api-sa.jftechws.com/ams";
    private const string CapsHost = "caps-sa.jftechws.com";
    private const string BossHost = "boss22-api-as.xmcsrv.net";
    private const string QrCipherKey = "SFpASnVGZW5nMjAy";

    internal sealed record Challenge(string Token, string Secret, string QrUrl);
    internal sealed record PollResult(
        bool Completed, bool Expired, bool Limited, string AccessToken, string AppInfoEnc,
        string LocalUser, string LocalPassword);
    internal sealed record DeviceTokenResult(int Code, string AdminToken);
    internal sealed record AppIdentityDiagnostics(int MoveCard, string MoveCardKind);
    internal sealed record CredentialParseDiagnostics(
        int PowersPresent, int MarkerFound, int EncodedFormat, int FiveFields, int PasswordRecovered,
        int DeviceTokenObjects, int AdminTokenPresent, int PwdTokenPresent, int PwdTokenDecrypted,
        int SessionUserFallback, int SessionPasswordFallback);
    internal static CredentialParseDiagnostics LastCredentialDiagnostics { get; private set; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    internal static AppIdentityDiagnostics LastAppIdentityDiagnostics { get; private set; } =
        new(0, "ausente");

    private sealed class MutableCredentialDiagnostics
    {
        internal int PowersPresent;
        internal int MarkerFound;
        internal int EncodedFormat;
        internal int FiveFields;
        internal int PasswordRecovered;
        internal int DeviceTokenObjects;
        internal int AdminTokenPresent;
        internal int PwdTokenPresent;
        internal int PwdTokenDecrypted;
        internal int SessionUserFallback;
        internal int SessionPasswordFallback;
    }

    internal static Challenge CreateChallenge()
    {
        string response = XMEyeBridge.Get($"{AmsHost}/qr/generate/v1", 0);
        using JsonDocument document = JsonDocument.Parse(response);
        EnsureSuccess(document.RootElement);
        JsonElement data = document.RootElement.GetProperty("data");
        string token = GetString(data, "token");
        string secret = GetString(data, "secret");
        if (token.Length == 0 || secret.Length == 0)
            throw new InvalidOperationException("O serviço oficial não retornou o token do QR.");

        string payload = JsonSerializer.Serialize(new { loginOs = "win", isoCode = "BRA", token });
        string encrypted = XMEyeBridge.Post(payload, QrCipherKey, 10);
        return new Challenge(token, secret, "https://d.xmeye.net/CSee?qrLogin=" + encrypted);
    }

    internal static void ConfigureBrazilRegion()
    {
        XMEyeBridge.SetHttpApiUrl(0, CapsHost, AmsHost);
        XMEyeBridge.SetHttpApiUrl(1, BossHost, AmsHost);
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
        string appInfoEnc = GetString(data, "appInfoEnc");
        string localUser = GetString(data, "uname");
        string localPassword = GetString(data, "upass");
        return new PollResult(
            accessToken.Length > 0 && appInfoEnc.Length > 0 && localUser.Length > 0,
            expired,
            limited,
            accessToken,
            appInfoEnc,
            localUser,
            localPassword);
    }

    internal static void InitializeAppInfo(string qrSecret, string appInfoEnc)
    {
        string dynamicSecret = DecryptHexAesEcb(qrSecret, "qr_code_login_ae");
        string appInfoKey = Convert.ToHexString(
            MD5.HashData(Encoding.UTF8.GetBytes(dynamicSecret + "827"))).ToLowerInvariant();
        string clearJson = DecryptHexAesEcb(appInfoEnc, appInfoKey);

        using JsonDocument document = JsonDocument.Parse(clearJson);
        JsonElement root = document.RootElement;
        string appId = GetString(root, "id");
        string uuid = GetString(root, "uuid");
        string secret = GetString(root, "secret");
        int moveCard = ReadMoveCard(root, out string moveCardKind);
        LastAppIdentityDiagnostics = new(moveCard, moveCardKind);
        if (appId.Length == 0 || uuid.Length == 0 || secret.Length == 0)
            throw new InvalidOperationException("A identidade retornada pelo QR está incompleta.");

        int result = XMEyeBridge.InitAppInfo(appId, uuid, secret, moveCard);
        if (result != 0)
            throw new InvalidOperationException($"A identidade oficial do aplicativo não foi aceita ({result}).");

        // Reaplica a mesma regiao apos InitAppInfo, como faz o VMS oficial.
        ConfigureBrazilRegion();
    }

    private static int ReadMoveCard(JsonElement root, out string kind)
    {
        JsonElement card = default;
        bool found = root.TryGetProperty("movecard", out card) ||
            root.TryGetProperty("moveCard", out card) ||
            root.TryGetProperty("movedCard", out card);
        if (!found)
        {
            kind = "ausente";
            return 0;
        }

        kind = card.ValueKind.ToString().ToLowerInvariant();
        if (card.ValueKind == JsonValueKind.Number && card.TryGetInt32(out int numeric))
            return numeric;
        if (card.ValueKind == JsonValueKind.String &&
            int.TryParse(card.GetString(), out int textNumeric))
            return textNumeric;
        if (card.ValueKind == JsonValueKind.True)
            return 1;
        return 0;
    }

    internal static IReadOnlyList<CloudApi.AccountDevice> GetDevices(
        string accessToken, string localUser, string localPassword)
    {
        // O VMS atual valida primeiro a sessao do QR e usa o endpoint QR da
        // regiao. O endpoint antigo api.xmeye.net/transfer/mdlist rejeita este
        // tipo de token com -4004.
        string userResponse = XMEyeBridge.PostAuthorized(
            $"{AmsHost}/userinfo2/v1",
            accessToken,
            2);
        using (JsonDocument userDocument = JsonDocument.Parse(userResponse))
            EnsureSuccess(userDocument.RootElement, "userinfo2");

        string listResponse = XMEyeBridge.PostAuthorized(
            $"{AmsHost}/qr/msdlistCode/v1",
            accessToken,
            2,
            $"uname={localUser}&upass={localPassword}");
        using JsonDocument listDocument = JsonDocument.Parse(listResponse);
        EnsureSuccess(listDocument.RootElement, "lista QR");

        var devices = new List<CloudApi.AccountDevice>();
        var cloudIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new MutableCredentialDiagnostics();
        if (listDocument.RootElement.TryGetProperty("data", out JsonElement data))
        {
            bool partitioned = CollectDevicePartitions(
                data, devices, cloudIds, diagnostics, localUser, localPassword);
            if (!partitioned)
                CollectDevices(
                    data, devices, cloudIds, diagnostics, false, localUser, localPassword);
        }
        LastCredentialDiagnostics = new(
            diagnostics.PowersPresent,
            diagnostics.MarkerFound,
            diagnostics.EncodedFormat,
            diagnostics.FiveFields,
            diagnostics.PasswordRecovered,
            diagnostics.DeviceTokenObjects,
            diagnostics.AdminTokenPresent,
            diagnostics.PwdTokenPresent,
            diagnostics.PwdTokenDecrypted,
            diagnostics.SessionUserFallback,
            diagnostics.SessionPasswordFallback);
        return devices;
    }

    internal static DeviceTokenResult QueryDeviceToken(string accessToken, string cloudId)
    {
        string response = XMEyeBridge.PostAuthorized(
            $"{AmsHost}/queryDeviceToken/v1", accessToken, 2, "uuids=" + cloudId);
        using JsonDocument document = JsonDocument.Parse(response);
        JsonElement root = document.RootElement;
        int code = root.TryGetProperty("code", out JsonElement codeValue) &&
            codeValue.TryGetInt32(out int parsedCode) ? parsedCode : int.MinValue;
        string adminToken = code == 2000 && root.TryGetProperty("data", out JsonElement data)
            ? FindAdminToken(data) : string.Empty;
        return new DeviceTokenResult(code, adminToken);
    }

    private static string FindAdminToken(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Name.Equals("AdminToken", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString() ?? string.Empty;
                string nested = FindAdminToken(property.Value);
                if (nested.Length > 0)
                    return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                string nested = FindAdminToken(item);
                if (nested.Length > 0)
                    return nested;
            }
        }
        return string.Empty;
    }

    private static bool CollectDevicePartitions(
        JsonElement element,
        List<CloudApi.AccountDevice> devices,
        HashSet<string> cloudIds,
        MutableCredentialDiagnostics diagnostics,
        string sessionUser,
        string sessionPassword)
    {
        bool found = false;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                found |= CollectDevicePartitions(
                    item, devices, cloudIds, diagnostics, sessionUser, sessionPassword);
            return found;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                continue;
            if (property.Name.Equals("mine", StringComparison.OrdinalIgnoreCase))
            {
                CollectDevices(
                    property.Value, devices, cloudIds, diagnostics, false,
                    sessionUser, sessionPassword);
                found = true;
            }
            else if (property.Name.Equals("share", StringComparison.OrdinalIgnoreCase))
            {
                CollectDevices(
                    property.Value, devices, cloudIds, diagnostics, true,
                    sessionUser, sessionPassword);
                found = true;
            }
            else
            {
                found |= CollectDevicePartitions(
                    property.Value, devices, cloudIds, diagnostics, sessionUser, sessionPassword);
            }
        }
        return found;
    }

    internal static byte[] RenderQr(string content)
    {
        using QRCodeData data = QRCodeGenerator.GenerateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qr = new PngByteQRCode(data);
        return qr.GetGraphic(7, new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 }, true);
    }

    private static void EnsureSuccess(JsonElement root, string? stage = null)
    {
        int code = root.TryGetProperty("code", out JsonElement value) && value.TryGetInt32(out int parsed)
            ? parsed : int.MinValue;
        if (code != 2000)
            throw new InvalidOperationException(
                stage is null
                    ? $"O serviço oficial do QR retornou {code}."
                    : $"O serviço oficial do QR retornou {code} em {stage}.");
    }

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;

    private static void CollectDevices(
        JsonElement element,
        List<CloudApi.AccountDevice> devices,
        HashSet<string> cloudIds,
        MutableCredentialDiagnostics diagnostics,
        bool isShared,
        string sessionUser,
        string sessionPassword)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                CollectDevices(
                    item, devices, cloudIds, diagnostics, isShared,
                    sessionUser, sessionPassword);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        string cloudId = FirstString(element, "uuid", "sn", "deviceId", "devId").Trim();
        if (cloudId.Length > 0 && cloudIds.Add(cloudId))
        {
            string deviceUser = FirstString(element, "username", "userName", "user");
            string devicePassword = FirstString(element, "password", "passwd", "upass");
            string adminToken = string.Empty;
            if (deviceUser.Length == 0 || devicePassword.Length == 0)
            {
                (string decodedUser, string decodedPassword) = DecodePowersCredentials(
                    FirstString(element, "powers"), diagnostics);
                if (deviceUser.Length == 0)
                    deviceUser = decodedUser;
                if (devicePassword.Length == 0)
                    devicePassword = decodedPassword;
            }

            // O VMS atual prefere deviceToken.PWDToken. Ele decifra esse token
            // e sobrescreve usuário/senha antes de cadastrar o dispositivo.
            if (TryGetPropertyIgnoreCase(element, "deviceToken", out JsonElement deviceToken) &&
                deviceToken.ValueKind == JsonValueKind.Object)
            {
                diagnostics.DeviceTokenObjects++;
                adminToken = FirstString(deviceToken, "AdminToken", "adminToken");
                if (adminToken.Length > 0)
                    diagnostics.AdminTokenPresent++;
                string pwdToken = FirstString(deviceToken, "PWDToken", "pwdToken");
                if (pwdToken.Length > 0)
                {
                    diagnostics.PwdTokenPresent++;
                    (string tokenUser, string tokenPassword) = DecryptPwdToken(pwdToken);
                    if (tokenUser.Length > 0 && tokenPassword.Length > 0)
                    {
                        diagnostics.PwdTokenDecrypted++;
                        deviceUser = tokenUser;
                        devicePassword = "MD5_" + tokenPassword;
                    }
                }
            }

            // O VMS Pro grava a credencial tecnica devolvida por code2u em
            // todos os dispositivos quando a lista cloud omite user/password.
            if (deviceUser.Length == 0 && sessionUser.Length > 0)
            {
                deviceUser = sessionUser;
                diagnostics.SessionUserFallback++;
            }
            if (devicePassword.Length == 0 && sessionPassword.Length > 0)
            {
                // No fluxo de conta, onBindCloudAccount preserva o upass de
                // code2u. O prefixo MD5_ pertence apenas ao PWDToken decifrado
                // e ao QR de dispositivo individual, nao a este fallback.
                devicePassword = sessionPassword;
                diagnostics.SessionPasswordFallback++;
            }

            devices.Add(new CloudApi.AccountDevice
            {
                CloudId = cloudId,
                Alias = FirstString(element, "nickname", "devName", "deviceName", "name").Trim(),
                DeviceUser = deviceUser,
                DevicePassword = devicePassword,
                AdminToken = adminToken,
                IsShared = isShared
            });
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                bool childShared = property.Name.Equals("share", StringComparison.OrdinalIgnoreCase)
                    ? true
                    : property.Name.Equals("mine", StringComparison.OrdinalIgnoreCase)
                        ? false
                        : isShared;
                CollectDevices(
                    property.Value, devices, cloudIds, diagnostics, childShared,
                    sessionUser, sessionPassword);
            }
        }
    }

    private static string FirstString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            string value = GetString(element, name);
            if (value.Length > 0)
                return value;
        }
        return string.Empty;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
            return true;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    // Reproduz CGlobalCloudUserInfo::extractDevInfo/DecDevInfo do VMS oficial.
    private static (string User, string Password) DecodePowersCredentials(
        string powers, MutableCredentialDiagnostics diagnostics)
    {
        if (powers.Length == 0)
            return (string.Empty, string.Empty);
        diagnostics.PowersPresent++;

        const string marker = "\"devInfo\":\"";
        int start = powers.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0 && powers.Contains("\\\"devInfo\\\":\\\"", StringComparison.Ordinal))
        {
            powers = powers.Replace("\\\"", "\"", StringComparison.Ordinal);
            start = powers.IndexOf(marker, StringComparison.Ordinal);
        }
        if (start < 0)
            return (string.Empty, string.Empty);
        diagnostics.MarkerFound++;
        start += marker.Length;
        int end = powers.IndexOf('"', start);
        if (end <= start)
            return (string.Empty, string.Empty);

        string encoded = powers[start..end];
        if (encoded.Length >= 3 && encoded[^1] == 'z' &&
            encoded.All(char.IsAsciiLetterOrDigit))
            diagnostics.EncodedFormat++;
        string decoded = DecodeDeviceInfo(encoded);
        string[] fields = decoded.Split(',');
        if (fields.Length != 5)
            return (string.Empty, string.Empty);
        diagnostics.FiveFields++;
        if (fields[2].Length > 0)
            diagnostics.PasswordRecovered++;
        return (fields[1], fields[2]);
    }

    private static string DecodeDeviceInfo(string encoded)
    {
        if (encoded.Length < 3 || encoded[^1] != 'z' ||
            encoded.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return encoded;

        char[] shifted = encoded[..^1].ToCharArray();
        const string key = "DecInfoEncode";
        for (int i = 0; i < shifted.Length; i++)
        {
            int baseChar;
            int radix;
            if (shifted[i] is >= '0' and <= '9')
            {
                baseChar = '0';
                radix = 10;
            }
            else if (shifted[i] is >= 'a' and <= 'z')
            {
                baseChar = 'a';
                radix = 26;
            }
            else
            {
                baseChar = 'A';
                radix = 26;
            }

            int offset = key[i % key.Length] % radix;
            shifted[i] = (char)(((shifted[i] - baseChar - offset + radix) % radix) + baseChar);
        }

        var decimalDigits = new StringBuilder(shifted.Length * 2);
        for (int i = 0; i < shifted.Length;)
        {
            int consumed = DecodeBase62(shifted, i, out int decoded);
            if (i + consumed >= shifted.Length)
            {
                decimalDigits.Append(decimalDigits.Length % 3 == 1
                    ? decoded.ToString("D2")
                    : decoded.ToString());
                break;
            }
            decimalDigits.Append(decoded.ToString("D2"));
            i += consumed;
        }

        if (decimalDigits.Length < 3 ||
            !int.TryParse(decimalDigits.ToString(decimalDigits.Length - 3, 3), out int code))
            return string.Empty;

        decimalDigits.Length -= 3;
        int digitShift = (code / 100 + code / 10 % 10 + code % 10) % 10;
        for (int i = 0; i < decimalDigits.Length; i++)
            decimalDigits[i] = (char)('0' + (decimalDigits[i] - '0' - digitShift + 10) % 10);

        if (decimalDigits.Length % 3 != 0)
            return string.Empty;

        var clear = new StringBuilder(decimalDigits.Length / 3);
        for (int i = 0; i < decimalDigits.Length; i += 3)
        {
            if (!int.TryParse(decimalDigits.ToString(i, 3), out int number))
                return string.Empty;
            clear.Append((char)(number - code));
        }
        return clear.ToString();
    }

    private static int DecodeBase62(char[] value, int index, out int decoded)
    {
        char current = value[index];
        if (current == '0' && index + 1 < value.Length)
        {
            DecodeBase62(value, index + 1, out int following);
            decoded = following + 61;
            return 2;
        }
        if (current is >= 'a' and <= 'z')
            decoded = current - 'a';
        else if (current is >= 'A' and <= 'Z')
            decoded = current - 39;
        else if (current is >= '1' and <= '9')
            decoded = current + 3;
        else
            decoded = 0;
        return 1;
    }

    private static (string User, string Password) DecryptPwdToken(string token)
    {
        // Chave usada por COpensslCrypt::DecryptPWDToken no VMS Pro atual.
        const string encodedKey = "SkFWQeecn+WlveWWneWVig==";
        byte[] encrypted;
        byte[] key;
        try
        {
            encrypted = Convert.FromBase64String(token);
            key = Convert.FromBase64String(encodedKey);
        }
        catch (FormatException)
        {
            return (string.Empty, string.Empty);
        }

        try
        {
            using Aes aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
            using ICryptoTransform decryptor = aes.CreateDecryptor();
            byte[] clear = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
            try
            {
                string value = Encoding.UTF8.GetString(clear);
                int separator = value.LastIndexOf(':');
                return separator > 0 && separator < value.Length - 1
                    ? (value[..separator], value[(separator + 1)..])
                    : (string.Empty, string.Empty);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (CryptographicException)
        {
            return (string.Empty, string.Empty);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string DecryptHexAesEcb(string ciphertext, string key)
    {
        byte[] encrypted = Convert.FromHexString(ciphertext);
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        try
        {
            using Aes aes = Aes.Create();
            aes.Key = keyBytes;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
            using ICryptoTransform decryptor = aes.CreateDecryptor();
            byte[] clear = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
}
