using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WalletWasabi.Logging;
using WalletWasabi.WabiSabi;
using WalletWasabi.WabiSabi.Backend.Models;

namespace WalletWasabi.Backend.Filters;

public class LateResponseLoggerFilter : ExceptionFilterAttribute
{
	public override void OnException(ExceptionContext context)
	{
		var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<WabiSabiCoordinator>>();
		if (context.Exception is not WrongPhaseException ex)
		{
			return;
		}

		var actionName = ((Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor)context.ActionDescriptor).ActionName;

		logger.LogInformation($"Request '{actionName}' missing the phase '{string.Join(",", ex.ExpectedPhases)}' ('{ex.PhaseTimeout}' timeout) by '{ex.Late}'. Round id '{ex.RoundId}'.");
	}
}
