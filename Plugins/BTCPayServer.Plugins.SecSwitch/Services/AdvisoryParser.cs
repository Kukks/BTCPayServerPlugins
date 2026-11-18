using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BTCPayServer.Plugins.SecSwitch.Models;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <param name="Id">The entry's id when it had one, so an unreadable entry still counts as "listed".</param>
public sealed record SkippedEntry(string? Id, string Reason);

public static class AdvisoryParser
{
    sealed record Entry(JsonElement Element, string Path, bool Conditional, string? Id);

    /// <summary>
    /// Parses direct advisories and the optional <c>conditionalAdvisories</c> array. A malformed envelope throws; a malformed entry is skipped
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

            var entries = ReadEntries(list, "advisories", conditional: false);
            if (doc.RootElement.TryGetProperty("conditionalAdvisories", out var conditionalList))
            {
                if (conditionalList.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("The advisory feed's \"conditionalAdvisories\" property must be an array.");
                entries = entries.Concat(ReadEntries(conditionalList, "conditionalAdvisories", conditional: true));
            }

            // A collision involving a conditional entry makes both versions ambiguous. Keep that id listed
            // without acting on the direct version where a dependency constraint might have been intended.
            var ambiguousIds = entries.Where(entry => entry.Id is not null)
                .GroupBy(entry => entry.Id!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Skip(1).Any() && group.Any(entry => entry.Conditional))
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var advisories = new List<Advisory>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry.Id is not null && ambiguousIds.Contains(entry.Id))
                    skipped.Add(new SkippedEntry(entry.Id, $"{entry.Path}: duplicate id '{entry.Id}'"));
                else if (!TryParse(entry.Element, entry.Conditional, out var advisory, out var error))
                    skipped.Add(new SkippedEntry(entry.Id, $"{entry.Path}: {error}"));
                else if (!ids.Add(advisory!.Id))
                    skipped.Add(new SkippedEntry(null, $"{entry.Path}: duplicate id '{advisory.Id}'"));
                else
                    advisories.Add(advisory);
            }
            return advisories;
        }
    }

    static IEnumerable<Entry> ReadEntries(JsonElement list, string property, bool conditional) =>
        list.EnumerateArray().Select((element, index) => new Entry(
            element, $"{property}[{index}]", conditional,
            element.ValueKind == JsonValueKind.Object ? GetString(element, "id") : null));

    static bool TryParse(JsonElement element, bool conditional, out Advisory? advisory, out string? error)
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
        else if (!TryParseDependency(element, identifier, conditional, out var requires, out error)) return false;
        else
        {
            error = null;
            advisory = new Advisory
            {
                Id = id,
                Identifier = identifier,
                AffectedVersions = affected!,
                FixedVersion = GetString(element, "fixedVersion"),
                Requires = requires,
                Severity = severity,
                Title = GetString(element, "title") ?? ""
            };
        }
        return advisory is not null;
    }

    static bool TryParseDependency(
        JsonElement element, string identifier, bool conditional, out AdvisoryDependency? requires, out string? error)
    {
        requires = null;
        error = null;
        var hasRequirement = element.TryGetProperty("requires", out var requirement);
        if (!conditional)
        {
            if (hasRequirement)
                error = "requires is only allowed in conditionalAdvisories";
            return !hasRequirement;
        }

        if (!hasRequirement || requirement.ValueKind != JsonValueKind.Object ||
            element.EnumerateObject().Count(property => property.NameEquals("requires")) != 1 ||
            requirement.EnumerateObject().Count() != 2)
        {
            error = "requires must contain exactly one plugin identifier and its affectedVersions";
            return false;
        }

        var dependency = GetString(requirement, "identifier");
        var affected = GetString(requirement, "affectedVersions");
        if (string.IsNullOrWhiteSpace(dependency) || !AdvisoryApplicability.IsDistinctPluginPair(identifier, dependency))
            error = "conditional advisories must name two distinct plugin identifiers";
        else if (!AdvisoryApplicability.TryParseCondition(affected, out _))
            error = $"requires.affectedVersions '{affected}' is not a valid version condition";
        else
            requires = new AdvisoryDependency(dependency, affected!);
        return requires is not null;
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
