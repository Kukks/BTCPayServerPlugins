using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Payouts;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Newtonsoft.Json.Linq;
using AuthenticationSchemes = BTCPayServer.Abstractions.Constants.AuthenticationSchemes;

namespace BTCPayServer.Plugins.SideShift
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/SideShift")]
    public class SideShiftController : Controller
    {
        private readonly SideShiftService _sideShiftService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly PayoutMethodHandlerDictionary _payoutMethodHandlerDictionary;
        private readonly PullPaymentHostedService _pullPaymentHostedService;
        private readonly BTCPayNetworkJsonSerializerSettings _serializerSettings;
        private readonly ApplicationDbContextFactory _dbContextFactory;

        public SideShiftController(
            SideShiftService sideShiftService,
            IHttpClientFactory httpClientFactory,
           PayoutMethodHandlerDictionary payoutMethodHandlerDictionary,
            PullPaymentHostedService pullPaymentHostedService,
            BTCPayNetworkJsonSerializerSettings serializerSettings, ApplicationDbContextFactory dbContextFactory)
        {
            _sideShiftService = sideShiftService;
            _httpClientFactory = httpClientFactory;
            _payoutMethodHandlerDictionary = payoutMethodHandlerDictionary;
            _pullPaymentHostedService = pullPaymentHostedService;
            _serializerSettings = serializerSettings;
            _dbContextFactory = dbContextFactory;
        }

        [HttpGet("")]
        public async Task<IActionResult> UpdateSideShiftSettings(string storeId)
        {
            SideShiftSettings SideShift = null;
            try
            {
                SideShift = await _sideShiftService.GetSideShiftForStore(storeId);
            }
            catch (Exception)
            {
                // ignored
            }

            return View(SideShift??new SideShiftSettings());
        }


        [HttpPost("")]
        public async Task<IActionResult> UpdateSideShiftSettings(string storeId, SideShiftSettings vm,
            string command)
        {
            if (vm.Enabled)
            {
                if (!ModelState.IsValid)
                {
                    return View(vm);
                }
            }


            switch (command)
            {
                case "save":
                    await _sideShiftService.SetSideShiftForStore(storeId, vm);
                    TempData["SuccessMessage"] = "SideShift settings modified";
                    return RedirectToAction(nameof(UpdateSideShiftSettings), new {storeId});

                default:
                    return View(vm);
            }
        }
        
        

        [HttpPost("~/plugins/sidehift/{pullPaymentId}/payouts")]
        [AllowAnonymous]
        public async Task<IActionResult> CreatePayout(string pullPaymentId,
            [FromBody] CreateSideShiftPayoutRequest request)
        {
            IPayoutHandler handler = null;
            if (string.IsNullOrEmpty(request.ShiftCurrency))
            {
                ModelState.AddModelError(nameof(request.ShiftCurrency), "ShiftCurrency must be specified");
            }

            if (string.IsNullOrEmpty(request.ShiftNetwork))
            {
                ModelState.AddModelError(nameof(request.ShiftNetwork), "ShiftNetwork must be specified");
            }

            if (request.Amount is null)
            {
                ModelState.AddModelError(nameof(request.Amount), "Amount must be specified");
            }

            if (!PayoutMethodId.TryParse(request.PayoutMethodId, out var pmi))
            {
                ModelState.AddModelError(nameof(request.PayoutMethodId), "Invalid payout method");
            }
            else
            {
                if (!_payoutMethodHandlerDictionary.TryGetValue(pmi, out handler))
                {
                    ModelState.AddModelError(nameof(request.PayoutMethodId), "Invalid payment method");
                }
            }
            var isLN = pmi.ToString().EndsWith("-" +PayoutTypes.LN.Id);
            if (isLN)
            {
                
                ModelState.AddModelError(nameof(request.PayoutMethodId), "SideShift does not support Lightning payouts");
            }
            if (!ModelState.IsValid)
            {
                return this.CreateValidationError(ModelState);
            }

            var pp = await
                _pullPaymentHostedService.GetPullPayment(pullPaymentId, false);
            var ppBlob = pp?.GetBlob();
            if (ppBlob is null)
            {
                return NotFound();
            }

            var ip = HttpContext.Connection.RemoteIpAddress;

            var client = _httpClientFactory.CreateClient("sideshift");
            if (ip is not null && !ip.IsLocal())
                client.DefaultRequestHeaders.Add("x-user-ip", ip.ToString());
            //
            // var quoteResponse = await client.PostAsJsonAsync("https://sideshift.ai/api/v2/quotes", new
            //     {
            //         depositCoin = pmi.CryptoCode,
            //         depositNetwork = pmi.PaymentType == LightningPaymentType.Instance ? "lightning" : null,
            //         settleCoin = request.ShiftCurrency,
            //         settleNetwork = request.ShiftNetwork,
            //         depositAmount = request.Amount.ToString(),
            //         affiliateId = "qg0OrfHJV"
            //     }
            // );
            // quoteResponse.EnsureSuccessStatusCode();
            // var quote = await quoteResponse.Content.ReadAsAsync<QuoteResponse>();
            // var shiftResponse = await client.PostAsJsonAsync("https://sideshift.ai/api/v2/shifts/fixed", new
            //     {
            //         settleAddress = request.Destination,
            //         settleMemo = request.Memo,
            //         quoteId = quote.id,
            //         affiliateId = "qg0OrfHJV"
            //     }
            // );
            // shiftResponse.EnsureSuccessStatusCode();
            // var shift = await shiftResponse.Content.ReadAsAsync<ShiftResponse>();

           var cryptoCode = pmi.ToString().Split('-')[0];
            var shiftResponse = await client.PostAsJsonAsync("https://sideshift.ai/api/v2/shifts/variable", new
                {
                    settleAddress = request.Destination,
                    affiliateId = "qg0OrfHJV",
                    settleMemo = request.Memo,
                    depositCoin = cryptoCode,
                    settleCoin = request.ShiftCurrency,
                    settleNetwork = request.ShiftNetwork,
                }
            );
            if (!shiftResponse.IsSuccessStatusCode)
            {
                var error = JObject.Parse(await shiftResponse.Content.ReadAsStringAsync());
                ModelState.AddModelError("",error["error"]["message"].Value<string>());
                
                return this.CreateValidationError(ModelState);
            }
            var shift = await shiftResponse.Content.ReadAsAsync<ShiftResponse>();

            
            var destination =
                await handler.ParseAndValidateClaimDestination(shift.depositAddress, ppBlob,
                    CancellationToken.None);

            var claim = await _pullPaymentHostedService.Claim(new ClaimRequest()
            {
                PullPaymentId = pullPaymentId,
                Destination = destination.destination,
                PayoutMethodId = pmi,
                ClaimedAmount = request.Amount
            });
            if (claim.Result == ClaimRequest.ClaimResult.Ok)
            {
                await using var ctx = _dbContextFactory.CreateContext();
                ppBlob.Description += $"<br/>The payout of {destination.destination} will be forwarded to SideShift.ai for further conversion. Please go to <a href=\"https://sideshift.ai/orders/{shift.id}?openSupport=true\">the order page</a> for support.";
                pp.SetBlob(ppBlob);
                ctx.Attach(pp).State = EntityState.Modified;
                await ctx.SaveChangesAsync();

            }
            return HandleClaimResult(claim);
        }


        private IActionResult HandleClaimResult(ClaimRequest.ClaimResponse result)
        {
            switch (result.Result)
            {
                case ClaimRequest.ClaimResult.Ok:
                    break;
                case ClaimRequest.ClaimResult.Duplicate:
                    return this.CreateAPIError("duplicate-destination", ClaimRequest.GetErrorMessage(result.Result));
                case ClaimRequest.ClaimResult.Expired:
                    return this.CreateAPIError("expired", ClaimRequest.GetErrorMessage(result.Result));
                case ClaimRequest.ClaimResult.NotStarted:
                    return this.CreateAPIError("not-started", ClaimRequest.GetErrorMessage(result.Result));
                case ClaimRequest.ClaimResult.Archived:
                    return this.CreateAPIError("archived", ClaimRequest.GetErrorMessage(result.Result));
                case ClaimRequest.ClaimResult.Overdraft:
                    return this.CreateAPIError("overdraft", ClaimRequest.GetErrorMessage(result.Result));
                case ClaimRequest.ClaimResult.AmountTooLow:
                    return this.CreateAPIError("amount-too-low", ClaimRequest.GetErrorMessage(result.Result));
                case ClaimRequest.ClaimResult.PaymentMethodNotSupported:
                    return this.CreateAPIError("payment-method-not-supported",
                        ClaimRequest.GetErrorMessage(result.Result));
                default:
                    throw new NotSupportedException("Unsupported ClaimResult");
            }

            return Ok(ToModel(result.PayoutData));
        }

        private Client.Models.PayoutData ToModel(Data.PayoutData p)
        {
            var blob = p.GetBlob(_serializerSettings);
            var model = new Client.Models.PayoutData()
            {
                Id = p.Id,
                PullPaymentId = p.PullPaymentDataId,
                Date = p.Date,
                OriginalCurrency = p.OriginalCurrency,
                OriginalAmount = p.OriginalAmount,
                PayoutCurrency = p.Currency,
                PayoutAmount = p.Amount,
                Revision = blob.Revision,
                State = p.State,
                PayoutMethodId = p.PayoutMethodId,
                PaymentProof = p.GetProofBlobJson(),
                Destination = blob.Destination,
                Metadata = blob.Metadata?? new JObject(),
            };
            return model;
        }

        public class CreateSideShiftPayoutThroughStoreRequest : CreatePayoutThroughStoreRequest
        {
            public string Memo { get; set; }
            public string ShiftCurrency { get; set; }
            public string ShiftNetwork { get; set; }
        }

        public class CreateSideShiftPayoutRequest : CreatePayoutRequest
        {
            public string Memo { get; set; }
            public string ShiftCurrency { get; set; }
            public string ShiftNetwork { get; set; }
        }

        public class QuoteResponse
        {
            public string id { get; set; }
            public string createdAt { get; set; }
            public string depositCoin { get; set; }
            public string settleCoin { get; set; }
            public string depositNetwork { get; set; }
            public string settleNetwork { get; set; }
            public string expiresAt { get; set; }
            public string depositAmount { get; set; }
            public string settleAmount { get; set; }
            public string rate { get; set; }
            public string affiliateId { get; set; }
        }

        public class ShiftResponse
        {
            public string id { get; set; }
            public string createdAt { get; set; }
            public string depositCoin { get; set; }
            public string settleCoin { get; set; }
            public string depositNetwork { get; set; }
            public string settleNetwork { get; set; }
            public string depositAddress { get; set; }
            public string settleAddress { get; set; }
            public string depositMin { get; set; }
            public string depositMax { get; set; }
            public string refundAddress { get; set; }
            public string type { get; set; }
            public string quoteId { get; set; }
            public string depositAmount { get; set; }
            public string settleAmount { get; set; }
            public string expiresAt { get; set; }
            public string status { get; set; }
            public string updatedAt { get; set; }
            public string rate { get; set; }
            public string averageShiftSeconds { get; set; }
        }
    }
}