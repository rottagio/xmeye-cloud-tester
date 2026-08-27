using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using TextBox = System.Windows.Controls.TextBox;
using TabControl = System.Windows.Controls.TabControl;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;

namespace XMEyeCloudTester;

public partial class MainWindow
{
    private sealed class PendingJsonConfigRead
    {
        internal required IntPtr Buffer;
        internal required TaskCompletionSource<int?> Completion;
    }

    private readonly ConcurrentDictionary<(int DeviceId, int Command), PendingJsonConfigRead>
        pendingJsonConfigReads = new();

    private void HandleJsonConfigResponse(int result, int targetDeviceId, int command)
    {
        if (!pendingJsonConfigReads.TryRemove((targetDeviceId, command), out PendingJsonConfigRead? pending))
            return;
        if (pending.Buffer != IntPtr.Zero)
            Marshal.FreeHGlobal(pending.Buffer);
        pending.Completion.TrySetResult(result);
    }

    private static string ExactJsonName(DeviceConfigurationCatalog.Definition definition, int channel)
    {
        bool perChannel = definition.Scope == DeviceConfigurationCatalog.ChannelScope.Channel ||
            definition.Scope == DeviceConfigurationCatalog.ChannelScope.DeviceOrChannel &&
            definition.Key is "Recording.Main" or "Recording.Extra" or "Tracking.Motion" or
                "Alarm.IntelligentAlert";
        return perChannel && channel >= 0
            ? $"{definition.JsonName}.[{channel}]"
            : definition.JsonName;
    }

    private static int ConfigChannel(DeviceConfigurationCatalog.Definition definition, int channel) =>
        definition.Scope == DeviceConfigurationCatalog.ChannelScope.Device ? -1 : channel;

    private JsonObject? TakeJsonResponse(string expectedName)
    {
        lock (ptzLogLock)
        {
            ReadNewPtzResponsesLocked();
            int count = genericJsonResponses.Count;
            for (int index = 0; index < count; index++)
            {
                string json = genericJsonResponses.Dequeue();
                try
                {
                    JsonObject? root = JsonNode.Parse(json) as JsonObject;
                    string? returnedName = null;
                    try { returnedName = root?["Name"]?.GetValue<string>(); } catch { }
                    if ((returnedName == expectedName || root?.ContainsKey(expectedName) == true) &&
                        root?["Ret"]?.GetValue<int>() == 100)
                        return root;
                }
                catch { }
            }
        }
        return null;
    }

    private async Task<JsonObject?> ReadGenericConfigAsync(
        int targetDeviceId, int channel, DeviceConfigurationCatalog.Definition definition)
    {
        if (definition.ReadCommand is not int command)
            return null;
        string exactName = ExactJsonName(definition, channel);
        await deviceConfigIoGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!CanIssueDeviceRequest(targetDeviceId, out _, out _, out _))
                return null;
            InitializePtzLogCursor();
            lock (ptzLogLock)
            {
                ReadNewPtzResponsesLocked();
                genericJsonResponses.Clear();
            }

