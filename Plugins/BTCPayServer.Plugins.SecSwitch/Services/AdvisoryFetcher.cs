using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SecSwitch.Models;

// Grants BTCPayServer.Plugins.SecSwitch.Tests visibility into AdvisoryFetcher's internal
// timeout-testing seam (see the internal FetchAsync overload below) without adding a single line
// of test-only surface to the public API. A plain assembly-level attribute in a .cs file rather
// than an MSBuild <InternalsVisibleTo> csproj item, so no .csproj needs to be touched for this.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("BTCPayServer.Plugins.SecSwitch.Tests")]

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// One advisory downloaded from the feed, not yet parsed or verified. <see cref="ContentHash"/> is
/// carried through unchanged from the <see cref="AdvisoryIndexEntry"/> that produced this fetch. A
/// later stage persists this hash in the ledger and feeds the set of known hashes back into the
/// next <see cref="AdvisoryFetcher.FetchAsync(string,ISet{string},CancellationToken)"/> call so an
/// already-seen advisory is skipped without a network round trip; without carrying the hash
/// through here, that skip could never match anything and every advisory would be re-downloaded on
/// every single poll, forever. Note this is NOT re-derived from <see cref="PayloadBytes"/> here -
/// doing so would be a trust decision (asserting the bytes are what the index claims), which is
/// exactly what this class must never do; see the class doc comment below.
/// </summary>
/// <param name="SignaturesComplete">
/// Task 8 review round 3: true only if EVERY signature file this advisory's signatures/index.json
/// listed (up to <see cref="AdvisoryFetcher"/>'s own per-advisory cap) was itself successfully
/// fetched during this poll. False if: the signature index itself could not be fetched or parsed
/// at all; the request budget or the overall poll deadline was exhausted partway through gathering
/// signatures; the per-advisory signature cap was hit before every listed name was reached; a
/// listed name was blank or failed the path-containment check (listed, but never resolvable to
/// anything to fetch); or any individual signature file failed to fetch. Zero signatures because
/// the index could not even be read is <c>false</c> here, deliberately NOT <c>true</c> - "zero
/// because we could not look" is not the same claim as "zero because there are none".
///
/// This says NOTHING about whether <see cref="ArmoredSignatures"/> are cryptographically valid or
/// sufficient for quorum - only whether <see cref="AdvisoryFetcher"/> believes it retrieved every
/// signature the feed listed. Verification is <see cref="AdvisoryVerifier"/>'s job, entirely
/// separate from and downstream of this flag; this class performs none of it (see the class doc
/// comment). A caller deciding whether to persist this advisory's <see cref="ContentHash"/> as
/// "already seen" should treat <c>false</c> as "do not persist yet, so it is retried next poll" -
/// persisting it anyway risks permanently caching a truncated signature set: an advisory that can
/// never reach quorum and is never retried again, because its content hash already looks handled.
/// </param>
public sealed record FetchedAdvisory(
    string Id, string ContentHash, byte[] PayloadBytes,
    IReadOnlyList<string> ArmoredSignatures, bool SignaturesComplete);

