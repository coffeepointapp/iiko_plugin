using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Bonoos.iikoFront.LoyaltyPlugin.Models;

// ─────────────────────────────────────────────────────────────────────────────
//  SDK SEAM FILE #2 (the other is Plugin.cs).
//
//  Implements iiko's external payment system so "Bonoos" appears in iikoOffice
//  under  Внешний тип оплаты → Безналичный тип.  iikoFront then calls these
//  methods at the payment lifecycle points, and we forward to the SDK-free
//  OrderTracker → Bonoos API:
//
//      Pay()                    → /order/pay-by-bonus/     (reserve / spend)
//      EmergencyCancelPayment() → /order/pay-by-bonus/cancel/  (unclosed order)
//      ReturnPayment()          → refund  (Phase 1.5 — not on backend yet)
//
//  ⚠ The IExternalPaymentProcessor signatures below follow the published v6
//    contract. iiko RMS 9 (Resto.Front.Api V9) may differ — VERIFY every
//    method signature, type, and namespace against the SDK on the VM. The
//    business logic (what we call on OrderTracker) does not change even if the
//    signatures do; keep the RunSync(...) delegations and re-shape the params.
// ─────────────────────────────────────────────────────────────────────────────

// ⚠ SEAM — SDK namespaces. Adjust to your V9 build.
using Resto.Front.Api;
using Resto.Front.Api.Data.Orders;
using Resto.Front.Api.Data.Security;
using Resto.Front.Api.Devices;
using Resto.Front.Api.UI;
using Resto.Front.Api.Exceptions;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    public class BonoosPaymentProcessor : IExternalPaymentProcessor
    {
        private readonly OrderTracker _tracker;
        private readonly Action<string> _log;

        public BonoosPaymentProcessor(OrderTracker tracker, Action<string> log)
        {
            _tracker = tracker;
            _log = log ?? (_ => { });
        }

        // Stable key (never shown) + display name (shown in iikoOffice "Безналичный тип").
        public string PaymentSystemKey => "bonoos-loyalty";
        public string PaymentSystemName => "Bonoos";

        // ───────────────────────── DEBIT: reserve/spend bonus ─────────────────────────

        public void Pay(decimal sum, Guid? orderId, Guid paymentTypeId, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer,
            IViewManager viewManager, IPaymentDataContext context, IProgressBar progressBar)
        {
            var oid = orderId?.ToString();
            if (string.IsNullOrEmpty(oid))
                throw new PaymentActionFailedException("Оплата бонусами доступна только внутри заказа.");

            if (!_tracker.TryGetOrder(oid, out var state) || state.Card == null)
                throw new PaymentActionFailedException("Сначала отсканируйте карту клиента.");

            var ok = RunSync(_tracker.PayByBonusAsync(
                oid, state.OrderNumber,
                state.LastItems ?? new List<OrderItemDto>(),
                state.Card,
                Rub(sum)));

            if (!ok)
                throw new PaymentActionFailedException("Не удалось списать бонусы.");

            state.ReservedTransactionId = transactionId.ToString();
            _log($"Bonoos: pay-by-bonus ok order={oid} sum={Rub(sum)}");
        }

        // ───────────────────────── CANCEL / REFUND ─────────────────────────

        // Unclosed order — cashier removed the payment / fiscal issue. Release the reservation.
        public void EmergencyCancelPayment(decimal sum, Guid? orderId, Guid paymentTypeId,
            Guid transactionId, IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer,
            IViewManager viewManager, IPaymentDataContext context, IProgressBar progressBar)
        {
            CancelReservation(orderId);
        }

        public void EmergencyCancelPaymentSilently(decimal sum, Guid? orderId, Guid paymentTypeId,
            Guid transactionId, IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer,
            IPaymentDataContext context)
        {
            CancelReservation(orderId);
        }

        // Closed-order reversal (return of goods) — reverses a committed bonus spend.
        // Backend refund is Phase 1.5; fail loudly rather than silently mis-handle money.
        public void ReturnPayment(decimal sum, Guid? orderId, Guid paymentTypeId, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer,
            IViewManager viewManager, IPaymentDataContext context, IProgressBar progressBar)
        {
            throw new PaymentActionFailedException("Возврат бонусов пока не поддерживается.");
        }

        public void ReturnPaymentWithoutOrder(decimal sum, Guid paymentTypeId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer,
            IViewManager viewManager, IProgressBar progressBar)
        {
            throw new PaymentActionFailedException("Возврат бонусов без заказа не поддерживается.");
        }

        // ───────────────────────── Silent path (unused) ─────────────────────────
        // Bonus payment needs a card + network, so we never run silently.

        public bool CanPaySilently(decimal sum, Guid? orderId, Guid paymentTypeId, IPaymentDataContext context)
            => false;

        public void PaySilently(decimal sum, Guid? orderId, Guid paymentTypeId, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer, IPaymentDataContext context)
            => throw new PaymentActionCancelledException();

        // ───────────────────────── Optional hooks ─────────────────────────

        // Called when the tender is added; the amount UI is iiko's. We could pre-fetch
        // the max spendable here (precheck) — left as a no-op for the first cut.
        public void CollectData(Guid orderId, Guid paymentTypeId, IUser cashier,
            IReceiptPrinter printer, IViewManager viewManager,
            IPaymentDataContext context, IProgressBar progressBar)
        {
        }

        public void OnPaymentAdded(IOrder order, IPaymentItem paymentItem, IUser cashier,
            IOperationService operationService, IReceiptPrinter printer,
            IViewManager viewManager, IPaymentDataContext context, IProgressBar progressBar)
        {
        }

        // Whether the bonus amount is editable in the prepayment screen — yes.
        public bool OnPreliminaryPaymentEditing(IOrder order, IPaymentItem paymentItem,
            IUser cashier, IOperationService operationService, IReceiptPrinter printer,
            IViewManager viewManager, IPaymentDataContext context, IProgressBar progressBar)
            => true;

        // ───────────────────────── SDK-free helpers ─────────────────────────

        private void CancelReservation(Guid? orderId)
        {
            var oid = orderId?.ToString();
            if (!string.IsNullOrEmpty(oid))
                RunSync(_tracker.CancelBonusAsync(oid));
        }

        // Invariant culture: a ru-RU Windows would otherwise emit "900,00" and break the JSON decimal.
        private static string Rub(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

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
