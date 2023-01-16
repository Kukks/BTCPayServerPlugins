using System.Collections.Generic;

namespace BTCPayServer.Plugins.TicketTailor
{
    public class TicketTailorSettings
    {
        public string ApiKey { get; set; }
        public string EventId { get; set; }

        public bool ShowDescription { get; set; }
        public string CustomCSS { get; set; }
        public List<SpecificTicket> SpecificTickets { get; set; }
        public bool BypassAvailabilityCheck { get; set; }
    }
}
