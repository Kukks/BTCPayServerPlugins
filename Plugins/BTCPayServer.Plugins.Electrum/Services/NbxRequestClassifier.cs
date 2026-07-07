#nullable enable
using System;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace BTCPayServer.Plugins.Electrum.Services;

public enum NbxCallKind { WalletRead, WalletMutate, GlobalRead, Broadcast, TrackOrGenerate, Unknown }

public record NbxCall(NbxCallKind Kind, string? WalletId);

/// <summary>
/// Pure classification of an intercepted NBXplorer REST call into a kind + wallet id.
/// Mirrors the path patterns encoded in <see cref="ElectrumHttpHandler.SendAsync"/> —
/// kept as a standalone, unit-tested function so the routing decision (per-wallet
/// backend, Electrum vs real NBX) can be tested without spinning up the handler.
/// </summary>
public static class NbxRequestClassifier
{
    public static NbxCall Classify(HttpMethod method, string absolutePath, string query)
    {
        var path = absolutePath ?? "";
        query ??= "";

        // POST /v1/cryptos/{code}/derivations(/{strategy})? — GenerateWallet or Track
        if (method == HttpMethod.Post && Regex.IsMatch(path, @"/v1/cryptos/\w+/derivations(/[^/]+)?$"))
            return new NbxCall(NbxCallKind.TrackOrGenerate, ExtractStrategy(path));

        // GET /v1/cryptos/{code}/derivations/{strategy}/addresses/unused — GetUnused
        if (method == HttpMethod.Get && path.Contains("/addresses/unused"))
        {
            var reserve = query.Contains("reserve=true", StringComparison.OrdinalIgnoreCase);
            return new NbxCall(reserve ? NbxCallKind.WalletMutate : NbxCallKind.WalletRead, ExtractStrategy(path));
        }

        // GET /v1/cryptos/{code}/derivations/{strategy}/utxos — GetUTXOs
        if (method == HttpMethod.Get && path.EndsWith("/utxos"))
            return new NbxCall(NbxCallKind.WalletRead, ExtractStrategy(path));

        // GET /v1/cryptos/{code}/derivations/{strategy}/balance — GetBalance
        if (method == HttpMethod.Get && path.EndsWith("/balance"))
            return new NbxCall(NbxCallKind.WalletRead, ExtractStrategy(path));

        // GET /v1/cryptos/{code}/derivations/{strategy}/transactions[/{txId}] — GetTransaction(s)
        if (method == HttpMethod.Get && Regex.IsMatch(path, @"/derivations/[^/]+/transactions(/[0-9a-fA-F]{64})?$"))
            return new NbxCall(NbxCallKind.WalletRead, ExtractStrategy(path));

        // GET /v1/cryptos/{code}/transactions/{txId} — GetTransaction by txid only (no wallet scope)
        if (method == HttpMethod.Get && Regex.IsMatch(path, @"/v1/cryptos/\w+/transactions/[0-9a-fA-F]{64}$"))
            return new NbxCall(NbxCallKind.GlobalRead, null);

        // POST /v1/cryptos/{code}/transactions — Broadcast
        if (method == HttpMethod.Post && Regex.IsMatch(path, @"/v1/cryptos/\w+/transactions$"))
            return new NbxCall(NbxCallKind.Broadcast, null);

        // GET /v1/cryptos/{code}/fees/{blockTarget} and /v1/cryptos/{code}/status — GlobalRead
        if (method == HttpMethod.Get &&
            (Regex.IsMatch(path, @"/v1/cryptos/\w+/fees/\d+$") || path.EndsWith("/status")))
            return new NbxCall(NbxCallKind.GlobalRead, null);

        // GET /v1/cryptos/{code}/derivations/{scheme}/metadata/{key} — GetMetadata
        if (method == HttpMethod.Get && Regex.IsMatch(path, @"/derivations/[^/]+/metadata/[^/]+$"))
            return new NbxCall(NbxCallKind.WalletRead, ExtractStrategy(path));

        // POST /v1/cryptos/{code}/derivations/{scheme}/metadata/{key} — SetMetadata
        if (method == HttpMethod.Post && Regex.IsMatch(path, @"/derivations/[^/]+/metadata/[^/]+$"))
            return new NbxCall(NbxCallKind.WalletMutate, ExtractStrategy(path));

        // POST /v1/cryptos/{code}/derivations/{strategy}/psbt/create — CreatePSBT
        if (method == HttpMethod.Post && path.EndsWith("/psbt/create"))
            return new NbxCall(NbxCallKind.WalletMutate, ExtractStrategy(path));

        // GET /v1/cryptos/{code}/derivations/{strategy}/utxos/scan — GetScanUTXOSetInformation
        if (method == HttpMethod.Get && path.EndsWith("/utxos/scan"))
            return new NbxCall(NbxCallKind.WalletRead, ExtractStrategy(path));

        // POST /v1/cryptos/{code}/derivations/{strategy}/utxos/scan — ScanUTXOSetAsync
        if (method == HttpMethod.Post && path.EndsWith("/utxos/scan"))
            return new NbxCall(NbxCallKind.WalletMutate, ExtractStrategy(path));

        // POST /v1/cryptos/{code}/derivations/{strategy}/utxos/wipe — WipeAsync
        if (method == HttpMethod.Post && path.EndsWith("/utxos/wipe"))
            return new NbxCall(NbxCallKind.WalletMutate, ExtractStrategy(path));

        return new NbxCall(NbxCallKind.Unknown, null);
    }

    private static string? ExtractStrategy(string path)
    {
        var match = Regex.Match(path, @"/derivations/([^/]+)");
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }
}
