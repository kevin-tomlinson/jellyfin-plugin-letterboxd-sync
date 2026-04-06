using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class PlaybackHandler : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly LetterboxdSyncService _syncService;
    private readonly ILogger<PlaybackHandler> _logger;

    public PlaybackHandler(
        ISessionManager sessionManager,
        LetterboxdSyncService syncService,
        ILoggerFactory loggerFactory)
    {
        _sessionManager = sessionManager;
        _syncService = syncService;
        _logger = loggerFactory.CreateLogger<PlaybackHandler>();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private async void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (!e.PlayedToCompletion)
            return;

        if (e.Item?.GetBaseItemKind() != BaseItemKind.Movie)
            return;

        foreach (var user in e.Users)
        {
            var account = Plugin.Instance!.Configuration.Accounts
                .FirstOrDefault(a => a.UserJellyfin == user.Id.ToString("N") && a.Enable);

            if (account == null)
                continue;

            _logger.LogInformation(
                "Playback completed — syncing {Movie} to Letterboxd for {Username}",
                e.Item.Name, user.Username);

            using var api = await _syncService.AuthenticateAsync(account, user, retryOnCloudflare: false, CancellationToken.None).ConfigureAwait(false);
            if (api == null)
                continue;

            try
            {
                await _syncService.SyncFilmAsync(api, account, user, e.Item, DateTime.UtcNow).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to sync {Movie} to Letterboxd for {Username}",
                    e.Item.Name, user.Username);
            }
        }
    }
}
