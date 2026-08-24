using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace XMEyeCloudTester;

public partial class MainWindow : Window
{
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetDllDirectory(string path);
    }

    private readonly Forms.Panel videoPanel = new() { BackColor = System.Drawing.Color.Black };
    private readonly CmsSdk.MessageCallback sdkCallback;
    private readonly QtRuntime qtRuntime = new();
    private readonly List<CloudApi.AccountDevice> accountDevices = [];
    private readonly object diagnosticLock = new();
    private readonly object deviceLoginLock = new();
    private readonly string diagnosticPath;
    private int deviceId;
    private bool playing;
    private bool sdkReady;
    private bool cloudReady;
    private bool captchaBusy;
    private string captchaToken = string.Empty;
    private CancellationTokenSource? qrLoginCts;
    private bool qrBusy;
    private int cloudGroupId;
    private string cloudAccessToken = string.Empty;
    private int pendingDeviceId;
    private TaskCompletionSource<int>? pendingDeviceLogin;

    public MainWindow()
    {
        string diagnosticDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XMEyeCloudAccountTester");
        Directory.CreateDirectory(diagnosticDirectory);
        diagnosticPath = Path.Combine(diagnosticDirectory, "diagnostic.log");
        File.WriteAllText(diagnosticPath, string.Empty);
        sdkCallback = OnSdkMessage;
        InitializeComponent();
        Version version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
        VersionText.Text = $"Versao {version.Major}.{version.Minor}.{version.Build}";
        VideoHost.Child = videoPanel;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeSdk();
        if (sdkReady)
            await RefreshQrAsync();
    }

    private void InitializeSdk()
    {
        try
        {
            string baseDirectory = AppContext.BaseDirectory;
            NativeMethods.SetDllDirectory(baseDirectory);
            string plugins = Path.Combine(baseDirectory, "plugins");
            Environment.SetEnvironmentVariable("QT_PLUGIN_PATH", plugins + ";" + baseDirectory);
            Environment.SetEnvironmentVariable("QT_QPA_PLATFORM_PLUGIN_PATH", Path.Combine(plugins, "platforms"));

            string dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XMEyeCloudAccountTester");
            Directory.CreateDirectory(dataDirectory);
            EnsureCmsDataLayout(dataDirectory);
            string sourceConfig = Path.Combine(baseDirectory, "config.ini");
            string localConfig = Path.Combine(dataDirectory, "config.ini");
            if (File.Exists(sourceConfig) && !File.Exists(localConfig))
                File.Copy(sourceConfig, localConfig);
            string sourceCloudServer = Path.Combine(baseDirectory, "CloudServer");
            string localCloudServer = Path.Combine(dataDirectory, "CloudServer");
            if (File.Exists(sourceCloudServer))
                File.Copy(sourceCloudServer, localCloudServer, overwrite: true);

            // O VMS Pro usa caminho vazio no CMS_Client_Init e resolve
            // data/users e data/cloudusers relativamente ao diretorio atual.
            Directory.SetCurrentDirectory(dataDirectory);

            Log("Driver SQLite: " +
                (File.Exists(Path.Combine(plugins, "sqldrivers", "qsqlite.dll")) ? "carregado do pacote" : "ausente") + ".");
            qtRuntime.Initialize();
            XMEyeBridge.EnableQtDiagnostics(Path.Combine(dataDirectory, "qt-sqlite.log"));
            int cmsResult = CmsSdk.CMS_Client_Init(string.Empty, sdkCallback, IntPtr.Zero, 0);
            sdkReady = cmsResult == 0;
            if (!sdkReady)
                throw new InvalidOperationException($"CMS_Client_Init retornou {cmsResult}.");
            int autoStatus = CmsSdk.CMS_Client_SetAutoCheckDevStatus(true);
            QrCloudApi.ConfigureBrazilRegion();

            Log("Motor de video inicializado; banco local do CMS sera concluido apos o login QR.");

            Log("Regiao Cloud: Brasil (SA).");
            Log($"Verificacao automatica de estado: {autoStatus}.");
            Log("Motor pronto. Carregando o login oficial por QR da conta XMEye/iCSee...");
        }
        catch (DllNotFoundException ex)
        {
            Log("BLOQUEADO: componente oficial ausente: " + ex.Message);
            DisableAccountLogin();
        }
        catch (BadImageFormatException)
        {
            Log("BLOQUEADO: arquitetura incorreta. Este pacote requer Windows 64 bits.");
            DisableAccountLogin();
        }
        catch (Exception ex)
        {
            Log("Falha ao iniciar: " + ex.Message);
            DisableAccountLogin();
        }
    }

    private async Task<bool> WaitForLocalDeviceStoreAsync(
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        Stopwatch timer = Stopwatch.StartNew();
        do
        {
            qtRuntime.ProcessEvents();
            if (CmsSdk.CMS_Client_IsFinishReadLocalDevInfo())
                return true;
            await Task.Delay(20, cancellationToken);
        }
        while (timer.Elapsed < timeout);
        return false;
    }

    private async void RefreshQr_Click(object sender, RoutedEventArgs e) =>
        await RefreshQrAsync();

    private async Task RefreshQrAsync()
    {
        if (!sdkReady || qrBusy)
            return;

        qrLoginCts?.Cancel();
        qrLoginCts?.Dispose();
        qrLoginCts = new CancellationTokenSource();
        CancellationToken cancellationToken = qrLoginCts.Token;
        qrBusy = true;
        RefreshQrButton.IsEnabled = false;
        QrImage.Source = null;
        QrStatusText.Text = "Gerando QR oficial...";
        QrStatusText.Visibility = Visibility.Visible;

        try
        {
            QrCloudApi.Challenge challenge = await Task.Run(QrCloudApi.CreateChallenge, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            QrImage.Source = LoadImage(QrCloudApi.RenderQr(challenge.QrUrl));
            QrStatusText.Visibility = Visibility.Collapsed;
            Log("QR oficial carregado. Escaneie pelo aplicativo XMEye/iCSee no celular.");
            _ = PollQrLoginAsync(challenge, cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            QrStatusText.Text = "Não foi possível gerar o QR. Clique em GERAR NOVO QR.";
            Log("FALHA AO GERAR O QR OFICIAL: " + SafeQrError(ex));
        }
        finally
        {
            qrBusy = false;
            RefreshQrButton.IsEnabled = sdkReady;
        }
    }

    private async Task PollQrLoginAsync(QrCloudApi.Challenge challenge, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken);
                QrCloudApi.PollResult status = await Task.Run(
                    () => QrCloudApi.Poll(challenge.Token), cancellationToken);

                if (status.Expired || status.Limited)
                {
                    QrImage.Source = null;
                    QrStatusText.Text = status.Limited
                        ? "Muitas consultas. Gere um novo QR em instantes."
                        : "Este QR expirou. Clique em GERAR NOVO QR.";
                    QrStatusText.Visibility = Visibility.Visible;
                    Log(status.Limited ? "QR temporariamente limitado." : "QR expirado.");
                    return;
                }

                if (!status.Completed)
                    continue;

                QrStatusText.Text = "Conta vinculada. Carregando câmeras...";
                QrStatusText.Visibility = Visibility.Visible;
                QrImage.Source = null;
                // CCloudLoginDlg::onBindCloudAccount, no VMS Pro oficial,
                // chama InitAppInfo antes de entregar a conta ao motor Cloud.
                // Inicializar a sessao antes desta identidade deixa o CMS com
                // um contexto de autorizacao diferente do usado pelo VMS.
                await Task.Run(
                    () => QrCloudApi.InitializeAppInfo(challenge.Secret, status.AppInfoEnc),
                    cancellationToken);
                QrCloudApi.AppIdentityDiagnostics identity = QrCloudApi.LastAppIdentityDiagnostics;
                Log($"Identidade QR preparada: movecard retornado {identity.ReturnedMoveCard}; aplicado {identity.AppliedMoveCard}; formato {identity.MoveCardKind}.");

                QrCloudApi.CmsCloudIdentity cmsIdentity = await Task.Run(
                    () => QrCloudApi.GetCmsCloudIdentity(status.AccessToken),
                    cancellationToken);
                bool importedVmsCredentials = EnsureCmsCloudUserStore(cmsIdentity.UserName);
                int linkedSession = await Task.Run(
                    () => CmsSdk.CMS_Client_UserLogin(
                        cmsIdentity.UserName, cmsIdentity.Password, 1, IntPtr.Zero),
                    cancellationToken);
                if (linkedSession <= 0)
                    throw new InvalidOperationException($"O CMS recusou a sessao local do QR ({linkedSession}).");
                await Task.Run(() => XMEyeBridge.SetCloudToken(cmsIdentity.CloudToken), cancellationToken);
                int mqttResult = await Task.Run(
                    () => CmsSdk.CMS_Client_InitMqtt(cmsIdentity.CloudToken),
                    cancellationToken);
                // Sequencia observada na copia instrumentada do VMS Pro atual.
                await Task.Run(
                    () => QrCloudApi.InitializeAppInfo(challenge.Secret, status.AppInfoEnc),
                    cancellationToken);
                bool localStoreReady = await WaitForLocalDeviceStoreAsync(
                    TimeSpan.FromSeconds(8), cancellationToken);
                // No devices.db do VMS oficial, Cloud Group e virtual: a
                // tabela Groups fica vazia e todos os dispositivos usam 65535.
                cloudGroupId = ushort.MaxValue;
                int bindCloudResult = await Task.Run(
                    () => CmsSdk.CMS_Client_SetBindCloudState(true),
                    cancellationToken);
                IReadOnlyList<CloudApi.AccountDevice> devices =
                    await Task.Run(
                        () => QrCloudApi.GetDevices(
                            status.AccessToken, status.LocalUser, status.LocalPassword),
                        cancellationToken);
                (int synchronized, int failed) = SynchronizeAccountDevicesToCms(devices);
                int queriedStates = await Task.Run(
                    () => QueryAllAccountDeviceStates(devices), cancellationToken);
                await Task.Delay(1200, cancellationToken);
                Log($"Sessao local vinculada ao QR: {linkedSession}.");
                Log("Identidade interna da conta aplicada ao CMS.");
                Log(importedVmsCredentials
                    ? "Credenciais locais importadas do VMS Pro."
                    : "Banco do VMS Pro nao localizado; usando somente os dados da nuvem.");
                Log($"Canal oficial MQTT da conta inicializado: {mqttResult}.");
                Log($"Banco local do CMS apos o QR: {(localStoreReady ? "pronto" : "tempo esgotado")}.");
                Log($"Estado de vinculo Cloud ativado: {bindCloudResult}.");
                ClearAccountDevices();
                cloudAccessToken = status.AccessToken;
                accountDevices.AddRange(devices);
                DeviceBox.ItemsSource = accountDevices;
                DeviceBox.IsEnabled = accountDevices.Count > 0;
                ForgetAccountButton.IsEnabled = true;
                if (accountDevices.Count > 0)
                {
                    DeviceBox.SelectedIndex = 0;
                    QrStatusText.Text = "Conta conectada.";
                    Log($"Conta autenticada pelo QR. Câmeras encontradas: {accountDevices.Count}.");
                    int withUser = accountDevices.Count(device => device.DeviceUser.Length > 0);
                    int withPassword = accountDevices.Count(device => device.DevicePassword.Length > 0);
                    int shared = accountDevices.Count(device => device.IsShared);
                    Log($"Dados de acesso recuperados pela nuvem: usuário em {withUser}/{accountDevices.Count}; senha em {withPassword}/{accountDevices.Count}.");
                    Log($"Vínculo das câmeras: próprias {accountDevices.Count - shared}; compartilhadas {shared}.");
                    QrCloudApi.CredentialParseDiagnostics parse = QrCloudApi.LastCredentialDiagnostics;
                    Log($"Estrutura protegida: powers {parse.PowersPresent}/{accountDevices.Count}; devInfo {parse.MarkerFound}; formato cifrado {parse.EncodedFormat}; campos decodificados {parse.FiveFields}; deviceToken {parse.DeviceTokenObjects}; AdminToken {parse.AdminTokenPresent}; PWDToken {parse.PwdTokenPresent}; tokens decifrados {parse.PwdTokenDecrypted}.");
                    Log($"Credencial individual da lista: senha direta {parse.DirectPasswordPresent}/{accountDevices.Count}; tamanho oficial 16 em {parse.PasswordLength16}/{accountDevices.Count}.");
                    Log($"Credencial técnica do QR aplicada: usuário {parse.SessionUserFallback}; senha {parse.SessionPasswordFallback}.");
                    Log($"Lista local do CMS sincronizada: {synchronized}/{accountDevices.Count}; falhas {failed}.");
                    Log($"Estados Cloud consultados em conjunto: {queriedStates}/{accountDevices.Count}.");
                }
                else
                {
                    QrStatusText.Text = "Conta conectada, mas nenhuma câmera foi encontrada.";
                    Log("Conta autenticada pelo QR, mas nenhuma câmera vinculada foi retornada.");
                }

                try
                {
                    Log($"Grupo local Cloud preparado: {cloudGroupId}.");
                }
                catch (Exception ex)
                {
                    cloudGroupId = -1;
                    Log("AVISO: a lista foi carregada, mas o grupo local Cloud ainda nao foi preparado: " + ex.Message);
                    foreach (string diagnostic in XMEyeBridge.ReadQtDiagnostics())
                        Log(diagnostic);
                }
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (CloudServiceException ex)
        {
            QrStatusText.Text = "A conta conectou, mas a lista de câmeras foi recusada.";
            Log($"FALHA AO CARREGAR CÂMERAS APÓS O QR — código {ex.Code}.");
        }
        catch (Exception ex)
        {
            QrStatusText.Text = "Falha ao consultar o QR. Gere um novo código.";
            Log("FALHA NO LOGIN POR QR: " + SafeQrError(ex));
        }
    }

    private static string SafeQrError(Exception error) => error switch
    {
        DllNotFoundException => "a ponte XMEyeBridge.dll não foi encontrada",
        BadImageFormatException => "a ponte QR possui arquitetura incompatível",
        _ => error.Message
    };

    private async void AccountLogin_Click(object sender, RoutedEventArgs e)
    {
        string account = AccountBox.Text.Trim();
        string password = AccountPasswordBox.Password;
        string verificationCode = CaptchaBox.Text.Trim();
        if (account.Length == 0 || password.Length == 0)
        {
            System.Windows.MessageBox.Show("Informe o e-mail/usuário e a senha da sua conta XMEye ou iCSee.");
            return;
        }
        if (verificationCode.Length != 4)
        {
            System.Windows.MessageBox.Show("Digite os quatro caracteres exibidos na imagem de verificação.");
            CaptchaBox.Focus();
            return;
        }
        if (!sdkReady || !cloudReady)
        {
            Log("O login cloud ainda não está pronto. Troque a imagem de verificação e tente novamente.");
            return;
        }

        SetAccountBusy(true);
        cloudReady = false;
        bool authenticated = false;
        Log("Autenticando pelo serviço HTTPS atual e carregando as câmeras vinculadas...");
        try
        {
            IReadOnlyList<CloudApi.AccountDevice> devices =
                await CloudApi.LoginAndGetDevicesAsync(
                    account, password, verificationCode, captchaToken);
            ClearAccountDevices();
            accountDevices.AddRange(devices);
            DeviceBox.ItemsSource = accountDevices;
            DeviceBox.IsEnabled = accountDevices.Count > 0;
            ForgetAccountButton.IsEnabled = true;
            AccountPasswordBox.Clear();
            CaptchaBox.Clear();
            authenticated = true;
            if (accountDevices.Count > 0)
            {
                DeviceBox.SelectedIndex = 0;
                Log($"Conta autenticada. Câmeras encontradas: {accountDevices.Count}.");
            }
            else
            {
                Log("Conta autenticada, mas nenhuma câmera vinculada foi retornada.");
            }
        }
        catch (CloudServiceException ex)
        {
            if (ex.Code == 4000)
                AccountPasswordBox.Clear();
            Log($"FALHA NO SERVIÇO CLOUD ATUAL — código {ex.Code}: {DescribeCloudError(ex)}.");
        }
        catch (HttpRequestException)
        {
            Log("FALHA DE REDE: não foi possível acessar api.xmeye.net por HTTPS.");
        }
        catch (TaskCanceledException)
        {
            Log("FALHA DE REDE: o serviço HTTPS da XMEye não respondeu dentro do prazo.");
        }
        catch (Exception ex)
        {
            Log("ERRO AO CARREGAR A CONTA: " + ex.Message);
        }
        finally
        {
            SetAccountBusy(false);
            if (!authenticated)
                await RefreshCaptchaAsync();
        }
    }

    private async void RefreshCaptcha_Click(object sender, RoutedEventArgs e) =>
        await RefreshCaptchaAsync();

    private async Task RefreshCaptchaAsync()
    {
        if (captchaBusy || !sdkReady)
            return;
        captchaBusy = true;
        cloudReady = false;
        captchaToken = string.Empty;
        AccountLoginButton.IsEnabled = false;
        RefreshCaptchaButton.IsEnabled = false;
        CaptchaBox.Clear();
        try
        {
            CloudApi.CaptchaChallenge challenge = await CloudApi.GetCaptchaAsync();
            captchaToken = challenge.Token;
            CaptchaImage.Source = LoadImage(challenge.ImageBytes);
            cloudReady = true;
            Log("Serviço de conta atual conectado; imagem de verificação carregada.");
        }
        catch (HttpRequestException)
        {
            Log("FALHA DE REDE: não foi possível carregar a verificação em api.xmeye.net.");
        }
        catch (TaskCanceledException)
        {
            Log("FALHA DE REDE: o serviço de verificação não respondeu dentro do prazo.");
        }
        catch (CloudServiceException ex)
        {
            Log($"FALHA AO CARREGAR A VERIFICAÇÃO — código {ex.Code}.");
        }
        catch (Exception ex)
        {
            Log("FALHA AO CARREGAR A VERIFICAÇÃO: " + ex.Message);
        }
        finally
        {
            captchaBusy = false;
            RefreshCaptchaButton.IsEnabled = sdkReady;
            AccountLoginButton.IsEnabled = sdkReady && cloudReady;
            if (cloudReady) CaptchaBox.Focus();
        }
    }

    private static BitmapImage LoadImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string DescribeCloudError(CloudServiceException error) => error.Code switch
    {
        4000 => "e-mail/usuário ou senha recusados pelo serviço HTTPS",
        4010 => "código da imagem incorreto",
        4048 => "muitas tentativas incorretas do código da imagem; aguarde antes de repetir",
        4049 => "muitas tentativas de login; a conta foi bloqueada temporariamente por 10 minutos",
        -1 => "código da imagem inválido/expirado ou parâmetros recusados",
        _ when error.Stage == CloudStage.DeviceList => "o login funcionou, mas a lista de câmeras foi recusada",
        _ => "erro retornado pelo serviço HTTPS da XMEye"
    };

    private async void OpenCamera_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not CloudApi.AccountDevice selected)
            return;
        SetCameraBusy(true);
        Log("Conectando à câmera selecionada pelo Cloud ID retornado pela conta...");
        try
        {
            DisconnectVideo(log: false);
            var result = await ConnectSelectedDeviceAsync(selected);

            if (!result.Ok)
            {
                string detail = result.Error switch
                {
                    -1 => "não foi possível cadastrar a câmera no motor local",
                    -2 => "a câmera não apareceu no motor local",
                    -3 => "tempo esgotado aguardando a conexão P2P",
                    -7 => "a credencial técnica do dispositivo foi recusada",
                    -29 => "a autorização do dispositivo retornada pela nuvem foi recusada",
                    _ => "erro retornado pelo dispositivo ou pela nuvem"
                };
                Log($"FALHA AO ABRIR A CÂMERA — código {result.Error}: {detail}.");
                return;
            }

            deviceId = result.Info.ID;
            int channel = int.Parse(((ComboBoxItem)ChannelBox.SelectedItem).Content.ToString()!);
            int hwnd = unchecked((int)videoPanel.Handle.ToInt64());
            int windowResult = CmsSdk.CMS_Client_CreatePlayWindow(0, hwnd, 0);
            int previewResult = CmsSdk.CMS_Client_StartPreview(
                deviceId, 0, channel,
                SubstreamBox.IsChecked == true ? CmsSdk.StreamType.Extra : CmsSdk.StreamType.Main,
                true);
            Log($"Janela de vídeo: {windowResult}; abertura do fluxo: {previewResult}.");
            if (previewResult == 0)
            {
                Log("A câmera conectou, mas a abertura do vídeo falhou.");
                return;
            }
            playing = true;
            VideoPlaceholder.Visibility = Visibility.Collapsed;
            DisconnectButton.IsEnabled = true;
            Log("VÍDEO REMOTO ABERTO.");
        }
        catch (Exception ex) { Log("ERRO AO ABRIR A CÂMERA: " + ex.Message); }
        finally { SetCameraBusy(false); }
    }

    private async Task<(bool Ok, int Error, CmsSdk.DeviceInfo Info)> ConnectSelectedDeviceAsync(
        CloudApi.AccountDevice selected)
    {
        var info = new CmsSdk.DeviceInfo();
        int found = CmsSdk.CMS_Client_GetDeviceByCloudID(selected.CloudId, 2, ref info);

        string name = string.IsNullOrWhiteSpace(selected.Alias) ? "Câmera XMEye" : selected.Alias;
        if (found == 0 || info.ID <= 0)
        {
            int added = CmsSdk.CMS_Client_AddDeviceByID(
                FormatCmsRegistrationId(selected.CloudId), selected.DeviceUser, selected.DevicePassword,
                selected.AdminToken, 0, name, cloudGroupId, selected.IsShared);
            Log($"Cadastro ausente na sincronização; recriado: {added}.");
            if (added < 0)
                return (false, added, info);
        }

        found = CmsSdk.CMS_Client_GetDeviceByCloudID(selected.CloudId, 2, ref info);
        if (found == 0 || info.ID <= 0)
            return (false, -2, info);

        Log("Cadastro Cloud preservado como foi criado pela sincronização oficial da conta.");

        int statusQuery = XMEyeBridge.QueryDeviceStatus(selected.CloudId);
        await Task.Delay(3000);
        CmsSdk.CMS_Client_GetDeviceByCloudID(selected.CloudId, 2, ref info);
        Log($"Preflight oficial de transporte: retorno {statusQuery}.");
        Log("Rota de transporte entregue ao NetSDK; o resultado real sera confirmado pelo login.");

        var loginSignal = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (deviceLoginLock)
        {
            pendingDeviceId = info.ID;
            pendingDeviceLogin = loginSignal;
        }
        try
        {
            CmsSdk.CMS_Client_DeviceLoginOrLogout(info.ID, true);
            for (int attempt = 0; attempt < 50; attempt++)
            {
                Task completed = await Task.WhenAny(loginSignal.Task, Task.Delay(400));
                if (completed == loginSignal.Task)
                {
                    int error = await loginSignal.Task;
                    CmsSdk.CMS_Client_GetDeviceByCloudID(selected.CloudId, 2, ref info);
                    return (false, error, info);
                }
                CmsSdk.CMS_Client_GetDeviceByCloudID(selected.CloudId, 2, ref info);
                if (info.LoginHandle > 0)
                    return (true, 0, info);
                if (info.Error != 0)
                    return (false, info.Error, info);
            }
            return (false, -3, info);
        }
        finally
        {
            lock (deviceLoginLock)
            {
                if (ReferenceEquals(pendingDeviceLogin, loginSignal))
                {
                    pendingDeviceLogin = null;
                    pendingDeviceId = 0;
                }
            }
        }
    }

    private (int Synchronized, int Failed) SynchronizeAccountDevicesToCms(
        IReadOnlyList<CloudApi.AccountDevice> devices)
    {
        if (cloudGroupId < 0)
            return (0, devices.Count);

        int synchronized = 0;
        int failed = 0;
        foreach (CloudApi.AccountDevice device in devices)
        {
            var existing = new CmsSdk.DeviceInfo();
            int found = CmsSdk.CMS_Client_GetDeviceByCloudID(device.CloudId, 2, ref existing);
            if (found != 0 && existing.ID > 0)
            {
                // As versoes anteriores gravaram por engano o upass do QR
                // (144 caracteres). Quando a lista devolver a credencial
                // individual de 16 caracteres, substitui esse cadastro ruim.
                // Sem uma senha individual confirmada, preserva o registro.
                if (device.DevicePassword.Length != 16)
                {
                    synchronized++;
                    continue;
                }
                CmsSdk.CMS_Client_DeviceLoginOrLogout(existing.ID, false);
                CmsSdk.CMS_Client_RemoveDevice(existing.ID);
            }

            string name = string.IsNullOrWhiteSpace(device.Alias) ? "Câmera XMEye" : device.Alias;
            int added = CmsSdk.CMS_Client_AddDeviceByID(
                FormatCmsRegistrationId(device.CloudId),
                device.DeviceUser,
                device.DevicePassword,
                device.AdminToken,
                0,
                name,
                cloudGroupId,
                device.IsShared);
            if (added > 0)
                synchronized++;
            else
                failed++;
        }
        return (synchronized, failed);
    }

    private static string FormatCmsRegistrationId(string cloudId) =>
        cloudId.Contains('_', StringComparison.Ordinal) ? cloudId : cloudId + "_Cloud";

    private static int QueryAllAccountDeviceStates(
        IReadOnlyList<CloudApi.AccountDevice> devices)
    {
        int queried = 0;
        foreach (CloudApi.AccountDevice device in devices)
        {
            int result = XMEyeBridge.QueryDeviceStatus(device.CloudId);
            if (result >= 0)
                queried++;
        }
        return queried;
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OpenCameraButton.IsEnabled = DeviceBox.SelectedItem is CloudApi.AccountDevice;

    private static void EnsureCmsDataLayout(string dataDirectory)
    {
        string usersDirectory = Path.Combine(dataDirectory, "data", "users");
        Directory.CreateDirectory(usersDirectory);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "data", "cloudusers"));
        string usersDatabase = Path.Combine(usersDirectory, "users.db");
        if (File.Exists(usersDatabase))
            return;

        // Banco estrutural padrao distribuido com o VMS Pro oficial. Ele
        // contem somente o usuario local padrao e nenhuma conta/camera Cloud.
        const string compressedTemplate =
            "H4sIAAAAAAAEAO3by07CQBQG4DPUIJoo7mZnZuECQjUYvLAxscKgjVixtiasSJWqTQSUlqg79d1c+g4+hbrTod7RxMSd5v8y08mZdk7a2Z023d6qBpEv9jvdlheJAk0QY7QsBBElVB+id+yb+CcJmpm6GEs/kDZ+TWmevlEDAAAAAAAAwO/UtCTP5Vg98naP/PDkSFW0jdA/6fntvcFwqGRLw5HCMVaqUgyczLS9lq+rKHtZYMOcc3Yl45xu6HfD+JD4tD6eEhmzLEzLkavSFjXb3DDsuliXdWG4zqZpqQUb0nJ0S+UWO4ZdWjPszMJcVlibjrDcalW4lrnlSr3mheFpp9v8eJHunB/7b8lfV+h2cHAYfZnOZhNJvsQZBe2mf/bybF4v6sRxI77Zxmw8aGrXUv2tG1OdpW9JNQAAAAAAAAD4oyZZkvh0SvOaraBdKSzkS/lyeXZxbr5YrBj0+DhK/fr/nlQDAAAAAAAAgP8lpfGR+JXA8/f/O1INAAAAAAAAAP6VFFP1f/wjwBOhU8RIAEAAAA==";
        WriteCompressedTemplate(usersDatabase, compressedTemplate);
    }

    private static bool EnsureCmsCloudUserStore(string cloudUser)
    {
        if (string.IsNullOrWhiteSpace(cloudUser) ||
            cloudUser.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidOperationException("A identidade tecnica do QR nao pode formar o banco Cloud.");

        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XMEyeCloudAccountTester");
        string cloudDirectory = Path.Combine(dataDirectory, "data", "cloudusers", cloudUser);
        Directory.CreateDirectory(cloudDirectory);
        string devicesDatabase = Path.Combine(cloudDirectory, "devices.db");
        bool imported = TryImportVmsDeviceDatabase(cloudUser, devicesDatabase);
        if (!imported && !File.Exists(devicesDatabase))
        {
            // Esquema vazio extraido do devices.db oficial: Devices, Groups e
            // sqlite_sequence, sem cameras, nomes, seriais ou credenciais.
            const string compressedTemplate =
                "H4sIABiPi2oC/+3Xz0/CMBQH8A2InDDeCOHSIwtLTEA4W8eCi2PAHCSczHQVF8cK29B4NCae/Y+18nsYEq+a7yfZmr627627veu+6SeM3PNo4iakLp1IsiydEyIt5aStzN5cln6pIJ5s4UM67hc+xQAAAAAAAADCJJsvlkryayNxbwPWjvh8Gi/fOc3WqaMTh16YOlnGSMVoEcNy9LZuk55tdKg9Ilf6iNCB0zUscaKjW45quRNGhtTWLqldaZ4pxOo6xBqYptLLHBWrVXm0qBbPAtEL3sRsNmfh3f40m6q/t1gJRQlVzJQ3Vc4Xy2X5/XSRs8WefJFqNWRSOVbBX15iceOdnes7HL6dSj0vYnG8WazXFLXHo+RnDi3gc08k30mjdqiWOjmIWXS4Vs+N42ceeZvVWqOpqDR0Az7WHtxwXVNt+WM/cYNUjAZuNEnvWvwb52XKNiGTj/1wN7ItPmShx6N0OiOczpNUTuLwRxamPlD57s1F0w8AAAAAAAAA/xj6fwAAAAAAAAD0/wAAAAAAAADw930BfhKd9QBAAAA=";
            WriteCompressedTemplate(devicesDatabase, compressedTemplate);
        }

        string sourceConfig = Path.Combine(dataDirectory, "config.ini");
        string cloudConfig = Path.Combine(cloudDirectory, "config.ini");
        if (File.Exists(sourceConfig) && !File.Exists(cloudConfig))
            File.Copy(sourceConfig, cloudConfig);
        return imported;
    }

    private static bool TryImportVmsDeviceDatabase(string cloudUser, string destination)
    {
        var candidates = new List<string>();
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length > 0)
        {
            candidates.Add(Path.Combine(
                documents, "ChatGPT", "APPs", "xmeye-diagnostico", "vms-instrumentado",
                "data", "cloudusers", cloudUser, "devices.db"));
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            candidates.Add(Path.Combine(
                directory.FullName, "vms-instrumentado", "data", "cloudusers",
                cloudUser, "devices.db"));
            candidates.Add(Path.Combine(
                directory.FullName, "xmeye-diagnostico", "vms-instrumentado", "data",
                "cloudusers", cloudUser, "devices.db"));
            candidates.Add(Path.Combine(
                directory.FullName, "VMS Pro", "data", "cloudusers",
                cloudUser, "devices.db"));
        }

        string fullDestination = Path.GetFullPath(destination);
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string fullCandidate = Path.GetFullPath(candidate);
                if (string.Equals(fullCandidate, fullDestination, StringComparison.OrdinalIgnoreCase) ||
                    !IsSqliteDatabase(fullCandidate))
                    continue;
                File.Copy(fullCandidate, fullDestination, overwrite: true);
                return true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private static bool IsSqliteDatabase(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 4096)
            return false;
        Span<byte> header = stackalloc byte[16];
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return stream.Read(header) == header.Length &&
            header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static void WriteCompressedTemplate(string path, string template)
    {
        byte[] compressed = Convert.FromBase64String(template);
        using var source = new MemoryStream(compressed);
        using var gzip = new System.IO.Compression.GZipStream(
            source, System.IO.Compression.CompressionMode.Decompress);
        using var destination = File.Create(path);
        gzip.CopyTo(destination);
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => DisconnectVideo(log: true);

    private async void ForgetAccount_Click(object sender, RoutedEventArgs e)
    {
        DisconnectVideo(log: false);
        ClearAccountDevices();
        cloudAccessToken = string.Empty;
        AccountBox.Clear();
        AccountPasswordBox.Clear();
        ForgetAccountButton.IsEnabled = false;
        Log("Dados da conta removidos da memória.");
        await RefreshQrAsync();
    }

    private void ClearAccountDevices()
    {
        DeviceBox.ItemsSource = null;
        DeviceBox.IsEnabled = false;
        OpenCameraButton.IsEnabled = false;
        accountDevices.Clear();
    }

    private void DisconnectVideo(bool log)
    {
        if (playing)
        {
            CmsSdk.CMS_Client_StopPreviewByWnd(0, 0);
            playing = false;
        }
        if (deviceId != 0)
        {
            CmsSdk.CMS_Client_DeviceLoginOrLogout(deviceId, false);
            deviceId = 0;
        }
        VideoPlaceholder.Visibility = Visibility.Visible;
        DisconnectButton.IsEnabled = false;
        if (log) Log("Vídeo desconectado.");
    }

    private void SetAccountBusy(bool busy)
    {
        AccountLoginButton.IsEnabled = !busy && sdkReady && cloudReady;
        RefreshCaptchaButton.IsEnabled = !busy && sdkReady;
        AccountLoginButton.Content = busy ? "CARREGANDO..." : "ENTRAR E CARREGAR CÂMERAS";
    }

    private void SetCameraBusy(bool busy)
    {
        OpenCameraButton.IsEnabled = !busy && DeviceBox.SelectedItem is CloudApi.AccountDevice;
        OpenCameraButton.Content = busy ? "CONECTANDO..." : "ABRIR CÂMERA SELECIONADA";
    }

    private void DisableAccountLogin()
    {
        AccountLoginButton.IsEnabled = false;
        RefreshCaptchaButton.IsEnabled = false;
    }

    private void Log(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
        try
        {
            lock (diagnosticLock) File.AppendAllText(diagnosticPath, line);
        }
        catch { }
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string baseDirectory = AppContext.BaseDirectory;
            string launcher = Path.Combine(baseDirectory, "XMEyeCloudTester.exe");
            if (!File.Exists(launcher))
                throw new FileNotFoundException("O atualizador nao foi encontrado.", launcher);

            var startInfo = new ProcessStartInfo(launcher)
            {
                UseShellExecute = false,
                WorkingDirectory = baseDirectory
            };
            startInfo.ArgumentList.Add("--manual-update");
            startInfo.ArgumentList.Add($"--wait-pid={Environment.ProcessId}");
            Process.Start(startInfo);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Nao foi possivel abrir o atualizador.\n\n" + ex.Message,
                "Atualizacao", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => System.Windows.Clipboard.SetText(LogBox.Text);

    private void OnSdkMessage(
        CmsSdk.MessageType type, int p1, int p2, int p3, int p4,
        IntPtr text1, IntPtr text2, uint size, IntPtr user)
    {
        if (type == CmsSdk.MessageType.DeviceControl && p1 == 3 && p4 != 0)
        {
            lock (deviceLoginLock)
                if (p2 == pendingDeviceId)
                    pendingDeviceLogin?.TrySetResult(p4);
        }
        // Native text is deliberately excluded because some SDK messages may
        // contain device metadata. Only non-sensitive numeric diagnostics are logged.
        Dispatcher.BeginInvoke((Action)(() => Log($"SDK {type}: {p1}, {p2}, {p3}, {p4}.")));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            qrLoginCts?.Cancel();
            qrLoginCts?.Dispose();
            DisconnectVideo(log: false);
            ClearAccountDevices();
            AccountPasswordBox.Clear();
            CaptchaBox.Clear();
            captchaToken = string.Empty;
            if (sdkReady)
            {
                CmsSdk.CMS_Client_SetBindCloudState(false);
                CmsSdk.CMS_Client_UnInit();
            }
            qtRuntime.Dispose();
        }
        catch { }
    }
}
