using System;
using HotelKiosk.Application.Options;
using HotelKiosk.Application.Services;
using Xunit;

namespace HotelKiosk.Tests
{
    public class PaymentPollingPolicyTests
    {
        private static CheckInStateMachineOptions Sm(int timeoutSec = 300, int pollSec = 3) => new()
        {
            PaymentCheckTimeoutSeconds = timeoutSec,
            PaymentPollIntervalSeconds = pollSec
        };

        [Fact]
        public void AlfaBankOptions_Win()
        {
            // Раньше здесь стояли хардкод 3 с и 5 мин, и AlfaBankQr:PollIntervalSeconds
            // / QrLifetimeMinutes не влияли ни на что
            var p = PaymentPollingPolicy.FromOptions(Sm(), qrPollIntervalSeconds: 7, qrLifetimeMinutes: 11);

            Assert.Equal(7, p.PollIntervalSeconds);
            Assert.Equal(11, p.DeadlineMinutes);
            Assert.Equal(TimeSpan.FromSeconds(7), p.PollInterval);
            Assert.Equal(TimeSpan.FromMinutes(11), p.Deadline);
        }

        [Fact]
        public void MissingAlfaBankOptions_FallsBackToStateMachine()
        {
            var p = PaymentPollingPolicy.FromOptions(Sm(timeoutSec: 240, pollSec: 6));

            Assert.Equal(6, p.PollIntervalSeconds);
            Assert.Equal(4, p.DeadlineMinutes);
        }

        [Fact]
        public void NonPositiveAlfaBankValues_FallBackToStateMachine()
        {
            var p = PaymentPollingPolicy.FromOptions(
                Sm(timeoutSec: 120, pollSec: 5), qrPollIntervalSeconds: 0, qrLifetimeMinutes: 0);

            Assert.Equal(5, p.PollIntervalSeconds);
            Assert.Equal(2, p.DeadlineMinutes);
        }

        [Fact]
        public void NullStateMachine_UsesBuiltInDefaults()
        {
            var p = PaymentPollingPolicy.FromOptions(null);

            Assert.Equal(3, p.PollIntervalSeconds);
            Assert.Equal(5, p.DeadlineMinutes);
        }

        [Fact]
        public void ZeroValues_AreClampedToOne()
        {
            // Иначе возможен бесконечный цикл опроса и нулевой дедлайн
            var p = PaymentPollingPolicy.FromOptions(new CheckInStateMachineOptions
            {
                PaymentPollIntervalSeconds = 0,
                PaymentCheckTimeoutSeconds = 0
            });

            Assert.Equal(1, p.PollIntervalSeconds);
            Assert.Equal(1, p.DeadlineMinutes);
        }

        [Fact]
        public void CardPollInterval_DiffersForCardPayments()
        {
            var p = PaymentPollingPolicy.FromOptions(Sm(), 3, 5, cardPollIntervalSeconds: 8);

            Assert.Equal(TimeSpan.FromSeconds(3), p.PollIntervalFor("qr"));
            Assert.Equal(TimeSpan.FromSeconds(8), p.PollIntervalFor("card"));
        }

        [Fact]
        public void CardPollInterval_FallsBackToQrInterval()
        {
            var p = PaymentPollingPolicy.FromOptions(Sm(), 4, 5);

            Assert.Equal(p.PollIntervalSeconds, p.CardPollIntervalSeconds);
            Assert.Equal(TimeSpan.FromSeconds(4), p.PollIntervalFor("card"));
        }

        [Fact]
        public void Default_HasUsableTimings()
        {
            var p = PaymentPollingPolicy.Default;

            Assert.True(p.PollIntervalSeconds > 0);
            Assert.True(p.DeadlineMinutes > 0);
        }
    }
}
