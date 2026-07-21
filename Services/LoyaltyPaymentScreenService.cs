using System;
using System.Linq;
using Resto.Front.Api;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.Data.Payments;
using Resto.Front.Api.Extensions;
using Resto.Front.Api.OperationContexts;
using Resto.Front.Api.UI;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// После «Списать» сумму с нумпада править нельзя — возвращаем LockedBonusPaymentSum.
    /// Удалить оплату и ввести заново можно (Locked сбрасывается в OnPaymentDeleting).
    /// </summary>
    internal sealed class LoyaltyPaymentScreenService : IDisposable
    {
        private readonly OrderTracker _tracker;
        private readonly Action<string> _log;
        private IDisposable _subscription;

        public LoyaltyPaymentScreenService(OrderTracker tracker, Action<string> log)
        {
            _tracker = tracker;
            _log = log ?? (_ => { });
        }

        public void Start()
        {
            _subscription = PluginContext.Notifications.PaymentScreenUpdated.Subscribe(OnPaymentScreenUpdated);
            _log("Bonoos: [LOYALTY] PaymentScreenUpdated — фиксация суммы бонусов.");
        }

        private void OnPaymentScreenUpdated((IPaymentScreenUpdatedContext context, IViewManager vm) args)
        {
            try
            {
                var ctx = args.context;
                if (ctx?.Payments == null)
                    return;

                var oid = ctx.OrderId.ToString();
                if (!_tracker.TryGetOrder(oid, out var state) ||
                    !(state.LockedBonusPaymentSum is decimal locked) ||
                    locked <= 0)
                    return;

                var bonusPayments = ctx.Payments.Where(LoyaltyPaymentHelper.IsBonusPayment).ToList();
                if (bonusPayments.Count == 0)
                    return;

                var current = bonusPayments.Sum(p => p.Sum);
                if (Math.Abs(current - locked) < 0.005m)
                    return;

                _log($"Bonoos: [LOYALTY] нумпад изменил бонусы {current:N2} → откат к {locked:N2}");

                var os = PluginContext.Operations;
                var order = os.GetOrderById(ctx.OrderId);
                if (order == null)
                    return;

                var credentials = os.GetDefaultCredentials();
                var primary = bonusPayments[0];
                try
                {
                    os.ChangePaymentItemSum(locked, null, locked, primary, order, credentials);
                }
                catch (Exception ex)
                {
                    _log($"Bonoos: [LOYALTY] откат суммы: {ex.Message}");
                }

                foreach (var extra in bonusPayments.Skip(1))
                {
                    try
                    {
                        order = os.GetOrderById(ctx.OrderId);
                        os.DeletePaymentItem(extra, order, credentials);
                    }
                    catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [LOYALTY] PaymentScreenUpdated: {ex.Message}");
            }
        }

        public void Dispose() => _subscription?.Dispose();
    }
}
