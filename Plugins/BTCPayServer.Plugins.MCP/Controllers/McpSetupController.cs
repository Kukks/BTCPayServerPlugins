using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Security.Greenfield;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.MCP.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/mcp-setup")]
public class McpSetupController : Controller
{
    private const string AppId = "btcpay-mcp-plugin";
    private readonly APIKeyRepository _apiKeyRepository;
    private readonly UserManager<ApplicationUser> _userManager;

    public McpSetupController(APIKeyRepository apiKeyRepository, UserManager<ApplicationUser> userManager)
    {
        _apiKeyRepository = apiKeyRepository;
        _userManager = userManager;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        ViewData["McpEndpointUrl"] = $"{baseUrl}/plugins/mcp";

        var mcpKey = await FindMcpKey();
        ViewData["McpApiKey"] = mcpKey?.Id;

        return View();
    }

    [HttpPost("create-key")]
    public async Task<IActionResult> CreateKey()
    {
        var userId = _userManager.GetUserId(User);
        var existing = await FindMcpKey();
        if (existing != null)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Info,
                Message = "An MCP API key already exists."
            });
            return RedirectToAction(nameof(Index));
        }

        var key = new APIKeyData
        {
            Id = Encoders.Hex.EncodeData(RandomUtils.GetBytes(20)),
            Type = APIKeyType.Permanent,
            UserId = userId,
            Label = "MCP",
        };
        key.SetBlob(new APIKeyBlob
        {
            Permissions = new[] { Policies.Unrestricted },
            ApplicationIdentifier = AppId
        });
        await _apiKeyRepository.CreateKey(key);

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Success,
            Html = $"API key created! <code class='alert-link'>{key.Id}</code>"
        });
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("revoke-key")]
    public async Task<IActionResult> RevokeKey(string keyId)
    {
        var userId = _userManager.GetUserId(User);
        if (!string.IsNullOrEmpty(keyId) && await _apiKeyRepository.Remove(keyId, userId))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Severity = StatusMessageModel.StatusSeverity.Success,
                Message = "MCP API key revoked."
            });
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task<APIKeyData?> FindMcpKey()
    {
        var userId = _userManager.GetUserId(User);
        var keys = await _apiKeyRepository.GetKeys(new APIKeyRepository.APIKeyQuery
        {
            UserId = new[] { userId }
        });
        return keys.FirstOrDefault(k => k.GetBlob()?.ApplicationIdentifier == AppId);
    }
}
