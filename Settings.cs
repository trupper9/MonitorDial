using System.Text.Json;
using Microsoft.Win32;

namespace MonitorDial;

public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

public sealed class AppSettings
{
    public Corner Corner { get; set; } = Corner.BottomRight;
    public bool CheckUpdates { get; set; } = true;
}

/// <summary>
/// Preferências do app, em %LOCALAPPDATA%\MonitorDial.Data\settings.json. Fica fora da pasta
/// de instalação (%LOCALAPPDATA%\MonitorDial) para sobreviver a atualizações e reinstalações.
/// </summary>
public static class SettingsStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name + ".Data");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static AppSettings? _cached;

    public static AppSettings Current => _cached ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // arquivo corrompido: volta ao padrão
        }
        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // sem permissão de escrita: as preferências valem só nesta sessão
        }
    }
}

/// <summary>Iniciar com o Windows, pela chave Run do usuário (sem precisar de administrador).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppInfo.Name) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(AppInfo.Name, $"\"{Environment.ProcessPath}\" {Program.StartupArg}");
            else key.DeleteValue(AppInfo.Name, false);
        }
    }
}

public static class TaskbarTheme
{
    public static bool IsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }
}

public static class AppInfo
{
    public const string Name = "MonitorDial";
    public const string RepoUrl = "https://github.com/trupper9/MonitorDial";

    public static string Version
    {
        get
        {
            var v = typeof(AppInfo).Assembly.GetName().Version;
            return v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
