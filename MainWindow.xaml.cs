using System.ComponentModel;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace XMEyeCloudTester;

public partial class MainWindow : Window
{
    private static class NativeMethods
    {
        internal const int GwlExStyle = -20;
        internal const long WsExLayoutRtl = 0x00400000L;
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoZOrder = 0x0004;
        internal const uint SwpFrameChanged = 0x0020;
        internal const uint WmSetIcon = 0x0080;
        internal const int IconSmall = 0;
        internal const int IconBig = 1;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool SetDllDirectory(string path);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(
            IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessage(
            IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    }

    private System.Drawing.Icon? nativeLargeIcon;
    private System.Drawing.Icon? nativeSmallIcon;
    private bool finalNativeIconPassScheduled;

    private sealed class CameraHeaderLabel : Forms.Label
    {
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);

            const int dotSize = 7;
            int dotY = Math.Max(0, (Height - dotSize) / 2);
            using (var dotBrush = new System.Drawing.SolidBrush(ForeColor))
                e.Graphics.FillEllipse(dotBrush, 8, dotY, dotSize, dotSize);

            string caption = Text.StartsWith("● ", StringComparison.Ordinal)
                ? Text[2..]
                : Text;
            var textBounds = new System.Drawing.Rectangle(
                21,
                0,
                Math.Max(0, Width - 26),
                Height);
            Forms.TextRenderer.DrawText(
                e.Graphics,
                caption,
                Font,
                textBounds,
                System.Drawing.Color.White,
                Forms.TextFormatFlags.Left |
                Forms.TextFormatFlags.VerticalCenter |
                Forms.TextFormatFlags.SingleLine |
                Forms.TextFormatFlags.EndEllipsis |
                Forms.TextFormatFlags.NoPrefix);
        }
    }

