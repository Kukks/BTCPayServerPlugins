using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BTCPayServer.Plugins.SecSwitch.Models;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.SecSwitch.Services;

/// <summary>
/// Bootstraps <see cref="SecSwitchSettings.TrustedKeys"/> from the plugin's own embedded
/// <c>Resources/trust-root.json</c> on first run (Task 13 hard requirement 3). Nothing else in the
/// plugin reads this resource - without this, <see cref="SecSwitchSettings.TrustedKeys"/> is always
/// empty on a fresh install, quorum can never be met, and the plugin is inert regardless of how it is
/// configured.
///
/// <see cref="Apply"/> is the pure, directly-testable half: given a resource payload and the current
/// key list, what should the resulting key list be. It never touches the filesystem, reflection, or
/// <see cref="ISettingsRepository"/> - a caller (<see cref="SecSwitchPeriodicTask"/>) supplies the
/// bytes and persists the result. <see cref="ReadEmbeddedTrustRoot"/> is the thin, harder-to-unit-test
/// half that actually locates and reads the embedded resource; kept separate so a test can drive
/// <see cref="Apply"/> with arbitrary (including malformed/hostile) byte arrays without needing a real
/// assembly manifest resource to do it.
///
/// The resource's schema is deliberately minimal - a JSON object with a <c>keys</c> array of armored
/// PGP public key blocks, nothing else per key. There is no separate "fingerprint" field anywhere in
/// this file for the exact reason <see cref="TrustedKey"/>'s own doc comment gives:
/// <see cref="AdvisoryVerifier.Verify"/> only ever admits a trusted entry whose OWN re-derived primary
/// fingerprint matches the label it is filed under, so a fingerprint sourced from anywhere else -
/// including this file, if it had one - would silently contribute nothing to quorum on a mismatch.
/// Every fingerprint here is derived via <see cref="AdvisoryVerifier.FingerprintOf"/> from the key
/// material itself; nothing is ever trusted from the JSON beyond the raw armored blob.
///
/// Never throws, from either <see cref="Apply"/> or <see cref="ReadEmbeddedTrustRoot"/>: a malformed
/// or absent resource, or a single unreadable bundled key, fails closed (with a log) rather than
/// aborting startup - mirroring the fail-closed-per-item posture <see cref="TrustStore"/> and
/// <see cref="AdvisoryVerifier"/> already apply to admin/rotation-supplied key material.
/// </summary>
public static class TrustRootBootstrapper
{
    /// <summary>
    /// The embedded resource's file name (see the plugin csproj's
    /// <c>&lt;EmbeddedResource Include="Resources\**" /&gt;</c>). Matched by suffix against
    /// <see cref="Assembly.GetManifestResourceNames"/> in <see cref="ReadEmbeddedTrustRoot"/> rather
    /// than assuming the full dotted manifest resource name (which depends on the assembly's default
    /// namespace) - the same defensive lookup style already used elsewhere in this codebase for a
    /// build-embedded resource (see core's <c>DBScriptsMigration.cs</c>).
    /// </summary>
    const string ResourceFileName = "trust-root.json";

    sealed class TrustRootFile
    {
        public string? Comment { get; set; }
        public string[] Keys { get; set; } = [];
    }

