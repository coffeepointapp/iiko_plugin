using System.Collections.Generic;
using System.Text;
using Bonoos.iikoFront.LoyaltyPlugin.Models;
using Resto.Front.Api.Data.View;
using Resto.Front.Api.UI;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Модалки как в AuthorizationGuest (OrderPusher):
    /// ShowOkPopup / ShowErrorPopup / ShowOkCancelPopup / ShowChooserPopup / ShowExtendedInputDialog.
    /// </summary>
    internal sealed class LoyaltyUi
    {
        public bool AskIfOfferedBonuses(IViewManager vm) =>
            vm.ShowOkCancelPopup("Авторизация", "Предложили накопить баллы или потратить?", "Да", "Нет");

        public bool AskIfHelpedRegistration(IViewManager vm) =>
            vm.ShowOkCancelPopup("Регистрация", "Помогли пройти регистрацию гостя?", "Да", "Нет");

        public bool AskIfToldAdvantages(IViewManager vm) =>
            vm.ShowOkCancelPopup("Преимущества", "Рассказали про преимущества системы лояльности?", "Да", "Нет");

        public string AskSkipReason(IViewManager vm)
        {
            var reasons = new List<string>
            {
                "Гость отказался давать номер",
                "У гостя не установлено приложение",
                "Забыл уточнить номер",
                "Не хотел регистрироваться или пользоваться системой лояльности",
            };
            int index = vm.ShowChooserPopup("Причины", reasons);
            return index < 0 ? null : reasons[index];
        }

        /// <summary>Как OrderPusher: ввод телефона через ExtendedInputDialog.</summary>
        public string ShowPhoneSearchInput(IViewManager vm)
        {
            var result = vm.ShowExtendedInputDialog(
                "Поиск клиента",
                "Введите номер телефона",
                new ExtendedInputDialogSettings { NumericInputMode = NumericInputMode.String });
            return (result as StringInputDialogResult)?.Result;
        }

        /// <summary>QR / свайп / набор — для экрана оплаты, если нужен скан карты.</summary>
        public CardInfo CaptureCardOrPhone(IViewManager vm)
        {
            var result = vm.ShowExtendedKeyboardDialog(
                "Карта лояльности / телефон",
                isMultiline: false,
                enableCardSlider: true,
                enableBarcode: true);
            return ScannerInput.FromDialogResult(result);
        }

        public void ShowError(IViewManager vm, string title, string message)
        {
            if (vm == null || string.IsNullOrWhiteSpace(message)) return;
            vm.ShowErrorPopup(title, message);
        }

        public void ShowSuccess(IViewManager vm, string title, string message)
        {
            if (vm == null || string.IsNullOrWhiteSpace(message)) return;
            vm.ShowOkPopup(title, message);
        }

        public void ShowClientCard(IViewManager vm, ClientLookupResponse resp)
        {
            if (vm == null || resp == null) return;

            if (!resp.Found)
            {
                ShowError(vm, "Лояльность", string.IsNullOrWhiteSpace(resp.Message) ? "Клиент не найден." : resp.Message);
                return;
            }

            ShowSuccess(vm, "Лояльность", FormatClientCard(resp));
        }

        public static string FormatClientCard(ClientLookupResponse resp)
        {
            if (resp == null || !resp.Found)
                return resp?.Message ?? "Клиент не найден.";

            var name = !string.IsNullOrWhiteSpace(resp.FirstName)
                ? $"{resp.FirstName} {resp.LastName}".Trim()
                : (string.IsNullOrWhiteSpace(resp.Username) ? "Клиент" : resp.Username);

            var sb = new StringBuilder();
            sb.AppendLine($"Клиент: {name}");

            if (resp.IsDiscountCard)
            {
                sb.AppendLine($"Скидочная карта: {resp.DiscountPercent ?? 0}%");
            }
            else
            {
                sb.AppendLine($"Баланс: {resp.BalanceDisplay ?? "0"}");
                sb.AppendLine($"Уровень кэшбэка: {resp.CashbackPercent ?? 0}%");
            }

            if (!string.IsNullOrWhiteSpace(resp.CardType))
                sb.AppendLine($"Тип карты: {resp.CardType}");

            return sb.ToString().TrimEnd();
        }
    }
}
