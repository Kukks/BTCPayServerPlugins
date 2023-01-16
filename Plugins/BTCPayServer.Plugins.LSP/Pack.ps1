dotnet publish -c Altcoins-Release -o bin/publish/BTCPayServer.Plugins.LSP
dotnet run -p ../../BTCPayServer.PluginPacker bin/publish/BTCPayServer.Plugins.LSP BTCPayServer.Plugins.LSP ../packed
