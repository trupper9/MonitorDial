using Velopack;
using Velopack.Sources;

namespace MonitorDial;

public enum UpdateState { NotInstalled, UpToDate, Available, Failed }

public sealed record UpdateResult(UpdateState State, string? Version = null);

/// <summary>Procura versões novas nos Releases do GitHub e aplica pelo Velopack.</summary>
public static class Updater
{
    private static UpdateInfo? _pending;

    /// <summary>Versão nova já encontrada, se houver.</summary>
    public static string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    private static UpdateManager CreateManager() =>
        new(new GithubSource(AppInfo.RepoUrl, accessToken: null, prerelease: false));

    public static async Task<UpdateResult> CheckAsync()
    {
        try
        {
            var manager = CreateManager();
            // Rodando direto da pasta de compilação não há instalação para atualizar.
            if (!manager.IsInstalled) return new UpdateResult(UpdateState.NotInstalled);

            _pending = await manager.CheckForUpdatesAsync();
            return _pending is null
                ? new UpdateResult(UpdateState.UpToDate)
                : new UpdateResult(UpdateState.Available, PendingVersion);
        }
        catch
        {
            return new UpdateResult(UpdateState.Failed);
        }
    }

    public static async Task<bool> DownloadAndRestartAsync()
    {
        try
        {
            if (_pending is null) return false;
            var manager = CreateManager();
            if (!manager.IsInstalled) return false;
            await manager.DownloadUpdatesAsync(_pending);
            manager.ApplyUpdatesAndRestart(_pending);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
