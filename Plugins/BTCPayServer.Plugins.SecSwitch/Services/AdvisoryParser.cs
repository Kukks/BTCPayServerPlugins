using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;
using PluginDependency = BTCPayServer.Abstractions.Contracts.IBTCPayServerPlugin.PluginDependency;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <param name="Id">The entry's id when it had one, so an unreadable entry still counts as "listed".</param>
public sealed record SkippedEntry(string? Id, string Reason);

public static class AdvisoryParser
{
    sealed record Entry(Advisory? Advisory, string Path, bool Conditional, string? Id, string? Error);

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
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The advisory feed must be a JSON object with an \"advisories\" array.");

            var list = ReadArray(root, "advisories", required: true);
            var conditionalList = ReadArray(root, "conditionalAdvisories");
            var entries = ReadEntries(list, "advisories", conditional: false)
                .Concat(ReadEntries(conditionalList, "conditionalAdvisories", conditional: true))
                .ToList();

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
                else if (entry.Advisory is not { } advisory)
                    skipped.Add(new SkippedEntry(entry.Id, $"{entry.Path}: {entry.Error}"));
                else if (!ids.Add(advisory.Id))
                    skipped.Add(new SkippedEntry(advisory.Id, $"{entry.Path}: duplicate id '{advisory.Id}'"));
                else
                    advisories.Add(advisory);
            }
            return advisories;
        }
    }

    static JsonElement ReadArray(JsonElement root, string name, bool required = false)
    {
        // TryGetProperty alone selects the last occurrence. An empty duplicate could look like a withdrawal.
        if (root.EnumerateObject().Where(property => property.NameEquals(name)).Take(2).Count() > 1)
            throw new InvalidDataException($"The advisory feed repeats its \"{name}\" property.");
        if (root.TryGetProperty(name, out var array) ? array.ValueKind != JsonValueKind.Array : required)
            throw new InvalidDataException($"The advisory feed's \"{name}\" property must be an array.");
        return array;
    }

    static IEnumerable<Entry> ReadEntries(JsonElement list, string property, bool conditional) =>
        list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select((element, index) => ReadEntry(element, $"{property}[{index}]", conditional))
            : [];

    static Entry ReadEntry(JsonElement element, string path, bool conditional)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new Entry(null, path, conditional, null, "entry must be a JSON object");

        string? id = null;
        try
        {
            id = GetString(element, "id");
            var advisory = ParseAdvisory(element, id, conditional, out var error);
            return new Entry(advisory, path, conditional, id, error);
        }
        catch (InvalidOperationException e)
        {
            // JsonDocument can read the envelope before GetString detects invalid Unicode in an entry.
            return new Entry(null, path, conditional, id, $"entry contains undecodable JSON text: {e.Message}");
        }
    }

    static Advisory? ParseAdvisory(JsonElement element, string? id, bool conditional, out string? error)
    {
        var identifier = GetString(element, "identifier");
        var affected = GetString(element, "affectedVersions");
        var severityRaw = GetString(element, "severity");

        if (string.IsNullOrWhiteSpace(id)) error = "missing required field: id";
        else if (string.IsNullOrWhiteSpace(identifier)) error = "missing required field: identifier";
        else if (!AdvisoryApplicability.TryParseCondition(affected, out _)) error = $"affectedVersions '{affected}' is not a valid version condition";
        else if (!TryParseSeverity(severityRaw, out var severity)) error = $"unrecognised severity '{severityRaw}'";
        else if (TryParseDependencies(element, identifier, conditional, out var dependencies, out error))
        {
            return new Advisory
            {
                Id = id,
                Identifier = identifier,
                AffectedVersions = affected!,
                FixedVersion = GetString(element, "fixedVersion"),
                Dependencies = dependencies,
                Severity = severity,
                Title = GetString(element, "title") ?? ""
            };
        }
        return null;
    }

    static bool TryParseDependencies(
        JsonElement element, string identifier, bool conditional, out PluginDependency[] dependencies, out string? error)
    {
        dependencies = [];
        error = null;
        var qualifiers = element.EnumerateObject()
            .Where(property => IsProperty(property.Name, "dependencies") || IsProperty(property.Name, "requires"))
            .Take(2)
            .ToArray();
        if (!conditional)
        {
            if (qualifiers.Length != 0)
                error = "dependencies and requires are only allowed in conditionalAdvisories";
            return qualifiers.Length == 0;
        }
        if (qualifiers.Length != 1)
        {
            error = "conditional advisories must specify either dependencies or requires once";
            return false;
        }
        if (string.Equals(identifier, AdvisoryApplicability.CoreIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            error = "conditional advisories must target a plugin";
            return false;
        }

        var qualifier = qualifiers[0];
        if (IsProperty(qualifier.Name, "requires"))
            return TryParseLegacyRequirement(qualifier.Value, identifier, out dependencies, out error);

        if (qualifier.Value.ValueKind != JsonValueKind.Array || qualifier.Value.GetArrayLength() == 0)
        {
            error = "dependencies must be a nonempty array";
            return false;
        }

        var parsed = new List<PluginDependency>();
        foreach (var item in qualifier.Value.EnumerateArray())
        {
            if (!TryParseNativeDependency(item, identifier, out var dependency))
            {
                error = $"dependencies[{parsed.Count}] must name a different component with a valid optional condition";
                return false;
            }
            parsed.Add(dependency!);
        }
        dependencies = parsed.ToArray();
        return true;
    }

    static bool TryParseNativeDependency(JsonElement item, string target, out PluginDependency? dependency)
    {
        dependency = null;
        if (item.ValueKind != JsonValueKind.Object)
            return false;

        JsonElement identifier = default;
        JsonElement condition = default;
        foreach (var property in item.EnumerateObject())
        {
            if (IsProperty(property.Name, "identifier") && identifier.ValueKind == JsonValueKind.Undefined)
                identifier = property.Value;
            else if (IsProperty(property.Name, "condition") && condition.ValueKind == JsonValueKind.Undefined)
                condition = property.Value;
            else
                return false;
        }

        var dependencyId = identifier.ValueKind == JsonValueKind.String ? identifier.GetString() : null;
        if (string.IsNullOrWhiteSpace(dependencyId) || !AdvisoryApplicability.IsAllowedDependency(target, dependencyId) ||
            condition.ValueKind is not (JsonValueKind.String or JsonValueKind.Null or JsonValueKind.Undefined))
            return false;
        var rawCondition = condition.ValueKind == JsonValueKind.String ? condition.GetString() : null;
        if (!AdvisoryApplicability.TryParseDependencyCondition(rawCondition, out _))
            return false;

        dependency = new PluginDependency { Identifier = dependencyId, Condition = rawCondition };
        return true;
    }

    static bool TryParseLegacyRequirement(
        JsonElement requirement, string identifier, out PluginDependency[] dependencies, out string? error)
    {
        dependencies = [];
        error = null;
        if (requirement.ValueKind != JsonValueKind.Object || requirement.EnumerateObject().Count() != 2)
        {
            error = "requires must contain exactly one plugin identifier and its affectedVersions";
            return false;
        }

        var dependency = GetString(requirement, "identifier");
        var affected = GetString(requirement, "affectedVersions");
        if (string.IsNullOrWhiteSpace(dependency) || !AdvisoryApplicability.IsAllowedDependency(identifier, dependency) ||
            string.Equals(dependency, AdvisoryApplicability.CoreIdentifier, StringComparison.OrdinalIgnoreCase))
            error = "conditional advisories must name two distinct plugin identifiers";
        else if (!AdvisoryApplicability.TryParseCondition(affected, out var condition))
            error = $"requires.affectedVersions '{affected}' is not a valid version condition";
        else
            // Legacy requires never matched "!", even when the plugin was absent. Only a standalone
            // "!" in a native dependency can match absence, so use a compound that also never matches.
            dependencies = [new PluginDependency { Identifier = dependency, Condition = condition is VersionCondition.Not ? "! && !" : affected! }];
        return dependencies.Length != 0;
    }

    static bool IsProperty(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

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
