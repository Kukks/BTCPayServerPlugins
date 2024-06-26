using System;
using System.Collections.Generic;
using NBitcoin.Secp256k1;
using Newtonsoft.Json;
using NNostr.Client;

namespace WalletWasabi.Backend.Controllers;

public class WabisabiCoordinatorSettings
{
    public bool Enabled { get; set; } = false;
    public Uri ForwardEndpoint { get; set; } = new Uri("https://wasabiwallet.io/");
    public string NostrIdentity { get; set; }
    public Uri NostrRelay { get; set; } = new Uri("wss://relay.primal.net");
    
    public List<DiscoveredCoordinator> DiscoveredCoordinators { get; set; } = new();


    public ECPrivKey GetKey() => string.IsNullOrEmpty(NostrIdentity) ? null : NostrExtensions.ParseKey(NostrIdentity);

    public ECXOnlyPubKey GetPubKey() => GetKey()?.CreatePubKey().ToXOnlyPubKey();
    public Uri UriToAdvertise { get; set; }
    public string TermsConditions { get; set; } =  @"
These terms and conditions govern your use of the Coinjoin Coordinator service. By using the service, you agree to be bound by these terms and conditions. If you do not agree to these terms and conditions, you should not use the service.

Coinjoin Coordinator Service: The Coinjoin Coordinator service is a tool that allows users to anonymize their cryptocurrency transactions by pooling them with other users' funds and sending them within a common transaction. The service does not store, transmit, or otherwise handle users' cryptocurrency funds.

Legal Compliance: You are responsible for complying with all applicable laws and regulations in your jurisdiction in relation to your use of the Coinjoin Coordinator service. The service is intended to be used for lawful purposes only.

No Warranty: The Coinjoin Coordinator service is provided on an ""as is"" and ""as available"" basis, without any warranty of any kind, either express or implied, including but not limited to the implied warranties of merchantability and fitness for a particular purpose.

Limitation of Liability: In no event shall the Coinjoin Coordinator be liable for any direct, indirect, incidental, special, or consequential damages, or loss of profits, arising out of or in connection with your use of the service.

Indemnification: You agree to indemnify and hold the Coinjoin Coordinator, its affiliates, officers, agents, and employees harmless from any claim or demand, including reasonable attorneys' fees, made by any third party due to or arising out of your use of the service, your violation of these terms and conditions, or your violation of any rights of another.

Severability: If any provision of these terms and conditions is found to be invalid or unenforceable, the remaining provisions shall remain in full force and effect.

Governing Law: These terms and conditions shall be governed by and construed in accordance with the laws of the jurisdiction in which the Coinjoin Coordinator is based.

Changes to Terms and Conditions: Coinjoin Coordinator reserves the right, at its sole discretion, to modify or replace these terms and conditions at any time.
";

    public string CoordinatorDescription { get; set; }
}

public class DiscoveredCoordinator
{
    public Uri Uri { get; set; }
    public string Name { get; set; }
    public string Relay { get; set; }
    public string Description { get; set; }
    public string? CoinjoinIdentifier { get; set; }
}
