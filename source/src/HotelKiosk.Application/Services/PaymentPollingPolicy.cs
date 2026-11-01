using System;
using HotelKiosk.Application.Options;

namespace HotelKiosk.Application.Services
{
    /// <summary>
    /// Тайминги фонового опроса оплаты: как часто дёргаем банк/пинпад и когда
    /// перестаём ждать.
    ///
    /// Зачем отдельный тип: значения раньше были захардкожены в трёх местах
    /// (удалённый мост — 5 минут и 3 секунды, kiosk.html — PAY_POLL_MS и
    /// PAY_DEADLINE_MS, CheckInStateMachineOptions — PaymentCheck*Seconds),
    /// из-за чего смена AlfaBankQr:PollIntervalSeconds в конфиге ни на что
    /// не влияла. Теперь источник один — конфиг, а JS получает те же числа
    /// при старте через KIOSK_CONFIG, чтобы фронтенд и бэкенд не расходились.
    /// </summary>
    public sealed class PaymentPollingPolicy
    {
        /// <summary>Интервал между проверками статуса платежа, секунды.</summary>
        public int PollIntervalSeconds { get; init; } = 3;

        /// <summary>
        /// Интервал опроса статуса при оплате картой (CardTerminal:PollIntervalSeconds).
        /// Отличается от QR: запрос идёт к локальному агенту пинпада, а не к шлюзу банка.
        /// </summary>
        public int CardPollIntervalSeconds { get; init; } = 3;

        /// <summary>Сколько ждать подтверждения оплаты до отмены QR-заказа, минуты.</summary>
        public int DeadlineMinutes { get; init; } = 5;

        /// <summary>Интервал опроса для указанного способа оплаты.</summary>
        public TimeSpan PollIntervalFor(string paymentMethod) =>
            TimeSpan.FromSeconds(paymentMethod == "card"
                ? CardPollIntervalSeconds
                : PollIntervalSeconds);

        public TimeSpan PollInterval => TimeSpan.FromSeconds(PollIntervalSeconds);

        public TimeSpan Deadline => TimeSpan.FromMinutes(DeadlineMinutes);

        /// <summary>Значения по умолчанию, если конфиг не задан.</summary>
        public static PaymentPollingPolicy Default { get; } = new();

        /// <summary>
        /// Собирает политику из опций состояния и эквайринга.
        /// Приоритет у AlfaBankQr — это фактический срок жизни QR у банка;
        /// CheckInStateMachine используется как запасной вариант, если секции
        /// AlfaBankQr в конфиге нет.
        /// </summary>
        public static PaymentPollingPolicy FromOptions(
            CheckInStateMachineOptions stateMachine,
            int? qrPollIntervalSeconds = null,
            int? qrLifetimeMinutes = null,
            int? cardPollIntervalSeconds = null)
        {
            var poll = qrPollIntervalSeconds is > 0 ? qrPollIntervalSeconds.Value
                : (stateMachine?.PaymentPollIntervalSeconds ?? 3);
            var minutes = qrLifetimeMinutes is > 0 ? qrLifetimeMinutes.Value
                : (stateMachine?.PaymentCheckTimeoutSeconds / 60 ?? 5);
            var cardPoll = cardPollIntervalSeconds is > 0 ? cardPollIntervalSeconds.Value : poll;

            return new PaymentPollingPolicy
            {
                PollIntervalSeconds = Math.Max(1, poll),
                CardPollIntervalSeconds = Math.Max(1, cardPoll),
                DeadlineMinutes = Math.Max(1, minutes)
            };
        }
    }
}
