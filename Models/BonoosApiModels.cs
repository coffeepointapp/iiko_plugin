using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Bonoos.iikoFront.LoyaltyPlugin.Models
{
    // ────────────────────────────── Common building blocks ──────────────────────────────

    public class CardInfo
    {
        [JsonProperty("track")]
        public string Track { get; set; }

        [JsonProperty("number")]
        public string Number { get; set; }
    }

    public class ProductInfo
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
    }

    public class OrderItemDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("product")]
        public ProductInfo Product { get; set; }

        [JsonProperty("price")]
        public string Price { get; set; }

        [JsonProperty("quantity")]
        public string Quantity { get; set; }

        [JsonProperty("sum")]
        public string Sum { get; set; }

        [JsonProperty("comment")]
        public string Comment { get; set; }
    }

    public class PaymentTypeInfo
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("kind")]
        public string Kind { get; set; }
    }

    public class PaymentDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("payment_type")]
        public PaymentTypeInfo PaymentType { get; set; }

        [JsonProperty("sum")]
        public string Sum { get; set; }
    }

    // ────────────────────────────── 6.1  POST /client/lookup/ ──────────────────────────────

    public class ClientLookupRequest
    {
        [JsonProperty("card")]
        public CardInfo Card { get; set; }
    }

    public class ClientLookupResponse
    {
        [JsonProperty("found")]
        public bool Found { get; set; }

        [JsonProperty("card_id")]
        public string CardId { get; set; }

        [JsonProperty("client_profile_id")]
        public string ClientProfileId { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("first_name")]
        public string FirstName { get; set; }

        [JsonProperty("last_name")]
        public string LastName { get; set; }

        [JsonProperty("balance_kopecks")]
        public int? BalanceKopecks { get; set; }

        [JsonProperty("balance_display")]
        public string BalanceDisplay { get; set; }

        [JsonProperty("cashback_percent")]
        public int? CashbackPercent { get; set; }

        /// <summary>
        /// LOYALTY_COINS | DISCOUNT | SUBSCRIPTION | … — branch pay/discount UX on this.
        /// </summary>
        [JsonProperty("card_type")]
        public string CardType { get; set; }

        [JsonProperty("discount_percent")]
        public int? DiscountPercent { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        public bool IsCashbackCard =>
            string.IsNullOrEmpty(CardType) ||
            string.Equals(CardType, "LOYALTY_COINS", StringComparison.OrdinalIgnoreCase);

        public bool IsDiscountCard =>
            string.Equals(CardType, "DISCOUNT", StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────── Common request body (precheck, pay-by-bonus, cancel, confirm) ──

    public class OrderRequestBase
    {
        [JsonProperty("order_id")]
        public string OrderId { get; set; }

        [JsonProperty("order_number")]
        public string OrderNumber { get; set; }

        [JsonProperty("items")]
        public List<OrderItemDto> Items { get; set; }

        [JsonProperty("card")]
        public CardInfo Card { get; set; }
    }

    public class PrecheckRequest : OrderRequestBase
    {
    }

    public class PayByBonusRequest : OrderRequestBase
    {
        [JsonProperty("amount")]
        public string Amount { get; set; }
    }

    public class CancelPayByBonusRequest : OrderRequestBase
    {
        [JsonProperty("amount")]
        public string Amount { get; set; }
    }

    public class ConfirmRequest : OrderRequestBase
    {
        [JsonProperty("payments")]
        public List<PaymentDto> Payments { get; set; }

        [JsonProperty("closed_at")]
        public string ClosedAt { get; set; }

        [JsonProperty("order_type")]
        public string OrderType { get; set; }

        [JsonProperty("reference_order_id")]
        public string ReferenceOrderId { get; set; }
    }

    // ────────────────────────────── Common response types ──────────────────────────────

    public class ClientInfo
    {
        /// <summary>
        /// OpenAPI: string for LOYALTY_COINS (e.g. "1400"), number 0 otherwise.
        /// Prefer lookup <c>balance_kopecks</c> for arithmetic.
        /// </summary>
        [JsonProperty("availableAmount")]
        [JsonConverter(typeof(FlexibleDecimalConverter))]
        public decimal? AvailableAmount { get; set; }

        [JsonProperty("mobilePhone")]
        public string MobilePhone { get; set; }

        [JsonProperty("email")]
        public string Email { get; set; }

        [JsonProperty("validationCode")]
        public string ValidationCode { get; set; }

        [JsonProperty("validationMessage")]
        public string ValidationMessage { get; set; }
    }

    public class DocumentPosition
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        // Kopecks. Typed decimal (not int) — the backend may emit fractional
        // values; an int? here would throw during deserialization and discard
        // the whole response.
        [JsonProperty("discountAmount")]
        public decimal? DiscountAmount { get; set; }

        [JsonProperty("paidAmount")]
        public decimal? PaidAmount { get; set; }
    }

    public class DocumentInfo
    {
        [JsonProperty("positions")]
        public List<DocumentPosition> Positions { get; set; }
    }

    public class DisplayText
    {
        [JsonProperty("text")]
        public string Text { get; set; }
    }

    public class FrontolResponse
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("client")]
        public ClientInfo Client { get; set; }

        [JsonProperty("validationCode")]
        public string ValidationCode { get; set; }

        [JsonProperty("validationMessage")]
        public string ValidationMessage { get; set; }

        [JsonProperty("document")]
        public DocumentInfo Document { get; set; }

        [JsonProperty("cashierInformation")]
        public List<DisplayText> CashierInformation { get; set; }

        [JsonProperty("customerInformation")]
        public List<DisplayText> CustomerInformation { get; set; }

        [JsonProperty("printingInformation")]
        public List<object> PrintingInformation { get; set; }
    }

    // ────────────────────────────── 6.5  POST /order/confirm/ response ──────────────────

    public class ConfirmResponse
    {
        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("duplicate")]
        public bool Duplicate { get; set; }

        [JsonProperty("fiscal_amount")]
        public int? FiscalAmount { get; set; }

        [JsonProperty("bonus_credit_amount")]
        public int? BonusCreditAmount { get; set; }

        [JsonProperty("bonus_debit_amount")]
        public int? BonusDebitAmount { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    // ────────────────────────────── 400 error response ─────────────────────────────

    public class ErrorResponse
    {
        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }
}
