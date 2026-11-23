using System.Text.RegularExpressions;

namespace MonitorDial;

/// <summary>Monitor em uso, compartilhado entre o painel e o ícone da bandeja.</summary>
public static class MonitorHub
{
    public static DdcMonitor? Monitor { get; private set; }
    public static string Name { get; private set; } = "";
    public static int Brightness { get; private set; } = -1;

    /// <summary>Disparado quando o brilho conhecido muda (o ícone da bandeja acompanha).</summary>
    public static event Action? BrightnessChanged;

    public static async Task<bool> ConnectAsync()
    {
        if (Monitor is not null) return true;
        var monitors = await Task.Run(DdcMonitor.Enumerate);
        if (monitors.Count == 0) return false;

        Monitor = monitors[0];
        foreach (var extra in monitors.Skip(1)) extra.Dispose();

        // A leitura de capacidades às vezes volta vazia na primeira tentativa.
        string model = "";
        for (int attempt = 0; attempt < 3 && model.Length == 0; attempt++)
            model = ParseModel(await Task.Run(Monitor.GetCapabilities));

        Name = model.Length == 0 ? (Monitor.EdidName.Length > 0 ? Monitor.EdidName : Monitor.Description)
            : Monitor.IsLg && !model.StartsWith("LG", StringComparison.OrdinalIgnoreCase) ? "LG " + model
            : model;
        await RefreshBrightnessAsync();
        return true;
    }

    public static async Task RefreshBrightnessAsync()
    {
        if (Monitor is null) return;
        var (ok, current, _) = await Monitor.GetAsync(0x10);
        if (ok) ReportBrightness(current);
    }

    public static void ReportBrightness(int value)
    {
        if (value == Brightness) return;
        Brightness = value;
        BrightnessChanged?.Invoke();
    }

    public static void Disconnect()
    {
        Monitor?.Dispose();
        Monitor = null;
    }

    // A resposta de capacidades traz "model(XYZ)"; alguns LG omitem o rótulo e
    // colam o modelo entre "type(lcd)" e "cmds(".
    private static string ParseModel(string caps)
    {
        var m = Regex.Match(caps, @"model\(([^)]+)\)");
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(caps, @"type\([^)]*\)([A-Za-z0-9\-]+?)cmds\(");
        return m.Success ? m.Groups[1].Value : "";
    }
}
