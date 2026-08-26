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
public sealed record FetchedAdvisory(string Id, string ContentHash, byte[] PayloadBytes, IReadOnlyList<string> ArmoredSignatures);

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
/// bounded in byte size, and both the number of advisories fetched per poll and the number of
/// signature files fetched per advisory are bounded in count - see the constants below, each
/// documented with its own reasoning. Every attacker-controlled path component - an index entry's
/// <see cref="AdvisoryIndexEntry.Path"/>, and a signature file name from a signatures/index.json -
/// is resolved and validated to still sit under its expected base URL before ever being requested
/// (see <see cref="TryResolveUnderBase"/>), so a hostile index or signature list cannot redirect a
/// request outside the feed (or, for a signature file name, outside its own advisory's
/// signatures/ directory).
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
    /// Bounds new-advisory DOWNLOAD ATTEMPTS per FetchAsync call - deliberately NOT how many index
    /// entries are scanned. Scanning the (already size-capped) index to check whether an entry's
    /// content hash is already known is a cheap in-memory HashSet lookup regardless of index
    /// length, so that scan is never capped; only the expensive part - actually fetching a
    /// not-yet-known advisory over the network - is. This also keeps the cap self-correcting
    /// rather than a starvation trap: capping which INDEX POSITIONS are ever looked at would let
    /// an advisory appended past the cap go unseen forever, since every poll re-scans from
    /// position 0. Capping ATTEMPTS instead means a backlog larger than the cap is simply spread
    /// over more polls - an entry skipped this round only because the cap was already spent is
    /// still "unknown" afterwards, so it is picked up automatically on the next poll.
    /// </summary>
    const int MaxAdvisoriesPerPoll = 50;

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
    /// responds at all - cannot stall a poll indefinitely. Applied per individual HTTP request
    /// (the index, one advisory.json, one signatures/index.json, or one signature file), not as a
    /// single budget for the whole FetchAsync call, since one poll legitimately makes many
    /// requests. Only the internal overload's <c>perRequestTimeout</c> parameter is visible to
    /// tests - see its doc comment for why.
    /// </summary>
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public Task<IReadOnlyList<FetchedAdvisory>> FetchAsync(
        string feedUrl, ISet<string> knownContentHashes, CancellationToken ct)
        => FetchAsync(feedUrl, knownContentHashes, RequestTimeout, ct);

    /// <summary>
    /// Internal so BTCPayServer.Plugins.SecSwitch.Tests (see the assembly-level
    /// InternalsVisibleTo above) can pass a millisecond-scale <paramref name="perRequestTimeout"/>
    /// to prove a hanging request is actually bounded, without waiting out the real 10-second
    /// production default and without adding a timeout parameter to the public
    /// <see cref="FetchAsync(string,ISet{string},CancellationToken)"/> overload - whose signature
    /// is fixed by this class's contract with its caller and must not change.
    /// </summary>
    internal async Task<IReadOnlyList<FetchedAdvisory>> FetchAsync(
        string feedUrl, ISet<string> knownContentHashes, TimeSpan perRequestTimeout, CancellationToken ct)
    {
        var results = new List<FetchedAdvisory>();
        try
        {
            if (string.IsNullOrWhiteSpace(feedUrl) || !Uri.TryCreate(feedUrl, UriKind.Absolute, out var parsedFeedUri))
                return results; // No usable feed URL - a liveness gap, not an error.

            var baseUri = WithTrailingSlash(parsedFeedUri);
            var known = knownContentHashes ?? new HashSet<string>();

            var indexBytes = await GetBytesAsync(new Uri(baseUri, "index.json"), MaxIndexBytes, perRequestTimeout, ct);
            if (indexBytes is null)
                return results;

            var index = AdvisoryParser.ParseIndex(indexBytes);

            var attempts = 0;
            foreach (var entry in index)
            {
                if (ct.IsCancellationRequested || attempts >= MaxAdvisoriesPerPoll)
                    break;

                if (entry is null ||
                    string.IsNullOrWhiteSpace(entry.Id) ||
                    string.IsNullOrWhiteSpace(entry.Path) ||
                    string.IsNullOrWhiteSpace(entry.ContentHash) ||
                    known.Contains(entry.ContentHash))
                    continue;

                attempts++;
                try
                {
                    if (!TryResolveUnderBase(baseUri, WithTrailingSlash(entry.Path), out var dirUri))
                        continue; // entry.Path attempted to escape the feed base - never fetched.

                    var payload = await GetBytesAsync(new Uri(dirUri, "advisory.json"), MaxAdvisoryBytes, perRequestTimeout, ct);
                    if (payload is null)
                        continue;

                    var signatures = await GetSignaturesAsync(dirUri, perRequestTimeout, ct);
                    results.Add(new FetchedAdvisory(entry.Id, entry.ContentHash, payload, signatures));
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

    async Task<IReadOnlyList<string>> GetSignaturesAsync(Uri dirUri, TimeSpan perRequestTimeout, CancellationToken ct)
    {
        var signatures = new List<string>();

        var listBytes = await GetBytesAsync(new Uri(dirUri, "signatures/index.json"), MaxSignatureIndexBytes, perRequestTimeout, ct);
        if (listBytes is null)
            return signatures;

        string[] names;
        try
        {
            names = JsonSerializer.Deserialize<string[]>(listBytes) ?? [];
        }
        catch (Exception)
        {
            // Broader than JsonException deliberately - "never throw" has no carve-out for an
            // unusual failure mode surfacing as a different exception type.
            return signatures;
        }

        var sigDirUri = new Uri(dirUri, "signatures/");
        var processed = 0;
        foreach (var name in names)
        {
            if (ct.IsCancellationRequested || processed >= MaxSignatureFilesPerAdvisory)
                break;

            if (string.IsNullOrWhiteSpace(name) || !TryResolveUnderBase(sigDirUri, name, out var fileUri))
                continue; // Blank, or attempted to escape this advisory's own signatures/ dir.

            processed++;
            var bytes = await GetBytesAsync(fileUri, MaxSignatureBytes, perRequestTimeout, ct);
            if (bytes is not null)
                signatures.Add(Encoding.UTF8.GetString(bytes));
        }
        return signatures;
    }

    /// <summary>
    /// Downloads <paramref name="url"/>, bounded to at most <paramref name="maxBytes"/> and to
    /// <paramref name="perRequestTimeout"/>. Never throws: any failure (unreachable host, non-2xx
    /// status, oversized body, timeout, cancellation) returns null. The size bound is enforced
    /// twice: a Content-Length-based fast path rejects an honestly-oversized response before
    /// reading any of its body, and a running-total check while streaming rejects a response that
    /// lies about, or omits, Content-Length but sends more than the cap anyway - the first is an
    /// optimisation, the second is the real guarantee.
    /// </summary>
    async Task<byte[]?> GetBytesAsync(Uri url, int maxBytes, TimeSpan perRequestTimeout, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(perRequestTimeout);

            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
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
    /// Resolves <paramref name="relativePath"/> against <paramref name="baseUri"/> and returns
    /// true only if the result still sits under <paramref name="baseUri"/>'s own scheme, host,
    /// port, AND path prefix. Both an index entry's <see cref="AdvisoryIndexEntry.Path"/> and a
    /// signature file name are attacker-influenced feed content, so both are resolved through this
    /// exact same allowlist check before ever reaching <see cref="http"/> - rather than trying to
    /// blocklist individual traversal spellings ("../", "..\", a leading "/", an embedded scheme, a
    /// "//host" protocol-relative reference, or a "user@host" disguise where a naive string-prefix
    /// check on the trusted host would be fooled into matching), this only ever accepts a result
    /// PROVABLY still inside the base, which is immune to spellings nobody has thought of yet.
    /// Scheme/Host/Port are compared individually rather than via Uri.Authority specifically so a
    /// "trustedhost@evil.example" userinfo prefix can never pass: Uri.Host never includes
    /// userinfo, so the comparison sees evil.example precisely as what it is - a different host -
    /// not as something starting with the trusted name.
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

        // A single leading '/' would otherwise resolve as "absolute path from the authority root",
        // bypassing the base's own sub-path even though host/scheme still match afterwards.
        var trimmed = relativePath.TrimStart('/');
        if (trimmed.Length == 0 || !Uri.TryCreate(baseUri, trimmed, out var candidate))
            return false;

        if (!string.Equals(candidate.Scheme, baseUri.Scheme, StringComparison.Ordinal) ||
            !string.Equals(candidate.Host, baseUri.Host, StringComparison.Ordinal) ||
            candidate.Port != baseUri.Port)
            return false;

        // baseUri's path always ends in '/' (see WithTrailingSlash), so this is a genuine
        // directory-prefix check, not a naive string prefix a sibling path like "/feed-evil"
        // could pass against "/feed". Ordinal: a security check on URI structure, never
        // culture-sensitive.
        if (!candidate.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
            return false;

        resolved = candidate;
        return true;
    }
}
