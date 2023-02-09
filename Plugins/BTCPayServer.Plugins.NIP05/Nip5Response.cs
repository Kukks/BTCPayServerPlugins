using System.Collections.Generic;
using Newtonsoft.Json;

public class Nip5Response
{
    [JsonProperty("names")] public Dictionary<string, string> Names { get; set; }

    [JsonProperty("relays", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, string[]>? Relays { get; set; }
}