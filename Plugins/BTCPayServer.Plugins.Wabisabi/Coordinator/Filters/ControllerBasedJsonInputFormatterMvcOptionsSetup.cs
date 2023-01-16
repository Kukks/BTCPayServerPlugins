
using System;
using System.Buffers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

public class ControllerBasedJsonInputFormatterMvcOptionsSetup : IConfigureOptions<MvcOptions>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly MvcNewtonsoftJsonOptions _jsonOptions;
    private readonly ArrayPool<char> _charPool;
    private readonly ObjectPoolProvider _objectPoolProvider;
    public ControllerBasedJsonInputFormatterMvcOptionsSetup(
        ILoggerFactory loggerFactory,
        IOptions<MvcNewtonsoftJsonOptions> jsonOptions,
        ArrayPool<char> charPool,
        ObjectPoolProvider objectPoolProvider)
    {
        if (loggerFactory == null)
        {
            throw new ArgumentNullException(nameof(loggerFactory));
        }

        if (jsonOptions == null)
        {
            throw new ArgumentNullException(nameof(jsonOptions));
        }

        if (charPool == null)
        {
            throw new ArgumentNullException(nameof(charPool));
        }

        if (objectPoolProvider == null)
        {
            throw new ArgumentNullException(nameof(objectPoolProvider));
        }

        _loggerFactory = loggerFactory;
        _jsonOptions = jsonOptions.Value;
        _charPool = charPool;
        _objectPoolProvider = objectPoolProvider;
    }
    public void Configure(MvcOptions options)
    {
        //remove the default
        options.InputFormatters.RemoveType<NewtonsoftJsonInputFormatter>();
        //add our own
        var jsonInputLogger = _loggerFactory.CreateLogger<ControllerBasedJsonInputFormatter>();

        options.InputFormatters.Add(new ControllerBasedJsonInputFormatter(
            jsonInputLogger,
            _jsonOptions.SerializerSettings,
            _charPool,
            _objectPoolProvider,
            options,
            _jsonOptions));
    }
}

//there is a similar class like this implemented by the framework 
//but it's a pity that it's internal
//So we define our own class here (which is exactly the same from the source code)
//It's quite simple like this

//This attribute is used as a marker to decorate any controllers 
//or actions that you want to apply your custom input formatter