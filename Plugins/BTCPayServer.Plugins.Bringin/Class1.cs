using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.JsonConverters;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Bringin;

public class BringinClient
{
    public BringinClient(string apiKey, HttpClient httpClient)
    {
        ApiKey = apiKey;
        HttpClient = httpClient;
    }

    private string ApiKey { get; set; }
    private HttpClient HttpClient { get; set; }


    public async Task<string> GetUserId()
    {
        var response = await HttpClient.GetAsync($"/api/v0/user/user-id");
        var content = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode) return JObject.Parse(content)["userId"].ToString();
        var error = JObject.Parse(content).ToObject<BringinErrorResponse>();
        throw new BringinException(error);
    }

    public async Task<RateResponse> GetRate(string ticker = "BTCEUR")
    {
        var request = new {ticker};
        var content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
        var response = await HttpClient.PostAsync($"/api/v0/offramp/rates", content);
        var responseContent = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode) return JObject.Parse(responseContent).ToObject<RateResponse>();
        var error = JObject.Parse(responseContent).ToObject<BringinErrorResponse>();
        throw new BringinException(error);
    }

    public async Task<CreateOrderResponse> PlaceOrder(CreateOrderRequest request)
    {
        var content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
        var response = await HttpClient.PostAsync($"/api/v0/offramp/order/lightning", content);
        var responseContent = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode) return JObject.Parse(responseContent).ToObject<CreateOrderResponse>();
        var error = JObject.Parse(responseContent).ToObject<BringinErrorResponse>();
        throw new BringinException(error);
    }

    public async Task<RateResponse> GetOrderInfo(GetOrderRequest request)
    {
        var content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
        var response = await HttpClient.PostAsync($"/api/v0/offramp/order/lightning", content);
        var responseContent = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode) return JObject.Parse(responseContent).ToObject<RateResponse>();
        var error = JObject.Parse(responseContent).ToObject<BringinErrorResponse>();
        throw new BringinException(error);
    }

    public class GetOrderResponse
    {
        public string OrderId { get; set; }
        public string Status { get; set; }
        public string SubType { get; set; }

        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal SourceAmount { get; set; }

        public string SourceCurrency { get; set; }

        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal DestinationAmount { get; set; }

        public string DestinationCurrency { get; set; }
        public BringinPrice BringinPrice { get; set; }
    }

    public class BringinPrice
    {
        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal Price { get; set; }

        public string Currency { get; set; }
    }

    public class GetOrderRequest
    {
        public string UserId { get; set; }
        public string OrderId { get; set; }
    }

    public class CreateOrderResponse
    {
        public string Id { get; set; }

        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal Amount { get; set; }

        public string Invoice { get; set; }

        [JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
        [JsonProperty("expiresAt")]
        public string Expiry { get; set; }
    }

    public class CreateOrderRequest
    {
        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal SourceAmount { get; set; }

        [JsonProperty("ipAddress")] public string IP { get; set; }
    }

    public class RateResponse
    {
        public string Ticker { get; set; }
        public string Currency { get; set; }

        [JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
        public string Timestamp { get; set; }

        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal Price { get; set; }

        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal BringinPrice { get; set; }
    }

    public class BringinErrorResponse
    {
        public string Message { get; set; }
        public string StatusCode { get; set; }
        public string ErrorCode { get; set; }
        public JObject ErrorDetails { get; set; }
    }
}

public class BringinException : Exception
{
    private readonly BringinClient.BringinErrorResponse _error;

    public BringinException(BringinClient.BringinErrorResponse error)
    {
        _error = error;
    }
}