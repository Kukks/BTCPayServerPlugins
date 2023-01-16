using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;

public class JsonSerializerPooledPolicy : IPooledObjectPolicy<JsonSerializer>
{
    private readonly JsonSerializerSettings _serializerSettings;
    
    public JsonSerializerPooledPolicy(JsonSerializerSettings serializerSettings)
    {
        _serializerSettings = serializerSettings;
    }

    public JsonSerializer Create() => JsonSerializer.Create(_serializerSettings);
    
    public bool Return(JsonSerializer serializer) => true;
}