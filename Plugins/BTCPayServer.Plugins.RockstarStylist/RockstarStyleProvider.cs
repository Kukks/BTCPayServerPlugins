using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.RockstarStylist
{
    public class RockstarStyleProvider
    {
        private HttpClient _githubClient;

        public RockstarStyleProvider(IHttpClientFactory httpClientFactory)
        {
            _githubClient = httpClientFactory.CreateClient();
            _githubClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("btcpayserver", "1"));
        }

        public async Task<RockstarStyle[]> Get()
        {
            var response = JArray.Parse(await _githubClient.GetStringAsync("https://api.github.com/repos/btcpayserver/BTCPayThemes/contents")); 
            return response.Where(token => token.Value<string>("type") == "dir").Select(token => new RockstarStyle()
            {
                StyleName = token.Value<string>("name"),
                CssUrl = $"https://btcpayserver.github.io/BTCPayThemes/{token.Value<string>("name")}/btcpay-checkout.custom.css",
                PreviewUrl = $"https://btcpayserver.github.io/BTCPayThemes/{token.Value<string>("name")}"
            }).ToArray();
        }
    }

    public class RockstarStyle
    {
        public string StyleName { get; set; }
        public string CssUrl { get; set; }
        public string PreviewUrl { get; set; }
    }
}
