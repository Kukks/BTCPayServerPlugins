using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BTCPayServer.Plugins.Conference;

public class ConferenceCsvService
{
    private static readonly string[] RequiredHeaders = { "Email", "StoreName" };

    private static readonly string[] AllHeaders =
    {
        "Email", "StoreName", "Currency", "Spread", "LightningConnectionString", "Password"
    };

    public byte[] GenerateTemplate()
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", AllHeaders));
        sb.AppendLine("merchant@example.com,Coffee Shop,USD,2.0,,");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public CsvImportResult ParseCsv(Stream stream, List<ConferenceMerchant> existingMerchants)
    {
        var result = new CsvImportResult();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            result.Errors.Add("CSV file is empty");
            return result;
        }

        var headers = ParseCsvLine(headerLine);
        var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Length; i++)
        {
            headerMap[headers[i].Trim()] = i;
        }

        foreach (var required in RequiredHeaders)
        {
            if (!headerMap.ContainsKey(required))
            {
                result.Errors.Add($"Missing required column: {required}");
            }
        }

        if (result.Errors.Count > 0)
            return result;

        var lineNumber = 1;
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var fields = ParseCsvLine(line);

            var email = GetField(fields, headerMap, "Email")?.Trim();
            var storeName = GetField(fields, headerMap, "StoreName")?.Trim();

            if (string.IsNullOrEmpty(email))
            {
                result.Errors.Add($"Line {lineNumber}: Email is required");
                continue;
            }

            if (string.IsNullOrEmpty(storeName))
            {
                result.Errors.Add($"Line {lineNumber}: StoreName is required");
                continue;
            }

            // Find existing merchant by email or create new
            var merchant = existingMerchants.FirstOrDefault(
                m => m.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

            if (merchant != null)
            {
                // Update existing merchant
                merchant.StoreName = storeName;
                result.Updated++;
            }
            else
            {
                merchant = new ConferenceMerchant { Email = email, StoreName = storeName };
                existingMerchants.Add(merchant);
                result.Added++;
            }

            // Optional fields
            var currency = GetField(fields, headerMap, "Currency")?.Trim();
            if (!string.IsNullOrEmpty(currency))
                merchant.Currency = currency;

            var spreadStr = GetField(fields, headerMap, "Spread")?.Trim();
            if (!string.IsNullOrEmpty(spreadStr) &&
                decimal.TryParse(spreadStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var spread))
                merchant.Spread = spread;

            var lnConnString = GetField(fields, headerMap, "LightningConnectionString")?.Trim();
            if (!string.IsNullOrEmpty(lnConnString))
                merchant.LightningConnectionString = lnConnString;

            var password = GetField(fields, headerMap, "Password")?.Trim();
            if (!string.IsNullOrEmpty(password))
                merchant.Password = password;
        }

        return result;
    }

    private static string GetField(string[] fields, Dictionary<string, int> headerMap, string column)
    {
        if (!headerMap.TryGetValue(column, out var index) || index >= fields.Length)
            return null;
        return fields[index];
    }

    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }
}

public class CsvImportResult
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool HasErrors => Errors.Count > 0;
}
