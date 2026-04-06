using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class LetterboxdSyncTask : IScheduledTask
{
    private readonly ILogger<LetterboxdSyncTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly LetterboxdSyncService _syncService;

    public LetterboxdSyncTask(
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        LetterboxdSyncService syncService)
    {
        _logger = loggerFactory.CreateLogger<LetterboxdSyncTask>();
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _syncService = syncService;
    }

    private static PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    public string Name => "Played media sync with letterboxd";
    public string Key => "LetterboxdSync";
    public string Description => "Sync movies with Letterboxd";
    public string Category => "LetterboxdSync";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        foreach (var user in _userManager.Users)
        {
            var account = Configuration.Accounts.FirstOrDefault(a => a.UserJellyfin == user.Id.ToString("N") && a.Enable);
            if (account == null)
                continue;

            var movies = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                IsVirtualItem = false,
                IsPlayed = true,
            });

            if (movies.Count == 0)
                continue;

            if (account.EnableDateFilter)
            {
                var cutoff = DateTime.UtcNow.AddDays(-account.DateFilterDays);
                movies = movies.Where(m =>
                {
                    var ud = _userDataManager.GetUserData(user, m);
                    return ud.LastPlayedDate.HasValue && ud.LastPlayedDate.Value >= cutoff;
                }).ToList();
            }

            using var api = await _syncService.AuthenticateAsync(account, user, retryOnCloudflare: true, cancellationToken).ConfigureAwait(false);
            if (api == null)
                continue;

            foreach (var movie in movies)
            {
                try
                {
                    var ud = _userDataManager.GetUserData(user, movie);
                    await _syncService.SyncFilmAsync(api, account, user, movie, ud.LastPlayedDate).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "{Message}\n                            User: {Username} ({UserId})\n                            Movie: {Movie}\n                            StackTrace: {StackTrace}",
                        ex.Message, user.Username, user.Id.ToString("N"), movie.OriginalTitle, ex.StackTrace);
                }
            }
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(1).Ticks
        }
    };
}
