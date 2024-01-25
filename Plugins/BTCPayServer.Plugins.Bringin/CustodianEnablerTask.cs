using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.Bringin;

public class CustodianEnablerTask: IStartupTask
{
    private readonly SettingsRepository _settingsRepository;

    public CustodianEnablerTask(SettingsRepository settingsRepository)
    {
        _settingsRepository = settingsRepository;
    }
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var policySettings = await _settingsRepository.GetSettingAsync<PoliciesSettings>() ?? new PoliciesSettings();
        if(policySettings.Experimental)
            return;
        policySettings.Experimental = true;
        await _settingsRepository.UpdateSetting(policySettings);
    }
}