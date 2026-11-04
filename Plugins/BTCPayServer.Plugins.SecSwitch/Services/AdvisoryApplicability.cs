using System;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.SecSwitch.Models;

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

    /// <summary>
    /// A fix is only usable when it is newer than the installed version and itself outside the affected range;
    /// anything else would downgrade, or reinstall a vulnerable version.
    /// </summary>
    public static bool HasUsableFix(Advisory advisory, Version installed) =>
        Version.TryParse(advisory.FixedVersion, out var fix) &&
        TryParseCondition(advisory.AffectedVersions, out var condition) &&
        Pad(fix) > Pad(installed) && !condition!.IsFulfilled(Pad(fix));

    public static bool IsApplicable(Advisory advisory, InstanceState state, out Version? installedVersion, out string reason)
    {
        installedVersion = null;

        if (string.Equals(advisory.Identifier, CoreIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            installedVersion = state.CoreVersion;
        }
        else
        {
            var match = state.InstalledPlugins.FirstOrDefault(kv =>
                string.Equals(kv.Key, advisory.Identifier, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
            {
                reason = $"{advisory.Identifier} is not installed on this instance.";
                return false;
            }
            installedVersion = match.Value;
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

        reason = $"Installed version {installedVersion} matches {advisory.AffectedVersions}.";
        return true;
    }

    static bool HasEmptySegment(string text) =>
        text.Split("||").Any(string.IsNullOrWhiteSpace) || text.Split("&&").Any(string.IsNullOrWhiteSpace);

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