/// <summary>
/// Downloads new advisories and their detached signatures from a GitHub-Pages-hosted feed. This is
/// the plugin's network attack surface: every byte <see cref="FetchAsync(string,ISet{string},CancellationToken)"/>
/// touches - the index, every advisory body, every signature file, every path and filename used to
/// compose a URL - is attacker-influenced, because "attacker" here includes anyone who can serve or
/// tamper with responses to the feed's URL (a compromised or spoofed mirror, a malicious CDN edge,
/// a man-in-the-middle on a misconfigured deployment), not just the feed's legitimate maintainers.
///
/// Two properties define this class and matter more than anything else about it:
///
/// 1. <b>This code does no verification and implies no trust.</b> It downloads bytes and hands
///    them back as <see cref="FetchedAdvisory"/>. Signature verification and quorum happen later,
///    in <see cref="AdvisoryVerifier"/>. Nothing here inspects a signature's validity, and a
///    successful fetch means only "downloaded", never "trusted".
/// 2. <b><see cref="FetchAsync(string,ISet{string},CancellationToken)"/> never throws</b>, for any
///    input (a null/malformed feed URL, a null known-hash set) and any network condition
///    (unreachable host, malformed JSON, a hostile/oversized response, a slow-loris connection
///    that never completes). A transport failure is a liveness gap - "nothing new seen this poll"
///    - never something a caller could mistake for a trust decision. Every fallible step fails
///    closed to "skip this one item, keep going" rather than propagating an exception; the outer
///    try/catch around the whole method body is a structural backstop for that, not the only line
///    of defence - every step above it is independently designed to fail closed too.
///
/// Every downloaded artefact (index, advisory body, signature list, each signature file) is
/// bounded in byte size, the number of SUCCESSFUL advisory downloads per poll is bounded (see
/// <see cref="MaxAdvisoriesPerPoll"/>), the number of signature files fetched per advisory is
/// bounded, and TOTAL requests issued per poll - successes and failures alike - are separately
/// bounded (see <see cref="MaxRequestsPerPoll"/>; Task 8 review, Finding 1: counting only
/// successes toward the per-poll cap is not itself enough - see that constant's own comment for
/// why a second, request-counting bound is required too). The whole poll is additionally bounded
/// by an overall wall-clock deadline (see <see cref="OverallPollDeadline"/>; Finding 3), on top of
/// the per-request timeout every individual HTTP call already has.
///
/// Every attacker-controlled path component - an index entry's <see cref="AdvisoryIndexEntry.Path"/>,
/// and a signature file name from a signatures/index.json - is resolved and validated to still sit
/// under its expected base URL before ever being requested (see <see cref="TryResolveUnderBase"/>),
/// so a hostile index or signature list cannot redirect a request outside the feed (or, for a
/// signature file name, outside its own advisory's signatures/ directory). That same containment
/// check is re-applied to the ACTUAL final URL a response came from after every request (see
/// <see cref="IsUnderBase"/>'s use in <see cref="GetBytesAsync"/>; Finding 2): validating only the
/// URL composed before the request would leave an HTTP redirect as a clean bypass, since the
/// <see cref="HttpClient"/> this class is handed is not one it controls and may well have
/// <c>AllowAutoRedirect</c> at its default of <c>true</c>. Callers SHOULD still set
/// <c>AllowAutoRedirect = false</c> on the handler backing the <see cref="HttpClient"/> passed to
/// the constructor as defence in depth, but this class does not rely on that.
///
/// <see cref="FetchAsync(string,ISet{string},CancellationToken)"/> only accepts an <c>https</c>
/// feed URL (Task 8 review, Finding 9): the redirect containment check above requires an EXACT
/// scheme match, and .NET's redirect handling only ever follows http -> https, never the reverse -
/// so an operator-supplied <c>http</c> feed URL whose host redirects to https (exactly what GitHub
/// Pages does) would otherwise fail silently and permanently, every poll, with no exception, log,
/// or signal. Refusing it outright up front converts that into an explicit, immediate empty
/// result. Not a trust requirement - the GPG quorum verified downstream is the real trust root, so
/// an http feed would still be safe, merely leaky and easy to interfere with - just a cost-free way
/// to remove the trap entirely.
/// </summary>
public sealed class AdvisoryFetcher(HttpClient http)
{
    /// <summary>
    /// A real index.json lists the feed's entire published advisory history, but even a large,
    /// long-running feed is at most a few thousand short entries. Generous headroom before parsing
    /// is even attempted, bounding a hostile or corrupted feed's worst case rather than reflecting
    /// a realistic size.
    /// </summary>
    const int MaxIndexBytes = 1024 * 1024; // 1 MB

