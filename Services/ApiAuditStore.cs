using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Журнал API-вызовов Bonoos — как Irbis_loyalty_audit.json / Sagi_order_requests.json.
    /// Файл: %AppData%\iiko\CashServer\PluginConfigs\Bonoos\Bonoos_loyalty_audit.json
    /// </summary>
    internal static class ApiAuditStore
    {
        private const int MaxCalls = 500;
        private const int MaxBodyChars = 8000;

        private static readonly object FileLock = new object();
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        private static Action<string> _log = _ => { };

        public static string AuditFilePath => PluginPaths.ApiAuditJsonPath;

        public static void Configure(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        public static void Record(
            string endpoint,
            string method,
            string url,
            string requestBody,
            int? httpStatus,
            string responseBody,
            long elapsedMs,
            bool success,
            string error = null,
            string orderId = null,
            string orderNumber = null)
        {
            var entry = new ApiAuditEntry
            {
                AtUtc = DateTime.UtcNow,
                Endpoint = endpoint,
                Method = method ?? "POST",
                Url = url,
                RequestBody = Truncate(requestBody),
                HttpStatus = httpStatus,
                ResponseBody = Truncate(responseBody),
                ElapsedMs = elapsedMs,
                Success = success,
                Error = error,
                OrderId = orderId,
                OrderNumber = orderNumber,
            };

            lock (FileLock)
            {
                try
                {
                    var doc = Load();
                    doc.apiCalls.Add(entry);
                    if (doc.apiCalls.Count > MaxCalls)
                        doc.apiCalls = doc.apiCalls.Skip(doc.apiCalls.Count - MaxCalls).ToList();
                    Save(doc);
                }
                catch (Exception ex)
                {
                    _log($"Bonoos: [AUDIT] write failed — {ex.Message}");
                }
            }
        }

        public static void Clear()
        {
            lock (FileLock)
            {
                try
                {
                    Save(new ApiAuditDocument());
                    _log($"Bonoos: [AUDIT] cleared {AuditFilePath}");
                }
                catch (Exception ex)
                {
                    _log($"Bonoos: [AUDIT] clear failed — {ex.Message}");
                }
            }
        }

        /// <summary>Очистка при закрытии кассовой смены.</summary>
        public static void ClearForCashSessionClose() => Clear();

        private static ApiAuditDocument Load()
        {
            try
            {
                var path = AuditFilePath;
                if (!File.Exists(path))
                    return new ApiAuditDocument();

                return JsonConvert.DeserializeObject<ApiAuditDocument>(File.ReadAllText(path), JsonSettings)
                       ?? new ApiAuditDocument();
            }
            catch
            {
                return new ApiAuditDocument();
            }
        }

        private static void Save(ApiAuditDocument doc)
        {
            var path = AuditFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonConvert.SerializeObject(doc ?? new ApiAuditDocument(), JsonSettings));
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            return text.Length <= MaxBodyChars ? text : text.Substring(0, MaxBodyChars) + "…";
        }
    }

    internal sealed class ApiAuditDocument
    {
        public int version { get; set; } = 1;
        public List<ApiAuditEntry> apiCalls { get; set; } = new List<ApiAuditEntry>();
    }

    internal sealed class ApiAuditEntry
    {
        public DateTime AtUtc { get; set; }
        public string Endpoint { get; set; }
        public string Method { get; set; }
        public string Url { get; set; }
        public string OrderId { get; set; }
        public string OrderNumber { get; set; }
        public string RequestBody { get; set; }
        public int? HttpStatus { get; set; }
        public string ResponseBody { get; set; }
        public long ElapsedMs { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }
}
