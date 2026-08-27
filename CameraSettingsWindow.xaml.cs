using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace XMEyeCloudTester;

public partial class CameraSettingsWindow : Window
{
    private sealed record TimeZoneChoice(int MinutesWest, string Display)
    {
        public override string ToString() => Display;
    }

    private readonly Func<Task<MainWindow.BasicSettingsLoadResult>> load;
    private readonly Func<CameraBasicConfigurationCodec.Snapshot,
        CameraBasicConfigurationCodec.Changes,
        Task<MainWindow.BasicSettingsSaveResult>> save;
    private readonly Func<CameraBasicConfigurationCodec.Snapshot,
        Task<MainWindow.BasicSettingsSaveResult>> synchronize;
    private CameraBasicConfigurationCodec.Snapshot? baseline;
    private bool applying;
    private bool busy;

    internal CameraSettingsWindow(
        CloudApi.AccountDevice device,
        Func<Task<MainWindow.BasicSettingsLoadResult>> load,
        Func<CameraBasicConfigurationCodec.Snapshot,
            CameraBasicConfigurationCodec.Changes,
            Task<MainWindow.BasicSettingsSaveResult>> save,
        Func<CameraBasicConfigurationCodec.Snapshot,
            Task<MainWindow.BasicSettingsSaveResult>> synchronize)
    {
        InitializeComponent();
        this.load = load;
        this.save = save;
        this.synchronize = synchronize;
        WindowTitleText.Text = $"Configurações — {device.Alias}";
        Loaded += async (_, _) => await ReloadAsync(confirmDiscard: false);
        SetControlsEnabled(false);
    }

