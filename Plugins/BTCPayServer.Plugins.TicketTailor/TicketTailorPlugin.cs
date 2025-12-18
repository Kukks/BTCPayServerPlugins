using System.Collections.Generic;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.Emails.Views;
using BTCPayServer.Plugins.Webhooks.TriggerProviders;
using BTCPayServer.Services.Apps;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.TicketTailor
{
    public class TicketTailorPlugin : BaseBTCPayServerPlugin
    {
        public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
        {
            new() {Identifier = nameof(BTCPayServer), Condition = ">=2.3.0"}
        };

        public override void Execute(IServiceCollection applicationBuilder)
        {
            applicationBuilder.AddStartupTask<AppMigrate>();
            applicationBuilder.AddWebhookTriggerProvider<TicketTailorWebhookProvider>();

            applicationBuilder.AddUIExtension("header-nav", "TicketTailor/NavExtension");
            applicationBuilder.AddSingleton<AppBaseType, TicketTailorApp>();


            var placeHolders = new List<EmailTriggerViewModel.PlaceHolder>()
            {
                new("{TicketTailor.ReceiptUrl}", "Ticket Tailor receipt URL")
            };
            placeHolders.AddRange(InvoiceTriggerProvider.GetInvoicePlaceholders());
            
            applicationBuilder.AddWebhookTriggerViewModels(new List<EmailTriggerViewModel>()
            {
                new()
                {
                    Trigger = TicketTailorWebhookProvider.TicketTailorTicketIssued,
                    Description = "Ticket Tailor - A ticket has been issued through ticket tailor",
                    DefaultEmail = new()
                    {
                        To = ["{Invoice.Buyer.MailboxAddress}"],
                        Subject = "Your ticket is available now.",
                        Body = "Your payment has been settled and the event ticket has been issued successfully. Please go to <a href='{TicketTailor.ReceiptUrl}'>{TicketTailor.ReceiptUrl}</a>"
                    },
                    PlaceHolders = placeHolders
                }
            });
            
            base.Execute(applicationBuilder);
        }
    }
}