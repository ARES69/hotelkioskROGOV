using System.ComponentModel;
using System.Reflection;

namespace HotelKiosk.Core.Models
{
    public enum LogusReservationStatus
    {
        [Description("New")] New,
        [Description("CheckedIn")] CheckedIn,
        [Description("Cancelled")] Cancelled,
        [Description("CheckedOut")] CheckedOut
    }

    public static class LogusReservationStatusExtensions
    {
        public static string ToLogusString(this LogusReservationStatus status)
        {
            var field = status.GetType().GetField(status.ToString());
            var attr = field?.GetCustomAttribute<DescriptionAttribute>();
            return attr?.Description ?? status.ToString();
        }
    }
}
