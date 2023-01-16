using System;
using System.IO;
using System.Threading.Tasks;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.LiquidPlus.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.LiquidPlus.Services
{
    public class CustomLiquidAssetsRepository
    {
        private readonly ILogger<CustomLiquidAssetsRepository> _logger;
        private readonly IOptions<DataDirectories> _options;
        private string File => Path.Combine(_options.Value.DataDir, "custom-liquid-assets.json");

        public CustomLiquidAssetsRepository(ILogger<CustomLiquidAssetsRepository> logger, IOptions<DataDirectories> options)
        {
            _logger = logger;
            _options = options;
        }

        public CustomLiquidAssetsSettings Get()
        {
            try
            {
                if (System.IO.File.Exists(File))
                {
                    return JObject.Parse(System.IO.File.ReadAllText(File)).ToObject<CustomLiquidAssetsSettings>();
                }
            }

            catch (Exception e)
            {
                _logger.LogError(e, "could not parse custom liquid assets file");
            }

            return new CustomLiquidAssetsSettings();
        }

        public async Task Set(CustomLiquidAssetsSettings settings)
        {
            try
            {
                await System.IO.File.WriteAllTextAsync(File, JObject.FromObject(settings).ToString(Formatting.Indented));

                ChangesPending = true;
            }

            catch (Exception e)
            {
                _logger.LogError(e, "could not write custom liquid assets file");
            }
        }

        public bool ChangesPending { get; private set; }
    }
}
