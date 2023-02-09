using System;
using System.IO;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Configuration;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin.DataEncoders;
using WalletWasabi.Affiliation.Models;
using WalletWasabi.Affiliation.Models.CoinjoinRequest;

namespace BTCPayServer.Plugins.Wabisabi.AffiliateServer;

public class WabisabiAffiliateSettings
{
    public bool Enabled { get; set; }
    public string SigningKey { get; set; }
    
    
}
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Route("plugins/wabisabi-affiliate")]
public class AffiliateServerController:Controller
{
    private readonly SettingsRepository _settingsRepository;
    private readonly IOptions<DataDirectories> _dataDirectories;
    private readonly ILogger<AffiliateServerController> _logger;

    public AffiliateServerController(SettingsRepository settingsRepository, IOptions<DataDirectories> dataDirectories, ILogger<AffiliateServerController> logger)
    {
        _settingsRepository = settingsRepository;
        _dataDirectories = dataDirectories;
        _logger = logger;
    }

    [HttpGet("edit")]
    public async Task<IActionResult> Edit()
    {
        var settings = 
            await _settingsRepository.GetSettingAsync<WabisabiAffiliateSettings>();
        return View(settings);
    }
    [HttpPost("edit")]
    public async Task<IActionResult> Edit(WabisabiAffiliateSettings settings)
    {
        await _settingsRepository.UpdateSetting(settings);
        return RedirectToAction("Edit");
    }
    [HttpGet("history")]
    public async Task<IActionResult> ViewRequests()
    {
        var path = Path.Combine(_dataDirectories.Value.DataDir, "Plugins", "CoinjoinAffiliate", "History.txt");
        if (!System.IO.File.Exists(path))
            return NotFound();

        return File(path,  MediaTypeNames.Text.Plain);
    }
    

    [AllowAnonymous]
    [HttpPost("get_status")]
    public async Task<IActionResult> GetStatus()
    {
        
        var settings = 
            await _settingsRepository.GetSettingAsync<WabisabiAffiliateSettings>();
        if(settings?.Enabled is true&& !string.IsNullOrEmpty(settings.SigningKey))
            return Ok(new { });
        return NotFound();

    }

    private static ECDsa ecdsa = ECDsa.Create();
    
    
    [AllowAnonymous]
    [HttpPost("get_coinjoin_request")]
    public async Task<IActionResult> GetCoinjoinRequest([FromBody] GetCoinjoinRequestRequest request)
    {
        var settings = await _settingsRepository.GetSettingAsync<WabisabiAffiliateSettings>();
        if (settings?.Enabled is not true)
        {
            return NotFound();
        }

        var keyB = Encoders.Hex.DecodeData(settings.SigningKey);
        ecdsa.ImportSubjectPublicKeyInfo(keyB.AsSpan(), out _);
        
        Payload payload = new(new Header(), request.Body);
        try
        {

        var valid = ecdsa.VerifyData(payload.GetCanonicalSerialization(), request.Signature, HashAlgorithmName.SHA256);
        if(!valid)
            return NotFound();
        
        var path = Path.Combine(_dataDirectories.Value.DataDir, "Plugins", "CoinjoinAffiliate", "History.txt");
        string rawBody;
        using (StreamReader reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            rawBody = (await reader.ReadToEndAsync());
        }

        await System.IO.File.AppendAllLinesAsync(path, new[] {rawBody.Replace(Environment.NewLine, "")}, Encoding.UTF8);
        return Ok(new GetCoinjoinRequestResponse(Array.Empty<byte>()));
        
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed on GetCoinjoinRequest" );
            return NotFound();

        }
        
    }
    
    
}