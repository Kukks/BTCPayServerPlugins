dotnet publish -c Altcoins-Release -o bin/publish/BTCPayServer.Plugins.FujiOracle
dotnet run -p ../../BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.FujiOracle BTCPayServer.Plugins.FujiOracle ../packed
