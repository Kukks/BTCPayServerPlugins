using System;
using System.Linq;
using System.Text.Json;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class AdvisoryParser
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(byte[] json, out Advisory? advisory, out string? error)
    {
        advisory = null;
        error = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            error = $"Malformed advisory JSON: {e.Message}";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Advisory root must be a JSON object.";
                return false;
            }

            var id = GetString(root, "id");
            var identifier = GetString(root, "identifier");
            var affected = GetString(root, "affectedVersions");
            var severityRaw = GetString(root, "severity");

            if (string.IsNullOrWhiteSpace(id)) { error = "Missing required field: id"; return false; }
            if (string.IsNullOrWhiteSpace(identifier)) { error = "Missing required field: identifier"; return false; }
            if (string.IsNullOrWhiteSpace(affected)) { error = "Missing required field: affectedVersions"; return false; }
            if (!TryParseSeverity(severityRaw, out var severity))
            {
                error = $"Unrecognised severity '{severityRaw}'.";
                return false;
            }

            advisory = new Advisory
            {
                Id = id!,
                Identifier = identifier!,
                AffectedVersions = affected!,
                FixedVersion = GetString(root, "fixedVersion"),
                Severity = severity,
                Title = GetString(root, "title") ?? "",
                Description = GetString(root, "description") ?? "",
                References = root.TryGetProperty("references", out var refs) && refs.ValueKind == JsonValueKind.Array
                    ? refs.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : [],
                PublishedAt = root.TryGetProperty("publishedAt", out var pub) && pub.ValueKind == JsonValueKind.String
                              && DateTimeOffset.TryParse(pub.GetString(), out var parsed)
                    ? parsed
                    : default,
                Supersedes = GetString(root, "supersedes"),
                Revoked = root.TryGetProperty("revoked", out var rev) && rev.ValueKind == JsonValueKind.True
            };
            return true;
        }
    }

    public static AdvisoryIndexEntry[] ParseIndex(byte[] json)
    {
        try
        {
            return JsonSerializer.Deserialize<AdvisoryIndexEntry[]>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            // Fail closed: malformed, empty, or wrongly-shaped input means "no advisories
            // seen", not a thrown exception a future caller might forget to catch.
            return [];
        }
    }

    static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // The advisory schema is string-names-only ("high", not "2"). Enum.TryParse would otherwise
    // accept the enum's underlying numeric value too - optionally sign-prefixed and whitespace-
    // padded per Enum.Parse's documented acceptance rules ("One or more blank spaces can precede
    // or follow each value, name, or comma") - which TryParse shares. Rather than keep chasing
    // that permissive surface one hole at a time, match against an explicit allowlist instead:
    // it is categorically immune to numerics, sign prefixes, whitespace padding, and any future
    // Enum.TryParse leniency. Trade-off: adding a severity level now means touching two places
    // (the enum and this switch) - for a security-relevant mapping that is a feature, since it
    // forces explicit intent rather than silently inheriting a new value.
    static bool TryParseSeverity(string? raw, out AdvisorySeverity severity)
    {
        severity = default;
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "low": severity = AdvisorySeverity.Low; return true;
            case "medium": severity = AdvisorySeverity.Medium; return true;
            case "high": severity = AdvisorySeverity.High; return true;
            case "critical": severity = AdvisorySeverity.Critical; return true;
            default: return false;
        }
    }
}
