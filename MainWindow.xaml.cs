using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using Forms = System.Windows.Forms;

namespace XMEyeCloudTester;

public partial class MainWindow : Window
{
    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetDllDirectory(string path);
    }

    private readonly Forms.TableLayoutPanel videoGrid = new()
    {
        BackColor = System.Drawing.Color.Black,
        Dock = Forms.DockStyle.Fill,
        CellBorderStyle = Forms.TableLayoutPanelCellBorderStyle.Single
    };
    private readonly List<Forms.Panel> videoPanels = [];
    private readonly List<Forms.Label> videoLabels = [];
    private readonly HashSet<int> activePreviewWindows = [];
    private readonly CmsSdk.MessageCallback sdkCallback;
    private readonly QtRuntime qtRuntime = new();
    private readonly List<CloudApi.AccountDevice> accountDevices = [];
    private readonly ConcurrentDictionary<int, int> automaticDeviceLoginResults = new();
    private readonly object diagnosticLock = new();
    private readonly string diagnosticPath;
    private readonly string diagnosticSession = Guid.NewGuid().ToString("N")[..8];
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
    private string cmsDevicesDatabasePath = string.Empty;
    private volatile int previewLoginError;
    private bool isClosing;

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
        VideoHost.Child = videoGrid;
        ConfigureVideoGrid(1);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Log($"Diagnostico iniciado: sessao {diagnosticSession}; versao {version.Major}.{version.Minor}.{version.Build}; " +
            $"Windows {Environment.OSVersion.Version}; processo {RuntimeInformation.ProcessArchitecture}.");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeSdk();
        if (!sdkReady)
            return;
        if (await TryRestoreSavedSessionAsync())
            return;
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
            AccountLoginButton.IsEnabled = true;
            QrCloudApi.ConfigureBrazilRegion();

            Log("Motor de video inicializado; banco local do CMS sera concluido apos o login QR.");

            Log("Regiao Cloud: Brasil (SA).");
            Log("Verificacao automatica de estado: padrao interno do CMS (igual ao VMS Pro).");
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

    private async Task<bool> TryRestoreSavedSessionAsync()
    {
        if (!CloudSessionStore.TryLoad(out CloudSessionStore.Session? saved) || saved is null)
            return false;

        QrImage.Source = null;
        QrStatusText.Text = "Restaurando a sessao protegida da conta...";
        QrStatusText.Visibility = Visibility.Visible;
        Log("Sessao protegida encontrada; validando na nuvem.");
        try
        {
            await Task.Run(() => QrCloudApi.InitializeAppInfo(saved.QrSecret, saved.AppInfoEnc));
            QrCloudApi.CmsCloudIdentity cmsIdentity = await Task.Run(
                () => QrCloudApi.GetCmsCloudIdentity(saved.AccessToken));
            Log($"Identidade CMS restaurada: usuario tamanho {cmsIdentity.UserName.Length}; senha/token tamanho {cmsIdentity.Password.Length}.");

            bool importedVmsCredentials = EnsureCmsCloudUserStore(cmsIdentity.UserName);
            int linkedSession = await Task.Run(
                () => CmsSdk.CMS_Client_UserLogin(
                    cmsIdentity.UserName, cmsIdentity.Password, 1, IntPtr.Zero));
            if (linkedSession <= 0)
                throw new InvalidOperationException($"O CMS recusou a sessao salva ({linkedSession}).");

            await Task.Run(() => XMEyeBridge.SetCloudToken(cmsIdentity.CloudToken));
            int mqttResult = await Task.Run(
                () => CmsSdk.CMS_Client_InitMqtt(cmsIdentity.CloudToken));
            await Task.Run(() => QrCloudApi.InitializeAppInfo(saved.QrSecret, saved.AppInfoEnc));
            bool localStoreReady = await WaitForLocalDeviceStoreAsync(
                TimeSpan.FromSeconds(8), CancellationToken.None);

            cloudGroupId = ushort.MaxValue;
            ClearAccountDevices();
            IReadOnlyList<CloudApi.AccountDevice> devices = await Task.Run(
                () => QrCloudApi.GetDevices(
                    saved.AccessToken, saved.LocalUser, saved.LocalPassword));
            (int synchronized, int failed) = SynchronizeAccountDevicesToCms(devices);
            int deviceLinkMonitor = await Task.Run(
                () => CmsSdk.CMS_Client_StartCheckDevLink());
            await Task.Run(() => CmsSdk.CMS_Client_EnableAutoModDeviceIP(true));

            cloudAccessToken = saved.AccessToken;
            accountDevices.AddRange(devices);
            DeviceBox.ItemsSource = accountDevices;
            DeviceBox.IsEnabled = accountDevices.Count > 0;
            SetGridButtonsEnabled(accountDevices.Count > 0);
            ForgetAccountButton.IsEnabled = true;
            if (accountDevices.Count > 0)
                DeviceBox.SelectedIndex = 0;

            QrStatusText.Text = accountDevices.Count > 0
                ? "Conta conectada automaticamente."
                : "Conta conectada, mas nenhuma camera foi encontrada.";
            Log($"Sessao protegida restaurada. Cameras encontradas: {accountDevices.Count}.");
            Log(importedVmsCredentials
                ? "Cadastro local correspondente localizado no VMS Pro."
                : "Cadastro local da conta carregado sem depender do VMS Pro.");
            Log($"Canal oficial MQTT da sessao restaurada: {mqttResult}; banco local {(localStoreReady ? "pronto" : "tempo esgotado")}; monitor {deviceLinkMonitor}.");
            Log($"Lista local do CMS sincronizada: {synchronized}/{accountDevices.Count}; falhas {failed}.");
            return true;
        }
        catch (Exception ex)
        {
            CloudSessionStore.Delete();
            ClearAccountDevices();
            cloudAccessToken = string.Empty;
            Log("A sessao protegida expirou ou foi recusada; um novo QR sera solicitado: " + SafeQrError(ex));
            return false;
        }
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
                Log($"Identidade CMS preparada: usuario tamanho {cmsIdentity.UserName.Length}; senha/token tamanho {cmsIdentity.Password.Length}.");
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
                ClearAccountDevices();
                IReadOnlyList<CloudApi.AccountDevice> devices =
                    await Task.Run(
                        () => QrCloudApi.GetDevices(
                            status.AccessToken, status.LocalUser, status.LocalPassword),
                        cancellationToken);
                (int synchronized, int failed) = SynchronizeAccountDevicesToCms(devices);
                int deviceLinkMonitor = await Task.Run(
                    () => CmsSdk.CMS_Client_StartCheckDevLink(),
                    cancellationToken);
                await Task.Run(
                    () => CmsSdk.CMS_Client_EnableAutoModDeviceIP(true),
                    cancellationToken);
                Log($"Sessao local vinculada ao QR: {linkedSession}.");
                Log("Identidade interna da conta aplicada ao CMS.");
                Log(importedVmsCredentials
                    ? "Credenciais locais importadas do VMS Pro."
                    : "Banco do VMS Pro nao localizado; usando somente os dados da nuvem.");
                Log($"Canal oficial MQTT da conta inicializado: {mqttResult}.");
                Log($"Banco local do CMS apos o QR: {(localStoreReady ? "pronto" : "tempo esgotado")}.");
                Log($"Monitor oficial de vinculo dos dispositivos iniciado: {deviceLinkMonitor}.");
                Log("Atualizacao automatica dos cadastros ativada como no VMS Pro.");
                cloudAccessToken = status.AccessToken;
                CloudSessionStore.Save(new CloudSessionStore.Session(
                    status.AccessToken,
                    challenge.Secret,
                    status.AppInfoEnc,
                    status.LocalUser,
                    status.LocalPassword));
                Log("Sessao da conta protegida pelo Windows para os proximos acessos.");
                accountDevices.AddRange(devices);
                DeviceBox.ItemsSource = accountDevices;
                DeviceBox.IsEnabled = accountDevices.Count > 0;
                SetGridButtonsEnabled(accountDevices.Count > 0);
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
                    Log("As cameras serao abertas somente apos o callback final do login automatico.");
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
        if (account.Length == 0 || password.Length == 0)
        {
            System.Windows.MessageBox.Show("Informe o e-mail/usuario e a senha da conta.");
            return;
        }
        if (!sdkReady)
        {
            Log("O motor oficial ainda nao esta pronto para o login da conta.");
            return;
        }
        if (accountDevices.Count > 0)
        {
            System.Windows.MessageBox.Show(
                "A conta atual ja esta conectada. Use SAIR E LIMPAR DADOS DA CONTA antes de entrar com outra conta.");
            return;
        }

        SetAccountBusy(true);
        Log($"Login oficial por conta iniciado: usuario tamanho {account.Length}; senha informada sim; tipo 0.");
        try
        {
            int loginResult = await Task.Run(
                () => CmsSdk.CMS_Client_UserLogin(account, password, 0, IntPtr.Zero));
            if (loginResult != 1)
            {
                Log($"FALHA NO LOGIN OFICIAL POR CONTA: retorno {loginResult}.");
                return;
            }

            IReadOnlyList<CloudApi.AccountDevice> devices = await Task.Run(
                () => QrCloudApi.GetDevicesByAccount(account, password));
            bool localStoreReady = await WaitForLocalDeviceStoreAsync(
                TimeSpan.FromSeconds(8), CancellationToken.None);
            cloudGroupId = ushort.MaxValue;
            ClearAccountDevices();
            (int synchronized, int failed) = SynchronizeAccountDevicesToCms(devices);
            int deviceLinkMonitor = await Task.Run(
                () => CmsSdk.CMS_Client_StartCheckDevLink());
            await Task.Run(() => CmsSdk.CMS_Client_EnableAutoModDeviceIP(true));

            accountDevices.AddRange(devices);
            DeviceBox.ItemsSource = accountDevices;
            DeviceBox.IsEnabled = accountDevices.Count > 0;
            SetGridButtonsEnabled(accountDevices.Count > 0);
            ForgetAccountButton.IsEnabled = true;
            if (accountDevices.Count > 0)
                DeviceBox.SelectedIndex = 0;
            QrImage.Source = null;
            QrStatusText.Visibility = Visibility.Visible;
            QrStatusText.Text = accountDevices.Count > 0
                ? "Conta conectada por usuario e senha."
                : "Conta conectada, mas nenhuma camera foi encontrada.";
            Log($"Conta autenticada pelo fluxo oficial do VMS. Cameras encontradas: {accountDevices.Count}.");
            Log($"Banco local {(localStoreReady ? "pronto" : "tempo esgotado")}; " +
                $"lista CMS {synchronized}/{accountDevices.Count}; falhas {failed}; monitor {deviceLinkMonitor}.");
            Log("A senha da conta nao foi salva. Para entrada automatica futura, use o QR uma vez.");
        }
        catch (Exception ex)
        {
            Log("FALHA NO LOGIN OFICIAL POR CONTA: " + SafeQrError(ex));
        }
        finally
        {
            AccountPasswordBox.Clear();
            password = string.Empty;
            SetAccountBusy(false);
        }
    }

    private async void LegacyAccountLogin_Click(object sender, RoutedEventArgs e)
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
            SetGridButtonsEnabled(accountDevices.Count > 0);
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

    private void ConfigureVideoGrid(int slots)
    {
        int side = slots switch
        {
            <= 1 => 1,
            <= 4 => 2,
            <= 9 => 3,
            _ => 4
        };

        videoGrid.SuspendLayout();
        // Alguns containers recusados podem ter sido removidos da grade visual,
        // mas continuam vivos para preservar os HWNDs durante os callbacks. Ao
        // reconstruir, libera todos eles, estejam ou nao anexados a grade.
        foreach (Forms.Control control in videoPanels
            .Select(panel => panel.Parent)
            .Where(parent => parent is not null)
            .Distinct()
            .Cast<Forms.Control>()
            .ToArray())
            control.Dispose();
        videoGrid.Controls.Clear();
        videoGrid.ColumnStyles.Clear();
        videoGrid.RowStyles.Clear();
        videoGrid.ColumnCount = side;
        videoGrid.RowCount = side;
        for (int index = 0; index < side; index++)
        {
            videoGrid.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100F / side));
            videoGrid.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100F / side));
        }

        videoPanels.Clear();
        videoLabels.Clear();
        for (int index = 0; index < side * side; index++)
        {
            var container = new Forms.Panel
            {
                BackColor = System.Drawing.Color.Black,
                Dock = Forms.DockStyle.Fill,
                Margin = new Forms.Padding(1)
            };
            var panel = new Forms.Panel
            {
                BackColor = System.Drawing.Color.Black,
                Dock = Forms.DockStyle.Fill,
                Margin = Forms.Padding.Empty
            };
            var label = new Forms.Label
            {
                AutoEllipsis = true,
                BackColor = System.Drawing.Color.Black,
                Dock = Forms.DockStyle.Top,
                ForeColor = System.Drawing.Color.White,
                Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold),
                Height = 26,
                Padding = new Forms.Padding(5, 3, 5, 3),
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                UseMnemonic = false,
                Visible = false
            };
            container.Controls.Add(panel);
            container.Controls.Add(label);
            videoPanels.Add(panel);
            videoLabels.Add(label);
            videoGrid.Controls.Add(container, index % side, index / side);
        }
        videoGrid.ResumeLayout(performLayout: true);
    }

    private void CompactVideoGrid(IReadOnlyCollection<int> visibleWindows)
    {
        int[] ordered = visibleWindows
            .Where(window => window >= 0 && window < videoPanels.Count)
            .Distinct()
            .OrderBy(window => window)
            .ToArray();
        int columns = ordered.Length switch
        {
            <= 1 => 1,
            <= 4 => 2,
            <= 9 => 3,
            _ => 4
        };
        int rows = Math.Max(1, (int)Math.Ceiling(ordered.Length / (double)columns));
        var visible = ordered.ToHashSet();

        videoGrid.SuspendLayout();
        for (int window = 0; window < videoPanels.Count; window++)
        {
            Forms.Control? container = videoPanels[window].Parent;
            if (container is null || visible.Contains(window))
                continue;
            container.Visible = false;
            if (videoGrid.Controls.Contains(container))
                videoGrid.Controls.Remove(container);
        }

        videoGrid.ColumnStyles.Clear();
        videoGrid.RowStyles.Clear();
        videoGrid.ColumnCount = columns;
        videoGrid.RowCount = rows;
        for (int column = 0; column < columns; column++)
            videoGrid.ColumnStyles.Add(
                new Forms.ColumnStyle(Forms.SizeType.Percent, 100F / columns));
        for (int row = 0; row < rows; row++)
            videoGrid.RowStyles.Add(
                new Forms.RowStyle(Forms.SizeType.Percent, 100F / rows));

        for (int position = 0; position < ordered.Length; position++)
        {
            Forms.Control? container = videoPanels[ordered[position]].Parent;
            if (container is null)
                continue;
            container.Visible = true;
            if (!videoGrid.Controls.Contains(container))
                videoGrid.Controls.Add(container);
            videoGrid.SetCellPosition(container, new Forms.TableLayoutPanelCellPosition(
                position % columns, position / columns));
        }
        videoGrid.ResumeLayout(performLayout: true);
    }

    private void ArrangeProgressiveVideoGrid(
        IReadOnlyCollection<int> visibleWindows, int requestedSlots)
    {
        int side = requestedSlots switch
        {
            <= 1 => 1,
            <= 4 => 2,
            <= 9 => 3,
            _ => 4
        };
        int[] ordered = visibleWindows
            .Where(window => window >= 0 && window < videoPanels.Count)
            .Distinct()
            .OrderBy(window => window)
            .ToArray();
        var visible = ordered.ToHashSet();

        videoGrid.SuspendLayout();
        for (int window = 0; window < videoPanels.Count; window++)
        {
            Forms.Control? container = videoPanels[window].Parent;
            if (container is null || visible.Contains(window))
                continue;
            container.Visible = false;
            if (videoGrid.Controls.Contains(container))
                videoGrid.Controls.Remove(container);
        }

        videoGrid.ColumnStyles.Clear();
        videoGrid.RowStyles.Clear();
        videoGrid.ColumnCount = side;
        videoGrid.RowCount = side;
        for (int index = 0; index < side; index++)
        {
            videoGrid.ColumnStyles.Add(
                new Forms.ColumnStyle(Forms.SizeType.Percent, 100F / side));
            videoGrid.RowStyles.Add(
                new Forms.RowStyle(Forms.SizeType.Percent, 100F / side));
        }

        for (int position = 0; position < ordered.Length; position++)
        {
            Forms.Control? container = videoPanels[ordered[position]].Parent;
            if (container is null)
                continue;
            container.Visible = true;
            if (!videoGrid.Controls.Contains(container))
                videoGrid.Controls.Add(container);
            videoGrid.SetCellPosition(container, new Forms.TableLayoutPanelCellPosition(
                position % side, position / side));
        }
        videoGrid.ResumeLayout(performLayout: true);
    }

    private int[] RegisterVideoWindows(int count)
    {
        int registered = Math.Min(count, videoPanels.Count);
        var results = new int[registered];
        for (int window = 0; window < registered; window++)
        {
            int hwnd = unchecked((int)videoPanels[window].Handle.ToInt64());
            results[window] = CmsSdk.CMS_Client_CreatePlayWindow(window, hwnd, 0);
        }
        Log($"Janelas nativas da grade registradas: {registered}; retornos zero {results.Count(result => result == 0)}.");
        return results;
    }

    private void SetVideoLabel(
        int window, CloudApi.AccountDevice device, int channel)
    {
        if (window < 0 || window >= videoLabels.Count)
            return;
        string name = string.IsNullOrWhiteSpace(device.Alias)
            ? $"Câmera {window + 1}"
            : device.Alias;
        Forms.Label label = videoLabels[window];
        label.Text = $"{name} — Canal {channel + 1}";
        label.Visible = true;
        label.BringToFront();
    }

    private async void GridLayout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            !int.TryParse(button.Tag?.ToString(), out int slots) ||
            slots is not (1 or 4 or 9 or 16) || accountDevices.Count == 0)
            return;

        SetCameraBusy(true);
        try
        {
            await DisconnectVideoAsync(log: false);
            ConfigureVideoGrid(slots);
            ArrangeProgressiveVideoGrid(Array.Empty<int>(), slots);
            VideoPlaceholder.Text = "Procurando a primeira camera disponivel...";
            VideoPlaceholder.Visibility = Visibility.Visible;
            deviceId = 0;

            var devicesToTry = new List<(CloudApi.AccountDevice Device, int[] Channels)>();
            if (slots == 1 && DeviceBox.SelectedItem is CloudApi.AccountDevice selected)
            {
                int channel = int.Parse(
                    ((ComboBoxItem)ChannelBox.SelectedItem).Content.ToString()!);
                devicesToTry.Add((selected, [channel]));
            }
            else
            {
                IReadOnlyDictionary<string, int> channelCounts = ReadOfficialChannelCounts();
                var channelSummary = new List<string>();
                foreach (CloudApi.AccountDevice device in accountDevices)
                {
                    int declared = channelCounts.TryGetValue(
                        NormalizeChannelCloudId(device.CloudId), out int count)
                        ? count
                        : 0;
                    string name = string.IsNullOrWhiteSpace(device.Alias)
                        ? "Camera"
                        : device.Alias;
                    channelSummary.Add(
                        $"{name} {(declared > 0 ? declared : "desconhecido")}");
                    int channelsToOpen = declared > 0 ? declared : 1;
                    devicesToTry.Add((device,
                        Enumerable.Range(0, channelsToOpen).ToArray()));
                }
                Log("Canais declarados pelo CMS: " +
                    string.Join("; ", channelSummary) + ".");
            }

            int[] windowResults = RegisterVideoWindows(slots);
            Log($"Abrindo grade {slots}: {devicesToTry.Count} cameras em sequencia; " +
                $"stream {(SubstreamBox.IsChecked == true ? "Extra" : "Main")}.");

            int opened = 0;
            int rejected = 0;
            int nextWindow = 0;
            foreach ((CloudApi.AccountDevice device, int[] channels) in devicesToTry)
            {
                if (nextWindow >= slots)
                    break;

                string name = string.IsNullOrWhiteSpace(device.Alias)
                    ? "Camera XMEye"
                    : device.Alias;
                Log($"Grade: testando {name}; canais declarados {channels.Length}.");

                var info = new CmsSdk.DeviceInfo();
                int found = 0;
                int state = 0;
                bool allowOptimisticPreview = false;
                Stopwatch deviceTimer = Stopwatch.StartNew();
                while (deviceTimer.Elapsed < TimeSpan.FromSeconds(30))
                {
                    qtRuntime.ProcessEvents();
                    info = new CmsSdk.DeviceInfo();
                    found = CmsSdk.CMS_Client_GetDeviceByCloudID(
                        device.CloudId, 2, ref info);
                    state = info.Error;
                    if (info.ID > 0 &&
                        automaticDeviceLoginResults.TryGetValue(
                            info.ID, out int callbackState))
                        state = callbackState;

                    if (found != 0 && info.ID > 0 &&
                        (info.LoginHandle > 0 || state > 0))
                        break;
                    if (found != 0 && info.ID > 0 && state is -7 or -8)
                        break;
                    if (found != 0 && info.ID > 0 && state == -25 &&
                        deviceTimer.Elapsed >= TimeSpan.FromSeconds(5))
                    {
                        allowOptimisticPreview = true;
                        break;
                    }
                    await Task.Delay(250);
                }

                bool ready = found != 0 && info.ID > 0 &&
                    (info.LoginHandle > 0 || state > 0 || allowOptimisticPreview);
                if (!ready)
                {
                    rejected += channels.Length;
                    Log($"Grade: {name} pulada; login nao disponivel; estado {state}; " +
                        $"{channels.Length} canal(is) ignorado(s).");
                    continue;
                }

                foreach (int channel in channels)
                {
                    if (nextWindow >= slots)
                        break;

                    SetVideoLabel(nextWindow, device, channel);
                    int previewResult = CmsSdk.CMS_Client_StartPreview(
                        info.ID, nextWindow, channel,
                        SubstreamBox.IsChecked == true
                            ? CmsSdk.StreamType.Extra
                            : CmsSdk.StreamType.Main,
                        false);
                    Log($"Grade: {name}; canal {channel + 1}; quadro {nextWindow + 1}; " +
                        $"janela {windowResults[nextWindow]}; fluxo {previewResult}; estado {state}.");
                    if (previewResult == 0)
                    {
                        videoLabels[nextWindow].Visible = false;
                        rejected++;
                        continue;
                    }

                    activePreviewWindows.Add(nextWindow);
                    opened++;
                    nextWindow++;
                    ArrangeProgressiveVideoGrid(activePreviewWindows, slots);
                    VideoPlaceholder.Visibility = Visibility.Collapsed;
                    await Task.Delay(350);
                }
            }

            playing = activePreviewWindows.Count > 0;
            DisconnectButton.IsEnabled = playing;
            VideoPlaceholder.Text = playing
                ? string.Empty
                : "Nenhuma camera respondeu nesta tentativa";
            VideoPlaceholder.Visibility = playing
                ? Visibility.Collapsed
                : Visibility.Visible;
            Log($"GRADE {slots} CONCLUIDA EM SEQUENCIA: fluxos visiveis {opened}; " +
                $"canais ignorados/recusados {rejected}.");
        }
        catch (Exception ex)
        {
            Log("ERRO AO ABRIR A GRADE: " + ex.Message);
        }
        finally
        {
            SetCameraBusy(false);
        }
    }

    private async void GridLayoutLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            !int.TryParse(button.Tag?.ToString(), out int slots) ||
            slots is not (1 or 4 or 9 or 16) || accountDevices.Count == 0)
            return;

        SetCameraBusy(true);
        try
        {
            await DisconnectVideoAsync(log: false);
            ConfigureVideoGrid(slots);
            // Os HWNDs precisam existir para o CMS testar cada fluxo, mas os
            // respectivos quadros nao precisam aparecer durante essa triagem.
            // Mantem todos fora da grade visivel e publica somente os aceitos.
            CompactVideoGrid(Array.Empty<int>());
            VideoPlaceholder.Text = "Testando as cameras e os canais disponiveis...";
            VideoPlaceholder.Visibility = Visibility.Visible;
            deviceId = 0;

            var requests = new List<(CloudApi.AccountDevice Device, int Channel)>();
            if (slots == 1 && DeviceBox.SelectedItem is CloudApi.AccountDevice selected)
            {
                int channel = int.Parse(((ComboBoxItem)ChannelBox.SelectedItem).Content.ToString()!);
                requests.Add((selected, channel));
            }
            else
            {
                IReadOnlyDictionary<string, int> channelCounts = ReadOfficialChannelCounts();
                var channelSummary = new List<string>();
                foreach (CloudApi.AccountDevice device in accountDevices)
                {
                    int declared = channelCounts.TryGetValue(
                        NormalizeChannelCloudId(device.CloudId), out int count)
                        ? count
                        : 0;
                    string name = string.IsNullOrWhiteSpace(device.Alias)
                        ? "Camera"
                        : device.Alias;
                    channelSummary.Add($"{name} {(declared > 0 ? declared : "desconhecido")}");

                    // AnalogChan + DigitalChan vem do cadastro oficial do CMS.
                    // Zero significa que o aparelho ainda nao revelou sua
                    // capacidade; conserva apenas o canal 1 para diagnostico.
                    int channelsToOpen = declared > 0 ? declared : 1;
                    for (int channel = 0;
                         channel < channelsToOpen && requests.Count < slots;
                         channel++)
                        requests.Add((device, channel));
                    if (requests.Count >= slots)
                        break;
                }
                Log("Canais declarados pelo CMS: " + string.Join("; ", channelSummary) + ".");
            }
            if (requests.Count > slots)
                requests.RemoveRange(slots, requests.Count - slots);

            int[] windowResults = RegisterVideoWindows(requests.Count);

            for (int window = 0; window < requests.Count && window < videoLabels.Count; window++)
                SetVideoLabel(window, requests[window].Device, requests[window].Channel);

            Log($"Abrindo grade {slots}: {requests.Count} tentativas em ordem; " +
                $"stream {(SubstreamBox.IsChecked == true ? "Extra" : "Main")}.");
            int opened = 0;
            int rejected = 0;
            var completedWindows = new HashSet<int>();
            var optimisticPreviewAttempts = new HashSet<int>();
            var emptyCredentialRetries = new Dictionary<string, int>(StringComparer.Ordinal);
            var lastLoginStates = new Dictionary<int, int>();
            Stopwatch loginTimer = Stopwatch.StartNew();
            while (completedWindows.Count < requests.Count &&
                   loginTimer.Elapsed < TimeSpan.FromSeconds(30))
            {
                qtRuntime.ProcessEvents();
                for (int window = 0; window < requests.Count && window < videoPanels.Count; window++)
                {
                    if (completedWindows.Contains(window))
                        continue;

                    (CloudApi.AccountDevice device, int channel) = requests[window];
                    var info = new CmsSdk.DeviceInfo();
                    int found = CmsSdk.CMS_Client_GetDeviceByCloudID(device.CloudId, 2, ref info);
                    int state = info.Error;
                    if (info.ID > 0 && automaticDeviceLoginResults.TryGetValue(info.ID, out int callbackState))
                        state = callbackState;
                    lastLoginStates[window] = state;

                    if (found == 0 || info.ID <= 0)
                    {
                        completedWindows.Add(window);
                        rejected++;
                        Log($"Grade: quadro {window + 1}; cadastro nao localizado; canal {channel}.");
                        continue;
                    }

                    // O VMS Pro consegue um login tradicional adicional usando a
                    // identidade indireta da conta (senha entregue ao NetSDK com
                    // tamanho zero). A copia importada pode fazer esta build do CMS
                    // enviar a credencial armazenada diretamente e receber -7.
                    // Recria somente os dispositivos recusados, uma unica vez, com
                    // senha e AdminToken vazios. Preservar o AdminToken fazia o SDK
                    // reconstruir a credencial direta de 64 caracteres; o VMS usa
                    // a identidade indireta da conta neste caminho (tamanho zero).
                    if (state == -7 && loginTimer.Elapsed >= TimeSpan.FromSeconds(5) &&
                        !emptyCredentialRetries.ContainsKey(device.CloudId))
                    {
                        try { CmsSdk.CMS_Client_DeviceLoginOrLogout(info.ID, false); }
                        catch { }
                        int removed = CmsSdk.CMS_Client_RemoveDevice(info.ID);
                        string name = string.IsNullOrWhiteSpace(device.Alias)
                            ? "Camera XMEye"
                            : device.Alias;
                        int added = CmsSdk.CMS_Client_AddDeviceByID(
                            FormatCmsRegistrationId(device.CloudId),
                            device.DeviceUser,
                            string.Empty,
                            string.Empty,
                            0,
                            name,
                            cloudGroupId,
                            device.IsShared);
                        int statusResult = added > 0
                            ? XMEyeBridge.QueryDeviceStatus(device.CloudId)
                            : int.MinValue;
                        emptyCredentialRetries[device.CloudId] = added;
                        Log($"Grade: {name}; dispositivo recusado com -7; " +
                            $"fallback sem senha e sem AdminToken: remocao {removed}; " +
                            $"novo ID {added}; consulta {statusResult}.");
                        continue;
                    }

                    // Depois que o proprio cadastro alternativo tambem devolve
                    // -7, nao ha outra etapa pendente: este quadro ja pode ser
                    // descartado. -8 tambem e terminal. Assim a grade final e
                    // revelada logo que a triagem termina, sem exibir recusados.
                    bool fallbackWasRejected = state == -7 &&
                        emptyCredentialRetries.TryGetValue(device.CloudId, out int fallbackId) &&
                        fallbackId > 0 && info.ID == fallbackId;
                    if (fallbackWasRejected || state == -8)
                    {
                        completedWindows.Add(window);
                        rejected++;
                        string name = string.IsNullOrWhiteSpace(device.Alias)
                            ? "Camera XMEye"
                            : device.Alias;
                        Log($"Grade: {name}; canal {channel}; estado terminal {state}; " +
                            "quadro ocultado antes de exibir a grade.");
                        continue;
                    }

                    // O CMS pode publicar um erro provisório e concluir o mesmo login
                    // alguns segundos depois (por exemplo, -4 seguido de 1). O VMS Pro
                    // mantém a tentativa viva; a grade deve fazer o mesmo até o prazo.
                    if (info.LoginHandle <= 0 && state <= 0)
                    {
                        // Nesta versao do CMS, -25 pode ser publicado mesmo depois
                        // de o NetSDK concluir o login por token. O VMS segue adiante
                        // e deixa a abertura do canal confirmar a sessao. Fazemos o
                        // mesmo uma unica vez, depois de dar tempo ao login nativo.
                        if (state != -25 || loginTimer.Elapsed < TimeSpan.FromSeconds(5) ||
                            !optimisticPreviewAttempts.Add(window))
                            continue;

                        int optimisticResult = CmsSdk.CMS_Client_StartPreview(
                            info.ID, window, channel,
                            SubstreamBox.IsChecked == true ? CmsSdk.StreamType.Extra : CmsSdk.StreamType.Main,
                            false);
                        videoLabels[window].BringToFront();
                        Log($"Grade: quadro {window + 1}; estado intermediario -25; " +
                            $"preview de confirmacao {optimisticResult}.");
                        if (optimisticResult == 0)
                            continue;

                        completedWindows.Add(window);
                        activePreviewWindows.Add(window);
                        opened++;
                        await Task.Delay(150);
                        continue;
                    }

                    int previewResult = CmsSdk.CMS_Client_StartPreview(
                        info.ID, window, channel,
                        SubstreamBox.IsChecked == true ? CmsSdk.StreamType.Extra : CmsSdk.StreamType.Main,
                        false);
                    videoLabels[window].BringToFront();
                    completedWindows.Add(window);
                    Log($"Grade: quadro {window + 1}; dispositivo tecnico {info.ID}; canal {channel}; " +
                        $"janela {windowResults[window]}; fluxo {previewResult}; estado {state}.");
                    if (previewResult != 0)
                    {
                        activePreviewWindows.Add(window);
                        opened++;
                    }
                    else
                    {
                        rejected++;
                    }
                    await Task.Delay(150);
                }
                if (completedWindows.Count < requests.Count)
                    await Task.Delay(250);
            }

            for (int window = 0; window < requests.Count; window++)
            {
                if (completedWindows.Contains(window))
                    continue;
                completedWindows.Add(window);
                rejected++;
                int finalState = lastLoginStates.TryGetValue(window, out int state) ? state : 0;
                Log($"Grade: quadro {window + 1}; login nao concluido apos 30 segundos; " +
                    $"ultimo estado {finalState}.");
            }

            int hidden = requests.Count - activePreviewWindows.Count;
            CompactVideoGrid(activePreviewWindows);
            Log($"Grade compactada: {activePreviewWindows.Count} fluxos aceitos visiveis; " +
                $"{hidden} recusados ocultados automaticamente.");
            playing = activePreviewWindows.Count > 0;
            DisconnectButton.IsEnabled = playing;
            VideoPlaceholder.Text = playing
                ? string.Empty
                : "Nenhuma camera respondeu nesta tentativa";
            VideoPlaceholder.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            Log($"GRADE {slots} INICIADA: fluxos aceitos {opened}; quadros ignorados/recusados {rejected}.");
        }
        catch (Exception ex)
        {
            Log("ERRO AO ABRIR A GRADE: " + ex.Message);
        }
        finally
        {
            SetCameraBusy(false);
        }
    }

    private async void OpenCamera_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not CloudApi.AccountDevice selected)
            return;
        SetCameraBusy(true);
        int selectedChannel = int.Parse(((ComboBoxItem)ChannelBox.SelectedItem).Content.ToString()!);
        Log($"Parametros seguros da selecao: Cloud ID tamanho {selected.CloudId.Length}; " +
            $"usuario tamanho {selected.DeviceUser.Length}; senha tamanho {selected.DevicePassword.Length}; " +
            $"token tamanho {selected.AdminToken.Length}; compartilhada {(selected.IsShared ? 1 : 0)}; " +
            $"canal {selectedChannel}; stream {(SubstreamBox.IsChecked == true ? "Extra" : "Main")}.");
        Log("Conectando à câmera selecionada pelo Cloud ID retornado pela conta...");
        try
        {
            await DisconnectVideoAsync(log: false);
            ConfigureVideoGrid(1);
            SetVideoLabel(0, selected, selectedChannel);
            int windowResult = RegisterVideoWindows(1)[0];
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
            int channel = selectedChannel;
            previewLoginError = 0;
            int previewResult = CmsSdk.CMS_Client_StartPreview(
                deviceId, 0, channel,
                SubstreamBox.IsChecked == true ? CmsSdk.StreamType.Extra : CmsSdk.StreamType.Main,
                false);
            Log($"Janela de vídeo: {windowResult}; abertura do fluxo: {previewResult}.");
            if (previewResult == 0)
            {
                Log("A câmera conectou, mas a abertura do vídeo falhou.");
                return;
            }
            (bool loginConfirmed, int loginError) = await WaitForPreviewLoginAsync(selected.CloudId);
            if (!loginConfirmed)
            {
                CmsSdk.CMS_Client_StopPreviewByWnd(0, 0);
                string detail = loginError == -7
                    ? "a credencial técnica do dispositivo foi recusada"
                    : loginError == -3
                        ? "tempo esgotado aguardando a confirmação do vídeo"
                        : "erro retornado pelo dispositivo ou pela nuvem";
                Log($"FALHA AO CONFIRMAR O VÍDEO — código {loginError}: {detail}.");
                return;
            }
            activePreviewWindows.Add(0);
            playing = true;
            VideoPlaceholder.Visibility = Visibility.Collapsed;
            DisconnectButton.IsEnabled = true;
            Log("VÍDEO REMOTO ABERTO.");
        }
        catch (Exception ex) { Log("ERRO AO ABRIR A CÂMERA: " + ex.Message); }
        finally { SetCameraBusy(false); }
    }

    private async Task<(bool Ok, int Error)> WaitForPreviewLoginAsync(string cloudId)
    {
        for (int attempt = 0; attempt < 80; attempt++)
        {
            await Task.Delay(250);
            if (previewLoginError < 0)
            {
                Log($"Confirmacao do preview interrompida no ciclo {attempt + 1}/80 pelo callback {previewLoginError}.");
                return (false, previewLoginError);
            }
            var info = new CmsSdk.DeviceInfo();
            int found = CmsSdk.CMS_Client_GetDeviceByCloudID(cloudId, 2, ref info);
            if (info.LoginHandle > 0)
            {
                Log($"Confirmacao do preview recebida no ciclo {attempt + 1}/80: consulta {found}; " +
                    $"ID {info.ID}; loginType {info.LoginType}; loginHandle positivo; erro {info.Error}.");
                return (true, 0);
            }
            if (info.Error < 0)
            {
                Log($"Confirmacao do preview recusada no ciclo {attempt + 1}/80: consulta {found}; " +
                    $"ID {info.ID}; loginType {info.LoginType}; loginHandle {info.LoginHandle}; erro {info.Error}.");
                return (false, info.Error);
            }
            if (info.Error > 0)
            {
                Log($"Confirmacao do preview recebida no ciclo {attempt + 1}/80: consulta {found}; " +
                    $"ID {info.ID}; estado positivo {info.Error}; aguardando os callbacks de video.");
                return (true, 0);
            }
        }
        Log("Confirmacao do preview esgotou 80 ciclos em 20 segundos sem handle ou erro do cadastro.");
        return (false, -3);
    }

    private async Task<(bool Ok, int Error, CmsSdk.DeviceInfo Info)> ConnectSelectedDeviceAsync(
        CloudApi.AccountDevice selected)
    {
        var info = new CmsSdk.DeviceInfo();
        int found = CmsSdk.CMS_Client_GetDeviceByCloudID(selected.CloudId, 2, ref info);
        Log($"Consulta inicial do cadastro: retorno {found}; ID {info.ID}; " +
            $"loginType {info.LoginType}; loginHandle {info.LoginHandle}; erro {info.Error}.");

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
        Log($"Consulta final do cadastro: retorno {found}; ID {info.ID}; " +
            $"loginType {info.LoginType}; loginHandle {info.LoginHandle}; erro {info.Error}.");
        if (found == 0 || info.ID <= 0)
            return (false, -2, info);

        Log("Cadastro Cloud preservado como foi criado pela sincronização oficial da conta.");

        Log("Aguardando o login automatico do CMS, como no VMS Pro...");
        (bool loginReady, int loginError) = await WaitForAutomaticDeviceLoginAsync(
            selected.CloudId, info.ID, TimeSpan.FromSeconds(30));
        if (!loginReady)
            return (false, loginError, info);

        found = CmsSdk.CMS_Client_GetDeviceByCloudID(selected.CloudId, 2, ref info);
        Log($"Login automatico confirmado: consulta {found}; ID {info.ID}; " +
            $"loginHandle {(info.LoginHandle > 0 ? "positivo" : "nao exposto")}; erro {info.Error}.");
        Log("Dispositivo preparado; a visualizacao reutilizara a sessao autenticada pelo CMS.");
        return (true, 0, info);
    }

    private async Task<(bool Ok, int Error)> WaitForAutomaticDeviceLoginAsync(
        string cloudId, int selectedDeviceId, TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        int lastStatusQuery = int.MinValue;
        while (timer.Elapsed < timeout)
        {
            qtRuntime.ProcessEvents();
            var info = new CmsSdk.DeviceInfo();
            int found = CmsSdk.CMS_Client_GetDeviceByCloudID(cloudId, 2, ref info);
            if (found != 0 && info.LoginHandle > 0)
                return (true, 0);

            if (automaticDeviceLoginResults.TryGetValue(selectedDeviceId, out int result))
            {
                if (result > 0)
                    return (true, 0);
                if (result < 0)
                {
                    Log($"Login automatico recusado pelo CMS: dispositivo {selectedDeviceId}; retorno {result}.");
                    return (false, result);
                }
            }

            // O VMS inicia a verificacao alguns segundos apos carregar o banco.
            // Esta consulta desperta o mesmo ciclo se ele ainda nao comecou.
            if (lastStatusQuery == int.MinValue && timer.Elapsed >= TimeSpan.FromSeconds(3))
            {
                lastStatusQuery = XMEyeBridge.QueryDeviceStatus(cloudId);
                Log($"Ciclo automatico de estado solicitado: retorno {lastStatusQuery}.");
            }
            await Task.Delay(100);
        }

        Log("Login automatico do CMS esgotou 30 segundos sem callback final.");
        return (false, -3);
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

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OpenCameraButton.IsEnabled = DeviceBox.SelectedItem is CloudApi.AccountDevice;
        if (DeviceBox.SelectedItem is not CloudApi.AccountDevice selected)
            return;

        IReadOnlyDictionary<string, int> counts = ReadOfficialChannelCounts();
        int declared = counts.TryGetValue(
            NormalizeChannelCloudId(selected.CloudId), out int count)
            ? count
            : 0;
        // Enquanto a capacidade for desconhecida, conserva quatro opcoes para
        // diagnostico manual. Assim que o CMS informar a quantidade real, o
        // seletor passa a mostrar exatamente todos os canais existentes.
        int options = declared > 0 ? Math.Min(64, declared) : 4;
        int previous = Math.Max(0, ChannelBox.SelectedIndex);
        ChannelBox.Items.Clear();
        for (int channel = 0; channel < options; channel++)
            ChannelBox.Items.Add(new ComboBoxItem { Content = channel.ToString() });
        ChannelBox.SelectedIndex = Math.Min(previous, options - 1);
    }

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

    private bool EnsureCmsCloudUserStore(string cloudUser)
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
        cmsDevicesDatabasePath = devicesDatabase;
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

    private IReadOnlyDictionary<string, int> ReadOfficialChannelCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (cmsDevicesDatabasePath.Length == 0 || !File.Exists(cmsDevicesDatabasePath))
            return counts;

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = cmsDevicesDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 2
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT CloudID, COALESCE(AnalogChan, 0), COALESCE(DigitalChan, 0) FROM Devices";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                    continue;
                string cloudId = NormalizeChannelCloudId(reader.GetString(0));
                int analog = Math.Max(0, reader.GetInt32(1));
                int digital = Math.Max(0, reader.GetInt32(2));
                int total = Math.Min(64, analog + digital);
                if (cloudId.Length > 0 && total > 0)
                    counts[cloudId] = total;
            }
        }
        catch (Exception ex)
        {
            Log("AVISO: nao foi possivel ler a quantidade oficial de canais: " + ex.Message);
        }
        return counts;
    }

    private static string NormalizeChannelCloudId(string cloudId)
    {
        string normalized = cloudId.Trim();
        const string suffix = "_Cloud";
        return normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? normalized[..^suffix.Length]
            : normalized;
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
                string sourceProfile = Path.Combine(
                    Path.GetDirectoryName(fullCandidate)!, "config.ini");
                string destinationProfile = Path.Combine(
                    Path.GetDirectoryName(fullDestination)!, "config.ini");
                if (File.Exists(sourceProfile))
                    File.Copy(sourceProfile, destinationProfile, overwrite: true);
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

    private async void Disconnect_Click(object sender, RoutedEventArgs e) =>
        await DisconnectVideoAsync(log: true);

    private async void ForgetAccount_Click(object sender, RoutedEventArgs e)
    {
        await DisconnectVideoAsync(log: false);
        CloudSessionStore.Delete();
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
        SetGridButtonsEnabled(false);
        accountDevices.Clear();
        automaticDeviceLoginResults.Clear();
    }

    private void DisconnectVideo(bool log)
    {
        foreach (int window in activePreviewWindows.ToArray())
        {
            try { CmsSdk.CMS_Client_StopPreviewByWnd(window, 0); }
            catch { }
        }
        activePreviewWindows.Clear();
        playing = false;
        // O VMS encerra somente o preview e preserva o login automatico do
        // dispositivo para que outra camera possa ser aberta imediatamente.
        deviceId = 0;
        VideoPlaceholder.Visibility = Visibility.Visible;
        DisconnectButton.IsEnabled = false;
        if (log) Log("Vídeo desconectado.");
    }

    private async Task DisconnectVideoAsync(bool log)
    {
        bool hadNativeWindows = activePreviewWindows.Count > 0;
        DisconnectVideo(log: false);
        if (hadNativeWindows)
        {
            // StopPreview is asynchronous. Keep every HWND alive while the
            // decoder/render threads finish before rebuilding the WinForms grid.
            await Task.Delay(600);
            qtRuntime.ProcessEvents();
        }
        if (log) Log("Vídeo desconectado.");
    }

    private void SetAccountBusy(bool busy)
    {
        AccountLoginButton.IsEnabled = !busy && sdkReady;
        RefreshCaptchaButton.IsEnabled = !busy && sdkReady;
        AccountLoginButton.Content = busy ? "CARREGANDO..." : "ENTRAR E CARREGAR CÂMERAS";
    }

    private void SetCameraBusy(bool busy)
    {
        OpenCameraButton.IsEnabled = !busy && DeviceBox.SelectedItem is CloudApi.AccountDevice;
        OpenCameraButton.Content = busy ? "CONECTANDO..." : "ABRIR CÂMERA SELECIONADA";
        SetGridButtonsEnabled(!busy && accountDevices.Count > 0);
    }

    private void SetGridButtonsEnabled(bool enabled)
    {
        foreach (UIElement element in GridButtonsPanel.Children)
            element.IsEnabled = enabled;
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

    private void SendDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string report = BuildSafeDiagnosticReport();
            string title = $"Diagnostico {diagnosticSession} - falha ao abrir video";
            string issueUrl =
                "https://github.com/rottagio/xmeye-cloud-tester/issues/new" +
                "?title=" + Uri.EscapeDataString(title) +
                "&labels=diagnostic" +
                "&body=" + Uri.EscapeDataString(report);
            Process.Start(new ProcessStartInfo(issueUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "Nao foi possivel abrir o envio do diagnostico.\n\n" + ex.Message,
                "Diagnostico", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string BuildSafeDiagnosticReport()
    {
        Version version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
        string log = SanitizeDiagnostic(LogBox.Text);
        const int maxLogLength = 12_000;
        if (log.Length > maxLogLength)
            log = "[trecho inicial omitido]\n" + log[^maxLogLength..];

        var report = new StringBuilder();
        report.AppendLine("## Diagnostico automatico do XMEye Cloud Tester");
        report.AppendLine();
        report.AppendLine($"- Sessao: `{diagnosticSession}`");
        report.AppendLine($"- Versao: `{version.Major}.{version.Minor}.{version.Build}`");
        report.AppendLine($"- Windows: `{Environment.OSVersion.Version}`");
        report.AppendLine($"- Arquitetura: `{RuntimeInformation.ProcessArchitecture}`");
        report.AppendLine("- Regiao Cloud: `Brasil (SA)`");
        report.AppendLine();
        report.AppendLine("Dados pessoais, credenciais, seriais, tokens, IPs e caminhos locais foram removidos automaticamente.");
        report.AppendLine();
        report.AppendLine("```text");
        report.Append(log.TrimEnd());
        report.AppendLine();
        report.AppendLine("```");
        return report.ToString();
    }

    private static string SanitizeDiagnostic(string text)
    {
        string safe = Regex.Replace(
            text,
            @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
            "[email removido]",
            RegexOptions.CultureInvariant);
        safe = Regex.Replace(
            safe,
            @"(?i)(?:[A-Z]:\\|\\\\)[^\r\n]+",
            "[caminho removido]",
            RegexOptions.CultureInvariant);
        safe = Regex.Replace(
            safe,
            @"(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)",
            "[ip removido]",
            RegexOptions.CultureInvariant);
        safe = Regex.Replace(
            safe,
            @"(?<![A-Za-z0-9+/=_\-])[A-Za-z0-9+/=_\-]{16,}(?![A-Za-z0-9+/=_\-])",
            "[dado tecnico removido]",
            RegexOptions.CultureInvariant);
        return safe;
    }

    private void OnSdkMessage(
        CmsSdk.MessageType type, int p1, int p2, int p3, int p4,
        IntPtr text1, IntPtr text2, uint size, IntPtr user)
    {
        if (isClosing)
            return;
        if (type == CmsSdk.MessageType.DeviceControl && p1 == 3)
        {
            automaticDeviceLoginResults[p2] = p4;
            if (p2 == deviceId && p4 < 0)
                previewLoginError = p4;
        }
        // Native text is deliberately excluded because some SDK messages may
        // contain device metadata. Only non-sensitive numeric diagnostics are logged.
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (type == CmsSdk.MessageType.VideoWindowControl &&
                p1 is 0 or 27 && p4 >= 0 && p4 < videoLabels.Count)
                videoLabels[p4].BringToFront();
            Log($"SDK {type}: {p1}, {p2}, {p3}, {p4}.");
        }));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        isClosing = true;
        try
        {
            qrLoginCts?.Cancel();
            qrLoginCts?.Dispose();
            foreach (int window in Enumerable.Range(0, Math.Max(videoPanels.Count, 16)))
            {
                try { CmsSdk.CMS_Client_StopPreviewByWnd(window, 0); }
                catch { }
            }
            activePreviewWindows.Clear();
            ClearAccountDevices();
            AccountPasswordBox.Clear();
            CaptchaBox.Clear();
            captchaToken = string.Empty;
            // Explicit CMS/QApplication teardown races with decoder and callback
            // threads in this SDK build. Process exit safely reclaims both; calling
            // UnInit here caused AccessViolation and heap-corruption crashes.
            sdkReady = false;
        }
        catch { }
    }
}
