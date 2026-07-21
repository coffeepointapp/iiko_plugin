using System;
using System.Threading;
using Resto.Front.Api;
using Resto.Front.Api.Data.Orders;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Раз в N минут: если гость привязан дольше 1 часа и заказ ещё жив — повторный lookup.
    /// Как Irbis LoyaltyOrderCache TTL = 1h.
    /// </summary>
    internal sealed class GuestRefreshService : IDisposable
    {
        private readonly OrderTracker _tracker;
        private readonly DiscountService _discount;
        private readonly Action<string> _log;
        private readonly Timer _timer;
        private int _running;

        public GuestRefreshService(OrderTracker tracker, DiscountService discount, Action<string> log)
        {
            _tracker = tracker;
            _discount = discount;
            _log = log ?? (_ => { });
            // Первая проверка через 2 мин, далее каждые 5 мин
            _timer = new Timer(OnTick, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
            _log($"Bonoos: [GUEST_REFRESH] timer started (TTL={OrderGuestStore.RefreshTtl.TotalHours}h)");
        }

        private void OnTick(object state)
        {
            if (Interlocked.Exchange(ref _running, 1) == 1)
                return;

            try
            {
                var stale = OrderGuestStore.GetStale(OrderGuestStore.RefreshTtl);
                if (stale.Count == 0)
                    return;

                _log($"Bonoos: [GUEST_REFRESH] stale guests: {stale.Count}");

                foreach (var snap in stale)
                {
                    try
                    {
                        RefreshOne(snap);
                    }
                    catch (Exception ex)
                    {
                        _log($"Bonoos: [GUEST_REFRESH] order={snap.OrderId} — {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [GUEST_REFRESH] tick — {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        private void RefreshOne(OrderGuestSnapshot snap)
        {
            if (snap?.Card == null || string.IsNullOrEmpty(snap.OrderId))
                return;

            if (!Guid.TryParse(snap.OrderId, out var gid))
                return;

            IOrder order;
            try
            {
                order = PluginContext.Operations.GetOrderById(gid);
            }
            catch
            {
                return;
            }

            if (order == null)
            {
                OrderGuestStore.Remove(snap.OrderId);
                _tracker.RemoveOrder(snap.OrderId);
                _log($"Bonoos: [GUEST_REFRESH] order gone — removed {snap.OrderId}");
                return;
            }

            if (order.Status != OrderStatus.New && order.Status != OrderStatus.Bill)
            {
                OrderGuestStore.Remove(snap.OrderId);
                return;
            }

            _log($"Bonoos: [GUEST_REFRESH] lookup order=#{order.Number} (last={snap.LastLookupAtUtc:o})");

            var lookup = _tracker.LookupClientAsync(
                    snap.OrderId,
                    order.Number.ToString(),
                    snap.Card)
                .ConfigureAwait(false).GetAwaiter().GetResult();

            if (lookup == null || !lookup.Found)
            {
                _log($"Bonoos: [GUEST_REFRESH] lookup miss order=#{order.Number}");
                return;
            }

            OrderGuestStore.TouchLookup(snap.OrderId, lookup);
            OrderGuestStore.UpdateOrderSum(snap.OrderId, order.Number, order.FullSum > 0 ? order.FullSum : order.ResultSum);

            // DISCOUNT: пересчитать скидку если заказ ещё New
            if (lookup.IsDiscountCard &&
                lookup.DiscountPercent is int pct && pct > 0 &&
                order.Status == OrderStatus.New &&
                _discount != null)
            {
                _discount.ApplyViaTryEditCurrentOrder(
                    order.Id, pct,
                    _tracker.TryGetOrder(snap.OrderId, out var st) ? st.AppliedFlexibleDiscountSum : null,
                    out var applied);
                if (applied.HasValue && _tracker.TryGetOrder(snap.OrderId, out var state))
                    state.AppliedFlexibleDiscountSum = applied;
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}
