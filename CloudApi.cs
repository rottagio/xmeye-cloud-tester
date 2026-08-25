using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XMEyeCloudTester;

internal static class CloudApi
{
    private static readonly Uri ApiBase = new("https://api.xmeye.net/api/");
    private static readonly byte[] TransportKey = Convert.FromHexString("d86d7bab3d6ac01ad9dc6a897652f2d2");
    private static readonly HttpClient Client = new()
    {
        BaseAddress = ApiBase,
        Timeout = TimeSpan.FromSeconds(20)
    };

    internal sealed class AccountDevice
    {
        internal required string CloudId { get; init; }
        public required string Alias { get; set; }
        internal required string DeviceUser { get; set; }
        internal required string DevicePassword { get; set; }
        internal string AdminToken { get; init; } = string.Empty;
        internal bool IsShared { get; init; }
        public string LocalGroup { get; set; } = "Casa";
        public bool ShowInLiveView { get; set; } = true;
        public bool Paused { get; set; }
        public string Model { get; init; } = string.Empty;
        public string Firmware { get; init; } = string.Empty;
        public string ProductId { get; init; } = string.Empty;
        public bool IsNetworkDevice { get; init; }
        public int NetworkPort { get; init; } = 34567;
        internal int CmsDeviceId { get; set; }
        public string RuntimeStatus { get; set; } = "Aguardando";

        public string OrganizationSummary =>
            $"{LocalGroup}  •  {RuntimeStatus}  •  {(ShowInLiveView ? "Visível no monitor" : "Oculta no monitor")}";

        public string MaskedCloudId => IsNetworkDevice ? $"Rede local: {CloudId}:{NetworkPort}" : CloudId.Length <= 8
            ? "••••"
            : $"{CloudId[..4]}••••{CloudId[^4..]}";

        public override string ToString() => string.IsNullOrWhiteSpace(Alias) ? CloudId : Alias;
    }

    internal sealed record CaptchaChallenge(string Token, byte[] ImageBytes);

    internal static async Task<CaptchaChallenge> GetCaptchaAsync()
    {
        using var request = NewJsonRequest(HttpMethod.Post, "transfer/image/v1/", "{}");
        using HttpResponseMessage response = await Client.SendAsync(request).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        int code = GetInt(root, "code");
        if (code != 200)
            throw new CloudServiceException(CloudStage.Captcha, code);

        JsonElement data = root.GetProperty("data");
        string token = data.GetProperty("codeToken").GetString() ?? string.Empty;
        string image = data.GetProperty("image").GetString() ?? string.Empty;
        if (token.Length == 0 || image.Length == 0)
            throw new InvalidOperationException("O serviço cloud retornou um CAPTCHA incompleto.");

        return new CaptchaChallenge(token, Convert.FromBase64String(image));
    }

    internal static async Task<IReadOnlyList<AccountDevice>> LoginAndGetDevicesAsync(
        string accountUser, string accountPassword, string verificationCode, string codeToken)
    {
        string loginJson = JsonSerializer.Serialize(new
        {
            uname = EncryptTransport(accountUser),
            upass = EncryptTransport(accountPassword),
            verCode = verificationCode
        });

        using var loginRequest = NewJsonRequest(HttpMethod.Post, "transfer/login/v1", loginJson);
        loginRequest.Headers.TryAddWithoutValidation("Code-Token", codeToken);
        using HttpResponseMessage loginResponse = await Client.SendAsync(loginRequest).ConfigureAwait(false);
        string loginResponseJson = await loginResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        loginResponse.EnsureSuccessStatusCode();

        string accessToken;
        using (JsonDocument document = JsonDocument.Parse(loginResponseJson))
        {
            JsonElement root = document.RootElement;
            int code = GetInt(root, "code");
            if (code != 2000)
                throw new CloudServiceException(CloudStage.Login, code);
            accessToken = root.TryGetProperty("accessToken", out JsonElement tokenElement)
                ? tokenElement.GetString() ?? string.Empty
                : string.Empty;
        }

        if (accessToken.Length == 0)
            throw new InvalidOperationException("O login cloud não retornou um token de sessão.");

        return await GetDevicesByAccessTokenAsync(accessToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<AccountDevice>> GetDevicesByAccessTokenAsync(string accessToken)
    {
        string listJson = JsonSerializer.Serialize(new { Authorization = accessToken });
        using var listRequest = NewJsonRequest(HttpMethod.Post, "transfer/mdlist/v1", listJson);
        listRequest.Headers.TryAddWithoutValidation("Access-Token", accessToken);
        using HttpResponseMessage listResponse = await Client.SendAsync(listRequest).ConfigureAwait(false);
        string listResponseJson = await listResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        listResponse.EnsureSuccessStatusCode();

        using JsonDocument listDocument = JsonDocument.Parse(listResponseJson);
        JsonElement listRoot = listDocument.RootElement;
        int listCode = GetInt(listRoot, "code");
        if (listCode != 2000)
            throw new CloudServiceException(CloudStage.DeviceList, listCode);

        if (!listRoot.TryGetProperty("data", out JsonElement outerData) ||
            !outerData.TryGetProperty("data", out JsonElement devicesElement) ||
            devicesElement.ValueKind != JsonValueKind.Array)
            return [];

        var devices = new List<AccountDevice>();
        foreach (JsonElement device in devicesElement.EnumerateArray())
        {
            string cloudId = GetString(device, "uuid").Trim();
            if (cloudId.Length == 0)
                continue;
            devices.Add(new AccountDevice
            {
                CloudId = cloudId,
                Alias = GetString(device, "nickname").Trim(),
                DeviceUser = GetString(device, "username"),
                DevicePassword = GetString(device, "password"),
                AdminToken = string.Empty,
                IsShared = false
            });
        }
        return devices;
    }

    private static HttpRequestMessage NewJsonRequest(HttpMethod method, string path, string json)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static string EncryptTransport(string value)
    {
        using Aes aes = Aes.Create();
        aes.Key = TransportKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        byte[] clear = Encoding.UTF8.GetBytes(value);
        try
        {
            using ICryptoTransform encryptor = aes.CreateEncryptor();
            return Convert.ToBase64String(encryptor.TransformFinalBlock(clear, 0, clear.Length));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static int GetInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : int.MinValue;

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

internal enum CloudStage { Captcha, Login, DeviceList }

internal sealed class CloudServiceException(CloudStage stage, int code) : Exception
{
    internal CloudStage Stage { get; } = stage;
    internal int Code { get; } = code;
}
