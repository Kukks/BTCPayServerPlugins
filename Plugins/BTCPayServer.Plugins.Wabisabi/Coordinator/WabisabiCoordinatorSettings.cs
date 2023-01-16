using System;
using System.Collections.Generic;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using NNostr.Client;

namespace WalletWasabi.Backend.Controllers;

public class WabisabiCoordinatorSettings
{
    public bool Enabled { get; set; } = false;
    
    public string NostrIdentity { get; set; }
    public Uri NostrRelay { get; set; } = new Uri("wss://relay.damus.io");
    
    public List<DiscoveredCoordinator> DiscoveredCoordinators { get; set; } = new();
    
    [JsonIgnore] public ECPrivKey? Key =>  string.IsNullOrEmpty(NostrIdentity)? null: NostrExtensions.ParseKey(NostrIdentity);
    [JsonIgnore] public ECXOnlyPubKey PubKey =>  Key?.CreatePubKey().ToXOnlyPubKey();
}

public class DiscoveredCoordinator
{
    public Uri Uri { get; set; }
    public string Name { get; set; }
}
