using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Bonoos.iikoFront.LoyaltyPlugin.Models;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Снимок гостя по заказу — рядом с DLL (как Sagi_order_requests.json).
    /// Файл: {PluginDirectory}\Bonoos_order_guests.json
    /// </summary>
    internal static class OrderGuestStore
    {
        private static readonly object FileLock = new object();
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        };

        private static Action<string> _log = _ => { };

        public static string FilePath => PluginPaths.OrderGuestsJsonPath;

        public static readonly TimeSpan RefreshTtl = TimeSpan.FromHours(1);

        public static void Configure(Action<string> log) => _log = log ?? (_ => { });

        public static void Save(OrderGuestSnapshot snap)
        {
            if (snap == null || string.IsNullOrEmpty(snap.OrderId))
                return;

            snap.UpdatedAtUtc = DateTime.UtcNow;
            if (snap.BoundAtUtc == default)
                snap.BoundAtUtc = snap.UpdatedAtUtc;
            if (snap.LastLookupAtUtc == default)
                snap.LastLookupAtUtc = snap.UpdatedAtUtc;

            lock (FileLock)
            {
                try
                {
                    var doc = Load();
                    doc.orders[snap.OrderId] = snap;
                    SaveDoc(doc);
                    _log($"Bonoos: [GUEST_JSON] saved order=#{snap.OrderNumber} type={snap.CardType} path={FilePath}");
                }
                catch (Exception ex)
                {
                    _log($"Bonoos: [GUEST_JSON] save failed: {ex.Message}");
                }
            }
        }

        public static void UpdateOrderSum(string orderId, int? orderNumber, decimal orderSum)
        {
            if (string.IsNullOrEmpty(orderId))
                return;

            lock (FileLock)
            {
                try
                {
                    var doc = Load();
                    if (!doc.orders.TryGetValue(orderId, out var snap) || snap == null)
                        return;
                    snap.OrderSum = orderSum;
                    if (orderNumber.HasValue)
                        snap.OrderNumber = orderNumber;
                    snap.UpdatedAtUtc = DateTime.UtcNow;
                    SaveDoc(doc);
                }
                catch (Exception ex)
                {
                    _log($"Bonoos: [GUEST_JSON] update sum failed: {ex.Message}");
                }
            }
        }

        public static void TouchLookup(string orderId, ClientLookupResponse lookup)
        {
            if (string.IsNullOrEmpty(orderId) || lookup == null || !lookup.Found)
                return;

            lock (FileLock)
            {
                try
                {
                    var doc = Load();
                    if (!doc.orders.TryGetValue(orderId, out var snap) || snap == null)
                        return;

                    ApplyLookup(snap, lookup);
                    snap.LastLookupAtUtc = DateTime.UtcNow;
                    snap.UpdatedAtUtc = snap.LastLookupAtUtc;
                    SaveDoc(doc);
                    _log($"Bonoos: [GUEST_JSON] refreshed lookup orderId={orderId}");
                }
                catch (Exception ex)
                {
                    _log($"Bonoos: [GUEST_JSON] touch failed: {ex.Message}");
                }
            }
        }

        public static bool TryGet(string orderId, out OrderGuestSnapshot snap)
        {
            snap = null;
            if (string.IsNullOrEmpty(orderId))
                return false;

            lock (FileLock)
            {
                try
                {
                    var doc = Load();
                    if (!doc.orders.TryGetValue(orderId, out snap) || snap == null)
                        return false;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void Remove(string orderId)
        {
            if (string.IsNullOrEmpty(orderId))
                return;

            lock (FileLock)
            {
                try
                {
                    var doc = Load();
                    if (doc.orders.Remove(orderId))
                    {
                        SaveDoc(doc);
                        _log($"Bonoos: [GUEST_JSON] removed {orderId}");
                    }
                }
                catch (Exception ex)
                {
                    _log($"Bonoos: [GUEST_JSON] remove failed: {ex.Message}");
                }
            }
        }

        /// <summary>Очистка при закрытии кассовой смены (CafeSessionClosing) — как Irbis.</summary>
        public static void ClearForCashSessionClose()
        {
            lock (FileLock)
            {
                try
                {
                    SaveDoc(new OrderGuestDocument());
                    _log($"Bonoos: [GUEST_JSON] cleared (закрытие смены): {FilePath}");
                }
                catch (Exception ex)
                {
                    _log($"Bonoos: [GUEST_JSON] clear failed: {ex.Message}");
                }
            }
        }

        /// <summary>Заказы, у которых последний lookup старше TTL и статус ещё живой (проверяет caller).</summary>
        public static List<OrderGuestSnapshot> GetStale(TimeSpan ttl)
        {
            lock (FileLock)
            {
                try
                {
                    var doc = Load();
                    var now = DateTime.UtcNow;
                    return doc.orders.Values
                        .Where(s => s != null && s.Card != null && (now - s.LastLookupAtUtc) >= ttl)
                        .ToList();
                }
                catch
                {
                    return new List<OrderGuestSnapshot>();
                }
            }
        }

        public static OrderGuestSnapshot FromLookup(
            string orderId,
            string orderNumber,
            decimal orderSum,
            CardInfo card,
            ClientLookupResponse lookup)
        {
            var snap = new OrderGuestSnapshot
            {
                OrderId = orderId,
                OrderNumber = int.TryParse(orderNumber, out var n) ? n : (int?)null,
                OrderSum = orderSum,
                Card = card,
                BoundAtUtc = DateTime.UtcNow,
                LastLookupAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            };
            ApplyLookup(snap, lookup);
            return snap;
        }

        private static void ApplyLookup(OrderGuestSnapshot snap, ClientLookupResponse lookup)
        {
            if (lookup == null)
                return;

            snap.Found = lookup.Found;
            snap.CardId = lookup.CardId;
            snap.ClientProfileId = lookup.ClientProfileId;
            snap.Username = lookup.Username;
            snap.FirstName = lookup.FirstName;
            snap.LastName = lookup.LastName;
            snap.GuestDisplayName = !string.IsNullOrWhiteSpace(lookup.FirstName)
                ? $"{lookup.FirstName} {lookup.LastName}".Trim()
                : (string.IsNullOrWhiteSpace(lookup.Username) ? "Клиент" : lookup.Username);
            snap.BalanceKopecks = lookup.BalanceKopecks;
            snap.BalanceDisplay = lookup.BalanceDisplay;
            snap.CashbackPercent = lookup.CashbackPercent;
            snap.CardType = lookup.CardType;
            snap.DiscountPercent = lookup.DiscountPercent;
            snap.Message = lookup.Message;
        }

        private static OrderGuestDocument Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new OrderGuestDocument();
                return JsonConvert.DeserializeObject<OrderGuestDocument>(File.ReadAllText(FilePath), JsonSettings)
                       ?? new OrderGuestDocument();
            }
            catch
            {
                return new OrderGuestDocument();
            }
        }

        private static void SaveDoc(OrderGuestDocument doc)
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(doc ?? new OrderGuestDocument(), JsonSettings));
        }
    }

    internal sealed class OrderGuestDocument
    {
        public int version { get; set; } = 1;
        public Dictionary<string, OrderGuestSnapshot> orders { get; set; } =
            new Dictionary<string, OrderGuestSnapshot>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class OrderGuestSnapshot
    {
        public string OrderId { get; set; }
        public int? OrderNumber { get; set; }
        public decimal OrderSum { get; set; }

        public bool Found { get; set; }
        public string CardId { get; set; }
        public string ClientProfileId { get; set; }
        public string Username { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string GuestDisplayName { get; set; }
        public int? BalanceKopecks { get; set; }
        public string BalanceDisplay { get; set; }
        public int? CashbackPercent { get; set; }
        public string CardType { get; set; }
        public int? DiscountPercent { get; set; }
        public string Message { get; set; }

        public CardInfo Card { get; set; }

        public DateTime BoundAtUtc { get; set; }
        public DateTime LastLookupAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        [JsonIgnore]
        public bool NeedsRefresh => DateTime.UtcNow - LastLookupAtUtc >= OrderGuestStore.RefreshTtl;
    }
}
