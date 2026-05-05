using System;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace UniClaude.Editor.VersionTracker
{
    /// <summary>
    /// Orchestrates version checks: fetches, parses, caches to <see cref="UniClaudeSettings"/>,
    /// and returns a derived <see cref="CheckResult"/>. Stateless aside from the persisted settings.
    /// </summary>
    public class VersionCheckService
    {
        /// <summary>Cache TTL: re-check allowed after 24 hours.</summary>
        public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

        /// <summary>Sentinel error string from the fetcher meaning "no releases exist yet" (HTTP 404).</summary>
        public const string NoReleasesError = "No releases published yet";

        /// <summary>Maximum length we'll cache for release notes. Anything longer is truncated.</summary>
        const int MaxReleaseNotesLength = 64 * 1024;

        /// <summary>Maximum length for a tag name. Generous but bounded.</summary>
        const int MaxTagLength = 256;

        /// <summary>Maximum length for the release URL.</summary>
        const int MaxUrlLength = 1024;

        readonly IReleaseFetcher _fetcher;
        readonly string _currentVersion;

        /// <summary>Create a new service with the given fetcher and current package version.</summary>
        /// <param name="fetcher">HTTP abstraction.</param>
        /// <param name="currentVersion">Version string from package.json (e.g. "0.2.0").</param>
        public VersionCheckService(IReleaseFetcher fetcher, string currentVersion)
        {
            _fetcher = fetcher;
            _currentVersion = currentVersion;
        }

        /// <summary>
        /// Returns the cached result built from persisted settings. Status is <see cref="CheckStatus.Unknown"/>
        /// when no cache exists.
        /// </summary>
        /// <returns>Cached check result.</returns>
        public CheckResult GetCached()
        {
            var settings = UniClaudeSettings.Load();
            return BuildResultFromSettings(settings);
        }

        /// <summary>
        /// Check for updates. When <paramref name="force"/> is false and cache is fresh, returns cached result
        /// without hitting the network. Persists successful results to settings.
        /// </summary>
        /// <param name="force">If true, always hits the fetcher even when cache is fresh.</param>
        /// <returns>Resolved check result.</returns>
        public async Task<CheckResult> CheckAsync(bool force)
        {
            var settings = UniClaudeSettings.Load();
            if (!force && IsCacheFresh(settings, DateTime.UtcNow))
            {
                return BuildResultFromSettings(settings);
            }

            var fetch = await _fetcher.FetchLatestAsync();
            var now = DateTime.UtcNow;

            if (!fetch.Ok)
            {
                if (fetch.Error == NoReleasesError)
                {
                    return new CheckResult
                    {
                        Status = CheckStatus.UpToDate,
                        CurrentVersion = _currentVersion,
                        CheckedAtIsoUtc = now.ToString("o"),
                    };
                }
                return new CheckResult
                {
                    Status = CheckStatus.Failed,
                    CurrentVersion = _currentVersion,
                    ErrorMessage = fetch.Error,
                    CheckedAtIsoUtc = now.ToString("o"),
                };
            }

            string tag, body, url, publishedAt;
            try
            {
                var json = JObject.Parse(fetch.Body);
                tag = SanitizeShortField((string)json["tag_name"], MaxTagLength);
                body = SanitizeReleaseNotes((string)json["body"]);
                url = SanitizeUrl((string)json["html_url"]);
                publishedAt = SanitizeShortField((string)json["published_at"], MaxTagLength);
                if (string.IsNullOrEmpty(tag))
                    return new CheckResult
                    {
                        Status = CheckStatus.Failed,
                        CurrentVersion = _currentVersion,
                        ErrorMessage = "No tag in response",
                        CheckedAtIsoUtc = now.ToString("o"),
                    };
            }
            catch (Exception ex)
            {
                return new CheckResult
                {
                    Status = CheckStatus.Failed,
                    CurrentVersion = _currentVersion,
                    ErrorMessage = "Parse error: " + ex.Message,
                    CheckedAtIsoUtc = now.ToString("o"),
                };
            }

            settings.LastVersionCheckIsoUtc = now.ToString("o");
            settings.LastKnownLatestVersion = tag;
            settings.LastKnownReleaseNotes = body;
            settings.LastKnownReleaseUrl = url;
            settings.LastKnownReleasePublishedAt = publishedAt;
            UniClaudeSettings.Save(settings);

            return BuildResultFromSettings(settings);
        }

        /// <summary>
        /// Strips control characters (other than tab/newline/CR) from a short metadata field
        /// and enforces a length cap. Defends against terminal-injection style payloads in
        /// response fields the user might display verbatim.
        /// </summary>
        internal static string SanitizeShortField(string input, int maxLen)
        {
            if (string.IsNullOrEmpty(input)) return input;
            var sb = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                if (c == '\t' || c == '\n' || c == '\r' || c >= 0x20) sb.Append(c);
            }
            var s = sb.ToString();
            if (s.Length > maxLen) s = s.Substring(0, maxLen);
            return s;
        }

        /// <summary>
        /// Cleans release-notes markdown before persisting: strips control characters,
        /// rejects <c>javascript:</c> / <c>data:</c> / <c>vbscript:</c> URI schemes that
        /// could otherwise be rendered as clickable links, and truncates to a sane upper bound.
        /// </summary>
        internal static string SanitizeReleaseNotes(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // Strip control bytes (keep tab/newline/CR for legitimate markdown formatting).
            var sb = new StringBuilder(input.Length);
            foreach (var c in input)
            {
                if (c == '\t' || c == '\n' || c == '\r' || c >= 0x20) sb.Append(c);
            }
            var cleaned = sb.ToString();

            // Disable a few URI schemes that have no business in release notes. We replace
            // the scheme with a visible placeholder rather than removing characters so the
            // user can see the original content was suspect.
            cleaned = ReplaceSchemeCaseInsensitive(cleaned, "javascript:", "blocked-scheme:");
            cleaned = ReplaceSchemeCaseInsensitive(cleaned, "data:",       "blocked-scheme:");
            cleaned = ReplaceSchemeCaseInsensitive(cleaned, "vbscript:",   "blocked-scheme:");
            cleaned = ReplaceSchemeCaseInsensitive(cleaned, "file:",       "blocked-scheme:");

            if (cleaned.Length > MaxReleaseNotesLength)
                cleaned = cleaned.Substring(0, MaxReleaseNotesLength) +
                          "\n\n…(release notes truncated by UniClaude — view full notes on GitHub)";

            return cleaned;
        }

        /// <summary>
        /// Validates that the release URL is HTTPS and points at github.com or a subdomain.
        /// Returns the URL when valid; null otherwise so the UI hides the "Open release" link
        /// rather than directing the user to an unexpected destination.
        /// </summary>
        internal static string SanitizeUrl(string input)
        {
            if (string.IsNullOrEmpty(input)) return null;
            if (input.Length > MaxUrlLength) return null;
            if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps) return null;
            var host = uri.Host;
            if (!host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                !host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
                return null;
            return uri.ToString();
        }

        static string ReplaceSchemeCaseInsensitive(string input, string scheme, string replacement)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(scheme)) return input;
            int idx = 0;
            var sb = new StringBuilder(input.Length);
            while (idx < input.Length)
            {
                int found = input.IndexOf(scheme, idx, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    sb.Append(input, idx, input.Length - idx);
                    break;
                }
                sb.Append(input, idx, found - idx);
                sb.Append(replacement);
                idx = found + scheme.Length;
            }
            return sb.ToString();
        }

        /// <summary>True when the last check is within <see cref="CacheTtl"/> of <paramref name="now"/>.</summary>
        /// <param name="settings">Settings instance holding the last-check timestamp.</param>
        /// <param name="now">Current UTC time.</param>
        /// <returns>True if cache is fresh.</returns>
        public static bool IsCacheFresh(UniClaudeSettings settings, DateTime now)
        {
            if (string.IsNullOrEmpty(settings.LastVersionCheckIsoUtc)) return false;
            if (!DateTime.TryParse(settings.LastVersionCheckIsoUtc,
                    null, System.Globalization.DateTimeStyles.RoundtripKind, out var last))
                return false;
            return (now - last) < CacheTtl;
        }

        CheckResult BuildResultFromSettings(UniClaudeSettings s)
        {
            if (string.IsNullOrEmpty(s.LastKnownLatestVersion))
            {
                return new CheckResult
                {
                    Status = CheckStatus.Unknown,
                    CurrentVersion = _currentVersion,
                };
            }

            return new CheckResult
            {
                Status = SemverCompare.IsNewer(s.LastKnownLatestVersion, _currentVersion)
                    ? CheckStatus.UpdateAvailable
                    : CheckStatus.UpToDate,
                CurrentVersion = _currentVersion,
                LatestVersion = s.LastKnownLatestVersion,
                ReleaseNotesMarkdown = s.LastKnownReleaseNotes,
                ReleaseUrl = s.LastKnownReleaseUrl,
                PublishedAtIsoUtc = s.LastKnownReleasePublishedAt,
                CheckedAtIsoUtc = s.LastVersionCheckIsoUtc,
            };
        }
    }
}
