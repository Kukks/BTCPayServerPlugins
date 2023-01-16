using System;
using Newtonsoft.Json;

public interface IControllerBasedJsonSerializerSettingsBuilder
{
    ControllerBasedJsonInputFormatter WithSerializerSettingsConfigurer(Action<JsonSerializerSettings> configurer);
    IControllerBasedJsonSerializerSettingsBuilder ForControllers(params string[] controllerNames);
    IControllerBasedJsonSerializerSettingsBuilder ForControllersWithAttribute<T>();
    IControllerBasedJsonSerializerSettingsBuilder ForActionsWithAttribute<T>();
}