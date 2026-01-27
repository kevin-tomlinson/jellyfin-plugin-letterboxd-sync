using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace LetterboxdSync;

public class LetterboxdApi
{
    private string csrf = string.Empty;
    private string username = string.Empty;

    public string Csrf => csrf;

    // Reused for the lifetime of this LetterboxdApi instance (one sync run)
    private readonly CookieContainer cookieContainer = new CookieContainer();
    private readonly HttpClientHandler handler;
    private readonly HttpClient client;

    private async Task RefreshCsrfFromFilmPageAsync(string filmSlug)
    {
        // Film page typically contains the CSRF token expected by actions related to that film.
        var html = await client.GetStringAsync($"/film/{filmSlug}/").ConfigureAwait(false);

        var fresh = ExtractHiddenInput(html, "__csrf");
        if (string.IsNullOrWhiteSpace(fresh))
            throw new Exception($"Could not extract __csrf from film page /film/{filmSlug}/");

        this.csrf = fresh!;
    }


    private string GetCsrfFromCookie()
    {
        var cookies = cookieContainer.GetCookies(new Uri("https://letterboxd.com/"));
        // This is the token Letterboxd expects in the "__csrf" form field
        return cookies["com.xk72.webparts.csrf"]?.Value ?? string.Empty;
    }

    private async Task RefreshCsrfCookieAsync()
    {
        // Touch a page to ensure the CSRF cookie exists / is fresh
        using var _ = await client.GetAsync("/").ConfigureAwait(false);

        var token = GetCsrfFromCookie();
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("Could not read CSRF cookie 'com.xk72.webparts.csrf' after refreshing.");
        this.csrf = token;
    }






    public LetterboxdApi()
    {
        handler = new HttpClientHandler
        {
            CookieContainer = cookieContainer,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true
        };

        client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://letterboxd.com")
        };

