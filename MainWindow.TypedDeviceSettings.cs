namespace XMEyeCloudTester;

public partial class MainWindow
{
    internal sealed record BasicSettingsLoadResult(
        CameraBasicConfigurationCodec.Snapshot? Snapshot,
        string Message);

    internal sealed record BasicSettingsSaveResult(
        bool Success,
        CameraBasicConfigurationCodec.Snapshot? Snapshot,
        string Message);

    private void ShowFunctionalDeviceSettings(CloudApi.AccountDevice device)
    {
        PreviewBinding? online = previewBindings.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.CloudId, device.CloudId, StringComparison.Ordinal) &&
            confirmedPreviewWindows.ContainsKey(candidate.Window));
        if (online is null)
        {
            System.Windows.MessageBox.Show(
                this,
                "Abra a imagem desta câmera antes de consultar as configurações do aparelho.",
                "Câmera desconectada",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var window = new CameraSettingsWindow(
            device,
            () => LoadBasicSettingsAsync(online),
            (baseline, changes) => SaveBasicSettingsAsync(device, online, baseline, changes),
            baseline => SynchronizeTypedCameraTimeAsync(online, baseline))
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async Task<BasicSettingsLoadResult> LoadBasicSettingsAsync(PreviewBinding online)
    {
        byte[]? general = await ReadTypedDeviceConfigV2Async(
            online.DeviceId, -1,
            CameraBasicConfigurationCodec.GeneralType,
            CameraBasicConfigurationCodec.GeneralSize).ConfigureAwait(false);
        if (general is null)
            return new(null, "Tempo esgotado ao consultar o nome do aparelho.");

        byte[]? location = await ReadTypedDeviceConfigV2Async(
            online.DeviceId, -1,
            CameraBasicConfigurationCodec.LocationType,
            CameraBasicConfigurationCodec.LocationSize).ConfigureAwait(false);
        if (location is null)
            return new(null, "Tempo esgotado ao consultar a localização e o idioma.");

        byte[]? camera = await ReadTypedDeviceConfigV2Async(
            online.DeviceId, online.Channel,
            CameraBasicConfigurationCodec.CameraParameterType,
            CameraBasicConfigurationCodec.CameraParameterSize).ConfigureAwait(false);
        if (camera is null)
            return new(null, "Tempo esgotado ao consultar os parâmetros da imagem.");

        byte[]? volume = await ReadTypedDeviceConfigV2Async(
            online.DeviceId, online.Channel,
            CameraBasicConfigurationCodec.VolumeType,
            CameraBasicConfigurationCodec.VolumeSize).ConfigureAwait(false);
        if (volume is null)
            return new(null, "Tempo esgotado ao consultar o volume do alto-falante.");

        byte[]? timeZone = await ReadTypedDeviceConfigV2Async(
            online.DeviceId, -1,
            CameraBasicConfigurationCodec.TimeZoneType,
            CameraBasicConfigurationCodec.TimeZoneSize).ConfigureAwait(false);
        if (timeZone is null)
            return new(null, "Tempo esgotado ao consultar o fuso horário.");

        byte[]? cameraTime = await ReadTypedDeviceConfigV2Async(
            online.DeviceId, -1,
            CameraBasicConfigurationCodec.TimeReadType,
            CameraBasicConfigurationCodec.TimeSize).ConfigureAwait(false);
        if (cameraTime is null)
            return new(null, "Tempo esgotado ao consultar a hora da câmera.");

        if (!CameraBasicConfigurationCodec.TryCreateSnapshot(
                general, location, camera, volume, timeZone, cameraTime,
                out CameraBasicConfigurationCodec.Snapshot? snapshot,
                out string error) || snapshot is null)
            return new(null, error);

        CloudApi.AccountDevice? device = accountDevices.FirstOrDefault(candidate =>
            string.Equals(candidate.CloudId, online.CloudId, StringComparison.Ordinal));
        if (device is not null)
        {
            int configuredSpeed = cameraCatalog.GetOrCreate(device, int.MaxValue).PtzSpeed;
            snapshot = snapshot with { PtzSpeed = configuredSpeed is 1 or 3 or 5 ? configuredSpeed : 5 };
        }

        return new(snapshot, "Dados consultados diretamente da câmera agora.");
    }

    private async Task<BasicSettingsSaveResult> SaveBasicSettingsAsync(
        CloudApi.AccountDevice device,
        PreviewBinding online,
        CameraBasicConfigurationCodec.Snapshot baseline,
        CameraBasicConfigurationCodec.Changes changes)
    {
        var writes = new List<(int Type, int Channel, byte[] Payload, string Label)>();
        if (changes.MachineName is string requestedName &&
            !string.Equals(requestedName.Trim(), baseline.MachineName, StringComparison.Ordinal))
            writes.Add((CameraBasicConfigurationCodec.GeneralType, -1,
                CameraBasicConfigurationCodec.WithMachineName(baseline.General, requestedName),
                "nome do aparelho"));

        bool mirror = changes.PictureMirror ?? baseline.PictureMirror;
        bool flip = changes.PictureFlip ?? baseline.PictureFlip;
        int sensitivity = changes.DayNightSensitivity ?? baseline.DayNightSensitivity;
        if (mirror != baseline.PictureMirror || flip != baseline.PictureFlip ||
            sensitivity != baseline.DayNightSensitivity)
            writes.Add((CameraBasicConfigurationCodec.CameraParameterType, online.Channel,
                CameraBasicConfigurationCodec.WithCameraParameters(
                    baseline.CameraParameters, mirror, flip, sensitivity),
                "orientação e imagem"));

        if (changes.SpeakerVolume is int volume &&
            (volume != baseline.LeftVolume || volume != baseline.RightVolume))
            writes.Add((CameraBasicConfigurationCodec.VolumeType, online.Channel,
                CameraBasicConfigurationCodec.WithVolume(baseline.Volume, volume),
                "volume do alto-falante"));

        if (changes.MinutesWest is int minutesWest && minutesWest != baseline.MinutesWest)
            writes.Add((CameraBasicConfigurationCodec.TimeZoneType, -1,
                CameraBasicConfigurationCodec.WithTimeZone(baseline.TimeZone, minutesWest),
                "fuso horário"));

        bool ptzSpeedChanged = changes.PtzSpeed is int requestedPtzSpeed &&
            requestedPtzSpeed != baseline.PtzSpeed;

        if (writes.Count == 0 && !ptzSpeedChanged)
            return new(true, baseline, "Nenhuma alteração pendente.");

        if (writes.Count == 0 && changes.PtzSpeed is int localPtzSpeed)
        {
            CameraCatalogStore.Entry localEntry = cameraCatalog.GetOrCreate(device, int.MaxValue);
            localEntry.PtzSpeed = localPtzSpeed;
            await Dispatcher.InvokeAsync(SaveCameraCatalog);
            return new(true, baseline with { PtzSpeed = localPtzSpeed },
                "Velocidade dos controles PTZ atualizada neste aplicativo.");
        }

        foreach ((int type, int channel, byte[] payload, string label) in writes)
        {
            int? result = await WriteTypedDeviceConfigV2Async(
                online.DeviceId, channel, type, payload).ConfigureAwait(false);
            if (result is null)
                return new(false, null,
                    $"Tempo esgotado aguardando a confirmação de {label}; a gravação não será repetida.");
            if (result < 0)
                return new(false, null, $"A câmera recusou {label} (código {result}).");
        }

        await Task.Delay(500).ConfigureAwait(false);
        BasicSettingsLoadResult verification = await LoadBasicSettingsAsync(online).ConfigureAwait(false);
        CameraBasicConfigurationCodec.Snapshot? actual = verification.Snapshot;
        if (actual is null)
            return new(false, null,
                "A alteração foi enviada, mas a nova leitura não chegou. O aplicativo não repetirá a escrita.");

        bool verified =
            (changes.MachineName is null ||
             string.Equals(actual.MachineName, changes.MachineName.Trim(), StringComparison.Ordinal)) &&
            (changes.PictureMirror is null || actual.PictureMirror == changes.PictureMirror) &&
            (changes.PictureFlip is null || actual.PictureFlip == changes.PictureFlip) &&
            (changes.DayNightSensitivity is null ||
             actual.DayNightSensitivity == changes.DayNightSensitivity) &&
            (changes.SpeakerVolume is null ||
             actual.LeftVolume == changes.SpeakerVolume && actual.RightVolume == changes.SpeakerVolume) &&
            (changes.MinutesWest is null || actual.MinutesWest == changes.MinutesWest);
        if (!verified)
            return new(false, actual,
                "A câmera respondeu, mas a nova leitura divergiu do valor solicitado.");

        if (changes.PtzSpeed is int ptzSpeed)
        {
            CameraCatalogStore.Entry speedEntry = cameraCatalog.GetOrCreate(device, int.MaxValue);
            speedEntry.PtzSpeed = ptzSpeed;
            actual = actual with { PtzSpeed = ptzSpeed };
            await Dispatcher.InvokeAsync(SaveCameraCatalog);
        }

        if (changes.MachineName is not null)
        {
            device.Alias = actual.MachineName;
            CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(
                device, Math.Max(0, DeviceBox.SelectedIndex));
            entry.Name = actual.MachineName;
            entry.UseCustomName = false;
            await Dispatcher.InvokeAsync(() =>
            {
                SaveCameraCatalog();
                DeviceBox.Items.Refresh();
                RefreshPreviewNames(device);
                UpdateCameraSummary();
            });
        }
        return new(true, actual, "Alterações confirmadas pela nova leitura da câmera.");
    }

    private async Task<BasicSettingsSaveResult> SynchronizeTypedCameraTimeAsync(
        PreviewBinding online,
        CameraBasicConfigurationCodec.Snapshot baseline)
    {
        DateTime requested = DateTime.Now;
        byte[] payload = CameraBasicConfigurationCodec.WithTime(baseline.CameraTime, requested);
        int? result = await WriteTypedDeviceConfigV2Async(
            online.DeviceId, -1,
            CameraBasicConfigurationCodec.TimeWriteType,
            payload).ConfigureAwait(false);
        if (result is null)
            return new(false, null,
                "Tempo esgotado aguardando a confirmação da hora; a gravação não será repetida.");
        if (result < 0)
            return new(false, null, $"A câmera recusou a sincronização da hora (código {result}).");

        await Task.Delay(500).ConfigureAwait(false);
        BasicSettingsLoadResult verification = await LoadBasicSettingsAsync(online).ConfigureAwait(false);
        if (verification.Snapshot is not { } actual)
            return new(false, null, "A hora foi enviada, mas a nova leitura não chegou.");
        if (Math.Abs((actual.DeviceTime - requested).TotalSeconds) > 10)
            return new(false, actual, "A nova leitura não confirmou a hora enviada.");
        return new(true, actual, "Hora sincronizada e confirmada pela nova leitura.");
    }
}