    private readonly Forms.TableLayoutPanel videoGrid = new()
    {
        BackColor = System.Drawing.Color.Black,
        Dock = Forms.DockStyle.Fill,
        CellBorderStyle = Forms.TableLayoutPanelCellBorderStyle.Single
    };
    private readonly List<Forms.Panel> videoPanels = [];
    private readonly List<Forms.Label> videoLabels = [];
    private readonly List<Forms.Label> videoBadges = [];
    private readonly List<Forms.Label> videoLoadingLabels = [];
    private readonly List<Forms.Panel> videoContainers = [];
    private readonly HashSet<int> activePreviewWindows = [];
    private readonly ConcurrentDictionary<int, PreviewBinding> previewBindings = new();
    private readonly ConcurrentDictionary<int, byte> confirmedPreviewWindows = new();
    private readonly ConcurrentDictionary<int, byte> failedPreviewWindows = new();
    private readonly ConcurrentDictionary<int, byte> recoveringPreviewWindows = new();
    private readonly ConcurrentDictionary<int, byte> disconnectedPreviewDevices = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> deviceReconnectLocks = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> passiveRecoveryLocks = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> disconnectedRecoveryLocks = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> deviceLoginAttemptLocks = new();
    private readonly ConcurrentDictionary<int, DeviceStabilityState> deviceStabilityStates = new();
    private readonly SemaphoreSlim manualReconnectCycleGate = new(1, 1);
    private readonly SemaphoreSlim p2pReloginGate = new(1, 1);
    private readonly ConcurrentDictionary<int, DeviceRequestProtection> deviceRequestProtections = new();
    private readonly ConcurrentDictionary<int, string> deviceCloudIds = new();
    private readonly ConcurrentQueue<PreviewBinding> capabilityDiscoveryQueue = new();
    private readonly ConcurrentDictionary<string, byte> queuedCapabilityDiscovery = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> attemptedCapabilityDiscovery = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim capabilityDiscoveryGate = new(1, 1);
    private static readonly TimeSpan CapabilityDiscoveryInitialDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CapabilityDiscoverySpacing = TimeSpan.FromSeconds(5);
    private const int SystemFunctionCommand = 1360;
    private const int PtzControlConfigCommand = 0x175;
    private const int StorageInfoCommand = 0x22;
    private const int RecordStorageTypeCommand = 0x7C;
    private const int RecordConfigCommand = 0x17;
    private const int CameraLightConfigCommand = 0x10415;
    private const int StorageInfoSize = 5572;
    private const int RecordStorageTypeSize = 4;
    private const int RecordConfigSize = 1360;
    private const int CameraLightConfigSize = 100;
    private const int DeviceConfigBufferSize = 64 * 1024;
    private readonly ConcurrentDictionary<int, PtzCapabilityState> ptzCapabilities = new();
    private readonly ConcurrentDictionary<(int DeviceId, int Command), IntPtr> pendingPtzConfigBuffers = new();
    private readonly ConcurrentDictionary<(int DeviceId, int Command), PendingBinaryConfigRead> pendingBinaryConfigReads = new();
    private readonly ConcurrentDictionary<(int DeviceId, int Command), PendingBinaryConfigWrite> pendingBinaryConfigWrites = new();
    private readonly ConcurrentDictionary<int, byte> timedOutDetailedConfigDevices = new();
    private readonly ConcurrentDictionary<(string DeviceKey, string Section, int Channel), byte> attemptedDetailedConfigReads = new();
    private readonly SemaphoreSlim detailedConfigReadGate = new(1, 1);
    private readonly SemaphoreSlim deviceConfigIoGate = new(1, 1);
    private readonly object ptzLogLock = new();
    private long ptzLogOffset = -1;
    private string ptzLogRemainder = string.Empty;
    private readonly Queue<string> ptzSystemResponses = new();
    private readonly Queue<string> ptzOrientationResponses = new();
    private readonly Queue<string> genericJsonResponses = new();
    private readonly Queue<int> pendingPtzSystemDevices = new();
    private readonly Queue<int> pendingPtzOrientationDevices = new();
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
    private bool accountBusy;
    private bool cameraBusy;
    private bool accountLogoutInProgress;
    private int cloudGroupId;
    private string cloudAccessToken = string.Empty;
    private volatile int previewLoginError;
    private bool isClosing;
    private readonly AppPreferences preferences = AppPreferences.Load();
    private readonly CameraCatalogStore cameraCatalog = CameraCatalogStore.Load();
    private readonly DeviceProfileStore deviceProfiles = DeviceProfileStore.Load();
    private readonly DeviceReadOnlyConfigStore readOnlyDeviceConfigs = DeviceReadOnlyConfigStore.Load();
    private bool preferencesReady;
    private int currentLayoutSlots = 1;
    private int selectedPreviewWindow = -1;
    private int soundingPreviewWindow = -1;
    private int audioDisplayPreviewWindow = -1;
    private int talkingDeviceId = -1;
    private int talkingChannel = -1;
    private bool talkInputOpen;
    private int activePtzCommand = -1;
    private readonly HashSet<int> recordingPreviewWindows = [];
    private readonly HashSet<int> mirroredPreviewWindows = [];
    private int focusedPreviewWindow = -1;
    private long previewGeneration;
    private int previewDragWindow = -1;
    private System.Drawing.Point previewDragStart;
    private readonly DispatcherTimer layoutRestoreTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(900)
    };
    private readonly bool controlledRequestTest = string.Equals(
        Environment.GetEnvironmentVariable("XMEYE_CONTROLLED_REQUEST_TEST"),
        "1",
        StringComparison.Ordinal);
    private bool onlineRefreshBusy;
    private bool gridOpening;
    private readonly object gridLayoutQueueSync = new();
    private bool gridLayoutWorkerRunning;
    private int? queuedGridLayoutSlots;
    private Task gridLayoutWorker = Task.CompletedTask;
    private readonly Forms.Panel recordingPlaybackPanel = new()
    {
        BackColor = System.Drawing.Color.Black,
        Dock = Forms.DockStyle.Fill
    };
    private readonly H264FilePlayer recordingPlayer = new();
    private readonly DispatcherTimer recordingPlaybackTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    private CancellationTokenSource? recordingThumbnailCts;
    private bool recordingTimelineUpdating;
    private LocalMediaItem? playingLocalMedia;
    private bool showRecordingMedia = true;
    private DateTime lastManualReconnectCycleUtc = DateTime.MinValue;
    private int nextFloatingWindow = 40;
    private readonly HashSet<int> floatingPreviewWindows = [];
    private readonly Forms.ToolTip cameraToolTip = new()
    {
        AutomaticDelay = 350,
        AutoPopDelay = 5000,
        ShowAlways = true
    };

    private sealed record PreviewBinding(
        int DeviceId, int Window, int Channel, CmsSdk.StreamType StreamType,
        string DisplayName, string CloudId, long Generation);

    private sealed class DeviceRequestProtection
    {
        internal readonly object Sync = new();
        internal int ConsecutiveFailures;
        internal int LastError;
        internal DateTime LastFailureUtc;
        internal DateTime NextAllowedUtc;
    }

    private sealed class DeviceStabilityState
    {
        internal readonly object Sync = new();
        internal readonly Queue<DateTime> DisconnectsUtc = new();
        internal DateTime LastDisconnectUtc;
        internal DateTime UnstableUntilUtc;
        internal DateTime LastPassiveCycleUtc;
        internal DateTime LastDeviceLoginAttemptUtc;
    }

    private sealed class PtzCapabilityState
    {
        internal bool? DeviceSupportsPtz;
        internal readonly Dictionary<int, (bool Mirror, bool Flip)> Channels = [];
    }

    private sealed class PendingBinaryConfigRead
    {
        internal required IntPtr Buffer;
        internal required int Size;
        internal required TaskCompletionSource<byte[]?> Completion;
    }

    private sealed class PendingBinaryConfigWrite
    {
        internal required IntPtr Buffer;
        internal required TaskCompletionSource<int?> Completion;
    }

    private sealed record ConfigurationWriteResult(
        bool Success, bool RolledBack, string Message);

    private sealed class LocalMediaItem : INotifyPropertyChanged
    {
        private string? thumbnailPath;
        internal required FileInfo File { get; init; }
        public required string Title { get; init; }
        public required string Subtitle { get; init; }
        public bool IsRecording { get; init; }
        public string? ThumbnailPath
        {
            get => thumbnailPath;
            set
            {
                if (thumbnailPath == value) return;
                thumbnailPath = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailPath)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class DeviceCompatibilityRow
    {
        public required string DeviceKey { get; init; }
        public required string Camera { get; init; }
        public required string Identifier { get; init; }
        public required string Model { get; init; }
        public required string Firmware { get; init; }
        public required string InternalCode { get; init; }
        public required string Platform { get; init; }
        public required string Channels { get; init; }
        public required string Ptz { get; init; }
        public required string HumanDetection { get; init; }
        public required string DoubleLight { get; init; }
        public required string AlarmSound { get; init; }
        public required string MotionDetection { get; init; }
        public required string MotionTracking { get; init; }
        public required string PtzPresets { get; init; }
        public required string TwoWayTalk { get; init; }
        public required string Wifi { get; init; }
        public required string CloudUpgrade { get; init; }
        public required string Inventory { get; init; }
        public required string Updated { get; init; }
    }

    private sealed class DeviceSettingRow
    {
        public required string Section { get; init; }
        public required string Setting { get; init; }
        public required string Support { get; init; }
        public required string DataState { get; init; }
        public required string Source { get; init; }
        public required string Observed { get; init; }
    }

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
        SourceInitialized += ApplyNativeWindowIcon;
        ContentRendered += ScheduleFinalNativeWindowIconPass;
        Closed += (_, _) => ReleaseNativeWindowIcons();
        if (controlledRequestTest)
            preferences.AutoReconnect = false;
        RestoreLayoutBox.IsChecked = preferences.RestoreLastLayout;
        AutoReconnectBox.IsChecked = preferences.AutoReconnect;
        DefaultQualityBox.SelectedIndex = preferences.DefaultSd ? 0 : 1;
        ConnectionTimeoutBox.SelectedIndex = preferences.ConnectionTimeoutSeconds switch
        {
            30 => 0,
            90 => 2,
            _ => 1
        };
        foreach (ComboBoxItem item in ReconnectDelayBox.Items)
            if (int.TryParse(item.Tag?.ToString(), out int delay) && delay == preferences.ReconnectDelaySeconds)
                ReconnectDelayBox.SelectedItem = item;
        SubstreamBox.IsChecked = preferences.DefaultSd;
        CaptureFolderBox.Text = preferences.GetCaptureFolder();
        RecordingFolderBox.Text = preferences.GetRecordingFolder();
        StartWithWindowsBox.IsChecked = preferences.StartWithWindows;
        LanguageBox.SelectedIndex = string.Equals(preferences.Language, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ThemeBox.SelectedIndex = string.Equals(preferences.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        foreach (ComboBoxItem item in StorageLimitBox.Items)
            if (int.TryParse(item.Tag?.ToString(), out int value) && value == preferences.StorageLimitGb)
                StorageLimitBox.SelectedItem = item;
        preferencesReady = true;
        ApplyTheme(preferences.Theme);
        ApplyLanguage(preferences.Language);
        Version version = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
        VersionText.Text = $"Versao {version.Major}.{version.Minor}.{version.Build}";
        VideoHost.Child = videoGrid;
        RecordingPlayerHost.Child = recordingPlaybackPanel;
        recordingPlaybackTimer.Tick += RecordingPlaybackTimer_Tick;
        layoutRestoreTimer.Tick += (_, _) =>
        {
            layoutRestoreTimer.Stop();
            if (gridOpening)
            {
                layoutRestoreTimer.Start();
                return;
            }
            RestoreConfiguredVideoGrid(onlyWhenChanged: true);
        };
        ConfigureVideoGrid(1);
        UpdateStreamQualityButtons();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Log($"Diagnostico iniciado: sessao {diagnosticSession}; versao {version.Major}.{version.Minor}.{version.Build}; " +
            $"Windows {Environment.OSVersion.Version}; processo {RuntimeInformation.ProcessArchitecture}.");
        if (cameraCatalog.MigratedLegacyChannelDetections > 0)
            Log($"Migração automática: {cameraCatalog.MigratedLegacyChannelDetections} " +
                "detecção(ões) antiga(s) de canal liberada(s) para uma única revalidação.");
        if (!string.IsNullOrWhiteSpace(App.WindowsIdentityRegistrationMessage))
            Log(App.WindowsIdentityRegistrationMessage);
        if (!string.IsNullOrWhiteSpace(App.AccountLogoutCleanupMessage))
            Log(App.AccountLogoutCleanupMessage);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeSdk();
        if (!sdkReady)
            return;
        if (await TryRestoreSavedSessionAsync())
        {
            if (controlledRequestTest)
            {
                Log("MODO DE TESTE CONTROLADO: sessão restaurada sem abrir a grade e sem reconexão automática.");
            }
            else if (preferences.RestoreLastLayout && accountDevices.Count > 0)
            {
                await WaitForCmsMonitorBeforeAutoPlayAsync(TimeSpan.FromSeconds(15));
                await OpenGridAsync(preferences.LastGridSize);
            }
            return;
        }
        RestoreManualCatalogDevices();
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

            string dataDirectory = GetCmsDataDirectory();
            Directory.CreateDirectory(dataDirectory);
            EnsureCmsDataLayout(dataDirectory);
            string sourceConfig = Path.Combine(baseDirectory, "config.ini");
            string localConfig = Path.Combine(dataDirectory, "config.ini");
            if (File.Exists(sourceConfig) && !File.Exists(localConfig))
                File.Copy(sourceConfig, localConfig);
            ConfigureCmsRecordingPath(localConfig);
            string sourceCloudServer = Path.Combine(baseDirectory, "CloudServer");
            string localCloudServer = Path.Combine(dataDirectory, "CloudServer");
            if (File.Exists(sourceCloudServer))
                File.Copy(sourceCloudServer, localCloudServer, overwrite: true);

            // O VMS Pro usa caminho vazio no CMS_Client_Init e resolve
            // data/users e data/cloudusers relativamente ao diretorio atual.
            Directory.SetCurrentDirectory(dataDirectory);

            Log("Driver SQLite: " +
                (File.Exists(Path.Combine(plugins, "sqldrivers", "qsqlite.dll")) ? "carregado do pacote" : "ausente") + ".");
            qtRuntime.Initialize(GetQtPumpState);
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

    private void ConfigureCmsRecordingPath(string configPath)
    {
        string folder = preferences.GetRecordingFolder();
        Directory.CreateDirectory(folder);
        if (!File.Exists(configPath))
            return;

        string normalized = folder.Replace('\\', '/');
        string[] lines = File.ReadAllLines(configPath);
        bool replaced = false;
        for (int index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith("RecordPath=", StringComparison.OrdinalIgnoreCase))
                continue;
            lines[index] = "RecordPath=" + normalized;
            replaced = true;
            break;
        }
        if (!replaced)
            lines = [.. lines, "RecordPath=" + normalized];
        File.WriteAllLines(configPath, lines);
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
            List<CloudApi.AccountDevice> devices = (await Task.Run(
                () => QrCloudApi.GetDevices(
                    saved.AccessToken, saved.LocalUser, saved.LocalPassword))).ToList();
            // A pausa precisa ser conhecida antes da sincronização e da primeira
            // consulta ao CMS; aplicá-la depois ainda permitiria uma requisição.
            cameraCatalog.ApplyAndSort(devices);
            (int synchronized, int failed) = SynchronizeAccountDevicesToCms(devices);
            int deviceLinkMonitor = await Task.Run(
                () => CmsSdk.CMS_Client_StartCheckDevLink());
            await Task.Run(() => CmsSdk.CMS_Client_EnableAutoModDeviceIP(true));

            cloudAccessToken = saved.AccessToken;
            accountDevices.AddRange(devices);
            ApplyCameraCatalog();
            DeviceBox.ItemsSource = accountDevices;
            UpdateCameraSummary();
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
            Log("Monitor interno do CMS iniciado; nenhuma consulta individual foi enviada aos dispositivos.");
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
                List<CloudApi.AccountDevice> devices = (await Task.Run(
                        () => QrCloudApi.GetDevices(
                            status.AccessToken, status.LocalUser, status.LocalPassword),
                        cancellationToken)).ToList();
                // Bloqueios locais entram antes de qualquer sincronização ou
                // consulta de estado, nunca depois do primeiro pedido.
                cameraCatalog.ApplyAndSort(devices);
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
                cancellationToken.ThrowIfCancellationRequested();
                if (accountLogoutInProgress)
                {
                    Log("Login QR descartado porque o logout da conta esta em andamento.");
                    return;
                }

                cloudAccessToken = status.AccessToken;
                CloudSessionStore.Save(new CloudSessionStore.Session(
                    status.AccessToken,
                    challenge.Secret,
                    status.AppInfoEnc,
                    status.LocalUser,
                    status.LocalPassword));
                Log("Sessao da conta protegida pelo Windows para os proximos acessos.");
                accountDevices.AddRange(devices);
                ApplyCameraCatalog();
                DeviceBox.ItemsSource = accountDevices;
                UpdateCameraSummary();
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
                    Log("Monitor interno do CMS iniciado; nenhuma consulta individual foi enviada aos dispositivos.");
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
            ApplyCameraCatalog();
            DeviceBox.ItemsSource = accountDevices;
            UpdateCameraSummary();
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
            Log("Monitor interno do CMS iniciado; nenhuma consulta individual foi enviada aos dispositivos.");
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
            ApplyCameraCatalog();
            DeviceBox.ItemsSource = accountDevices;
            UpdateCameraSummary();
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

    private static string FriendlySdkError(int code) => code switch
    {
        0 => "operação não concluída pelo motor de vídeo",
        -1 => "não foi possível cadastrar a câmera no motor local",
        -2 => "a câmera não foi localizada no cadastro local",
        -3 => "tempo de conexão esgotado",
        -4 => "câmera offline ou indisponível na rede",
        -6 => "a nuvem ou o dispositivo recusou a operação",
        -7 => "usuário ou senha do dispositivo incorretos",
        -8 => "sessão da câmera indisponível ou expirada",
        -25 => "a câmera atingiu o limite de conexões simultâneas",
        -29 => "a autorização da conta para este dispositivo foi recusada",
        11602 or -11602 => "não foi possível localizar o servidor da câmera na nuvem",
        _ => "erro retornado pelo dispositivo ou pelo serviço XMEye"
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
        foreach (Forms.Control control in videoGrid.Controls.Cast<Forms.Control>().ToArray())
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
        videoBadges.Clear();
        videoLoadingLabels.Clear();
        videoContainers.Clear();
        mirroredPreviewWindows.Clear();
        for (int index = 0; index < side * side; index++)
        {
            int window = index;
            var container = new Forms.Panel
            {
                BackColor = System.Drawing.Color.FromArgb(43, 57, 73),
                Dock = Forms.DockStyle.Fill,
                Margin = new Forms.Padding(1),
                Padding = new Forms.Padding(1),
                AllowDrop = true
            };
            var panel = new Forms.Panel
            {
                BackColor = System.Drawing.Color.Black,
                Dock = Forms.DockStyle.Fill,
                Margin = Forms.Padding.Empty
            };
            var label = new CameraHeaderLabel
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
                Visible = false,
                AllowDrop = true,
                Cursor = Forms.Cursors.SizeAll
            };
            var badge = new Forms.Label
            {
                AutoSize = false,
                BackColor = System.Drawing.Color.FromArgb(28, 37, 49),
                ForeColor = System.Drawing.Color.White,
                Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold),
                Height = 25,
                Width = 118,
                Padding = new Forms.Padding(5, 2, 5, 2),
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Visible = false,
                Anchor = Forms.AnchorStyles.Top | Forms.AnchorStyles.Right
            };
            var loading = new Forms.Label
            {
                AutoSize = false,
                BackColor = System.Drawing.Color.Black,
                Dock = Forms.DockStyle.Fill,
                ForeColor = System.Drawing.Color.FromArgb(142, 162, 188),
                Font = new System.Drawing.Font("Segoe UI", 12F, System.Drawing.FontStyle.Regular),
                Text = "Carregando...",
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Visible = true
            };
            panel.Controls.Add(loading);
            container.Controls.Add(panel);
            container.Controls.Add(label);
            container.Controls.Add(badge);
            Forms.ContextMenuStrip cameraMenu = CreatePreviewContextMenu(window);
            container.ContextMenuStrip = cameraMenu;
            panel.ContextMenuStrip = cameraMenu;
            loading.ContextMenuStrip = cameraMenu;
            label.ContextMenuStrip = cameraMenu;
            badge.ContextMenuStrip = cameraMenu;
            void PositionBadge()
            {
                badge.Left = Math.Max(2, container.ClientSize.Width - badge.Width - 4);
                badge.Top = label.Height + 4;
                if (badge.Visible)
                    badge.BringToFront();
            }
            container.Resize += (_, _) => PositionBadge();
            PositionBadge();
            panel.Click += (_, _) => SelectPreviewWindow(window);
            panel.DoubleClick += (_, _) => TogglePreviewFocus(window);
            loading.Click += (_, _) => SelectPreviewWindow(window);
            loading.DoubleClick += (_, _) => TogglePreviewFocus(window);
            label.Click += (_, _) => SelectPreviewWindow(window);
            label.DoubleClick += (_, _) => TogglePreviewFocus(window);
            label.MouseDown += (_, args) => PreviewDragMouseDown(window, args);
            label.MouseMove += (_, args) => PreviewDragMouseMove(window, args);
            label.DragEnter += PreviewPanelDragEnter;
            label.DragDrop += (_, args) => PreviewDragDrop(window, args);
            container.DragEnter += PreviewPanelDragEnter;
            container.DragDrop += (_, args) => PreviewDragDrop(window, args);
            videoPanels.Add(panel);
            videoLabels.Add(label);
            videoBadges.Add(badge);
            videoLoadingLabels.Add(loading);
            videoContainers.Add(container);
            videoGrid.Controls.Add(container, index % side, index / side);
        }
        selectedPreviewWindow = -1;
        soundingPreviewWindow = -1;
        audioDisplayPreviewWindow = -1;
        focusedPreviewWindow = -1;
        SelectedCameraText.Text = "Nenhuma câmera selecionada";
        AudioButton.IsEnabled = false;
        AudioButton.Content = "🔊  Áudio";
        RecordButton.IsEnabled = false;
        RecordButton.Content = "⏺  Gravar";
        CaptureButton.IsEnabled = false;
        TalkButton.IsEnabled = false;
        TalkButton.Content = "🎙  Falar";
        PtzSidePanel.IsEnabled = false;
        PtzCameraText.Text = "Selecione uma câmera";
        SeparateWindowButton.IsEnabled = false;
        videoGrid.ResumeLayout(performLayout: true);
    }

    private Forms.ContextMenuStrip CreatePreviewContextMenu(int window)
    {
        var menu = new Forms.ContextMenuStrip();
        void Select() => SelectPreviewWindow(window);
        Forms.ToolStripItem Add(string text, Action action)
        {
            Forms.ToolStripItem item = menu.Items.Add(text);
            item.Click += (_, _) => { Select(); action(); };
            return item;
        }
        Add("🔊  Ouvir / silenciar", () => ToggleAudio_Click(this, new RoutedEventArgs()));
        Add("📷  Capturar foto", () => CaptureSelected_Click(this, new RoutedEventArgs()));
        Add("⏺  Iniciar / parar gravação", () => ToggleRecord_Click(this, new RoutedEventArgs()));
        Add("🎙  Falar", () => ToggleTalk_Click(this, new RoutedEventArgs()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        Forms.ToolStripItem ptzItem = Add("✥  Selecionar para PTZ", () => PtzCameraText.Text = SelectedCameraText.Text);
        Add("↕  Girar 180°", () => _ = RotateSelectedCameraAsync());
        Forms.ToolStripMenuItem mirrorItem = (Forms.ToolStripMenuItem)Add(
            "⇄  Espelhar exibição", ToggleMirrorDisplay);
        Add("↻  Reconectar", () => ReconnectAll_Click(this, new RoutedEventArgs()));
        Add("▣  Abrir em janela separada", () => OpenSeparateWindow_Click(this, new RoutedEventArgs()));
        menu.Opening += (_, args) =>
        {
            bool online = confirmedPreviewWindows.ContainsKey(window);
            foreach (Forms.ToolStripItem item in menu.Items)
                if (item is not Forms.ToolStripSeparator)
                    item.Enabled = online || (item.Text?.Contains("Reconectar", StringComparison.Ordinal) == true);
            ptzItem.Enabled = online && previewBindings.TryGetValue(window, out PreviewBinding? binding) &&
                SupportsPtz(binding);
            if (!previewBindings.ContainsKey(window))
                args.Cancel = true;
            mirrorItem.Checked = mirroredPreviewWindows.Contains(window);
        };
        return menu;
    }

    private Task RotateSelectedCameraAsync()
    {
        SendPtzOneShot(34, 5, "girar imagem");
        return Task.CompletedTask;
    }

    private void ToggleMirrorDisplay_Click(object sender, RoutedEventArgs e) =>
        ToggleMirrorDisplay();

    private void ToggleMirrorDisplay()
    {
        if (selectedPreviewWindow < 0 ||
            !previewBindings.TryGetValue(selectedPreviewWindow, out PreviewBinding? binding))
            return;

        bool mirror = !mirroredPreviewWindows.Contains(selectedPreviewWindow);
        ApplyLocalMirror(selectedPreviewWindow, mirror);
        CloudApi.AccountDevice? device = accountDevices.FirstOrDefault(item =>
            string.Equals(item.CloudId, binding.CloudId, StringComparison.Ordinal));
        if (device is not null)
        {
            cameraCatalog.GetOrCreate(device, int.MaxValue).MirrorDisplay = mirror;
            SaveCameraCatalog();
        }
        SelectedCameraText.Text = mirror
            ? $"{binding.DisplayName} — imagem espelhada neste computador"
            : $"{binding.DisplayName} — espelhamento removido";
        Log($"Espelhamento local: janela {selectedPreviewWindow}; ativo {(mirror ? 1 : 0)}.");
    }

    private void ApplyLocalMirror(int window, bool mirror)
    {
        if (window < 0 || window >= videoPanels.Count)
            return;
        IntPtr handle = videoPanels[window].Handle;
        long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        long updated = mirror
            ? style | NativeMethods.WsExLayoutRtl
            : style & ~NativeMethods.WsExLayoutRtl;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new IntPtr(updated));
        NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize |
            NativeMethods.SwpNoZOrder | NativeMethods.SwpFrameChanged);
        videoPanels[window].Invalidate(true);
        if (mirror) mirroredPreviewWindows.Add(window);
        else mirroredPreviewWindows.Remove(window);
    }

    private void ApplySavedMirror(PreviewBinding binding)
    {
        CloudApi.AccountDevice? device = accountDevices.FirstOrDefault(item =>
            string.Equals(item.CloudId, binding.CloudId, StringComparison.Ordinal));
        bool mirror = device is not null &&
            cameraCatalog.GetOrCreate(device, int.MaxValue).MirrorDisplay;
        if (mirroredPreviewWindows.Contains(binding.Window) != mirror)
            ApplyLocalMirror(binding.Window, mirror);
    }

    private void RestoreConfiguredVideoGrid(bool onlyWhenChanged = false)
    {
        if (videoContainers.Count == 0)
            return;

        int visibleSlots = Math.Min(currentLayoutSlots, videoContainers.Count);
        int side = (int)Math.Sqrt(currentLayoutSlots);
        var rank = preferences.LiveLayoutOrder
            .Select((key, index) => (key, index))
            .GroupBy(item => item.key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        List<int> visualOrder = previewBindings.Values
            .OrderBy(binding => rank.TryGetValue(
                PreviewOrderKey(binding.CloudId, binding.Channel), out int order)
                    ? order
                    : int.MaxValue)
            .ThenBy(binding => binding.Window)
            .Select(binding => binding.Window)
            .Concat(Enumerable.Range(0, videoContainers.Count)
                .Where(window => !previewBindings.ContainsKey(window)))
            .Take(visibleSlots)
            .ToList();

        if (onlyWhenChanged)
        {
            bool changed = false;
            for (int visualIndex = 0; visualIndex < visualOrder.Count; visualIndex++)
            {
                Forms.Panel container = videoContainers[visualOrder[visualIndex]];
                if (!container.Visible || videoGrid.GetColumn(container) != visualIndex % side ||
                    videoGrid.GetRow(container) != visualIndex / side)
                {
                    changed = true;
                    break;
                }
            }
            if (!changed)
                return;
        }

        videoGrid.SuspendLayout();
        for (int index = 0; index < videoContainers.Count; index++)
        {
            Forms.Panel container = videoContainers[index];
            videoGrid.SetColumnSpan(container, 1);
            container.Visible = false;
        }
        for (int visualIndex = 0; visualIndex < visualOrder.Count; visualIndex++)
        {
            Forms.Panel container = videoContainers[visualOrder[visualIndex]];
            videoGrid.SetColumn(container, visualIndex % side);
            videoGrid.SetRow(container, visualIndex / side);
            container.Visible = true;
        }

        videoGrid.ColumnStyles.Clear();
        videoGrid.RowStyles.Clear();
        videoGrid.ColumnCount = side;
        videoGrid.RowCount = side;
        for (int index = 0; index < side; index++)
        {
            videoGrid.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100F / side));
            videoGrid.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100F / side));
        }
        videoGrid.ResumeLayout(performLayout: true);
        focusedPreviewWindow = -1;
        Log($"Grade restaurada sem mover janelas nativas: {currentLayoutSlots} quadros em {side}x{side}.");
    }

    private void ScheduleLayoutRestore()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)ScheduleLayoutRestore);
            return;
        }
        layoutRestoreTimer.Stop();
        layoutRestoreTimer.Start();
    }

    private void SelectPreviewWindow(int window)
    {
        if (!previewBindings.TryGetValue(window, out PreviewBinding? binding))
            return;

        selectedPreviewWindow = window;
        for (int index = 0; index < videoContainers.Count; index++)
        {
            bool selected = index == window;
            videoLabels[index].BackColor = System.Drawing.Color.Black;
            videoContainers[index].BackColor = selected
                ? System.Drawing.Color.FromArgb(0, 157, 229)
                : System.Drawing.Color.FromArgb(43, 57, 73);
            if (videoLabels[index].Visible)
                videoLabels[index].BringToFront();
        }
        SelectedCameraText.Text = $"{binding.DisplayName} — Canal {binding.Channel + 1}";
        bool online = confirmedPreviewWindows.ContainsKey(window);
        CaptureButton.IsEnabled = online;
        AudioButton.IsEnabled = online;
        RecordButton.IsEnabled = online;
        TalkButton.IsEnabled = online;
        bool ptzAvailable = online && SupportsPtz(binding);
        PtzSidePanel.IsEnabled = ptzAvailable;
        PtzCameraText.Text = ptzAvailable
            ? $"{binding.DisplayName} — Canal {binding.Channel + 1}"
            : online
                ? $"{binding.DisplayName} — PTZ indisponível neste canal"
                : $"{binding.DisplayName} — offline";
        SeparateWindowButton.IsEnabled = online;
        LoadSmartControls(binding);
        UpdateAudioButton(window);
        UpdateRecordButton(window);
        UpdateTalkButton(binding);
    }

    private void UpdateTalkButton(PreviewBinding binding)
    {
        int targetChannel = binding.Channel > 0 ? 0 : binding.Channel;
        bool talking = talkingDeviceId == binding.DeviceId && talkingChannel == targetChannel;
        TalkButton.Content = talking ? "■  Parar fala" : "🎙  Falar";
    }

    private void ToggleTalk_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPreviewWindow < 0 ||
            !confirmedPreviewWindows.ContainsKey(selectedPreviewWindow) ||
            !previewBindings.TryGetValue(selectedPreviewWindow, out PreviewBinding? binding))
        {
            SelectedCameraText.Text = "Selecione uma câmera online para falar";
            return;
        }

        try
        {
            // Em câmeras multissensor XM o alto-falante normalmente pertence
            // ao canal principal, inclusive quando o canal 2 está selecionado.
            int talkChannel = binding.Channel > 0 ? 0 : binding.Channel;
            bool sameCamera = talkingDeviceId == binding.DeviceId && talkingChannel == talkChannel;
            StopActiveTalk();
            if (!sameCamera)
            {
                int microphoneResult = CmsSdk.CMS_Client_OpenTalk(0, true);
                if (microphoneResult <= 0)
                {
                    SelectedCameraText.Text = "Não foi possível abrir o microfone padrão";
                    Log($"Fala não iniciada: microfone padrão recusado; retorno {microphoneResult}.");
                    return;
                }
                talkInputOpen = true;
                CmsSdk.CMS_Client_StartTalk(binding.DeviceId, talkChannel);
                talkingDeviceId = binding.DeviceId;
                talkingChannel = talkChannel;
                RefreshPreviewHeader(binding.Window);
                SelectedCameraText.Text = $"Falando em {binding.DisplayName}";
                Log($"Fala iniciada: dispositivo {binding.DeviceId}; canal de áudio {talkChannel}.");
            }
            UpdateTalkButton(binding);
        }
        catch (Exception ex)
        {
            StopActiveTalk();
            SelectedCameraText.Text = "A câmera não aceitou o áudio bidirecional";
            Log("Falha ao iniciar fala: " + SanitizeDiagnostic(ex.Message));
        }
    }

    private void StopActiveTalk()
    {
        int device = talkingDeviceId;
        int channel = talkingChannel;
        bool closeTalkInput = talkInputOpen;
        talkingDeviceId = -1;
        talkingChannel = -1;
        talkInputOpen = false;
        if (sdkReady && device >= 0)
        {
            try { CmsSdk.CMS_Client_StopTalk(device, channel); }
            catch { }
        }
        if (sdkReady && closeTalkInput)
        {
            try { CmsSdk.CMS_Client_OpenTalk(0, false); }
            catch { }
        }
        TalkButton.Content = "🎙  Falar";
        foreach (PreviewBinding binding in previewBindings.Values.Where(item => item.DeviceId == device))
            RefreshPreviewHeader(binding.Window);
    }

    private void PtzCommand_Down(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            !int.TryParse(element.Tag?.ToString(), out int command))
            return;
        SendPtzCommand(command, stop: false);
        element.CaptureMouse();
        e.Handled = true;
    }

    private void PtzCommand_Up(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        StopPtzCommand();
        if (sender is FrameworkElement element)
            element.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void PtzCommand_Leave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            StopPtzCommand();
    }

    private void PtzOneShot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            !int.TryParse(element.Tag?.ToString(), out int command))
            return;
        SendPtzOneShot(command, 5, command == 34 ? "girar imagem" : "reiniciar posição PTZ");
    }

    private int SelectedPresetNumber()
    {
        if (PtzPresetBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int value))
            return value;
        return 1;
    }

    private void PtzPresetGo_Click(object sender, RoutedEventArgs e) =>
        SendPtzOneShot(19, SelectedPresetNumber(), "abrir posição favorita");

    private void PtzPresetSave_Click(object sender, RoutedEventArgs e) =>
        SendPtzOneShot(17, SelectedPresetNumber(), "salvar posição favorita");

    private void PtzPresetQuick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element &&
            int.TryParse(element.Tag?.ToString(), out int preset))
            SendPtzOneShot(19, preset, "abrir posição favorita");
    }

    private void ApplyNativeWindowIcon(object? sender, EventArgs e) =>
        ApplyNativeWindowIcon("SourceInitialized");

    private void ScheduleFinalNativeWindowIconPass(object? sender, EventArgs e)
    {
        if (finalNativeIconPassScheduled)
            return;
        finalNativeIconPassScheduled = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => ApplyNativeWindowIcon("ContentRendered/ApplicationIdle")));
    }

    private void ApplyNativeWindowIcon(string stage)
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            string? executable = Environment.ProcessPath;
            if (helper.Handle == IntPtr.Zero || string.IsNullOrWhiteSpace(executable))
                return;

            using System.Drawing.Icon? embedded = System.Drawing.Icon.ExtractAssociatedIcon(executable);
            if (embedded is null)
            {
                Log($"Ícone nativo ({stage}): o executável não forneceu um ícone associado.");
                return;
            }

            var newLargeIcon = new System.Drawing.Icon(embedded, new System.Drawing.Size(32, 32));
            var newSmallIcon = new System.Drawing.Icon(embedded, new System.Drawing.Size(16, 16));
            BitmapSource windowIcon = Imaging.CreateBitmapSourceFromHIcon(
                newLargeIcon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            windowIcon.Freeze();
            Icon = windowIcon;
            _ = NativeMethods.SendMessage(helper.Handle, NativeMethods.WmSetIcon,
                (IntPtr)NativeMethods.IconBig, newLargeIcon.Handle);
            _ = NativeMethods.SendMessage(helper.Handle, NativeMethods.WmSetIcon,
                (IntPtr)NativeMethods.IconSmall, newSmallIcon.Handle);

            System.Drawing.Icon? oldLargeIcon = nativeLargeIcon;
            System.Drawing.Icon? oldSmallIcon = nativeSmallIcon;
            nativeLargeIcon = newLargeIcon;
            nativeSmallIcon = newSmallIcon;
            oldLargeIcon?.Dispose();
            oldSmallIcon?.Dispose();
            Log($"Ícone nativo aplicado ({stage}): janela=0x{helper.Handle.ToInt64():X}; " +
                $"grande=0x{nativeLargeIcon.Handle.ToInt64():X}; pequeno=0x{nativeSmallIcon.Handle.ToInt64():X}.");
        }
        catch (Exception ex)
        {
            Log($"Ícone nativo falhou ({stage}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ReleaseNativeWindowIcons()
    {
        nativeLargeIcon?.Dispose();
        nativeLargeIcon = null;
        nativeSmallIcon?.Dispose();
        nativeSmallIcon = null;
    }

    private void PtzPresetQuick_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element &&
            int.TryParse(element.Tag?.ToString(), out int preset))
        {
            SendPtzOneShot(17, preset, "salvar posição favorita");
            e.Handled = true;
        }
    }

    private bool SupportsPtz(PreviewBinding binding)
    {
        if (ptzCapabilities.TryGetValue(binding.DeviceId, out PtzCapabilityState? state))
        {
            lock (state)
                return state.DeviceSupportsPtz == true && state.Channels.ContainsKey(binding.Channel);
        }

        CloudApi.AccountDevice? device = accountDevices.FirstOrDefault(item =>
            string.Equals(item.CloudId, binding.CloudId, StringComparison.Ordinal));
        if (device is null)
            return false;
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
        return entry.PtzSupportedChannels.TryGetValue(binding.Channel, out bool supported) && supported;
    }

    private (bool Mirror, bool Flip) PtzOrientation(PreviewBinding binding)
    {
        if (ptzCapabilities.TryGetValue(binding.DeviceId, out PtzCapabilityState? state))
        {
            lock (state)
                if (state.Channels.TryGetValue(binding.Channel, out var orientation))
                    return orientation;
        }

        CloudApi.AccountDevice? device = accountDevices.FirstOrDefault(item =>
            string.Equals(item.CloudId, binding.CloudId, StringComparison.Ordinal));
        if (device is null)
            return default;
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
        entry.PtzMirrorChannels.TryGetValue(binding.Channel, out bool mirror);
        entry.PtzFlipChannels.TryGetValue(binding.Channel, out bool flip);
        return (mirror, flip);
    }

    private int MapPtzDirection(PreviewBinding binding, int command)
    {
        (bool mirror, bool flip) = PtzOrientation(binding);
        if (mirror && command is 2 or 3)
            command = command == 2 ? 3 : 2;
        if (flip && command is 0 or 1)
            command = command == 0 ? 1 : 0;
        return command;
    }

    private void QueueAutomaticCapabilityDiscovery(PreviewBinding binding)
    {
        if (isClosing || string.IsNullOrWhiteSpace(binding.CloudId) ||
            deviceProfiles.TryGetCurrentCapability(
                binding.CloudId, "Identification.SystemFunctionSchema3", out _))
            return;
        if (!queuedCapabilityDiscovery.TryAdd(binding.CloudId, 0))
            return;
        capabilityDiscoveryQueue.Enqueue(binding);
        _ = ProcessAutomaticCapabilityDiscoveryQueueAsync();
    }

    private async Task ProcessAutomaticCapabilityDiscoveryQueueAsync()
    {
        if (!await capabilityDiscoveryGate.WaitAsync(0).ConfigureAwait(false))
            return;
        try
        {
            await Task.Delay(CapabilityDiscoveryInitialDelay).ConfigureAwait(false);
            while (!isClosing && capabilityDiscoveryQueue.TryDequeue(out PreviewBinding? queued))
            {
                queuedCapabilityDiscovery.TryRemove(queued.CloudId, out _);
                if (deviceProfiles.TryGetCurrentCapability(
                        queued.CloudId, "Identification.SystemFunctionSchema3", out _) ||
                    attemptedCapabilityDiscovery.ContainsKey(queued.CloudId))
                    continue;

                PreviewBinding? online = previewBindings.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.CloudId, queued.CloudId, StringComparison.Ordinal) &&
                    confirmedPreviewWindows.ContainsKey(candidate.Window));
                TimeSpan wait = TimeSpan.Zero;
                int lastError = 0;
                if (online is null ||
                    !CanIssueDeviceRequest(online.DeviceId, out wait, out _, out lastError))
                {
                    if (online is not null)
                        Log($"Identificacao automatica adiada: dispositivo {online.DeviceId}; " +
                            $"erro {lastError}; espera {Math.Ceiling(wait.TotalMinutes)} min.");
                    continue;
                }

                // One inventory read per device/firmware, only after video is
                // confirmed. There is no automatic retry in the same session.
                DeviceConfigurationCatalog.Definition? inventoryDefinition =
                    DeviceConfigurationCatalog.Find("Capability.SystemFunction");
                if (inventoryDefinition is null ||
                    !DeviceConfigurationReadPolicy.CanDiscoverAutomatically(inventoryDefinition))
                {
                    Log("Identificação automática bloqueada pela política de leitura.");
                    continue;
                }
                attemptedCapabilityDiscovery[queued.CloudId] = 0;
                await deviceConfigIoGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    InitializePtzLogCursor();
                    RequestPtzConfig(online.DeviceId, SystemFunctionCommand,
                        "{\"Name\":\"SystemFunction\",\"SessionID\":\"0x08\"}");
                    Log($"Identificacao automatica enviada uma vez apos a primeira imagem: dispositivo {online.DeviceId}.");
                    await Task.Delay(CapabilityDiscoverySpacing).ConfigureAwait(false);
                }
                finally
                {
                    deviceConfigIoGate.Release();
                }
            }
        }
        finally
        {
            capabilityDiscoveryGate.Release();
            if (!isClosing && !capabilityDiscoveryQueue.IsEmpty)
                _ = ProcessAutomaticCapabilityDiscoveryQueueAsync();
        }
    }

    private string NetSdkLogPath => Path.Combine(AppContext.BaseDirectory, "Log", "log", "netsdk.log");

    private void InitializePtzLogCursor()
    {
        lock (ptzLogLock)
        {
            if (ptzLogOffset >= 0)
                return;
            try
            {
                var file = new FileInfo(NetSdkLogPath);
                ptzLogOffset = file.Exists ? file.Length : 0;
            }
            catch { ptzLogOffset = 0; }
        }
    }

    private void ReadNewPtzResponsesLocked()
    {
        try
        {
            string path = NetSdkLogPath;
            if (!File.Exists(path))
                return;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (ptzLogOffset < 0)
                ptzLogOffset = stream.Length;
            if (stream.Length < ptzLogOffset)
            {
                ptzLogOffset = 0;
                ptzLogRemainder = string.Empty;
            }
            if (stream.Length == ptzLogOffset)
                return;
            stream.Position = ptzLogOffset;
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true);
            string appended = reader.ReadToEnd();
            ptzLogOffset = stream.Length;
            string text = ptzLogRemainder + appended;
            string[] lines = text.Split('\n');
            ptzLogRemainder = text.EndsWith('\n') ? string.Empty : lines[^1];
            int completeLines = text.EndsWith('\n') ? lines.Length : lines.Length - 1;
            for (int index = 0; index < completeLines; index++)
            {
                string json = lines[index].Trim();
                if (!json.StartsWith('{') || !json.Contains("\"Ret\":100", StringComparison.Ordinal))
                    continue;
                if (json.Contains("\"Name\":\"SystemFunction\"", StringComparison.Ordinal))
                    ptzSystemResponses.Enqueue(json);
                else if (json.Contains("\"Name\":\"Uart.PTZControlCmd\"", StringComparison.Ordinal))
                    ptzOrientationResponses.Enqueue(json);
                genericJsonResponses.Enqueue(json);
            }
        }
        catch
        {
            // O SDK pode girar o arquivo durante a leitura; a proxima tentativa continua.
        }
    }

    private List<(int DeviceId, int Command, string Json)> CollectPtzLogAssignments()
    {
        var assignments = new List<(int, int, string)>();
        lock (ptzLogLock)
        {
            ReadNewPtzResponsesLocked();
            while (pendingPtzSystemDevices.Count > 0 && ptzSystemResponses.Count > 0)
                assignments.Add((pendingPtzSystemDevices.Dequeue(), SystemFunctionCommand,
                    ptzSystemResponses.Dequeue()));
            while (pendingPtzOrientationDevices.Count > 0 && ptzOrientationResponses.Count > 0)
                assignments.Add((pendingPtzOrientationDevices.Dequeue(), PtzControlConfigCommand,
                    ptzOrientationResponses.Dequeue()));
        }
        return assignments;
    }

    private void ProcessPtzLogAssignments()
    {
        foreach ((int targetDeviceId, int command, string json) in CollectPtzLogAssignments())
            ApplyPtzConfigJson(targetDeviceId, command, json);
    }

    private async Task RetryPtzLogAssignmentsAsync()
    {
        for (int attempt = 0; attempt < 20 && !isClosing; attempt++)
        {
            await Task.Delay(75).ConfigureAwait(false);
            ProcessPtzLogAssignments();
            lock (ptzLogLock)
                if (pendingPtzSystemDevices.Count == 0 && pendingPtzOrientationDevices.Count == 0)
                    return;
        }
    }

    private void RequestPtzConfig(int targetDeviceId, int command, string request)
    {
        var key = (targetDeviceId, command);
        if (pendingPtzConfigBuffers.ContainsKey(key))
            return;
        IntPtr buffer = Marshal.AllocHGlobal(DeviceConfigBufferSize);
        byte[] empty = new byte[DeviceConfigBufferSize];
        Marshal.Copy(empty, 0, buffer, empty.Length);
        byte[] encoded = Encoding.UTF8.GetBytes(request);
        Marshal.Copy(encoded, 0, buffer, Math.Min(encoded.Length, DeviceConfigBufferSize - 1));
        if (!pendingPtzConfigBuffers.TryAdd(key, buffer))
        {
            Marshal.FreeHGlobal(buffer);
            return;
        }
        try
        {
            int result = CmsSdk.CMS_Client_GetDeviceConfig(
                targetDeviceId, -1, command, buffer, DeviceConfigBufferSize, -1);
            if (result < 0 && pendingPtzConfigBuffers.TryRemove(key, out IntPtr rejected))
                Marshal.FreeHGlobal(rejected);
        }
        catch
        {
            if (pendingPtzConfigBuffers.TryRemove(key, out IntPtr rejected))
                Marshal.FreeHGlobal(rejected);
        }
    }

    private static string ReadNativeJson(IntPtr pointer, int maximumBytes)
    {
        if (pointer == IntPtr.Zero)
            return string.Empty;
        try
        {
            int length = 0;
            int limit = Math.Clamp(maximumBytes, 1, DeviceConfigBufferSize);
            while (length < limit && Marshal.ReadByte(pointer, length) != 0)
                length++;
            if (length == 0)
                return string.Empty;
            byte[] bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return string.Empty; }
    }

    private static bool TryFindBoolean(JsonElement element, string propertyName, out bool value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName))
                {
                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        value = property.Value.GetBoolean();
                        return true;
                    }
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out int number))
                    {
                        value = number != 0;
                        return true;
                    }
                }
                if (TryFindBoolean(property.Value, propertyName, out value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                if (TryFindBoolean(item, propertyName, out value))
                    return true;
        }
        value = false;
        return false;
    }

    private static bool TryFindAnyBoolean(
        JsonElement element, IReadOnlyList<string> propertyNames,
        out bool value, out string matchedProperties)
    {
        bool found = false;
        bool anySupported = false;
        var matches = new List<string>();
        foreach (string propertyName in propertyNames)
        {
            if (!TryFindBoolean(element, propertyName, out bool supported))
                continue;
            found = true;
            anySupported |= supported;
            matches.Add(propertyName);
        }
        value = anySupported;
        matchedProperties = string.Join(", ", matches);
        return found;
    }

    private void HandlePtzConfigResponse(
        int targetDeviceId, int command, IntPtr text1, IntPtr text2, uint size)
    {
        var key = (targetDeviceId, command);
        pendingPtzConfigBuffers.TryRemove(key, out IntPtr requestBuffer);
        // text1/text2 não são ponteiros de texto estáveis nesta build do CMS.
        // O VMS fornece o buffer de entrada/saída; lemos somente essa memória nossa.
        string json = string.Empty;
        if (requestBuffer != IntPtr.Zero)
            Marshal.FreeHGlobal(requestBuffer);
        if (string.IsNullOrWhiteSpace(json))
        {
            lock (ptzLogLock)
            {
                if (command == SystemFunctionCommand)
                    pendingPtzSystemDevices.Enqueue(targetDeviceId);
                else
                    pendingPtzOrientationDevices.Enqueue(targetDeviceId);
            }
            ProcessPtzLogAssignments();
            _ = RetryPtzLogAssignmentsAsync();
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            PtzCapabilityState state = ptzCapabilities.GetOrAdd(targetDeviceId, _ => new PtzCapabilityState());
            lock (state)
            {
                if (command == SystemFunctionCommand &&
                    TryFindBoolean(document.RootElement, "SupportPTZDirectionControl", out bool supported))
                {
                    state.DeviceSupportsPtz = supported;
                }
                else if (command == PtzControlConfigCommand &&
                    document.RootElement.TryGetProperty("Uart.PTZControlCmd", out JsonElement channels) &&
                    channels.ValueKind == JsonValueKind.Array)
                {
                    state.Channels.Clear();
                    int channel = 0;
                    foreach (JsonElement item in channels.EnumerateArray())
                    {
                        TryFindBoolean(item, "MirrorOperation", out bool mirror);
                        TryFindBoolean(item, "FlipOperation", out bool flip);
                        state.Channels[channel++] = (mirror, flip);
                    }
                }
            }
            Dispatcher.BeginInvoke((Action)(() => ApplyPtzCapability(targetDeviceId)));
        }
        catch
        {
            // Nunca registra o JSON nativo: ele pode conter metadados do dispositivo.
        }
    }

    private void ApplyPtzConfigJson(int targetDeviceId, int command, string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            PtzCapabilityState state = ptzCapabilities.GetOrAdd(targetDeviceId, _ => new PtzCapabilityState());
            lock (state)
            {
                if (command == SystemFunctionCommand &&
                    TryFindBoolean(document.RootElement, "SupportPTZDirectionControl", out bool supported))
                {
                    state.DeviceSupportsPtz = supported;
                }
                else if (command == PtzControlConfigCommand &&
                    document.RootElement.TryGetProperty("Uart.PTZControlCmd", out JsonElement channels) &&
                    channels.ValueKind == JsonValueKind.Array)
                {
                    state.Channels.Clear();
                    int channel = 0;
                    foreach (JsonElement item in channels.EnumerateArray())
                    {
                        TryFindBoolean(item, "MirrorOperation", out bool mirror);
                        TryFindBoolean(item, "FlipOperation", out bool flip);
                        state.Channels[channel++] = (mirror, flip);
                    }
                }
            }
            if (command == SystemFunctionCommand)
                RecordSystemFunctionProfile(targetDeviceId, document.RootElement);
            Dispatcher.BeginInvoke((Action)(() => ApplyPtzCapability(targetDeviceId)));
        }
        catch
        {
            // Nao registra a resposta nativa: ela pode conter metadados do dispositivo.
        }
    }

    private void ApplyPtzCapability(int targetDeviceId)
    {
        if (!ptzCapabilities.TryGetValue(targetDeviceId, out PtzCapabilityState? state))
            return;
        bool? supported;
        Dictionary<int, (bool Mirror, bool Flip)> channels;
        lock (state)
        {
            supported = state.DeviceSupportsPtz;
            channels = new Dictionary<int, (bool Mirror, bool Flip)>(state.Channels);
        }
        if (supported is null)
            return;

        CloudApi.AccountDevice? device = accountDevices.FirstOrDefault(item =>
            item.CmsDeviceId == targetDeviceId || previewBindings.Values.Any(binding =>
                binding.DeviceId == targetDeviceId && string.Equals(binding.CloudId, item.CloudId, StringComparison.Ordinal)));
        if (device is null)
            return;
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
        bool orientationReported = channels.Count > 0;
        if (!orientationReported)
        {
            foreach (int channel in previewBindings.Values
                         .Where(binding => binding.DeviceId == targetDeviceId &&
                             confirmedPreviewWindows.ContainsKey(binding.Window))
                         .Select(binding => binding.Channel)
                         .Distinct())
            {
                entry.PtzMirrorChannels.TryGetValue(channel, out bool mirror);
                entry.PtzFlipChannels.TryGetValue(channel, out bool flip);
                channels[channel] = (mirror, flip);
            }
            lock (state)
                foreach ((int channel, var orientation) in channels)
                    state.Channels[channel] = orientation;
        }
        if (channels.Count == 0)
            return;
        entry.PtzSupportedChannels.Clear();
        entry.PtzMirrorChannels.Clear();
        entry.PtzFlipChannels.Clear();
        foreach ((int channel, var orientation) in channels)
        {
            entry.PtzSupportedChannels[channel] = supported == true;
            entry.PtzMirrorChannels[channel] = orientation.Mirror;
            entry.PtzFlipChannels[channel] = orientation.Flip;
        }
        SaveCameraCatalog();
        bool profileChanged = deviceProfiles.UpdateIdentity(
            device, entry, GetLocalOemId(device));
        profileChanged |= deviceProfiles.RecordCapability(
            device.CloudId, "PTZ.Direction", supported == true, "SystemFunction");
        foreach (int channel in channels.Keys)
            profileChanged |= deviceProfiles.RecordCapability(
                device.CloudId, $"PTZ.Channel.{channel + 1}", supported == true,
                orientationReported ? "Uart.PTZControlCmd" : "SystemFunction + preview confirmado");
        if (profileChanged)
            SaveDeviceProfiles();
        int supportedChannels = entry.PtzSupportedChannels.Count(item => item.Value);
        Log($"Capacidade PTZ confirmada: dispositivo {targetDeviceId}; canais suportados {supportedChannels}; " +
            (orientationReported ? "orientacao informada pelo dispositivo." : "orientacao local padrao preservada."));
        if (selectedPreviewWindow >= 0 && previewBindings.TryGetValue(selectedPreviewWindow, out PreviewBinding? selected))
            SelectPreviewWindow(selected.Window);
    }

    private void SendPtzOneShot(int command, int value, string description)
    {
        if (selectedPreviewWindow < 0 || !confirmedPreviewWindows.ContainsKey(selectedPreviewWindow) ||
            !previewBindings.TryGetValue(selectedPreviewWindow, out PreviewBinding? binding) ||
            !SupportsPtz(binding))
        {
            SelectedCameraText.Text = "PTZ indisponível neste canal";
            return;
        }
        if (!CanIssueDeviceRequest(binding.DeviceId, out TimeSpan wait, out _, out int lastError))
        {
            SelectedCameraText.Text = $"Comando suspenso: aguarde {Math.Ceiling(wait.TotalMinutes)} min";
            Log($"PTZ favorito suprimido: dispositivo {binding.DeviceId}; erro {lastError}; nenhuma requisição enviada.");
            return;
        }
        // VMS Pro keeps the configured PTZ speed in the third argument and
        // sends the preset number in the fourth argument for commands 17-19.
        int result = CmsSdk.CMS_Client_SendPTZCommand(selectedPreviewWindow, command, 5, value);
        SelectedCameraText.Text = result > 0
            ? $"Comando enviado: {description} {value}"
            : $"A câmera não confirmou: {description}";
        Log($"PTZ favorito: janela {selectedPreviewWindow}; comando {command}; posição {value}; retorno {result}.");
    }

    private void SendPtzCommand(int command, bool stop)
    {
        if (selectedPreviewWindow < 0 || !confirmedPreviewWindows.ContainsKey(selectedPreviewWindow) ||
            !previewBindings.TryGetValue(selectedPreviewWindow, out PreviewBinding? binding) ||
            !SupportsPtz(binding))
            return;
        // O comando de parada precisa sempre acompanhar um movimento já
        // iniciado; suprimi-lo poderia deixar o motor ativo até o timeout.
        if (!stop && !CanIssueDeviceRequest(binding.DeviceId, out _, out _, out _))
            return;
        int nativeCommand = stop ? command : MapPtzDirection(binding, command);
        int result = CmsSdk.CMS_Client_SendPTZCommand(selectedPreviewWindow, nativeCommand, 5, stop ? 1 : 0);
        if (!stop)
            activePtzCommand = nativeCommand;
        if (result <= 0)
            SelectedCameraText.Text = "Este canal não confirmou suporte ao comando PTZ";
        Log($"PTZ: janela {selectedPreviewWindow}; comando visual {command}; comando orientado {nativeCommand}; {(stop ? "parar" : "iniciar")}; retorno {result}.");
    }

    private void StopPtzCommand()
    {
        if (activePtzCommand < 0 || selectedPreviewWindow < 0)
            return;
        int command = activePtzCommand;
        activePtzCommand = -1;
        try { SendPtzCommand(command, stop: true); }
        catch { }
    }

    private CloudApi.AccountDevice? SelectedSmartDevice()
    {
        if (CamerasView.Visibility == Visibility.Visible &&
            DeviceBox.SelectedItem is CloudApi.AccountDevice selectedDevice)
            return selectedDevice;
        if (selectedPreviewWindow < 0 ||
            !previewBindings.TryGetValue(selectedPreviewWindow, out PreviewBinding? binding))
            return null;
        return accountDevices.FirstOrDefault(item =>
            string.Equals(item.CloudId, binding.CloudId, StringComparison.Ordinal));
    }

    private CameraCatalogStore.Entry? SelectedCatalogEntry()
    {
        CloudApi.AccountDevice? device = SelectedSmartDevice();
        return device is null ? null : cameraCatalog.GetOrCreate(device, int.MaxValue);
    }

    private void LoadSmartControls(CloudApi.AccountDevice device)
    {
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
        MotionTrackingBox.IsChecked = entry.MotionTracking;
        MotionSensitivitySlider.Value = Math.Clamp(entry.MotionSensitivity, 1, 6);
        foreach (ComboBoxItem item in TrackingTimeBox.Items)
            if (int.TryParse(item.Tag?.ToString(), out int seconds) && seconds == entry.TrackingSeconds)
                TrackingTimeBox.SelectedItem = item;
        foreach (ComboBoxItem item in TrackingPositionBox.Items)
            if (int.TryParse(item.Tag?.ToString(), out int preset) && preset == entry.TrackingPreset)
                TrackingPositionBox.SelectedItem = item;
        HumanDetectionBox.IsChecked = entry.HumanDetection;
        SmartAlertBox.IsChecked = entry.SmartAlert;
        TraceBox.IsChecked = entry.ShowTrace;
        AudibleWarningBox.IsChecked = entry.AudibleWarning;
        LightWarningBox.IsChecked = entry.LightWarning;
        TriggerMessageBox.Text = string.IsNullOrWhiteSpace(entry.TriggerMessage)
            ? "Movimento detectado"
            : entry.TriggerMessage;
        SmartControlStatusText.Text = "Configuração local carregada. O envio só é habilitado quando o modelo confirma suporte.";
    }

    private void LoadSmartControls(PreviewBinding binding)
    {
        CloudApi.AccountDevice? device = accountDevices.FirstOrDefault(item =>
            string.Equals(item.CloudId, binding.CloudId, StringComparison.Ordinal));
        if (device is not null)
            LoadSmartControls(device);
    }

    private void ApplySmartControls_Click(object sender, RoutedEventArgs e)
    {
        CameraCatalogStore.Entry? entry = SelectedCatalogEntry();
        CloudApi.AccountDevice? device = SelectedSmartDevice();
        if (entry is null || device is null)
        {
            SmartControlStatusText.Text = "Selecione uma câmera.";
            return;
        }
        entry.MotionTracking = MotionTrackingBox.IsChecked == true;
        entry.MotionSensitivity = (int)MotionSensitivitySlider.Value;
        entry.TrackingSeconds = TrackingTimeBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int seconds) ? seconds : 15;
        entry.TrackingPreset = TrackingPositionBox.SelectedItem is ComboBoxItem positionItem &&
            int.TryParse(positionItem.Tag?.ToString(), out int preset) ? preset : 0;
        entry.HumanDetection = HumanDetectionBox.IsChecked == true;
        entry.SmartAlert = SmartAlertBox.IsChecked == true;
        entry.ShowTrace = TraceBox.IsChecked == true;
        entry.AudibleWarning = AudibleWarningBox.IsChecked == true;
        entry.LightWarning = LightWarningBox.IsChecked == true;
        entry.TriggerMessage = TriggerMessageBox.Text.Trim();
        SaveCameraCatalog();

        // As estruturas binárias desses recursos variam por firmware. O CMS
        // empacotado não publica uma ABI segura para consultá-las. Guardamos a
        // intenção por câmera, mas jamais exibimos um falso "aplicado".
        SmartControlStatusText.Text =
            "Preferências salvas neste computador. Este firmware ainda não confirmou a interface de configuração remota.";
        CameraSaveStatusText.Text = "Preferências inteligentes salvas localmente.";
        if (CamerasView.Visibility != Visibility.Visible)
            SelectedCameraText.Text = $"{device.Alias}: recursos inteligentes salvos; envio não confirmado pelo firmware";
        Log($"Controles inteligentes salvos localmente para {device.Alias}; envio remoto indisponível na ABI atual.");
    }

    private void OpenSeparateWindow_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPreviewWindow < 0 ||
            !confirmedPreviewWindows.ContainsKey(selectedPreviewWindow) ||
            !previewBindings.TryGetValue(selectedPreviewWindow, out PreviewBinding? binding))
            return;

        int nativeWindow = Interlocked.Increment(ref nextFloatingWindow);
        var hostPanel = new Forms.Panel { BackColor = System.Drawing.Color.Black, Dock = Forms.DockStyle.Fill };
        var host = new System.Windows.Forms.Integration.WindowsFormsHost { Child = hostPanel };
        var floatingLayout = new Grid();
        floatingLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        floatingLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        floatingLayout.Children.Add(host);
        var floatingBar = new DockPanel
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 26, 41)),
            LastChildFill = false
        };
        Grid.SetRow(floatingBar, 1);
        var alwaysOnTop = new System.Windows.Controls.CheckBox
        {
            Content = "Sempre visível",
            Foreground = System.Windows.Media.Brushes.White,
            Margin = new Thickness(12, 8, 12, 8)
        };
        floatingBar.Children.Add(alwaysOnTop);
        floatingLayout.Children.Add(floatingBar);
        var floating = new Window
        {
            Owner = this,
            Title = $"{binding.DisplayName} — Canal {binding.Channel + 1}",
            Width = 800,
            Height = 500,
            MinWidth = 400,
            MinHeight = 260,
            Background = System.Windows.Media.Brushes.Black,
            Content = floatingLayout,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        alwaysOnTop.Checked += (_, _) => floating.Topmost = true;
        alwaysOnTop.Unchecked += (_, _) => floating.Topmost = false;
        hostPanel.DoubleClick += (_, _) =>
        {
            floating.WindowStyle = floating.WindowStyle == WindowStyle.None
                ? WindowStyle.SingleBorderWindow
                : WindowStyle.None;
            floating.WindowState = floating.WindowStyle == WindowStyle.None
                ? WindowState.Maximized
                : WindowState.Normal;
        };
        floating.Loaded += (_, _) =>
        {
            try
            {
                if (!CanIssueDeviceRequest(binding.DeviceId, out _, out _, out _))
                {
                    floating.Title += " — bloqueada temporariamente";
                    Log($"Janela separada suprimida: {DeviceBlockStatus(binding.DeviceId)}.");
                    return;
                }
                int create = CmsSdk.CMS_Client_CreatePlayWindow(
                    nativeWindow, unchecked((int)hostPanel.Handle.ToInt64()), 0);
                int preview = CmsSdk.CMS_Client_StartPreview(
                    binding.DeviceId, nativeWindow, binding.Channel, binding.StreamType, true);
                if (preview > 0)
                {
                    floatingPreviewWindows.Add(nativeWindow);
                    Log($"Janela separada aberta: dispositivo {binding.DeviceId}; canal {binding.Channel}; retorno {create}/{preview}.");
                }
                else
                {
                    floating.Title += " — não foi possível abrir";
                    Log($"Janela separada recusada: retorno {create}/{preview}.");
                }
            }
            catch (Exception ex)
            {
                floating.Title += " — indisponível";
                Log("Falha na janela separada: " + SanitizeDiagnostic(ex.Message));
            }
        };
        floating.Closed += (_, _) =>
        {
            try { CmsSdk.CMS_Client_StopPreviewByWnd(nativeWindow, 0); } catch { }
            floatingPreviewWindows.Remove(nativeWindow);
        };
        floating.Show();
    }

    private void ToggleAudio_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPreviewWindow < 0 ||
            !confirmedPreviewWindows.ContainsKey(selectedPreviewWindow))
        {
            AudioButton.IsEnabled = false;
            SelectedCameraText.Text = "Selecione uma câmera online para ouvir";
            return;
        }

        int window = selectedPreviewWindow;
        int previousSoundingWindow = soundingPreviewWindow;
        int previousDisplayWindow = audioDisplayPreviewWindow;
        try
        {
            bool sounding = audioDisplayPreviewWindow == window &&
                soundingPreviewWindow >= 0 &&
                CmsSdk.CMS_Client_isSounding(soundingPreviewWindow);
            if (sounding)
            {
                int closeResult = CmsSdk.CMS_Client_CloseSound(soundingPreviewWindow);
                if (closeResult > 0)
                {
                    soundingPreviewWindow = -1;
                    audioDisplayPreviewWindow = -1;
                }
                Log($"Áudio fechado: janela {window}; retorno {closeResult}.");
            }
            else
            {
                if (soundingPreviewWindow >= 0 && soundingPreviewWindow != window)
                {
                    try { CmsSdk.CMS_Client_CloseSound(soundingPreviewWindow); }
                    catch { }
                }

                int sourceWindow = window;
                if (previewBindings.TryGetValue(window, out PreviewBinding? selected) &&
                    selected.Channel > 0)
                {
                    PreviewBinding? channelOne = previewBindings.Values.FirstOrDefault(candidate =>
                        candidate.DeviceId == selected.DeviceId &&
                        candidate.Channel == 0 &&
                        confirmedPreviewWindows.ContainsKey(candidate.Window));
                    if (channelOne is not null)
                        sourceWindow = channelOne.Window;
                }

                int openResult = CmsSdk.CMS_Client_OpenSound(sourceWindow);
                soundingPreviewWindow = openResult > 0 ? sourceWindow : -1;
                audioDisplayPreviewWindow = openResult > 0 ? window : -1;
                Log($"Áudio aberto: quadro {window}; fonte {sourceWindow}; retorno {openResult}.");
                if (openResult <= 0)
                    SelectedCameraText.Text = "Não foi possível abrir o áudio desta câmera";
            }

            UpdateAudioButton(window);
            RefreshPreviewHeader(previousSoundingWindow);
            RefreshPreviewHeader(previousDisplayWindow);
            RefreshPreviewHeader(window);
        }
        catch (Exception ex)
        {
            soundingPreviewWindow = -1;
            audioDisplayPreviewWindow = -1;
            AudioButton.Content = "🔊  Áudio";
            SelectedCameraText.Text = "O motor de áudio não respondeu";
            Log($"Falha ao alternar áudio da janela {window}: {SanitizeDiagnostic(ex.Message)}");
        }
    }

    private void UpdateAudioButton(int window)
    {
        bool sounding = false;
        if (confirmedPreviewWindows.ContainsKey(window) && audioDisplayPreviewWindow == window &&
            soundingPreviewWindow >= 0)
        {
            try { sounding = CmsSdk.CMS_Client_isSounding(soundingPreviewWindow); }
            catch { }
        }

        if (!sounding && audioDisplayPreviewWindow == window)
        {
            soundingPreviewWindow = -1;
            audioDisplayPreviewWindow = -1;
        }

        AudioButton.Content = sounding ? "🔇  Silenciar" : "🔊  Áudio";
    }

    private async void ToggleRecord_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPreviewWindow < 0 ||
            !confirmedPreviewWindows.ContainsKey(selectedPreviewWindow))
        {
            RecordButton.IsEnabled = false;
            SelectedCameraText.Text = "Selecione uma câmera online para gravar";
            return;
        }

        int window = selectedPreviewWindow;
        try
        {
            bool recording = recordingPreviewWindows.Contains(window) ||
                CmsSdk.CMS_Client_isRecording(window);
            string recordingFolder = preferences.GetRecordingFolder();
            Directory.CreateDirectory(recordingFolder);
            int settingsResult = XMEyeBridge.ConfigureRecording(recordingFolder);
            CmsSdk.RecordPlanUnit plan = CmsSdk.RecordPlanUnit.Create(window, !recording);
            int getResult = CmsSdk.CMS_Client_GetRecordPlan(window, ref plan);
            plan.Window = window;
            plan.Enabled = recording ? (byte)0 : (byte)1;
            int setResult = CmsSdk.CMS_Client_SetRecordPlan(ref plan);
            bool requestedRecording = !recording && setResult > 0;
            if (requestedRecording)
                recordingPreviewWindows.Add(window);
            else if (recording && setResult > 0)
                recordingPreviewWindows.Remove(window);

            UpdateRecordButton(window, trustRequestedState: true);
            RefreshPreviewHeader(window);
            Log($"Gravação {(requestedRecording ? "solicitada" : "encerrada")}: janela {window}; configuração {settingsResult}; consulta {getResult}; retorno {setResult}; pasta {SanitizeDiagnostic(recordingFolder)}.");

            if (requestedRecording)
            {
                bool confirmed = false;
                for (int attempt = 0; attempt < 25; attempt++)
                {
                    await Task.Delay(200);
                    try { confirmed = CmsSdk.CMS_Client_isRecording(window); }
                    catch { }
                    if (confirmed)
                        break;
                }

                if (confirmed)
                    Log($"Gravação confirmada pelo CMS: janela {window}.");
                else
                {
                    recordingPreviewWindows.Remove(window);
                    SelectedCameraText.Text = "O CMS aceitou o comando, mas não iniciou o arquivo";
                    Log($"FALHA AO CONFIRMAR GRAVAÇÃO: janela {window}; pasta configurada {SanitizeDiagnostic(recordingFolder)}.");
                }
                UpdateRecordButton(window, trustRequestedState: true);
                RefreshPreviewHeader(window);
            }
            else if (!recording && setResult <= 0)
                SelectedCameraText.Text = "Não foi possível iniciar a gravação desta câmera";
            else if (recording && setResult > 0)
            {
                FileInfo? saved = FindNewestLocalRecording();
                SelectedCameraText.Text = saved is null
                    ? "Gravação local encerrada"
                    : $"Gravação salva: {saved.Name}";
                RefreshCaptureList();
            }
        }
        catch (Exception ex)
        {
            recordingPreviewWindows.Remove(window);
            RecordButton.Content = "⏺  Gravar";
            SelectedCameraText.Text = "O motor de gravação não respondeu";
            Log($"Falha ao alternar gravação da janela {window}: {SanitizeDiagnostic(ex.Message)}");
        }
    }

    private void UpdateRecordButton(int window, bool trustRequestedState = false)
    {
        bool recording = recordingPreviewWindows.Contains(window);
        bool talking = previewBindings.TryGetValue(window, out PreviewBinding? binding) &&
            binding.DeviceId == talkingDeviceId && binding.Channel == talkingChannel;
        if (!trustRequestedState && confirmedPreviewWindows.ContainsKey(window))
        {
            try { recording = CmsSdk.CMS_Client_isRecording(window); }
            catch { }
        }
        if (recording)
            recordingPreviewWindows.Add(window);
        else
            recordingPreviewWindows.Remove(window);
        RecordButton.Content = recording ? "■  Parar gravação" : "⏺  Gravar";
    }

    private void RefreshPreviewHeader(int window)
    {
        if (window < 0 || !previewBindings.TryGetValue(window, out PreviewBinding? binding))
            return;
        if (confirmedPreviewWindows.ContainsKey(window))
            SetPreviewStatus(binding, "Online", System.Drawing.Color.LimeGreen);
        else if (recoveringPreviewWindows.ContainsKey(window))
            SetPreviewStatus(binding, "Reconectando", System.Drawing.Color.Orange);
        else
            SetPreviewStatus(binding, "Offline", System.Drawing.Color.IndianRed);
    }

    private void RefreshPreviewBadge(int window)
    {
        if (window < 0 || window >= videoBadges.Count)
            return;

        bool audio = window == audioDisplayPreviewWindow;
        bool recording = recordingPreviewWindows.Contains(window);
        bool talking = previewBindings.TryGetValue(window, out PreviewBinding? talkBinding) &&
            talkBinding.DeviceId == talkingDeviceId && talkBinding.Channel == talkingChannel;
        bool borrowedChannelOne = audio && soundingPreviewWindow != audioDisplayPreviewWindow;
        Forms.Label badge = videoBadges[window];
        var icons = new List<string>(3);
        if (audio) icons.Add("🔊");
        if (talking) icons.Add("🎙");
        if (recording) icons.Add("●");
        badge.Text = string.Join(" ", icons);
        badge.Width = Math.Max(38, 14 + icons.Count * 25);
        badge.Font = new System.Drawing.Font(
            audio ? "Segoe UI Emoji" : "Segoe UI",
            10F,
            System.Drawing.FontStyle.Bold);
        badge.AccessibleDescription = borrowedChannelOne
            ? "Áudio recebido do canal 1"
            : audio ? "Áudio ativo" : recording ? "Gravação local ativa" : string.Empty;
        badge.Left = Math.Max(2, videoContainers[window].ClientSize.Width - badge.Width - 4);
        badge.ForeColor = recording
            ? System.Drawing.Color.FromArgb(255, 92, 92)
            : System.Drawing.Color.FromArgb(92, 174, 255);
        badge.Visible = audio || talking || recording;
        if (badge.Visible)
            badge.BringToFront();
    }

    private void CaptureSelected_Click(object sender, RoutedEventArgs e)
    {
        if (selectedPreviewWindow < 0 ||
            !previewBindings.TryGetValue(selectedPreviewWindow, out PreviewBinding? binding) ||
            !confirmedPreviewWindows.ContainsKey(selectedPreviewWindow) ||
            selectedPreviewWindow >= videoPanels.Count)
        {
            SelectedCameraText.Text = "Selecione uma câmera online para capturar";
            CaptureButton.IsEnabled = false;
            return;
        }

        try
        {
            Forms.Panel panel = videoPanels[selectedPreviewWindow];
            if (panel.Width <= 1 || panel.Height <= 1)
                throw new InvalidOperationException("A área de vídeo ainda não está pronta.");

            string folder = preferences.GetCaptureFolder();
            Directory.CreateDirectory(folder);
            string safeName = Regex.Replace(binding.DisplayName, "[^A-Za-z0-9À-ÿ_-]+", "-").Trim('-');
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "camera";
            string fileName = $"{safeName}-canal-{binding.Channel + 1}-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            string destination = Path.Combine(folder, fileName);

            System.Drawing.Point origin = panel.PointToScreen(System.Drawing.Point.Empty);
            using var image = new System.Drawing.Bitmap(
                panel.ClientSize.Width,
                panel.ClientSize.Height,
                System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(image))
            {
                graphics.CopyFromScreen(
                    origin.X, origin.Y, 0, 0, panel.ClientSize,
                    System.Drawing.CopyPixelOperation.SourceCopy);
            }
            image.Save(destination, System.Drawing.Imaging.ImageFormat.Png);
            SelectedCameraText.Text = $"Foto salva: {fileName}";
            Log($"Captura local salva: câmera {binding.DisplayName}; canal {binding.Channel + 1}; arquivo {fileName}.");
            RefreshCaptureList();
        }
        catch (Exception ex)
        {
            SelectedCameraText.Text = "Não foi possível salvar a foto";
            Log($"Falha na captura local: {ex.Message}");
        }
    }

    private void ChooseCaptureFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Escolha a pasta onde as fotos das câmeras serão salvas",
            SelectedPath = preferences.GetCaptureFolder(),
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return;

        preferences.CaptureFolder = dialog.SelectedPath;
        CaptureFolderBox.Text = dialog.SelectedPath;
        SavePreferences();
        RefreshCaptureList();
    }

    private void ChooseRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Escolha a pasta onde as gravações serão salvas",
            SelectedPath = preferences.GetRecordingFolder(),
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return;

        preferences.RecordingFolder = dialog.SelectedPath;
        RecordingFolderBox.Text = dialog.SelectedPath;
        SavePreferences();
        SelectedCameraText.Text = "A nova pasta de gravações será usada ao reiniciar";
    }

    private void RefreshCaptures_Click(object sender, RoutedEventArgs e) => RefreshCaptureList();

    private void MediaFilter_Click(object sender, RoutedEventArgs e)
    {
        showRecordingMedia = (sender as FrameworkElement)?.Tag?.ToString() != "Images";
        var active = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(22, 119, 255));
        var inactive = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(19, 39, 61));
        VideosFilterButton.Background = showRecordingMedia ? active : inactive;
        ImagesFilterButton.Background = showRecordingMedia ? inactive : active;
        if (!showRecordingMedia && recordingPlayer.IsOpen)
            StopLocalRecordingPlayer();
        RecordingPlayerTitleText.Text = showRecordingMedia ? "Selecione uma gravação" : "Imagens capturadas";
        RecordingPlayerStatusText.Text = showRecordingMedia
            ? "Clique duas vezes numa miniatura para reproduzir."
            : "Clique duas vezes numa imagem para abri-la em tamanho completo.";
        RefreshCaptureList();
    }

    private void RefreshCaptureList()
    {
        try
        {
            Directory.CreateDirectory(preferences.GetCaptureFolder());
            Directory.CreateDirectory(preferences.GetRecordingFolder());
            FileInfo[] allFiles = EnumerateLocalMediaFiles()
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(200)
                .ToArray();
            FileInfo[] files = allFiles
                .Where(file => showRecordingMedia != IsPhotoExtension(file.Extension))
                .ToArray();
            LocalMediaItem[] items = files.Select(CreateLocalMediaItem).ToArray();
            CaptureListBox.ItemsSource = items;
            CaptureListStatusText.Text = files.Length switch
            {
                0 when showRecordingMedia => "Nenhum vídeo encontrado.",
                0 => "Nenhuma imagem encontrada.",
                _ when showRecordingMedia => $"{files.Length} vídeo(s) local(is)",
                _ => $"{files.Length} imagem(ns) local(is)"
            };
            DeleteMediaButton.IsEnabled = false;
            recordingThumbnailCts?.Cancel();
            recordingThumbnailCts?.Dispose();
            recordingThumbnailCts = new CancellationTokenSource();
            _ = GenerateRecordingThumbnailsAsync(items, recordingThumbnailCts.Token);
        }
        catch (Exception ex)
        {
            CaptureListBox.ItemsSource = null;
            CaptureListStatusText.Text = "Não foi possível ler as pastas locais.";
            Log($"Falha ao listar fotos e gravações locais: {ex.Message}");
        }
    }

    private IEnumerable<FileInfo> EnumerateLocalMediaFiles()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string[] roots =
        [
            preferences.GetCaptureFolder(),
            preferences.GetRecordingFolder(),
            Path.Combine(videos, "iCSee-XMEyeRecords")
        ];
        string[] extensions = [".png", ".jpg", ".jpeg", ".h264", ".h265", ".mp4", ".avi"];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
                continue;
            IEnumerable<string> paths;
            try { paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToArray(); }
            catch { continue; }
            foreach (string path in paths)
            {
                if (extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
                    seen.Add(path))
                    yield return new FileInfo(path);
            }
        }
    }

    private static bool IsPhotoExtension(string extension) =>
        extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    private FileInfo? FindNewestLocalRecording() => EnumerateLocalMediaFiles()
        .Where(file => !IsPhotoExtension(file.Extension))
        .OrderByDescending(file => file.LastWriteTimeUtc)
        .FirstOrDefault();

    private LocalMediaItem CreateLocalMediaItem(FileInfo file)
    {
        bool recording = !IsPhotoExtension(file.Extension);
        string title = file.Name;
        if (recording)
        {
            Match folder = Regex.Match(file.Directory?.Name ?? string.Empty,
                "^Ch(?<channel>\\d+)_(?<name>.+)_[^_]+_\\d+$",
                RegexOptions.CultureInvariant);
            title = folder.Success
                ? $"{folder.Groups["name"].Value.Replace('+', ' ')} — Canal {folder.Groups["channel"].Value}"
                : "Gravação local";
        }

        DateTime timestamp = file.LastWriteTime;
        if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file.Name),
            "yyyyMMddHHmmssfff", null,
            System.Globalization.DateTimeStyles.None, out DateTime parsed))
            timestamp = parsed;
        int duration = recording ? H264FilePlayer.ReadDurationSeconds(file.FullName) : 0;
        string subtitle = recording
            ? $"{timestamp:dd/MM/yyyy HH:mm:ss}  •  {FormatMediaTime(duration)}"
            : $"Foto  •  {timestamp:dd/MM/yyyy HH:mm:ss}";
        return new LocalMediaItem
        {
            File = file,
            Title = title,
            Subtitle = subtitle,
            IsRecording = recording,
            ThumbnailPath = recording ? GetCachedThumbnailPath(file) : file.FullName
        };
    }

    private static string? GetCachedThumbnailPath(FileInfo file)
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XMEyeCloudAccountTester", "thumbnails");
        string keySource = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(keySource)));
        string path = Path.Combine(cache, key + ".jpg");
        return File.Exists(path) ? path : null;
    }

    private async Task GenerateRecordingThumbnailsAsync(
        IEnumerable<LocalMediaItem> items, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(true);
            foreach (LocalMediaItem item in items.Where(candidate =>
                         candidate.IsRecording && string.IsNullOrWhiteSpace(candidate.ThumbnailPath)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (playingLocalMedia is not null ||
                    recordingPlaybackPanel.Width < 20 || recordingPlaybackPanel.Height < 20)
                    return;
                if (!recordingPlayer.Open(item.File.FullName, recordingPlaybackPanel.Handle, playSound: false))
                    continue;
                try
                {
                    await Task.Delay(700, cancellationToken).ConfigureAwait(true);
                    string cache = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "XMEyeCloudAccountTester", "thumbnails");
                    Directory.CreateDirectory(cache);
                    string keySource = $"{item.File.FullName}|{item.File.Length}|{item.File.LastWriteTimeUtc.Ticks}";
                    string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        Encoding.UTF8.GetBytes(keySource)));
                    string destination = Path.Combine(cache, key + ".jpg");
                    System.Drawing.Point origin = recordingPlaybackPanel.PointToScreen(System.Drawing.Point.Empty);
                    using var bitmap = new System.Drawing.Bitmap(
                        recordingPlaybackPanel.ClientSize.Width,
                        recordingPlaybackPanel.ClientSize.Height,
                        System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(origin, System.Drawing.Point.Empty,
                            recordingPlaybackPanel.ClientSize,
                            System.Drawing.CopyPixelOperation.SourceCopy);
                    bitmap.Save(destination, System.Drawing.Imaging.ImageFormat.Jpeg);
                    item.ThumbnailPath = destination;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"Miniatura local não gerada: {ex.Message}");
                }
                finally
                {
                    recordingPlayer.Close();
                    recordingPlaybackPanel.Invalidate();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log("Falha ao gerar miniaturas: " + SanitizeDiagnostic(ex.Message)); }
    }

    private void OpenCaptureFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = CaptureListBox.SelectedItem is LocalMediaItem selected
                ? selected.File.DirectoryName ?? preferences.GetCaptureFolder()
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    "iCSee-XMEyeRecords");
            if (!Directory.Exists(folder))
                folder = preferences.GetCaptureFolder();
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CaptureListStatusText.Text = "Não foi possível abrir a pasta.";
            Log($"Falha ao abrir a pasta de capturas: {ex.Message}");
        }
    }

    private void OpenSelectedCapture_Click(object sender, RoutedEventArgs e) => OpenSelectedCapture();

    private void CaptureListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        DeleteMediaButton.IsEnabled = CaptureListBox.SelectedItems.Count > 0;

    private void DeleteSelectedMedia_Click(object sender, RoutedEventArgs e)
    {
        LocalMediaItem[] items = CaptureListBox.SelectedItems
            .OfType<LocalMediaItem>()
            .Where(item => item.File.Exists)
            .ToArray();
        if (items.Length == 0)
            return;

        string description = items.Length == 1
            ? $"'{items[0].Title}'"
            : $"{items.Length} arquivos selecionados";
        MessageBoxResult answer = System.Windows.MessageBox.Show(
            $"Mover {description} para a Lixeira?",
            items.Length == 1 ? "Excluir arquivo local" : "Excluir arquivos locais",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            if (playingLocalMedia is not null && items.Any(item =>
                    playingLocalMedia.File.FullName.Equals(item.File.FullName,
                        StringComparison.OrdinalIgnoreCase)))
                StopLocalRecordingPlayer();
            int removed = 0;
            foreach (LocalMediaItem item in items)
            {
                string? thumbnail = item.ThumbnailPath;
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    item.File.FullName,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                if (item.IsRecording && !string.IsNullOrWhiteSpace(thumbnail) && File.Exists(thumbnail))
                    File.Delete(thumbnail);
                removed++;
                Log($"Arquivo local movido para a Lixeira: {item.File.Name}.");
            }
            CaptureListStatusText.Text = removed == 1
                ? "Arquivo movido para a Lixeira."
                : $"{removed} arquivos movidos para a Lixeira.";
            RefreshCaptureList();
        }
        catch (Exception ex)
        {
            CaptureListStatusText.Text = "Não foi possível mover o arquivo para a Lixeira.";
            Log($"Falha ao excluir arquivo local: {ex.Message}");
        }
    }

    private void CaptureListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        OpenSelectedCapture();

    private void OpenSelectedCapture()
    {
        if (CaptureListBox.SelectedItem is not LocalMediaItem item || !item.File.Exists)
            return;
        if (item.IsRecording)
        {
            PlayLocalRecording(item);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(item.File.FullName) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CaptureListStatusText.Text = "Não foi possível abrir o arquivo selecionado.";
            Log($"Falha ao abrir arquivo local: {ex.Message}");
        }
    }

    private void PlayLocalRecording(LocalMediaItem item)
    {
        recordingThumbnailCts?.Cancel();
        recordingPlayer.Close();
        playingLocalMedia = null;
        if (!recordingPlayer.Open(item.File.FullName, recordingPlaybackPanel.Handle, playSound: true))
        {
            RecordingPlayerStatusText.Text = "Não foi possível abrir esta gravação.";
            Log($"H264Play recusou a gravação local {item.File.Name}.");
            return;
        }
        playingLocalMedia = item;
        RecordingPlayerTitleText.Text = item.Title;
        RecordingPlayerStatusText.Text = item.Subtitle;
        RecordingPlayPauseButton.Content = "⏸ Pausar";
        RecordingSoundButton.Content = recordingPlayer.IsSounding ? "🔊 Áudio" : "🔇 Sem áudio";
        RecordingTimeline.Maximum = Math.Max(1, recordingPlayer.DurationSeconds);
        recordingPlaybackTimer.Start();
        Log($"Reprodução local iniciada: {item.Title}; duração {recordingPlayer.DurationSeconds}s.");
    }

    private void RecordingPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!recordingPlayer.IsOpen) { OpenSelectedCapture(); return; }
        bool paused = recordingPlayer.TogglePause();
        RecordingPlayPauseButton.Content = paused ? "▶ Continuar" : "⏸ Pausar";
    }

    private void RecordingSound_Click(object sender, RoutedEventArgs e)
    {
        if (!recordingPlayer.IsOpen) return;
        bool enabled = !recordingPlayer.IsSounding;
        recordingPlayer.SetSound(enabled);
        RecordingSoundButton.Content = recordingPlayer.IsSounding ? "🔊 Áudio" : "🔇 Sem áudio";
    }

    private void RecordingStop_Click(object sender, RoutedEventArgs e) => StopLocalRecordingPlayer();

    private void StopLocalRecordingPlayer()
    {
        recordingPlaybackTimer.Stop();
        recordingPlayer.Close();
        playingLocalMedia = null;
        RecordingPlayPauseButton.Content = "▶ Reproduzir";
        RecordingSoundButton.Content = "🔇 Áudio";
        RecordingPlayerTitleText.Text = "Selecione uma gravação";
        RecordingPlayerStatusText.Text = "Clique duas vezes numa miniatura para reproduzir.";
        RecordingTimeline.Value = 0;
        RecordingPositionText.Text = "00:00 / 00:00";
        recordingPlaybackPanel.Invalidate();
    }

    private void RecordingPlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (!recordingPlayer.IsOpen) return;
        int position = recordingPlayer.PositionSeconds;
        int duration = recordingPlayer.DurationSeconds;
        recordingTimelineUpdating = true;
        RecordingTimeline.Maximum = Math.Max(1, duration);
        RecordingTimeline.Value = Math.Min(RecordingTimeline.Maximum, Math.Max(0, position));
        recordingTimelineUpdating = false;
        RecordingPositionText.Text = $"{FormatMediaTime(position)} / {FormatMediaTime(duration)}";
    }

    private void RecordingTimeline_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!recordingTimelineUpdating && recordingPlayer.IsOpen && RecordingTimeline.Maximum > 0)
            recordingPlayer.Seek(RecordingTimeline.Value / RecordingTimeline.Maximum);
    }

    private static string FormatMediaTime(int seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");

    private void PreviewDragMouseDown(int window, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left || !previewBindings.ContainsKey(window))
            return;
        previewDragWindow = window;
        previewDragStart = e.Location;
    }

    private void PreviewDragMouseMove(int window, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left || previewDragWindow != window)
            return;
        if (Math.Abs(e.X - previewDragStart.X) < Forms.SystemInformation.DragSize.Width / 2 &&
            Math.Abs(e.Y - previewDragStart.Y) < Forms.SystemInformation.DragSize.Height / 2)
            return;

        previewDragWindow = -1;
        videoLabels[window].DoDragDrop(window, Forms.DragDropEffects.Move);
    }

    private static void PreviewPanelDragEnter(object? sender, Forms.DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(typeof(int)) == true
            ? Forms.DragDropEffects.Move
            : Forms.DragDropEffects.None;
    }

    private void PreviewDragDrop(int targetWindow, Forms.DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(int)) is not int sourceWindow ||
            sourceWindow == targetWindow ||
            !previewBindings.ContainsKey(sourceWindow) ||
            targetWindow < 0 || targetWindow >= videoContainers.Count)
            return;

        Forms.TableLayoutPanelCellPosition sourcePosition =
            videoGrid.GetCellPosition(videoContainers[sourceWindow]);
        Forms.TableLayoutPanelCellPosition targetPosition =
            videoGrid.GetCellPosition(videoContainers[targetWindow]);
        videoGrid.SuspendLayout();
        videoGrid.SetCellPosition(videoContainers[sourceWindow], targetPosition);
        videoGrid.SetCellPosition(videoContainers[targetWindow], sourcePosition);
        videoGrid.ResumeLayout(performLayout: true);
        SaveLiveLayoutOrder();
        SelectPreviewWindow(sourceWindow);
        Log("Ordem da grade alterada e salva.");
    }

    private void SaveLiveLayoutOrder()
    {
        IEnumerable<string> visibleOrder = previewBindings.Values
            .OrderBy(binding =>
            {
                Forms.TableLayoutPanelCellPosition position =
                    videoGrid.GetCellPosition(videoContainers[binding.Window]);
                return position.Row * Math.Max(1, videoGrid.ColumnCount) + position.Column;
            })
            .Select(binding => PreviewOrderKey(binding.CloudId, binding.Channel));
        preferences.LiveLayoutOrder = visibleOrder
            .Concat(preferences.LiveLayoutOrder)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        SavePreferences();
    }

    private void TogglePreviewFocus(int window)
    {
        if (!previewBindings.ContainsKey(window))
            return;

        SelectPreviewWindow(window);
        if (focusedPreviewWindow == window)
        {
            RestoreConfiguredVideoGrid();
            ScheduleLayoutRestore();
            SelectPreviewWindow(window);
            return;
        }

        videoGrid.SuspendLayout();
        for (int index = 0; index < videoContainers.Count; index++)
            videoContainers[index].Visible = index == window;
        videoGrid.ColumnStyles.Clear();
        videoGrid.RowStyles.Clear();
        videoGrid.ColumnCount = 1;
        videoGrid.RowCount = 1;
        videoGrid.ColumnStyles.Add(new Forms.ColumnStyle(Forms.SizeType.Percent, 100F));
        videoGrid.RowStyles.Add(new Forms.RowStyle(Forms.SizeType.Percent, 100F));
        videoGrid.SetColumnSpan(videoContainers[window], 1);
        videoGrid.SetColumn(videoContainers[window], 0);
        videoGrid.SetRow(videoContainers[window], 0);
        videoGrid.ResumeLayout(performLayout: true);
        focusedPreviewWindow = window;
        videoLabels[window].BringToFront();
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
        string environment = string.IsNullOrWhiteSpace(device.LocalGroup)
            ? string.Empty
            : $"  ·  {device.LocalGroup}";
        Forms.Label label = videoLabels[window];
        bool useSd = cameraCatalog.GetOrCreate(device, int.MaxValue).PreferredSd
            ?? (SubstreamBox.IsChecked == true);
        string quality = useSd ? "SD" : "HD";
        string transport = device.IsNetworkDevice ? "LAN" : "P2P";
        label.Text = $"● {name}{environment} — Canal {channel + 1}  |  Conectando  |  {quality}  |  {transport}";
        label.ForeColor = System.Drawing.Color.Gold;
        label.Visible = true;
        label.BringToFront();
        SetVideoLoadingState(window, "Carregando...");
    }

    private void SetVideoLoadingState(int window, string? message)
    {
        if (window < 0 || window >= videoLoadingLabels.Count)
            return;
        Forms.Label loading = videoLoadingLabels[window];
        loading.Text = message ?? string.Empty;
        loading.Visible = !string.IsNullOrWhiteSpace(message);
        if (loading.Visible)
            loading.BringToFront();
    }

    private void SetPreviewStatus(PreviewBinding binding, string status, System.Drawing.Color color)
    {
        if (binding.Window < 0 || binding.Window >= videoLabels.Count)
            return;
        if (status.StartsWith("Online", StringComparison.OrdinalIgnoreCase))
            ApplySavedMirror(binding);
        string quality = binding.StreamType == CmsSdk.StreamType.Extra ? "SD" : "HD";
        string transport = System.Net.IPAddress.TryParse(binding.CloudId, out _) ? "LAN" : "P2P";
        string environment = accountDevices.FirstOrDefault(device =>
            string.Equals(device.CloudId, binding.CloudId, StringComparison.Ordinal))?.LocalGroup ?? string.Empty;
        string environmentText = string.IsNullOrWhiteSpace(environment) ? string.Empty : $"  ·  {environment}";
        Forms.Label label = videoLabels[binding.Window];
        label.Text = $"● {binding.DisplayName}{environmentText} — Canal {binding.Channel + 1}  |  {status}  |  {quality}  |  {transport}";
        label.ForeColor = color;
        label.Visible = true;
        label.BringToFront();
        SetVideoLoadingState(
            binding.Window,
            status.StartsWith("Online", StringComparison.OrdinalIgnoreCase)
                ? null
                : status.StartsWith("Reconectando", StringComparison.OrdinalIgnoreCase)
                    ? "Reconectando..."
                    : status.StartsWith("Alterando", StringComparison.OrdinalIgnoreCase)
                        ? status + "..."
                        : status);
        RefreshPreviewBadge(binding.Window);
    }

    private async void GridLayout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            !int.TryParse(button.Tag?.ToString(), out int slots) ||
            slots is not (1 or 4 or 9 or 16))
            return;

        await OpenGridAsync(slots);
    }

    private Task OpenGridAsync(int slots)
    {
        if (slots is not (1 or 4 or 9 or 16) || accountDevices.Count == 0)
            return Task.CompletedTask;

        lock (gridLayoutQueueSync)
        {
            queuedGridLayoutSlots = slots;
            UpdateLayoutButtonSelection(slots);
            if (gridLayoutWorkerRunning)
            {
                Log($"Troca para grade {slots} enfileirada; a montagem atual sera concluida antes de reconstruir as janelas.");
                return gridLayoutWorker;
            }

            gridLayoutWorkerRunning = true;
            gridLayoutWorker = ProcessQueuedGridLayoutsAsync();
            return gridLayoutWorker;
        }
    }

    private async Task<byte[]?> RequestBinaryDeviceConfigAsync(
        int targetDeviceId, int channel, int command, int bufferSize)
    {
        var key = (targetDeviceId, command);
        if (pendingBinaryConfigReads.ContainsKey(key))
            return null;

        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
        var completion = new TaskCompletionSource<byte[]?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingBinaryConfigRead
        {
            Buffer = buffer,
            Size = bufferSize,
            Completion = completion
        };
        if (!pendingBinaryConfigReads.TryAdd(key, pending))
        {
            Marshal.FreeHGlobal(buffer);
            return null;
        }

        try
        {
            int result = CmsSdk.CMS_Client_GetDeviceConfig(
                targetDeviceId, channel, command, buffer, bufferSize, -1);
            if (result < 0 && pendingBinaryConfigReads.TryRemove(key, out PendingBinaryConfigRead? rejected))
            {
                RecordDeviceRequestResult(targetDeviceId, result);
                Marshal.FreeHGlobal(rejected.Buffer);
                rejected.Completion.TrySetResult(null);
            }
        }
        catch
        {
            if (pendingBinaryConfigReads.TryRemove(key, out PendingBinaryConfigRead? rejected))
            {
                Marshal.FreeHGlobal(rejected.Buffer);
                rejected.Completion.TrySetResult(null);
            }
        }

        Task completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(20)))
            .ConfigureAwait(false);
        // Em timeout o buffer permanece vivo: o SDK ainda pode escrever nele. O
        // callback tardio ou o fechamento do aplicativo faz a liberacao segura.
        if (completed == completion.Task)
            return await completion.Task.ConfigureAwait(false);
        timedOutDetailedConfigDevices[targetDeviceId] = 0;
        return null;
    }

    private void HandleBinaryDeviceConfigResponse(int result, int targetDeviceId, int command)
    {
        var key = (targetDeviceId, command);
        if (!pendingBinaryConfigReads.TryRemove(key, out PendingBinaryConfigRead? pending))
            return;
        if (result >= 0)
            timedOutDetailedConfigDevices.TryRemove(targetDeviceId, out _);

        byte[]? bytes = null;
        try
        {
            if (result >= 0 && pending.Buffer != IntPtr.Zero)
            {
                bytes = new byte[pending.Size];
                Marshal.Copy(pending.Buffer, bytes, 0, bytes.Length);
            }
        }
        catch
        {
            bytes = null;
        }
        finally
        {
            if (pending.Buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(pending.Buffer);
        }
        pending.Completion.TrySetResult(bytes);
    }

    private async Task<int?> RequestBinaryDeviceConfigWriteAsync(
        int targetDeviceId, int channel, int command, byte[] value)
    {
        var key = (targetDeviceId, command);
        if (pendingBinaryConfigReads.ContainsKey(key) || pendingBinaryConfigWrites.ContainsKey(key))
            return null;
        IntPtr buffer = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, buffer, value.Length);
        var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingBinaryConfigWrite { Buffer = buffer, Completion = completion };
        if (!pendingBinaryConfigWrites.TryAdd(key, pending))
        {
            Marshal.FreeHGlobal(buffer);
            return null;
        }
        try
        {
            int accepted = CmsSdk.CMS_Client_SetDeviceConfig(
                targetDeviceId, channel, command, buffer, value.Length);
            if (accepted < 0 &&
                pendingBinaryConfigWrites.TryRemove(key, out PendingBinaryConfigWrite? rejected))
            {
                Marshal.FreeHGlobal(rejected.Buffer);
                rejected.Completion.TrySetResult(accepted);
            }
        }
        catch
        {
            if (pendingBinaryConfigWrites.TryRemove(key, out PendingBinaryConfigWrite? rejected))
            {
                Marshal.FreeHGlobal(rejected.Buffer);
                rejected.Completion.TrySetResult(null);
            }
        }

        Task completed = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(20)))
            .ConfigureAwait(false);
        return completed == completion.Task
            ? await completion.Task.ConfigureAwait(false)
            : null;
    }

    private void HandleBinaryDeviceConfigWriteResponse(int result, int targetDeviceId, int command)
    {
        if (!pendingBinaryConfigWrites.TryRemove(
                (targetDeviceId, command), out PendingBinaryConfigWrite? pending))
            return;
        if (pending.Buffer != IntPtr.Zero)
            Marshal.FreeHGlobal(pending.Buffer);
        pending.Completion.TrySetResult(result);
    }

    private async Task<int?> WriteSerializedBinaryConfigAsync(
        int targetDeviceId, int channel, int command, byte[] value)
    {
        await deviceConfigIoGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!CanIssueDeviceRequest(targetDeviceId, out _, out _, out _))
                return null;
            return await RequestBinaryDeviceConfigWriteAsync(
                targetDeviceId, channel, command, value).ConfigureAwait(false);
        }
        finally
        {
            deviceConfigIoGate.Release();
        }
    }

    private async Task<byte[]?> ReadSerializedBinaryConfigAsync(
        int targetDeviceId, int channel, int command, int bufferSize)
    {
        await deviceConfigIoGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (timedOutDetailedConfigDevices.ContainsKey(targetDeviceId) ||
                !CanIssueDeviceRequest(targetDeviceId, out _, out _, out _))
                return null;
            return await RequestBinaryDeviceConfigAsync(
                targetDeviceId, channel, command, bufferSize).ConfigureAwait(false);
        }
        finally
        {
            deviceConfigIoGate.Release();
        }
    }

    private static DeviceReadOnlyConfigStore.StorageInfo? ParseStorageInfo(byte[] bytes)
    {
        if (bytes.Length < StorageInfoSize)
            return null;
        int diskCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4));
        if (diskCount is < 0 or > 8)
            return null;

        var info = new DeviceReadOnlyConfigStore.StorageInfo
        {
            ObservedAtUtc = DateTime.UtcNow,
            DiskCount = diskCount
        };
        for (int disk = 0; disk < diskCount; disk++)
        {
            int diskOffset = 4 + disk * 696;
            int partitionCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(diskOffset + 4, 4));
            if (partitionCount is < 0 or > 4)
                return null;
            for (int partition = 0; partition < partitionCount; partition++)
            {
                int offset = diskOffset + 56 + partition * 160;
                uint total = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 8, 4));
                uint free = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12, 4));
                if (total == 0 || free > total)
                    continue;
                string fileSystem = Encoding.ASCII.GetString(bytes, offset + 152, 8).TrimEnd('\0', ' ');
                fileSystem = new string(fileSystem.Where(character => !char.IsControl(character)).ToArray());
                info.Partitions.Add(new DeviceReadOnlyConfigStore.PartitionInfo
                {
                    TypeCode = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)),
                    TotalMegabytes = total,
                    FreeMegabytes = free,
                    StatusCode = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 16, 4)),
                    FileSystem = fileSystem
                });
            }
        }
        return info;
    }

    private static DeviceReadOnlyConfigStore.RecordingInfo? ParseRecordingInfo(
        int channel, byte[] storageTarget, byte[] recordConfig)
    {
        if (storageTarget.Length < RecordStorageTypeSize || recordConfig.Length < RecordConfigSize)
            return null;
        int preRecord = BinaryPrimitives.ReadInt32LittleEndian(recordConfig.AsSpan(0, 4));
        int packetLength = BinaryPrimitives.ReadInt32LittleEndian(recordConfig.AsSpan(8, 4));
        int recordMode = BinaryPrimitives.ReadInt32LittleEndian(recordConfig.AsSpan(12, 4));
        if (preRecord is < 0 or > 3600 || packetLength is < 0 or > 1440 ||
            recordMode is < -1 or > 100)
            return null;

        int enabledPeriods = 0;
        for (int period = 0; period < 42; period++)
        {
            int offset = 16 + period * 28;
            int enabled = BinaryPrimitives.ReadInt32LittleEndian(recordConfig.AsSpan(offset, 4));
            int start = BinaryPrimitives.ReadInt32LittleEndian(recordConfig.AsSpan(offset + 4, 4));
            int end = BinaryPrimitives.ReadInt32LittleEndian(recordConfig.AsSpan(offset + 8, 4));
            if (enabled != 0 && start is >= 0 and <= 86400 && end is >= 0 and <= 86400)
                enabledPeriods++;
        }

        return new DeviceReadOnlyConfigStore.RecordingInfo
        {
            ObservedAtUtc = DateTime.UtcNow,
            Channel = channel,
            PreRecordSeconds = preRecord,
            Redundancy = recordConfig[4] != 0,
            PacketLengthMinutes = packetLength,
            RecordModeCode = recordMode,
            UsesSata = storageTarget[0] != 0,
            UsesUsb = storageTarget[1] != 0,
            UsesSd = storageTarget[2] != 0,
            UsesDvd = storageTarget[3] != 0,
            EnabledSchedulePeriods = enabledPeriods
        };
    }

    private static DeviceReadOnlyConfigStore.LightInfo? ParseCameraLightInfo(int channel, byte[] bytes)
    {
        if (bytes.Length < CameraLightConfigSize)
            return null;
        string mode = Encoding.ASCII.GetString(bytes, 0, 64).TrimEnd('\0', ' ');
        mode = new string(mode.Where(character => !char.IsControl(character)).ToArray());
        int duration = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(64, 4));
        int level = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(68, 4));
        int enabled = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(72, 4));
        int start = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(76, 4));
        int end = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(80, 4));
        if (duration is < 0 or > 86400 || level is < 0 or > 100 ||
            start is < 0 or > 86400 || end is < 0 or > 86400)
            return null;
        return new DeviceReadOnlyConfigStore.LightInfo
        {
            ObservedAtUtc = DateTime.UtcNow,
            Channel = channel,
            WorkMode = mode,
            DurationSeconds = duration,
            Level = level,
            ScheduleEnabled = enabled != 0,
            ScheduleStartSeconds = start,
            ScheduleEndSeconds = end
        };
    }

    private bool CanReadMappedConfiguration(
        CloudApi.AccountDevice device,
        string configurationKey,
        out DeviceConfigurationCatalog.Definition? definition,
        out string reason)
    {
        definition = DeviceConfigurationCatalog.Find(configurationKey);
        if (definition is null)
        {
            reason = "Configuração ausente do catálogo técnico.";
            return false;
        }
        bool? capabilitySupported = null;
        if (definition.RequiredCapability.Length > 0 &&
            deviceProfiles.TryGetCurrentCapability(
                device.CloudId, definition.RequiredCapability, out bool supported))
            capabilitySupported = supported;
        return DeviceConfigurationReadPolicy.CanReadOnDemand(
            definition, capabilitySupported, out reason);
    }

    private void RecordMappedConfiguration(
        CloudApi.AccountDevice device,
        string configurationKey,
        string source,
        DateTime observedAtUtc)
    {
        if (deviceProfiles.RecordConfigurationEvidence(
                device.CloudId, configurationKey, true, source, observedAtUtc))
            SaveDeviceProfiles();
    }

    private async Task<DeviceReadOnlyConfigStore.StorageInfo?> ReadStorageOnDemandAsync(
        CloudApi.AccountDevice device, PreviewBinding online)
    {
        if (!CanReadMappedConfiguration(device, "Storage.Info", out _, out string denied))
        {
            Log("Leitura de armazenamento recusada pela política: " + denied);
            return null;
        }
        DeviceReadOnlyConfigStore.DeviceData cached = readOnlyDeviceConfigs.GetOrCreate(device.CloudId);
        if (cached.Storage is not null)
            return cached.Storage;
        await detailedConfigReadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (cached.Storage is not null)
                return cached.Storage;
            if (!attemptedDetailedConfigReads.TryAdd((device.CloudId, "Storage", -1), 0))
                return null;
            Log($"Leitura sob demanda enviada uma vez: armazenamento; dispositivo {online.DeviceId}.");
            byte[]? bytes = await ReadSerializedBinaryConfigAsync(
                online.DeviceId, -1, StorageInfoCommand, StorageInfoSize).ConfigureAwait(false);
            DeviceReadOnlyConfigStore.StorageInfo? parsed = bytes is null ? null : ParseStorageInfo(bytes);
            if (parsed is null)
                return null;
            cached.Storage = parsed;
            readOnlyDeviceConfigs.Save();
            RecordMappedConfiguration(device, "Storage.Info",
                $"CMS binário 0x{StorageInfoCommand:X}", parsed.ObservedAtUtc);
            return parsed;
        }
        finally
        {
            detailedConfigReadGate.Release();
        }
    }

    private async Task<DeviceReadOnlyConfigStore.RecordingInfo?> ReadRecordingOnDemandAsync(
        CloudApi.AccountDevice device, PreviewBinding online)
    {
        if (!CanReadMappedConfiguration(device, "Recording.Main", out _, out string denied))
        {
            Log("Leitura de gravação recusada pela política: " + denied);
            return null;
        }
        DeviceReadOnlyConfigStore.DeviceData cached = readOnlyDeviceConfigs.GetOrCreate(device.CloudId);
        if (cached.RecordingByChannel.TryGetValue(online.Channel, out DeviceReadOnlyConfigStore.RecordingInfo? saved))
            return saved;
        await detailedConfigReadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (cached.RecordingByChannel.TryGetValue(online.Channel, out saved))
                return saved;
            if (!attemptedDetailedConfigReads.TryAdd((device.CloudId, "Recording", online.Channel), 0))
                return null;
            Log($"Leitura sob demanda enviada uma vez: gravacao; dispositivo {online.DeviceId}; canal {online.Channel}.");
            byte[]? target = await ReadSerializedBinaryConfigAsync(
                online.DeviceId, online.Channel, RecordStorageTypeCommand, RecordStorageTypeSize)
                .ConfigureAwait(false);
            if (target is null)
                return null;
            await Task.Delay(800).ConfigureAwait(false);
            byte[]? config = await ReadSerializedBinaryConfigAsync(
                online.DeviceId, online.Channel, RecordConfigCommand, RecordConfigSize)
                .ConfigureAwait(false);
            DeviceReadOnlyConfigStore.RecordingInfo? parsed = config is null
                ? null
                : ParseRecordingInfo(online.Channel, target, config);
            if (parsed is null)
                return null;
            cached.RecordingByChannel[online.Channel] = parsed;
            readOnlyDeviceConfigs.Save();
            RecordMappedConfiguration(device, "Recording.Main",
                $"CMS binário 0x{RecordConfigCommand:X}", parsed.ObservedAtUtc);
            return parsed;
        }
        finally
        {
            detailedConfigReadGate.Release();
        }
    }

    private async Task<DeviceReadOnlyConfigStore.LightInfo?> ReadCameraLightOnDemandAsync(
        CloudApi.AccountDevice device, PreviewBinding online)
    {
        if (!CanReadMappedConfiguration(device, "Light.White", out _, out string denied))
        {
            Log("Leitura de iluminação recusada pela política: " + denied);
            return null;
        }
        DeviceReadOnlyConfigStore.DeviceData cached = readOnlyDeviceConfigs.GetOrCreate(device.CloudId);
        if (cached.LightByChannel.TryGetValue(online.Channel, out DeviceReadOnlyConfigStore.LightInfo? saved))
            return saved;
        await detailedConfigReadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (cached.LightByChannel.TryGetValue(online.Channel, out saved))
                return saved;
            if (!attemptedDetailedConfigReads.TryAdd((device.CloudId, "Light", online.Channel), 0))
                return null;
            Log($"Leitura sob demanda enviada uma vez: iluminacao; dispositivo {online.DeviceId}; canal {online.Channel}.");
            byte[]? bytes = await ReadSerializedBinaryConfigAsync(
                online.DeviceId, online.Channel, CameraLightConfigCommand, CameraLightConfigSize)
                .ConfigureAwait(false);
            DeviceReadOnlyConfigStore.LightInfo? parsed = bytes is null
                ? null
                : ParseCameraLightInfo(online.Channel, bytes);
            if (parsed is null)
                return null;
            cached.LightByChannel[online.Channel] = parsed;
            readOnlyDeviceConfigs.Save();
            RecordMappedConfiguration(device, "Light.White",
                $"CMS binário 0x{CameraLightConfigCommand:X}", parsed.ObservedAtUtc);
            return parsed;
        }
        finally
        {
            detailedConfigReadGate.Release();
        }
    }

    private async Task<ConfigurationWriteResult> SetCameraLightLevelControlledAsync(
        CloudApi.AccountDevice device, PreviewBinding online, int requestedLevel)
    {
        if (!DeviceConfigurationWritePolicy.IsValidWhiteLightLevel(requestedLevel))
            return new(false, false, "Nível fora do intervalo permitido (0 a 100).");
        DeviceConfigurationCatalog.Definition? definition =
            DeviceConfigurationCatalog.Find("Light.White");
        if (definition is null)
            return new(false, false, "Configuração ausente do catálogo técnico.");

        await detailedConfigReadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[]? original = await ReadSerializedBinaryConfigAsync(
                online.DeviceId, online.Channel, CameraLightConfigCommand, CameraLightConfigSize)
                .ConfigureAwait(false);
            DeviceReadOnlyConfigStore.LightInfo? before = original is null
                ? null
                : ParseCameraLightInfo(online.Channel, original);
            deviceProfiles.Devices.TryGetValue(device.CloudId, out DeviceProfileStore.Profile? profile);
            DeviceProfileStore.ConfigurationBinding? binding = null;
            profile?.CompatibleCommands.TryGetValue("Light.White", out binding);
            if (!DeviceConfigurationWritePolicy.CanWrite(
                    definition, binding, before is not null, DateTime.UtcNow,
                    out TimeSpan wait, out string denied))
            {
                string suffix = wait > TimeSpan.Zero
                    ? $" Aguarde {Math.Max(1, Math.Ceiling(wait.TotalSeconds))} segundo(s)."
                    : string.Empty;
                return new(false, false, denied + suffix);
            }
            if (original is null || before is null)
                return new(false, false, "A câmera não forneceu um valor inicial validável.");
            if (before.Level == requestedLevel)
                return new(true, false, "A câmera já está configurada com este nível.");

            byte[] proposed = (byte[])original.Clone();
            BinaryPrimitives.WriteInt32LittleEndian(proposed.AsSpan(68, 4), requestedLevel);
            DateTime writeAtUtc = DateTime.UtcNow;
            if (deviceProfiles.RecordConfigurationWrite(
                    device.CloudId, "Light.White", writeAtUtc))
                SaveDeviceProfiles();
            int? writeResult = await WriteSerializedBinaryConfigAsync(
                online.DeviceId, online.Channel, CameraLightConfigCommand, proposed)
                .ConfigureAwait(false);
            if (writeResult is null)
                return new(false, false,
                    "O SDK não confirmou a alteração. Nenhuma repetição ou restauração automática foi enviada.");
            if (writeResult < 0)
                return new(false, false, $"A câmera recusou a alteração (código {writeResult}).");

            await Task.Delay(1200).ConfigureAwait(false);
            byte[]? verifiedBytes = await ReadSerializedBinaryConfigAsync(
                online.DeviceId, online.Channel, CameraLightConfigCommand, CameraLightConfigSize)
                .ConfigureAwait(false);
            DeviceReadOnlyConfigStore.LightInfo? verified = verifiedBytes is null
                ? null
                : ParseCameraLightInfo(online.Channel, verifiedBytes);
            if (verified is null)
                return new(false, false,
                    "A gravação foi aceita, mas a leitura de confirmação não respondeu. Não repita agora.");
            if (verified.Level == requestedLevel)
            {
                DeviceReadOnlyConfigStore.DeviceData cache =
                    readOnlyDeviceConfigs.GetOrCreate(device.CloudId);
                cache.LightByChannel[online.Channel] = verified;
                readOnlyDeviceConfigs.Save();
                RecordMappedConfiguration(device, "Light.White",
                    $"CMS binário 0x{CameraLightConfigCommand:X}; escrita validada",
                    verified.ObservedAtUtc);
                return new(true, false,
                    $"Nível alterado de {before.Level} para {verified.Level} e confirmado pela câmera.");
            }

            // A câmera respondeu com outro valor: restaura exatamente o bloco
            // original uma única vez e valida a restauração, sem loop.
            int? rollbackResult = await WriteSerializedBinaryConfigAsync(
                online.DeviceId, online.Channel, CameraLightConfigCommand, original)
                .ConfigureAwait(false);
            if (rollbackResult is null || rollbackResult < 0)
                return new(false, false,
                    "A validação divergiu e a câmera não confirmou a restauração automática.");
            await Task.Delay(1200).ConfigureAwait(false);
            byte[]? rollbackBytes = await ReadSerializedBinaryConfigAsync(
                online.DeviceId, online.Channel, CameraLightConfigCommand, CameraLightConfigSize)
                .ConfigureAwait(false);
            DeviceReadOnlyConfigStore.LightInfo? rollback = rollbackBytes is null
                ? null
                : ParseCameraLightInfo(online.Channel, rollbackBytes);
            bool restored = rollback?.Level == before.Level;
            if (restored && rollback is not null)
            {
                readOnlyDeviceConfigs.GetOrCreate(device.CloudId)
                    .LightByChannel[online.Channel] = rollback;
                readOnlyDeviceConfigs.Save();
            }
            return new(false, restored, restored
                ? "A câmera não manteve o novo valor; o valor anterior foi restaurado e confirmado."
                : "A câmera não manteve o novo valor e a restauração não pôde ser confirmada.");
        }
        finally
        {
            detailedConfigReadGate.Release();
        }
    }

    private async Task WaitForCmsMonitorBeforeAutoPlayAsync(TimeSpan timeout)
    {
        var registeredDeviceIds = new HashSet<int>();
        foreach (CloudApi.AccountDevice device in accountDevices.Where(device => !device.Paused))
        {
            var info = new CmsSdk.DeviceInfo();
            if (GetCmsDeviceInfo(device.CloudId, ref info) != 0 && info.ID > 0)
                registeredDeviceIds.Add(info.ID);
        }
        if (registeredDeviceIds.Count == 0)
            return;

        Log($"Aguardando passivamente o monitor do CMS antes da grade automatica; " +
            $"limite {Math.Ceiling(timeout.TotalSeconds)}s; nenhuma requisicao individual sera enviada.");
        Stopwatch timer = Stopwatch.StartNew();
        while (!isClosing && timer.Elapsed < timeout)
        {
            qtRuntime.ProcessEvents();
            int answered = registeredDeviceIds.Count(automaticDeviceLoginResults.ContainsKey);
            if (answered == registeredDeviceIds.Count)
                break;
            await Task.Delay(200);
        }
        int responses = registeredDeviceIds.Count(automaticDeviceLoginResults.ContainsKey);
        Log($"Monitor do CMS estabilizado para a grade: respostas {responses}/{registeredDeviceIds.Count}; " +
            $"espera {Math.Ceiling(timer.Elapsed.TotalSeconds)}s.");
    }

    private async Task ProcessQueuedGridLayoutsAsync()
    {
        await Task.Yield();
        while (true)
        {
            int slots;
            lock (gridLayoutQueueSync)
            {
                if (queuedGridLayoutSlots is not int requested)
                {
                    gridLayoutWorkerRunning = false;
                    return;
                }
                slots = requested;
                queuedGridLayoutSlots = null;
            }

            await OpenGridCoreAsync(slots);
        }
    }

    private async Task OpenGridCoreAsync(int slots)
    {
        if (slots is not (1 or 4 or 9 or 16) || accountDevices.Count == 0)
            return;
        if (slots == 1 && DeviceBox.SelectedItem is CloudApi.AccountDevice manualSelected &&
            cameraCatalog.GetOrCreate(manualSelected, int.MaxValue).IsManual &&
            string.IsNullOrWhiteSpace(manualSelected.DevicePassword) &&
            !PromptManualDeviceCredentials(manualSelected))
            return;

        currentLayoutSlots = slots;
        preferences.LastGridSize = slots;
        preferences.DefaultSd = SubstreamBox.IsChecked == true;
        SubstreamBox.Content = preferences.DefaultSd ? "SD" : "HD";
        SavePreferences();
        UpdateLayoutButtonSelection(slots);

        SetCameraBusy(true);
        gridOpening = true;
        try
        {
            await DisconnectVideoAsync(log: false);
            ConfigureVideoGrid(slots);
            VideoPlaceholder.Visibility = Visibility.Collapsed;
            deviceId = 0;

            var requests = new List<(CloudApi.AccountDevice Device, int Channel)>();
            if (slots == 1 && DeviceBox.SelectedItem is CloudApi.AccountDevice selected)
            {
                int channel = int.Parse(((ComboBoxItem)ChannelBox.SelectedItem).Content.ToString()!);
                requests.Add((selected, channel));
            }
            else
            {
                // Testa os canais em ordem e só ocupa um quadro quando o SDK
                // confirmar imagem. Assim câmeras simples não deixam um canal 2
                // preto e câmeras indisponíveis não reservam espaço na grade.
                for (int index = 0; index < accountDevices.Count; index++)
                {
                    if (!accountDevices[index].ShowInLiveView)
                        continue;
                    CameraCatalogStore.Entry catalogEntry = cameraCatalog.GetOrCreate(accountDevices[index], index);
                    if (catalogEntry.Paused || accountDevices[index].Paused)
                    {
                        accountDevices[index].RuntimeStatus = "Pausada";
                        continue;
                    }
                    if (catalogEntry.IsManual && string.IsNullOrWhiteSpace(accountDevices[index].DevicePassword))
                    {
                        accountDevices[index].RuntimeStatus = "Credenciais necessárias";
                        continue;
                    }
                    // O VMS Pro abre apenas canais conhecidos/selecionados; ele
                    // nao testa todos os canais novamente a cada troca de grade.
                    // Para um dispositivo novo, teste somente o canal principal.
                    int configuredCount = cameraCatalog.ShouldProbeSecondaryChannel(accountDevices[index])
                        ? 2
                        : 1;
                    // Sem capacidade detectada, valida o principal e sonda o
                    // secundario uma unica vez. O loop impede que o secundario
                    // seja testado antes de o principal confirmar imagem.
                    foreach (int channel in Enumerable.Range(0, configuredCount))
                        requests.Add((accountDevices[index], channel));
                }
                requests = OrderLiveRequests(requests);
            }
            int[] windowResults = RegisterVideoWindows(slots);
            Log($"Abrindo grade {slots}: {requests.Count} candidatos em sequencia; " +
                $"stream {(SubstreamBox.IsChecked == true ? "Extra" : "Main")}.");
            int opened = 0;
            int rejected = 0;
            int destinationWindow = 0;
            var pending = new List<(CloudApi.AccountDevice Device, int Channel)>(requests);
            var rejectionLogged = new HashSet<string>(StringComparer.Ordinal);
            var transientFailures = new Dictionary<string,
                (CloudApi.AccountDevice Device, int Channel, int DeviceId)>(StringComparer.Ordinal);
            var openedKeys = new HashSet<string>(StringComparer.Ordinal);
            bool channelKnowledgeChanged = false;
            Stopwatch loginTimer = Stopwatch.StartNew();
            int connectionTimeoutSeconds = preferences.ConnectionTimeoutSeconds;
            while (destinationWindow < slots && pending.Count > 0 &&
                   loginTimer.Elapsed < TimeSpan.FromSeconds(connectionTimeoutSeconds))
            {
                qtRuntime.ProcessEvents();
                bool progressed = false;
                foreach ((CloudApi.AccountDevice device, int channel) in pending.ToArray())
                {
                    string candidateKey = device.CloudId + ":" + channel;
                    CameraCatalogStore.Entry candidateCatalog = cameraCatalog.GetOrCreate(device, int.MaxValue);
                    if (channel > 0 &&
                        candidateCatalog.ChannelCountOverride is not (1 or 2) &&
                        candidateCatalog.DetectedChannelCount is null &&
                        !openedKeys.Contains(PreviewOrderKey(device.CloudId, 0)))
                    {
                        // O canal principal ainda nao foi validado. Se ele ainda
                        // esta na fila, aguarda a proxima passagem sem enviar
                        // pedido. Se ja foi recusado, descarta tambem a sonda do
                        // secundario para nao esperar ate o timeout da grade.
                        if (pending.Any(item =>
                                string.Equals(item.Device.CloudId, device.CloudId, StringComparison.Ordinal) &&
                                item.Channel == 0))
                            continue;
                        pending.Remove((device, channel));
                        rejected++;
                        progressed = true;
                        continue;
                    }
                    var info = new CmsSdk.DeviceInfo();
                    int found = GetCmsDeviceInfo(device.CloudId, ref info);
                    int state = info.Error;
                    if (info.ID > 0 && automaticDeviceLoginResults.TryGetValue(info.ID, out int callbackState))
                        state = callbackState;

                    if (found == 0 || info.ID <= 0)
                    {
                        pending.Remove((device, channel));
                        rejected++;
                        progressed = true;
                        continue;
                    }
                    TrackDeviceRequestProtection(device, info.ID);
                    if (!CanIssueDeviceRequest(info.ID, out _, out _, out _))
                    {
                        pending.Remove((device, channel));
                        rejected++;
                        progressed = true;
                        if (rejectionLogged.Add(device.CloudId))
                            Log($"Grade: {device.Alias}; {DeviceBlockStatus(info.ID)}; nenhuma requisicao sera enviada durante o intervalo seguro.");
                        continue;
                    }
                    if (info.LoginHandle <= 0 && state <= 0)
                    {
                        // Igual ao VMS Pro: StartCheckDevLink e o monitor interno
                        // possuem o estado de login. A grade nunca chama
                        // DeviceLoginOrLogout automaticamente. Enquanto a resposta
                        // ainda nao chegou, aguarda uma janela curta compartilhada
                        // pela montagem inteira, sem multiplicar por canal.
                        if (state == 0 && loginTimer.Elapsed < TimeSpan.FromSeconds(15))
                        {
                            continue;
                        }
                        pending.Remove((device, channel));
                        rejected++;
                        progressed = true;
                        if (state < 0 && rejectionLogged.Add(device.CloudId))
                            Log($"Grade: {device.Alias}; login provisoriamente recusado ({state}); cadastro preservado.");
                        else if (rejectionLogged.Add(device.CloudId))
                            Log($"Grade: {device.Alias}; monitor do CMS ainda sem confirmacao; " +
                                "nenhum login individual foi enviado.");
                        continue;
                    }

                    pending.Remove((device, channel));
                    SetVideoLabel(destinationWindow, device, channel);
                    bool useSd = cameraCatalog.GetOrCreate(device, int.MaxValue).PreferredSd
                        ?? (SubstreamBox.IsChecked == true);
                    CmsSdk.StreamType streamType = useSd
                        ? CmsSdk.StreamType.Extra
                        : CmsSdk.StreamType.Main;
                    bool visible = await TryOpenConfirmedPreviewAsync(
                        info.ID, destinationWindow, channel, streamType,
                        string.IsNullOrWhiteSpace(device.Alias) ? "Câmera" : device.Alias,
                        device.CloudId, TimeSpan.FromSeconds(15));
                    Log($"Grade: {device.Alias}; canal {channel + 1}; quadro {destinationWindow + 1}; " +
                        $"janela {windowResults[destinationWindow]}; confirmado {(visible ? 1 : 0)}.");
                    if (visible)
                    {
                        transientFailures.Remove(candidateKey);
                        openedKeys.Add(candidateKey);
                        channelKnowledgeChanged |= cameraCatalog.MarkChannelAvailable(device, channel);
                        if (channel > 0 && candidateCatalog.ChannelCountOverride is not (1 or 2))
                            channelKnowledgeChanged |= cameraCatalog.SetDetectedChannelCount(device, 2);
                        opened++;
                        destinationWindow++;
                    }
                    else
                    {
                        videoLabels[destinationWindow].Visible = false;
                        videoLabels[destinationWindow].Text = string.Empty;
                        bool rejectedSecondary = channel > 0 &&
                            openedKeys.Contains(PreviewOrderKey(device.CloudId, 0));
                        if (rejectedSecondary && candidateCatalog.ChannelCountOverride is not (1 or 2))
                        {
                            if (candidateCatalog.SecondaryChannelConfirmedEver ||
                                candidateCatalog.DetectedChannelCount == 2 ||
                                candidateCatalog.KnownChannels.Contains(1))
                            {
                                Log($"Grade: {device.Alias}; canal 2 já confirmado anteriormente; " +
                                    "falha atual tratada como transitória e histórico preservado.");
                            }
                            else
                            {
                                channelKnowledgeChanged |= cameraCatalog.RecordSecondaryChannelProbeFailure(device);
                                string channelClassification = candidateCatalog.SecondaryChannelProbeFailures >= 2
                                    ? "1 canal confirmado após duas verificações espaçadas"
                                    : "canal 2 inconclusivo; nova verificação somente após 24h";
                                Log($"Grade: {device.Alias}; {channelClassification}; o secundario nao ocupara quadro offline.");
                            }
                        }
                        else
                        {
                            transientFailures[candidateKey] = (device, channel, info.ID);
                        }
                        rejected++;
                        Log($"Grade: {device.Alias}; canal {channel + 1}; " +
                            "preview nao confirmou; nenhuma repeticao foi enviada neste ciclo.");
                    }
                    progressed = true;
                    if (destinationWindow >= slots)
                        break;
                }
                if (!progressed)
                    await Task.Delay(250);
            }

            if (channelKnowledgeChanged)
            {
                SaveCameraCatalog();
                RefreshDeviceProfilesFromKnownData();
            }

            int reconnecting = 0;
            foreach ((CloudApi.AccountDevice device, int channel) in requests)
            {
                if (destinationWindow >= slots)
                    break;
                string candidateKey = PreviewOrderKey(device.CloudId, channel);
                if (openedKeys.Contains(candidateKey) ||
                    (!transientFailures.ContainsKey(candidateKey) &&
                     !IsKnownLiveChannel(device, channel)))
                    continue;

                int pendingDeviceId = transientFailures.TryGetValue(
                    candidateKey, out var transient)
                    ? transient.DeviceId
                    : GetCmsDeviceId(device.CloudId);
                if (pendingDeviceId <= 0)
                    continue;
                TrackDeviceRequestProtection(device, pendingDeviceId);
                SetVideoLabel(destinationWindow, device, channel);
                bool reconnectSd = cameraCatalog.GetOrCreate(device, int.MaxValue).PreferredSd
                    ?? (SubstreamBox.IsChecked == true);
                CmsSdk.StreamType reconnectStream = reconnectSd
                    ? CmsSdk.StreamType.Extra
                    : CmsSdk.StreamType.Main;
                var binding = new PreviewBinding(
                    pendingDeviceId, destinationWindow, channel, reconnectStream,
                    string.IsNullOrWhiteSpace(device.Alias) ? "Câmera" : device.Alias,
                    device.CloudId,
                    Volatile.Read(ref previewGeneration));
                previewBindings[destinationWindow] = binding;
                bool monitorReportedUnavailable =
                    !automaticDeviceLoginResults.TryGetValue(
                        pendingDeviceId, out int pendingLoginState) || pendingLoginState <= 0;
                if (monitorReportedUnavailable &&
                    disconnectedPreviewDevices.TryAdd(pendingDeviceId, 0))
                {
                    bool unstable = RecordInitialDeviceUnavailable(pendingDeviceId);
                    Log($"Grade: dispositivo {pendingDeviceId} ganhou quadro sem sessao P2P; " +
                        $"vigia unico iniciado em modo {(unstable ? "instavel" : "normal")}; " +
                        "os canais nao geram pedidos separados.");
                    _ = MonitorDisconnectedDeviceAsync(pendingDeviceId);
                }
                bool requestProtected = !CanIssueDeviceRequest(
                    pendingDeviceId, out _, out _, out _);
                bool automaticRecovery = preferences.AutoReconnect && !requestProtected;
                SetPreviewStatus(
                    binding,
                    requestProtected ? DeviceBlockStatus(pendingDeviceId) :
                    automaticRecovery ? "Reconectando" : "Offline",
                    automaticRecovery ? System.Drawing.Color.Orange : System.Drawing.Color.IndianRed);
                if (automaticRecovery)
                    _ = RecoverPreviewAsync(binding);
                Log($"Grade: {device.Alias}; canal {channel + 1}; quadro {destinationWindow + 1}; " +
                    (automaticRecovery
                        ? "vaga preservada para reconexao automatica protegida."
                        : "vaga preservada sem nova requisicao automatica."));
                destinationWindow++;
                reconnecting++;
            }

            rejected = Math.Max(0, requests.Count - opened - reconnecting);
            for (int window = 0; window < videoLoadingLabels.Count; window++)
                if (!previewBindings.ContainsKey(window) && !activePreviewWindows.Contains(window))
                    SetVideoLoadingState(window, null);
            Log($"Grade preenchida em sequencia: {opened} fluxos confirmados; " +
                $"{reconnecting} reconectando; {rejected} candidatos vazios ou recusados.");

            // A velocidade de login de cada dispositivo nao pode decidir sua
            // posicao visual. As janelas nativas podem abrir fora de ordem e,
            // ao final, os containers voltam para a ordem persistida.
            RestoreConfiguredVideoGrid();
            playing = activePreviewWindows.Count > 0;
            DisconnectButton.IsEnabled = playing;
            VideoPlaceholder.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            Log($"GRADE {slots} INICIADA: fluxos aceitos {opened}; quadros ignorados/recusados {rejected}.");
        }
        catch (Exception ex)
        {
            Log("ERRO AO ABRIR A GRADE: " + ex.Message);
        }
        finally
        {
            gridOpening = false;
            ScheduleLayoutRestore();
            SetCameraBusy(false);
        }
    }

    private bool PromptManualDeviceCredentials(CloudApi.AccountDevice device)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = "Credenciais do dispositivo",
            Width = 410,
            Height = 310,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 25, 40))
        };
        var form = new StackPanel { Margin = new Thickness(24) };
        var title = new TextBlock
        {
            Text = device.Alias,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        };
        var hint = new TextBlock
        {
            Text = "A senha é usada somente nesta sessão e não será salva.",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 162, 188)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 14)
        };
        var user = new System.Windows.Controls.TextBox { Text = device.DeviceUser, Margin = new Thickness(0, 5, 0, 10) };
        var password = new PasswordBox { Margin = new Thickness(0, 5, 0, 14) };
        var connect = new System.Windows.Controls.Button { Content = "CONECTAR", Height = 42, IsDefault = true };
        form.Children.Add(title);
        form.Children.Add(hint);
        form.Children.Add(new TextBlock { Text = "Usuário do dispositivo", Foreground = System.Windows.Media.Brushes.White });
        form.Children.Add(user);
        form.Children.Add(new TextBlock { Text = "Senha do dispositivo", Foreground = System.Windows.Media.Brushes.White });
        form.Children.Add(password);
        form.Children.Add(connect);
        connect.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(user.Text) || password.Password.Length == 0)
                return;
            dialog.DialogResult = true;
        };
        dialog.Content = form;
        if (dialog.ShowDialog() != true)
            return false;
        device.DeviceUser = user.Text.Trim();
        device.DevicePassword = password.Password;
        password.Clear();
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
        entry.DeviceUser = device.DeviceUser;
        SaveCameraCatalog();
        int registered = RegisterDeviceToCms(device);
        device.CmsDeviceId = registered > 0 ? GetCmsDeviceId(device.CloudId) : 0;
        device.RuntimeStatus = registered > 0 ? "Conectando" : "Cadastro recusado";
        DeviceBox.Items.Refresh();
        return registered > 0;
    }

    private List<(CloudApi.AccountDevice Device, int Channel)> OrderLiveRequests(
        List<(CloudApi.AccountDevice Device, int Channel)> requests)
    {
        var rank = preferences.LiveLayoutOrder
            .Select((key, index) => (key, index))
            .GroupBy(item => item.key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        return requests
            .Select((request, originalIndex) => (request, originalIndex))
            .OrderBy(item => rank.TryGetValue(
                PreviewOrderKey(item.request.Device.CloudId, item.request.Channel), out int order)
                    ? order
                    : int.MaxValue)
            .ThenBy(item => item.originalIndex)
            .Select(item => item.request)
            .ToList();
    }

    private static string PreviewOrderKey(string cloudId, int channel) => $"{cloudId}:{channel}";

    private bool IsKnownLiveChannel(CloudApi.AccountDevice device, int channel) =>
        // A ordem visual tambem guarda candidatos que foram testados e nunca
        // exibiram imagem. Ela nao pode ser usada como prova de que um canal
        // existe, pois cameras quebradas acabariam ocupando vagas eternamente.
        // Somente MarkChannelAvailable, apos callback de imagem confirmado,
        // inclui o canal no catalogo de fluxos recuperaveis.
        cameraCatalog.IsKnownChannel(device, channel);

    private static int GetCmsDeviceInfo(string identifier, ref CmsSdk.DeviceInfo info)
    {
        return System.Net.IPAddress.TryParse(identifier, out _)
            ? CmsSdk.CMS_Client_GetDeviceByIP(identifier, ref info)
            : CmsSdk.CMS_Client_GetDeviceByCloudID(identifier, 2, ref info);
    }

    private static int GetCmsDeviceId(string cloudId)
    {
        var info = new CmsSdk.DeviceInfo();
        int found = GetCmsDeviceInfo(cloudId, ref info);
        return found != 0 ? info.ID : 0;
    }

    private string GetLocalOemId(CloudApi.AccountDevice device)
    {
        var info = new CmsSdk.DeviceInfo();
        int found = GetCmsDeviceInfo(device.CloudId, ref info);
        return found != 0 && info.ID > 0 ? CmsSdk.GetOemId(ref info) : string.Empty;
    }

    private void RefreshDeviceProfilesFromKnownData()
    {
        bool changed = false;
        foreach (CloudApi.AccountDevice device in accountDevices)
        {
            CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
            changed |= deviceProfiles.UpdateIdentity(device, entry, GetLocalOemId(device));
            changed |= ImportCachedConfigurationEvidence(device);
        }
        if (changed)
            SaveDeviceProfiles();
    }

    private bool ImportCachedConfigurationEvidence(CloudApi.AccountDevice device)
    {
        if (!readOnlyDeviceConfigs.Devices.TryGetValue(
                device.CloudId, out DeviceReadOnlyConfigStore.DeviceData? cached))
            return false;
        bool changed = false;
        if (cached.Storage is not null)
            changed |= deviceProfiles.RecordConfigurationEvidence(
                device.CloudId, "Storage.Info", true,
                $"Cache CMS binário 0x{StorageInfoCommand:X}", cached.Storage.ObservedAtUtc);
        if (cached.RecordingByChannel.Values.OrderBy(item => item.ObservedAtUtc).LastOrDefault()
            is DeviceReadOnlyConfigStore.RecordingInfo recording)
            changed |= deviceProfiles.RecordConfigurationEvidence(
                device.CloudId, "Recording.Main", true,
                $"Cache CMS binário 0x{RecordConfigCommand:X}", recording.ObservedAtUtc);
        if (cached.LightByChannel.Values.OrderBy(item => item.ObservedAtUtc).LastOrDefault()
            is DeviceReadOnlyConfigStore.LightInfo light)
            changed |= deviceProfiles.RecordConfigurationEvidence(
                device.CloudId, "Light.White", true,
                $"Cache CMS binário 0x{CameraLightConfigCommand:X}", light.ObservedAtUtc);
        return changed;
    }

    private void RecordSystemFunctionProfile(int targetDeviceId, JsonElement root)
    {
        if (!deviceCloudIds.TryGetValue(targetDeviceId, out string? cloudId))
        {
            CloudApi.AccountDevice? matched = accountDevices.FirstOrDefault(device =>
                device.CmsDeviceId == targetDeviceId);
            cloudId = matched?.CloudId;
        }
        if (string.IsNullOrWhiteSpace(cloudId))
            return;

        // Different firmware generations report the same feature under different
        // official keys (including legacy misspellings such as HumanDection).
        // Normalize only this response already received from SystemFunction; the
        // native JSON is never persisted because it can contain device metadata.
        bool changed = false;
        foreach (DeviceCapabilityCatalog.Definition capability in DeviceCapabilityCatalog.Definitions)
            if (TryFindAnyBoolean(root, capability.ProviderAliases, out bool supported, out string matched))
                changed |= deviceProfiles.RecordCapability(
                    cloudId, capability.Key, supported, "SystemFunction: " + matched);
        changed |= deviceProfiles.RecordCapability(
            cloudId, "Identification.SystemFunctionSchema3", true, "SystemFunction");
        changed |= deviceProfiles.RecordConfigurationEvidence(
            cloudId, "Capability.SystemFunction", true, "SystemFunction: resposta válida");
        if (changed)
            SaveDeviceProfiles();
    }

    private void SaveDeviceProfiles()
    {
        try { deviceProfiles.Save(); }
        catch (Exception ex) { Log("Não foi possível salvar o perfil técnico local: " + ex.Message); }
    }

    private void TrackDeviceRequestProtection(CloudApi.AccountDevice device, int cmsDeviceId)
    {
        if (cmsDeviceId <= 0)
            return;
        deviceCloudIds[cmsDeviceId] = device.CloudId;
        device.CmsDeviceId = cmsDeviceId;
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
        if (deviceRequestProtections.TryGetValue(cmsDeviceId, out DeviceRequestProtection? active))
        {
            DateTime activeUntil;
            int activeError;
            lock (active.Sync)
            {
                activeUntil = active.NextAllowedUtc;
                activeError = active.LastError;
            }
            if (activeError is -27 or -25 && activeUntil > DateTime.UtcNow)
            {
                if (entry.LastRequestError != activeError ||
                    entry.RequestBlockedUntilUtc is not DateTime savedUntil ||
                    savedUntil.ToUniversalTime() < activeUntil)
                {
                    entry.LastRequestError = activeError;
                    entry.RequestBlockedUntilUtc = activeUntil;
                    SaveCameraCatalog();
                }
                return;
            }
        }
        if (entry.LastRequestError is not (-27 or -25) ||
            entry.RequestBlockedUntilUtc is not DateTime blockedUntil)
            return;

        DateTime blockedUntilUtc = blockedUntil.ToUniversalTime();
        if (blockedUntilUtc <= DateTime.UtcNow)
        {
            entry.LastRequestError = 0;
            entry.RequestBlockedUntilUtc = null;
            SaveCameraCatalog();
            return;
        }

        DeviceRequestProtection protection = deviceRequestProtections.GetOrAdd(
            cmsDeviceId, _ => new DeviceRequestProtection());
        lock (protection.Sync)
        {
            protection.LastError = entry.LastRequestError;
            protection.NextAllowedUtc = blockedUntilUtc;
        }
    }

    private void PersistDeviceProtection(int device, int error, DateTime blockedUntilUtc)
    {
        if (!deviceCloudIds.TryGetValue(device, out string? cloudId))
            return;
        Dispatcher.BeginInvoke((Action)(() =>
        {
            CloudApi.AccountDevice? accountDevice = accountDevices.FirstOrDefault(item =>
                string.Equals(item.CloudId, cloudId, StringComparison.Ordinal));
            if (accountDevice is null)
                return;
            CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(accountDevice, int.MaxValue);
            if (entry.LastRequestError == error &&
                entry.RequestBlockedUntilUtc is DateTime existing &&
                existing.ToUniversalTime() >= blockedUntilUtc)
                return;
            entry.LastRequestError = error;
            entry.RequestBlockedUntilUtc = blockedUntilUtc;
            SaveCameraCatalog();
        }));
    }

    private void ClearPersistedDeviceProtection(int device)
    {
        if (!deviceCloudIds.TryGetValue(device, out string? cloudId))
            return;
        Dispatcher.BeginInvoke((Action)(() =>
        {
            CloudApi.AccountDevice? accountDevice = accountDevices.FirstOrDefault(item =>
                string.Equals(item.CloudId, cloudId, StringComparison.Ordinal));
            if (accountDevice is null)
                return;
            CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(accountDevice, int.MaxValue);
            if (entry.LastRequestError == 0 && entry.RequestBlockedUntilUtc is null)
                return;
            entry.LastRequestError = 0;
            entry.RequestBlockedUntilUtc = null;
            SaveCameraCatalog();
        }));
    }

    private void RecordDeviceRequestResult(int device, int result)
    {
        if (device <= 0 || result == 0)
            return;

        DeviceRequestProtection protection = deviceRequestProtections.GetOrAdd(
            device, _ => new DeviceRequestProtection());
        bool clearPersisted = false;
        lock (protection.Sync)
        {
            if (result > 0)
            {
                // Um callback positivo passivo nao cancela a quarentena. O
                // primeiro pedido do app so pode ocorrer apos o prazo completo.
                if (protection.LastError is -27 or -25 &&
                    protection.NextAllowedUtc > DateTime.UtcNow)
                    return;
                protection.ConsecutiveFailures = 0;
                protection.LastError = 0;
                protection.LastFailureUtc = default;
                protection.NextAllowedUtc = default;
                clearPersisted = true;
            }
            else
            {
                DateTime now = DateTime.UtcNow;
                if (result is -27 or -25 &&
                    protection.LastError == result && protection.NextAllowedUtc > now)
                {
                    // O monitor CMS pode repetir o mesmo erro passivamente. Não
                    // prolongar a quarentena, senão ela nunca terminaria.
                    return;
                }
                // The native monitor can repeat the same callback while processing
                // one request. Count it once, otherwise a single request would look
                // like a burst produced by the application.
                if (protection.LastError == result &&
                    now - protection.LastFailureUtc < TimeSpan.FromSeconds(15))
                    return;

                protection.LastError = result;
                protection.LastFailureUtc = now;
                protection.ConsecutiveFailures++;
                TimeSpan cooldown = ConnectionRecoveryPolicy.ErrorCooldown(
                    result, protection.ConsecutiveFailures);
                protection.NextAllowedUtc = now + cooldown;
                if (result is -27 or -25)
                    PersistDeviceProtection(device, result, protection.NextAllowedUtc);
            }
        }
        if (clearPersisted)
            ClearPersistedDeviceProtection(device);
    }

    private bool CanIssueDeviceRequest(
        int device, out TimeSpan wait, out bool blockedByDevice, out int lastError)
    {
        wait = TimeSpan.Zero;
        blockedByDevice = false;
        lastError = 0;
        if (device <= 0 || !deviceRequestProtections.TryGetValue(device, out DeviceRequestProtection? protection))
            return true;

        lock (protection.Sync)
        {
            lastError = protection.LastError;
            wait = protection.NextAllowedUtc > DateTime.UtcNow
                ? protection.NextAllowedUtc - DateTime.UtcNow
                : TimeSpan.Zero;
            blockedByDevice = lastError == -27 && wait > TimeSpan.Zero;
            return wait <= TimeSpan.Zero;
        }
    }

    private bool IsDeviceCooldownBlocked(int device) =>
        !CanIssueDeviceRequest(device, out _, out bool blockedByDevice, out _) && blockedByDevice;

    private string DeviceBlockStatus(int device)
    {
        if (!CanIssueDeviceRequest(
                device, out TimeSpan wait, out bool blockedByDevice, out int lastError))
        {
            if (blockedByDevice)
                return $"Bloqueada - aguarde ate {DateTime.Now.Add(wait):HH:mm}";
            if (lastError == -25)
                return $"Limite de conexoes - aguarde ate {DateTime.Now.Add(wait):HH:mm}";
            return $"Aguardando sinal - nova tentativa ate {DateTime.Now.Add(wait):HH:mm}";
        }
        return "Offline";
    }

    private TimeSpan GetProtectedRetryDelay(int device)
    {
        TimeSpan configuredMinimum = TimeSpan.FromSeconds(
            Math.Max(60, preferences.ReconnectDelaySeconds));
        if (CanIssueDeviceRequest(device, out TimeSpan wait, out _, out _))
            return configuredMinimum;
        return wait < configuredMinimum ? configuredMinimum : wait;
    }

    private bool RecordDeviceDisconnect(int device)
    {
        DeviceStabilityState state = deviceStabilityStates.GetOrAdd(
            device, _ => new DeviceStabilityState());
        DateTime now = DateTime.UtcNow;
        lock (state.Sync)
        {
            if (state.LastDisconnectUtc != default &&
                now - state.LastDisconnectUtc < TimeSpan.FromSeconds(15))
                return state.UnstableUntilUtc > now;
            state.LastDisconnectUtc = now;
            while (state.DisconnectsUtc.Count > 0 &&
                   now - state.DisconnectsUtc.Peek() >
                   ConnectionRecoveryPolicy.UnstableObservationWindow)
                state.DisconnectsUtc.Dequeue();
            state.DisconnectsUtc.Enqueue(now);
            if (state.DisconnectsUtc.Count >= 2)
                state.UnstableUntilUtc = now + ConnectionRecoveryPolicy.UnstableModeDuration;
            return state.UnstableUntilUtc > now;
        }
    }

    private bool RecordInitialDeviceUnavailable(int device)
    {
        DeviceStabilityState state = deviceStabilityStates.GetOrAdd(
            device, _ => new DeviceStabilityState());
        lock (state.Sync)
        {
            // Montar novamente a grade não representa outra queda física. A
            // primeira indisponibilidade apenas ancora o intervalo seguro; só o
            // callback DeviceControl=4 alimenta o histórico de instabilidade.
            if (state.LastDisconnectUtc == default)
                state.LastDisconnectUtc = DateTime.UtcNow;
            return state.UnstableUntilUtc > DateTime.UtcNow;
        }
    }

    private bool IsDeviceInUnstableMode(int device)
    {
        if (!deviceStabilityStates.TryGetValue(device, out DeviceStabilityState? state))
            return false;
        lock (state.Sync)
            return state.UnstableUntilUtc > DateTime.UtcNow;
    }

    private TimeSpan GetDisconnectedDeviceLoginWait(int device, bool manual)
    {
        bool unstable = !manual && IsDeviceInUnstableMode(device);
        TimeSpan minimum = ConnectionRecoveryPolicy.DeviceLoginMinimum(
            unstable, preferences.ReconnectDelaySeconds);
        DeviceStabilityState state = deviceStabilityStates.GetOrAdd(
            device, _ => new DeviceStabilityState());
        DateTime now = DateTime.UtcNow;
        TimeSpan intervalWait;
        lock (state.Sync)
        {
            DateTime anchor = state.LastDeviceLoginAttemptUtc != default
                ? state.LastDeviceLoginAttemptUtc
                : state.LastDisconnectUtc;
            TimeSpan elapsed = anchor == default ? TimeSpan.Zero : now - anchor;
            intervalWait = elapsed >= minimum ? TimeSpan.Zero : minimum - elapsed;
        }
        if (!CanIssueDeviceRequest(device, out TimeSpan protectedWait, out _, out _))
            return protectedWait > intervalWait ? protectedWait : intervalWait;
        return intervalWait;
    }

    private async Task<bool> TryRequestDisconnectedDeviceLoginAsync(int device, bool manual)
    {
        if (isClosing || !disconnectedPreviewDevices.ContainsKey(device))
            return false;

        SemaphoreSlim gate = deviceLoginAttemptLocks.GetOrAdd(
            device, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0))
        {
            Log($"Retomada do dispositivo {device} ja possui uma solicitacao em andamento; " +
                "nenhuma chamada duplicada foi enviada.");
            return false;
        }

        try
        {
            if (isClosing || !disconnectedPreviewDevices.ContainsKey(device))
                return false;
            TimeSpan wait = GetDisconnectedDeviceLoginWait(device, manual);
            if (wait > TimeSpan.Zero)
            {
                Log($"Retomada {(manual ? "manual" : "automatica")} do dispositivo {device} " +
                    $"protegida por mais {Math.Ceiling(wait.TotalSeconds)}s; nenhuma requisicao enviada.");
                return false;
            }
            if (!CanIssueDeviceRequest(device, out TimeSpan protectedWait, out _, out int protectedError))
            {
                Log($"Login do dispositivo {device} suprimido apos erro {protectedError}; " +
                    $"restante {Math.Ceiling(protectedWait.TotalSeconds)}s.");
                return false;
            }

            DeviceStabilityState state = deviceStabilityStates.GetOrAdd(
                device, _ => new DeviceStabilityState());
            lock (state.Sync)
                state.LastDeviceLoginAttemptUtc = DateTime.UtcNow;

            await p2pReloginGate.WaitAsync();
            try
            {
                if (isClosing || !disconnectedPreviewDevices.ContainsKey(device))
                    return false;
                int result = await Task.Run(
                    () => CmsSdk.CMS_Client_DeviceLoginOrLogout(device, true));
                Log($"Login unico por dispositivo solicitado: dispositivo {device}; " +
                    $"origem {(manual ? "botao" : "vigia protegido")}; retorno {result}; " +
                    "nenhum canal foi aberto nesta etapa.");
                if (result < 0)
                    RecordDeviceRequestResult(device, result);
                return result != 0;
            }
            finally
            {
                p2pReloginGate.Release();
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task MonitorDisconnectedDeviceAsync(int device)
    {
        SemaphoreSlim gate = disconnectedRecoveryLocks.GetOrAdd(
            device, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0))
            return;

        try
        {
            while (!isClosing && disconnectedPreviewDevices.ContainsKey(device))
            {
                TimeSpan wait = GetDisconnectedDeviceLoginWait(device, manual: false);
                if (wait > TimeSpan.Zero)
                {
                    Log($"Vigia de sinal: dispositivo {device}; nova tentativa unica em " +
                        $"{Math.Ceiling(wait.TotalSeconds)}s; nenhuma requisicao durante a espera.");
                    await Task.Delay(wait);
                }
                if (isClosing || !disconnectedPreviewDevices.ContainsKey(device))
                    break;
                await TryRequestDisconnectedDeviceLoginAsync(device, manual: false);
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryReservePassiveRecoveryCycle(
        int device, bool unstable, out TimeSpan remaining)
    {
        DeviceStabilityState state = deviceStabilityStates.GetOrAdd(
            device, _ => new DeviceStabilityState());
        DateTime now = DateTime.UtcNow;
        TimeSpan minimum = ConnectionRecoveryPolicy.PassiveCycleMinimum(
            unstable, preferences.ReconnectDelaySeconds);
        lock (state.Sync)
        {
            TimeSpan elapsed = now - state.LastPassiveCycleUtc;
            if (state.LastPassiveCycleUtc != DateTime.MinValue && elapsed < minimum)
            {
                remaining = minimum - elapsed;
                return false;
            }
            state.LastPassiveCycleUtc = now;
            remaining = TimeSpan.Zero;
            return true;
        }
    }

    private async Task RecoverReturnedDevicePreviewsAsync(int recoveredDeviceId)
    {
        SemaphoreSlim gate = passiveRecoveryLocks.GetOrAdd(
            recoveredDeviceId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0))
        {
            Log($"Retomada passiva ja ativa: dispositivo {recoveredDeviceId}; callback duplicado ignorado.");
            return;
        }

        try
        {
            bool unstable = IsDeviceInUnstableMode(recoveredDeviceId);
            if (!TryReservePassiveRecoveryCycle(
                    recoveredDeviceId, unstable, out TimeSpan cycleWait))
            {
                Log($"Retomada passiva aguardando o intervalo do dispositivo {recoveredDeviceId}; " +
                    $"restante {Math.Ceiling(cycleWait.TotalSeconds)}s; nenhuma requisicao enviada agora.");
                await Task.Delay(cycleWait);
                if (isClosing || disconnectedPreviewDevices.ContainsKey(recoveredDeviceId) ||
                    !automaticDeviceLoginResults.TryGetValue(
                        recoveredDeviceId, out int delayedLoginState) || delayedLoginState <= 0)
                    return;
                unstable = IsDeviceInUnstableMode(recoveredDeviceId);
                if (!TryReservePassiveRecoveryCycle(
                        recoveredDeviceId, unstable, out TimeSpan secondWait))
                {
                    Log($"Retomada passiva ainda protegida: dispositivo {recoveredDeviceId}; " +
                        $"restante {Math.Ceiling(secondWait.TotalSeconds)}s; ciclo encerrado sem pedidos.");
                    return;
                }
            }

            TimeSpan grace = ConnectionRecoveryPolicy.PassiveGrace(unstable);
            Log($"Sessao P2P retornou passivamente: dispositivo {recoveredDeviceId}; " +
                $"modo {(unstable ? "instavel" : "normal")}; aguardando {grace.TotalSeconds:0}s " +
                "para os previews retomarem sozinhos.");
            await Task.Delay(grace);
            if (isClosing || disconnectedPreviewDevices.ContainsKey(recoveredDeviceId) ||
                !automaticDeviceLoginResults.TryGetValue(recoveredDeviceId, out int loginState) ||
                loginState <= 0)
            {
                Log($"Retomada passiva cancelada: dispositivo {recoveredDeviceId} perdeu a sessao durante a espera.");
                return;
            }

            PreviewBinding[] missing = previewBindings.Values
                .Where(binding =>
                    binding.DeviceId == recoveredDeviceId &&
                    binding.Generation == Volatile.Read(ref previewGeneration) &&
                    !confirmedPreviewWindows.ContainsKey(binding.Window))
                .OrderBy(binding => binding.Window)
                .ToArray();
            if (missing.Length == 0)
            {
                Log($"Retomada passiva concluida sem pedidos: dispositivo {recoveredDeviceId}; " +
                    "todos os previews voltaram sozinhos.");
                return;
            }

            if (!CanIssueDeviceRequest(
                    recoveredDeviceId, out TimeSpan protectedWait,
                    out _, out int protectedError))
            {
                foreach (PreviewBinding binding in missing)
                    SetPreviewStatus(binding, DeviceBlockStatus(recoveredDeviceId), System.Drawing.Color.IndianRed);
                Log($"Retomada passiva aguardara a protecao do dispositivo {recoveredDeviceId}; " +
                    $"erro {protectedError}; restante {Math.Ceiling(protectedWait.TotalSeconds)}s; " +
                    "nenhuma requisicao enviada agora.");
                await Task.Delay(protectedWait);
                if (isClosing || disconnectedPreviewDevices.ContainsKey(recoveredDeviceId) ||
                    !CanIssueDeviceRequest(recoveredDeviceId, out _, out _, out _))
                    return;
                if (automaticDeviceLoginResults.TryGetValue(
                        recoveredDeviceId, out int currentLoginState) && currentLoginState > 0)
                    RecordDeviceRequestResult(recoveredDeviceId, currentLoginState);
            }

            int attempted = 0;
            int recovered = 0;
            foreach (PreviewBinding binding in missing)
            {
                if (isClosing || disconnectedPreviewDevices.ContainsKey(recoveredDeviceId))
                    break;
                if (!previewBindings.TryGetValue(binding.Window, out PreviewBinding? current) ||
                    current != binding || confirmedPreviewWindows.ContainsKey(binding.Window) ||
                    recoveringPreviewWindows.ContainsKey(binding.Window))
                    continue;
                if (!CanIssueDeviceRequest(recoveredDeviceId, out _, out _, out _))
                    break;

                attempted++;
                try { CmsSdk.CMS_Client_StopPreviewByWnd(binding.Window, 0); }
                catch { }
                activePreviewWindows.Remove(binding.Window);
                confirmedPreviewWindows.TryRemove(binding.Window, out _);
                failedPreviewWindows.TryRemove(binding.Window, out _);
                SetPreviewStatus(binding, "Preparando retomada", System.Drawing.Color.Orange);
                Log($"Retomada passiva: preview antigo encerrado localmente; dispositivo " +
                    $"{recoveredDeviceId}; canal {binding.Channel}; janela {binding.Window}.");
                await Task.Delay(500);
                await RecoverPreviewAsync(binding, forceOneAttempt: true);
                if (confirmedPreviewWindows.ContainsKey(binding.Window))
                    recovered++;
                if (!isClosing && attempted < missing.Length)
                    await Task.Delay(ConnectionRecoveryPolicy.PreviewSpacing);
            }
            Log($"Retomada passiva concluida: dispositivo {recoveredDeviceId}; " +
                $"ausentes {missing.Length}; tentados {attempted}; recuperados {recovered}.");
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> TryOpenConfirmedPreviewAsync(
        int selectedDeviceId, int window, int channel,
        CmsSdk.StreamType streamType, string displayName, string cloudId, TimeSpan timeout)
    {
        if (!CanIssueDeviceRequest(selectedDeviceId, out TimeSpan wait, out _, out int lastError))
        {
            Log($"Abertura suprimida: dispositivo {selectedDeviceId}; erro {lastError}; " +
                $"quarentena restante {Math.Ceiling(wait.TotalMinutes)} min.");
            return false;
        }
        confirmedPreviewWindows.TryRemove(window, out _);
        failedPreviewWindows.TryRemove(window, out _);
        previewBindings[window] = new PreviewBinding(
            selectedDeviceId, window, channel, streamType, displayName,
            cloudId,
            Volatile.Read(ref previewGeneration));
        int startResult = CmsSdk.CMS_Client_StartPreview(
            selectedDeviceId, window, channel, streamType, false);
        videoLabels[window].BringToFront();
        if (startResult == 0)
        {
            previewBindings.TryRemove(window, out _);
            SetVideoLoadingState(window, null);
            return false;
        }

        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            qtRuntime.ProcessEvents();
            if (confirmedPreviewWindows.ContainsKey(window))
            {
                activePreviewWindows.Add(window);
                SetPreviewStatus(previewBindings[window], "Online", System.Drawing.Color.LimeGreen);
                QueueAutomaticCapabilityDiscovery(previewBindings[window]);
                return true;
            }
            if (failedPreviewWindows.ContainsKey(window))
                break;
            await Task.Delay(100);
        }

        try { CmsSdk.CMS_Client_StopPreviewByWnd(window, 0); }
        catch { }
        previewBindings.TryRemove(window, out _);
        confirmedPreviewWindows.TryRemove(window, out _);
        failedPreviewWindows.TryRemove(window, out _);
        SetVideoLoadingState(window, null);
        await Task.Delay(250);
        return false;
    }

    private async Task RecoverPreviewAsync(PreviewBinding binding, bool forceOneAttempt = false)
    {
        const int maximumAutomaticAttempts = 3;
        int attempt = 0;
        if (!recoveringPreviewWindows.TryAdd(binding.Window, 0))
            return;
        try
        {
            while (!isClosing &&
                   binding.Generation == Volatile.Read(ref previewGeneration) &&
                   previewBindings.TryGetValue(binding.Window, out PreviewBinding? current) &&
                   current == binding &&
                   attempt < maximumAutomaticAttempts &&
                   (preferences.AutoReconnect || forceOneAttempt && attempt == 0))
            {
                if (disconnectedPreviewDevices.ContainsKey(binding.DeviceId))
                {
                    SetPreviewStatus(binding, "Aguardando sinal", System.Drawing.Color.Orange);
                    Log($"Recuperacao de preview suspensa: dispositivo {binding.DeviceId} sem sessao P2P; " +
                        "nenhuma requisicao enviada.");
                    return;
                }
                attempt++;
                confirmedPreviewWindows.TryRemove(binding.Window, out _);
                failedPreviewWindows.TryRemove(binding.Window, out _);
                SetPreviewStatus(
                    binding,
                    $"Reconectando {attempt} • {DateTime.Now:HH:mm:ss}",
                    System.Drawing.Color.Orange);
                bool loginReady = await EnsureDeviceLoginForRecoveryAsync(binding, attempt);
                if (!loginReady)
                {
                    TimeSpan waitingDelay = GetProtectedRetryDelay(binding.DeviceId);
                    if (!CanIssueDeviceRequest(binding.DeviceId, out _, out _, out _))
                    {
                        SetPreviewStatus(binding, DeviceBlockStatus(binding.DeviceId), System.Drawing.Color.IndianRed);
                        Log($"Reconexao suspensa pela protecao ativa: dispositivo {binding.DeviceId}; nenhuma requisicao enviada.");
                        return;
                    }
                    if (forceOneAttempt)
                    {
                        SetPreviewStatus(binding, "Offline", System.Drawing.Color.IndianRed);
                        Log($"Tentativa manual encerrada sem login confirmado: dispositivo {binding.DeviceId}; " +
                            $"canal {binding.Channel}; nenhuma repeticao automatica enviada.");
                        return;
                    }
                    Log($"Reconexao aguardando o monitor automatico do CMS; nova verificacao em " +
                        $"{Math.Ceiling(waitingDelay.TotalSeconds)}s: dispositivo {binding.DeviceId}; canal {binding.Channel}.");
                    await Task.Delay(waitingDelay);
                    continue;
                }
                if (!CanIssueDeviceRequest(binding.DeviceId, out _, out _, out _))
                {
                    SetPreviewStatus(binding, DeviceBlockStatus(binding.DeviceId), System.Drawing.Color.IndianRed);
                    return;
                }
                if (disconnectedPreviewDevices.ContainsKey(binding.DeviceId))
                {
                    SetPreviewStatus(binding, "Aguardando sinal", System.Drawing.Color.Orange);
                    return;
                }
                PreviewResourceLogger.Write(
                    "antes", binding.DeviceId, binding.Window, attempt);
                int result = CmsSdk.CMS_Client_StartPreview(
                    binding.DeviceId, binding.Window, binding.Channel, binding.StreamType, false);
                PreviewResourceLogger.Write(
                    "depois-start", binding.DeviceId, binding.Window, attempt, result);
                Log($"Recuperacao do preview: dispositivo {binding.DeviceId}; canal {binding.Channel}; " +
                    $"janela {binding.Window}; tentativa {attempt}; retorno {result}.");
                if (result != 0)
                {
                    Stopwatch confirmation = Stopwatch.StartNew();
                    TimeSpan confirmationLimit = TimeSpan.FromSeconds(forceOneAttempt ? 15 : 30);
                    while (confirmation.Elapsed < confirmationLimit)
                    {
                        if (isClosing ||
                            binding.Generation != Volatile.Read(ref previewGeneration) ||
                            !previewBindings.TryGetValue(binding.Window, out PreviewBinding? activeBinding) ||
                            activeBinding != binding)
                            return;
                        qtRuntime.ProcessEvents();
                        if (confirmedPreviewWindows.ContainsKey(binding.Window))
                        {
                            PreviewResourceLogger.Write(
                                "confirmado", binding.DeviceId, binding.Window, attempt, result);
                            SetPreviewStatus(binding, "Online", System.Drawing.Color.LimeGreen);
                            QueueAutomaticCapabilityDiscovery(binding);
                            Log($"Preview recuperado com imagem: dispositivo {binding.DeviceId}; " +
                                $"canal {binding.Channel}; janela {binding.Window}.");
                            return;
                        }
                        // Durante a retomada o SDK primeiro encerra o player antigo
                        // (ChannelControl/VideoWindowControl = 1) e somente depois
                        // entrega a nova resolucao. Esse aviso transitório não pode
                        // cancelar a espera nem disparar StartPreview repetidamente.
                        await Task.Delay(100);
                    }
                }
                PreviewResourceLogger.Write(
                    "sem-imagem", binding.DeviceId, binding.Window, attempt, result);
                TimeSpan retryDelay = GetProtectedRetryDelay(binding.DeviceId);
                if (!CanIssueDeviceRequest(binding.DeviceId, out _, out _, out _))
                {
                    SetPreviewStatus(binding, DeviceBlockStatus(binding.DeviceId), System.Drawing.Color.IndianRed);
                    return;
                }
                if (forceOneAttempt)
                {
                    SetPreviewStatus(binding, "Offline", System.Drawing.Color.IndianRed);
                    Log($"Tentativa manual sem imagem: dispositivo {binding.DeviceId}; canal {binding.Channel}; " +
                        "nenhuma repeticao automatica enviada.");
                    return;
                }
                Log($"Preview ainda sem imagem; nova tentativa automatica em " +
                    $"{Math.Ceiling(retryDelay.TotalSeconds)}s: dispositivo {binding.DeviceId}; canal {binding.Channel}.");
                await Task.Delay(retryDelay);
            }

            if (preferences.AutoReconnect && attempt >= maximumAutomaticAttempts &&
                binding.Generation == Volatile.Read(ref previewGeneration) &&
                previewBindings.TryGetValue(binding.Window, out PreviewBinding? active) &&
                active == binding)
            {
                SetPreviewStatus(binding, "Offline - reconexao manual", System.Drawing.Color.IndianRed);
                Log($"Reconexao automatica encerrada apos {maximumAutomaticAttempts} tentativas: " +
                    $"dispositivo {binding.DeviceId}; canal {binding.Channel}. Nenhuma nova requisicao sera enviada automaticamente.");
            }
        }
        finally
        {
            recoveringPreviewWindows.TryRemove(binding.Window, out _);
        }
    }

    private async Task<bool> EnsureDeviceLoginForRecoveryAsync(
        PreviewBinding binding, int attempt)
    {
        SemaphoreSlim gate = deviceReconnectLocks.GetOrAdd(
            binding.DeviceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (!CanIssueDeviceRequest(
                    binding.DeviceId, out TimeSpan protectedWait,
                    out bool blockedByDevice, out int protectedError))
            {
                string reason = blockedByDevice
                    ? $"bloqueado pelo dispositivo ({protectedError}); quarentena de uma hora"
                    : $"em espera protegida por {Math.Ceiling(protectedWait.TotalSeconds)}s apos erro {protectedError}";
                Log($"Requisicao P2P suprimida: dispositivo {binding.DeviceId}; {reason}.");
                return false;
            }

            int knownState = automaticDeviceLoginResults.TryGetValue(
                binding.DeviceId, out int callbackState)
                ? callbackState
                : 0;
            bool wasDisconnected = disconnectedPreviewDevices.ContainsKey(binding.DeviceId);
            if (knownState > 0 && !wasDisconnected)
                return true;

            await p2pReloginGate.WaitAsync();
            try
            {
                // O VMS Pro deixa o monitor interno do CMS concluir o login. Chamar
                // DeviceLoginOrLogout e StartPreview enquanto o dispositivo ainda
                // está recusado multiplica as tentativas por canal e transforma
                // erros transitórios -7 em bloqueio -27.
                Log($"Aguardando o monitor interno do CMS: dispositivo {binding.DeviceId}; " +
                    $"tentativa visual {attempt}; estado anterior {knownState}.");

                // Never call DeviceLoginOrLogout automatically here. The CMS
                // monitor already owns the device login state; a second login
                // multiplied by the number of channels is what led devices to
                // reject requests and eventually return -27.

                Stopwatch timer = Stopwatch.StartNew();
                while (!isClosing && timer.Elapsed < TimeSpan.FromSeconds(12))
                {
                    qtRuntime.ProcessEvents();
                    if (automaticDeviceLoginResults.TryGetValue(
                            binding.DeviceId, out int currentState) && currentState > 0)
                    {
                        disconnectedPreviewDevices.TryRemove(binding.DeviceId, out _);
                        Log($"Login P2P confirmado pelo monitor do CMS: dispositivo {binding.DeviceId}; " +
                            $"tentativa {attempt}; estado {currentState}.");
                        return true;
                    }
                    await Task.Delay(200);
                }
                Log($"Login P2P ainda pendente no monitor do CMS: dispositivo {binding.DeviceId}; tentativa {attempt}.");
                return false;
            }
            finally
            {
                p2pReloginGate.Release();
            }
        }
        finally
        {
            gate.Release();
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
                string detail = FriendlySdkError(result.Error);
                Log($"FALHA AO ABRIR A CÂMERA — código {result.Error}: {detail}.");
                return;
            }

            deviceId = result.Info.ID;
            int channel = selectedChannel;
            previewLoginError = 0;
            if (!CanIssueDeviceRequest(deviceId, out _, out _, out _))
            {
                Log($"Abertura individual suprimida: {DeviceBlockStatus(deviceId)}. Nenhuma requisicao enviada.");
                return;
            }
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
                string detail = FriendlySdkError(loginError);
                Log($"FALHA AO CONFIRMAR O VÍDEO — código {loginError}: {detail}.");
                return;
            }
            activePreviewWindows.Add(0);
            var singleBinding = new PreviewBinding(
                deviceId, 0, channel,
                SubstreamBox.IsChecked == true ? CmsSdk.StreamType.Extra : CmsSdk.StreamType.Main,
                string.IsNullOrWhiteSpace(selected.Alias) ? "Câmera" : selected.Alias,
                selected.CloudId,
                Volatile.Read(ref previewGeneration));
            previewBindings[0] = singleBinding;
            SetPreviewStatus(singleBinding, "Online", System.Drawing.Color.LimeGreen);
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
            int found = GetCmsDeviceInfo(cloudId, ref info);
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
        int found = GetCmsDeviceInfo(selected.CloudId, ref info);
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

        found = GetCmsDeviceInfo(selected.CloudId, ref info);
        Log($"Consulta final do cadastro: retorno {found}; ID {info.ID}; " +
            $"loginType {info.LoginType}; loginHandle {info.LoginHandle}; erro {info.Error}.");
        if (found == 0 || info.ID <= 0)
            return (false, -2, info);

        TrackDeviceRequestProtection(selected, info.ID);
        if (!CanIssueDeviceRequest(info.ID, out TimeSpan protectedWait, out _, out int protectedError))
        {
            Log($"Abertura cancelada antes do login: erro {protectedError}; " +
                $"quarentena restante {Math.Ceiling(protectedWait.TotalMinutes)} min. Nenhuma requisicao enviada.");
            return (false, protectedError, info);
        }

        Log("Cadastro Cloud preservado como foi criado pela sincronização oficial da conta.");

        Log("Aguardando o login automatico do CMS, como no VMS Pro...");
        (bool loginReady, int loginError) = await WaitForAutomaticDeviceLoginAsync(
            selected.CloudId, info.ID, TimeSpan.FromSeconds(30));
        if (!loginReady)
            return (false, loginError, info);

        found = GetCmsDeviceInfo(selected.CloudId, ref info);
        Log($"Login automatico confirmado: consulta {found}; ID {info.ID}; " +
            $"loginHandle {(info.LoginHandle > 0 ? "positivo" : "nao exposto")}; erro {info.Error}.");
        Log("Dispositivo preparado; a visualizacao reutilizara a sessao autenticada pelo CMS.");
        return (true, 0, info);
    }

    private async Task<(bool Ok, int Error)> WaitForAutomaticDeviceLoginAsync(
        string cloudId, int selectedDeviceId, TimeSpan timeout)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            qtRuntime.ProcessEvents();
            var info = new CmsSdk.DeviceInfo();
            int found = GetCmsDeviceInfo(cloudId, ref info);
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
            int found = GetCmsDeviceInfo(device.CloudId, ref existing);
            CameraCatalogStore.Entry catalogEntry = cameraCatalog.GetOrCreate(device, int.MaxValue);
            if (catalogEntry.Paused || device.Paused)
            {
                // Operação somente no cadastro local: não envia logout,
                // consulta de estado ou preview para a câmera pausada.
                if (found != 0 && existing.ID > 0)
                    CmsSdk.CMS_Client_RemoveDevice(existing.ID);
                device.CmsDeviceId = 0;
                device.Paused = true;
                device.RuntimeStatus = "Pausada";
                synchronized++;
                continue;
            }
            if (found != 0 && existing.ID > 0)
            {
                TrackDeviceRequestProtection(device, existing.ID);
                // Sincronizar a conta nunca deve remover e recriar um cadastro
                // existente. Essa operação reinicia a máquina de login P2P e,
                // multiplicada por todas as câmeras a cada abertura do app,
                // pode produzir a rajada que termina em bloqueio -27.
                // Credenciais só são substituídas por uma ação explícita do
                // usuário na tela da câmera.
                synchronized++;
                continue;
            }

            int added = RegisterDeviceToCms(device);
            if (added > 0)
            {
                device.CmsDeviceId = GetCmsDeviceId(device.CloudId);
                TrackDeviceRequestProtection(device, device.CmsDeviceId);
                synchronized++;
            }
            else
                failed++;
        }
        return (synchronized, failed);
    }

    private int RegisterDeviceToCms(CloudApi.AccountDevice device)
    {
        string name = string.IsNullOrWhiteSpace(device.Alias) ? "Câmera XMEye" : device.Alias;
        if (device.IsNetworkDevice)
            return CmsSdk.CMS_Client_AddDeviceByIP(
                device.CloudId, device.NetworkPort, device.DeviceUser, device.DevicePassword,
                0, name, cloudGroupId > 0 ? cloudGroupId : ushort.MaxValue,
                1, 0, 0);
        return CmsSdk.CMS_Client_AddDeviceByID(
            FormatCmsRegistrationId(device.CloudId), device.DeviceUser,
            device.DevicePassword, device.AdminToken, 0, name,
            cloudGroupId, device.IsShared);
    }

    private static string FormatCmsRegistrationId(string cloudId) =>
        cloudId.Contains('_', StringComparison.Ordinal) ? cloudId : cloudId + "_Cloud";

    private void ApplyCameraCatalog()
    {
        RestoreManualCatalogDevices();
        cameraCatalog.ApplyAndSort(accountDevices);
        try { cameraCatalog.Save(); }
        catch (Exception ex) { Log("Nao foi possivel salvar a organizacao das cameras: " + ex.Message); }
        RefreshDeviceProfilesFromKnownData();
    }

    private void RestoreManualCatalogDevices()
    {
        foreach ((string key, CameraCatalogStore.Entry entry) in cameraCatalog.Cameras)
        {
            if (!entry.IsManual || string.IsNullOrWhiteSpace(entry.Identifier) ||
                accountDevices.Any(device => string.Equals(device.CloudId, entry.Identifier, StringComparison.Ordinal)))
                continue;
            accountDevices.Add(new CloudApi.AccountDevice
            {
                CloudId = entry.Identifier,
                Alias = string.IsNullOrWhiteSpace(entry.Name) ? "Câmera" : entry.Name,
                DeviceUser = entry.DeviceUser,
                DevicePassword = string.Empty,
                IsShared = false,
                LocalGroup = string.IsNullOrWhiteSpace(entry.Group) ? "Casa" : entry.Group,
                ShowInLiveView = entry.ShowInLiveView,
                IsNetworkDevice = entry.IsNetworkDevice,
                NetworkPort = entry.NetworkPort,
                RuntimeStatus = "Credenciais necessárias"
            });
        }
        if (accountDevices.Count > 0)
        {
            cameraCatalog.ApplyAndSort(accountDevices);
            DeviceBox.ItemsSource = null;
            DeviceBox.ItemsSource = accountDevices;
            DeviceBox.IsEnabled = true;
            SetGridButtonsEnabled(true);
            UpdateCameraSummary();
        }
    }

    private void DeviceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CameraSaveStatusText.Text = string.Empty;
        bool selected = DeviceBox.SelectedItem is CloudApi.AccountDevice;
        OpenCameraButton.IsEnabled = selected;
        if (!selected)
        {
            CameraNameBox.Clear();
            CameraGroupBox.Clear();
            CameraUserBox.Clear();
            CameraPasswordBox.Clear();
            CameraSerialText.Text = "Serial protegido: —";
            CameraTechnicalText.Text = "Modelo e firmware: —";
            ShowCameraInLiveBox.IsChecked = false;
            MirrorCameraDisplayBox.IsChecked = false;
            CameraQualityBox.SelectedIndex = 0;
            CameraChannelCountBox.SelectedIndex = 0;
            return;
        }

        var device = (CloudApi.AccountDevice)DeviceBox.SelectedItem;
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(
            device, Math.Max(0, DeviceBox.SelectedIndex));
        CameraNameBox.Text = device.Alias;
        CameraGroupBox.Text = entry.Group;
        CameraUserBox.Text = device.DeviceUser;
        CameraPasswordBox.Clear();
        CameraSerialText.Text = $"Serial protegido: {device.MaskedCloudId}";
        string profileSummary = deviceProfiles.BuildTechnicalSummary(device.CloudId);
        string[] details = new[]
        {
            string.IsNullOrWhiteSpace(profileSummary) ? null : profileSummary,
            string.IsNullOrWhiteSpace(profileSummary) && !string.IsNullOrWhiteSpace(device.Model)
                ? "Modelo informado: " + device.Model : null,
            string.IsNullOrWhiteSpace(profileSummary) && !string.IsNullOrWhiteSpace(device.Firmware)
                ? "Firmware: " + device.Firmware : null,
            string.IsNullOrWhiteSpace(profileSummary) && !string.IsNullOrWhiteSpace(device.ProductId)
                ? "PID: " + device.ProductId : null,
            entry.ChannelCountOverride is 1 or 2
                ? $"Canais configurados: {entry.ChannelCountOverride.Value}"
                : entry.DetectedChannelCount is 1 or 2
                    ? $"Canais detectados automaticamente: {entry.DetectedChannelCount.Value}"
                : entry.KnownChannels.Count == 0
                ? "Canais: serão detectados no primeiro teste"
                : "Canais detectados: " + string.Join(", ", entry.KnownChannels.Select(channel => channel + 1)),
            device.IsNetworkDevice ? "Transporte: rede local" : "Transporte: Cloud P2P"
        }.Where(value => value is not null).Select(value => value!).ToArray();
        CameraTechnicalText.Text = details.Length == 0
            ? "Modelo e firmware não informados pelo serviço da conta."
            : string.Join("  •  ", details);
        ShowCameraInLiveBox.IsChecked = entry.ShowInLiveView;
        MirrorCameraDisplayBox.IsChecked = entry.MirrorDisplay;
        CameraQualityBox.SelectedIndex = entry.PreferredSd switch
        {
            true => 1,
            false => 2,
            _ => 0
        };
        CameraChannelCountBox.SelectedIndex = entry.ChannelCountOverride switch
        {
            1 => 1,
            2 => 2,
            _ => 0
        };
        LoadSmartControls(device);
    }

    private void UseAccountLogin_Click(object sender, RoutedEventArgs e)
    {
        AccountBox.Focus();
        AccountBox.ScrollToHome();
    }

    private async void ReadAccountQr_Click(object sender, RoutedEventArgs e) => await RefreshQrAsync();

    private void AddDeviceQr_Click(object sender, RoutedEventArgs e)
    {
        string clipboard = string.Empty;
        try { if (System.Windows.Clipboard.ContainsText()) clipboard = System.Windows.Clipboard.GetText().Trim(); }
        catch { }
        Match serialMatch = Regex.Match(clipboard, @"(?<![A-Za-z0-9])[A-Za-z0-9_-]{12,64}(?![A-Za-z0-9])");
        string suggested = serialMatch.Success ? serialMatch.Value : clipboard;

        var dialog = new Window
        {
            Title = "Adicionar pelo QR da câmera", Owner = this, Width = 450, Height = 530,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 25, 40))
        };
        var form = new StackPanel { Margin = new Thickness(24) };
        static TextBlock Label(string value) => new()
        {
            Text = value, Foreground = System.Windows.Media.Brushes.White,
            Margin = new Thickness(0, 7, 0, 4)
        };
        var qrContent = new System.Windows.Controls.TextBox { Text = suggested, Height = 72, TextWrapping = TextWrapping.Wrap };
        var name = new System.Windows.Controls.TextBox { Text = "Câmera" };
        var user = new System.Windows.Controls.TextBox { Text = "admin" };
        var password = new PasswordBox();
        var group = new System.Windows.Controls.TextBox { Text = "Casa" };
        form.Children.Add(Label("Conteúdo do QR ou Cloud ID")); form.Children.Add(qrContent);
        var readImage = new System.Windows.Controls.Button
        {
            Content = "Ler QR de uma imagem...", Margin = new Thickness(0, 7, 0, 0)
        };
        readImage.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Escolha a imagem do QR da câmera",
                Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Todos os arquivos|*.*"
            };
            if (picker.ShowDialog(dialog) != true) return;
            try
            {
                string decoded = QrImageDecoder.Decode(picker.FileName);
                if (string.IsNullOrWhiteSpace(decoded))
                    throw new InvalidOperationException("Nenhum QR foi reconhecido nessa imagem.");
                qrContent.Text = decoded;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(dialog, ex.Message, "Leitura do QR",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        form.Children.Add(readImage);
        form.Children.Add(new TextBlock
        {
            Text = "Copie o conteúdo lido pelo celular ou digite o serial mostrado junto ao QR.",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 162, 188)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5)
        });
        form.Children.Add(Label("Nome")); form.Children.Add(name);
        form.Children.Add(Label("Usuário do dispositivo")); form.Children.Add(user);
        form.Children.Add(Label("Senha do dispositivo")); form.Children.Add(password);
        form.Children.Add(Label("Grupo / ambiente")); form.Children.Add(group);
        var save = new System.Windows.Controls.Button { Content = "ADICIONAR E TESTAR", Height = 44, IsDefault = true, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancelar", Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(qrContent.Text) || string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(user.Text))
            {
                System.Windows.MessageBox.Show(dialog, "Informe o QR/serial, o nome e o usuário.");
                return;
            }
            dialog.DialogResult = true;
        };
        form.Children.Add(save); form.Children.Add(cancel); dialog.Content = form;
        if (dialog.ShowDialog() != true) return;

        string raw = qrContent.Text.Trim();
        Match extracted = Regex.Match(raw, @"(?<![A-Za-z0-9])[A-Za-z0-9_-]{12,64}(?![A-Za-z0-9])");
        string cloudId = extracted.Success ? extracted.Value : raw;
        AddManualCloudDevice(cloudId, name.Text.Trim(), user.Text.Trim(), password.Password,
            string.IsNullOrWhiteSpace(group.Text) ? "Casa" : group.Text.Trim(), "QR da câmera");
        password.Clear();
    }

    private void AddNetworkDevice_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Adicionar por IP / rede", Owner = this, Width = 430, Height = 565,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 25, 40))
        };
        var form = new StackPanel { Margin = new Thickness(24) };
        static TextBlock Label(string value) => new()
        {
            Text = value, Foreground = System.Windows.Media.Brushes.White,
            Margin = new Thickness(0, 7, 0, 4)
        };
        var name = new System.Windows.Controls.TextBox();
        var address = new System.Windows.Controls.TextBox();
        var port = new System.Windows.Controls.TextBox { Text = "34567" };
        var user = new System.Windows.Controls.TextBox { Text = "admin" };
        var password = new PasswordBox();
        var group = new System.Windows.Controls.TextBox { Text = "Casa" };
        form.Children.Add(Label("Nome da câmera")); form.Children.Add(name);
        form.Children.Add(Label("Endereço IP")); form.Children.Add(address);
        form.Children.Add(Label("Porta")); form.Children.Add(port);
        form.Children.Add(Label("Usuário do dispositivo")); form.Children.Add(user);
        form.Children.Add(Label("Senha do dispositivo")); form.Children.Add(password);
        form.Children.Add(Label("Grupo / ambiente")); form.Children.Add(group);
        form.Children.Add(new TextBlock
        {
            Text = "Use esta opção quando o computador alcança diretamente a câmera. A senha permanece somente na memória.",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 162, 188)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10)
        });
        var save = new System.Windows.Controls.Button { Content = "ADICIONAR E TESTAR", Height = 44, IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "Cancelar", Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) ||
                !System.Net.IPAddress.TryParse(address.Text.Trim(), out _) ||
                !int.TryParse(port.Text, out int parsedPort) || parsedPort is < 1 or > 65535 ||
                string.IsNullOrWhiteSpace(user.Text))
            {
                System.Windows.MessageBox.Show(dialog, "Informe nome, IP válido, porta e usuário.",
                    "Cadastro", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            dialog.DialogResult = true;
        };
        form.Children.Add(save); form.Children.Add(cancel); dialog.Content = form;
        if (dialog.ShowDialog() != true) return;

        int networkPort = int.Parse(port.Text);
        var device = new CloudApi.AccountDevice
        {
            CloudId = address.Text.Trim(), Alias = name.Text.Trim(),
            DeviceUser = user.Text.Trim(), DevicePassword = password.Password,
            IsShared = false, IsNetworkDevice = true, NetworkPort = networkPort,
            LocalGroup = string.IsNullOrWhiteSpace(group.Text) ? "Casa" : group.Text.Trim()
        };
        if (accountDevices.Any(item => string.Equals(item.CloudId, device.CloudId, StringComparison.Ordinal)))
        {
            CameraSaveStatusText.Text = "Este endereço já está na lista.";
            password.Clear();
            return;
        }

        int result = CmsSdk.CMS_Client_AddDeviceByIP(
            device.CloudId, networkPort, device.DeviceUser, device.DevicePassword,
            0, device.Alias, cloudGroupId > 0 ? cloudGroupId : ushort.MaxValue,
            1, 0, 0);
        password.Clear();
        if (result <= 0)
        {
            CameraSaveStatusText.Text = "Não foi possível cadastrar a câmera pela rede. Confira endereço, porta e credenciais.";
            Log($"Cadastro por IP recusado: porta {networkPort}; retorno {result}.");
            return;
        }
        accountDevices.Add(device);
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, accountDevices.Count - 1);
        entry.Group = device.LocalGroup; entry.Name = device.Alias; entry.UseCustomName = true;
        entry.IsManual = true; entry.IsNetworkDevice = true; entry.Identifier = device.CloudId;
        entry.NetworkPort = device.NetworkPort; entry.DeviceUser = device.DeviceUser;
        SaveCameraCatalog();
        DeviceBox.ItemsSource = null; DeviceBox.ItemsSource = accountDevices;
        DeviceBox.IsEnabled = true; DeviceBox.SelectedItem = device;
        SetGridButtonsEnabled(true); UpdateCameraSummary();
        CameraSaveStatusText.Text = "Câmera de rede cadastrada. Use Testar conexão para confirmar.";
        Log($"Cadastro manual por IP concluído: porta {networkPort}; ID técnico {result}.");
    }

    private void AddCloudId_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Adicionar por Cloud ID",
            Owner = this,
            Width = 430,
            Height = 510,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(13, 25, 40))
        };
        var form = new StackPanel { Margin = new Thickness(24) };
        static TextBlock Label(string value) => new()
        {
            Text = value, Foreground = System.Windows.Media.Brushes.White,
            Margin = new Thickness(0, 7, 0, 4)
        };
        var name = new System.Windows.Controls.TextBox();
        var cloudId = new System.Windows.Controls.TextBox();
        var user = new System.Windows.Controls.TextBox { Text = "admin" };
        var password = new PasswordBox();
        var group = new System.Windows.Controls.TextBox { Text = "Casa" };
        form.Children.Add(Label("Nome da câmera")); form.Children.Add(name);
        form.Children.Add(Label("Cloud ID / serial")); form.Children.Add(cloudId);
        form.Children.Add(Label("Usuário do dispositivo")); form.Children.Add(user);
        form.Children.Add(Label("Senha do dispositivo")); form.Children.Add(password);
        form.Children.Add(Label("Grupo / ambiente")); form.Children.Add(group);
        form.Children.Add(new TextBlock
        {
            Text = "A senha é usada somente nesta sessão e não será salva.",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 162, 188)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10)
        });
        var save = new System.Windows.Controls.Button { Content = "ADICIONAR E TESTAR", Height = 44, IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "Cancelar", Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(cloudId.Text) ||
                string.IsNullOrWhiteSpace(user.Text))
            {
                System.Windows.MessageBox.Show(dialog, "Preencha nome, Cloud ID e usuário.",
                    "Cadastro", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            dialog.DialogResult = true;
        };
        form.Children.Add(save); form.Children.Add(cancel); dialog.Content = form;
        if (dialog.ShowDialog() != true) return;

        var device = new CloudApi.AccountDevice
        {
            CloudId = cloudId.Text.Trim(),
            Alias = name.Text.Trim(),
            DeviceUser = user.Text.Trim(),
            DevicePassword = password.Password,
            IsShared = false,
            LocalGroup = string.IsNullOrWhiteSpace(group.Text) ? "Casa" : group.Text.Trim()
        };
        AddManualCloudDevice(device.CloudId, device.Alias, device.DeviceUser,
            device.DevicePassword, device.LocalGroup, "Cloud ID");
        password.Clear();
    }

    private void AddManualCloudDevice(string cloudId, string name, string user, string password, string group, string source)
    {
        if (accountDevices.Any(item => string.Equals(item.CloudId, cloudId, StringComparison.Ordinal)))
        {
            CameraSaveStatusText.Text = "Este Cloud ID já está na lista.";
            return;
        }
        var device = new CloudApi.AccountDevice
        {
            CloudId = cloudId, Alias = name, DeviceUser = user, DevicePassword = password,
            IsShared = false, LocalGroup = group
        };
        accountDevices.Add(device);
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, accountDevices.Count - 1);
        entry.Group = group; entry.Name = name; entry.UseCustomName = true;
        entry.IsManual = true; entry.IsNetworkDevice = false; entry.Identifier = cloudId;
        entry.DeviceUser = user;
        SaveCameraCatalog();
        (int synchronized, int failed) = SynchronizeAccountDevicesToCms([device]);
        DeviceBox.ItemsSource = null; DeviceBox.ItemsSource = accountDevices;
        DeviceBox.IsEnabled = true; DeviceBox.SelectedItem = device;
        SetGridButtonsEnabled(true); UpdateCameraSummary();
        CameraSaveStatusText.Text = synchronized > 0
            ? "Câmera cadastrada nesta sessão. Teste a conexão para confirmar."
            : "O motor recusou o cadastro. Confira Cloud ID, usuário e senha.";
        Log($"Cadastro manual por {source}: sincronizados {synchronized}; falhas {failed}.");
    }

    private async void TestSelectedCamera_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not CloudApi.AccountDevice) return;
        CameraSaveStatusText.Text = "Testando conexão e vídeo...";
        await OpenGridAsync(1);
        CameraSaveStatusText.Text = previewBindings.Count > 0
            ? "Teste iniciado. Consulte o estado no monitor Ao vivo."
            : "A câmera não iniciou o teste de vídeo.";
    }

    private void RedetectSelectedCameraChannels_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not CloudApi.AccountDevice device)
        {
            CameraSaveStatusText.Text = "Selecione uma câmera para revalidar os canais.";
            return;
        }
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
        if (entry.ChannelCountOverride is 1 or 2)
        {
            CameraSaveStatusText.Text = "Selecione Detectar automaticamente e salve antes de revalidar.";
            return;
        }
        if (!cameraCatalog.ResetInconclusiveChannelDetection(device))
        {
            CameraSaveStatusText.Text = entry.SecondaryChannelConfirmedEver || entry.KnownChannels.Contains(1)
                ? "O canal 2 já possui confirmação histórica e não será removido."
                : "A detecção já está pronta para o próximo teste.";
            return;
        }
        SaveCameraCatalog();
        RefreshDeviceProfilesFromKnownData();
        DeviceBox_SelectionChanged(DeviceBox, null!);
        CameraSaveStatusText.Text = "Detecção liberada. Abra novamente a grade para validar o canal 2 uma única vez.";
        Log($"Redetecção explícita de canais liberada para o dispositivo selecionado; nenhuma chamada enviada agora.");
    }

    private void ShowSelectedDeviceSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not CloudApi.AccountDevice device)
        {
            System.Windows.MessageBox.Show(this, "Selecione uma câmera.",
                "Configurações da câmera", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowFunctionalDeviceSettings(device);
    }

    private void ShowFriendlyDeviceSettings(CloudApi.AccountDevice device)
    {
        RefreshDeviceProfilesFromKnownData();
        deviceProfiles.Devices.TryGetValue(device.CloudId, out DeviceProfileStore.Profile? profile);
        var dialog = new Window
        {
            Owner = this,
            Title = $"Configurações — {device.Alias}",
            Width = 1080,
            Height = 720,
            MinWidth = 860,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(8, 20, 33))
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(26, 22, 26, 16) };
        var deviceTitle = new TextBlock
        {
            Text = device.Alias,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold
        };
        header.Children.Add(deviceTitle);
        header.Children.Add(new TextBlock
        {
            Text = "Configurações individuais desta câmera",
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(142, 162, 188)),
            FontSize = 14,
            Margin = new Thickness(0, 4, 0, 0)
        });
        root.Children.Add(header);

        var status = new TextBlock
        {
            Text = "Selecione uma categoria.",
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(142, 162, 188)),
            Margin = new Thickness(26, 12, 26, 18),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(status, 2);
        root.Children.Add(status);

        var tabs = new System.Windows.Controls.TabControl
        {
            TabStripPlacement = Dock.Left,
            Margin = new Thickness(18, 0, 18, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        dialog.Content = root;

        bool Confirmed(string configurationKey) =>
            profile?.CompatibleCommands.TryGetValue(
                configurationKey, out DeviceProfileStore.ConfigurationBinding? binding) == true &&
            binding.Supported == true;

        PreviewBinding? OnlineBinding() => previewBindings.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.CloudId, device.CloudId, StringComparison.Ordinal) &&
            confirmedPreviewWindows.ContainsKey(candidate.Window));

        static TextBlock PageTitle(string text) => new()
        {
            Text = text,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        };

        static System.Windows.Controls.Button ActionButton(string text) => new()
        {
            Content = text,
            Padding = new Thickness(14, 8, 14, 8),
            MinWidth = 130,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        static System.Windows.Controls.Primitives.ToggleButton Toggle(
            bool value, bool enabled = true) => new()
        {
            IsChecked = value,
            IsEnabled = enabled,
            Content = value ? "Ligado" : "Desligado",
            MinWidth = 104,
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        static string MaskIdentifier(string value)
        {
            value = value.Trim();
            if (value.Length <= 4)
                return "••••";
            if (value.Length <= 8)
                return value[..2] + "••••" + value[^2..];
            return value[..4] + "••••" + value[^4..];
        }

        static Border Card(string title, string description, string state,
            UIElement? action = null, bool available = true)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (action is not null)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold
            });
            text.Children.Add(new TextBlock
            {
                Text = description,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(142, 162, 188)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 16, 0)
            });
            text.Children.Add(new TextBlock
            {
                Text = state,
                Foreground = new System.Windows.Media.SolidColorBrush(available
                    ? System.Windows.Media.Color.FromRgb(75, 222, 128)
                    : System.Windows.Media.Color.FromRgb(142, 162, 188)),
                Margin = new Thickness(0, 8, 0, 0)
            });
            grid.Children.Add(text);
            if (action is not null)
            {
                Grid.SetColumn(action, 1);
                grid.Children.Add(action);
            }
            return new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(14, 31, 49)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(38, 58, 80)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18),
                Margin = new Thickness(0, 0, 0, 12),
                Child = grid
            };
        }

        static StackPanel Page(string title)
        {
            var panel = new StackPanel { Margin = new Thickness(24, 8, 24, 24) };
            panel.Children.Add(PageTitle(title));
            return panel;
        }

        static TabItem Category(string title, StackPanel page) => new()
        {
            Header = title,
            Padding = new Thickness(18, 12, 18, 12),
            MinWidth = 205,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = page
            }
        };

        void RebuildPages(int selectedIndex = -1)
        {
            if (selectedIndex < 0)
                selectedIndex = tabs.SelectedIndex;
            tabs.Items.Clear();
            CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
            int? channels = profile?.ConfirmedChannelCount ?? entry.DetectedChannelCount;

            StackPanel basics = Page("Configuração básica");
            var renameButton = ActionButton("Alterar");
            renameButton.Click += (_, _) =>
            {
                var renameDialog = new Window
                {
                    Owner = dialog,
                    Title = "Nome da câmera",
                    Width = 440,
                    Height = 220,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(8, 20, 33))
                };
                var renamePanel = new StackPanel { Margin = new Thickness(22) };
                renamePanel.Children.Add(new TextBlock
                {
                    Text = "Nome exibido no monitor",
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold
                });
                var nameBox = new System.Windows.Controls.TextBox
                {
                    Text = device.Alias,
                    MaxLength = 64,
                    Margin = new Thickness(0, 12, 0, 16),
                    Padding = new Thickness(9, 7, 9, 7)
                };
                var renameActions = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right
                };
                renameActions.Children.Add(new System.Windows.Controls.Button
                {
                    Content = "Cancelar", IsCancel = true, Padding = new Thickness(15, 7, 15, 7)
                });
                var saveName = new System.Windows.Controls.Button
                {
                    Content = "Salvar", IsDefault = true, Padding = new Thickness(15, 7, 15, 7),
                    Margin = new Thickness(8, 0, 0, 0)
                };
                saveName.Click += (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                    {
                        nameBox.Focus();
                        return;
                    }
                    renameDialog.DialogResult = true;
                };
                renameActions.Children.Add(saveName);
                renamePanel.Children.Add(nameBox);
                renamePanel.Children.Add(renameActions);
                renameDialog.Content = renamePanel;
                nameBox.SelectAll();
                nameBox.Focus();
                if (renameDialog.ShowDialog() != true)
                    return;

                string name = nameBox.Text.Trim();
                CameraCatalogStore.Entry selectedEntry = cameraCatalog.GetOrCreate(
                    device, Math.Max(0, DeviceBox.SelectedIndex));
                device.Alias = name;
                selectedEntry.Name = name;
                selectedEntry.UseCustomName = !string.Equals(
                    name, selectedEntry.OnlineName, StringComparison.CurrentCultureIgnoreCase);
                SaveCameraCatalog();
                DeviceBox.Items.Refresh();
                if (DeviceBox.SelectedItem == device)
                    CameraNameBox.Text = name;
                RefreshPreviewNames(device);
                UpdateCameraSummary();
                deviceTitle.Text = name;
                dialog.Title = $"Configurações — {name}";
                status.Text = "Nome salvo e aplicado à grade, aos títulos e às gravações deste monitor.";
                RebuildPages(tabs.SelectedIndex);
            };
            basics.Children.Add(Card("Nome do dispositivo",
                "Altera o nome usado em toda a interface deste monitor.", device.Alias,
                renameButton));

            CameraCatalogStore.Entry basicEntry = cameraCatalog.GetOrCreate(device, int.MaxValue);
            var mirrorToggle = Toggle(basicEntry.MirrorDisplay);
            mirrorToggle.Click += (_, _) =>
            {
                bool enabled = mirrorToggle.IsChecked == true;
                basicEntry.MirrorDisplay = enabled;
                SaveCameraCatalog();
                foreach (PreviewBinding binding in previewBindings.Values.Where(binding =>
                             string.Equals(binding.CloudId, device.CloudId, StringComparison.Ordinal)))
                    ApplySavedMirror(binding);
                mirrorToggle.Content = enabled ? "Ligado" : "Desligado";
                status.Text = enabled
                    ? "Espelhamento aplicado às imagens desta câmera no monitor."
                    : "Espelhamento removido das imagens desta câmera no monitor.";
            };
            basics.Children.Add(Card("Girar a imagem para esquerda-direita",
                "Espelha imediatamente a imagem desta câmera neste monitor.",
                basicEntry.MirrorDisplay ? "Ligado" : "Desligado", mirrorToggle));

            if (Confirmed("Audio.SpeakerVolume"))
                basics.Children.Add(Card("Volume do alto-falante",
                    "Mesmo ajuste apresentado pelo aplicativo móvel.",
                    "Disponível — leitura detalhada necessária antes de alterar"));
            if (Confirmed("Network.Ntp"))
                basics.Children.Add(Card("Configuração de hora",
                    "Fuso horário, hora do dispositivo e sincronização.",
                    "Disponível — consulte em Sobre"));
            tabs.Items.Add(Category("Básicas", basics));

            DeviceReadOnlyConfigStore.DeviceData local = readOnlyDeviceConfigs.GetOrCreate(device.CloudId);
            StackPanel storage = Page("Armazenamento");
            var storageButton = ActionButton(local.Storage is null ? "Consultar" : "Atualizado");
            storageButton.IsEnabled = local.Storage is null;
            string storageState = local.Storage is null
                ? "Ainda não consultado"
                : local.Storage.Partitions.Count == 0
                    ? $"{local.Storage.DiskCount} unidade(s) informada(s)"
                    : $"{local.Storage.Partitions.Sum(item => item.TotalMegabytes) / 1024d:F1} GB — " +
                      $"{local.Storage.Partitions.Sum(item => item.FreeMegabytes) / 1024d:F1} GB livres";
            storage.Children.Add(Card("Cartão SD e capacidade",
                "Capacidade total e espaço livre informados pela câmera.",
                storageState, storageButton, local.Storage is not null));
            if (local.Storage is not null)
            {
                foreach (DeviceReadOnlyConfigStore.PartitionInfo partition in local.Storage.Partitions)
                    storage.Children.Add(Card("Partição de armazenamento",
                        string.IsNullOrWhiteSpace(partition.FileSystem)
                            ? "Partição informada pelo dispositivo."
                            : $"Sistema de arquivos: {partition.FileSystem}",
                        $"{partition.TotalMegabytes / 1024d:F1} GB no total — " +
                        $"{partition.FreeMegabytes / 1024d:F1} GB livres"));
                storage.Children.Add(Card("Armazenamento completo",
                    "O comportamento de sobrescrita só será alterado após leitura e validação do payload.",
                    "Somente consulta nesta versão"));
            }
            storageButton.Click += async (_, _) =>
            {
                PreviewBinding? online = OnlineBinding();
                if (online is null)
                {
                    status.Text = "A câmera precisa estar com imagem online para consultar o armazenamento.";
                    return;
                }
                storageButton.IsEnabled = false;
                status.Text = "Consultando armazenamento uma única vez...";
                DeviceReadOnlyConfigStore.StorageInfo? result = await ReadStorageOnDemandAsync(device, online);
                status.Text = result is null
                    ? "A câmera não retornou dados válidos. A consulta não será repetida automaticamente."
                    : "Armazenamento atualizado.";
                RebuildPages(tabs.SelectedIndex);
            };
            tabs.Items.Add(Category("Armazenamento", storage));

            StackPanel recording = Page("Gravação");
            PreviewBinding? currentOnline = OnlineBinding();
            DeviceReadOnlyConfigStore.RecordingInfo? recordingInfo = currentOnline is not null &&
                local.RecordingByChannel.TryGetValue(currentOnline.Channel, out DeviceReadOnlyConfigStore.RecordingInfo? rec)
                ? rec
                : local.RecordingByChannel.Values.FirstOrDefault();
            var recordingButton = ActionButton(recordingInfo is null ? "Consultar" : "Atualizado");
            recordingButton.IsEnabled = recordingInfo is null;
            string recordingMode = recordingInfo?.RecordModeCode switch
            {
                0 => "Gravação agendada",
                1 => "Gravação manual",
                2 => "Sem gravação",
                _ => recordingInfo is null ? "" : $"Modo {recordingInfo.RecordModeCode}"
            };
            string recordingState = recordingInfo is null
                ? "Ainda não consultado"
                : $"{recordingMode} — arquivos de {recordingInfo.PacketLengthMinutes} min";
            recording.Children.Add(Card("Configurações de gravação",
                "Modo, destino e duração dos arquivos gravados pela câmera.",
                recordingState, recordingButton, recordingInfo is not null));
            if (recordingInfo is not null)
            {
                recording.Children.Add(Card("Duração da gravação",
                    "Duração atual de cada arquivo de vídeo.",
                    $"{recordingInfo.PacketLengthMinutes} minuto(s)"));
                recording.Children.Add(Card("Destino da gravação",
                    "Armazenamento selecionado pelo dispositivo.",
                    recordingInfo.UsesSd ? "Cartão SD" : recordingInfo.UsesSata
                        ? "Disco interno" : recordingInfo.UsesUsb ? "USB" : "Não identificado"));
            }
            recordingButton.Click += async (_, _) =>
            {
                PreviewBinding? online = OnlineBinding();
                if (online is null)
                {
                    status.Text = "A câmera precisa estar com imagem online para consultar a gravação.";
                    return;
                }
                recordingButton.IsEnabled = false;
                status.Text = "Consultando configuração de gravação uma única vez...";
                DeviceReadOnlyConfigStore.RecordingInfo? result = await ReadRecordingOnDemandAsync(device, online);
                status.Text = result is null
                    ? "A câmera não retornou dados válidos. A consulta não será repetida automaticamente."
                    : "Configuração de gravação atualizada.";
                RebuildPages(tabs.SelectedIndex);
            };
            tabs.Items.Add(Category("Gravação", recording));

            void AddFeaturePage(string category, params (string Key, string Title, string Description)[] features)
            {
                var supported = features.Where(feature =>
                    DeviceSettingsPresentationPolicy.IsCustomerFacingConfiguration(feature.Key) &&
                    Confirmed(feature.Key)).ToArray();
                if (supported.Length == 0)
                    return;
                StackPanel page = Page(category);
                foreach (var feature in supported)
                    page.Children.Add(Card(feature.Title, feature.Description,
                        "Disponível nesta câmera — configuração detalhada ainda protegida"));
                tabs.Items.Add(Category(category, page));
            }

            AddFeaturePage("Alarme inteligente",
                ("Alarm.Motion", "Detecção de movimento", "Detecção de atividade na imagem."),
                ("Alarm.Human", "Detecção de pessoa", "Reconhecimento de presença humana."),
                ("Alarm.Pir", "Sensor PIR", "Sensor físico de presença, quando disponível."),
                ("Tracking.Motion", "Rastreamento", "Acompanhamento automático de movimento."));

            if (Confirmed("Light.White") || Confirmed("Alarm.VoiceType") || Confirmed("Audio.SpeakerVolume"))
            {
                StackPanel alarm = Page("Alarme sonoro e luminoso");
                if (Confirmed("Light.White"))
                {
                    currentOnline = OnlineBinding();
                    DeviceReadOnlyConfigStore.LightInfo? light = currentOnline is not null &&
                        local.LightByChannel.TryGetValue(currentOnline.Channel, out DeviceReadOnlyConfigStore.LightInfo? value)
                        ? value
                        : null;
                    var lightAction = ActionButton(light is null ? "Carregar" : "Alterar nível");
                    lightAction.Click += async (_, _) =>
                    {
                        PreviewBinding? online = OnlineBinding();
                        if (online is null)
                        {
                            status.Text = "A câmera precisa estar com imagem online.";
                            return;
                        }
                        if (!readOnlyDeviceConfigs.GetOrCreate(device.CloudId).LightByChannel.TryGetValue(
                                online.Channel, out DeviceReadOnlyConfigStore.LightInfo? loaded))
                        {
                            lightAction.IsEnabled = false;
                            status.Text = "Carregando iluminação uma única vez...";
                            loaded = await ReadCameraLightOnDemandAsync(device, online);
                            status.Text = loaded is null
                                ? "A câmera não retornou uma configuração de iluminação válida."
                                : "Iluminação carregada.";
                            RebuildPages(tabs.SelectedIndex);
                            return;
                        }
                        ConfigurationWriteResult result = await PromptAndSetLightLevelAsync(
                            dialog, device, online, loaded);
                        status.Text = result.Message;
                        RebuildPages(tabs.SelectedIndex);
                    };
                    alarm.Children.Add(Card("Modo de controle da luz",
                        "Consulta o modo, a duração e a intensidade da luz desta câmera.",
                        light is null ? "Carregue os valores atuais antes de alterar" :
                            $"{light.WorkMode} — nível {light.Level} — {light.DurationSeconds}s",
                        lightAction, light is not null));
                }
                if (Confirmed("Alarm.VoiceType"))
                    alarm.Children.Add(Card("Aviso sonoro", "Som emitido pela câmera durante um alerta.",
                        "Disponível — alteração ainda protegida"));
                tabs.Items.Add(Category("Som e luz", alarm));
            }

            AddFeaturePage("Áudio",
                ("Audio.SpeakerVolume", "Volume do alto-falante", "Volume de reprodução e avisos."),
                ("Audio.MicrophoneVolume", "Volume do microfone", "Nível de captura de áudio."));
            AddFeaturePage("Rede",
                ("Network.Wifi", "Configurações de Wi‑Fi", "Modo de rede e rede sem fio utilizada pelo dispositivo."));
            AddFeaturePage("Avançadas",
                ("Tracking.Motion", "Rastreamento de movimento", "Ativação e sensibilidade do rastreamento."),
                ("Camera.Parameters", "Imagem e WDR", "Orientação, visão diurna/noturna e compensação WDR."));

            StackPanel about = Page("Sobre");
            about.Children.Add(Card("Modelo", "Modelo informado pelo dispositivo ou provedor.",
                string.IsNullOrWhiteSpace(profile?.ReportedModel) ? "Não informado" : profile.ReportedModel,
                available: !string.IsNullOrWhiteSpace(profile?.ReportedModel)));
            about.Children.Add(Card("Firmware", "Versão instalada na câmera.",
                string.IsNullOrWhiteSpace(profile?.Firmware) ? "Não informado" : profile.Firmware,
                available: !string.IsNullOrWhiteSpace(profile?.Firmware)));
            about.Children.Add(Card("Identificador do dispositivo",
                "Identificador mascarado para evitar exposição acidental.",
                MaskIdentifier(device.CloudId)));
            about.Children.Add(Card("Canais de vídeo",
                "Quantidade confirmada por imagens realmente recebidas.",
                channels is int count ? $"{count} canal(is)" : "Ainda não identificado",
                available: channels is not null));
            tabs.Items.Add(Category("Sobre", about));

            tabs.SelectedIndex = Math.Clamp(selectedIndex, 0, tabs.Items.Count - 1);
        }

        RebuildPages(0);
        dialog.ShowDialog();
    }

    private async Task<ConfigurationWriteResult> PromptAndSetLightLevelAsync(
        Window owner, CloudApi.AccountDevice device, PreviewBinding online,
        DeviceReadOnlyConfigStore.LightInfo currentLight)
    {
        var prompt = new Window
        {
            Owner = owner,
            Title = "Nível da luz branca",
            Width = 430,
            Height = 245,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(8, 20, 33))
        };
        var panel = new StackPanel { Margin = new Thickness(22) };
        var valueText = new TextBlock
        {
            Text = $"Nível: {currentLight.Level}",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 18,
            Margin = new Thickness(0, 0, 0, 10)
        };
        var slider = new System.Windows.Controls.Slider
        {
            Minimum = 0, Maximum = 100, Value = currentLight.Level,
            TickFrequency = 5, IsSnapToTickEnabled = true
        };
        slider.ValueChanged += (_, _) => valueText.Text = $"Nível: {(int)slider.Value}";
        panel.Children.Add(valueText);
        panel.Children.Add(slider);
        panel.Children.Add(new TextBlock
        {
            Text = "A alteração será enviada uma vez e confirmada pela própria câmera.",
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(142, 162, 188)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 12)
        });
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        buttons.Children.Add(new System.Windows.Controls.Button
        {
            Content = "Cancelar", IsCancel = true, Padding = new Thickness(15, 7, 15, 7)
        });
        var apply = new System.Windows.Controls.Button
        {
            Content = "Aplicar", IsDefault = true, Padding = new Thickness(15, 7, 15, 7),
            Margin = new Thickness(8, 0, 0, 0)
        };
        apply.Click += (_, _) => prompt.DialogResult = true;
        buttons.Children.Add(apply);
        panel.Children.Add(buttons);
        prompt.Content = panel;
        if (prompt.ShowDialog() != true)
            return new(false, false, "Alteração cancelada.");
        int requested = (int)slider.Value;
        if (requested == currentLight.Level)
            return new(true, false, "O nível não foi alterado.");
        if (System.Windows.MessageBox.Show(owner,
                $"Alterar a luz de {currentLight.Level} para {requested}?",
                "Confirmar alteração", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return new(false, false, "Alteração cancelada.");
        return await SetCameraLightLevelControlledAsync(device, online, requested);
    }

    private void ShowSelectedDeviceSettingsLegacy(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not CloudApi.AccountDevice device)
        {
            System.Windows.MessageBox.Show(this, "Selecione uma câmera.",
                "Configurações da câmera", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RefreshDeviceProfilesFromKnownData();
        deviceProfiles.Devices.TryGetValue(device.CloudId, out DeviceProfileStore.Profile? profile);
        DeviceProfileStore.CapabilitySnapshot CapabilitySnapshot(params string[] keys) =>
            deviceProfiles.GetCapability(device.CloudId, keys);
        static string CapabilityText(DeviceProfileStore.CapabilitySnapshot snapshot) => snapshot.State switch
        {
            DeviceProfileStore.CapabilityState.Available => "Disponível",
            DeviceProfileStore.CapabilityState.Unavailable => "Não disponível",
            _ => "A identificar"
        };
        static string FriendlySource(string source) => string.IsNullOrWhiteSpace(source)
            ? "Nenhuma evidência recebida"
            : source.Replace("SystemFunction:", "Resposta da câmera:", StringComparison.Ordinal)
                .Replace("SystemFunction + preview confirmado", "Resposta da câmera e imagem confirmada", StringComparison.Ordinal)
                .Replace("SystemFunction", "Resposta da câmera", StringComparison.Ordinal);
        static string ObservedText(DateTime? observedAtUtc) => observedAtUtc is DateTime observed
            ? observed.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
            : "—";
        string Channels()
        {
            CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
            int? count = profile?.ConfirmedChannelCount ?? entry.DetectedChannelCount;
            return count is int value ? $"{value} confirmado(s)" : "A identificar";
        }
        DeviceSettingRow Row(
            string section, string setting, string support, string dataState,
            string source = "Dados já recebidos", DateTime? observedAtUtc = null) => new()
        {
            Section = section,
            Setting = setting,
            Support = support,
            DataState = dataState,
            Source = source,
            Observed = ObservedText(observedAtUtc)
        };
        DeviceProfileStore.CapabilitySnapshot doubleLightSnapshot =
            CapabilitySnapshot("SupportDoubleLightCamera");
        string doubleLight = CapabilityText(doubleLightSnapshot);
        PreviewBinding? online = previewBindings.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.CloudId, device.CloudId, StringComparison.Ordinal) &&
            confirmedPreviewWindows.ContainsKey(candidate.Window));

        DeviceSettingRow[] BuildRows()
        {
            DeviceReadOnlyConfigStore.DeviceData local = readOnlyDeviceConfigs.GetOrCreate(device.CloudId);
            DeviceReadOnlyConfigStore.RecordingInfo? recording = online is not null &&
                local.RecordingByChannel.TryGetValue(online.Channel, out DeviceReadOnlyConfigStore.RecordingInfo? channelRecording)
                ? channelRecording
                : local.RecordingByChannel.Values.OrderBy(item => item.Channel).FirstOrDefault();
            DeviceReadOnlyConfigStore.LightInfo? light = online is not null &&
                local.LightByChannel.TryGetValue(online.Channel, out DeviceReadOnlyConfigStore.LightInfo? channelLight)
                ? channelLight
                : local.LightByChannel.Values.OrderBy(item => item.Channel).FirstOrDefault();
            string storageSummary = local.Storage is null
                ? "Ainda não lido; disponível sob demanda"
                : local.Storage.Partitions.Count == 0
                    ? $"{local.Storage.DiskCount} disco(s); sem partição utilizável informada"
                    : $"{local.Storage.DiskCount} disco(s); " +
                      $"{local.Storage.Partitions.Sum(item => item.TotalMegabytes) / 1024d:F1} GB; " +
                      $"{local.Storage.Partitions.Sum(item => item.FreeMegabytes) / 1024d:F1} GB livres";
            string recordingSummary;
            if (recording is null)
            {
                recordingSummary = "Ainda não lido; disponível sob demanda";
            }
            else
            {
                string[] targets =
                [
                    recording.UsesSata ? "SATA" : string.Empty,
                    recording.UsesUsb ? "USB" : string.Empty,
                    recording.UsesSd ? "SD" : string.Empty,
                    recording.UsesDvd ? "DVD" : string.Empty
                ];
                string targetText = string.Join(", ", targets.Where(value => value.Length > 0));
                if (targetText.Length == 0)
                    targetText = "não informado";
                recordingSummary = $"Canal {recording.Channel + 1}; modo {recording.RecordModeCode}; " +
                    $"pré-gravação {recording.PreRecordSeconds}s; arquivo {recording.PacketLengthMinutes}min; " +
                    $"{recording.EnabledSchedulePeriods} período(s); destino {targetText}";
            }
            string lightSummary = light is null
                ? doubleLightSnapshot.State == DeviceProfileStore.CapabilityState.Available
                    ? "Compatibilidade confirmada; detalhes não foram consultados"
                    : doubleLight
                : $"Canal {light.Channel + 1}; modo {light.WorkMode}; nível {light.Level}; " +
                  $"duração {light.DurationSeconds}s; agenda {(light.ScheduleEnabled ? "ativa" : "inativa")}";

            var rows = new List<DeviceSettingRow>
            {
                Row("Básicas", "Canais", Channels(), "Dado local confirmado por imagem",
                    "Catálogo local de canais"),
                Row("Básicas", "Modelo e firmware", profile?.Firmware.Length > 0 || profile?.ReportedModel.Length > 0
                    ? "Informado" : "Não informado pelo provedor", "Dado recebido durante o cadastro",
                    "Conta/CMS", profile?.UpdatedAtUtc),
            };
            rows.Add(Row("Rede", "Transporte da câmera", "Cloud P2P",
                "Senhas e chaves de rede não são consultadas", "Cadastro local"));

            Dictionary<string, DeviceProfileStore.ConfigurationBinding> bindings =
                profile?.CompatibleCommands ?? new(StringComparer.Ordinal);
            foreach (DeviceConfigurationCatalog.Definition definition in
                     DeviceConfigurationCatalog.Definitions
                         .OrderBy(item => item.Section).ThenBy(item => item.Label))
            {
                if (!bindings.TryGetValue(definition.Key, out DeviceProfileStore.ConfigurationBinding? binding) ||
                    !DeviceSettingsPresentationPolicy.ShowConfirmed(binding))
                    continue;

                string dataState = definition.Access == DeviceConfigurationCatalog.AccessMode.ReadOnly
                    ? "Leitura confirmada"
                    : "Recurso confirmado; alterações ainda desabilitadas";
                DateTime? observed = binding.ObservedAtUtc;
                if (definition.Key == "Storage.Info")
                {
                    dataState = storageSummary;
                    observed = local.Storage?.ObservedAtUtc ?? observed;
                }
                else if (definition.Key == "Recording.Main")
                {
                    dataState = recordingSummary;
                    observed = recording?.ObservedAtUtc ?? observed;
                }
                else if (definition.Key == "Light.White")
                {
                    dataState = lightSummary;
                    observed = light?.ObservedAtUtc ?? observed;
                }
                rows.Add(Row(definition.Section, definition.Label, "Disponível",
                    dataState, FriendlySource(binding.Evidence), observed));
            }

            // Estas duas sondagens seguras precisam continuar visíveis antes
            // da confirmação; caso contrário não haveria como solicitá-las.
            if (DeviceSettingsPresentationPolicy.OfferSafeProbe("Storage.Info") &&
                (!bindings.TryGetValue("Storage.Info", out DeviceProfileStore.ConfigurationBinding? storageBinding) ||
                 !DeviceSettingsPresentationPolicy.ShowConfirmed(storageBinding)))
                rows.Add(Row("Armazenamento", "Discos e cartão SD", "Leitura segura disponível",
                    storageSummary, "Ainda não consultado"));
            if (DeviceSettingsPresentationPolicy.OfferSafeProbe("Recording.Main") &&
                (!bindings.TryGetValue("Recording.Main", out DeviceProfileStore.ConfigurationBinding? recordingBinding) ||
                 !DeviceSettingsPresentationPolicy.ShowConfirmed(recordingBinding)))
                rows.Add(Row("Gravação", "Gravação principal", "Leitura segura disponível",
                    recordingSummary, "Ainda não consultado"));
            return rows.ToArray();
        }

        DeviceSettingRow[] initialRows = BuildRows();
        var sectionFilter = new System.Windows.Controls.ComboBox
        {
            MinWidth = 220,
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            ItemsSource = new[] { "Todas as seções" }
                .Concat(initialRows.Select(item => item.Section).Distinct(StringComparer.Ordinal))
                .ToArray(),
            SelectedIndex = 0
        };
        DeviceSettingRow[] FilteredRows()
        {
            DeviceSettingRow[] rows = BuildRows();
            string selectedSection = sectionFilter.SelectedItem?.ToString() ?? "Todas as seções";
            return selectedSection == "Todas as seções"
                ? rows
                : rows.Where(item => item.Section == selectedSection).ToArray();
        }

        var table = new DataGrid
        {
            ItemsSource = initialRows,
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(7, 17, 30)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 58, 80)),
            RowBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 21, 35)),
            AlternatingRowBackground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13, 28, 45)),
            HorizontalGridLinesBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 58, 80)),
            VerticalGridLinesBrush = System.Windows.Media.Brushes.Transparent
        };
        static void AddSettingColumn(DataGrid grid, string header, string property, double width) =>
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new System.Windows.Data.Binding(property),
                Width = new DataGridLength(width)
            });
        AddSettingColumn(table, "Seção", nameof(DeviceSettingRow.Section), 145);
        AddSettingColumn(table, "Configuração", nameof(DeviceSettingRow.Setting), 235);
        AddSettingColumn(table, "Compatibilidade", nameof(DeviceSettingRow.Support), 190);
        AddSettingColumn(table, "Estado dos dados", nameof(DeviceSettingRow.DataState), 300);
        AddSettingColumn(table, "Origem", nameof(DeviceSettingRow.Source), 260);
        AddSettingColumn(table, "Confirmado em", nameof(DeviceSettingRow.Observed), 135);

        sectionFilter.SelectionChanged += (_, _) => table.ItemsSource = FilteredRows();

        var layout = new Grid { Margin = new Thickness(20) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        title.Children.Add(new TextBlock
        {
            Text = $"Configurações — {device.Alias}",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        title.Children.Add(new TextBlock
        {
            Text = "São exibidos apenas dados recebidos e recursos confirmados para esta câmera. " +
                "As leituras detalhadas são feitas sob demanda; abrir esta tela não consulta o dispositivo.",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 162, 188)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        int confirmedConfigurations = profile?.CompatibleCommands.Count(item => item.Value.Supported == true) ?? 0;
        int unavailableConfigurations = profile?.CompatibleCommands.Count(item => item.Value.Supported == false) ?? 0;
        int pendingConfigurations = Math.Max(0,
            DeviceConfigurationCatalog.Definitions.Length - confirmedConfigurations - unavailableConfigurations);
        title.Children.Add(new TextBlock
        {
            Text = $"Perfil individual: {confirmedConfigurations} confirmado(s), " +
                $"{unavailableConfigurations} incompatível(is), {pendingConfigurations} ainda não identificado(s).",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(104, 211, 145)),
            Margin = new Thickness(0, 5, 0, 0)
        });
        layout.Children.Add(title);
        var tableArea = new Grid();
        tableArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        tableArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tableArea.Children.Add(sectionFilter);
        Grid.SetRow(table, 1);
        tableArea.Children.Add(table);
        Grid.SetRow(tableArea, 1);
        layout.Children.Add(tableArea);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var status = new TextBlock
        {
            Text = "Nenhuma leitura é feita automaticamente nesta tela.",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 162, 188)),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(status, Dock.Left);
        footer.Children.Add(status);
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var storageButton = new System.Windows.Controls.Button
        {
            Content = "Carregar armazenamento",
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(8, 0, 0, 0)
        };
        var recordingButton = new System.Windows.Controls.Button
        {
            Content = "Carregar gravação",
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(8, 0, 0, 0)
        };
        var lightButton = new System.Windows.Controls.Button
        {
            Content = "Carregar iluminação",
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = doubleLight == "Disponível" ||
                readOnlyDeviceConfigs.GetOrCreate(device.CloudId).LightByChannel.Count > 0
        };
        var editLightButton = new System.Windows.Controls.Button
        {
            Content = "Alterar nível da luz",
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(8, 0, 0, 0),
            IsEnabled = false
        };
        actions.Children.Add(storageButton);
        actions.Children.Add(recordingButton);
        actions.Children.Add(lightButton);
        actions.Children.Add(editLightButton);
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        void RefreshRows()
        {
            table.ItemsSource = null;
            table.ItemsSource = FilteredRows();
        }

        bool PrepareRead()
        {
            online = previewBindings.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.CloudId, device.CloudId, StringComparison.Ordinal) &&
                confirmedPreviewWindows.ContainsKey(candidate.Window));
            if (online is null)
            {
                status.Text = "A câmera precisa estar com imagem online para fazer esta leitura.";
                return false;
            }
            if (!CanIssueDeviceRequest(online.DeviceId, out TimeSpan wait, out _, out _))
            {
                status.Text = $"Leitura protegida: aguarde {Math.Max(1, Math.Ceiling(wait.TotalMinutes))} minuto(s).";
                return false;
            }
            storageButton.IsEnabled = false;
            recordingButton.IsEnabled = false;
            lightButton.IsEnabled = false;
            editLightButton.IsEnabled = false;
            return true;
        }

        void RestoreReadButtons()
        {
            DeviceReadOnlyConfigStore.DeviceData local = readOnlyDeviceConfigs.GetOrCreate(device.CloudId);
            int channel = online?.Channel ?? -1;
            storageButton.Content = local.Storage is null ? "Carregar armazenamento" : "Armazenamento carregado";
            storageButton.IsEnabled = local.Storage is null &&
                !attemptedDetailedConfigReads.ContainsKey((device.CloudId, "Storage", -1));
            bool recordingLoaded = channel >= 0 && local.RecordingByChannel.ContainsKey(channel);
            recordingButton.Content = recordingLoaded ? "Gravação carregada" : "Carregar gravação";
            recordingButton.IsEnabled = !recordingLoaded && (channel < 0 ||
                !attemptedDetailedConfigReads.ContainsKey((device.CloudId, "Recording", channel)));
            bool lightLoaded = channel >= 0 && local.LightByChannel.ContainsKey(channel);
            lightButton.Content = lightLoaded ? "Iluminação carregada" : "Carregar iluminação";
            lightButton.IsEnabled = !lightLoaded && doubleLight == "Disponível" && (channel < 0 ||
                !attemptedDetailedConfigReads.ContainsKey((device.CloudId, "Light", channel)));
            DeviceProfileStore.ConfigurationBinding? lightBinding = null;
            profile?.CompatibleCommands.TryGetValue("Light.White", out lightBinding);
            bool writeAllowed = channel >= 0 && lightLoaded &&
                DeviceConfigurationCatalog.Find("Light.White") is DeviceConfigurationCatalog.Definition lightDefinition &&
                DeviceConfigurationWritePolicy.CanWrite(
                    lightDefinition, lightBinding, true, DateTime.UtcNow, out _, out _);
            editLightButton.IsEnabled = writeAllowed;
        }

        RestoreReadButtons();

        storageButton.Click += async (_, _) =>
        {
            if (!PrepareRead() || online is null)
                return;
            status.Text = "Lendo armazenamento uma única vez...";
            DeviceReadOnlyConfigStore.StorageInfo? result = await ReadStorageOnDemandAsync(device, online);
            RefreshRows();
            status.Text = result is null
                ? "A câmera não retornou dados válidos. Nenhuma repetição automática será feita."
                : "Armazenamento carregado e mantido no cache local.";
            RestoreReadButtons();
        };
        recordingButton.Click += async (_, _) =>
        {
            if (!PrepareRead() || online is null)
                return;
            status.Text = $"Lendo gravação do canal {online.Channel + 1}, sem alterar a câmera...";
            DeviceReadOnlyConfigStore.RecordingInfo? result = await ReadRecordingOnDemandAsync(device, online);
            RefreshRows();
            status.Text = result is null
                ? "A câmera não retornou dados válidos. Nenhuma repetição automática será feita."
                : "Configuração de gravação carregada e mantida no cache local.";
            RestoreReadButtons();
        };
        lightButton.Click += async (_, _) =>
        {
            if (!PrepareRead() || online is null)
                return;
            status.Text = $"Lendo iluminação do canal {online.Channel + 1}, sem alterar a câmera...";
            DeviceReadOnlyConfigStore.LightInfo? result = await ReadCameraLightOnDemandAsync(device, online);
            RefreshRows();
            status.Text = result is null
                ? "A câmera não retornou dados válidos. Nenhuma repetição automática será feita."
                : "Configuração de iluminação carregada e mantida no cache local.";
            RestoreReadButtons();
        };
        editLightButton.Click += async (_, _) =>
        {
            online = previewBindings.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.CloudId, device.CloudId, StringComparison.Ordinal) &&
                confirmedPreviewWindows.ContainsKey(candidate.Window));
            if (online is null ||
                !readOnlyDeviceConfigs.GetOrCreate(device.CloudId).LightByChannel.TryGetValue(
                    online.Channel, out DeviceReadOnlyConfigStore.LightInfo? currentLight))
            {
                status.Text = "Carregue a iluminação com a câmera online antes de alterar.";
                RestoreReadButtons();
                return;
            }

            var levelDialog = new Window
            {
                Owner = this,
                Title = "Nível da luz branca",
                Width = 430,
                Height = 245,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 20, 33))
            };
            var levelPanel = new StackPanel { Margin = new Thickness(22) };
            var levelText = new TextBlock
            {
                Text = $"Nível: {currentLight.Level}",
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 18,
                Margin = new Thickness(0, 0, 0, 10)
            };
            var levelSlider = new System.Windows.Controls.Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = currentLight.Level,
                TickFrequency = 5,
                IsSnapToTickEnabled = true
            };
            levelSlider.ValueChanged += (_, _) => levelText.Text = $"Nível: {(int)levelSlider.Value}";
            var warning = new TextBlock
            {
                Text = "A alteração será enviada uma vez, relida e validada. Em divergência, o valor anterior será restaurado uma vez.",
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 162, 188)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 12)
            };
            var levelActions = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            var cancelLevel = new System.Windows.Controls.Button
            {
                Content = "Cancelar", IsCancel = true, Padding = new Thickness(15, 7, 15, 7)
            };
            var applyLevel = new System.Windows.Controls.Button
            {
                Content = "Continuar", IsDefault = true, Padding = new Thickness(15, 7, 15, 7),
                Margin = new Thickness(8, 0, 0, 0)
            };
            applyLevel.Click += (_, _) => levelDialog.DialogResult = true;
            levelActions.Children.Add(cancelLevel);
            levelActions.Children.Add(applyLevel);
            levelPanel.Children.Add(levelText);
            levelPanel.Children.Add(levelSlider);
            levelPanel.Children.Add(warning);
            levelPanel.Children.Add(levelActions);
            levelDialog.Content = levelPanel;
            if (levelDialog.ShowDialog() != true)
                return;

            int requestedLevel = (int)levelSlider.Value;
            if (requestedLevel == currentLight.Level)
            {
                status.Text = "O nível não foi alterado.";
                return;
            }
            if (System.Windows.MessageBox.Show(this,
                    $"Alterar o nível da luz de '{device.Alias}', canal {online.Channel + 1}, " +
                    $"de {currentLight.Level} para {requestedLevel}?\n\n" +
                    "Será enviada uma única alteração ao dispositivo.",
                    "Confirmar alteração remota", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            storageButton.IsEnabled = false;
            recordingButton.IsEnabled = false;
            lightButton.IsEnabled = false;
            editLightButton.IsEnabled = false;
            status.Text = "Alterando e validando a iluminação...";
            ConfigurationWriteResult result = await SetCameraLightLevelControlledAsync(
                device, online, requestedLevel);
            RefreshRows();
            status.Text = result.Message;
            RestoreReadButtons();
        };

        new Window
        {
            Owner = this,
            Title = "Configurações da câmera",
            Width = 1380,
            Height = 650,
            MinWidth = 980,
            MinHeight = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 20, 33)),
            Content = layout
        }.ShowDialog();
    }

    private void ShowCompatibilityTable_Click(object sender, RoutedEventArgs e)
    {
        RefreshDeviceProfilesFromKnownData();
        DeviceCompatibilityRow[] rows = accountDevices.Select(BuildCompatibilityRow).ToArray();
        var table = new DataGrid
        {
            ItemsSource = rows,
            IsReadOnly = true,
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserReorderColumns = true,
            CanUserResizeColumns = true,
            CanUserSortColumns = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(7, 17, 30)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(38, 58, 80)),
            RowBackground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(10, 21, 35)),
            AlternatingRowBackground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(13, 28, 45)),
            HorizontalGridLinesBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(38, 58, 80)),
            VerticalGridLinesBrush = System.Windows.Media.Brushes.Transparent,
            SelectionMode = DataGridSelectionMode.Single,
            SelectionUnit = DataGridSelectionUnit.FullRow
        };
        static void AddColumn(DataGrid grid, string header, string property, double width)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = header,
                Binding = new System.Windows.Data.Binding(property),
                Width = new DataGridLength(width)
            });
        }
        AddColumn(table, "Câmera", nameof(DeviceCompatibilityRow.Camera), 130);
        AddColumn(table, "Identificador", nameof(DeviceCompatibilityRow.Identifier), 125);
        AddColumn(table, "Tipo cloud", nameof(DeviceCompatibilityRow.Model), 120);
        AddColumn(table, "Firmware", nameof(DeviceCompatibilityRow.Firmware), 220);
        AddColumn(table, "Código interno", nameof(DeviceCompatibilityRow.InternalCode), 105);
        AddColumn(table, "Plataforma", nameof(DeviceCompatibilityRow.Platform), 170);
        AddColumn(table, "Canais", nameof(DeviceCompatibilityRow.Channels), 85);
        AddColumn(table, "PTZ", nameof(DeviceCompatibilityRow.Ptz), 95);
        AddColumn(table, "Pessoa", nameof(DeviceCompatibilityRow.HumanDetection), 95);
        AddColumn(table, "Luz dupla", nameof(DeviceCompatibilityRow.DoubleLight), 95);
        AddColumn(table, "Alarme sonoro", nameof(DeviceCompatibilityRow.AlarmSound), 115);
        AddColumn(table, "Movimento", nameof(DeviceCompatibilityRow.MotionDetection), 95);
        AddColumn(table, "Rastreamento", nameof(DeviceCompatibilityRow.MotionTracking), 105);
        AddColumn(table, "Favoritos PTZ", nameof(DeviceCompatibilityRow.PtzPresets), 105);
        AddColumn(table, "Falar", nameof(DeviceCompatibilityRow.TwoWayTalk), 85);
        AddColumn(table, "Wi-Fi", nameof(DeviceCompatibilityRow.Wifi), 85);
        AddColumn(table, "Upgrade cloud", nameof(DeviceCompatibilityRow.CloudUpgrade), 110);
        AddColumn(table, "Identificação", nameof(DeviceCompatibilityRow.Inventory), 150);
        AddColumn(table, "Atualizado", nameof(DeviceCompatibilityRow.Updated), 125);

        var content = new Grid { Margin = new Thickness(20) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock
        {
            Text = "Identificação e compatibilidade",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "A identificação ocorre automaticamente, uma vez, após a primeira imagem de cada câmera. '?' significa que a câmera ainda não conectou ou não informou o recurso. Abrir esta tela não envia consultas.",
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(142, 162, 188)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 0)
        });
        content.Children.Add(heading);
        Grid.SetRow(table, 1);
        content.Children.Add(table);

        var identifyButton = new System.Windows.Controls.Button
        {
            Content = "Atualizar tabela",
            Margin = new Thickness(0, 12, 12, 0),
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        var identifyStatus = new TextBlock
        {
            Text = "Mostrando somente dados locais já recebidos das câmeras.",
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(142, 162, 188)),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var footer = new DockPanel();
        footer.Children.Add(identifyButton);
        DockPanel.SetDock(identifyButton, Dock.Left);
        footer.Children.Add(identifyStatus);
        Grid.SetRow(footer, 2);
        content.Children.Add(footer);

        identifyButton.Click += (_, _) =>
        {
            RefreshDeviceProfilesFromKnownData();
            string? selectedKey = (table.SelectedItem as DeviceCompatibilityRow)?.DeviceKey;
            DeviceCompatibilityRow[] refreshedRows = accountDevices.Select(BuildCompatibilityRow).ToArray();
            table.ItemsSource = refreshedRows;
            if (selectedKey is not null)
                table.SelectedItem = refreshedRows.FirstOrDefault(row =>
                    string.Equals(row.DeviceKey, selectedKey, StringComparison.Ordinal));
            identifyStatus.Text = $"Tabela local atualizada às {DateTime.Now:HH:mm:ss}. Nenhuma consulta foi enviada.";
        };

        var dialog = new Window
        {
            Owner = this,
            Title = "Compatibilidade das câmeras",
            Width = 1380,
            Height = 650,
            MinWidth = 900,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(8, 20, 33)),
            Content = content
        };
        dialog.ShowDialog();
    }

    private DeviceCompatibilityRow BuildCompatibilityRow(CloudApi.AccountDevice device)
    {
        deviceProfiles.Devices.TryGetValue(device.CloudId, out DeviceProfileStore.Profile? profile);
        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, int.MaxValue);
        string Capability(string key) =>
            deviceProfiles.GetCapability(device.CloudId, key).State switch
            {
                DeviceProfileStore.CapabilityState.Available => "Sim",
                DeviceProfileStore.CapabilityState.Unavailable => "Não",
                _ => "?"
            };
        string AnyCapability(params string[] keys)
        {
            return deviceProfiles.GetCapability(device.CloudId, keys).State switch
            {
                DeviceProfileStore.CapabilityState.Available => "Sim",
                DeviceProfileStore.CapabilityState.Unavailable => "Não",
                _ => "?"
            };
        }
        int identifiedCapabilities = DeviceCapabilityCatalog.Definitions.Count(definition =>
            deviceProfiles.GetCapability(device.CloudId, definition.Key).State !=
                DeviceProfileStore.CapabilityState.Unknown);
        bool inventoryCompleted = deviceProfiles.GetCapability(
            device.CloudId, "Identification.SystemFunctionSchema3").State ==
                DeviceProfileStore.CapabilityState.Available;
        int? channels = profile?.ConfirmedChannelCount ??
            (entry.ChannelCountOverride is 1 or 2 ? entry.ChannelCountOverride : entry.DetectedChannelCount);
        return new DeviceCompatibilityRow
        {
            DeviceKey = device.CloudId,
            Camera = string.IsNullOrWhiteSpace(device.Alias) ? "Câmera" : device.Alias,
            Identifier = device.MaskedCloudId,
            Model = profile?.ReportedModel.Length > 0 ? profile.ReportedModel : "Não informado",
            Firmware = profile?.Firmware.Length > 0 ? profile.Firmware : "Não informado",
            InternalCode = profile?.FirmwareProductCode.Length > 0 ? profile.FirmwareProductCode : "?",
            Platform = profile?.ChipSolutionCode.Length > 0
                ? profile.ChipFamily.Length > 0
                    ? $"{profile.ChipFamily} ({profile.ChipSolutionCode})"
                    : $"Código {profile.ChipSolutionCode}"
                : "?",
            Channels = channels?.ToString() ?? "?",
            Ptz = AnyCapability("SupportPTZDirectionControl", "PTZ.Direction"),
            HumanDetection = Capability("SupportHumanDetection"),
            DoubleLight = Capability("SupportDoubleLightCamera"),
            AlarmSound = AnyCapability("SupportAlarmSound", "SupportDVRAlarmSound", "SupportIPCAlarmSound"),
            MotionDetection = Capability("SupportMotionDetection"),
            MotionTracking = Capability("SupportMotionTracking"),
            PtzPresets = Capability("SupportPtzPresets"),
            TwoWayTalk = Capability("SupportTwoWayTalk"),
            Wifi = Capability("SupportWifi"),
            CloudUpgrade = Capability("SupportCloudUpgradeConfig"),
            Inventory = inventoryCompleted
                ? $"Concluída ({identifiedCapabilities}/{DeviceCapabilityCatalog.Definitions.Length})"
                : identifiedCapabilities > 0
                    ? $"Parcial ({identifiedCapabilities}/{DeviceCapabilityCatalog.Definitions.Length})"
                    : "Aguardando imagem",
            Updated = profile is not null && profile.UpdatedAtUtc != default
                ? profile.UpdatedAtUtc.ToLocalTime().ToString("dd/MM HH:mm")
                : "—"
        };
    }

    private async void RemoveSelectedCamera_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not CloudApi.AccountDevice device) return;
        if (System.Windows.MessageBox.Show(this,
                $"Remover '{device.Alias}' apenas deste computador?\n\nA câmera não será desvinculada da conta iCSee.",
                "Remover câmera", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await DisconnectVideoAsync(log: false);
        try
        {
            CmsSdk.DeviceInfo info = default;
            if (GetCmsDeviceInfo(device.CloudId, ref info) > 0 && info.ID > 0)
                CmsSdk.CMS_Client_RemoveDevice(info.ID);
        }
        catch { }
        accountDevices.Remove(device);
        cameraCatalog.Cameras.Remove(device.CloudId);
        if (deviceProfiles.Remove(device.CloudId))
            SaveDeviceProfiles();
        cameraCatalog.NormalizeOrder(accountDevices);
        SaveCameraCatalog();
        DeviceBox.ItemsSource = null;
        DeviceBox.ItemsSource = accountDevices;
        DeviceBox.IsEnabled = accountDevices.Count > 0;
        if (accountDevices.Count > 0) DeviceBox.SelectedIndex = 0;
        UpdateCameraSummary();
        SetGridButtonsEnabled(accountDevices.Count > 0);
        CameraSaveStatusText.Text = "Cadastro local removido.";
    }

    private void SaveCameraProfile_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceBox.SelectedItem is not CloudApi.AccountDevice device)
            return;
        string name = CameraNameBox.Text.Trim();
        if (name.Length == 0)
        {
            CameraNameBox.Focus();
            return;
        }

        CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(
            device, Math.Max(0, DeviceBox.SelectedIndex));
        string previousUser = device.DeviceUser;
        bool credentialsChanged = !string.Equals(previousUser, CameraUserBox.Text.Trim(), StringComparison.Ordinal) ||
            CameraPasswordBox.Password.Length > 0;
        if (!string.IsNullOrWhiteSpace(CameraUserBox.Text))
            device.DeviceUser = CameraUserBox.Text.Trim();
        if (CameraPasswordBox.Password.Length > 0)
            device.DevicePassword = CameraPasswordBox.Password;
        device.Alias = name;
        entry.Name = name;
        entry.UseCustomName = !string.Equals(
            name, entry.OnlineName, StringComparison.CurrentCultureIgnoreCase);
        entry.Group = string.IsNullOrWhiteSpace(CameraGroupBox.Text)
            ? "Casa"
            : CameraGroupBox.Text.Trim();
        entry.ShowInLiveView = ShowCameraInLiveBox.IsChecked == true;
        entry.MirrorDisplay = MirrorCameraDisplayBox.IsChecked == true;
        entry.DeviceUser = device.DeviceUser;
        entry.PreferredSd = CameraQualityBox.SelectedIndex switch
        {
            1 => true,
            2 => false,
            _ => null
        };
        entry.ChannelCountOverride = CameraChannelCountBox.SelectedIndex switch
        {
            1 => 1,
            2 => 2,
            _ => null
        };
        device.LocalGroup = entry.Group;
        device.ShowInLiveView = entry.ShowInLiveView;
        if (credentialsChanged)
        {
            try
            {
                CmsSdk.DeviceInfo existing = default;
                if (GetCmsDeviceInfo(device.CloudId, ref existing) != 0 && existing.ID > 0)
                {
                    CmsSdk.CMS_Client_DeviceLoginOrLogout(existing.ID, false);
                    CmsSdk.CMS_Client_RemoveDevice(existing.ID);
                }
                int registered = RegisterDeviceToCms(device);
                CameraSaveStatusText.Text = registered > 0
                    ? "Credenciais atualizadas nesta sessão. Use Testar conexão para confirmar."
                    : "O motor recusou as novas credenciais.";
                Log($"Credenciais do dispositivo atualizadas sem registrar conteúdo: retorno {registered}.");
            }
            catch (Exception ex)
            {
                CameraSaveStatusText.Text = "Não foi possível aplicar as novas credenciais.";
                Log("Falha ao atualizar credenciais: " + SanitizeDiagnostic(ex.Message));
            }
            finally { CameraPasswordBox.Clear(); }
        }
        SaveCameraCatalog();
        DeviceBox.Items.Refresh();
        RefreshPreviewNames(device);
        foreach (PreviewBinding binding in previewBindings.Values.Where(binding =>
                     string.Equals(binding.CloudId, device.CloudId, StringComparison.Ordinal)))
            ApplySavedMirror(binding);
        UpdateCameraSummary();
        if (!credentialsChanged)
            CameraSaveStatusText.Text = entry.ShowInLiveView
                ? "Organização salva. A câmera será usada na próxima abertura da grade."
                : "Organização salva. A câmera ficará oculta na próxima abertura da grade.";
    }

    private void MoveCameraUp_Click(object sender, RoutedEventArgs e) => MoveSelectedCamera(-1);

    private void MoveCameraDown_Click(object sender, RoutedEventArgs e) => MoveSelectedCamera(1);

    private void MoveSelectedCamera(int direction)
    {
        if (DeviceBox.SelectedItem is not CloudApi.AccountDevice device)
            return;
        int current = accountDevices.IndexOf(device);
        int destination = current + direction;
        if (current < 0 || destination < 0 || destination >= accountDevices.Count)
            return;
        accountDevices.RemoveAt(current);
        accountDevices.Insert(destination, device);
        cameraCatalog.NormalizeOrder(accountDevices);
        SaveCameraCatalog();
        ApplyCameraOrderToLiveLayout();
        DeviceBox.ItemsSource = null;
        DeviceBox.ItemsSource = accountDevices;
        DeviceBox.SelectedIndex = destination;
    }

    private void ApplyCameraOrderToLiveLayout()
    {
        preferences.LiveLayoutOrder = accountDevices
            .SelectMany(device => new[]
            {
                PreviewOrderKey(device.CloudId, 0),
                PreviewOrderKey(device.CloudId, 1)
            })
            .ToList();
        SavePreferences();
        if (previewBindings.Count > 0)
            RestoreConfiguredVideoGrid();
    }

    private void ResetLiveOrder_Click(object sender, RoutedEventArgs e)
    {
        ApplyCameraOrderToLiveLayout();
        Log("Ordem da grade restaurada conforme a lista da tela Cameras.");
    }

    private void SaveCameraCatalog()
    {
        try { cameraCatalog.Save(); }
        catch (Exception ex) { Log("Nao foi possivel salvar a organizacao das cameras: " + ex.Message); }
    }

    private void RefreshPreviewNames(CloudApi.AccountDevice device)
    {
        foreach ((int window, PreviewBinding binding) in previewBindings.ToArray())
        {
            if (!string.Equals(binding.CloudId, device.CloudId, StringComparison.Ordinal))
                continue;
            PreviewBinding updated = binding with { DisplayName = device.Alias };
            previewBindings[window] = updated;
            SetPreviewStatus(
                updated,
                confirmedPreviewWindows.ContainsKey(window) ? "Online" : "Reconectando",
                confirmedPreviewWindows.ContainsKey(window)
                    ? System.Drawing.Color.LimeGreen
                    : System.Drawing.Color.Orange);
        }
    }

    private async void RefreshOnlineDevices_Click(object sender, RoutedEventArgs e) =>
        await RefreshOnlineDevicesAsync(true);

    private async Task RefreshOnlineDevicesAsync(bool userInitiated)
    {
        if (onlineRefreshBusy || isClosing ||
            !CloudSessionStore.TryLoad(out CloudSessionStore.Session? session) || session is null)
            return;

        onlineRefreshBusy = true;
        RefreshOnlineDevicesButton.IsEnabled = false;
        string? selectedCloudId = (DeviceBox.SelectedItem as CloudApi.AccountDevice)?.CloudId;
        try
        {
            IReadOnlyList<CloudApi.AccountDevice> fresh = await Task.Run(() =>
            {
                QrCloudApi.InitializeAppInfo(session.QrSecret, session.AppInfoEnc);
                return QrCloudApi.GetDevices(
                    session.AccessToken, session.LocalUser, session.LocalPassword);
            });

            var previousNames = accountDevices.ToDictionary(
                device => device.CloudId, device => device.Alias, StringComparer.Ordinal);
            accountDevices.Clear();
            accountDevices.AddRange(fresh);
            ApplyCameraCatalog();
            CloudApi.AccountDevice[] newlyLinked = accountDevices
                .Where(device => !previousNames.ContainsKey(device.CloudId))
                .ToArray();
            if (newlyLinked.Length > 0)
                SynchronizeAccountDevicesToCms(newlyLinked);
            DeviceBox.ItemsSource = null;
            DeviceBox.ItemsSource = accountDevices;
            DeviceBox.IsEnabled = accountDevices.Count > 0;
            SetGridButtonsEnabled(accountDevices.Count > 0);
            DeviceBox.SelectedItem = accountDevices.FirstOrDefault(
                device => string.Equals(device.CloudId, selectedCloudId, StringComparison.Ordinal));
            if (DeviceBox.SelectedItem is null && accountDevices.Count > 0)
                DeviceBox.SelectedIndex = 0;

            int renamed = 0;
            foreach (CloudApi.AccountDevice device in accountDevices)
            {
                if (previousNames.TryGetValue(device.CloudId, out string? oldName) &&
                    !string.Equals(oldName, device.Alias, StringComparison.CurrentCulture))
                    renamed++;
                RefreshPreviewNames(device);
            }
            UpdateCameraSummary();
            if (userInitiated || renamed > 0 || previousNames.Count != accountDevices.Count)
                Log($"Lista online atualizada: {accountDevices.Count} cameras; nomes alterados {renamed}.");
            if (userInitiated)
                CameraSaveStatusText.Text =
                    "Dados online atualizados. Apelidos locais foram preservados.";
        }
        catch (Exception ex)
        {
            if (userInitiated)
                Log("Nao foi possivel atualizar a lista online agora: " + SafeQrError(ex));
        }
        finally
        {
            onlineRefreshBusy = false;
            RefreshOnlineDevicesButton.IsEnabled = true;
        }
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        string page = (sender as System.Windows.Controls.Button)?.Tag?.ToString() ?? "Live";
        if (page != "Recordings" && recordingPlayer.IsOpen)
            StopLocalRecordingPlayer();
        LiveView.Visibility = page == "Live" ? Visibility.Visible : Visibility.Collapsed;
        CamerasView.Visibility = page == "Cameras" ? Visibility.Visible : Visibility.Collapsed;
        RecordingsView.Visibility = page == "Recordings" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        LiveHeaderControls.Visibility = page == "Live" ? Visibility.Visible : Visibility.Collapsed;
        PtzSidePanel.Visibility = page == "Live" ? Visibility.Visible : Visibility.Collapsed;
        bool english = string.Equals(preferences.Language, "en", StringComparison.OrdinalIgnoreCase);
        PageTitleText.Text = page switch
        {
            "Cameras" => english ? "Cameras" : "Câmeras",
            "Recordings" => english ? "Recordings" : "Gravações",
            "Settings" => english ? "Settings" : "Configurações",
            _ => english ? "Monitoring" : "Monitoramento"
        };
        if (page == "Recordings")
            RefreshCaptureList();

        UpdateNavigationColors();
    }

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        bool entering = WindowStyle != WindowStyle.None;
        WindowStyle = entering ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = entering ? WindowState.Maximized : WindowState.Normal;
    }

    private async void ReconnectAll_Click(object sender, RoutedEventArgs e)
    {
        if (!await manualReconnectCycleGate.WaitAsync(0))
        {
            SelectedCameraText.Text = "Um ciclo de reconexão já está em andamento";
            return;
        }

        bool cycleUsed = false;
        try
        {
            TimeSpan minimumInterval = TimeSpan.FromSeconds(
                Math.Max(60, preferences.ReconnectDelaySeconds));
            TimeSpan sinceLastCycle = DateTime.UtcNow - lastManualReconnectCycleUtc;
            if (lastManualReconnectCycleUtc != DateTime.MinValue && sinceLastCycle < minimumInterval)
            {
                TimeSpan remaining = minimumInterval - sinceLastCycle;
                string message = $"Aguarde {Math.Ceiling(remaining.TotalSeconds)} segundos para um novo ciclo.";
                SelectedCameraText.Text = message;
                Log($"Ciclo manual global suprimido por intervalo minimo; restante " +
                    $"{Math.Ceiling(remaining.TotalSeconds)}s. Nenhuma requisicao enviada.");
                return;
            }

            PreviewBinding[] failed = previewBindings.Values
                .Where(binding =>
                    binding.Generation == Volatile.Read(ref previewGeneration) &&
                    !confirmedPreviewWindows.ContainsKey(binding.Window))
                .OrderBy(binding => binding.Window)
                .ToArray();
            if (failed.Length == 0)
            {
                SelectedCameraText.Text = "Todas as câmeras da grade já estão online";
                Log("Ciclo manual global dispensado: nenhum fluxo offline; nenhuma requisicao enviada.");
                return;
            }
            // Uma avaliação manual de falhas também entra no intervalo global,
            // mesmo quando todas as chamadas forem suprimidas por proteção.
            cycleUsed = true;

            bool english = string.Equals(
                preferences.Language, "en", StringComparison.OrdinalIgnoreCase);
            ReconnectFailedButton.IsEnabled = false;
            int processed = 0;
            int recovered = 0;
            int protectedOrBusy = 0;
            int deviceLoginRequests = 0;
            Log($"Ciclo manual global iniciado: {failed.Length} fluxo(s) sem imagem; " +
                "somente falhas serao avaliadas em sequencia.");

            int[] disconnectedDevices = failed
                .Where(binding => disconnectedPreviewDevices.ContainsKey(binding.DeviceId))
                .Select(binding => binding.DeviceId)
                .Distinct()
                .ToArray();
            foreach (int disconnectedDevice in disconnectedDevices)
            {
                ReconnectFailedButton.Content = english
                    ? "↻  Checking P2P signal"
                    : "↻  Verificando sinal P2P";
                if (await TryRequestDisconnectedDeviceLoginAsync(
                        disconnectedDevice, manual: true))
                    deviceLoginRequests++;
            }

            foreach (PreviewBinding binding in failed)
            {
                if (isClosing)
                    break;
                if (binding.Generation != Volatile.Read(ref previewGeneration) ||
                    !previewBindings.TryGetValue(binding.Window, out PreviewBinding? current) ||
                    current != binding || confirmedPreviewWindows.ContainsKey(binding.Window))
                    continue;
                if (disconnectedPreviewDevices.ContainsKey(binding.DeviceId))
                {
                    protectedOrBusy++;
                    SetPreviewStatus(binding, "Aguardando sinal", System.Drawing.Color.Orange);
                    Log($"Ciclo manual: dispositivo {binding.DeviceId} ainda sem sessao P2P; " +
                        $"canal {binding.Channel} ignorado; nenhuma requisicao enviada.");
                    continue;
                }
                bool deviceAlreadyRecovering = previewBindings.Values.Any(candidate =>
                    candidate.DeviceId == binding.DeviceId &&
                    recoveringPreviewWindows.ContainsKey(candidate.Window));
                if (deviceAlreadyRecovering)
                {
                    protectedOrBusy++;
                    Log($"Ciclo manual: dispositivo {binding.DeviceId} ja possui recuperacao em andamento; " +
                        $"canal {binding.Channel} ignorado para evitar chamadas concorrentes.");
                    continue;
                }
                if (!CanIssueDeviceRequest(
                        binding.DeviceId, out TimeSpan wait,
                        out _, out int lastError))
                {
                    protectedOrBusy++;
                    SetPreviewStatus(binding, DeviceBlockStatus(binding.DeviceId), System.Drawing.Color.IndianRed);
                    Log($"Ciclo manual: dispositivo {binding.DeviceId}; canal {binding.Channel}; " +
                        $"protegido apos erro {lastError}; restante {Math.Ceiling(wait.TotalSeconds)}s; " +
                        "nenhuma requisicao enviada.");
                    continue;
                }

                processed++;
                ReconnectFailedButton.Content = english
                    ? $"↻  Reconnecting {processed}/{failed.Length}"
                    : $"↻  Reconectando {processed}/{failed.Length}";
                Log($"Ciclo manual: tentando dispositivo {binding.DeviceId}; canal {binding.Channel}; " +
                    $"janela {binding.Window}; item {processed}/{failed.Length}.");
                await RecoverPreviewAsync(binding, forceOneAttempt: true);
                if (confirmedPreviewWindows.ContainsKey(binding.Window))
                    recovered++;

                if (!isClosing && processed < failed.Length)
                    await Task.Delay(ConnectionRecoveryPolicy.PreviewSpacing);
            }

            SelectedCameraText.Text = processed == 0 && deviceLoginRequests > 0
                ? $"Sinal P2P solicitado para {deviceLoginRequests} dispositivo(s); aguardando confirmação"
                : processed == 0
                ? $"{protectedOrBusy} câmera(s) aguardando proteção ou já reconectando"
                : $"Ciclo concluído: {recovered} recuperada(s) em {processed} tentativa(s)";
            Log($"Ciclo manual global concluido: candidatas {failed.Length}; tentadas {processed}; " +
                $"logins unicos de dispositivo {deviceLoginRequests}; recuperadas {recovered}; " +
                $"protegidas ou ocupadas {protectedOrBusy}.");
        }
        catch (Exception ex)
        {
            Log($"Falha no ciclo manual global: {ex.GetType().Name}: {ex.Message}");
            SelectedCameraText.Text = "Não foi possível concluir o ciclo de reconexão";
        }
        finally
        {
            if (cycleUsed)
                lastManualReconnectCycleUtc = DateTime.UtcNow;
            ReconnectFailedButton.IsEnabled = true;
            ReconnectAllButtonText(string.Equals(
                preferences.Language, "en", StringComparison.OrdinalIgnoreCase));
            manualReconnectCycleGate.Release();
        }
    }

    private void PreferenceChanged_Click(object sender, RoutedEventArgs e)
    {
        if (!preferencesReady)
            return;
        preferences.RestoreLastLayout = RestoreLayoutBox.IsChecked == true;
        preferences.AutoReconnect = AutoReconnectBox.IsChecked == true;
        SavePreferences();
    }

    private void PreferenceChanged_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!preferencesReady)
            return;
        if (ReferenceEquals(sender, DefaultQualityBox))
        {
            preferences.DefaultSd = DefaultQualityBox.SelectedIndex != 1;
            SubstreamBox.IsChecked = preferences.DefaultSd;
        }
        else if (ConnectionTimeoutBox.SelectedItem is ComboBoxItem timeoutItem &&
                 int.TryParse(timeoutItem.Tag?.ToString(), out int timeoutSeconds))
            preferences.ConnectionTimeoutSeconds = timeoutSeconds;
        SavePreferences();
    }

    private void StartWithWindowsChanged_Click(object sender, RoutedEventArgs e)
    {
        if (!preferencesReady) return;
        bool enabled = StartWithWindowsBox.IsChecked == true;
        try
        {
            using Microsoft.Win32.RegistryKey? run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run is null) throw new InvalidOperationException("A chave de inicializacao nao esta disponivel.");
            if (enabled)
                run.SetValue("iCSeeXMEyeMonitor", $"\"{Environment.ProcessPath}\"");
            else
                run.DeleteValue("iCSeeXMEyeMonitor", throwOnMissingValue: false);
            preferences.StartWithWindows = enabled;
            SavePreferences();
        }
        catch (Exception ex)
        {
            StartWithWindowsBox.IsChecked = preferences.StartWithWindows;
            System.Windows.MessageBox.Show("Nao foi possivel alterar a inicializacao do Windows.\n\n" + ex.Message,
                "Configuracoes", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StorageLimitChanged_Click(object sender, SelectionChangedEventArgs e)
    {
        if (!preferencesReady || StorageLimitBox.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int limit)) return;
        preferences.StorageLimitGb = limit;
        SavePreferences();
        _ = Task.Run(EnforceRecordingStorageLimit);
    }

    private void ReconnectDelayChanged_Click(object sender, SelectionChangedEventArgs e)
    {
        if (!preferencesReady || ReconnectDelayBox.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int seconds))
            return;
        preferences.ReconnectDelaySeconds = seconds;
        SavePreferences();
    }

    private void LanguageChanged_Click(object sender, SelectionChangedEventArgs e)
    {
        if (!preferencesReady || LanguageBox.SelectedItem is not ComboBoxItem item)
            return;
        preferences.Language = item.Tag?.ToString() ?? "pt-BR";
        ApplyLanguage(preferences.Language);
        SavePreferences();
    }

    private void ApplyLanguage(string language)
    {
        bool english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        LiveNavButton.Content = english ? "▣   Live" : "▣   Ao vivo";
        CamerasNavButton.Content = english ? "▤   Cameras" : "▤   Câmeras";
        RecordingsNavButton.Content = english ? "▶   Recordings" : "▶   Gravações";
        SettingsNavButton.Content = english ? "⚙   Settings" : "⚙   Configurações";
        DisconnectButton.Content = english ? "Disconnect" : "Desconectar";
        ReconnectAllButtonText(english);
        if (LiveView.Visibility == Visibility.Visible)
            PageTitleText.Text = english ? "Monitoring" : "Monitoramento";
        else if (CamerasView.Visibility == Visibility.Visible)
            PageTitleText.Text = english ? "Cameras" : "Câmeras";
        else if (RecordingsView.Visibility == Visibility.Visible)
            PageTitleText.Text = english ? "Recordings" : "Gravações";
        else
            PageTitleText.Text = english ? "Settings" : "Configurações";
        TranslateVisibleUi(english);
    }

    private void TranslateVisibleUi(bool english)
    {
        foreach (TextBlock text in FindVisualChildren<TextBlock>(this))
            text.Text = TranslateUiText(text.Text, english);
        foreach (ContentControl control in FindVisualChildren<ContentControl>(this))
            if (control.Content is string content)
                control.Content = TranslateUiText(content, english);
        foreach (FrameworkElement element in FindVisualChildren<FrameworkElement>(this))
            if (element.ToolTip is string tooltip)
                element.ToolTip = TranslateUiText(tooltip, english);
    }

    private static string TranslateUiText(string value, bool english)
    {
        (string Pt, string En)[] translations =
        [
            ("Ao vivo", "Live"), ("Câmeras", "Cameras"), ("Gravações", "Recordings"),
            ("Configurações", "Settings"), ("Monitoramento", "Monitoring"),
            ("Ordem das câmeras", "Camera order"), ("Tela cheia", "Fullscreen"),
            ("Áudio", "Audio"), ("Capturar", "Capture"), ("Gravar", "Record"),
            ("Falar", "Talk"), ("Janela", "Window"), ("Reconectar", "Reconnect"),
            ("Desconectar", "Disconnect"), ("Qualidade", "Quality"),
            ("Adicionar câmeras", "Add cameras"), ("Conta iCSee", "iCSee account"),
            ("Ler QR", "Read QR"), ("QR da câmera", "Camera QR"),
            ("Câmeras cadastradas", "Registered cameras"), ("Todos os grupos", "All groups"),
            ("Cadastro rápido", "Quick setup"), ("Grupo / ambiente", "Group / room"),
            ("Usuário do dispositivo", "Device user"), ("Nova senha (opcional)", "New password (optional)"),
            ("Mostrar esta câmera no monitor", "Show this camera in live view"),
            ("Espelhar a exibição neste computador", "Mirror display on this computer"),
            ("Qualidade preferida desta câmera", "Preferred quality for this camera"),
            ("Testar conexão", "Test connection"), ("Remover localmente", "Remove locally"),
            ("Salvar", "Save"), ("Sair da conta", "Sign out"),
            ("Biblioteca local", "Local library"), ("Vídeos", "Videos"), ("Imagens", "Images"),
            ("Abrir", "Open"), ("Selecione uma gravação", "Select a recording"),
            ("Reproduzir", "Play"), ("Parar", "Stop"),
            ("Restaurar último layout", "Restore last layout"),
            ("Reconexão ativa de canais isolados", "Active reconnection for isolated channels"),
            ("Iniciar junto com o Windows", "Start with Windows"),
            ("Qualidade padrão", "Default quality"),
            ("Tempo máximo para montar a grade", "Maximum grid connection time"),
            ("Intervalo de reconexão", "Reconnection interval"),
            ("Idioma", "Language"), ("Tema", "Theme"), ("Escuro", "Dark"), ("Claro", "Light"),
            ("Pasta de fotos", "Photos folder"), ("Pasta de gravações", "Recordings folder"),
            ("Limite para gravações locais", "Local recordings limit"),
            ("Diagnóstico técnico", "Technical diagnostics"), ("Copiar", "Copy"),
            ("Exportar diagnóstico", "Export diagnostics"),
            ("Verificar atualização", "Check for updates")
        ];
        foreach ((string pt, string en) in translations)
        {
            if (english && string.Equals(value, pt, StringComparison.OrdinalIgnoreCase)) return en;
            if (!english && string.Equals(value, en, StringComparison.OrdinalIgnoreCase)) return pt;
            if (english && value.Contains(pt, StringComparison.Ordinal)) return value.Replace(pt, en, StringComparison.Ordinal);
            if (!english && value.Contains(en, StringComparison.Ordinal)) return value.Replace(en, pt, StringComparison.Ordinal);
        }
        return value;
    }

    private void ReconnectAllButtonText(bool english)
    {
        ReconnectFailedButton.Content = english
            ? "↻  Reconnect failures"
            : "↻  Reconectar falhas";
    }

    private void ThemeChanged_Click(object sender, SelectionChangedEventArgs e)
    {
        if (!preferencesReady || ThemeBox.SelectedItem is not ComboBoxItem item)
            return;
        preferences.Theme = item.Tag?.ToString() ?? "Dark";
        ApplyTheme(preferences.Theme);
        SavePreferences();
    }

    private void ApplyTheme(string theme)
    {
        bool light = string.Equals(theme, "Light", StringComparison.OrdinalIgnoreCase);
        Background = new System.Windows.Media.SolidColorBrush(light
            ? System.Windows.Media.Color.FromRgb(232, 238, 246)
            : System.Windows.Media.Color.FromRgb(7, 17, 30));
        if (Resources["Panel"] is System.Windows.Media.SolidColorBrush panel && !panel.IsFrozen)
            panel.Color = light ? System.Windows.Media.Color.FromRgb(248, 250, 253) : System.Windows.Media.Color.FromRgb(13, 25, 40);
        if (Resources["PanelAlt"] is System.Windows.Media.SolidColorBrush alt && !alt.IsFrozen)
            alt.Color = light ? System.Windows.Media.Color.FromRgb(235, 241, 248) : System.Windows.Media.Color.FromRgb(17, 31, 49);
        if (Resources["Text"] is System.Windows.Media.SolidColorBrush text && !text.IsFrozen)
            text.Color = light ? System.Windows.Media.Color.FromRgb(20, 34, 52) : System.Windows.Media.Color.FromRgb(243, 247, 252);
        if (Resources["Muted"] is System.Windows.Media.SolidColorBrush muted && !muted.IsFrozen)
            muted.Color = light ? System.Windows.Media.Color.FromRgb(75, 94, 117) : System.Windows.Media.Color.FromRgb(142, 162, 188);
        SidebarBorder.Background = ThemeBrush(light, 241, 246, 251, 8, 20, 33);
        HeaderBorder.Background = ThemeBrush(light, 250, 252, 255, 9, 21, 34);
        SidebarBorder.BorderBrush = ThemeBrush(light, 205, 216, 229, 26, 43, 61);
        HeaderBorder.BorderBrush = ThemeBrush(light, 205, 216, 229, 27, 44, 64);
        foreach (TextBlock block in FindVisualChildren<TextBlock>(this))
        {
            if (block.Foreground is System.Windows.Media.SolidColorBrush brush &&
                IsUiNeutral(brush.Color))
                block.Foreground = light
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 50, 72))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 232, 247));
        }
        foreach (System.Windows.Controls.Button button in FindVisualChildren<System.Windows.Controls.Button>(this))
        {
            if (button.Background is System.Windows.Media.SolidColorBrush brush && IsDarkControl(brush.Color))
            {
                button.Background = light
                    ? ThemeBrush(true, 229, 237, 247, 0, 0, 0)
                    : ThemeBrush(false, 0, 0, 0, 23, 42, 66);
                button.Foreground = light
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 52, 78))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(234, 241, 250));
            }
        }
        UpdateNavigationColors();
    }

    private static System.Windows.Media.SolidColorBrush ThemeBrush(
        bool light, byte lr, byte lg, byte lb, byte dr, byte dg, byte db) =>
        new(light
            ? System.Windows.Media.Color.FromRgb(lr, lg, lb)
            : System.Windows.Media.Color.FromRgb(dr, dg, db));

    private static bool IsUiNeutral(System.Windows.Media.Color color) =>
        color == System.Windows.Media.Color.FromRgb(255, 255, 255) ||
        color == System.Windows.Media.Color.FromRgb(220, 232, 247) ||
        color == System.Windows.Media.Color.FromRgb(202, 214, 232) ||
        color == System.Windows.Media.Color.FromRgb(232, 240, 250) ||
        color == System.Windows.Media.Color.FromRgb(32, 50, 72);

    private static bool IsDarkControl(System.Windows.Media.Color color) =>
        color == System.Windows.Media.Color.FromRgb(23, 42, 66) ||
        color == System.Windows.Media.Color.FromRgb(14, 28, 44) ||
        color == System.Windows.Media.Color.FromRgb(229, 237, 247);

    private void UpdateNavigationColors()
    {
        string activePage = LiveView.Visibility == Visibility.Visible ? "Live" :
            CamerasView.Visibility == Visibility.Visible ? "Cameras" :
            RecordingsView.Visibility == Visibility.Visible ? "Recordings" : "Settings";
        bool light = string.Equals(preferences.Theme, "Light", StringComparison.OrdinalIgnoreCase);
        foreach (System.Windows.Controls.Button button in
                 new[] { LiveNavButton, CamerasNavButton, RecordingsNavButton, SettingsNavButton })
        {
            bool active = Equals(button.Tag?.ToString(), activePage);
            button.Background = active
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 86, 145))
                : light
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 237, 247))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 28, 44));
            button.Foreground = active || !light
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 52, 78));
            button.BorderThickness = active ? new Thickness(3, 0, 0, 0) : new Thickness(0);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            System.Windows.DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (T nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }

    private void EnforceRecordingStorageLimit()
    {
        int limitGb = preferences.StorageLimitGb;
        if (limitGb <= 0) return;
        try
        {
            string folder = preferences.GetRecordingFolder();
            if (!Directory.Exists(folder)) return;
            long limit = limitGb * 1024L * 1024L * 1024L;
            FileInfo[] recordings = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(file => !IsPhotoExtension(file.Extension))
                .OrderBy(file => file.LastWriteTimeUtc).ToArray();
            long total = recordings.Sum(file => file.Length);
            foreach (FileInfo file in recordings)
            {
                if (total <= limit) break;
                long size = file.Length;
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file.FullName,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                total -= size;
            }
        }
        catch (Exception ex)
        {
            WriteDiagnosticLine($"[{DateTime.Now:HH:mm:ss}] Limite de armazenamento: {SanitizeDiagnostic(ex.Message)}{Environment.NewLine}");
        }
    }

    private async void DefaultStreamChanged_Click(object sender, RoutedEventArgs e)
    {
        if (!preferencesReady)
            return;
        preferences.DefaultSd = SubstreamBox.IsChecked == true;
        UpdateStreamQualityButtons();
        DefaultQualityBox.SelectedIndex = preferences.DefaultSd ? 0 : 1;
        if (selectedPreviewWindow >= 0 &&
            previewBindings.TryGetValue(selectedPreviewWindow, out PreviewBinding? preferredBinding))
        {
            CloudApi.AccountDevice? preferredDevice = accountDevices.FirstOrDefault(device =>
                string.Equals(device.CloudId, preferredBinding.CloudId, StringComparison.Ordinal));
            if (preferredDevice is not null)
            {
                cameraCatalog.GetOrCreate(preferredDevice, int.MaxValue).PreferredSd = preferences.DefaultSd;
                SaveCameraCatalog();
                CameraQualityBox.SelectedIndex = preferences.DefaultSd ? 1 : 2;
            }
        }
        SavePreferences();
        if (selectedPreviewWindow >= 0 &&
            previewBindings.TryGetValue(selectedPreviewWindow, out PreviewBinding? selected))
            await SwitchSelectedPreviewStreamAsync(selected);
    }

    private void StreamQuality_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;
        bool useSd = string.Equals(element.Tag?.ToString(), "SD", StringComparison.OrdinalIgnoreCase);
        if (SubstreamBox.IsChecked == useSd)
        {
            UpdateStreamQualityButtons();
            return;
        }
        SubstreamBox.IsChecked = useSd;
        DefaultStreamChanged_Click(SubstreamBox, new RoutedEventArgs());
    }

    private void UpdateStreamQualityButtons()
    {
        bool sd = SubstreamBox.IsChecked == true;
        System.Windows.Media.Brush active = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(22, 119, 255));
        System.Windows.Media.Brush inactive = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(23, 42, 66));
        if (SdButton is null || HdButton is null)
            return;
        SdButton.Background = sd ? active : inactive;
        HdButton.Background = sd ? inactive : active;
        SdButton.Foreground = sd ? System.Windows.Media.Brushes.White : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 162, 188));
        HdButton.Foreground = sd ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(142, 162, 188)) : System.Windows.Media.Brushes.White;
    }

    private async Task SwitchSelectedPreviewStreamAsync(PreviewBinding binding)
    {
        CmsSdk.StreamType requested = SubstreamBox.IsChecked == true
            ? CmsSdk.StreamType.Extra
            : CmsSdk.StreamType.Main;
        if (binding.StreamType == requested ||
            recoveringPreviewWindows.ContainsKey(binding.Window) ||
            binding.Generation != Volatile.Read(ref previewGeneration))
            return;

        PreviewBinding updated = binding with { StreamType = requested };
        previewBindings[binding.Window] = updated;
        activePreviewWindows.Remove(binding.Window);
        confirmedPreviewWindows.TryRemove(binding.Window, out _);
        failedPreviewWindows.TryRemove(binding.Window, out _);
        SetPreviewStatus(updated, "Alterando qualidade", System.Drawing.Color.Gold);
        try { CmsSdk.CMS_Client_StopPreviewByWnd(binding.Window, 0); }
        catch { }
        await Task.Delay(800);
        if (updated.Generation != Volatile.Read(ref previewGeneration) ||
            !previewBindings.TryGetValue(updated.Window, out PreviewBinding? current) ||
            current != updated)
            return;
        Log($"Qualidade alterada para {(requested == CmsSdk.StreamType.Extra ? "SD" : "HD")}: " +
            $"dispositivo {updated.DeviceId}; canal {updated.Channel}; janela {updated.Window}.");
        await RecoverPreviewAsync(updated, forceOneAttempt: true);
    }

    private void SavePreferences()
    {
        try { preferences.Save(); }
        catch (Exception ex) { Log("Nao foi possivel salvar as preferencias: " + ex.Message); }
    }

    private void UpdateLayoutButtonSelection(int slots)
    {
        foreach (System.Windows.Controls.Button button in GridButtonsPanel.Children)
        {
            bool active = int.TryParse(button.Tag?.ToString(), out int value) && value == slots;
            button.Background = new System.Windows.Media.SolidColorBrush(active
                ? System.Windows.Media.Color.FromRgb(22, 119, 255)
                : System.Windows.Media.Color.FromRgb(23, 42, 66));
            button.BorderBrush = new System.Windows.Media.SolidColorBrush(active
                ? System.Windows.Media.Color.FromRgb(72, 157, 255)
                : System.Windows.Media.Color.FromRgb(52, 80, 111));
        }
    }

    private void UpdateCameraSummary()
    {
        int total = accountDevices.Count;
        int online = automaticDeviceLoginResults.Values.Count(value => value > 0);
        CameraCountText.Text = total == 0 ? "Nenhuma câmera carregada" : $"{total} dispositivos cadastrados";
        OnlineStatusText.Text = total == 0
            ? "●  Aguardando câmeras"
            : $"●  {Math.Min(online, total)} de {total} online";
        RefreshCameraGroupFilter();
    }

    private void RefreshCameraGroupFilter()
    {
        if (CameraGroupFilterBox is null) return;
        string selected = (CameraGroupFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "*";
        string[] groups = accountDevices
            .Select(device => cameraCatalog.GetOrCreate(device, int.MaxValue).Group)
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        string[] current = CameraGroupFilterBox.Items.OfType<ComboBoxItem>()
            .Skip(1).Select(item => item.Tag?.ToString() ?? string.Empty).ToArray();
        if (!groups.SequenceEqual(current, StringComparer.CurrentCultureIgnoreCase))
        {
            CameraGroupFilterBox.SelectionChanged -= CameraGroupFilter_SelectionChanged;
            CameraGroupFilterBox.Items.Clear();
            CameraGroupFilterBox.Items.Add(new ComboBoxItem { Content = "Todos os grupos", Tag = "*" });
            foreach (string group in groups)
                CameraGroupFilterBox.Items.Add(new ComboBoxItem { Content = group, Tag = group });
            CameraGroupFilterBox.SelectedItem = CameraGroupFilterBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), selected,
                    StringComparison.CurrentCultureIgnoreCase)) ?? CameraGroupFilterBox.Items[0];
            CameraGroupFilterBox.SelectionChanged += CameraGroupFilter_SelectionChanged;
        }
        ApplyCameraGroupFilter();
    }

    private void CameraGroupFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyCameraGroupFilter();

    private void ApplyCameraGroupFilter()
    {
        if (DeviceBox is null || CameraGroupFilterBox?.SelectedItem is not ComboBoxItem item)
            return;
        string group = item.Tag?.ToString() ?? "*";
        DeviceBox.Items.Filter = group == "*"
            ? null
            : candidate => candidate is CloudApi.AccountDevice device &&
                string.Equals(cameraCatalog.GetOrCreate(device, int.MaxValue).Group, group,
                    StringComparison.CurrentCultureIgnoreCase);
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

    private static string GetCmsDataDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "XMEyeCloudAccountTester-CMS-v3");

    private static bool EnsureCmsCloudUserStore(string cloudUser)
    {
        if (string.IsNullOrWhiteSpace(cloudUser) ||
            cloudUser.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidOperationException("A identidade tecnica do QR nao pode formar o banco Cloud.");

        // CMS_Client_Init receives an empty path and resolves data/cloudusers
        // relative to this dedicated LocalAppData profile. Preparing the store
        // under AppContext.BaseDirectory left a second, unused database beside
        // the executable and broke the first login after a complete logout.
        string dataDirectory = GetCmsDataDirectory();
        string cloudDirectory = Path.Combine(dataDirectory, "data", "cloudusers", cloudUser);
        Directory.CreateDirectory(cloudDirectory);
        string devicesDatabase = Path.Combine(cloudDirectory, "devices.db");
        // O banco do VMS serve somente para a primeira inicializacao. Copia-lo
        // novamente em todo login apagava os IDs e o estado que o CMS havia
        // consolidado nas sessoes anteriores, fazendo a grade voltar a abrir
        // apenas os dois registros ainda compativeis com a copia antiga.
        bool imported = !File.Exists(devicesDatabase) &&
            TryImportVmsDeviceDatabase(cloudUser, devicesDatabase);
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
        accountLogoutInProgress = true;
        ForgetAccountButton.IsEnabled = false;
        qrLoginCts?.Cancel();
        CloudSessionStore.BeginLogout();
        await DisconnectVideoAsync(log: false);
        ClearAccountDevices();
        cloudAccessToken = string.Empty;
        captchaToken = string.Empty;
        cloudReady = false;
        AccountBox.Clear();
        AccountPasswordBox.Clear();
        CaptchaBox.Clear();
        Log(CloudSessionStore.Exists
            ? "FALHA NO LOGOUT: o arquivo protegido da sessao ainda existe."
            : "LOGOUT INICIADO: sessao protegida removida; reiniciando para isolar a identidade nativa da conta.");

        // CMSClient não expõe logout da conta e seu UnInit causa corrupção de
        // memória quando os decodificadores já foram usados. Encerrar o processo
        // e iniciar uma instância limpa é a forma segura de descartar toda a
        // identidade nativa, MQTT, callbacks e tokens mantidos pela DLL.
        if (System.Windows.Application.Current is App app)
            app.RestartAfterAccountLogout();
        else
            System.Windows.Application.Current.Shutdown();
    }

    private void ClearAccountDevices()
    {
        DeviceBox.ItemsSource = null;
        DeviceBox.IsEnabled = false;
        OpenCameraButton.IsEnabled = false;
        SetGridButtonsEnabled(false);
        accountDevices.Clear();
        automaticDeviceLoginResults.Clear();
        UpdateCameraSummary();
    }

    private void DisconnectVideo(bool log)
    {
        // Invalida imediatamente as tarefas assíncronas ligadas à grade
        // anterior. Elas podem concluir a chamada nativa atual, mas não podem
        // iniciar outro preview nem alterar os controles da nova grade.
        Interlocked.Increment(ref previewGeneration);
        int[] windowsToStop = activePreviewWindows
            .Concat(previewBindings.Keys)
            .Concat(recoveringPreviewWindows.Keys)
            .Concat(floatingPreviewWindows)
            .Distinct()
            .ToArray();
        if (sdkReady)
            StopPtzCommand();
        else
            activePtzCommand = -1;
        StopActiveTalk();
        if (soundingPreviewWindow >= 0)
        {
            try { CmsSdk.CMS_Client_CloseSound(soundingPreviewWindow); }
            catch { }
            soundingPreviewWindow = -1;
            audioDisplayPreviewWindow = -1;
        }
        foreach (int window in recordingPreviewWindows.ToArray())
        {
            try
            {
                CmsSdk.RecordPlanUnit plan = CmsSdk.RecordPlanUnit.Create(window, false);
                CmsSdk.CMS_Client_GetRecordPlan(window, ref plan);
                plan.Window = window;
                plan.Enabled = 0;
                CmsSdk.CMS_Client_SetRecordPlan(ref plan);
            }
            catch { }
        }
        recordingPreviewWindows.Clear();
        foreach (int window in windowsToStop)
        {
            try { CmsSdk.CMS_Client_StopPreviewByWnd(window, 0); }
            catch { }
        }
        activePreviewWindows.Clear();
        floatingPreviewWindows.Clear();
        previewBindings.Clear();
        for (int window = 0; window < videoLoadingLabels.Count; window++)
            SetVideoLoadingState(window, null);
        confirmedPreviewWindows.Clear();
        failedPreviewWindows.Clear();
        disconnectedPreviewDevices.Clear();
        playing = false;
        // O VMS encerra somente o preview e preserva o login automatico do
        // dispositivo para que outra camera possa ser aberta imediatamente.
        deviceId = 0;
        VideoPlaceholder.Visibility = Visibility.Visible;
        DisconnectButton.IsEnabled = false;
        AudioButton.IsEnabled = false;
        AudioButton.Content = "🔊  Áudio";
        RecordButton.IsEnabled = false;
        RecordButton.Content = "⏺  Gravar";
        CaptureButton.IsEnabled = false;
        TalkButton.IsEnabled = false;
        PtzSidePanel.IsEnabled = false;
        PtzCameraText.Text = "Selecione uma câmera";
        SeparateWindowButton.IsEnabled = false;
        if (log) Log("Vídeo desconectado.");
    }

    private async Task DisconnectVideoAsync(bool log)
    {
        bool hadNativeWindows = activePreviewWindows.Count > 0 || previewBindings.Count > 0;
        bool hadRecoveryInProgress = recoveringPreviewWindows.Count > 0;
        DisconnectVideo(log: false);
        if (hadNativeWindows || hadRecoveryInProgress)
        {
            // StopPreview is asynchronous. Keep every HWND alive while the
            // decoder/render threads finish before rebuilding the WinForms grid.
            // A recovery has just issued StartPreview on another native thread;
            // that path needs a longer drain to avoid disposing an HWND while
            // NetSDK is still attaching its renderer to it.
            int drainMilliseconds = hadRecoveryInProgress ? 3500 : 800;
            if (hadRecoveryInProgress)
                Log("Aguardando o motor de video concluir a reconexao anterior antes de remontar a grade.");
            await Task.Delay(drainMilliseconds);
            qtRuntime.ProcessEvents();
        }
        recoveringPreviewWindows.Clear();
        if (log) Log("Vídeo desconectado.");
    }

    private void SetAccountBusy(bool busy)
    {
        accountBusy = busy;
        AccountLoginButton.IsEnabled = !busy && sdkReady;
        RefreshCaptchaButton.IsEnabled = !busy && sdkReady;
        AccountLoginButton.Content = busy ? "CARREGANDO..." : "ENTRAR E CARREGAR CÂMERAS";
    }

    private void SetCameraBusy(bool busy)
    {
        cameraBusy = busy;
        OpenCameraButton.IsEnabled = !busy && DeviceBox.SelectedItem is CloudApi.AccountDevice;
        OpenCameraButton.Content = busy ? "CONECTANDO..." : "ABRIR CÂMERA SELECIONADA";
        // Os botoes de layout continuam aceitando a ultima escolha. A fila
        // serializada coalesce cliques sem permitir duas montagens concorrentes.
        SetGridButtonsEnabled(accountDevices.Count > 0);
    }

    private QtPumpState GetQtPumpState()
    {
        bool active = playing || activePreviewWindows.Count > 0 || previewBindings.Count > 0 ||
            gridOpening || accountBusy || cameraBusy || qrBusy || captchaBusy || onlineRefreshBusy ||
            recoveringPreviewWindows.Count > 0 || disconnectedPreviewDevices.Count > 0 ||
            recordingPreviewWindows.Count > 0 || recordingPlayer.IsOpen || talkInputOpen ||
            pendingPtzConfigBuffers.Count > 0 || pendingBinaryConfigReads.Count > 0 ||
            pendingBinaryConfigWrites.Count > 0 || pendingJsonConfigReads.Count > 0;
        if (active)
            return QtPumpState.Active;
        return WindowState == WindowState.Minimized
            ? QtPumpState.MinimizedIdle
            : QtPumpState.VisibleIdle;
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
        WriteDiagnosticLine(line);
    }

    private void WriteDiagnosticLine(string line)
    {
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

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        string completeLog = File.Exists(diagnosticPath)
            ? File.ReadAllText(diagnosticPath)
            : LogBox.Text;
        System.Windows.Clipboard.SetText(SanitizeDiagnostic(completeLog));
    }

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
        if (type == CmsSdk.MessageType.DeviceRemoteConfig &&
            pendingJsonConfigReads.ContainsKey((p2, p4)))
        {
            RecordDeviceRequestResult(p2, p1);
            HandleJsonConfigResponse(p1, p2, p4);
            WriteDiagnosticLine($"[{DateTime.Now:HH:mm:ss}] SDK {type}: JSON {p1}, {p2}, {p3}, {p4}.{Environment.NewLine}");
            return;
        }
        if (type == CmsSdk.MessageType.DeviceRemoteConfig &&
            pendingBinaryConfigWrites.ContainsKey((p2, p4)))
        {
            RecordDeviceRequestResult(p2, p1);
            HandleBinaryDeviceConfigWriteResponse(p1, p2, p4);
            WriteDiagnosticLine($"[{DateTime.Now:HH:mm:ss}] SDK {type}: gravação {p1}, {p2}, {p3}, {p4}.{Environment.NewLine}");
            return;
        }
        if (type == CmsSdk.MessageType.DeviceRemoteConfig &&
            pendingBinaryConfigReads.ContainsKey((p2, p4)))
        {
            // Nesta build do CMS: p1=resultado, p2=deviceId, p3=canal, p4=comando.
            RecordDeviceRequestResult(p2, p1);
            HandleBinaryDeviceConfigResponse(p1, p2, p4);
            WriteDiagnosticLine($"[{DateTime.Now:HH:mm:ss}] SDK {type}: {p1}, {p2}, {p3}, {p4}.{Environment.NewLine}");
            return;
        }
        if (type == CmsSdk.MessageType.DeviceRemoteConfig &&
            p4 is SystemFunctionCommand or PtzControlConfigCommand)
        {
            // Nesta build do CMS: p1=resultado, p2=deviceId, p3=canal, p4=comando.
            RecordDeviceRequestResult(p2, p1);
            HandlePtzConfigResponse(p2, p4, text1, text2, size);
            WriteDiagnosticLine($"[{DateTime.Now:HH:mm:ss}] SDK {type}: {p1}, {p2}, {p3}, {p4}.{Environment.NewLine}");
            return;
        }
        int lostPreviewWindow = -1;
        bool deviceBecameUnstable = false;
        if (type == CmsSdk.MessageType.DeviceControl && p1 == 3)
        {
            automaticDeviceLoginResults[p2] = p4;
            RecordDeviceRequestResult(p2, p4);
            if (p2 == deviceId && p4 < 0)
                previewLoginError = p4;
        }
        if (type == CmsSdk.MessageType.VideoWindowControl &&
            p1 == 7 && p2 >= 0 && p3 > 0 && p4 > 0)
        {
            confirmedPreviewWindows[p2] = 0;
            failedPreviewWindows.TryRemove(p2, out _);
        }
        else if (type == CmsSdk.MessageType.VideoWindowControl && p1 == 1 && p4 >= 0 &&
                 !recoveringPreviewWindows.ContainsKey(p4))
        {
            if (confirmedPreviewWindows.ContainsKey(p4) && previewBindings.ContainsKey(p4))
                lostPreviewWindow = p4;
            confirmedPreviewWindows.TryRemove(p4, out _);
            failedPreviewWindows[p4] = 0;
        }
        if (type == CmsSdk.MessageType.ChannelControl && p1 == 3 && p4 >= 0 &&
            !recoveringPreviewWindows.ContainsKey(p4))
        {
            if (confirmedPreviewWindows.ContainsKey(p4) && previewBindings.ContainsKey(p4))
                lostPreviewWindow = p4;
            confirmedPreviewWindows.TryRemove(p4, out _);
            failedPreviewWindows[p4] = 0;
        }
        if (type == CmsSdk.MessageType.DeviceControl && p1 == 4)
        {
            automaticDeviceLoginResults[p2] = 0;
            disconnectedPreviewDevices[p2] = 0;
            deviceBecameUnstable = RecordDeviceDisconnect(p2);
            foreach (PreviewBinding binding in previewBindings.Values.Where(binding => binding.DeviceId == p2))
                confirmedPreviewWindows.TryRemove(binding.Window, out _);
        }
        bool shouldRecoverReturnedDevice =
            type == CmsSdk.MessageType.DeviceControl && p1 == 3 && p4 > 0 &&
            disconnectedPreviewDevices.TryRemove(p2, out _);
        bool shouldMonitorDisconnectedDevice =
            type == CmsSdk.MessageType.DeviceControl &&
            (p1 == 4 || p1 == 3 && p4 <= 0 && disconnectedPreviewDevices.ContainsKey(p2));
        bool shouldRecoverPreview = preferences.AutoReconnect && lostPreviewWindow >= 0;
        // Native text is deliberately excluded because some SDK messages may
        // contain device metadata. Only non-sensitive numeric diagnostics are logged.
        WriteDiagnosticLine($"[{DateTime.Now:HH:mm:ss}] SDK {type}: {p1}, {p2}, {p3}, {p4}.{Environment.NewLine}");
        bool needsUiUpdate =
            type == CmsSdk.MessageType.VideoWindowControl && p1 is 0 or 7 or 27 ||
            type == CmsSdk.MessageType.DeviceControl && p1 is 3 or 4 ||
            shouldRecoverReturnedDevice || shouldRecoverPreview;
        if (!needsUiUpdate)
            return;
        Dispatcher.BeginInvoke((Action)(() =>
        {
            if (type == CmsSdk.MessageType.VideoWindowControl &&
                p1 is 0 or 27 && p4 >= 0 && p4 < videoLabels.Count)
                videoLabels[p4].BringToFront();
            if (type == CmsSdk.MessageType.VideoWindowControl && p1 == 7 && !gridOpening)
                ScheduleLayoutRestore();
            if (type == CmsSdk.MessageType.DeviceControl && p1 == 4)
            {
                foreach (PreviewBinding binding in previewBindings.Values.Where(binding => binding.DeviceId == p2))
                    SetPreviewStatus(
                        binding,
                        CanIssueDeviceRequest(p2, out _, out _, out _)
                            ? "Aguardando sinal"
                            : DeviceBlockStatus(p2),
                        CanIssueDeviceRequest(p2, out _, out _, out _)
                            ? System.Drawing.Color.Orange
                            : System.Drawing.Color.IndianRed);
                Log($"Sessao P2P perdida: dispositivo {p2}; nenhuma requisicao enviada; " +
                    $"modo {(deviceBecameUnstable ? "instavel por 30 minutos" : "observacao passiva")}.");
            }
            if (type == CmsSdk.MessageType.DeviceControl && p1 == 3 && p4 is -27 or -25)
            {
                foreach (PreviewBinding binding in previewBindings.Values.Where(binding => binding.DeviceId == p2))
                    SetPreviewStatus(
                        binding,
                        DeviceBlockStatus(p2),
                        System.Drawing.Color.IndianRed);
            }
            if (type == CmsSdk.MessageType.DeviceControl && p1 is 3 or 4)
            {
                CloudApi.AccountDevice? changedDevice = accountDevices.FirstOrDefault(device =>
                    device.CmsDeviceId == p2 || previewBindings.Values.Any(binding =>
                        binding.DeviceId == p2 && string.Equals(binding.CloudId, device.CloudId, StringComparison.Ordinal)));
                if (changedDevice is not null)
                {
                    TrackDeviceRequestProtection(changedDevice, p2);
                    changedDevice.RuntimeStatus = !CanIssueDeviceRequest(p2, out _, out _, out _)
                        ? DeviceBlockStatus(p2)
                        : p1 == 4
                        ? "Aguardando sinal"
                        : p4 <= 0
                        ? "Offline"
                        : "Online";
                    DeviceBox.Items.Refresh();
                }
                UpdateCameraSummary();
            }
            if (shouldMonitorDisconnectedDevice)
                _ = MonitorDisconnectedDeviceAsync(p2);
            if (shouldRecoverReturnedDevice)
            {
                Log($"Dispositivo {p2} retomou a sessao pelo monitor interno; " +
                    "iniciando observacao passiva dos previews.");
                _ = RecoverReturnedDevicePreviewsAsync(p2);
            }
            else if (shouldRecoverPreview &&
                     previewBindings.TryGetValue(lostPreviewWindow, out PreviewBinding? lostBinding))
            {
                Log($"Canal sem imagem detectado: janela {lostPreviewWindow}; iniciando reconexão automática.");
                SetPreviewStatus(lostBinding, "Reconectando", System.Drawing.Color.Orange);
                _ = RecoverPreviewAsync(lostBinding);
            }
        }));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        isClosing = true;
        layoutRestoreTimer.Stop();
        recordingPlaybackTimer.Stop();
        recordingThumbnailCts?.Cancel();
        recordingPlayer.Dispose();
        foreach (var pending in pendingPtzConfigBuffers.ToArray())
            if (pendingPtzConfigBuffers.TryRemove(pending.Key, out IntPtr buffer) && buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        foreach (var pending in pendingBinaryConfigReads.ToArray())
        {
            if (!pendingBinaryConfigReads.TryRemove(pending.Key, out PendingBinaryConfigRead? request))
                continue;
            if (request.Buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(request.Buffer);
            request.Completion.TrySetResult(null);
        }
        foreach (var pending in pendingBinaryConfigWrites.ToArray())
        {
            if (!pendingBinaryConfigWrites.TryRemove(pending.Key, out PendingBinaryConfigWrite? request))
                continue;
            if (request.Buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(request.Buffer);
            request.Completion.TrySetResult(null);
        }
        foreach (var pending in pendingJsonConfigReads.ToArray())
        {
            if (!pendingJsonConfigReads.TryRemove(pending.Key, out PendingJsonConfigRead? request))
                continue;
            if (request.Buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(request.Buffer);
            request.Completion.TrySetResult(null);
        }
        lock (ptzLogLock)
            genericJsonResponses.Clear();
        StopPtzCommand();
        StopActiveTalk();
        try
        {
            qrLoginCts?.Cancel();
            qrLoginCts?.Dispose();
            foreach (int window in sdkReady
                         ? Enumerable.Range(0, Math.Max(videoPanels.Count, 16))
                         : Enumerable.Empty<int>())
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
