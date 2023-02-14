using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.TicketTailor
{
    public class TicketTailorSettings
    {
        [Newtonsoft.Json.JsonIgnore][JsonIgnore]
        public string ApiKey { get; set; }
        public string EventId { get; set; }

        public bool ShowDescription { get; set; }
        public string CustomCSS { get; set; }
        public List<SpecificTicket> SpecificTickets { get; set; }
        public bool BypassAvailabilityCheck { get; set; }
    }
}
