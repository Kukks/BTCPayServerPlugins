using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using NNostr.Client;

namespace BTCPayServer.Plugins.NIP05;

public class ZapperSettings
{
    public ZapperSettings(string ZapperPrivateKey)
    {
        this.ZapperPrivateKey = ZapperPrivateKey;
    }

    public ZapperSettings()
    {
        
    }

    [JsonIgnore]
    public ECPrivKey ZappingKey => NostrExtensions.ParseKey(ZapperPrivateKey);
    [JsonIgnore]
    public ECXOnlyPubKey ZappingPublicKey => ZappingKey.CreateXOnlyPubKey();
    [JsonIgnore]
    public string ZappingPublicKeyHex => ZappingPublicKey.ToHex();
    public string ZapperPrivateKey { get; set; }
}