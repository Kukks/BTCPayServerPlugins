using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BTCPayServer.Services;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Newtonsoft.Json;
using NLog;
using WalletWasabi.WabiSabi.Client.Banning;
using ILogger = Microsoft.Extensions.Logging.ILogger;
using Logger = WalletWasabi.Logging.Logger;

namespace BTCPayServer.Plugins.Wabisabi;

public class SettingsCoinPrison : CoinPrison
{
    private readonly SettingsRepository _settingsRepository;
    private readonly string _coordinatorName;

    public SettingsCoinPrison(SettingsRepository settingsRepository, string coordinatorName,
        Dictionary<OutPoint, PrisonedCoinRecord> bannedCoins) : base(null, bannedCoins)
    {
        _settingsRepository = settingsRepository;
        _coordinatorName = coordinatorName;
    }

    protected override void ToFile()
    {
        var json = JsonConvert.SerializeObject(_bannedCoins, Formatting.Indented);
        _settingsRepository.UpdateSetting(json, "wabisabi_" + _coordinatorName + "_bannedcoins").GetAwaiter().GetResult();
    }

    public static async Task<SettingsCoinPrison> CreateFromCoordinatorName(SettingsRepository settingsRepository,
        string coordinatorName, ILogger logger)
    {
        Dictionary<OutPoint, PrisonedCoinRecord> prisonedCoinRecords = new();
        try
        {
            var data = await settingsRepository.GetSettingAsync<string>("wabisabi_" + coordinatorName + "_bannedcoins");
            if (string.IsNullOrWhiteSpace(data))
            {
                logger.LogDebug("Prisoned coins file is empty.");
                return new(settingsRepository, coordinatorName, new());
            }
            prisonedCoinRecords = JsonConvert.DeserializeObject<Dictionary<OutPoint, PrisonedCoinRecord>>(data)
                                  ?? throw new InvalidDataException("Prisoned coins file is corrupted.");
        }
        catch (Exception exc)
        {
            logger.LogError($"There was an error during loading {nameof(SettingsCoinPrison)}. Ignoring corrupt data.", exc);
        }

        return new(settingsRepository, coordinatorName, prisonedCoinRecords);
    }
}