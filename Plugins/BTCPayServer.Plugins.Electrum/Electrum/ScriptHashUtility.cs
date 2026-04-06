using System;
using System.Security.Cryptography;
using NBitcoin;

namespace BTCPayServer.Plugins.Electrum;

public static class ScriptHashUtility
{
    public static string ComputeScriptHash(Script script)
    {
        var scriptBytes = script.ToBytes();
        var hash = SHA256.HashData(scriptBytes);
        Array.Reverse(hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
