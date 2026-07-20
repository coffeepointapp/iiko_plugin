using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bonoos.iikoFront.LoyaltyPlugin.Models;
using Resto.Front.Api.Data.Orders;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Maps iiko SDK order objects to the Bonoos API DTOs. Shared by the payment
    /// processor (spend) and the plugin's order-close hook (confirm/cashback).
    /// Money is emitted with InvariantCulture so a ru-RU Windows doesn't produce
    /// "900,00" and break the JSON decimal.
    /// </summary>
    internal static class SdkMap
    {
        public static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

        public static List<OrderItemDto> Items(IOrder order)
        {
            return order.Items.OfType<IOrderProductItem>().Select(it => new OrderItemDto
            {
                Id = it.Id.ToString(),
                Product = new ProductInfo
                {
                    Id = it.Product.Id.ToString(),
                    // IProduct has no Code — Number is the article/SKU (FastCode is the quick-code).
                    Code = Convert.ToString(it.Product.Number, CultureInfo.InvariantCulture),
                    Name = it.Product.Name,
                },
                Price = Money(it.Price),
                Quantity = it.Amount.ToString("F3", CultureInfo.InvariantCulture),
                Sum = Money(it.Price * it.Amount),
                Comment = "",
            }).ToList();
        }

        public static List<PaymentDto> Payments(IOrder order)
        {
            return order.Payments.Select(p => new PaymentDto
            {
                Id = p.Id.ToString(),
                PaymentType = new PaymentTypeInfo
                {
                    Id = p.PaymentType.Id.ToString(),
                    Name = p.PaymentType.Name,
                    // Backend classifies the bonus tender by payment_type.id (tenant config),
                    // so we just relay iiko's native kind.
                    Kind = p.PaymentType.Kind.ToString().ToLowerInvariant(),
                },
                Sum = Money(p.Sum),
            }).ToList();
        }
    }
}
