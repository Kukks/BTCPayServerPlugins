using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.JsonConverters;
using NBitcoin;
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


    public static Uri GetDashboardUri(Network network)
    {
        if (network.ChainName == ChainName.Mainnet)
        {
            return new Uri("https://app.bringin.xyz");
        }

        return new Uri("https://dev-app.bringin.xyz");
    }

    public static Uri GetApiUrl(Network network)
    {
        if (network.ChainName == ChainName.Mainnet)
        {
            return new Uri("https://api.bringin.xyz");
        }

        return new Uri("https://dev.bringin.xyz");
    }


    public static HttpClient CreateClient(Network network, IHttpClientFactory httpClientFactory, string? apiKey = null)
    {
        var httpClient = httpClientFactory.CreateClient("bringin");
        httpClient.BaseAddress = GetApiUrl(network);
        if (apiKey != null)
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("api-key", apiKey);
        return httpClient;
    }

    public static async Task<Uri> OnboardUri(HttpClient httpClient, Uri callback, Network network)
    {
        var content = new StringContent(JsonConvert.SerializeObject(new
        {
            callback
        }), Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync($"/api/v0/application/btcpay/signup-url", content);
        var responseContent = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode) return new Uri(JObject.Parse(responseContent)["signupURL"].ToString());
        return GetDashboardUri(network);
    }

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
        var response = await HttpClient.PostAsync($"/api/v0/offramp/order", content);
        var responseContent = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode) return JObject.Parse(responseContent).ToObject<CreateOrderResponse>();
        var error = JObject.Parse(responseContent).ToObject<BringinErrorResponse>();
        throw new BringinException(error);
    }

    public async Task<decimal> GetFiatBalance(string currency = "EUR")
    {
        var content = new StringContent(JsonConvert.SerializeObject(new
        {
            currency
        }), Encoding.UTF8, "application/json");
        var response = await HttpClient.PostAsync($"/api/v0/user/get-balance/fiat", content);
        var responseContent = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
        {
            var balance = JObject.Parse(responseContent).ToObject<BalanceResponse>();
            return balance.Balance / 100m; //response is in cents 
        }

        var error = JObject.Parse(responseContent).ToObject<BringinErrorResponse>();
        throw new BringinException(error);
    }

    public class BalanceResponse
    {
        [JsonProperty("balance")]
        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal Balance { get; set; }
    }
    
    
    public async Task<GetTransactionListResponse> GetTransactions()
    {
        var content = new StringContent(JsonConvert.SerializeObject(new
        {
            startDate = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeMilliseconds(),
            endDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }), Encoding.UTF8, "application/json");
        var response = await HttpClient.PostAsync($"/api/v0/account/transactions", content);
        var responseContent = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
        {
            return JObject.Parse(responseContent).ToObject<GetTransactionListResponse>();
        }

        var error = JObject.Parse(responseContent).ToObject<BringinErrorResponse>();
        throw new BringinException(error);
    }

    public class GetTransactionListResponse
    {
        [JsonProperty("transactions")]
        public BringinTransaction[] Transactions { get; set; }
    }
    
    
    public class BringinTransaction
    {
        // {
        //     "orderId": "3521154c-30b4-480c-834d-38f80d507963",
        //     "type": "OFFRAMP_WITHOUT_FIAT_WITHDRAWAL",
        //     "subType": "LIGHTNING",
        //     "sourceAmount": "100000",
        //     "sourceCurrency": "BTC",
        //     "destinationAmount": "3816",
        //     "destinationAddress": "b0a4c862-c941-4d3c-8727-18e5097a3b5a",
        //     "destinationCurrency": "EUR",
        //     "status": "SUCCESSFUL",
        //     "createdAt": "2024-01-18T14:02:59.709Z",
        // }
        [JsonProperty("orderId")]
        public string OrderId { get; set; }
        [JsonProperty("type")]
        public string Type { get; set; }
        [JsonProperty("subType")]
        public string SubType { get; set; }
        [JsonProperty("sourceAmount")]
        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal SourceAmount { get; set; }
        [JsonProperty("sourceCurrency")]
        public string SourceCurrency { get; set; }
        [JsonProperty("destinationAmount")]
        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal DestinationAmount { get; set; }
        [JsonProperty("destinationCurrency")]
        public string DestinationCurrency { get; set; }
        [JsonProperty("destinationAddress")]
        public string DestinationAddress { get; set; }
        [JsonProperty("status")]
        public string Status { get; set; }
        [JsonProperty("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

    }
    //
    // public class GetOrderResponse
    // {
    //     public string OrderId { get; set; }
    //     public string Status { get; set; }
    //     public string SubType { get; set; }
    //
    //     [JsonConverter(typeof(NumericStringJsonConverter))]
    //     public decimal SourceAmount { get; set; }
    //
    //     public string SourceCurrency { get; set; }
    //
    //     [JsonConverter(typeof(NumericStringJsonConverter))]
    //     public decimal DestinationAmount { get; set; }
    //
    //     public string DestinationCurrency { get; set; }
    //     public BringinPrice BringinPrice { get; set; }
    // }

    public class BringinPrice
    {
        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal Price { get; set; }

        public string Currency { get; set; }
    }


    public class CreateOrderResponse
    {
        [JsonProperty("id")] public string Id { get; set; }

        [JsonProperty("amount")]
        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal Amount { get; set; }

        [JsonProperty("invoice")] public string Invoice { get; set; }
        [JsonProperty("depositAddress")] public string DepositAddress { get; set; }

        [JsonProperty("expiresAt")] public long Expiry { get; set; }
    }

    public class CreateOrderRequest
    {
        [JsonProperty("sourceAmount")]
        [JsonConverter(typeof(NumericStringJsonConverter))]
        public decimal SourceAmount { get; set; }

        [JsonProperty("ipAddress")] public string IP { get; set; }
        [JsonProperty("paymentMethod")] public string PaymentMethod { get; set; }
    }

    public class RateResponse
    {
        public string Ticker { get; set; }
        public string Currency { get; set; }


        public long Timestamp { get; set; }

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
        public string ErrorMessage { get; set; }
        public JToken ErrorDetails { get; set; }
    }
}