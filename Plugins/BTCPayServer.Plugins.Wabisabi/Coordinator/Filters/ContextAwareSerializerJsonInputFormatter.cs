using System.Buffers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;

public abstract class ContextAwareSerializerJsonInputFormatter : NewtonsoftJsonInputFormatter 
{        
    public ContextAwareSerializerJsonInputFormatter(ILogger logger, 
        JsonSerializerSettings serializerSettings, 
        ArrayPool<char> charPool, ObjectPoolProvider objectPoolProvider, MvcOptions options, MvcNewtonsoftJsonOptions jsonOptions) : base(logger, serializerSettings, charPool, objectPoolProvider, options, jsonOptions)
    {
        PoolProvider = objectPoolProvider;
    }
    readonly AsyncLocal<InputFormatterContext> _currentContextAsyncLocal = new AsyncLocal<InputFormatterContext>();
    readonly AsyncLocal<ActionContext> _currentActionAsyncLocal = new AsyncLocal<ActionContext>();
    protected InputFormatterContext CurrentContext => _currentContextAsyncLocal.Value;
    protected ActionContext CurrentAction => _currentActionAsyncLocal.Value;
    protected ObjectPoolProvider PoolProvider { get; }
    public override Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
    {
        _currentContextAsyncLocal.Value = context;
        _currentActionAsyncLocal.Value = context.HttpContext.RequestServices.GetRequiredService<IActionContextAccessor>().ActionContext;
        return base.ReadRequestBodyAsync(context, encoding); 
    }
    public override Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context)
    {
        _currentContextAsyncLocal.Value = context;
        _currentActionAsyncLocal.Value = context.HttpContext.RequestServices.GetRequiredService<IActionContextAccessor>().ActionContext;
        return base.ReadRequestBodyAsync(context);
    }
    protected virtual JsonSerializer CreateJsonSerializer(InputFormatterContext context) => null;
    protected override JsonSerializer CreateJsonSerializer()
    {
        var context = CurrentContext;
        return (context == null ? null : CreateJsonSerializer(context)) ?? base.CreateJsonSerializer();
    }
}