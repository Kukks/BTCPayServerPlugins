#nullable enable
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.NIP05
{
    public class Nip5StoreSettings
    {
        [Required] public string PubKey { get; set; }
        
        public string? PrivateKey { get; set; }
        [Required] public string Name { get; set; }

        public string[]? Relays { get; set; }
    }
}