using System.IO;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.Webhooks;
using BTCPayServer.Plugins.Webhooks.Views;
using BTCPayServer.Services.Apps;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Subscriptions
{
    public class SubscriptionPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        [
            new() {Identifier = nameof(BTCPayServer), Condition = ">=2.3.7"}
        ];

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddSingleton<ISwaggerProvider, SubscriptionsSwaggerProvider>();
            applicationBuilder.AddSingleton<SubscriptionService>();
            applicationBuilder.AddHostedService(s => s.GetRequiredService<SubscriptionService>());
            applicationBuilder.AddWebhookTriggerProvider<SubscriptionWebhookTriggerProvider>();
            applicationBuilder.AddSingleton(new AvailableWebhookViewModel(SubscriptionService.SubscriptionStatusUpdated, "A subscription status has been updated"));
            applicationBuilder.AddSingleton(new AvailableWebhookViewModel(SubscriptionService.SubscriptionRenewalRequested, "A subscription has generated a payment request for renewal"));

            applicationBuilder.AddUIExtension("header-nav", "Subscriptions/NavExtension");
            applicationBuilder.AddSingleton<AppBaseType, SubscriptionApp>();
            base.Execute(applicationBuilder);
        }
    }

    public class SubscriptionsSwaggerProvider : ISwaggerProvider
    {
        private readonly IFileProvider _fileProvider;

        public SubscriptionsSwaggerProvider(IWebHostEnvironment webHostEnvironment)
        {
            _fileProvider = webHostEnvironment.WebRootFileProvider;
        }

        public async Task<JObject> Fetch()
        {
            var file = _fileProvider.GetFileInfo("Resources/swagger.subscriptions.json");
            using var reader = new StreamReader(file.CreateReadStream());
            return JObject.Parse(await reader.ReadToEndAsync());
        }
    }
}