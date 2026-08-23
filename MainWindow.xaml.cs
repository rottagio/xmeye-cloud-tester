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
    private readonly string diagnosticPath;
    private int deviceId;
    private bool playing;
    private bool sdkReady;
    private bool cloudReady;
    private bool captchaBusy;
    private string captchaToken = string.Empty;
    private CancellationTokenSource? qrLoginCts;
    private bool qrBusy;

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
            Directory.SetCurrentDirectory(baseDirectory);
            NativeMethods.SetDllDirectory(baseDirectory);
            string plugins = Path.Combine(baseDirectory, "plugins");
            Environment.SetEnvironmentVariable("QT_PLUGIN_PATH", plugins + ";" + baseDirectory);
            Environment.SetEnvironmentVariable("QT_QPA_PLATFORM_PLUGIN_PATH", Path.Combine(plugins, "platforms"));

            string dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XMEyeCloudAccountTester");
            Directory.CreateDirectory(dataDirectory);
            string sourceConfig = Path.Combine(baseDirectory, "config.ini");
            string localConfig = Path.Combine(dataDirectory, "config.ini");
            if (File.Exists(sourceConfig) && !File.Exists(localConfig))
                File.Copy(sourceConfig, localConfig);

            Log("Driver SQLite: " +
                (File.Exists(Path.Combine(plugins, "sqldrivers", "qsqlite.dll")) ? "carregado do pacote" : "ausente") + ".");
            qtRuntime.Initialize();
            int cmsResult = CmsSdk.CMS_Client_Init(dataDirectory.Replace('\\', '/'), sdkCallback, IntPtr.Zero, 0);
            sdkReady = cmsResult == 0;
            if (!sdkReady)
                throw new InvalidOperationException($"CMS_Client_Init retornou {cmsResult}.");

            IntPtr cloudIp = Marshal.AllocHGlobal(256);
            try
            {
                unsafe { new Span<byte>((void*)cloudIp, 256).Clear(); }
                int localLogin = CmsSdk.CMS_Client_UserLogin("admin", "", 0, cloudIp);
                Log($"Motor de vídeo inicializado (sessão local: {localLogin}).");
            }
            finally { Marshal.FreeHGlobal(cloudIp); }

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
                IReadOnlyList<CloudApi.AccountDevice> devices =
                    await CloudApi.GetDevicesByAccessTokenAsync(status.AccessToken);
                ClearAccountDevices();
                accountDevices.AddRange(devices);
                DeviceBox.ItemsSource = accountDevices;
                DeviceBox.IsEnabled = accountDevices.Count > 0;
                ForgetAccountButton.IsEnabled = true;
                if (accountDevices.Count > 0)
                {
                    DeviceBox.SelectedIndex = 0;
                    QrStatusText.Text = "Conta conectada.";
                    Log($"Conta autenticada pelo QR. Câmeras encontradas: {accountDevices.Count}.");
                }
                else
                {
                    QrStatusText.Text = "Conta conectada, mas nenhuma câmera foi encontrada.";
                    Log("Conta autenticada pelo QR, mas nenhuma câmera vinculada foi retornada.");
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
            var result = await Task.Run(async () =>
            {
                var info = new CmsSdk.DeviceInfo();
                int found = CmsSdk.CMS_Client_GetDeviceByCloudID(selected.CloudId, 2, ref info);
                if (found == 0)
                {
                    string name = string.IsNullOrWhiteSpace(selected.Alias) ? "Câmera XMEye" : selected.Alias;
                    int added = CmsSdk.CMS_Client_AddDeviceByID(
                        selected.CloudId, selected.DeviceUser, selected.DevicePassword,
                        0, name, 0, 2);
                    if (added == 0)
                        return (Ok: false, Error: -1, Info: info);
                    found = CmsSdk.CMS_Client_GetDeviceByCloudID(selected.CloudId, 2, ref info);
                }
                if (found == 0 || info.ID <= 0)
                    return (Ok: false, Error: -2, Info: info);

                CmsSdk.CMS_Client_DeviceLoginOrLogout(info.ID, true);
                for (int attempt = 0; attempt < 50; attempt++)
                {
                    await Task.Delay(400);
                    CmsSdk.CMS_Client_GetDeviceByCloudID(selected.CloudId, 2, ref info);
                    if (info.LoginHandle > 0)
                        return (Ok: true, Error: 0, Info: info);
                    if (info.Error != 0)
                        return (Ok: false, Error: info.Error, Info: info);
                }
                return (Ok: false, Error: -3, Info: info);
            });

            if (!result.Ok)
            {
                string detail = result.Error switch
                {
                    -1 => "não foi possível cadastrar a câmera no motor local",
                    -2 => "a câmera não apareceu no motor local",
                    -3 => "tempo esgotado aguardando a conexão P2P",
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

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        OpenCameraButton.IsEnabled = DeviceBox.SelectedItem is CloudApi.AccountDevice;

    private void Disconnect_Click(object sender, RoutedEventArgs e) => DisconnectVideo(log: true);

    private async void ForgetAccount_Click(object sender, RoutedEventArgs e)
    {
        DisconnectVideo(log: false);
        ClearAccountDevices();
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
            if (sdkReady) CmsSdk.CMS_Client_UnInit();
            qtRuntime.Dispose();
        }
        catch { }
    }
}