        // Reasonable "browser-like" defaults. Keep it minimal.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36"
        );
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.9");
    }

    public async Task Authenticate(string username, string password)
    {
        this.username = username;

        // 1) GET /sign-in/ to obtain cookies + __csrf
        using (var signInResponse = await client.GetAsync("/sign-in/").ConfigureAwait(false))
        {
            if (signInResponse.StatusCode != HttpStatusCode.OK)
                throw new Exception($"Letterboxd returned {(int)signInResponse.StatusCode} on /sign-in/");

            var signInHtml = await signInResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            var csrfFromSignIn = ExtractHiddenInput(signInHtml, "__csrf");
            if (string.IsNullOrWhiteSpace(csrfFromSignIn))
                throw new Exception("Could not find __csrf token on /sign-in/ (login flow likely changed).");

            this.csrf = csrfFromSignIn;
        }

        // 2) POST /user/login.do with credentials + __csrf
        using (var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/user/login.do"))
        {
            loginRequest.Headers.Referrer = new Uri("https://letterboxd.com/sign-in/");
            loginRequest.Headers.TryAddWithoutValidation("Origin", "https://letterboxd.com");

            loginRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "username", username },
                { "password", password },
                { "__csrf", this.csrf },
                { "authenticationCode", "" }
            });

            using (var loginResponse = await client.SendAsync(loginRequest).ConfigureAwait(false))
            {
                if (loginResponse.StatusCode == HttpStatusCode.Forbidden)
                {
                    var body = await loginResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new Exception(
                        "Letterboxd returned 403 during login. This is likely reCAPTCHA/anti-bot enforcement. " +
                        "First 300 chars: " + body.Substring(0, Math.Min(body.Length, 300))
                    );
                }

                if (!loginResponse.IsSuccessStatusCode)
                    throw new Exception($"Letterboxd returned {(int)loginResponse.StatusCode} during login.");
            }
        }

        // 3) Refresh CSRF cookie after login
        await RefreshCsrfCookieAsync().ConfigureAwait(false);

        // 4) Refresh CSRF after login (best-effort)
        // Some sites rotate CSRF tokens; we try to extract it from the homepage.
        try
        {
            var homeHtml = await client.GetStringAsync("/").ConfigureAwait(false);
            var csrfAfter = ExtractHiddenInput(homeHtml, "__csrf");
            if (!string.IsNullOrWhiteSpace(csrfAfter))
                this.csrf = csrfAfter;
        }
        catch
        {
            // Keep the sign-in csrf if this fails.
        }
    }

    public async Task<FilmResult> SearchFilmByTmdbId(int tmdbid)
    {
        // Reuse the authenticated client + cookies from Authenticate.
        var tmdbPath = $"/tmdb/{tmdbid}";

        using (var res = await client.GetAsync(tmdbPath).ConfigureAwait(false))
        {
            if (res.StatusCode == HttpStatusCode.Forbidden)
                throw new Exception($"TMDB lookup returned 403 for https://letterboxd.com/tmdb/{tmdbid}");

            if (!res.IsSuccessStatusCode)
                throw new Exception($"TMDB lookup returned {(int)res.StatusCode} for https://letterboxd.com/tmdb/{tmdbid}");

            // IMPORTANT:
            // Letterboxd may not 302 redirect here anymore; the final RequestUri can remain /tmdb/<id>.
            // So we parse the returned HTML to find a film link / canonical URL.
            var html = await res.Content.ReadAsStringAsync().ConfigureAwait(false);

            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Best case: canonical link points at the film page.
            string filmUrl = htmlDoc.DocumentNode
                .SelectSingleNode("//link[@rel='canonical']")
                ?.GetAttributeValue("href", string.Empty) ?? string.Empty;

            // Fallback: any anchor to /film/<slug>/
            if (string.IsNullOrWhiteSpace(filmUrl))
            {
                var a = htmlDoc.DocumentNode.SelectSingleNode("//a[starts-with(@href, '/film/')]");
                var href = a?.GetAttributeValue("href", string.Empty) ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(href))
                    filmUrl = href.StartsWith("/") ? "https://letterboxd.com" + href : href;
            }

            if (string.IsNullOrWhiteSpace(filmUrl))
            {
                // Helpful debug: show what URL we actually fetched/ended at
                var finalUri = res?.RequestMessage?.RequestUri?.ToString() ?? string.Empty;
                throw new Exception($"The search returned no results (Could not resolve film URL from TMDB page). FinalUrl='{finalUri}'");
            }

            // Extract slug from film URL
            var filmUri = new Uri(filmUrl, UriKind.Absolute);
            var segments = filmUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length < 2 || !segments[0].Equals("film", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"TMDB page resolved to non-film URL: '{filmUrl}'");

            string filmSlug = segments[1];

            // Load film page and extract filmId
            using (var filmRes = await client.GetAsync($"/film/{filmSlug}/").ConfigureAwait(false))
            {
                if (!filmRes.IsSuccessStatusCode)
                    throw new Exception($"Film page lookup returned {(int)filmRes.StatusCode} for https://letterboxd.com/film/{filmSlug}/");

                var filmHtml = await filmRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                var filmDoc = new HtmlDocument();
                filmDoc.LoadHtml(filmHtml);

                HtmlNode? elForId =
                    filmDoc.DocumentNode.SelectSingleNode($"//div[@data-film-slug='{filmSlug}']") ??
                    filmDoc.DocumentNode.SelectSingleNode($"//div[@data-item-link='/film/{filmSlug}/']") ??
                    filmDoc.DocumentNode.SelectSingleNode("//div[@data-film-id]");

                if (elForId == null)
                    throw new Exception("The search returned no results (No html element found to get letterboxd filmId)");

                string filmId = elForId.GetAttributeValue("data-film-id", string.Empty);
                if (string.IsNullOrEmpty(filmId))
                    throw new Exception("The search returned no results (data-film-id attribute is empty)");

                return new FilmResult(filmSlug, filmId);
            }
        }
    }


    public async Task MarkAsWatched(string filmSlug, string filmId, DateTime? date, string[] tags, bool liked = false)
    {
        string url = "/s/save-diary-entry";
        DateTime viewingDate = date == null ? DateTime.Now : (DateTime)date;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            // IMPORTANT: Refresh CSRF from the film page (scoped token).
            await RefreshCsrfCookieAsync().ConfigureAwait(false);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                // Make the request look like the browser flow for this action
                request.Headers.Referrer = new Uri($"https://letterboxd.com/film/{filmSlug}/");
                request.Headers.TryAddWithoutValidation("Origin", "https://letterboxd.com");
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "__csrf", this.csrf },
                    { "json", "true" },
                    { "viewingId", string.Empty },
                    { "filmId", filmId },
                    { "specifiedDate", date == null ? "false" : "true" },
                    { "viewingDateStr", viewingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                    { "review", string.Empty },
                    { "tags", date != null && tags.Length > 0 ? $"[{string.Join(",", tags)}]" : string.Empty },
                    { "rating", "0" },
                    { "liked", liked.ToString().ToLowerInvariant() } // some endpoints are picky about casing
                });

                using (var response = await client.SendAsync(request).ConfigureAwait(false))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        throw new Exception($"Letterboxd returned {(int)response.StatusCode}");

                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    using (JsonDocument doc = JsonDocument.Parse(body))
                    {
                        var json = doc.RootElement;

                        // If response includes rotated CSRF, keep it
                        if (json.TryGetProperty("csrf", out var csrfEl) && csrfEl.ValueKind == JsonValueKind.String)
                        {
                            var newCsrf = csrfEl.GetString();
                            if (!string.IsNullOrWhiteSpace(newCsrf))
                                this.csrf = newCsrf!;
                        }

                        if (SuccessOperation(json, out string message))
                            return;

                        // Retry once on "expired"
                        if (attempt == 0 &&
                            message.Contains("expired", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        throw new Exception(message);
                    }
                }
            }
        }

        throw new Exception("Failed to submit diary entry after retry.");
    }




    public async Task<DateTime?> GetDateLastLog(string filmSlug)
    {
        // Uses same authenticated cookie container via client.
        string url = $"/{this.username}/film/{filmSlug}/diary/";

        var responseHtml = await client.GetStringAsync(url).ConfigureAwait(false);

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(responseHtml);

        var monthElements = htmlDoc.DocumentNode.SelectNodes("//a[contains(@class, 'month')]");
        var dayElements = htmlDoc.DocumentNode.SelectNodes("//a[contains(@class, 'date') or contains(@class, 'daydate')]");
        var yearElements = htmlDoc.DocumentNode.SelectNodes("//a[contains(@class, 'year')]");

        var lstDates = new List<DateTime>();

        if (monthElements != null && dayElements != null && yearElements != null)
        {
            var minCount = Math.Min(Math.Min(monthElements.Count, dayElements.Count), yearElements.Count);

            for (int i = 0; i < minCount; i++)
            {
                var month = monthElements[i].InnerText?.Trim();
                var day = dayElements[i].InnerText?.Trim();
                var year = yearElements[i].InnerText?.Trim();

                if (!string.IsNullOrEmpty(month) && !string.IsNullOrEmpty(day) && !string.IsNullOrEmpty(year))
                {
                    var dateString = $"{day} {month} {year}";
                    if (DateTime.TryParse(dateString, out DateTime parsedDate))
                        lstDates.Add(parsedDate);
                }
            }
        }

        return lstDates.Count > 0 ? lstDates.Max() : null;
    }

    private bool SuccessOperation(JsonElement json, out string message)
    {
        message = string.Empty;

        if (json.TryGetProperty("messages", out JsonElement messagesElement))
        {
            StringBuilder errorMessages = new StringBuilder();
            foreach (var i in messagesElement.EnumerateArray())
                errorMessages.Append(i.GetString());
            message = errorMessages.ToString();
        }

        if (json.TryGetProperty("result", out JsonElement statusElement))
        {
            switch (statusElement.ValueKind)
            {
                case JsonValueKind.String:
                    return statusElement.GetString() == "error" ? false : true;
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
            }
        }

        return false;
    }

    private static string? ExtractHiddenInput(string html, string name)
    {
        // Matches: <input type="hidden" name="__csrf" value="...">
        var pattern = $@"<input[^>]*\bname\s*=\s*[""']{Regex.Escape(name)}[""'][^>]*\bvalue\s*=\s*[""']([^""']*)[""'][^>]*>";
        var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }
}

public class FilmResult
{
    public string filmSlug = string.Empty;
    public string filmId = string.Empty;

    public FilmResult(string filmSlug, string filmId)
    {
        this.filmSlug = filmSlug;
        this.filmId = filmId;
    }
}
