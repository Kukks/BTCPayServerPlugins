using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Form;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Forms;
using BTCPayServer.Plugins.FileSeller;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.BitcoinSwitch;

public class BitcoinSwitchPlugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() {Identifier = nameof(BTCPayServer), Condition = ">=2.0.0"}
    };

    public override void Execute(IServiceCollection applicationBuilder)
    {
        applicationBuilder.AddSingleton<BitcoinSwitchService>();
        applicationBuilder.AddHostedService<BitcoinSwitchService>(provider => provider.GetRequiredService<BitcoinSwitchService>());
        applicationBuilder.AddUIExtension("app-template-editor-item-detail", "BitcoinSwitch/BitcoinSwitchPluginTemplateEditorItemDetail");
        var regsitration = applicationBuilder.Single(descriptor =>
            descriptor.ServiceType == typeof(IFormComponentProvider) &&
            descriptor.ImplementationType == typeof(HtmlInputFormProvider));
        applicationBuilder.Remove(regsitration);
        applicationBuilder.AddSingleton<IFormComponentProvider, HtmlInput2FormProvider>();
        base.Execute(applicationBuilder);
    }
}

public class HtmlInput2FormProvider : HtmlInputFormProvider
{
    
    public override void Validate(Form form, Field field)
    {
        base.Validate(form, field);

        if (field.ValidationErrors.Count != 0)
        {
            return;
        }

        if (field.AdditionalData.TryGetValue("regex", out var regex) &&  regex.ToString() is {} regexStr&&
            !System.Text.RegularExpressions.Regex.IsMatch(GetValue(form, field), regexStr))
        {
            var regexErrorMessage = field.AdditionalData.TryGetValue("regex-error-message", out var regexError)
                ? regexError.ToString()
                : "The value is not valid";
           
            field.ValidationErrors.Add(regexErrorMessage);
        }
    }
}