using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.VisualBasic;
using NBitcoin;
using NBitcoin.Crypto;
using NBitcoin.DataEncoders;
using NBitcoin.Secp256k1;
using Newtonsoft.Json.Linq;
using Key = NBitcoin.Key;

namespace BTCPayServer.Plugins.FujiOracle
{
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
    [Route("plugins/{storeId}/FujiOracle")]
    public class FujiOracleController : Controller
    {



        private async Task<BTCPayServerClient> CreateClient(string storeId)
        {
            return await _btcPayServerClientFactory.Create(null, new[] {storeId}, new DefaultHttpContext()
            {
                Request =
                {
                    Scheme = "https",
                    Host = Request.Host,
                    Path = Request.Path,
                    PathBase = Request.PathBase
                }
            });
        }
        

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly FujiOracleService _FujiOracleService;
        private readonly IBTCPayServerClientFactory _btcPayServerClientFactory;

        public FujiOracleController(IHttpClientFactory httpClientFactory,
            FujiOracleService FujiOracleService,
            IBTCPayServerClientFactory btcPayServerClientFactory)
        {
            _httpClientFactory = httpClientFactory;
            _FujiOracleService = FujiOracleService;
            _btcPayServerClientFactory = btcPayServerClientFactory;
        }

        [HttpGet("update")]
        public async Task<IActionResult> UpdateFujiOracleSettings(string storeId)
        {
            var
                FujiOracle = (await _FujiOracleService.GetFujiOracleForStore(storeId)) ?? new();


            return View(FujiOracle);
        }

       

        [HttpPost("update")]
        public async Task<IActionResult> UpdateFujiOracleSettings(string storeId,
            FujiOracleSettings vm,
            string command)
        {
            if (command == "generate")
            {
                ModelState.Clear();

                if (ECPrivKey.TryCreate(new ReadOnlySpan<byte>(RandomNumberGenerator.GetBytes(32)), out var key))
                {
                    vm.Key = key.ToHex();
                }
                return View(vm);
            }

            if (command == "add-pair")
            {
                vm.Pairs ??= new List<string>();
                vm.Pairs.Add("");
                return View(vm);
            }

            if (command.StartsWith("remove-pair"))
            {
                var i = int.Parse(command.Substring(command.IndexOf(":", StringComparison.InvariantCultureIgnoreCase) +
                                                    1));
                vm.Pairs.RemoveAt(i);
                return View(vm);
            }


            var validPairsToQuery = "";
            for (var i = 0; i < vm.Pairs.Count; i++)
            {
                string vmPair = vm.Pairs[i];
                if (string.IsNullOrWhiteSpace(vmPair))
                {
                    
                    ModelState.AddModelError($"{vm.Pairs}[{i}]",
                        $"Remove invalid");
                    continue;
                }

                var split = vmPair.Split("_", StringSplitOptions.RemoveEmptyEntries);
                if (split.Length != 2)
                {
                    ModelState.AddModelError($"{vm.Pairs}[{i}]",
                        $"Invalid format, needs to be BTC_USD format");
                    continue;
                }

                validPairsToQuery += "," + vmPair;
            }

            validPairsToQuery = validPairsToQuery.TrimStart(',');
            if (!string.IsNullOrEmpty(validPairsToQuery))
            {
                try
                {
                    var url = Request.GetAbsoluteUri(Url.Action("GetRates2",
                        "BitpayRate", new {storeId, currencyPairs = validPairsToQuery}));
                    var resp = JArray.Parse(await _httpClientFactory.CreateClient().GetStringAsync(url));

                    for (var i = 0; i < vm.Pairs.Count; i++)
                    {
                        
                        string vmPair = vm.Pairs[i];
                        if (!resp.Any(token => token["currencyPair"].Value<string>() == vmPair))
                        {
                            ModelState.AddModelError($"{vm.Pairs}[{i}]",
                                $"You store could not resolve pair {vmPair}");
                        }
                    }
                }
                catch (Exception e)
                {
                }
               
            }

            if (string.IsNullOrEmpty(vm.Key) && vm.Enabled)
            {
                
                ModelState.AddModelError(nameof(vm.Enabled),
                    $"Cannot enable without a key");
            }

            if (!string.IsNullOrEmpty(vm.Key))
            {
                try
                {
                    if (HexEncoder.IsWellFormed(vm.Key))
                    {
                        ECPrivKey.Create(Encoders.Hex.DecodeData(vm.Key));
                    }
                }
                catch (Exception e)
                {
                  
                    ModelState.AddModelError(nameof(vm.Enabled),
                        $"Key was invalid");
                }
            }
            if (!ModelState.IsValid)
            {
                return View(vm);
            }


            switch (command?.ToLowerInvariant())
            {
                case "save":
                    await _FujiOracleService.SetFujiOracleForStore(storeId, vm);
                    TempData["SuccessMessage"] = "FujiOracle settings modified";
                    return RedirectToAction(nameof(UpdateFujiOracleSettings), new {storeId});

                default:
                    return View(vm);
            }
        }

