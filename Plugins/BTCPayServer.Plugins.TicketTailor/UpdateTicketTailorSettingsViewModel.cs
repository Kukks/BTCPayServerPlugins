using System.Collections.Generic;
using BTCPayServer.Models;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.TicketTailor;

public class UpdateTicketTailorSettingsViewModel
{
    public string AppName { get; set; }
    public bool Archived { get; set; }
    public string NewSpecificTicket { get; set; }
    public string ApiKey { get; set; }
    public SelectList Events { get; set; }
    public string EventId { get; set; }
    public bool ShowDescription { get; set; }
    public bool SendEmail { get; set; }
    public string CustomCSS { get; set; }
    public TicketTailorClient.TicketType[] TicketTypes { get; set; }

    public List<SpecificTicket> SpecificTickets { get; set; }
    public bool BypassAvailabilityCheck { get; set; }
    public bool RequireFullName { get; set; }
    public bool AllowDiscountCodes { get; set; }
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
    
    public string Name { get; set; }
    public string Email { get; set; }
            
    public PurchaseRequestItem[] Items { get; set; }
    public string AccessCode { get; set; }
    public string DiscountCode { get; set; }
    public StoreBrandingViewModel StoreBranding { get; set; }

    public class PurchaseRequestItem
    {
        public string TicketTypeId { get; set; }
        public int Quantity { get; set; }
    }
}
