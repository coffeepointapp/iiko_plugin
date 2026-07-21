using System;
using Resto.Front.Api;

namespace Bonoos.iikoFront.LoyaltyPlugin.Services
{
    /// <summary>
    /// Демо-срок: плагин работает включительно до 25.07.2026.
    /// После этой даты — idle (без регистрации ПС / кнопок).
    /// Один файл — потом можно заменить на нормальную лицензию.
    /// </summary>
    internal static class DemoLicense
    {
        /// <summary>Последний день, когда демо ещё работает (локальная дата кассы).</summary>
        public static readonly DateTime ExpiresOnInclusive = new DateTime(2026, 7, 25);

        public static bool IsActive => DateTime.Today <= ExpiresOnInclusive.Date;

        public static string ExpiredMessage =>
            "Демо-период Bonoos закончился (до 25.07.2026).\n" +
            "Обратитесь к поставщику для активации полной версии.";

        /// <summary>
        /// true = можно стартовать. false = срок вышел (лог + уведомление кассиру).
        /// </summary>
        public static bool EnsureActive(Action<string> log)
        {
            if (IsActive)
            {
                log?.Invoke(
                    $"Bonoos: [DEMO] OK — действует до {ExpiresOnInclusive:dd.MM.yyyy} " +
                    $"(сегодня {DateTime.Today:dd.MM.yyyy})");
                return true;
            }

            log?.Invoke(
                $"Bonoos: [DEMO] EXPIRED — сегодня {DateTime.Today:dd.MM.yyyy}, " +
                $"срок был до {ExpiresOnInclusive:dd.MM.yyyy}. Плагин не запускается.");

            try
            {
                PluginContext.Operations.AddErrorMessage(ExpiredMessage, "Bonoos");
            }
            catch
            {
                try
                {
                    PluginContext.Operations.AddNotificationMessage(
                        ExpiredMessage, "Bonoos", TimeSpan.FromSeconds(12));
                }
                catch
                {
                    // Host ещё может быть не готов к UI — достаточно лога.
                }
            }

            return false;
        }
    }
}
