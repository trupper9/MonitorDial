using Microsoft.UI.Dispatching;
using Velopack;

namespace MonitorDial;

public static class Program
{
    public const string StartupArg = "--startup";
    private const string MutexName = "MonitorDial.Single";
    private const string ShowEventName = "MonitorDial.Show";

    /// <summary>Sinalizado quando outra instância é aberta, para a primeira mostrar o painel.</summary>
    public static EventWaitHandle? ShowSignal { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        VelopackApp.Build().Run();

        // "--selftest <arquivo>" exercita cada controle no monitor real, grava um relatório e sai.
        int i = Array.IndexOf(args, "--selftest");
        if (i >= 0 && i + 1 < args.Length) return SelfTest.Run(args[i + 1]);

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Já está rodando na bandeja: pede para a instância existente abrir o painel.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
            return 0;
        }
        ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            new App(startHidden: args.Contains(StartupArg));
        });
        return 0;
    }
}