    /// <summary>
    /// A real advisory.json (id, identifier, affected-version range, title, description, a handful
    /// of reference URLs) is a few KB. Matches the "generous headroom, not a realistic size"
    /// reasoning AdvisoryVerifier.MaxTrustedKeyBlobLength already applies to a different downloaded
    /// artefact.
    /// </summary>
    const int MaxAdvisoryBytes = 256 * 1024; // 256 KB

    /// <summary>
    /// signatures/index.json is just a JSON array of filenames for one advisory. Sized well above
    /// what MaxSignatureFilesPerAdvisory (64) reasonably-long filenames would ever occupy, while
    /// still bounding a hostile response.
    /// </summary>
    const int MaxSignatureIndexBytes = 16 * 1024; // 16 KB

    /// <summary>
    /// A real detached signature is a few hundred bytes. Deliberately equal to
    /// AdvisoryVerifier.MaxArmoredSignatureLength: a signature file is bound by the same generous
    /// headroom whether it is being downloaded here or handed to AdvisoryVerifier.Verify next.
    /// </summary>
    const int MaxSignatureBytes = 64 * 1024; // 64 KB

    /// <summary>
    /// Bounds SUCCESSFUL advisory downloads (<c>results.Count</c>) per FetchAsync call -
    /// deliberately NOT attempts, and NOT how many index entries are scanned.
    ///
    /// Task 8 review, Finding 1 (Critical): an earlier version of this file incremented a separate
    /// `attempts` counter against this same cap for every entry that merely had three non-blank
    /// fields and an unknown content hash - BEFORE the containment check and BEFORE any download,
    /// so an entry that then failed (containment-rejected, 404, oversized, timed out) burned a
    /// cap slot without ever becoming a FetchedAdvisory. Since only a SUCCESSFUL fetch's content
    /// hash ever reaches the caller's knownContentHashes set, a failing entry is exactly as
    /// "unknown" on the next poll as it was on this one - it fails again at the SAME index
    /// position, burning the same slot, forever. Fifty permanently-failing entries (a hostile
    /// index, or ordinary feed rot - stale directories that now 404) ahead of a genuine advisory
    /// in the index therefore starved it out completely: no exception, no log, no signal, just
    /// silent permanent non-discovery.
    ///
    /// Counting successes instead is self-correcting the way this cap was always meant to be: a
    /// backlog of entries that all eventually succeed is simply spread over more polls (scanning
    /// the already size-capped index to check `known.Contains(entry.ContentHash)` is a cheap
    /// in-memory HashSet lookup regardless of index length, so that scan is never itself capped),
    /// while an entry that keeps failing never occupies this budget at all. That alone reopens a
    /// different problem this cap also used to prevent - a hostile index of arbitrarily many
    /// failing entries could once again force unbounded network requests, since failures no longer
    /// cost anything against THIS cap - see <see cref="MaxRequestsPerPoll"/> for the bound that
    /// closes that gap instead.
    /// </summary>
    const int MaxAdvisoriesPerPoll = 50;

    /// <summary>
    /// Bounds TOTAL HTTP requests issued per FetchAsync call - the index, every advisory body,
    /// every signatures/index.json, and every signature file, successes and failures alike -
    /// regardless of how many of them succeed. Deliberately separate from, and much larger than,
    /// <see cref="MaxAdvisoriesPerPoll"/> (Task 8 review, Finding 1): since that cap now counts
    /// only successes, a wall of entries that each fail would otherwise cost nothing against it and
    /// so could be walked through with unboundedly many requests - reopening the exact
    /// unbounded-request DoS the original (attempt-counting) cap existed to prevent, just via
    /// failures instead of successes.
    ///
    /// 50 legitimate successes can themselves cost up to 50 x 66 = 3,300 requests (1 advisory.json
    /// + 1 signatures/index.json + up to 64 signature files each - see
    /// MaxSignatureFilesPerAdvisory). This is set comfortably above that so a fully legitimate poll
    /// is never cut off by it, with headroom left over (roughly 800 more requests) to walk past a
    /// realistic amount of failing or rotted entries within the same poll before deferring the rest
    /// to the next one. It does NOT guarantee an arbitrarily large hostile index can never starve a
    /// genuine advisory positioned after it - an index with enough failing entries to exceed this
    /// ceiling on every single poll (the index size cap, MaxIndexBytes, structurally allows up to
    /// roughly 26,000 minimal entries) could still do that. That residual is accepted rather than
    /// closed here: closing it fully needs a persisted per-poll cursor/offset into the index, which
    /// is a materially bigger design change than a review fix-round, and reaching it requires an
    /// attacker who already controls the bulk of the feed's own index content near its size cap -
    /// a much higher bar than the 50-entry, no-index-control-needed trigger Finding 1 itself had.
    /// </summary>
    const int MaxRequestsPerPoll = 4096;

