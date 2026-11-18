using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;
using PluginDependency = BTCPayServer.Abstractions.Contracts.IBTCPayServerPlugin.PluginDependency;

namespace BTCPayServer.Plugins.SecSwitch.Services;

public static class AdvisoryApplicability
{
    public const string CoreIdentifier = "BTCPayServer";

    /// <summary>Callers match this phrase in the reason to tell "cannot tell" apart from "does not apply".</summary>
    public const string IndeterminateVersionPhrase = "could not be determined";

    /// <summary>
    /// Plugins are published and loaded with four-part versions ("1.2.3.0") while advisories usually write three
    /// ("1.2.3"), and System.Version orders a missing part before 0. Both sides are padded so boundaries compare right.
    /// </summary>
    public static Version Pad(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    /// <summary>
    /// Parses an <c>affectedVersions</c> value. VersionCondition reads a blank string, or a whitespace-only clause such
    /// as "&lt;1.0.0 || || &gt;=2.0.0", as "matches every version", so those are rejected instead of being allowed to
    /// act on everything. It silently drops zero-length segments ("a||||b" becomes "a||b"); those are rejected too,
    /// because a clause the author meant to write is then missing without anyone noticing.
    /// </summary>
    public static bool TryParseCondition(string? text, out VersionCondition? condition)
    {
        condition = null;
        if (string.IsNullOrWhiteSpace(text) || HasEmptySegment(text) ||
            !VersionCondition.TryParse(text, out var parsed) || ContainsBlankClause(parsed))
            return false;

        PadVersions(parsed);
        condition = parsed;
        return true;
    }

    internal static bool TryParseDependencyCondition(string? text, out VersionCondition? condition)
    {
        if (!string.IsNullOrWhiteSpace(text))
            return TryParseCondition(text, out condition);

        condition = new VersionCondition.Yes();
        return true;
    }

    /// <summary>
    /// A fix is only usable when it is newer than the installed version and itself outside the affected range;
    /// anything else would downgrade, or reinstall a vulnerable version.
    /// </summary>
    public static bool HasUsableFix(Advisory advisory, Version installed) =>
        Version.TryParse(advisory.FixedVersion, out var fix) &&
        TryParseCondition(advisory.AffectedVersions, out var condition) &&
        Pad(fix) > Pad(installed) && !condition!.IsFulfilled(Pad(fix));

    public static bool IsApplicable(Advisory advisory, InstanceState state, out Version? installedVersion, out string reason) =>
        IsApplicable(advisory, state, null, out installedVersion, out reason);

    /// <summary>Checks whether a candidate update would leave a related advisory applicable to the resulting versions.</summary>
    public static bool IsApplicableAfterUpdate(Advisory advisory, InstanceState state, string identifier, Version version) =>
        (SameIdentifier(advisory.Identifier, identifier) ||
         advisory.Dependencies.Any(dependency => SameIdentifier(dependency.Identifier, identifier))) &&
        IsApplicable(advisory, state, (identifier, version), out _, out _);

    internal static bool IsAllowedDependency(string target, string dependency) =>
        !SameIdentifier(target, CoreIdentifier) && !string.IsNullOrWhiteSpace(dependency) &&
        !SameIdentifier(target, dependency);

    static bool IsApplicable(Advisory advisory, InstanceState state, (string Identifier, Version Version)? update,
        out Version? installedVersion, out string reason)
    {
        if (!TryGetVersion(state, advisory.Identifier, update, out installedVersion))
        {
            reason = $"{advisory.Identifier} is not installed on this instance.";
            return false;
        }

        if (installedVersion is null)
        {
            reason = $"Installed version of {advisory.Identifier} {IndeterminateVersionPhrase}.";
            return false;
        }

        if (!TryParseCondition(advisory.AffectedVersions, out var condition))
        {
            reason = $"affectedVersions '{advisory.AffectedVersions}' could not be parsed.";
            installedVersion = null;
            return false;
        }

        if (!condition!.IsFulfilled(Pad(installedVersion)))
        {
            reason = $"Installed version {installedVersion} is not affected by {advisory.AffectedVersions}.";
            installedVersion = null;
            return false;
        }

        var reasons = new List<string> { $"Installed version {installedVersion} matches {advisory.AffectedVersions}." };
        foreach (var dependency in advisory.Dependencies)
        {
            if (!DependencyMatches(advisory.Identifier, dependency, state, update, out reason))
            {
                installedVersion = null;
                return false;
            }
            reasons.Add(reason);
        }

        reason = string.Join(" ", reasons);
        return true;
    }

    static bool DependencyMatches(string target, PluginDependency dependency, InstanceState state,
        (string Identifier, Version Version)? update, out string reason)
    {
        if (!IsAllowedDependency(target, dependency.Identifier) ||
            !TryParseDependencyCondition(dependency.Condition, out var condition))
        {
            reason = "The dependency identifier or condition is invalid.";
            return false;
        }

        var present = TryGetVersion(state, dependency.Identifier, update, out var version);
        if (condition is VersionCondition.Not)
        {
            reason = present
                ? $"Dependency {dependency.Identifier} must be absent."
                : $"Dependency {dependency.Identifier} is absent.";
            return !present;
        }
        if (!present)
            reason = $"Dependency {dependency.Identifier} is not present on this instance.";
        else if (condition is VersionCondition.Yes)
        {
            reason = $"Dependency {dependency.Identifier} is present.";
            return true;
        }
        else if (version is null)
            reason = $"Installed version of dependency {dependency.Identifier} {IndeterminateVersionPhrase}.";
        else if (!condition!.IsFulfilled(Pad(version)))
            reason = $"Dependency {dependency.Identifier} {version} does not match {dependency.Condition}.";
        else
        {
            reason = $"Dependency {dependency.Identifier} {version} matches {dependency.Condition}.";
            return true;
        }

        return false;
    }

    static bool TryGetVersion(InstanceState state, string identifier, (string Identifier, Version Version)? update,
        out Version? version)
    {
        if (SameIdentifier(identifier, CoreIdentifier))
        {
            version = state.CoreVersion;
        }
        else
        {
            var match = state.InstalledPlugins.FirstOrDefault(kv => SameIdentifier(kv.Key, identifier));
            version = match.Value;
            if (match.Key is null)
                return false;
        }

        if (update is { } candidate && SameIdentifier(identifier, candidate.Identifier))
            version = candidate.Version;
        return true;
    }

    static bool SameIdentifier(string? first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    static bool HasEmptySegment(string text) =>
        text.Split(["||", "&&"], StringSplitOptions.None).Any(string.IsNullOrWhiteSpace);

    static bool ContainsBlankClause(VersionCondition condition) => condition switch
    {
        VersionCondition.Yes => true,
        VersionCondition.All all => all.Conditions.Any(ContainsBlankClause),
        VersionCondition.Any any => any.Conditions.Any(ContainsBlankClause),
        _ => false
    };

    static void PadVersions(VersionCondition condition)
    {
        switch (condition)
        {
            case VersionCondition.Op op:
                op.Version = Pad(op.Version);
                break;
            case VersionCondition.All all:
                foreach (var inner in all.Conditions)
                    PadVersions(inner);
                break;
            case VersionCondition.Any any:
                foreach (var inner in any.Conditions)
                    PadVersions(inner);
                break;
        }
    }
}
