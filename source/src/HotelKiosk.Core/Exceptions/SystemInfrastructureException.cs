using System;

namespace HotelKiosk.Core.Exceptions
{
    public class SystemInfrastructureException : Exception
    {
        public SystemInfrastructureException(string message) : base(message) { }
        public SystemInfrastructureException(string message, Exception inner) : base(message, inner) { }
    }
}
