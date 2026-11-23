using Microsoft.UI.Xaml;

namespace MonitorDial;

public partial class App : Application
{
    private readonly bool _startHidden;
    private TrayApp? _tray;

    public App(bool startHidden)
    {
        _startHidden = startHidden;
        InitializeComponent();
        // O app vive na bandeja: fechar o painel não encerra o processo.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args) => _tray = new TrayApp(_startHidden);
}
