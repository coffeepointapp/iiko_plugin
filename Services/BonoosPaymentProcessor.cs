using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Bonoos.iikoFront.LoyaltyPlugin.Models;
using Resto.Front.Api;
using Resto.Front.Api.Data.Cheques;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.Data.Organization;
using Resto.Front.Api.Data.Payments;
using Resto.Front.Api.Data.Security;
using Resto.Front.Api.Data.View;
using Resto.Front.Api.Exceptions;
using Resto.Front.Api.Extensions;
using Resto.Front.Api.UI;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Внешняя ПС Bonoos — порядок как IrbisBonusPaymentProcessor (LOYALTY.md §6):
    /// CollectData = только проверки (без диалогов);
    /// OnPaymentAdded = баланс → сумма → ChangePaymentItemSum;
    /// Pay = POST /order/pay-by-bonus/.
    /// Отмена UI: удалить строку оплаты, без PaymentActionCancelledException.
    /// </summary>
    public sealed class BonoosPaymentProcessor : MarshalByRefObject, IPaymentProcessor, IDisposable
    {
        private static readonly ConcurrentDictionary<Guid, object> BonusPaymentLocks =
            new ConcurrentDictionary<Guid, object>();

        private readonly OrderTracker _tracker;
        private readonly Action<string> _log;

        public BonoosPaymentProcessor(OrderTracker tracker, Action<string> log)
        {
            _tracker = tracker;
            _log = log ?? (_ => { });
        }

        public string PaymentSystemKey => "bonoos-loyalty";
        public string PaymentSystemName => "Bonoos";

        private const string MsgDiscountNoBonus =
            "У гостя категория «скидка».\nБонусы использовать нельзя.";

        private const string MsgGuestNotBound =
            "Оплата Bonoos недоступна.\nСначала привяжите гостя (кнопка «Гость» или скан QR).";

        // ── CollectData: ТОЛЬКО проверки. Диалогов нет (иначе «Сбор данных»). ──
        public void CollectData(Guid orderId, Guid paymentTypeId, IUser cashier,
            IReceiptPrinter printer, IViewManager viewManager, IPaymentDataContext context)
        {
            var order = PluginContext.Operations.GetOrderById(orderId);
            if (order == null)
                return;

            // Бизнес-отказ (скидка / нет гостя): НЕ throw — иначе VS «прерывает» и Host зависает на «Сбор данных».
            // Отказ и модалка — в OnPaymentAdded (удаляем строку оплаты + ShowErrorPopup).
            if (!TryGetLoyaltyForBonusPay(orderId, order, out var state, out var denyReason))
            {
                _log($"Bonoos: [LOYALTY] CollectData soft-deny: {denyReason}");
                // Помечаем context? нет — просто выходим; OnPaymentAdded покажет текст.
                return;
            }

            if (!(state.AvailableBonusKopecks is int kopecks) || kopecks <= 0)
            {
                _log("Bonoos: [LOYALTY] CollectData soft-deny: баланс 0");
                return;
            }

            var balance = kopecks / 100m;
            var existing = LoyaltyPaymentHelper.SumBonusPayments(order);
            if (existing > 0)
            {
                throw new PaymentActionFailedException(
                    "Бонусы уже применены на этот заказ.\nУдалите или измените текущую бонусную оплату.",
                    false);
            }

            var maxAllowed = LoyaltyPaymentHelper.GetMaxBonusAllowed(order, balance);
            if (maxAllowed <= 0)
            {
                throw new PaymentActionFailedException(
                    $"Нечего списывать.\nБаланс: {balance:N2}, к оплате: {order.ResultSum:N2}.",
                    false);
            }

            // Диалоги — в OnPaymentAdded (как Irbis / Sagi).
        }

        // ── OnPaymentAdded: все UI-диалоги ──
        public void OnPaymentAdded(IOrder order, IPaymentItem paymentItem, IUser cashier,
            IOperationService operationService, IReceiptPrinter printer, IViewManager viewManager,
            IPaymentDataContext context)
        {
            if (order == null || paymentItem == null)
                throw new PaymentActionFailedException("Не удалось добавить оплату Bonoos.", false);

            var orderId = order.Id;
            var gate = BonusPaymentLocks.GetOrAdd(orderId, _ => new object());
            lock (gate)
            {
                order = operationService.GetOrderById(orderId) ?? order;
                var oid = orderId.ToString();

                if (!TryGetLoyaltyForBonusPay(orderId, order, out var state, out var denyReason))
                {
                    DenyBonusPaymentUi(order, paymentItem, operationService, viewManager,
                        denyReason ?? MsgGuestNotBound);
                    return;
                }

                if (!(state.AvailableBonusKopecks is int kopecks) || kopecks <= 0)
                {
                    DenyBonusPaymentUi(order, paymentItem, operationService, viewManager,
                        "Баланс гостя не найден или равен 0.\nПривяжите гостя заново.");
                    return;
                }

                var balance = kopecks / 100m;
                var maxAllowed = LoyaltyPaymentHelper.GetMaxBonusAllowed(order, balance, paymentItem);
                _log($"Bonoos: [LOYALTY] OnPaymentAdded balance={balance:N2} maxAllowed={maxAllowed:N2} paymentSum={paymentItem.Sum:N2}");

                if (maxAllowed <= 0)
                {
                    RejectBonusPayment(order, paymentItem, operationService,
                        $"Нечего списывать.\nБаланс: {balance:N2}.");
                    return;
                }

                decimal amountToApply;
                if (paymentItem.Sum > 0m)
                {
                    amountToApply = paymentItem.Sum;
                }
                else
                {
                    if (viewManager == null)
                    {
                        RejectBonusPayment(order, paymentItem, operationService,
                            "Не удалось открыть диалог списания бонусов.");
                        return;
                    }

                    if (!TryCollectBonusPaymentViaUi(order, state, balance, maxAllowed, viewManager, out amountToApply))
                    {
                        CancelBonusPaymentAddition(operationService, order, paymentItem);
                        return;
                    }
                }

                if (amountToApply <= 0m)
                {
                    RejectBonusPayment(order, paymentItem, operationService,
                        "Сумма списания должна быть больше 0.");
                    return;
                }

                if (amountToApply > maxAllowed)
                {
                    RejectBonusPayment(order, paymentItem, operationService,
                        $"Сумма списания ({amountToApply:N2}) превышает допустимую ({maxAllowed:N2}).");
                    return;
                }

                try
                {
                    var credentials = operationService.GetDefaultCredentials();
                    var freshOrder = operationService.GetOrderById(orderId);
                    // max = amount: нумпад не должен поднимать сумму выше зафиксированной
                    operationService.ChangePaymentItemSum(
                        amountToApply, null, amountToApply, paymentItem, freshOrder, credentials);
                    _tracker.LockBonusPaymentSum(oid, amountToApply);
                    _log($"Bonoos: [LOYALTY] OnPaymentAdded: применено {amountToApply:N2} (зафиксировано)");
                }
                catch (Exception ex) when (
                    !(ex is PaymentActionCancelledException) &&
                    !(ex is PaymentActionFailedException))
                {
                    _log($"Bonoos: [LOYALTY] OnPaymentAdded ChangePaymentItemSum: {ex.Message}");
                    RejectBonusPayment(order, paymentItem, operationService, ex.Message);
                }
            }
        }

        private bool TryCollectBonusPaymentViaUi(
            IOrder order,
            OrderLoyaltyState state,
            decimal balance,
            decimal maxAllowed,
            IViewManager viewManager,
            out decimal amountToApply)
        {
            amountToApply = 0m;

            var name = "клиент";
            viewManager.ShowOkPopup(
                "Бонусы",
                $"Баланс: {balance:N2}\nМожно списать: {maxAllowed:N2}");

            if (!LoyaltyBonusAmountPrompt.TryPromptBonusAmount(
                    viewManager,
                    maxAllowed,
                    balance,
                    name,
                    maxAllowed,
                    out amountToApply,
                    balanceAlreadyShown: true))
            {
                _log($"Bonoos: [LOYALTY] ввод суммы отменён order=#{order.Number}");
                return false;
            }

            if (amountToApply <= 0m)
            {
                viewManager.ShowErrorPopup("Бонусы", "Сумма списания должна быть больше 0.");
                return false;
            }

            if (amountToApply > maxAllowed)
            {
                viewManager.ShowErrorPopup(
                    "Бонусы",
                    $"Сумма списания ({amountToApply:N2}) превышает допустимую ({maxAllowed:N2}).");
                return false;
            }

            return true;
        }

        // ── Pay: финальная проверка + API pay-by-bonus (без повторного UI) ──
        public void Pay(decimal sum, IOrder order, IPaymentItem paymentItem, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IOperationService operationService,
            IReceiptPrinter printer, IViewManager viewManager, IPaymentDataContext context)
        {
            if (order == null)
                throw new PaymentActionFailedException("Заказ не найден.", false);

            if (sum <= 0)
                throw new PaymentActionFailedException("Сумма оплаты должна быть больше 0.", false);

            var oid = order.Id.ToString();
            if (!_tracker.TryGetOrder(oid, out var state) || state.Card == null)
            {
                throw new PaymentActionFailedException(
                    "Гость не привязан. Оплата Bonoos недоступна.", false);
            }

            var balance = (state.AvailableBonusKopecks ?? 0) / 100m;
            var maxAllowed = LoyaltyPaymentHelper.GetMaxBonusAllowed(order, balance, paymentItem);
            if (sum > maxAllowed)
            {
                throw new PaymentActionFailedException(
                    $"Сумма списания ({sum:N2}) превышает допустимую ({maxAllowed:N2}).", false);
            }

            var ok = RunSync(_tracker.PayByBonusAsync(
                oid, order.Number.ToString(), SdkMap.Items(order), state.Card, SdkMap.Money(sum)));

            if (!ok)
                throw new PaymentActionFailedException("Не удалось списать бонусы в системе лояльности.", false);

            state.ReservedTransactionId = transactionId.ToString();
            _log($"Bonoos: [LOYALTY] Pay ok sum={sum:N2} order=#{order.Number}");
        }

        public void OnPaymentDeleting(IOrder order, IPaymentItem paymentItem, IUser cashier,
            IOperationService operations, IReceiptPrinter printer, IViewManager viewManager,
            IPaymentDataContext context)
        {
            if (order == null)
                return;
            // Удалить оплату и ввести заново — можно. Снимаем фиксацию суммы + резерв.
            _tracker.ClearLockedBonusPaymentSum(order.Id.ToString());
            CancelReservation(order.Id);
        }

        /// <summary>Запрет правки предварительной оплаты с нумпада (как Irbis/Sagi).</summary>
        public bool OnPreliminaryPaymentEditing(IOrder order, IPaymentItem paymentItem, IUser cashier,
            IOperationService operationService, IReceiptPrinter printer, IViewManager viewManager,
            IPaymentDataContext context) => false;

        public void EmergencyCancelPayment(decimal sum, Guid? orderId, Guid paymentTypeId,
            Guid transactionId, IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer,
            IViewManager viewManager, IPaymentDataContext context)
            => CancelReservation(orderId);

        public void EmergencyCancelPaymentSilently(decimal sum, Guid? orderId, Guid paymentTypeId,
            Guid transactionId, IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer,
            IPaymentDataContext context)
            => CancelReservation(orderId);

        public void ReturnPayment(decimal sum, Guid? orderId, Guid paymentTypeId, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer, IViewManager viewManager,
            IPaymentDataContext context)
            => throw new PaymentActionFailedException("Возврат бонусов пока не поддерживается.", false);

        public void ReturnPaymentSilently(decimal sum, Guid? orderId, Guid paymentTypeId, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer, IPaymentDataContext context)
            => throw new PaymentActionFailedException("Возврат бонусов пока не поддерживается.", false);

        public void ReturnPaymentWithoutOrder(decimal sum, Guid? orderId, Guid paymentTypeId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer, IViewManager viewManager)
            => throw new PaymentActionFailedException("Возврат бонусов без заказа не поддерживается.", false);

        public bool CanPaySilently(decimal sum, Guid? orderId, Guid paymentTypeId, IPaymentDataContext context)
            => false;

        public void PaySilently(decimal sum, IOrder order, IPaymentItem paymentItem, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer, IPaymentDataContext context)
            => throw new PaymentActionFailedException("Тихая оплата Bonoos не поддерживается.", false);

        public void Dispose() { }

        /// <summary>
        /// Гость для списания бонусов: память → JSON.
        /// DISCOUNT → denyReason с текстом про скидку (не «привяжите гостя»).
        /// </summary>
        private bool TryGetLoyaltyForBonusPay(
            Guid orderId,
            IOrder order,
            out OrderLoyaltyState state,
            out string denyReason)
        {
            state = null;
            denyReason = null;
            var oid = orderId.ToString();

            if (!_tracker.TryGetOrder(oid, out state) || state.Card == null)
                TryRestoreGuestFromJson(oid, order, out state);

            if (IsDiscountGuest(state, oid))
            {
                denyReason = MsgDiscountNoBonus;
                _log($"Bonoos: [LOYALTY] bonus denied — DISCOUNT guest order=#{order?.Number}");
                return false;
            }

            if (state?.Card == null)
            {
                denyReason = MsgGuestNotBound;
                return false;
            }

            return true;
        }

        private void TryRestoreGuestFromJson(string orderId, IOrder order, out OrderLoyaltyState state)
        {
            state = null;
            if (!OrderGuestStore.TryGet(orderId, out var snap) || snap?.Card == null)
                return;

            state = _tracker.GetOrCreateOrder(orderId, order?.Number.ToString() ?? snap.OrderNumber?.ToString());
            state.Card = snap.Card;
            state.CardType = snap.CardType;
            state.DiscountPercent = snap.DiscountPercent;
            state.AvailableBonusKopecks = snap.BalanceKopecks;
            state.BalanceDisplay = snap.BalanceDisplay;
            state.CashbackPercent = snap.CashbackPercent;
            state.GuestDisplayName = snap.GuestDisplayName;
            state.BoundAtUtc = snap.BoundAtUtc;
            state.LastLookupAtUtc = snap.LastLookupAtUtc;
            _log($"Bonoos: [LOYALTY] guest restored from JSON for pay order=#{order?.Number} type={snap.CardType}");
        }

        private static bool IsDiscountGuest(OrderLoyaltyState state, string orderId)
        {
            if (state != null && state.IsDiscountCard)
                return true;

            if (OrderGuestStore.TryGet(orderId, out var snap) &&
                string.Equals(snap.CardType, "DISCOUNT", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private void CancelBonusPaymentAddition(
            IOperationService operationService, IOrder order, IPaymentItem paymentItem)
        {
            PluginContext.Log.Info($"Bonoos: [LOYALTY] OnPaymentAdded: сбор бонусов отменён order=#{order?.Number}");
            if (order != null)
                _tracker.ClearLockedBonusPaymentSum(order.Id.ToString());
            DeletePaymentQuietly(operationService, order, paymentItem);
        }

        private static void DeletePaymentQuietly(
            IOperationService operationService, IOrder order, IPaymentItem paymentItem)
        {
            try
            {
                operationService.DeletePaymentItem(
                    paymentItem,
                    operationService.GetOrderById(order.Id),
                    operationService.GetDefaultCredentials());
            }
            catch (Exception ex)
            {
                PluginContext.Log.Warn($"Bonoos: [LOYALTY] DeletePaymentItem: {ex.Message}");
            }
        }

        /// <summary>
        /// Отказ без throw: удалить строку оплаты + ShowErrorPopup.
        /// PaymentActionFailedException в CollectData/отладчике рвёт VS и зависает «Сбор данных».
        /// </summary>
        private void DenyBonusPaymentUi(
            IOrder order,
            IPaymentItem paymentItem,
            IOperationService operationService,
            IViewManager viewManager,
            string message)
        {
            _log($"Bonoos: [LOYALTY] deny UI: {message.Replace("\n", " | ")}");
            if (order != null)
                _tracker.ClearLockedBonusPaymentSum(order.Id.ToString());
            DeletePaymentQuietly(operationService, order, paymentItem);
            try
            {
                viewManager?.ShowErrorPopup("Bonoos", message);
            }
            catch (Exception ex)
            {
                _log($"Bonoos: [LOYALTY] ShowErrorPopup failed: {ex.Message}");
                try
                {
                    PluginContext.Operations.AddNotificationMessage(message, "Bonoos", TimeSpan.FromSeconds(6));
                }
                catch { /* ignore */ }
            }
        }

        private void RejectBonusPayment(
            IOrder order, IPaymentItem paymentItem, IOperationService operationService, string message)
        {
            PluginContext.Log.Warn($"Bonoos: [LOYALTY] rejected: {message}");
            if (order != null)
                _tracker.ClearLockedBonusPaymentSum(order.Id.ToString());
            DeletePaymentQuietly(operationService, order, paymentItem);
            throw new PaymentActionFailedException(message, false);
        }

        private void CancelReservation(Guid? orderId)
        {
            var oid = orderId?.ToString();
            if (!string.IsNullOrEmpty(oid))
                RunSync(_tracker.CancelBonusAsync(oid));
        }

        private static T RunSync<T>(Task<T> task)
        {
            try { return task.ConfigureAwait(false).GetAwaiter().GetResult(); }
            catch { return default; }
        }

        private static void RunSync(Task task)
        {
            try { task.ConfigureAwait(false).GetAwaiter().GetResult(); }
            catch { /* best-effort */ }
        }
    }
}
