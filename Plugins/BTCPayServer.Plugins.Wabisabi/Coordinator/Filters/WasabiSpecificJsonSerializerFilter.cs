using System;
using System.Buffers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using WalletWasabi.WabiSabi.Models.Serialization;

namespace WalletWasabi.Backend.Filters;

public class WasabiSpecificJsonSerializerFilter: Attribute, IResultFilter{

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult objectResult)
        {
            var serializerSettings = new JsonSerializerSettings()
            {
                Converters = JsonSerializationOptions.Default.Settings.Converters
            };

            var mvoptions = new MvcOptions();

            var jsonFormatter = new NewtonsoftJsonOutputFormatter(
                serializerSettings,
                ArrayPool<char>.Shared, mvoptions, new MvcNewtonsoftJsonOptions());

            objectResult.Formatters.Add(jsonFormatter);
        }
    }

}