    /// <summary>
    /// Returns <paramref name="current"/> plus any bundled key from <paramref name="trustRootJson"/>
    /// not already present (matched by fingerprint, case-insensitively - the same convention every
    /// other trusted-key comparison in this plugin uses). Never removes or replaces an existing entry
    /// - an admin-added key, and a key added by a PRIOR call to this method, are both left untouched.
    /// A null/empty/malformed <paramref name="trustRootJson"/>, or a single unreadable bundled key
    /// entry, is logged and skipped rather than thrown - see the class doc comment.
    /// </summary>
    public static List<TrustedKey> Apply(
        IReadOnlyList<TrustedKey> current, byte[]? trustRootJson, ILogger logger)
    {
        var working = (current ?? Array.Empty<TrustedKey>()).Where(k => k is not null).ToList();

        if (trustRootJson is null || trustRootJson.Length == 0)
        {
            logger.LogWarning("SecSwitch trust-root resource is missing or empty; no bundled trusted keys were added.");
            return working;
        }

        TrustRootFile? file;
        try
        {
            file = JsonSerializer.Deserialize<TrustRootFile>(
                trustRootJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception e)
        {
            // Broader than JsonException deliberately - matches TrustStore.TryApplyRotation's own
            // "never throw has no carve-out for an unusual failure mode" posture for the identical
            // JsonSerializer.Deserialize call shape.
            logger.LogWarning(e, "SecSwitch trust-root resource is malformed; no bundled trusted keys were added.");
            return working;
        }

        if (file?.Keys is null || file.Keys.Length == 0)
            return working; // Nothing to add - not an error. Today's shipped resource is exactly this.

        var existing = new HashSet<string>(
            working.Where(k => !string.IsNullOrWhiteSpace(k.Fingerprint)).Select(k => k.Fingerprint),
            StringComparer.OrdinalIgnoreCase);

        foreach (var armored in file.Keys)
        {
            if (string.IsNullOrWhiteSpace(armored))
                continue;

            // CRITICAL CONTRACT, mirrors TrustStore.TryApplyRotation and
            // SecSwitchController.AddTrustedKey exactly: the fingerprint stored for a newly admitted
            // key is always re-derived from the key material itself via FingerprintOf, never accepted
            // from any other source - there is no other source in this file's schema to begin with
            // (see the class doc comment), but the derivation still has to happen somewhere, and it
            // must be this, not a value invented or copied from elsewhere.
            string fingerprint;
            try
            {
                fingerprint = AdvisoryVerifier.FingerprintOf(armored);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "SecSwitch trust-root resource contained an unreadable bundled key; skipping it.");
                continue;
            }

            if (existing.Contains(fingerprint))
                continue; // Already trusted (admin-added, or a prior bootstrap) - idempotent no-op.

            // Beyond FingerprintOf's own guard: a blob it can parse is not necessarily one
            // AdvisoryVerifier.LoadTrustedKeys would ever actually load (e.g. one over its own
            // byte-length cap) - admitting it here anyway would store a fingerprint that can never
            // again contribute to quorum. TryLoadTrustedKey is the exact same admission check
            // LoadTrustedKeys itself applies, shared so the two can never drift apart.
            if (!AdvisoryVerifier.TryLoadTrustedKey(fingerprint, armored, out _))
            {
                logger.LogWarning(
                    "SecSwitch trust-root resource contained a bundled key ({Fingerprint}) that could never be loaded as trusted; skipping it.",
                    fingerprint);
                continue;
            }

            working.Add(new TrustedKey { Fingerprint = fingerprint, ArmoredPublicKey = armored, Identity = fingerprint });
            existing.Add(fingerprint);
        }

        return working;
    }

    /// <summary>
    /// Reads the plugin's own embedded <c>Resources/trust-root.json</c> and returns its raw bytes, or
    /// null if it cannot be found or read for any reason (never throws - see the class doc comment).
    /// Uses <c>typeof(TrustRootBootstrapper).Assembly</c> rather than
    /// <see cref="Assembly.GetExecutingAssembly"/> so the resolved assembly is always the plugin's
    /// own, regardless of which assembly's code happens to call this method (a test in
    /// BTCPayServer.Plugins.SecSwitch.Tests, for instance).
    /// </summary>
    public static byte[]? ReadEmbeddedTrustRoot(ILogger logger)
    {
        try
        {
            var assembly = typeof(TrustRootBootstrapper).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + ResourceFileName, StringComparison.Ordinal));
            if (resourceName is null)
            {
                logger.LogWarning(
                    "SecSwitch could not find an embedded '{ResourceFileName}' resource in its own assembly; no bundled trusted keys were added.",
                    ResourceFileName);
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                return null;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "SecSwitch failed to read its embedded trust-root resource; no bundled trusted keys were added.");
            return null;
        }
    }
}
