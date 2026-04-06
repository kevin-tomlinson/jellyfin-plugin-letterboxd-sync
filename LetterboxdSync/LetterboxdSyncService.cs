using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using LetterboxdSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

public class LetterboxdSyncService
{
    private readonly IUserDataManager _userDataManager;
    private readonly ILogger<LetterboxdSyncService> _logger;

    public LetterboxdSyncService(IUserDataManager userDataManager, ILoggerFactory loggerFactory)
    {
        _userDataManager = userDataManager;
        _logger = loggerFactory.CreateLogger<LetterboxdSyncService>();
    }

    /// <summary>
    /// Authenticates with Letterboxd and returns a ready-to-use API instance.
    /// The caller is responsible for disposing it. Returns null if authentication fails.
    /// </summary>
    public async Task<LetterboxdApi?> AuthenticateAsync(Account account, User user, bool retryOnCloudflare, CancellationToken cancellationToken)
    {
        int maxAttempts = retryOnCloudflare ? 2 : 1;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var delayMs = 60_000 + Random.Shared.Next(0, 30_001);
                _logger.LogInformation(
                    "Letterboxd login hit Cloudflare for {Username}. Retrying in {Seconds}s.",
                    user.Username, delayMs / 1000);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            var api = new LetterboxdApi();
            try
            {
                await api.Authenticate(account.UserLetterboxd!, account.PasswordLetterboxd!).ConfigureAwait(false);
                return api;
            }
            catch (Exception ex)
            {
                api.Dispose();
                bool isCloudflare = ex.Message.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
                if (isCloudflare && attempt == 0 && retryOnCloudflare)
                    continue;
                _logger.LogError(
                    "Letterboxd authentication failed for {Username} ({UserId}): {Message}",
                    user.Username, user.Id.ToString("N"), ex.Message);
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a movie on Letterboxd and logs it as watched.
    /// Returns true if logged, false if skipped (no TMDB ID, or already logged on the same date).
    /// Throws on Letterboxd API errors.
    /// </summary>
    public async Task<bool> SyncFilmAsync(LetterboxdApi api, Account account, User user, BaseItem movie, DateTime? viewingDate)
    {
        string title = movie.OriginalTitle ?? movie.Name;

        if (!int.TryParse(movie.GetProviderId(MetadataProvider.Tmdb), out int tmdbId))
        {
            _logger.LogWarning(
                "Film has no TmdbId — User: {Username} ({UserId}), Movie: {Movie}",
                user.Username, user.Id.ToString("N"), title);
            return false;
        }

        var userItemData = _userDataManager.GetUserData(user, movie);
        bool favorite = movie.IsFavoriteOrLiked(user, userItemData) && account.SendFavorite;

        var filmResult = await api.SearchFilmByTmdbId(tmdbId).ConfigureAwait(false);

        var dateLastLog = await api.GetDateLastLog(filmResult.filmSlug).ConfigureAwait(false);
        var date = viewingDate.HasValue
            ? new DateTime(viewingDate.Value.Year, viewingDate.Value.Month, viewingDate.Value.Day)
            : DateTime.Today;

        if (dateLastLog?.Date == date)
        {
            _logger.LogWarning(
                "Film already logged on {Date} — User: {Username} ({UserId}), Movie: {Movie} ({TmdbId})",
                dateLastLog.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                user.Username, user.Id.ToString("N"), title, tmdbId);
            return false;
        }

        await api.MarkAsWatched(filmResult.filmSlug, filmResult.filmId, date, new[] { string.Empty }, favorite).ConfigureAwait(false);

        _logger.LogInformation(
            "Film logged in Letterboxd — User: {Username} ({UserId}), Movie: {Movie} ({TmdbId}), Date: {Date}",
            user.Username, user.Id.ToString("N"), title, tmdbId, date);

        return true;
    }
}
