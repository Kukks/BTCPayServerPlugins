#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.TicketTailor;

public class TicketTailorClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public TicketTailorClient(IHttpClientFactory httpClientFactory, string apiKey)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.BaseAddress = new Uri("https://api.tickettailor.com");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Encoders.Base64.EncodeData(Encoding.ASCII.GetBytes(apiKey)));
    }

    public async Task<Event[]> GetEvents()
    {
        return (await _httpClient.GetFromJsonAsync<DataHolder<Event[]>>("/v1/events"))?.Data;
    }

    public async Task<Event> GetEvent(string id)
    {
        return await _httpClient.GetFromJsonAsync<Event>($"/v1/events/{id}");
    }

    public async Task<(IssuedTicket?, string? error)> CreateTicket(IssueTicketRequest request)
    {
        var data = JsonSerializer.SerializeToElement(request).EnumerateObject().Select(property =>
                new KeyValuePair<string, string>(property.Name, property.Value.GetString()))
            .Where(pair => pair.Value != null);


        var response = await _httpClient.PostAsync($"/v1/issued_tickets", new FormUrlEncodedContent(data.ToArray()));
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            return (null, error);
        }

        return (await response.Content.ReadFromJsonAsync<IssuedTicket>(), null);
    }

    public async Task<(Hold?, string? error)> CreateHold(CreateHoldRequest request)
    {
        var data = new Dictionary<string, string>();
        data.Add("note", request.Note);
        data.Add("event_id", request.EventId);
        foreach (var i in request.TicketTypeId.Where(pair => pair.Value > 0))
        {
            data.Add($"ticket_type_id[{i.Key}]", i.Value.ToString());
        }

        var response = await _httpClient.PostAsync($"/v1/holds", new FormUrlEncodedContent(data));
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            return (null, error);
        }

        return (await response.Content.ReadFromJsonAsync<Hold>(), null);
    }



    public async Task<Hold?> GetHold(string holdId)
    {
        var response = await _httpClient.GetAsync($"/v1/holds/{holdId}");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }


        return await response.Content.ReadFromJsonAsync<Hold>();
    }

    public async Task<bool> DeleteHold(string holdId)
    {
        var response = await _httpClient.DeleteAsync($"/v1/holds/{holdId}");
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }
        return (await  response.Content.ReadFromJsonAsync<JsonObject>()).TryGetPropertyValue("deleted", out var jDeleted) &&
               jDeleted.GetValue<string>() == "true";
    }


    public async Task<IssuedTicket> GetTicket(string id)
    {
        return await _httpClient.GetFromJsonAsync<IssuedTicket>($"/v1/issued_tickets/{id}");
    }

    public class DataHolder<T>
    {
        [JsonPropertyName("data")] public T Data { get; set; }
    }


    public void Dispose()
    {
        _httpClient?.Dispose();
    }

    public class IssueTicketRequest
    {
        [JsonPropertyName("event_id")] public string EventId { get; set; }
        [JsonPropertyName("email")] public string Email { get; set; }
        [JsonPropertyName("full_name")] public string FullName { get; set; }
        [JsonPropertyName("reference")] public string Reference { get; set; }
        [JsonPropertyName("hold_id")] public string HoldId { get; set; }
        [JsonPropertyName("ticket_type_id")] public string TicketTypeId { get; set; }
    }

    public class Hold
    {

        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("note")] public string Note { get; set; }
        [JsonPropertyName("total_on_hold")] public int TotalOnHold { get; set; }
        [JsonPropertyName("quantities")] public HoldQuantity[] Quantities { get; set; }
    }

    public class HoldQuantity
    {

        [JsonPropertyName("ticket_type_id")] public string TicketTypeId { get; set; }
        [JsonPropertyName("quantity")] public int Quantity { get; set; }
    }

    public class CreateHoldRequest
    {

        [JsonPropertyName("event_id")] public string EventId { get; set; }
        [JsonPropertyName("note")] public string Note { get; set; }
        [JsonPropertyName("ticket_type_id")] public Dictionary<string, int> TicketTypeId { get; set; }
    }



    public class EventEnd
    {
        [JsonPropertyName("date")] public string Date { get; set; }

        [JsonPropertyName("formatted")] public string Formatted { get; set; }

        [JsonPropertyName("iso")] public DateTime Iso { get; set; }

        [JsonPropertyName("time")] public string Time { get; set; }

        [JsonPropertyName("timezone")] public string Timezone { get; set; }

        [JsonPropertyName("unix")] public int Unix { get; set; }
    }

    public class Images
    {
        [JsonPropertyName("header")] public string Header { get; set; }

        [JsonPropertyName("thumbnail")] public string Thumbnail { get; set; }
    }

    public class PaymentMethod
    {
        [JsonPropertyName("external_id")] public string ExternalId { get; set; }

        [JsonPropertyName("id")] public string Id { get; set; }

        [JsonPropertyName("instructions")] public string Instructions { get; set; }

        [JsonPropertyName("name")] public string Name { get; set; }

        [JsonPropertyName("type")] public string Type { get; set; }
    }

    public class Start
    {
        [JsonPropertyName("date")] public string Date { get; set; }

        [JsonPropertyName("formatted")] public string Formatted { get; set; }

        [JsonPropertyName("iso")] public DateTime Iso { get; set; }

        [JsonPropertyName("time")] public string Time { get; set; }

        [JsonPropertyName("timezone")] public string Timezone { get; set; }

        [JsonPropertyName("unix")] public int Unix { get; set; }
    }

    public class TicketGroup
    {
        [JsonPropertyName("id")] public string Id { get; set; }

        [JsonPropertyName("max_per_order")] public int? MaxPerOrder { get; set; }

        [JsonPropertyName("name")] public string Name { get; set; }

        [JsonPropertyName("sort_order")] public int SortOrder { get; set; }

        [JsonPropertyName("ticket_ids")] public List<string> TicketIds { get; set; }
    }

    public class TicketType
    {
        [JsonPropertyName("object")] public string Object { get; set; }

        [JsonPropertyName("id")] public string Id { get; set; }

        [JsonPropertyName("access_code")] public string AccessCode { get; set; }

        [JsonPropertyName("booking_fee")] public int BookingFee { get; set; }

        [JsonPropertyName("description")] public string Description { get; set; }

        [JsonPropertyName("group_id")] public string GroupId { get; set; }

        [JsonPropertyName("max_per_order")] public int? MaxPerOrder { get; set; }

        [JsonPropertyName("min_per_order")] public int? MinPerOrder { get; set; }

        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("price")] public decimal Price { get; set; }

        [JsonPropertyName("status")] public string Status { get; set; }

        [JsonPropertyName("sort_order")] public int SortOrder { get; set; }

        [JsonPropertyName("type")] public string Type { get; set; }

        [JsonPropertyName("quantity")] public int Quantity { get; set; }

        [JsonPropertyName("quantity_held")] public int QuantityHeld { get; set; }

        [JsonPropertyName("quantity_issued")] public int QuantityIssued { get; set; }

        [JsonPropertyName("quantity_total")] public int QuantityTotal { get; set; }
    }

    public class Venue
    {
        [JsonPropertyName("name")] public string Name { get; set; }

        [JsonPropertyName("postal_code")] public string PostalCode { get; set; }
    }

    public class IssuedTicket
    {
        [JsonPropertyName("id")] public string Id { get; set; }

        [JsonPropertyName("reference")] public string Reference { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; }
        [JsonPropertyName("status")] public string Status { get; set; }

        [JsonPropertyName("full_name")] public string FullName { get; set; }


        [JsonPropertyName("qr_code_url")] public string QrCodeUrl { get; set; }
        [JsonPropertyName("barcode_url")] public string BarcodeUrl { get; set; }
        [JsonPropertyName("barcode")] public string Barcode { get; set; }
        [JsonPropertyName("ticket_type_id")] public string TicketTypeId { get; set; }
    }

    public class Event
    {
        [JsonPropertyName("object")] public string Object { get; set; }

        [JsonPropertyName("id")] public string Id { get; set; }

        [JsonPropertyName("access_code")] public object AccessCode { get; set; }

        [JsonPropertyName("call_to_action")] public string CallToAction { get; set; }

        [JsonPropertyName("created_at")] public int CreatedAt { get; set; }

        [JsonPropertyName("currency")] public string Currency { get; set; }

        [JsonPropertyName("description")] public string Description { get; set; }

        [JsonPropertyName("end")] public EventEnd EventEnd { get; set; }

        [JsonPropertyName("hidden")] public string Hidden { get; set; }

        [JsonPropertyName("images")] public Images Images { get; set; }

        [JsonPropertyName("name")] public string Title { get; set; }

        [JsonPropertyName("online_event")] public string OnlineEvent { get; set; }

        [JsonPropertyName("payment_methods")] public List<PaymentMethod> PaymentMethods { get; set; }

        [JsonPropertyName("private")] public string Private { get; set; }

        [JsonPropertyName("start")] public Start Start { get; set; }

        [JsonPropertyName("status")] public string Status { get; set; }

        [JsonPropertyName("ticket_groups")] public List<TicketGroup> TicketGroups { get; set; }

        [JsonPropertyName("ticket_types")] public List<TicketType> TicketTypes { get; set; }

        [JsonPropertyName("tickets_available")]
        public string TicketsAvailable { get; set; }

        [JsonPropertyName("timezone")] public string Timezone { get; set; }

        [JsonPropertyName("total_holds")] public int TotalHolds { get; set; }

        [JsonPropertyName("total_issued_tickets")]
        public int TotalIssuedTickets { get; set; }

        [JsonPropertyName("total_orders")] public int TotalOrders { get; set; }

        [JsonPropertyName("unavailable")] public string Unavailable { get; set; }

        [JsonPropertyName("unavailable_status")]
        public object UnavailableStatus { get; set; }

        [JsonPropertyName("url")] public string Url { get; set; }

        [JsonPropertyName("venue")] public Venue Venue { get; set; }
    }

    public async Task<DiscountCode?> GetDiscountCode(string code)
    {
        var response = await _httpClient.GetAsync($"/v1/discounts?code={code}");
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var result = await response.Content.ReadFromJsonAsync<DataHolder<DiscountCode[]>>();
        return result?.Data?.FirstOrDefault();
    }
    public class DiscountCode
{
    public string id { get; set; }
    public string code { get; set; }
    public Expires? expires { get; set; }
    public int? max_redemptions { get; set; }
    public string name { get; set; }
    public int? face_value_amount { get; set; }
    public int? face_value_percentage { get; set; }
    public string[] ticket_types { get; set; }
    public int? times_redeemed { get; set; }
    public string type { get; set; }
    public class Expires
    {
        public string date { get; set; }
        public string formatted { get; set; }
        public string iso { get; set; }
        public string time { get; set; }
        public string timezone { get; set; }
        public long unix { get; set; }
    }
}




    
    
}