    /// <summary>
    /// A realistic quorum is a handful of signers (see AdvisoryVerifier.MaxSignaturesPerBlock's
    /// own comment). Deliberately the same value: it bounds how many signature FILES one
    /// advisory's signatures/index.json may list before the rest are ignored, matching the same
    /// order-of-magnitude headroom AdvisoryVerifier already applies to signature PACKETS within a
    /// single file.
    /// </summary>
    const int MaxSignatureFilesPerAdvisory = 64;

    /// <summary>
    /// Default per-request timeout: generous for a static GitHub-Pages-hosted feed under normal
    /// conditions, but finite, so a connection that accepts bytes but trickles them - or never
    /// responds at all - cannot stall a single request indefinitely. Bounds one HTTP request; see
    /// <see cref="OverallPollDeadline"/> for the bound on the whole poll. Only the internal
    /// overload's <c>perRequestTimeout</c> parameter is visible to tests - see its doc comment for
    /// why.
    /// </summary>
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Task 8 review, Finding 3 (Important): per-request bounding alone does not bound the WHOLE
    /// poll. Worst case without this: 1 index + up to 50 advisories x (1 advisory.json + 1
    /// signatures/index.json + up to 64 signature files) = 3,301 requests, each allowed to take up
    /// to RequestTimeout (10s) - roughly 9.2 hours for one FetchAsync call against a feed that
    /// stalls every response until just before its timeout. That blacks out discovery of new
    /// advisories for hours per poll, indefinitely, which is unacceptable for a security kill
    /// switch. Five minutes is comfortably more than any legitimate poll should ever need (real
    /// network calls against a static feed complete in well under a second each) while still being
    /// far short of an hourly poll interval, so a poll that hits this deadline does not pile up
    /// against the next scheduled one. Exceeding it returns whatever was fetched so far, never
    /// throws - see the class doc comment. Only the internal overload's <c>overallDeadline</c>
    /// parameter is visible to tests.
    /// </summary>
    static readonly TimeSpan OverallPollDeadline = TimeSpan.FromMinutes(5);

    public Task<IReadOnlyList<FetchedAdvisory>> FetchAsync(
        string feedUrl, ISet<string> knownContentHashes, CancellationToken ct)
        => FetchAsync(feedUrl, knownContentHashes, RequestTimeout, OverallPollDeadline, ct);

