using System.Text.Json;

var plugins = Directory.GetDirectories("../../../../Plugins");
Console.WriteLine(string.Join(',',plugins));
var p = string.Join(';', plugins.Select(s => $"{Path.GetFullPath(s)}/bin/Altcoins-Debug/net6.0/{Path.GetFileName(s)}.dll" ));;
var fileContents = $"{{ \"DEBUG_PLUGINS\": \"{p}\"}}";
var content = JsonSerializer.Serialize(new
{
    DEBUG_PLUGINS = p
});

await File.WriteAllTextAsync("../../../../submodules/BTCPayServer/BTCPayServer/appsettings.dev.json", content);