        [AllowAnonymous]
        [HttpGet("")]
        public async Task<IActionResult> GetOracleInfo(string storeId)
        {
            var oracle = await _FujiOracleService.GetFujiOracleForStore(storeId);
            if (oracle is null || !oracle.Enabled || oracle.Key is null)
            {
                return NotFound();
            }

            return Ok(new
            {
                publicKey = new Key(Encoders.Hex.DecodeData(oracle.Key)).PubKey.ToHex(),
                availableTickers = oracle.Pairs.ToArray()
            });
        }
        [AllowAnonymous]
        [HttpGet("{pair}")]
        public async Task<IActionResult> GetOracleAttestation(string storeId, string pair)
        {
            var oracle = await _FujiOracleService.GetFujiOracleForStore(storeId);
            if (oracle is null || !oracle.Enabled || oracle.Key is null || !oracle.Pairs.Contains(pair))
            {
                return NotFound();
            }
            var url = Request.GetAbsoluteUri(Url.Action("GetRates2",
                "BitpayRate", new {storeId, currencyPairs = pair}));
            var resp = JArray.Parse(await _httpClientFactory.CreateClient().GetStringAsync(url)).First();
            var ts = DateTimeOffset.Now.ToUnixTimeSeconds();
            var rate =(long)decimal.Truncate( resp["rate"].Value<decimal>());
            var messageBytes = BitConverter.GetBytes(ts).Concat(BitConverter.GetBytes(rate)).ToArray();
            using var sha256Hash = System.Security.Cryptography.SHA256.Create();
            var messageHash =  sha256Hash.ComputeHash(messageBytes);
            var key = Extensions.ParseKey(oracle.Key);
            var buf = new byte[64];
            key.SignBIP340(messageHash).WriteToSpan(buf);
            var sig = buf.ToHex();
            
            return Ok(new
            {
                timestamp = ts.ToString(),
                lastPrice = rate.ToString(),
                attestation = new {
                    signature= sig,
                    message= messageBytes.ToHex(),
                    messageHash= messageHash.ToHex()
            },
            });
        }
    }

    public static class Extensions
    {
        public static ECPrivKey ParseKey(string key)
        {
            return ECPrivKey.Create(key.DecodHexData());
        }
        
        public static byte[] DecodHexData(this string encoded)
        {
            if (encoded == null)
                throw new ArgumentNullException(nameof(encoded));
            if (encoded.Length % 2 == 1)
                throw new FormatException("Invalid Hex String");

            var result = new byte[encoded.Length / 2];
            for (int i = 0, j = 0; i < encoded.Length; i += 2, j++)
            {
                var a = IsDigit(encoded[i]);
                var b = IsDigit(encoded[i + 1]);
                if (a == -1 || b == -1)
                    throw new FormatException("Invalid Hex String");
                result[j] = (byte)(((uint)a << 4) | (uint)b);
            }

            return result;
        }
        
        public static int IsDigit(this char c)
        {
            if ('0' <= c && c <= '9')
            {
                return c - '0';
            }
            else if ('a' <= c && c <= 'f')
            {
                return c - 'a' + 10;
            }
            else if ('A' <= c && c <= 'F')
            {
                return c - 'A' + 10;
            }
            else
            {
                return -1;
            }
        }
        
        public static string ToHex(this byte[] bytes)
        {
            var builder = new StringBuilder();
            foreach (var t in bytes)
            {
                builder.Append(t.ToHex());
            }

            return builder.ToString();
        }

        private static string ToHex(this byte b)
        {
            return b.ToString("x2");
        }

        public static string ToHex(this Span<byte> bytes)
        {
            var builder = new StringBuilder();
            foreach (var t in bytes)
            {
                builder.Append(t.ToHex());
            }

            return builder.ToString();
        }
        
        public static string ToHex(this ECPrivKey key)
        {
            Span<byte> output = new(new byte[32]);
            key.WriteToSpan(output);
            return output.ToHex();
        }
    }
}
