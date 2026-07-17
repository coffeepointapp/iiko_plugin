using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Bonoos.iikoFront.LoyaltyPlugin.Models;

// ─────────────────────────────────────────────────────────────────────────────
//  SDK SEAM FILE #2. Implements iiko's external payment system so "Bonoos" shows
//  in iikoOffice under  Внешний тип оплаты → Безналичный тип.
//
//  V9 interface is Resto.Front.Api.IPaymentProcessor (NOT IExternalPaymentProcessor,
//  which was the v6 name). RegisterPaymentSystem(IPaymentProcessor, ...) takes it.
//  iikoFront calls:
//      Pay()                    → /order/pay-by-bonus/     (reserve / spend)
//      EmergencyCancelPayment() → /order/pay-by-bonus/cancel/
//      ReturnPayment()          → refund (Phase 1.5 — not on backend yet)
//  Business logic lives in the SDK-free OrderTracker; we only translate here.
// ─────────────────────────────────────────────────────────────────────────────

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
    // Plugins are marshaled across the API boundary, so the processor is a
    // MarshalByRefObject (matches the SDK sample).
    public sealed class BonoosPaymentProcessor : MarshalByRefObject, IPaymentProcessor, IDisposable
    {
        private readonly OrderTracker _tracker;
        private readonly Action<string> _log;

        public BonoosPaymentProcessor(OrderTracker tracker, Action<string> log)
        {
            _tracker = tracker;
            _log = log ?? (_ => { });
        }

        public string PaymentSystemKey => "bonoos-loyalty";
        public string PaymentSystemName => "Bonoos";

        // ───────────────────────── DEBIT: reserve/spend bonus ─────────────────────────
        // NOTE V9: Pay receives the full IOrder (not just an id) and IOperationService.
        public void Pay(decimal sum, IOrder order, IPaymentItem paymentItem, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IOperationService operationService,
            IReceiptPrinter printer, IViewManager viewManager, IPaymentDataContext context)
        {
            if (order == null)
                throw new PaymentActionFailedException("Оплата бонусами доступна только внутри заказа.");

            var oid = order.Id.ToString();
            if (!_tracker.TryGetOrder(oid, out var state) || state.Card == null)
                throw new PaymentActionFailedException("Сначала отсканируйте карту клиента.");

            var ok = RunSync(_tracker.PayByBonusAsync(
                oid, order.Number.ToString(), MapItems(order), state.Card, Rub(sum)));

            if (!ok)
                throw new PaymentActionFailedException("Не удалось списать бонусы.");

            state.ReservedTransactionId = transactionId.ToString();
            _log($"Bonoos: pay-by-bonus ok order={oid} sum={Rub(sum)}");
        }

        // ───────────────────────── CANCEL / REFUND ─────────────────────────
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
            => throw new PaymentActionFailedException("Возврат бонусов пока не поддерживается.");

        // ⚠ V9 added this. Signature inferred (Silently drops IViewManager) — if VS still
        //   reports it missing, the params differ: use "Implement Interface" (Ctrl+.).
        public void ReturnPaymentSilently(decimal sum, Guid? orderId, Guid paymentTypeId, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer, IPaymentDataContext context)
            => throw new PaymentActionFailedException("Возврат бонусов пока не поддерживается.");

        public void ReturnPaymentWithoutOrder(decimal sum, Guid? orderId, Guid paymentTypeId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer, IViewManager viewManager)
            => throw new PaymentActionFailedException("Возврат бонусов без заказа не поддерживается.");

        // ───────────────────────── Silent path (unused) ─────────────────────────
        public bool CanPaySilently(decimal sum, Guid? orderId, Guid paymentTypeId, IPaymentDataContext context)
            => false;

        public void PaySilently(decimal sum, IOrder order, IPaymentItem paymentItem, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IReceiptPrinter printer, IPaymentDataContext context)
            => throw new PaymentActionCancelledException();

        // ───────────────────────── Optional hooks ─────────────────────────
        public void CollectData(Guid orderId, Guid paymentTypeId, IUser cashier,
            IReceiptPrinter printer, IViewManager viewManager, IPaymentDataContext context)
        {
        }

        public void OnPaymentAdded(IOrder order, IPaymentItem paymentItem, IUser cashier,
            IOperationService operations, IReceiptPrinter printer, IViewManager viewManager,
            IPaymentDataContext context)
        {
        }

        // ⚠ V9 added this (bonus tender removed before close). Signature inferred from
        //   OnPaymentAdded — verify via "Implement Interface" (Ctrl+.) if reported missing.
        public void OnPaymentDeleting(IOrder order, IPaymentItem paymentItem, IUser cashier,
            IOperationService operations, IReceiptPrinter printer, IViewManager viewManager,
            IPaymentDataContext context)
        {
            if (order != null)
                CancelReservation(order.Id);
        }

        public bool OnPreliminaryPaymentEditing(IOrder order, IPaymentItem paymentItem, IUser cashier,
            IOperationService operationService, IReceiptPrinter printer, IViewManager viewManager,
            IPaymentDataContext context)
            => true;

        public void Dispose() { }

        // ───────────────────────── helpers ─────────────────────────
        private void CancelReservation(Guid? orderId)
        {
            var oid = orderId?.ToString();
            if (!string.IsNullOrEmpty(oid))
                RunSync(_tracker.CancelBonusAsync(oid));
        }

        // IProduct (Resto.Front.Api.Data.Assortment) has no `Code`. We send `Number`
        // (article/SKU); swap to it.Product.FastCode if you prefer the cashier quick-code.
        // ⚠ Still verify item value props (.Amount / .Price) via IntelliSense if they error.
        private static List<OrderItemDto> MapItems(IOrder order)
        {
            return order.Items.OfType<IOrderProductItem>().Select(it => new OrderItemDto
            {
                Id = it.Id.ToString(),
                Product = new ProductInfo
                {
                    Id = it.Product.Id.ToString(),
                    Code = Convert.ToString(it.Product.Number, CultureInfo.InvariantCulture),
                    Name = it.Product.Name,
                },
                Price = Rub(it.Price),
                Quantity = it.Amount.ToString("F3", CultureInfo.InvariantCulture),
                Sum = Rub(it.Price * it.Amount),
                Comment = "",
            }).ToList();
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
