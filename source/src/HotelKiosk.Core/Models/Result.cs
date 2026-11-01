namespace HotelKiosk.Core.Models
{
    public class Result<T>
    {
        public bool IsSuccess { get; }
        public T Value { get; }
        public string Error { get; }
        public bool IsBusinessError { get; }

        private Result(bool isSuccess, T value, string error, bool isBusinessError)
        {
            IsSuccess = isSuccess;
            Value = value;
            Error = error;
            IsBusinessError = isBusinessError;
        }

        public static Result<T> Success(T value) => new(true, value, null, false);
        public static Result<T> BusinessFailure(string error) => new(false, default, error, true);
        public static Result<T> SystemFailure(string error) => new(false, default, error, false);
    }
}
