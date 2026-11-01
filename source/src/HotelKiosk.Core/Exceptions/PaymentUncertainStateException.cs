using System;

namespace HotelKiosk.Core.Exceptions
{
    public class PaymentUncertainStateException : Exception
    {
        public string TransactionId { get; }
        public PaymentUncertainStateException(string transactionId, string message) : base(message)
        {
            TransactionId = transactionId;
        }
    }
}
