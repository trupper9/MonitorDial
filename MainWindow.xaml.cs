using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace MonitorDial;

/// <summary>Painel de tamanho fixo que abre num canto da tela, a partir do ícone da bandeja.</summary>
public sealed partial class MainWindow : Window
{
    // Tamanho em pixels independentes de escala; a altura encolhe se a tela for baixa.
    private const int PanelWidth = 440;
    private const int PanelHeight = 720;
    private const int EdgeMargin = 12;

    private static readonly string IconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "MonitorDial.ico");

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);

    private sealed class Binding
    {
        public required VcpControl Control;
        public required FrameworkElement Element;
        public TextBlock? ValueText;
        public int? Pending;
        public bool Sending;
    }

    private readonly TrayApp _tray;
    private readonly IntPtr _hwnd;
    private readonly List<Binding> _bindings = new();
    private bool _built;
    private bool _loading;
    private bool _reloadQueued;
    private bool _settingsLoading;
    private bool _allowClose;

    public MainWindow(TrayApp tray)
    {
        _tray = tray;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        Title = AppInfo.Name;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        if (File.Exists(IconPath))
        {
            AppWindow.SetIcon(IconPath);
            TitleIcon.Source = new BitmapImage(new Uri(IconPath));
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += OnChanged;
        ApplyCorner();
    }

    // ---------- Janela: canto fixo, fechar e minimizar escondem na bandeja ----------

    public void ShowAtCorner(bool settings)
    {
        ApplyCorner();
        ShowSettings(settings);
        AppWindow.Show();
        SetForegroundWindow(_hwnd);
        _ = RefreshAsync();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    private void ApplyCorner()
    {
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        var work = DisplayArea.Primary.WorkArea;
        int margin = (int)(EdgeMargin * scale);
        int width = (int)(PanelWidth * scale);
        int height = Math.Min((int)(PanelHeight * scale), work.Height - 2 * margin);

        var corner = SettingsStore.Current.Corner;
        bool right = corner is Corner.TopRight or Corner.BottomRight;
        bool bottom = corner is Corner.BottomLeft or Corner.BottomRight;
        int x = right ? work.X + work.Width - width - margin : work.X + margin;
        int y = bottom ? work.Y + work.Height - height - margin : work.Y + margin;
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        AppWindow.Hide();
    }

    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
        {
            p.Restore();
            AppWindow.Hide();
        }
    }

    // ---------- Construção dos controles ----------

    private async Task RefreshAsync()
    {
        if (!_built)
        {
            if (!await MonitorHub.ConnectAsync())
            {
                MonitorName.Text = "Nenhum monitor encontrado";
                MonitorInfo.Text = "Nenhum monitor respondeu a DDC/CI. Confira se a opção DDC/CI está ligada no menu do monitor e toque em Recarregar.";
                return;
            }
            var monitor = MonitorHub.Monitor!;
            MonitorName.Text = MonitorHub.Name;
            MonitorInfo.Text = monitor.PnpId.Length > 0 ? $"DDC/CI · {monitor.PnpId}" : "DDC/CI";
            BuildUi(monitor.IsLg);
            _built = true;
        }
        await LoadAsync();
    }

    private void BuildUi(bool isLg)
    {
        Root.Children.Clear();
        _bindings.Clear();
        var controls = ControlCatalog.For(isLg).ToList();
        foreach (string section in ControlCatalog.Sections)
        {
            var inSection = controls.Where(c => c.Section == section).ToList();
            if (inSection.Count == 0) continue;
            var panel = AddCard(section);
            foreach (var c in inSection) panel.Children.Add(BuildRow(c));
        }

        var reset = AddCard("Restaurar");
        reset.Children.Add(new TextBlock
        {
            Text = "Volta os ajustes ao padrão de fábrica. No LG 34WK500 estes comandos também redefinem tempo de resposta e FreeSync.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });
        var buttons = new StackPanel { Spacing = 8 };
        buttons.Children.Add(MakeResetButton("Restaurar brilho e contraste", "resetLuminance", ControlCatalog.ResetLuminance));
        buttons.Children.Add(MakeResetButton("Restaurar cor", "resetColor", ControlCatalog.ResetColor));
        reset.Children.Add(buttons);
    }

    private StackPanel AddCard(string title)
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        Root.Children.Add(new Border { Style = (Style)Application.Current.Resources["CardStyle"], Child = panel });
        return panel;
    }

    private FrameworkElement BuildRow(VcpControl c)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        grid.Children.Add(new TextBlock { Text = c.Label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });

        var binding = new Binding { Control = c, Element = null! };
        FrameworkElement element;
        switch (c.Kind)
        {
            case ControlKind.Range:
                var slider = new Slider
                {
                    Minimum = c.Min,
                    Maximum = c.Max == 0 ? 255 : c.Max,
                    StepFrequency = c.Step,
                    SmallChange = c.Step,
                    LargeChange = Math.Max(c.Step, 10),
                    SnapsTo = SliderSnapsTo.StepValues,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetColumn(text, 2);
                grid.Children.Add(text);
                binding.ValueText = text;
                slider.ValueChanged += (_, e) =>
                {
                    text.Text = ((int)e.NewValue).ToString();
                    if (!_loading) Queue(binding, (int)e.NewValue);
                };
                element = slider;
                break;

            case ControlKind.Choice:
                var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var (value, label) in c.Options!) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
                combo.SelectionChanged += (_, _) =>
                {
                    if (!_loading && combo.SelectedItem is ComboBoxItem item) Queue(binding, (int)item.Tag);
                };
                Grid.SetColumnSpan(combo, 2);
                element = combo;
                break;

            default:
                var toggle = new ToggleSwitch { OnContent = "Ligado", OffContent = "Desligado" };
                toggle.Toggled += (_, _) =>
                {
                    if (!_loading) Queue(binding, toggle.IsOn ? c.OnValue : c.OffValue);
                };
                Grid.SetColumnSpan(toggle, 2);
                element = toggle;
                break;
        }

        AutomationProperties.SetName(element, c.Label);
        AutomationProperties.SetAutomationId(element, c.Key);
        Grid.SetColumn(element, 1);
        grid.Children.Add(element);
        binding.Element = element;
        _bindings.Add(binding);
        return grid;
    }

    private Button MakeResetButton(string label, string id, byte code)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += async (_, _) =>
        {
            var monitor = MonitorHub.Monitor;
            if (monitor is null) return;
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = label + "?",
                Content = "Os ajustes voltam ao padrão de fábrica. Não dá para desfazer.",
                PrimaryButtonText = "Restaurar",
                CloseButtonText = "Cancelar",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            bool ok = await monitor.SetAsync(code, 1);
            if (!ok) { ShowError("O monitor não aceitou o comando de restauração."); return; }
            await Task.Delay(2000);
            await LoadAsync();
        };
        return button;
    }

    // ---------- Leitura e escrita no monitor ----------

    /// <summary>Lê todos os ajustes do monitor e atualiza a tela.</summary>
    private async Task LoadAsync()
    {
        var monitor = MonitorHub.Monitor;
        if (monitor is null || !_built) return;
        if (_loading) { _reloadQueued = true; return; }
        _loading = true;
        ReloadButton.IsEnabled = false;
        int failed = 0;
        try
        {
            foreach (var b in _bindings)
            {
                if (b.Sending || b.Pending is not null) continue;   // não atropela um ajuste em andamento
                var (ok, current, max) = await monitor.GetAsync(b.Control.Code);
                if (b.Element is Control ctl) ctl.IsEnabled = ok;
                if (!ok) { failed++; continue; }
                Apply(b, current, max);
                if (b.Control.Key == "brightness") MonitorHub.ReportBrightness(current);
            }
        }
        finally
        {
            _loading = false;
            ReloadButton.IsEnabled = true;
        }
        if (failed > 0) ShowError($"{failed} ajuste(s) não responderam à leitura. Toque em Recarregar.");
        if (_reloadQueued) { _reloadQueued = false; await LoadAsync(); }
    }

    private static void Apply(Binding b, int current, int max)
    {
        switch (b.Element)
        {
            case Slider s:
                if (b.Control.Max == 0 && max > 0) s.Maximum = max;
                s.Value = Math.Clamp(current, s.Minimum, s.Maximum);
                if (b.ValueText is not null) b.ValueText.Text = ((int)s.Value).ToString();
                break;
            case ComboBox cb:
                cb.SelectedItem = cb.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == current);
                break;
            case ToggleSwitch t:
                t.IsOn = current == b.Control.OnValue;
                break;
        }
    }

    /// <summary>
    /// Envia o valor ao monitor. Enquanto um envio está em andamento, só o valor mais
    /// recente fica na fila, para o controle deslizante não acumular comandos.
    /// </summary>
    private async void Queue(Binding b, int value)
    {
        var monitor = MonitorHub.Monitor;
        if (monitor is null) return;
        b.Pending = value;
        if (b.Sending) return;
        b.Sending = true;
        bool allOk = true;
        int last = value;
        try
        {
            while (b.Pending is int next)
            {
                b.Pending = null;
                last = next;
                allOk &= await monitor.SetAsync(b.Control.Code, b.Control.ToWire(next));
            }
            if (allOk && b.Control.Key == "brightness") MonitorHub.ReportBrightness(last);

            // Listas e chaves: confere se o monitor realmente aplicou. Alguns valores
            // são aceitos sem erro e ignorados, dependendo de outros ajustes.
            if (allOk && b.Control.Kind != ControlKind.Range)
            {
                await Task.Delay(b.Control.SettleMs);
                var (ok, current, max) = await monitor.GetAsync(b.Control.Code);
                if (ok && current != last && b.Pending is null)
                {
                    bool wasLoading = _loading;
                    _loading = true;
                    try { Apply(b, current, max); } finally { _loading = wasLoading; }
                    ShowError($"O monitor não aplicou {b.Control.Label}. {b.Control.RejectHint}".Trim());
                }
            }
        }
        finally { b.Sending = false; }

        if (!allOk) ShowError($"O monitor não aceitou o ajuste de {b.Control.Label}.");

        bool affectsOthers = b.Control.ReloadAllAfter
            || b.Control.Key is "red" or "green" or "blue"    // RGB troca a temperatura de cor
            || b.Control.Key is "volume" or "mute";           // volume 0 e mudo andam juntos
        if (affectsOthers)
        {
            await Task.Delay(600);
            if (b.Pending is null && !b.Sending) await LoadAsync();
        }
    }

    private void ShowError(string message)
    {
        Status.Severity = InfoBarSeverity.Error;
        Status.Title = "Erro";
        Status.Message = message;
        Status.IsOpen = true;
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ---------- Configurações ----------

    private void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings(SettingsButton.IsChecked == true);

    private void ShowSettings(bool show)
    {
        SettingsButton.IsChecked = show;
        SettingsView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ControlsView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ReloadButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) return;

        _settingsLoading = true;
        try
        {
            StartupToggle.IsOn = AutoStart.Enabled;
            AutoUpdateToggle.IsOn = SettingsStore.Current.CheckUpdates;
            string corner = SettingsStore.Current.Corner.ToString();
            CornerBox.SelectedItem = CornerBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == corner);
            VersionText.Text = $"Versão instalada: {AppInfo.Version}";
            if (Updater.PendingVersion is string pending) OfferUpdate(pending);
        }
        finally { _settingsLoading = false; }
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoading) AutoStart.Enabled = StartupToggle.IsOn;
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading) return;
        SettingsStore.Current.CheckUpdates = AutoUpdateToggle.IsOn;
        SettingsStore.Save();
    }

    private void OnCornerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsLoading || CornerBox.SelectedItem is not ComboBoxItem item) return;
        if (!Enum.TryParse((string)item.Tag, out Corner corner)) return;
        SettingsStore.Current.Corner = corner;
        SettingsStore.Save();
        ApplyCorner();
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Procurando...";
        var result = await Updater.CheckAsync();
        CheckUpdateButton.IsEnabled = true;
        switch (result.State)
        {
            case UpdateState.Available:
                OfferUpdate(result.Version!);
                break;
            case UpdateState.UpToDate:
                UpdateStatus.Text = "Você já está na versão mais recente.";
                break;
            case UpdateState.NotInstalled:
                UpdateStatus.Text = "As atualizações funcionam na versão instalada pelo instalador (Setup).";
                break;
            default:
                UpdateStatus.Text = "Não foi possível verificar agora. Confira a conexão e tente de novo.";
                break;
        }
    }

    private void OfferUpdate(string version)
    {
        UpdateStatus.Text = $"Versão {version} disponível.";
        InstallUpdateButton.Visibility = Visibility.Visible;
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Baixando a atualização. O app reinicia sozinho ao terminar.";
        if (await Updater.DownloadAndRestartAsync()) return;
        UpdateStatus.Text = "A atualização falhou. Tente de novo mais tarde.";
        InstallUpdateButton.IsEnabled = true;
        CheckUpdateButton.IsEnabled = true;
    }
}
