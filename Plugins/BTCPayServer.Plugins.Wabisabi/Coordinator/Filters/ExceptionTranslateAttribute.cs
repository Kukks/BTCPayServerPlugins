using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NLog;
using WabiSabi.Crypto;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend.Models;
using WalletWasabi.WabiSabi.Models;
using WalletWasabi.WabiSabi.Models.Serialization;

namespace WalletWasabi.Backend.Filters;

public class ExceptionTranslateAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        var serializerSettings = new JsonSerializerSettings()
        {
            Converters = JsonSerializationOptions.Default.Settings.Converters
        };
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<WabiSabiCoordinator>>();
        var exception = context.Exception.InnerException ?? context.Exception;
        logger.LogError(exception, "Exception occured in WabiSabiCoordinator  API, ");
        context.Result = exception switch
        {
            WabiSabiProtocolException e => new JsonResult(new Error(
                Type: ProtocolConstants.ProtocolViolationType,
                ErrorCode: e.ErrorCode.ToString(),
                Description: e.Message,
                ExceptionData: e.ExceptionData ?? EmptyExceptionData.Instance), serializerSettings )
            {
                StatusCode = (int) HttpStatusCode.InternalServerError
            },
            WabiSabiCryptoException e => new JsonResult(new Error(
                Type: ProtocolConstants.ProtocolViolationType,
                ErrorCode: WabiSabiProtocolErrorCode.CryptoException.ToString(),
                Description: e.Message,
                ExceptionData: EmptyExceptionData.Instance), serializerSettings)
            {
                StatusCode = (int) HttpStatusCode.InternalServerError
            },
            _ => new StatusCodeResult((int) HttpStatusCode.InternalServerError)
        };
    }
}