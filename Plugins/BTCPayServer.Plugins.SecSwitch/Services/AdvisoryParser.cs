using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <param name="Id">The entry's id when it had one, so an unreadable entry still counts as "listed".</param>
public sealed record SkippedEntry(string? Id, string Reason);

public static class AdvisoryParser
{
    /// <summary>
    /// Parses <c>{"advisories": [...]}</c>. A malformed envelope throws; a malformed entry is skipped
    /// (reported in <paramref name="skipped"/>) so one bad entry cannot hide the rest of the feed.
    /// </summary>
    public static List<Advisory> ParseFeed(byte[] json, out List<SkippedEntry> skipped)
    {
        skipped = [];
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"Malformed advisory feed JSON: {e.Message}", e);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("advisories", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The advisory feed must be a JSON object with an \"advisories\" array.");

            var advisories = new List<Advisory>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = -1;
            foreach (var element in list.EnumerateArray())
            {
                index++;
                if (!TryParse(element, out var advisory, out var error))
                    skipped.Add(new SkippedEntry(element.ValueKind == JsonValueKind.Object ? GetString(element, "id") : null, $"advisories[{index}]: {error}"));
                else if (!ids.Add(advisory!.Id))
                    skipped.Add(new SkippedEntry(null, $"advisories[{index}]: duplicate id '{advisory.Id}'"));
                else
                    advisories.Add(advisory);
            }
            return advisories;
        }
    }

    static bool TryParse(JsonElement element, out Advisory? advisory, out string? error)
    {
        advisory = null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            error = "entry must be a JSON object";
            return false;
        }

        var id = GetString(element, "id");
        var identifier = GetString(element, "identifier");
        var affected = GetString(element, "affectedVersions");
        var severityRaw = GetString(element, "severity");

        if (string.IsNullOrWhiteSpace(id)) error = "missing required field: id";
        else if (string.IsNullOrWhiteSpace(identifier)) error = "missing required field: identifier";
        else if (!AdvisoryApplicability.TryParseCondition(affected, out _)) error = $"affectedVersions '{affected}' is not a valid version condition";
        else if (!TryParseSeverity(severityRaw, out var severity)) error = $"unrecognised severity '{severityRaw}'";
        else
        {
            error = null;
            advisory = new Advisory
            {
                Id = id,
                Identifier = identifier,
                AffectedVersions = affected!,
                FixedVersion = GetString(element, "fixedVersion"),
                Severity = severity,
                Title = GetString(element, "title") ?? ""
            };
        }
        return advisory is not null;
    }

    static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Explicit allowlist: Enum.TryParse would also accept numeric strings such as "7".
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
