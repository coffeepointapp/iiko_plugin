using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Resto.Front.Api;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.Data.Payments;
using Resto.Front.Api.Extensions;
using Resto.Front.Api.UI;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Кнопка «Гость» на экране заказа.
    /// Отвязка: снять скидку + бонусные оплаты через os сессии (без TryEditCurrentOrder — иначе зависание).
    /// </summary>
    internal sealed class BonoosUiManager : MarshalByRefObject, IDisposable
    {
        private readonly OrderTracker _tracker;
        private readonly DiscountService _discount;
        private readonly Action<string> _log;
        private readonly Action<string> _onGuestUnbound;
        private readonly List<IDisposable> _subscriptions = new List<IDisposable>();

        public BonoosUiManager(
            OrderTracker tracker,
            DiscountService discount,
            Action<string> log,
            Action<string> onGuestUnbound = null)
        {
            _tracker = tracker;
            _discount = discount;
            _log = log ?? (_ => { });
            _onGuestUnbound = onGuestUnbound;
        }

        public override object InitializeLifetimeService() => null;

        public void InitializeButtons(IOperationService operations)
        {
            try
            {
                Action<(IOrder order, IOperationService os, IViewManager vm)> handler = OnGuestButtonClick;
                var guestButton = operations.AddButtonToOrderEditScreen(
                    "Гость",
                    handler,
                    BonoosButtonGeometry.ForToolbar);

                if (guestButton != null)
                {
                    _subscriptions.Add(guestButton);
                    _log("Bonoos: [UI] кнопка «Гость» зарегистрирована.");
                }
                else
                {
                    _log("Bonoos: [UI] AddButtonToOrderEditScreen вернул null!");
                }
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [UI] не удалось зарегистрировать кнопку: {ex}");
            }
        }

        private void OnGuestButtonClick((IOrder order, IOperationService os, IViewManager vm) args)
        {
            try
            {
                _log($"Bonoos: [UI] CLICK «Гость» order=#{args.order?.Number}");

                if (args.order == null || args.vm == null || args.os == null)
                    return;

                var oid = args.order.Id.ToString();

                if (!_tracker.TryGetOrder(oid, out var state) || state.Card == null)
                    TryRestoreFromJson(oid, args.order);

                if (!_tracker.HasBoundCard(oid) || !_tracker.TryGetOrder(oid, out state) || state.Card == null)
                {
                    args.vm.ShowOkPopup(
                        "Гость",
                        "Гость не привязан.\n\nОтсканируйте QR-код клиента на экране заказа.");
                    return;
                }

                var sum = args.order.FullSum > 0 ? args.order.FullSum : args.order.ResultSum;
                var info =
                    $"Гость: {state.GuestDisplayName ?? "клиент"}\n" +
                    $"Заказ: #{args.order.Number}\n" +
                    $"Сумма: {sum:N2}\n" +
                    (state.IsDiscountCard
                        ? $"Тип: DISCOUNT\nСкидка: {state.DiscountPercent ?? 0}%"
                        : $"Тип: {state.CardType ?? "LOYALTY_COINS"}\n" +
                          $"Баланс: {state.BalanceDisplay ?? ((state.AvailableBonusKopecks ?? 0) / 100m).ToString("0.##")}\n" +
                          $"Кэшбэк: {state.CashbackPercent ?? 0}%");

                var unbind = args.vm.ShowOkCancelPopup(
                    "Гость",
                    info + "\n\nОтвязать гостя от заказа?\nСкидка и бонусы Bonoos будут сняты.",
                    "Отвязать",
                    "Закрыть");

                if (!unbind)
                    return;

                UnbindGuestFromOrder(args.order, args.os, oid, state);

                // Без второго ShowOkPopup — иначе Host зависает («сторонний плагин»).
                try
                {
                    PluginContext.Operations.AddNotificationMessage(
                        "Гость отвязан. Скидка/бонусы сняты.",
                        "Bonoos",
                        TimeSpan.FromSeconds(4));
                }
                catch { /* ignore */ }

                _log($"Bonoos: [UI] гость отвязан order=#{args.order.Number}");
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [UI] ошибка кнопки: {ex}");
                try { args.vm?.ShowErrorPopup("Гость", ex.Message); }
                catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Снять скидку + оплаты Bonoos + отменить резерв API + очистить JSON/память.
        /// Только os/order из клика кнопки — без TryEditCurrentOrder.
        /// </summary>
        private void UnbindGuestFromOrder(
            IOrder order,
            IOperationService os,
            string oid,
            OrderLoyaltyState state)
        {
            // 1) Скидка «Свободная сумма» — всегда пробуем снять (не только если HasOurDiscount)
            try
            {
                if (_discount != null)
                {
                    _discount.RemoveOurDiscount(order, os);
                    _log($"Bonoos: [UI] после отвязки — попытка снять скидку order=#{order.Number}");
                }
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [UI] снятие скидки: {ex.Message}");
            }

            // 2) Строки оплаты Bonoos на заказе
            try
            {
                RemoveBonusPayments(order, os);
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [UI] снятие оплат: {ex.Message}");
            }

            // 3) Отмена резерва на сервере — в фоне (HTTP не блокирует UI)
            var needCancel = state.BonusReserved;
            if (needCancel)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        _tracker.CancelBonusAsync(oid).ConfigureAwait(false).GetAwaiter().GetResult();
                        _log($"Bonoos: [UI] cancel pay-by-bonus ok orderId={oid}");
                    }
                    catch (Exception ex)
                    {
                        _log($"Bonoos: [UI] cancel pay-by-bonus: {ex.Message}");
                    }
                });
            }

            // 4) Память + JSON
            _tracker.UnbindGuest(oid);
            OrderGuestStore.Remove(oid);
            _onGuestUnbound?.Invoke(oid);
        }

        private void RemoveBonusPayments(IOrder order, IOperationService os)
        {
            if (order?.Payments == null || os == null)
                return;

            var bonuses = order.Payments.Where(LoyaltyPaymentHelper.IsBonusPayment).ToList();
            if (bonuses.Count == 0)
                return;

            var credentials = os.GetDefaultCredentials();
            foreach (var payment in bonuses)
            {
                try
                {
                    // order из сессии кнопки — без GetOrderById
                    os.DeletePaymentItem(payment, order, credentials);
                    _log($"Bonoos: [UI] удалена оплата Bonoos sum={payment.Sum:N2}");
                }
                catch (Exception ex)
                {
                    _log($"Bonoos: [UI] DeletePaymentItem: {ex.Message}");
                }
            }

            _tracker.ClearLockedBonusPaymentSum(order.Id.ToString());
        }

        private void TryRestoreFromJson(string orderId, IOrder order)
        {
            if (!OrderGuestStore.TryGet(orderId, out var snap) || snap?.Card == null)
                return;

            var state = _tracker.GetOrCreateOrder(orderId, order.Number.ToString());
            state.Card = snap.Card;
            state.CardType = snap.CardType;
            state.DiscountPercent = snap.DiscountPercent;
            state.AvailableBonusKopecks = snap.BalanceKopecks;
            state.BalanceDisplay = snap.BalanceDisplay;
            state.CashbackPercent = snap.CashbackPercent;
            state.GuestDisplayName = snap.GuestDisplayName;
            state.BoundAtUtc = snap.BoundAtUtc;
            state.LastLookupAtUtc = snap.LastLookupAtUtc;
            _log($"Bonoos: [UI] гость из JSON order=#{order.Number} type={snap.CardType}");
        }

        public void Dispose()
        {
            foreach (var s in _subscriptions)
            {
                try { s.Dispose(); }
                catch { /* ignore */ }
            }
            _subscriptions.Clear();
        }
    }
}
