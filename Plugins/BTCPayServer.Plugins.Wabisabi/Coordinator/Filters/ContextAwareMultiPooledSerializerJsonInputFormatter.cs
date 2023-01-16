using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;

public abstract class ContextAwareMultiPooledSerializerJsonInputFormatter : ContextAwareSerializerJsonInputFormatter
{
    public ContextAwareMultiPooledSerializerJsonInputFormatter(ILogger logger, JsonSerializerSettings serializerSettings, ArrayPool<char> charPool, ObjectPoolProvider objectPoolProvider, MvcOptions options, MvcNewtonsoftJsonOptions jsonOptions) 
        : base(logger, serializerSettings, charPool, objectPoolProvider, options, jsonOptions)
    {
        
    }
    readonly IDictionary<object, ObjectPool<JsonSerializer>> _serializerPools = new ConcurrentDictionary<object, ObjectPool<JsonSerializer>>();
    readonly AsyncLocal<object> _currentPoolKeyAsyncLocal = new AsyncLocal<object>();
    protected object CurrentPoolKey => _currentPoolKeyAsyncLocal.Value;
    protected abstract object GetSerializerPoolKey(InputFormatterContext context);
    protected override JsonSerializer CreateJsonSerializer(InputFormatterContext context)
    {
        object poolKey = GetSerializerPoolKey(context) ?? "";
        if(!_serializerPools.TryGetValue(poolKey, out var pool))
        {
            //clone the settings
            var serializerSettings = new JsonSerializerSettings();
            foreach(var prop in typeof(JsonSerializerSettings).GetProperties().Where(e => e.CanWrite))
            {
                prop.SetValue(serializerSettings, prop.GetValue(SerializerSettings));
            }
            ConfigureSerializerSettings(serializerSettings, poolKey, context);
            pool = PoolProvider.Create(new JsonSerializerPooledPolicy(serializerSettings));
            _serializerPools[poolKey] = pool;
        }
        _currentPoolKeyAsyncLocal.Value = poolKey;
        return pool.Get();
    }
    protected override void ReleaseJsonSerializer(JsonSerializer serializer)
    {            
        if(_serializerPools.TryGetValue(CurrentPoolKey ?? "", out var pool))
        {
            pool.Return(serializer);
        }         
    }
    protected virtual void ConfigureSerializerSettings(JsonSerializerSettings serializerSettings, object poolKey, InputFormatterContext context) { }
}