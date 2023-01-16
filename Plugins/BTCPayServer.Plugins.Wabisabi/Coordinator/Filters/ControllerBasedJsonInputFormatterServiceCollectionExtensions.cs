using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class ControllerBasedJsonInputFormatterServiceCollectionExtensions
{
    public static IServiceCollection AddControllerBasedJsonInputFormatter(this IServiceCollection services,
        Action<ControllerBasedJsonInputFormatter> configureFormatter)
    {
        if(configureFormatter == null)
        {
            throw new ArgumentNullException(nameof(configureFormatter));
        }
        services.TryAddSingleton<IActionContextAccessor, ActionContextAccessor>();
        return services.ConfigureOptions<ControllerBasedJsonInputFormatterMvcOptionsSetup>()
            .PostConfigure<MvcOptions>(o => {
                var jsonInputFormatter = o.InputFormatters.OfType<ControllerBasedJsonInputFormatter>().FirstOrDefault();
                if(jsonInputFormatter != null)
                {
                    configureFormatter(jsonInputFormatter);
                }
            });
    }
}