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
        // Pay MUST stay synchronous (an async Pay crashes the processor — SDK issue #13);
        // we call the backend blocking via RunSync. V9 Pay gets the full IOrder + IViewManager.
        public void Pay(decimal sum, IOrder order, IPaymentItem paymentItem, Guid transactionId,
            IPointOfSale pointOfSale, IUser cashier, IOperationService operationService,
            IReceiptPrinter printer, IViewManager viewManager, IPaymentDataContext context)
        {
            if (order == null)
                throw new PaymentActionFailedException("Оплата бонусами доступна только внутри заказа.");

            var oid = order.Id.ToString();
            var state = _tracker.GetOrCreateOrder(oid, order.Number.ToString());

            // The card may already be bound (cashier swiped it while editing the order,
            // via OrderEditCardSlided). If not, prompt on the payment screen — V9 has no
            // payment-screen scan event, so the dialog is the path here.
            if (state.Card == null)
            {
                var card = PromptForCard(viewManager);
                if (card == null)
                    throw new PaymentActionCancelledException(); // cashier closed the dialog

                var lookup = RunSync(_tracker.LookupClientAsync(oid, order.Number.ToString(), card));
                if (lookup == null || !lookup.Found)
                    throw new PaymentActionFailedException("Карта не найдена в системе лояльности.");
                // LookupClientAsync bound the card to state already.
            }

            // Cap: never spend more bonus than the client has. iiko owns the amount
            // numpad on the payment screen (Pay receives `sum`), so we can't pre-limit
            // the input — instead reject an over-amount with the exact max. The backend
            // is the authoritative guard; this just fails fast with a clear message.
            if (state.AvailableBonusKopecks is int availKopecks && availKopecks >= 0)
            {
                var sumKopecks = (long)Math.Round(sum * 100m);
                if (sumKopecks > availKopecks)
                    throw new PaymentActionFailedException(
                        $"Недостаточно бонусов. Доступно: {availKopecks / 100m:0.##} ₽");
            }

            var ok = RunSync(_tracker.PayByBonusAsync(
                oid, order.Number.ToString(), SdkMap.Items(order), state.Card, SdkMap.Money(sum)));

            if (!ok)
                throw new PaymentActionFailedException("Не удалось списать бонусы.");

            state.ReservedTransactionId = transactionId.ToString();
            PluginContext.Operations.AddNotificationMessage($"Списано {SdkMap.Money(sum)} ₽ бонусами", "Bonoos");
            _log($"Bonoos: pay-by-bonus ok order={oid} sum={SdkMap.Money(sum)}");
        }

        // Prompts the cashier for the loyalty card on the payment screen. The dialog
        // accepts a swipe (card slider), a scanned QR/barcode, or a typed number.
        private static CardInfo PromptForCard(IViewManager viewManager)
        {
            var result = viewManager.ShowExtendedKeyboardDialog(
                "Карта лояльности", isMultiline: false, enableCardSlider: true, enableBarcode: true);
            switch (result)
            {
                case CardInputDialogResult card:      // swiped magnetic card
                    return new CardInfo { Track = card.FullCardTrack };
                case BarcodeInputDialogResult barcode: // scanned QR/barcode (Bonoos Wallet)
                    return new CardInfo { Track = barcode.Barcode };
                case StringInputDialogResult typed:    // typed on the keyboard (phone/number)
                    return new CardInfo { Number = typed.Result };
                default:
                    return null;                       // cashier cancelled (result is null)
            }
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
