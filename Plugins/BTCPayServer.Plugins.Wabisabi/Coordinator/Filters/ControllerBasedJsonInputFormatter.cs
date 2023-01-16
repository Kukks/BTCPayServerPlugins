using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;

public class ControllerBasedJsonInputFormatter : ContextAwareMultiPooledSerializerJsonInputFormatter,
    IControllerBasedJsonSerializerSettingsBuilder
{
    public ControllerBasedJsonInputFormatter(ILogger logger, JsonSerializerSettings serializerSettings, ArrayPool<char> charPool, ObjectPoolProvider objectPoolProvider, MvcOptions options, MvcNewtonsoftJsonOptions jsonOptions) : base(logger, serializerSettings, charPool, objectPoolProvider, options, jsonOptions)
    {
    }
    readonly IDictionary<object, Action<JsonSerializerSettings>> _configureSerializerSettings
        = new Dictionary<object, Action<JsonSerializerSettings>>();
    readonly HashSet<object> _beingAppliedConfigurationKeys = new HashSet<object>();
    protected override object GetSerializerPoolKey(InputFormatterContext context)
    {
        var routeValues = context.HttpContext.GetRouteData()?.Values;
        var controllerName = routeValues == null ? null : routeValues["controller"]?.ToString();
        if(controllerName != null && _configureSerializerSettings.ContainsKey(controllerName))
        {
            return controllerName;
        }
        var actionContext = CurrentAction;
        if (actionContext != null && actionContext.ActionDescriptor is ControllerActionDescriptor actionDesc)
        {
            foreach (var attr in actionDesc.MethodInfo.GetCustomAttributes(true)
                         .Concat(actionDesc.ControllerTypeInfo.GetCustomAttributes(true)))
            {
                var key = attr.GetType();
                if (_configureSerializerSettings.ContainsKey(key))
                {                        
                    return key;
                }
            }
        }
        return null;
    }
    public IControllerBasedJsonSerializerSettingsBuilder ForControllers(params string[] controllerNames)
    {
        foreach(var controllerName in controllerNames ?? Enumerable.Empty<string>())
        {                
            _beingAppliedConfigurationKeys.Add((controllerName ?? "").ToLowerInvariant());
        }            
        return this;
    }
    public IControllerBasedJsonSerializerSettingsBuilder ForControllersWithAttribute<T>()
    {
        _beingAppliedConfigurationKeys.Add(typeof(T));
        return this;
    }
    public IControllerBasedJsonSerializerSettingsBuilder ForActionsWithAttribute<T>()
    {
        _beingAppliedConfigurationKeys.Add(typeof(T));
        return this;
    }
    ControllerBasedJsonInputFormatter IControllerBasedJsonSerializerSettingsBuilder.WithSerializerSettingsConfigurer(Action<JsonSerializerSettings> configurer)
    {
        if (configurer == null) throw new ArgumentNullException(nameof(configurer));
        foreach(var key in _beingAppliedConfigurationKeys)
        {
            _configureSerializerSettings[key] = configurer;
        }
        _beingAppliedConfigurationKeys.Clear();
        return this;
    }
    protected override void ConfigureSerializerSettings(JsonSerializerSettings serializerSettings, object poolKey, InputFormatterContext context)
    {            
        if (_configureSerializerSettings.TryGetValue(poolKey, out var configurer))
        {
            configurer.Invoke(serializerSettings);
        }
    }
}