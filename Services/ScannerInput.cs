using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Bonoos.iikoFront.LoyaltyPlugin.Models;
using Resto.Front.Api.Data.View;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Scanner / dialog input parsing for Bonoos cards.
    /// Pattern taken from the working Sagi scanner (OrderEditBarcodeScanned /
    /// OrderEditCardSlided + reflection over CardInputDialogResult). OrderPusher
    /// itself does <b>not</b> subscribe to barcode events — only typed phone via
    /// ShowExtendedInputDialog — so live QR capture mirrors Sagi, not OrderPusher.
    /// </summary>
    internal static class ScannerInput
    {
        /// <summary>Strip control chars / ZWSP / BOM left by some scanners.</summary>
        public static string Normalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;
            var chars = raw.Trim()
                .Where(c => !char.IsControl(c) && c != '\u200B' && c != '\uFEFF')
                .ToArray();
            return new string(chars).Trim();
        }

        /// <summary>
        /// CardInputDialogResult.FullCardTrack is often empty on V9 — Sagi reads
        /// Track2 / Track1 / CardNumber / … via reflection instead.
        /// </summary>
        public static string ExtractCardPayload(CardInputDialogResult card)
        {
            if (card == null)
                return string.Empty;

            foreach (var propName in new[]
                     { "FullCardTrack", "Track2", "Track1", "CardNumber", "Barcode", "Value", "Data", "Card" })
            {
                try
                {
                    var prop = card.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
                    var value = prop?.GetValue(card)?.ToString();
                    value = Normalize(value);
                    if (!string.IsNullOrEmpty(value))
                        return value;
                }
                catch
                {
                    // try next property
                }
            }

            return Normalize(card.ToString());
        }

        /// <summary>
        /// Map raw scanner/dialog text to Bonoos <see cref="CardInfo"/>.
        /// UUID → card.track (Wallet QR). Digits (chat id / phone) → card.number.
        /// Product EANs (12/13/14) → null so iiko still gets the barcode.
        /// Also unwraps iiko KeyboardDriver wrappers:
        ///   &lt;barcode …/&gt;;UUID?barcode
        /// </summary>
        public static CardInfo TryParseToCard(string raw, out string reasonIfRejected)
        {
            reasonIfRejected = null;
            var text = UnwrapScannerPayload(Normalize(raw));
            if (string.IsNullOrEmpty(text))
            {
                reasonIfRejected = "empty";
                return null;
            }

            // Wallet QR encodes the card UUID directly (OpenAPI card.track).
            if (Guid.TryParse(text, out var guid))
                return new CardInfo { Track = guid.ToString() };

            // Digits: phone or Bonoos chat-id (OpenAPI card.number).
            var digits = Regex.Replace(text, @"[^\d]", "");
            if (digits.Length == 0)
            {
                reasonIfRejected = "not a card identifier";
                return null;
            }

            if (digits.Length == 12 || digits.Length == 13 || digits.Length == 14)
            {
                reasonIfRejected = $"product barcode len={digits.Length}";
                return null;
            }

            if (digits.Length >= 6 && digits.Length <= 16)
            {
                var number = digits;
                if (number.Length == 11 && number[0] == '8')
                    number = "7" + number.Substring(1);
                else if (number.Length == 10)
                    number = "7" + number;
                return new CardInfo { Number = number };
            }

            reasonIfRejected = $"unsupported len={digits.Length} text='{text}'";
            return null;
        }

        /// <summary>
        /// Pull a GUID / bare code out of iiko keyboard-wedge wrappers, e.g.
        /// &lt;barcode class="…KeyboardDriver"/&gt;;56562dc7-…?barcode
        /// </summary>
        private static string UnwrapScannerPayload(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var guidMatch = Regex.Match(
                text,
                @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (guidMatch.Success)
                return guidMatch.Value;

            // ;PAYLOAD?suffix  or  ;PAYLOAD
            var semi = text.LastIndexOf(';');
            if (semi >= 0 && semi + 1 < text.Length)
            {
                var after = text.Substring(semi + 1);
                var q = after.IndexOf('?');
                if (q >= 0)
                    after = after.Substring(0, q);
                after = after.Trim();
                if (!string.IsNullOrEmpty(after))
                    return after;
            }

            return text;
        }

        /// <summary>Map an ExtendedKeyboardDialog result to CardInfo (pay screen / gate).</summary>
        public static CardInfo FromDialogResult(IInputDialogResult result)
        {
            switch (result)
            {
                case CardInputDialogResult card:
                    return TryParseToCard(ExtractCardPayload(card), out _);
                case BarcodeInputDialogResult barcode:
                    return TryParseToCard(barcode.Barcode, out _);
                case StringInputDialogResult typed:
                    return TryParseToCard(typed.Result, out _);
                default:
                    return null;
            }
        }
    }
}
