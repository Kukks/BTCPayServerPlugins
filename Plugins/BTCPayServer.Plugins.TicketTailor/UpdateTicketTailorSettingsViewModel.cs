using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.TicketTailor;

public class UpdateTicketTailorSettingsViewModel
{
    public string NewSpecificTicket { get; set; }
    public string ApiKey { get; set; }
    public SelectList Events { get; set; }
    public string EventId { get; set; }
    public bool ShowDescription { get; set; }
    public string CustomCSS { get; set; }
    public TicketTailorClient.TicketType[] TicketTypes { get; set; }

    public List<SpecificTicket> SpecificTickets { get; set; }
    public bool BypassAvailabilityCheck { get; set; }
}

public class SpecificTicket
{
    public string TicketTypeId { get; set; }
    public decimal? Price { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public bool Hidden { get; set; }
}

public class TicketTailorViewModel
{
    public TicketTailorClient.Event Event { get; set; }
    public TicketTailorSettings Settings { get; set; }
}