            var key = (targetDeviceId, command);
            if (pendingJsonConfigReads.ContainsKey(key))
                return null;
            IntPtr buffer = Marshal.AllocHGlobal(DeviceConfigBufferSize);
            Marshal.Copy(new byte[DeviceConfigBufferSize], 0, buffer, DeviceConfigBufferSize);
            byte[] request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Name = exactName }));
            Marshal.Copy(request, 0, buffer, request.Length);
            var completion = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pendingJsonConfigReads.TryAdd(key, new PendingJsonConfigRead
                {
                    Buffer = buffer,
                    Completion = completion
                }))
            {
                Marshal.FreeHGlobal(buffer);
                return null;
            }

            int accepted;
            try
            {
                accepted = CmsSdk.CMS_Client_GetDeviceConfig(
                    targetDeviceId, ConfigChannel(definition, channel), command,
                    buffer, DeviceConfigBufferSize, -1);
            }
            catch
            {
                accepted = -1;
            }
            if (accepted < 0)
            {
                if (pendingJsonConfigReads.TryRemove(key, out PendingJsonConfigRead? rejected))
                {
                    Marshal.FreeHGlobal(rejected.Buffer);
                    rejected.Completion.TrySetResult(accepted);
                }
                RecordDeviceRequestResult(targetDeviceId, accepted);
                return null;
            }

            JsonObject? response = null;
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (!isClosing && DateTime.UtcNow < deadline)
            {
                response ??= TakeJsonResponse(exactName);
                if (response is not null && completion.Task.IsCompleted)
                    break;
                if (completion.Task.IsCompleted && await completion.Task.ConfigureAwait(false) is int result && result < 0)
                    return null;
                await Task.Delay(75).ConfigureAwait(false);
            }
            return response ?? TakeJsonResponse(exactName);
        }
        finally
        {
            deviceConfigIoGate.Release();
        }
    }

    private async Task<ConfigurationWriteResult> WriteGenericFieldAsync(
        CloudApi.AccountDevice device, PreviewBinding online,
        DeviceConfigurationCatalog.Definition definition, JsonObject originalRoot,
        Action<JsonNode> mutate, bool sensitiveConfirmed = false)
    {
        string exactName = ExactJsonName(definition, online.Channel);
        JsonNode? original = originalRoot[exactName];
        if (original is null)
            return new(false, false, "A resposta da câmera não contém esta configuração.");

        deviceProfiles.RecordConfigurationEvidence(device.CloudId, definition.Key, true,
            "CMS JSON validado", DateTime.UtcNow);
        deviceProfiles.Devices.TryGetValue(device.CloudId, out DeviceProfileStore.Profile? profile);
        DeviceProfileStore.ConfigurationBinding? binding = null;
        profile?.CompatibleCommands.TryGetValue(definition.Key, out binding);
        if (!DeviceConfigurationWritePolicy.CanWrite(definition, binding, true, sensitiveConfirmed,
                DateTime.UtcNow, out TimeSpan wait, out string denied))
            return new(false, false, wait > TimeSpan.Zero
                ? $"{denied} Aguarde {Math.Ceiling(wait.TotalSeconds)} segundo(s)."
                : denied);

        JsonNode proposed = original.DeepClone();
        mutate(proposed);
        if (JsonNode.DeepEquals(original, proposed))
            return new(true, false, "A câmera já está com este valor.");

        var envelope = new JsonObject
        {
            [exactName] = proposed.DeepClone(),
            ["Name"] = exactName
        };
        deviceProfiles.RecordConfigurationWrite(device.CloudId, definition.Key, DateTime.UtcNow);
        SaveDeviceProfiles();
        int? result = await WriteSerializedBinaryConfigAsync(
            online.DeviceId, ConfigChannel(definition, online.Channel), definition.WriteCommand!.Value,
            Encoding.UTF8.GetBytes(envelope.ToJsonString())).ConfigureAwait(false);
        if (result is null)
            return new(false, false, "O SDK não confirmou a gravação; ela não foi repetida.");
        if (result < 0)
            return new(false, false, $"A câmera recusou a alteração (código {result}).");

        await Task.Delay(700).ConfigureAwait(false);
        JsonObject? verified = await ReadGenericConfigAsync(
            online.DeviceId, online.Channel, definition).ConfigureAwait(false);
        if (verified?[exactName] is JsonNode actual && JsonNode.DeepEquals(actual, proposed))
            return new(true, false, "Alteração aplicada e confirmada pela câmera.");

        var rollback = new JsonObject
        {
            [exactName] = original.DeepClone(),
            ["Name"] = exactName
        };
        int? rollbackResult = await WriteSerializedBinaryConfigAsync(
            online.DeviceId, ConfigChannel(definition, online.Channel), definition.WriteCommand.Value,
            Encoding.UTF8.GetBytes(rollback.ToJsonString())).ConfigureAwait(false);
        return rollbackResult >= 0
            ? new(false, true, "A confirmação divergiu; o valor anterior foi restaurado uma vez.")
            : new(false, false, "A confirmação divergiu e a câmera não confirmou a restauração.");
    }

    private static bool NodeBool(JsonNode? node, string name, bool fallback = false)
    {
        if (node is not JsonObject obj || obj[name] is not JsonValue value)
            return fallback;
        return value.TryGetValue<bool>(out bool boolean) ? boolean :
            value.TryGetValue<int>(out int number) ? number != 0 : fallback;
    }

    private static int NodeInt(JsonNode? node, string name, int fallback = 0)
    {
        if (node is not JsonObject obj || obj[name] is not JsonValue value)
            return fallback;
        return value.TryGetValue<int>(out int number) ? number : fallback;
    }

    private static string NodeText(JsonNode? node, string name, string fallback = "Não informado")
    {
        if (node is JsonValue scalar)
        {
            try { return scalar.GetValue<string>(); }
            catch { return scalar.ToJsonString().Trim('"'); }
        }
        if (node is not JsonObject obj || obj[name] is null)
            return fallback;
        try { return obj[name]!.GetValue<string>(); }
        catch { return obj[name]!.ToJsonString().Trim('"'); }
    }

    private static void SetNodeValue(JsonNode node, string name, object value)
    {
        if (node is not JsonObject obj)
            return;
        obj[name] = JsonValue.Create(value);
    }

    private static void SetBooleanLike(JsonNode node, string name, bool value)
    {
        if (node is not JsonObject obj)
            return;
        bool numeric = obj[name] is JsonValue current && current.TryGetValue<int>(out _);
        obj[name] = numeric ? JsonValue.Create(value ? 1 : 0) : JsonValue.Create(value);
    }

    private static void SetNestedNodeValue(JsonNode node, string container, string name, object value)
    {
        if (node is not JsonObject obj)
            return;
        if (obj[container] is not JsonObject nested)
            obj[container] = nested = new JsonObject();
        nested[name] = JsonValue.Create(value);
    }

    private static void SetNestedBooleanLike(JsonNode node, string container, string name, bool value)
    {
        if (node is not JsonObject obj)
            return;
        if (obj[container] is not JsonObject nested)
            obj[container] = nested = new JsonObject();
        bool numeric = nested[name] is JsonValue current && current.TryGetValue<int>(out _);
        nested[name] = numeric ? JsonValue.Create(value ? 1 : 0) : JsonValue.Create(value);
    }

    private void ShowFunctionalDeviceSettings(CloudApi.AccountDevice device)
    {
        PreviewBinding? online = previewBindings.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.CloudId, device.CloudId, StringComparison.Ordinal) &&
            confirmedPreviewWindows.ContainsKey(candidate.Window));
        var window = new Window
        {
            Owner = this,
            Title = $"Configurações — {device.Alias}",
            Width = 1040,
            Height = 760,
            MinWidth = 850,
            MinHeight = 600,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(8, 20, 33))
        };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock
        {
            Text = device.Alias,
            Foreground = Brushes.White,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(24, 20, 24, 12)
        };
        root.Children.Add(title);
        var status = new TextBlock
        {
            Text = online is null ? "Abra a imagem desta câmera para consultar e alterar o aparelho."
                : "Consultando as configurações aceitas por esta câmera…",
            Foreground = new SolidColorBrush(Color.FromRgb(142, 162, 188)),
            Margin = new Thickness(24, 10, 24, 16),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(status, 2);
        root.Children.Add(status);
        var tabs = new TabControl
        {
            TabStripPlacement = Dock.Left,
            Margin = new Thickness(16, 0, 16, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);
        window.Content = root;

        var configs = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var failures = new HashSet<string>(StringComparer.Ordinal);

        static StackPanel Page(string heading)
        {
            var panel = new StackPanel { Margin = new Thickness(22, 8, 22, 22) };
            panel.Children.Add(new TextBlock
            {
                Text = heading, Foreground = Brushes.White, FontSize = 23,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14)
            });
            return panel;
        }

        static Border Row(string label, string detail, UIElement? control = null)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = detail, Foreground = new SolidColorBrush(Color.FromRgb(142, 162, 188)), Margin = new Thickness(0, 4, 18, 0), TextWrapping = TextWrapping.Wrap });
            grid.Children.Add(text);
            if (control is not null) { Grid.SetColumn(control, 1); grid.Children.Add(control); }
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(14, 31, 49)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(38, 58, 80)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(17), Margin = new Thickness(0, 0, 0, 10), Child = grid
            };
        }

        static Button Button(string text) => new()
        {
            Content = text, Padding = new Thickness(14, 8, 14, 8),
            MinWidth = 115, VerticalAlignment = VerticalAlignment.Center
        };

        static System.Windows.Controls.Primitives.ToggleButton Switch(bool on) => new()
        {
            IsChecked = on, Content = on ? "Ligado" : "Desligado", MinWidth = 105,
            Padding = new Thickness(12, 8, 12, 8), VerticalAlignment = VerticalAlignment.Center
        };

        static TabItem Tab(string name, StackPanel page) => new()
        {
            Header = name, MinWidth = 190, Padding = new Thickness(16, 11, 16, 11),
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = page }
        };

        JsonNode? Config(string key)
        {
            if (!configs.TryGetValue(key, out JsonObject? root))
                return null;
            DeviceConfigurationCatalog.Definition definition = DeviceConfigurationCatalog.Find(key)!;
            return root[ExactJsonName(definition, online?.Channel ?? 0)];
        }

        async Task Apply(string key, Action<JsonNode> mutation, bool sensitive = false)
        {
            if (online is null || !configs.TryGetValue(key, out JsonObject? original))
            {
                status.Text = "Esta configuração não foi fornecida pela câmera.";
                return;
            }
            DeviceConfigurationCatalog.Definition definition = DeviceConfigurationCatalog.Find(key)!;
            status.Text = "Aplicando e confirmando na câmera…";
            ConfigurationWriteResult result = await WriteGenericFieldAsync(
                device, online, definition, original, mutation, sensitive);
            status.Text = result.Message;
            if (result.Success)
            {
                string exactName = ExactJsonName(definition, online.Channel);
                if (original[exactName] is JsonNode cached)
                    mutation(cached);
            }
            BuildPages(tabs.SelectedIndex);
        }

        async Task Rename()
        {
            string? value = PromptText(window, "Nome do dispositivo", "Novo nome", device.Alias, false);
            if (string.IsNullOrWhiteSpace(value)) return;
            await Apply("Basic.General", node => SetNodeValue(node, "MachineName", value.Trim()));
            if (!status.Text.StartsWith("Alteração aplicada", StringComparison.Ordinal)) return;
            device.Alias = value.Trim();
            CameraCatalogStore.Entry entry = cameraCatalog.GetOrCreate(device, Math.Max(0, DeviceBox.SelectedIndex));
            entry.Name = device.Alias; entry.UseCustomName = false;
            SaveCameraCatalog(); DeviceBox.Items.Refresh(); RefreshPreviewNames(device); UpdateCameraSummary();
            title.Text = device.Alias; window.Title = $"Configurações — {device.Alias}";
        }

        void AddToggle(StackPanel page, string key, string field, string label, string detail,
            string? nested = null)
        {
            JsonNode? node = Config(key);
            bool fieldAvailable = node is JsonObject source && (nested is null
                ? source.ContainsKey(field)
                : source[nested] is JsonObject nestedObject && nestedObject.ContainsKey(field));
            bool value = nested is null ? NodeBool(node, field) :
                node is JsonObject obj && NodeBool(obj[nested], field);
            var toggle = Switch(value);
            toggle.IsEnabled = fieldAvailable;
            toggle.Click += async (_, _) =>
            {
                bool requested = toggle.IsChecked == true;
                toggle.IsEnabled = false;
                await Apply(key, n => { if (nested is null) SetBooleanLike(n, field, requested); else SetNestedBooleanLike(n, nested, field, requested); });
            };
            page.Children.Add(Row(label, !fieldAvailable ? "Não oferecido por este firmware" : detail, toggle));
        }

        void AddNumber(StackPanel page, string key, string field, string label, int min, int max, string suffix = "")
        {
            JsonNode? node = Config(key);
            bool available = node is JsonObject objectWithField && objectWithField.ContainsKey(field);
            int value = Math.Clamp(NodeInt(node, field, min), min, max);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 180, TickFrequency = 1, IsSnapToTickEnabled = true, IsEnabled = available };
            var current = new TextBlock { Text = $"{value}{suffix}", Foreground = Brushes.White, Width = 70, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, _) => current.Text = $"{(int)slider.Value}{suffix}";
            var save = Button("Salvar"); save.IsEnabled = available; save.Margin = new Thickness(8, 0, 0, 0);
            save.Click += async (_, _) => { save.IsEnabled = false; await Apply(key, n => SetNodeValue(n, field, (int)slider.Value)); };
            panel.Children.Add(slider); panel.Children.Add(current); panel.Children.Add(save);
            page.Children.Add(Row(label, available ? $"Faixa permitida: {min} a {max}{suffix}" : "Não oferecido por este firmware", panel));
        }

        void AddNestedNumber(StackPanel page, string key, string container, string field,
            string label, int min, int max, string suffix = "")
        {
            JsonNode? node = Config(key);
            JsonObject? nested = node is JsonObject obj ? obj[container] as JsonObject : null;
            bool available = nested?.ContainsKey(field) == true;
            int value = Math.Clamp(NodeInt(nested, field, min), min, max);
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 180, TickFrequency = 1, IsSnapToTickEnabled = true, IsEnabled = available };
            var current = new TextBlock { Text = $"{value}{suffix}", Foreground = Brushes.White, Width = 70, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, _) => current.Text = $"{(int)slider.Value}{suffix}";
            var save = Button("Salvar"); save.IsEnabled = available; save.Margin = new Thickness(8, 0, 0, 0);
            save.Click += async (_, _) => { save.IsEnabled = false; await Apply(key, n => SetNestedNodeValue(n, container, field, (int)slider.Value)); };
            panel.Children.Add(slider); panel.Children.Add(current); panel.Children.Add(save);
            page.Children.Add(Row(label, available ? $"Faixa permitida: {min} a {max}{suffix}" : "Não oferecido por este firmware", panel));
        }

        void BuildPages(int selected)
        {
            tabs.Items.Clear();
            var basic = Page("Configuração básica");
            var rename = Button("Alterar"); rename.IsEnabled = Config("Basic.General") is not null; rename.Click += async (_, _) => await Rename();
            basic.Children.Add(Row("Nome do dispositivo", NodeText(Config("Basic.General"), "MachineName", device.Alias), rename));
            AddToggle(basic, "Camera.Parameters", "PictureMirror", "Girar a imagem para esquerda-direita", "Alteração gravada no aparelho.");
            AddToggle(basic, "Camera.Parameters", "PictureFlip", "Girar a imagem para cima-baixo", "Alteração gravada no aparelho.");
            basic.Children.Add(Row("Idioma", NodeText(Config("Basic.Location"), "Language")));
            AddNumber(basic, "Camera.Parameters", "DncThr", "Sensibilidade de troca dia/noite", 0, 10);
            AddNumber(basic, "Audio.SpeakerVolume", "LeftVolume", "Volume do alto-falante", 1, 100);
            var sync = Button("Sincronizar agora"); sync.IsEnabled = online is not null;
            sync.Click += async (_, _) => await SynchronizeDeviceTimeAsync(device, online!, status);
            basic.Children.Add(Row("Configuração de hora", "Sincroniza a hora da câmera com este computador.", sync));
            tabs.Items.Add(Tab("Básicas", basic));

            var recording = Page("Configurações de gravação");
            JsonNode? recordSwitchNode = Config("Recording.Main");
            var recordSwitch = Switch(recordSwitchNode is not null && NodeInt(recordSwitchNode, "RecordMode", 2) != 2);
            recordSwitch.IsEnabled = recordSwitchNode is not null;
            recordSwitch.Click += async (_, _) =>
            {
                bool enabled = recordSwitch.IsChecked == true;
                recordSwitch.IsEnabled = false;
                await Apply("Recording.Main", node => SetNodeValue(node, "RecordMode", enabled ? 0 : 2));
            };
            recording.Children.Add(Row("Botão REC", "Ativa ou desativa a gravação no dispositivo.", recordSwitch));
            AddNumber(recording, "Recording.Main", "PacketLength", "Duração da gravação", 5, 120, " min");
            JsonNode? record = Config("Recording.Main");
            recording.Children.Add(Row("Modo de gravação", record is null ? "Não oferecido" : NodeInt(record, "RecordMode") == 0 ? "Gravação 24 horas/agendada" : NodeInt(record, "RecordMode") == 1 ? "Gravação manual" : "Gravação desativada"));
            tabs.Items.Add(Tab("Gravação", recording));

            var alarm = Page("Alarme inteligente");
            AddToggle(alarm, "Alarm.Motion", "Enable", "Ligado", "Chave principal de detecção de movimento.");
            AddToggle(alarm, "Alarm.Human", "Enable", "Detecção de corpo humano", "Reconhecimento humano da câmera.");
            AddToggle(alarm, "Alarm.Human", "RecordEnable", "Gravar vídeo ao detectar", "Vínculo de gravação do alarme.", "EventHandler");
            AddToggle(alarm, "Alarm.Human", "SnapEnable", "Capturar imagem ao detectar", "Vínculo de captura do alarme.", "EventHandler");
            alarm.Children.Add(Row("Notificações do sistema", "WeChat e Não perturbe pertencem ao aplicativo móvel; não alteram o aparelho."));
            tabs.Items.Add(Tab("Alarme inteligente", alarm));

            var light = Page("Modo de controle da luz");
            JsonNode? lightNode = Config("Light.White");
            var modeValues = new Dictionary<string, string>
            {
                ["Automático"] = "Auto", ["Sempre ligada"] = "KeepOpen",
                ["Desligada"] = "Close", ["Por horário"] = "Timing",
                ["Alerta de luz dupla"] = "Intelligent"
            };
            var mode = new System.Windows.Controls.ComboBox
            {
                ItemsSource = modeValues.Keys, MinWidth = 190, Padding = new Thickness(8, 6, 8, 6),
                IsEnabled = lightNode is JsonObject lightObject && lightObject.ContainsKey("WorkMode")
            };
            string currentMode = NodeText(lightNode, "WorkMode", "");
            mode.SelectedItem = modeValues.FirstOrDefault(item => item.Value == currentMode).Key ?? currentMode;
            var saveMode = Button("Salvar"); saveMode.Margin = new Thickness(8, 0, 0, 0); saveMode.IsEnabled = mode.IsEnabled;
            saveMode.Click += async (_, _) =>
            {
                if (mode.SelectedItem is not string selected || !modeValues.TryGetValue(selected, out string? protocolValue)) return;
                saveMode.IsEnabled = false;
                await Apply("Light.White", n => SetNodeValue(n, "WorkMode", protocolValue));
            };
            var modePanel = new StackPanel { Orientation = Orientation.Horizontal };
            modePanel.Children.Add(mode); modePanel.Children.Add(saveMode);
            light.Children.Add(Row("Modo de controle", "Full Night Vision, infravermelho, luz contínua ou alerta inteligente.", modePanel));
            AddNumber(light, "Light.White", "Brightness", "Brilho da luz", 0, 100);
            AddNestedNumber(light, "Light.White", "MoveTrigLight", "Level", "Sensibilidade", 0, 5);
            AddNestedNumber(light, "Light.White", "MoveTrigLight", "Duration", "Duração da luz ligada", 0, 600, " s");
            AddToggle(light, "Alarm.IntelligentAlert", "Enable", "Alerta inteligente", "Ativa o vínculo inteligente de alarme.");
            tabs.Items.Add(Tab("Som e luz", light));

            var tracking = Page("Configurações avançadas");
            AddToggle(tracking, "Tracking.Motion", "Enable", "Rastreamento de movimento", "A câmera acompanha o movimento.");
            AddNumber(tracking, "Tracking.Motion", "Sensitivity", "Sensibilidade do rastreamento", 0, 2);
            AddNumber(tracking, "Tracking.Motion", "ReturnTime", "Hora daemon/retorno", 0, 600, " s");
            tracking.Children.Add(Row("Configuração WDR", NodeText(Config("Camera.Parameters"), "BLCMode")));
            tabs.Items.Add(Tab("Avançadas", tracking));

            var storage = Page("Gerenciamento de armazenamento");
            DeviceReadOnlyConfigStore.DeviceData stored = readOnlyDeviceConfigs.GetOrCreate(device.CloudId);
            if (stored.Storage is null)
            {
                var load = Button("Consultar"); load.IsEnabled = online is not null;
                load.Click += async (_, _) => { load.IsEnabled = false; status.Text = "Consultando o cartão SD…"; await ReadStorageOnDemandAsync(device, online!); BuildPages(tabs.SelectedIndex); status.Text = "Armazenamento atualizado."; };
                storage.Children.Add(Row("Cartão SD", "Capacidade ainda não consultada.", load));
            }
            else
            {
                double total = stored.Storage.Partitions.Sum(p => p.TotalMegabytes) / 1024d;
                double free = stored.Storage.Partitions.Sum(p => p.FreeMegabytes) / 1024d;
                storage.Children.Add(Row("Capacidade de armazenamento", $"{total:F2} GB"));
                storage.Children.Add(Row("Espaço livre", $"{free:F2} GB"));
                JsonNode? general = Config("Basic.General");
                bool cyclic = string.Equals(NodeText(general, "OverWrite", ""), "OverWrite", StringComparison.OrdinalIgnoreCase);
                var overwrite = Switch(cyclic); overwrite.IsEnabled = general is JsonObject generalObject && generalObject.ContainsKey("OverWrite");
                overwrite.Click += async (_, _) =>
                {
                    bool requested = overwrite.IsChecked == true;
                    overwrite.IsEnabled = false;
                    await Apply("Basic.General", n => SetNodeValue(n, "OverWrite", requested ? "OverWrite" : "StopRecord"));
                };
                storage.Children.Add(Row("Gravação cíclica", "Sobrescreve os arquivos mais antigos quando o cartão fica cheio.", overwrite));
                storage.Children.Add(Row("Formato", "A formatação não é exibida sem um comando destrutivo validado para este firmware."));
            }
            tabs.Items.Add(Tab("Armazenamento", storage));

            var network = Page("Configurações de rede");
            JsonNode? wifi = Config("Network.Wifi");
            network.Children.Add(Row("Modo de roteamento", wifi is null ? "Não oferecido" : NodeBool(wifi, "Enable") ? "Ligado" : "Desligado"));
            network.Children.Add(Row("Rede Wi‑Fi", NodeText(wifi, "SSID")));
            var wifiEdit = Button("Alterar Wi‑Fi"); wifiEdit.IsEnabled = wifi is not null;
            wifiEdit.Click += async (_, _) =>
            {
                string? ssid = PromptText(window, "Rede Wi‑Fi", "Nome da rede (SSID)", NodeText(wifi, "SSID", ""), false);
                if (string.IsNullOrWhiteSpace(ssid)) return;
                string? password = PromptText(window, "Rede Wi‑Fi", "Senha da rede", "", true);
                if (password is null) return;
                if (MessageBox.Show(window, "A câmera pode ficar offline ao trocar de Wi‑Fi. Continuar?", "Confirmar alteração de rede", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                await Apply("Network.Wifi", node => { SetNodeValue(node, "SSID", ssid.Trim()); if (password.Length > 0) SetNodeValue(node, "Keys", password); }, true);
            };
            network.Children.Add(Row("Alterar rede e senha", "A câmera poderá desconectar e voltar pela nova rede.", wifiEdit));
            tabs.Items.Add(Tab("Rede", network));

            var about = Page("Sobre o dispositivo");
            string serial = device.CloudId.Length <= 8 ? "••••" : device.CloudId[..4] + "••••" + device.CloudId[^4..];
            about.Children.Add(Row("N.º de série", serial));
            about.Children.Add(Row("Nome de login do dispositivo", string.IsNullOrWhiteSpace(device.DeviceUser) ? "Não informado" : device.DeviceUser[..Math.Min(1, device.DeviceUser.Length)] + "•••"));
            about.Children.Add(Row("Versão do dispositivo", string.IsNullOrWhiteSpace(device.Model) ? "Não informado" : device.Model));
            about.Children.Add(Row("Versão do software", string.IsNullOrWhiteSpace(device.Firmware) ? "Não informado" : device.Firmware));
            about.Children.Add(Row("Fuso horário", NodeText(Config("Time.TimeZone"), "timeMin")));
            about.Children.Add(Row("Hora do dispositivo", NodeText(Config("Time.Current"), "OPTimeQuery")));
            about.Children.Add(Row("Modo de rede", device.IsNetworkDevice ? "LAN" : "RPS / Cloud P2P"));
            tabs.Items.Add(Tab("Sobre", about));

            tabs.SelectedIndex = Math.Clamp(selected, 0, tabs.Items.Count - 1);
        }

        window.Loaded += async (_, _) =>
        {
            BuildPages(0);
            if (online is null) return;
            string[] keys =
            [
                "Basic.General", "Basic.Location", "Time.TimeZone", "Time.Current",
                "Recording.Main", "Alarm.Motion", "Alarm.Human", "Tracking.Motion",
                "Light.White", "Alarm.IntelligentAlert", "Audio.SpeakerVolume",
                "Camera.Parameters", "Network.Wifi"
            ];
            foreach (string key in keys)
            {
                if (isClosing || !window.IsVisible) break;
                DeviceConfigurationCatalog.Definition definition = DeviceConfigurationCatalog.Find(key)!;
                JsonObject? response = await ReadGenericConfigAsync(online.DeviceId, online.Channel, definition);
                if (response is null) failures.Add(key);
                else
                {
                    configs[key] = response;
                    RecordMappedConfiguration(device, key, "CMS JSON validado", DateTime.UtcNow);
                }
                BuildPages(tabs.SelectedIndex);
            }
            status.Text = failures.Count == 0
                ? "Configurações carregadas. Cada alteração será gravada e relida para confirmação."
                : $"Configurações carregadas; {failures.Count} recurso(s) não foram oferecidos por este firmware.";
        };
        window.ShowDialog();
    }

    private static string? PromptText(Window owner, string title, string label, string initial, bool password)
    {
        var dialog = new Window
        {
            Owner = owner, Title = title, Width = 440, Height = 225,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(8, 20, 33))
        };
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold });
        Control input;
        if (password) input = new PasswordBox { Margin = new Thickness(0, 12, 0, 16), Padding = new Thickness(8), Password = initial };
        else input = new TextBox { Margin = new Thickness(0, 12, 0, 16), Padding = new Thickness(8), Text = initial, MaxLength = 64 };
        panel.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(14, 7, 14, 7) });
        var save = new Button { Content = "Confirmar", IsDefault = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(save); panel.Children.Add(actions); dialog.Content = panel;
        if (dialog.ShowDialog() != true) return null;
        return password ? ((PasswordBox)input).Password : ((TextBox)input).Text;
    }

    private async Task SynchronizeDeviceTimeAsync(
        CloudApi.AccountDevice device, PreviewBinding online, TextBlock status)
    {
        DeviceConfigurationCatalog.Definition definition = DeviceConfigurationCatalog.Find("Time.Current")!;
        string value = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var envelope = new JsonObject { ["Name"] = "OPTimeSetting", ["OPTimeSetting"] = value };
        status.Text = "Sincronizando a hora do dispositivo…";
        int? result = await WriteSerializedBinaryConfigAsync(online.DeviceId, -1,
            definition.WriteCommand!.Value, Encoding.UTF8.GetBytes(envelope.ToJsonString()));
        status.Text = result >= 0 ? "Hora enviada à câmera; consulte novamente para conferir."
            : "A câmera não confirmou a sincronização de hora.";
    }
}
