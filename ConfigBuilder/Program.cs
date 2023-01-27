using System.Text.Json;

var plugins = Directory.GetDirectories("../../../../Plugins");
var p = "";
foreach (var plugin in plugins)
{
    var x = Directory.GetDirectories(Path.Combine(plugin,"bin"));
    if (x.Any(s => s.EndsWith("Altcoins-Debug")))
    {
        p += $"{Path.GetFullPath(plugin)}/bin/Altcoins-Debug/net6.0/{Path.GetFileName(plugin)}.dll;";
    }
    else
    {
        p += $"{Path.GetFullPath(plugin)}/bin/Debug/net6.0/{Path.GetFileName(plugin)}.dll;";
    }
}

var content = JsonSerializer.Serialize(new
{
    DEBUG_PLUGINS = p
});

Console.WriteLine(content);
await File.WriteAllTextAsync("../../../../submodules/BTCPayServer/BTCPayServer/appsettings.dev.json", content);