    /// <summary>
    /// Internal so BTCPayServer.Plugins.SecSwitch.Tests (see the assembly-level
    /// InternalsVisibleTo above) can pass millisecond-scale <paramref name="perRequestTimeout"/>
    /// and <paramref name="overallDeadline"/> values to prove both timeouts are actually enforced,
    /// without waiting out the real 10-second / 5-minute production defaults and without adding
    /// timeout parameters to the public
    /// <see cref="FetchAsync(string,ISet{string},CancellationToken)"/> overload - whose signature
    /// is fixed by this class's contract with its caller and must not change.
    /// </summary>
    internal async Task<IReadOnlyList<FetchedAdvisory>> FetchAsync(
        string feedUrl, ISet<string> knownContentHashes, TimeSpan perRequestTimeout, TimeSpan overallDeadline,
        CancellationToken ct)
    {
        var results = new List<FetchedAdvisory>();
        try
        {
            if (string.IsNullOrWhiteSpace(feedUrl) || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var parsedFeedUri))
                return results; // No usable feed URL - a liveness gap, not an error.

            // Task 8 review, Finding 9: SecSwitchSettings.FeedUrl is operator-settable with no
            // validation anywhere else in the plugin. An http:// feed URL whose host issues the
            // standard site-wide redirect to https - exactly what GitHub Pages does - would
            // otherwise fail SILENTLY AND FOREVER: SocketsHttpHandler never follows https->http (so
            // that direction is moot), but it DOES follow http->https, and IsUnderBase's post-
            // response check (see GetBytesAsync) correctly requires an EXACT scheme match, so the
            // https response from that redirect would be discarded every single poll with no
            // exception, log, or signal. Refusing an http feed URL outright, here, converts that
            // silent-forever failure into an explicit, immediate "nothing fetched" - still fails
            // closed (see the class doc comment), never throws. Requiring https costs nothing (it
            // is what the real feed already uses) and removes the trap entirely rather than trying
            // to special-case it in the containment check itself - deliberately NOT fixed by
            // letting IsUnderBase permit an http->https upgrade, which would weaken that check's
            // guarantee for every caller for a case that, with this refusal, can no longer occur.
            // Transport security is defence in depth here, not load-bearing - the GPG quorum
            // (verified downstream, not by this class) is the actual trust root, so an http feed
            // would still be safe, merely leaky (poll timing) and trivially suppressible.
            if (!string.Equals(parsedFeedUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
                return results;

            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overallCts.CancelAfter(overallDeadline);
            var pollCt = overallCts.Token;

            var baseUri = WithTrailingSlash(parsedFeedUri);

            // Task 8 review, Minor 7: always rebuilt into a fresh OrdinalIgnoreCase-keyed set,
            // regardless of what the caller passed (including null), rather than trusting the
            // caller's own set to already compare that way. A content hash is a hex digest whose
            // casing carries no meaning, so a value differing only in case from what the caller
            // persisted must still be recognised as "already known" - LedgerStore hit exactly this
            // class of bug once already (a custom dictionary comparer does not survive a JSON round
            // trip), which is why this does not assume the caller got it right either.
            var known = knownContentHashes is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(knownContentHashes, StringComparer.OrdinalIgnoreCase);

            var budget = new RequestBudget(MaxRequestsPerPoll);

            var indexBytes = await GetBytesAsync(new Uri(baseUri, "index.json"), MaxIndexBytes, perRequestTimeout, baseUri, budget, pollCt);
            if (indexBytes is null)
                return results;

            var index = AdvisoryParser.ParseIndex(indexBytes);

            foreach (var entry in index)
            {
                if (pollCt.IsCancellationRequested || results.Count >= MaxAdvisoriesPerPoll || budget.Exhausted)
                    break;

                if (entry is null ||
                    string.IsNullOrWhiteSpace(entry.Id) ||
                    string.IsNullOrWhiteSpace(entry.Path) ||
                    string.IsNullOrWhiteSpace(entry.ContentHash) ||
                    known.Contains(entry.ContentHash))
                    continue;

                try
                {
                    if (!TryResolveUnderBase(baseUri, WithTrailingSlash(entry.Path), out var dirUri))
                        continue; // entry.Path attempted to escape the feed base - never fetched.

                    var payload = await GetBytesAsync(new Uri(dirUri, "advisory.json"), MaxAdvisoryBytes, perRequestTimeout, baseUri, budget, pollCt);
                    if (payload is null)
                        continue;

                    var (signatures, signaturesComplete) = await GetSignaturesAsync(dirUri, perRequestTimeout, baseUri, budget, pollCt);
                    results.Add(new FetchedAdvisory(entry.Id, entry.ContentHash, payload, signatures, signaturesComplete));
                }
                catch (Exception)
                {
                    // This one entry is unusable - never let it abort the rest of the poll.
                }
            }
        }
        catch (Exception)
        {
            // Backstop only: every path above is already designed to fail closed per item without
            // throwing. This exists so a future change that misses a case fails toward "nothing
            // further this poll" instead of throwing out of FetchAsync - see the class doc comment.
        }
        return results;
    }

    /// <summary>
    /// Returns the signatures successfully fetched for one advisory, AND whether that set is known
    /// to be complete - see <see cref="FetchedAdvisory.SignaturesComplete"/>'s doc comment for the
    /// full contract this return value must satisfy (Task 8 review round 3). <c>Complete</c> starts
    /// true and is set false the instant any of: the signature index fails to fetch or parse (the
    /// early returns below); the loop stops before reaching every listed name (cancellation, the
    /// per-advisory cap, or the request budget - Task 8 review, Finding 1's `budget.Exhausted` gate
    /// applies here too, so a poll-wide exhaustion truncates THIS advisory's signatures exactly the
    /// same way); a listed name is blank or fails containment; or an individual fetch fails. It is
    /// never set back to true once false - one bad name or one failed fetch is enough to make the
    /// whole set suspect, even if every other listed name succeeded.
    /// </summary>
    async Task<(IReadOnlyList<string> Signatures, bool Complete)> GetSignaturesAsync(
        Uri dirUri, TimeSpan perRequestTimeout, Uri baseUri, RequestBudget budget, CancellationToken ct)
    {
        var signatures = new List<string>();

        var listBytes = await GetBytesAsync(new Uri(dirUri, "signatures/index.json"), MaxSignatureIndexBytes, perRequestTimeout, baseUri, budget, ct);
        if (listBytes is null)
            return (signatures, false); // Could not even see what should be there - never "complete" by default.

        string[] names;
        try
        {
            names = JsonSerializer.Deserialize<string[]>(listBytes) ?? [];
        }
        catch (Exception)
        {
            // Broader than JsonException deliberately - "never throw" has no carve-out for an
            // unusual failure mode surfacing as a different exception type.
            return (signatures, false);
        }

        var sigDirUri = new Uri(dirUri, "signatures/");
        var processed = 0;
        var complete = true;
        foreach (var name in names)
        {
            if (ct.IsCancellationRequested || processed >= MaxSignatureFilesPerAdvisory || budget.Exhausted)
            {
                complete = false; // Stopped before reaching every listed name.
                break;
            }

            if (string.IsNullOrWhiteSpace(name) || !TryResolveUnderBase(sigDirUri, name, out var fileUri))
            {
                complete = false; // Listed, but never resolvable to anything to fetch.
                continue; // Blank, or attempted to escape this advisory's own signatures/ dir.
            }

            processed++;
            var bytes = await GetBytesAsync(fileUri, MaxSignatureBytes, perRequestTimeout, baseUri, budget, ct);
            if (bytes is not null)
                signatures.Add(Encoding.UTF8.GetString(bytes));
            else
                complete = false; // Listed, resolved, but the fetch itself failed.
        }
        return (signatures, complete);
    }

    /// <summary>
    /// Downloads <paramref name="url"/>, bounded to at most <paramref name="maxBytes"/>, to
    /// <paramref name="perRequestTimeout"/>, and by <paramref name="budget"/>'s remaining request
    /// count. Never throws: any failure (unreachable host, non-2xx status, a response that landed
    /// outside <paramref name="baseUri"/>, oversized body, timeout, cancellation, budget exhausted)
    /// returns null.
    ///
    /// The size bound is enforced twice: a Content-Length-based fast path rejects an honestly
    /// oversized response before reading any of its body, and a running-total check while streaming
    /// rejects a response that lies about, or omits, Content-Length but sends more than the cap
    /// anyway - the first is an optimisation, the second is the real guarantee.
    ///
    /// Task 8 review, Finding 2 (Important, SSRF): the URL composed by the caller (validated by
    /// <see cref="TryResolveUnderBase"/>) is not necessarily the URL a response actually came from.
    /// <see cref="http"/> is an <see cref="HttpClient"/> this class does not own or configure - if
    /// its handler follows redirects (the framework default), a hostile feed answering with a 3xx
    /// can make it transparently return a body served by a completely different host, with no
    /// containment check ever applied to that host. This re-validates the ACTUAL final URL
    /// (<c>response.RequestMessage.RequestUri</c>) against <paramref name="baseUri"/> before
    /// trusting anything about the response. <see cref="IsUnderBase"/> requires an EXACT scheme
    /// match (Task 8 review, Finding 9 - an earlier version of this comment incorrectly claimed a
    /// "scheme normalisation" redirect was accepted; it is not, deliberately - see
    /// <see cref="FetchAsync(string,ISet{string},TimeSpan,TimeSpan,CancellationToken)"/>'s https-only
    /// guard for why that never needs to matter in practice), so what is actually accepted is a
    /// same-scheme, same-host, same-port redirect that stays under the base - e.g. a path
    /// normalisation - while a cross-host OR cross-scheme one is discarded regardless of what the
    /// caller's <see cref="HttpClient"/> handler is configured to do.
    /// </summary>
    async Task<byte[]?> GetBytesAsync(Uri url, int maxBytes, TimeSpan perRequestTimeout, Uri baseUri, RequestBudget budget, CancellationToken ct)
    {
        if (!budget.TryConsume())
            return null; // Request ceiling reached this poll - fail closed, no request issued.

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(perRequestTimeout);

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            // See this method's doc comment (Finding 2) - never trust a response without knowing
            // where it actually came from, which may differ from `url` if a redirect was followed.
            var finalUri = response.RequestMessage?.RequestUri;
            if (finalUri is null || !IsUnderBase(baseUri, finalUri))
                return null;

            if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > maxBytes)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeoutCts.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    return null; // Over cap mid-stream - an absent or dishonest Content-Length must not bypass the bound.
            }
            return buffer.ToArray();
        }
        catch (Exception)
        {
            // A transport failure is a liveness gap, never a trust decision - callers see
            // "nothing new" (see the class doc comment).
            return null;
        }
    }

    static Uri WithTrailingSlash(Uri uri)
    {
        if (uri.AbsolutePath.EndsWith('/'))
            return uri;
        return new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;
    }

    static string WithTrailingSlash(string path) => path.EndsWith('/') ? path : path + "/";

    /// <summary>
    /// True only if <paramref name="candidate"/> sits under <paramref name="baseUri"/>'s own
    /// scheme, host, port, AND path prefix. Shared by <see cref="TryResolveUnderBase"/> (validating
    /// a URL composed from attacker-influenced feed content BEFORE it is requested) and
    /// <see cref="GetBytesAsync"/> (validating the URL a response actually came from AFTER a
    /// request, to catch a redirect - Finding 2) - the same allowlist check applies to both, since
    /// both are asking the identical question: "is this URL provably still inside the feed?".
    /// Scheme/Host/Port are compared individually rather than via Uri.Authority specifically so a
    /// "trustedhost@evil.example" userinfo prefix can never pass: Uri.Host never includes
    /// userinfo, so the comparison sees evil.example precisely as what it is - a different host -
    /// not as something starting with the trusted name.
    /// </summary>
    static bool IsUnderBase(Uri baseUri, Uri candidate)
    {
        if (!string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.Ordinal) ||
            !string.Equals(candidate.Host, baseUri.Host, StringComparison.Ordinal) ||
            candidate.Port != baseUri.Port)
            return false;

        // baseUri's path always ends in '/' (see WithTrailingSlash), so this is a genuine
        // directory-prefix check, not a naive string prefix a sibling path like "/feed-evil"
        // could pass against "/feed". Ordinal: a security check on URI structure, never
        // culture-sensitive.
        return candidate.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> against <paramref name="baseUri"/> and returns
    /// true only if the result satisfies <see cref="IsUnderBase"/>. Both an index entry's
    /// <see cref="AdvisoryIndexEntry.Path"/> and a signature file name are attacker-influenced feed
    /// content, so both are resolved through this exact same check before ever reaching
    /// <see cref="http"/> - rather than trying to blocklist individual traversal spellings ("../",
    /// "..\", a leading "/", an embedded scheme, a "//host" protocol-relative reference, or a
    /// "user@host" disguise), this only ever accepts a result PROVABLY still inside the base, which
    /// is immune to spellings nobody has thought of yet.
    /// </summary>
    static bool TryResolveUnderBase(Uri baseUri, string relativePath, out Uri resolved)
    {
        resolved = baseUri; // Never read on the false path - every call site checks the bool first.

        // "//..." is RFC 3986's network-path-reference syntax - it names a DIFFERENT authority
        // (host[:port]), not a path. Must be rejected outright, distinctly from a single leading
        // '/' (handled by TrimStart below): trimming it the same way would silently reinterpret
        // "//evil.example/x" as the harmless relative path "evil.example/x" under THIS base,
        // rather than the attempt to name a different host it actually is.
        if (relativePath.StartsWith("//", StringComparison.Ordinal))
            return false;

        // Task 8 review, Minor 5: defence in depth beyond what THIS code's own resolution needs -
        // Uri.TryCreate never decodes percent-encoding before the containment check below runs, so
        // a percent-encoded separator cannot bypass it HERE. But a different server sitting in
        // front of the real feed (a CDN, a misconfigured reverse proxy) that decodes the path
        // before serving could interpret one as an actual separator this check never validated.
        // Rejected outright rather than relied upon to be harmless everywhere this fetcher's
        // output might end up.
        if (relativePath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Contains("%5c", StringComparison.OrdinalIgnoreCase))
            return false;

        // Task 8 review, Minor 6: a query or fragment component would silently retarget which
        // resource the composed URL actually names (e.g. "a1?x=1" loses the "a1" path segment
        // entirely once "?x=1" is parsed as a query) even though the RESULT still passes the
        // containment check below - "contained" is not the same guarantee as "means what the
        // index/signature list said". Reject outright rather than let a path be silently
        // reinterpreted.
        if (relativePath.IndexOfAny(['?', '#']) >= 0)
            return false;

        // A single leading '/' would otherwise resolve as "absolute path from the authority root",
        // bypassing the base's own sub-path even though host/scheme still match afterwards.
        var trimmed = relativePath.TrimStart('/');
        if (trimmed.Length == 0 || !Uri.TryCreate(baseUri, trimmed, out var candidate))
            return false;

        if (!IsUnderBase(baseUri, candidate))
            return false;

        resolved = candidate;
        return true;
    }

    /// <summary>
    /// A simple, non-thread-safe request counter - safe because <see cref="FetchAsync(string,ISet{string},TimeSpan,TimeSpan,CancellationToken)"/>
    /// processes entries strictly sequentially (awaited one at a time; no parallel fan-out).
    /// Constructed fresh per <see cref="FetchAsync(string,ISet{string},TimeSpan,TimeSpan,CancellationToken)"/>
    /// call specifically so the budget cannot leak across two calls on the same
    /// <see cref="AdvisoryFetcher"/> instance (a plain instance field would have exactly that bug).
    /// </summary>
    sealed class RequestBudget(int max)
    {
        int _count;
        public bool Exhausted => _count >= max;

        public bool TryConsume()
        {
            if (_count >= max)
                return false;
            _count++;
            return true;
        }
    }
}