    private async Task ReloadAsync(bool confirmDiscard)
    {
        if (confirmDiscard && IsDirty() && System.Windows.MessageBox.Show(
                this,
                "Descartar as alterações locais e consultar a câmera novamente?",
                "Consultar novamente",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        SetBusy(true, "Consultando a câmera…");
        MainWindow.BasicSettingsLoadResult result;
        try
        {
            result = await load();
        }
        catch (Exception ex)
        {
            result = new(null, $"Falha ao consultar a câmera: {ex.Message}");
        }

        if (result.Snapshot is null)
        {
            baseline = null;
            LoadStateIcon.Text = "!";
            LoadStateIcon.Foreground = System.Windows.Media.Brushes.OrangeRed;
            LoadStateText.Text = result.Message;
            SetBusy(false);
            SetControlsEnabled(false);
            return;
        }

        ApplySnapshot(result.Snapshot);
        LoadStateIcon.Text = "✓";
        LoadStateIcon.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(78, 214, 155));
        LoadStateText.Text = result.Message;
        SetBusy(false);
    }

    private void ApplySnapshot(CameraBasicConfigurationCodec.Snapshot snapshot)
    {
        applying = true;
        baseline = snapshot;
        MachineNameBox.Text = snapshot.MachineName;
        LanguageText.Text = snapshot.Language;
        MirrorSwitch.IsChecked = snapshot.PictureMirror;
        FlipSwitch.IsChecked = snapshot.PictureFlip;

        double sensitivityMaximum = snapshot.DayNightSensitivity <= 10 ? 10 : 100;
        SensitivitySlider.Maximum = sensitivityMaximum;
        SensitivityMaximumText.Text = ((int)sensitivityMaximum).ToString();
        SensitivitySlider.Value = snapshot.DayNightSensitivity;
        SensitivityValue.Text = snapshot.DayNightSensitivity.ToString();
        VolumeSlider.Value = snapshot.LeftVolume;
        VolumeValue.Text = snapshot.LeftVolume == snapshot.RightVolume
            ? snapshot.LeftVolume.ToString()
            : $"{snapshot.LeftVolume}/{snapshot.RightVolume}";
        AudioModeText.Text = $"Modo retornado pela câmera: {snapshot.AudioMode}";
        RotationSpeedBox.SelectedItem = RotationSpeedBox.Items
            .OfType<ComboBoxItem>()
            .First(item => Convert.ToInt32(item.Tag) == snapshot.PtzSpeed);
        PopulateTimeZones(snapshot.MinutesWest);
        CameraTimeText.Text = snapshot.DeviceTime.ToString("dd/MM/yyyy HH:mm:ss");
        WindowTitleText.Text = $"Configurações — {snapshot.MachineName}";
        applying = false;
        SetControlsEnabled(true);
        UpdatePendingState();
    }

    private void PopulateTimeZones(int selectedMinutesWest)
    {
        var choices = new List<TimeZoneChoice>();
        for (int utcMinutes = -12 * 60; utcMinutes <= 14 * 60; utcMinutes += 30)
        {
            int minutesWest = -utcMinutes;
            choices.Add(new TimeZoneChoice(
                minutesWest,
                CameraBasicConfigurationCodec.FormatTimeZone(minutesWest)));
        }
        if (choices.All(item => item.MinutesWest != selectedMinutesWest))
            choices.Add(new TimeZoneChoice(
                selectedMinutesWest,
                CameraBasicConfigurationCodec.FormatTimeZone(selectedMinutesWest)));
        TimeZoneBox.ItemsSource = choices.OrderByDescending(item => item.MinutesWest).ToList();
        TimeZoneBox.SelectedItem = choices.First(item => item.MinutesWest == selectedMinutesWest);
    }

    private CameraBasicConfigurationCodec.Changes BuildChanges()
    {
        if (baseline is null)
            return new(null, null, null, null, null, null, null);
        string name = MachineNameBox.Text.Trim();
        bool mirror = MirrorSwitch.IsChecked == true;
        bool flip = FlipSwitch.IsChecked == true;
        int sensitivity = (int)Math.Round(SensitivitySlider.Value);
        int volume = (int)Math.Round(VolumeSlider.Value);
        int timeZone = (TimeZoneBox.SelectedItem as TimeZoneChoice)?.MinutesWest ?? baseline.MinutesWest;
        int ptzSpeed = RotationSpeedBox.SelectedItem is ComboBoxItem speedItem
            ? Convert.ToInt32(speedItem.Tag)
            : baseline.PtzSpeed;
        return new(
            string.Equals(name, baseline.MachineName, StringComparison.Ordinal) ? null : name,
            mirror == baseline.PictureMirror ? null : mirror,
            flip == baseline.PictureFlip ? null : flip,
            sensitivity == baseline.DayNightSensitivity ? null : sensitivity,
            volume == baseline.LeftVolume && volume == baseline.RightVolume ? null : volume,
            timeZone == baseline.MinutesWest ? null : timeZone,
            ptzSpeed == baseline.PtzSpeed ? null : ptzSpeed);
    }

    private bool IsDirty()
    {
        CameraBasicConfigurationCodec.Changes changes = BuildChanges();
        return changes.MachineName is not null || changes.PictureMirror is not null ||
            changes.PictureFlip is not null || changes.DayNightSensitivity is not null ||
            changes.SpeakerVolume is not null || changes.MinutesWest is not null ||
            changes.PtzSpeed is not null;
    }

    private void EditableControl_Changed(object sender, RoutedEventArgs e)
    {
        if (sender == SensitivitySlider)
            SensitivityValue.Text = ((int)Math.Round(SensitivitySlider.Value)).ToString();
        if (sender == VolumeSlider)
            VolumeValue.Text = ((int)Math.Round(VolumeSlider.Value)).ToString();
        if (!applying)
            UpdatePendingState();
    }

    private void UpdatePendingState()
    {
        bool dirty = baseline is not null && IsDirty();
        SaveButton.IsEnabled = dirty && !busy && Encoding.UTF8.GetByteCount(MachineNameBox.Text.Trim()) < 0x40 &&
            MachineNameBox.Text.Trim().Length > 0;
        DiscardButton.IsEnabled = dirty && !busy;
        ReloadButton.IsEnabled = !busy;
        SynchronizeButton.IsEnabled = baseline is not null && !dirty && !busy;
        PendingIcon.Text = dirty ? "●" : "✓";
        PendingIcon.Foreground = dirty
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(242, 185, 75))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(53, 198, 208));
        PendingText.Text = dirty
            ? "Alterações pendentes — serão enviadas somente ao salvar"
            : "Nenhuma alteração pendente";
    }

    private void SetBusy(bool value, string? message = null)
    {
        busy = value;
        if (message is not null)
        {
            LoadStateIcon.Text = "◌";
            LoadStateIcon.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(242, 185, 75));
            LoadStateText.Text = message;
        }
        SetControlsEnabled(!value && baseline is not null);
        UpdatePendingState();
    }

    private void SetControlsEnabled(bool enabled)
    {
        MachineNameBox.IsEnabled = enabled;
        MirrorSwitch.IsEnabled = enabled;
        FlipSwitch.IsEnabled = enabled;
        SensitivitySlider.IsEnabled = enabled;
        VolumeSlider.IsEnabled = enabled;
        TimeZoneBox.IsEnabled = enabled;
        RotationSpeedBox.IsEnabled = enabled;
        SynchronizeButton.IsEnabled = enabled;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (baseline is null || !IsDirty())
            return;
        CameraBasicConfigurationCodec.Changes changes = BuildChanges();
        SetBusy(true, "Alterações enviadas; confirmando pela nova leitura…");
        MainWindow.BasicSettingsSaveResult result;
        try
        {
            result = await save(baseline, changes);
        }
        catch (Exception ex)
        {
            result = new(false, null, $"Falha ao preparar a alteração: {ex.Message}");
        }
        if (result.Snapshot is not null)
            ApplySnapshot(result.Snapshot);
        LoadStateIcon.Text = result.Success ? "✓" : "!";
        LoadStateIcon.Foreground = result.Success
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 214, 155))
            : System.Windows.Media.Brushes.OrangeRed;
        LoadStateText.Text = result.Message;
        SetBusy(false);
    }

    private async void Synchronize_Click(object sender, RoutedEventArgs e)
    {
        if (baseline is null || IsDirty())
            return;
        SetBusy(true, "Sincronizando a hora e consultando novamente…");
        MainWindow.BasicSettingsSaveResult result;
        try
        {
            result = await synchronize(baseline);
        }
        catch (Exception ex)
        {
            result = new(false, null, $"Falha ao sincronizar a hora: {ex.Message}");
        }
        if (result.Snapshot is not null)
            ApplySnapshot(result.Snapshot);
        LoadStateIcon.Text = result.Success ? "✓" : "!";
        LoadStateIcon.Foreground = result.Success
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(78, 214, 155))
            : System.Windows.Media.Brushes.OrangeRed;
        LoadStateText.Text = result.Message;
        SetBusy(false);
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (baseline is not null)
            ApplySnapshot(baseline);
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) =>
        await ReloadAsync(confirmDiscard: true);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
