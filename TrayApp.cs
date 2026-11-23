using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace MonitorDial;

/// <summary>Ícone na bandeja, menu de contexto e ciclo de vida do painel.</summary>
public sealed class TrayApp
{
    private const int BrightnessRefreshMs = 60_000;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private readonly WF.NotifyIcon _tray = new();
    private readonly WF.Timer _brightnessTimer = new() { Interval = BrightnessRefreshMs };
    private readonly WF.Timer _updateTimer = new() { Interval = (int)UpdateCheckInterval.TotalMilliseconds };
    private readonly WF.ToolStripMenuItem _autoStartItem;
    private readonly SynchronizationContext _ui;
    private System.Drawing.Icon? _icon;
    private string _iconKey = "";
    private MainWindow? _panel;
    private string? _notifiedVersion;

    public TrayApp(bool startHidden)
    {
        _ui = SynchronizationContext.Current!;

        _autoStartItem = new WF.ToolStripMenuItem("Iniciar com o Windows") { Checked = AutoStart.Enabled, CheckOnClick = true };
        _autoStartItem.CheckedChanged += (_, _) => AutoStart.Enabled = _autoStartItem.Checked;

        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Abrir painel", null, (_, _) => ShowPanel());
        menu.Items.Add("Configurações", null, (_, _) => ShowPanel(settings: true));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Exit());
        menu.Opening += (_, _) => _autoStartItem.Checked = AutoStart.Enabled;
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left) TogglePanel();
        };
        _tray.BalloonTipClicked += (_, _) => ShowPanel(settings: true);

        Render();
        _tray.Visible = true;

        MonitorHub.BrightnessChanged += OnBrightnessChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        _brightnessTimer.Tick += async (_, _) => await MonitorHub.RefreshBrightnessAsync();
        _brightnessTimer.Start();
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync();
        _updateTimer.Start();

        WatchShowSignal();
        _ = StartAsync(startHidden);
    }

    private async Task StartAsync(bool startHidden)
    {
        await MonitorHub.ConnectAsync();
        Render();
        _panel = new MainWindow(this);
        if (!startHidden) ShowPanel();
        await Task.Delay(8000);
        await CheckUpdatesAsync();
    }

    // Outra instância aberta (atalho do menu Iniciar, por exemplo) pede para mostrar o painel.
    private void WatchShowSignal()
    {
        var signal = Program.ShowSignal;
        if (signal is null) return;
        var thread = new Thread(() =>
        {
            while (signal.WaitOne()) _ui.Post(_ => ShowPanel(), null);
        })
        { IsBackground = true, Name = "MonitorDial.ShowSignal" };
        thread.Start();
    }

    public void ShowPanel(bool settings = false)
    {
        if (_panel is null) return;
        _panel.ShowAtCorner(settings);
    }

    private void TogglePanel()
    {
        if (_panel is null) return;
        if (_panel.AppWindow.IsVisible) _panel.AppWindow.Hide();
        else ShowPanel();
    }

    private void Render()
    {
        bool light = TaskbarTheme.IsLight();
        int size = WF.SystemInformation.SmallIconSize.Width;
        int? percent = MonitorHub.Brightness >= 0 ? MonitorHub.Brightness : null;
        string key = $"{percent}|{light}|{size}";
        if (key != _iconKey)
        {
            var old = _icon;
            _icon = TrayIcon.Draw(size, percent, light);
            _tray.Icon = _icon;
            if (old is not null) TrayIcon.Destroy(old);
            _iconKey = key;
        }

        string text = MonitorHub.Monitor is null
            ? "MonitorDial: nenhum monitor encontrado"
            : percent is null ? MonitorHub.Name : $"{MonitorHub.Name}\nBrilho {percent}%";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private void OnBrightnessChanged() => _ui.Post(_ => Render(), null);

    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => _ui.Post(_ => Render(), null);

    private async Task CheckUpdatesAsync()
    {
        if (!SettingsStore.Current.CheckUpdates) return;
        var result = await Updater.CheckAsync();
        if (result.State != UpdateState.Available || result.Version == _notifiedVersion) return;
        _notifiedVersion = result.Version;
        _tray.ShowBalloonTip(8000, "Atualização disponível",
            $"MonitorDial {result.Version} está pronto para instalar. Clique para abrir as configurações.",
            WF.ToolTipIcon.Info);
    }

    public void Exit()
    {
        MonitorHub.BrightnessChanged -= OnBrightnessChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        _brightnessTimer.Stop();
        _updateTimer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        if (_icon is not null) TrayIcon.Destroy(_icon);
        _panel?.CloseForExit();
        MonitorHub.Disconnect();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }
}
