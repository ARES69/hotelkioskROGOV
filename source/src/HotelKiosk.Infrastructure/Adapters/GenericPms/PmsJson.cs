using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HotelKiosk.Infrastructure.Adapters.GenericPms
{
    /// <summary>
    /// Подстановка значений в шаблоны запросов и чтение полей из ответа —
    /// механика, на которой держится конфигурируемая интеграция с PMS.
    ///
    /// Смысл: киоск работает с канонической моделью (SelfCheckinInfo и т.д.),
    /// а конкретная PMS отдаёт свои собственные имена полей и свои URL.
    /// Расхождение описывается в appsettings.json, а не в коде, поэтому
    /// подключение новой PMS — это правка конфига, а не сборка.
    /// </summary>
    public static class PmsJson
    {
        // ====================================================================
        //  Чтение значений из JSON по пути
        // ====================================================================

        /// <summary>
        /// Читает значение по точечному пути: "reservation.room.number".
        /// Регистр не важен — у разных PMS он разный.
        /// Пустой путь или пустое имя свойства означают «сам объект».
        /// </summary>
        public static bool TryGet(JsonElement element, string path, out JsonElement value)
        {
            value = default;
            if (element.ValueKind != JsonValueKind.Object) return false;
            if (string.IsNullOrWhiteSpace(path)) return false;

            var current = element;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.ValueKind != JsonValueKind.Object) return false;
                if (!TryGetPropertyIgnoreCase(current, segment, out var next)) return false;
                current = next;
            }

            value = current;
            return true;
        }

        public static JsonElement? Get(JsonElement element, string path) =>
            TryGet(element, path, out var v) ? v : null;

        private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
        {
            if (obj.TryGetProperty(name, out value)) return true;

            foreach (var prop in obj.EnumerateObject())
            {
                if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                value = prop.Value;
                return true;
            }

            value = default;
            return false;
        }

        // ====================================================================
        //  Приведение к строгому типу
        // ====================================================================

        public static string GetString(JsonElement element, string path)
        {
            if (!TryGet(element, path, out var v) || v.ValueKind == JsonValueKind.Null) return null;

            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
                _ => null
            };
        }

        public static int? GetInt(JsonElement element, string path)
        {
            if (!TryGet(element, path, out var v)) return null;

            return v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                _ => null
            };
        }

        public static bool? GetBool(JsonElement element, string path)
        {
            if (!TryGet(element, path, out var v)) return null;

            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => v.TryGetInt32(out var n) && n != 0,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b)
                    ? b
                    : v.GetString() is "1" ? true : v.GetString() is "0" ? false : null,
                _ => null
            };
        }

        public static decimal? GetDecimal(JsonElement element, string path)
        {
            if (!TryGet(element, path, out var v)) return null;

            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    return v.TryGetDecimal(out var d) ? d : null;

                case JsonValueKind.String:
                {
                    var s = v.GetString()?.Trim();
                    if (string.IsNullOrEmpty(s)) return null;

                    if (decimal.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands,
                            CultureInfo.InvariantCulture, out var plain))
                        return plain;

                    // Русский формат: "5 000,50" — запятая это десятичная
                    // запятая, а НЕ разделитель тысяч. Если не убрать её
                    // перед invariant-разбором, получится 5000050 вместо
                    // 5000.50: по правилам invariant запятая = разделитель тысяч.
                    var normalized = s
                        .Replace(" ", string.Empty)
                        .Replace(" ", string.Empty)   // неразрывный пробел
                        .Replace(" ", string.Empty)
                        .Replace(" ", string.Empty)
                        .Replace(',', '.');

                    return decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var result) ? result : null;
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// Дата в ответе PMS. Формат отличается от системы к системе, поэтому
        /// перебираем типовые: ISO, "yyyy-MM-dd HH:mm:ss", "dd.MM.yyyy".
        /// </summary>
        public static DateTime? GetDateTime(JsonElement element, string path)
        {
            if (!TryGet(element, path, out var v) || v.ValueKind == JsonValueKind.Null) return null;

            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;

                if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var iso))
                    return DateTime.SpecifyKind(iso, DateTimeKind.Utc).ToLocalTime();

                if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var plain))
                    return plain;

                return DateTime.TryParse(s, new CultureInfo("ru-RU"),
                    DateTimeStyles.None, out var ru) ? ru : null;
            }

            // Unix-время в секундах или миллисекундах
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var epoch))
            {
                var ticks = epoch > 1_000_000_000_000L ? epoch / 1000 : epoch;
                if (ticks > 0)
                    return DateTimeOffset.FromUnixTimeSeconds(ticks).LocalDateTime;
            }

            return null;
        }

        /// <summary>Список объектов по пути: пустой массив, если пути нет.</summary>
        public static List<JsonElement> GetArray(JsonElement element, string path)
        {
            var result = new List<JsonElement>();
            if (!TryGet(element, path, out var v) || v.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in v.EnumerateArray()) result.Add(item);
            return result;
        }

        // ====================================================================
        //  Подстановка в шаблоны запросов
        // ====================================================================

        /// <summary>
        /// Подставляет значения в шаблон: "reservations/{code}" + {code}=BR-1
        /// → "reservations/BR-1".
        ///
        /// Поддерживается формат с двоеточием для чисел:
        ///   {amount:0.00}  → "5000.00"  (в любом случае с точкой)
        ///   {amount}       → "5000"
        ///
        /// URL-значения экранируются: пробелы и кириллица в номере брони
        /// иначе ломают запрос.
        /// </summary>
        public static string Expand(string template, IReadOnlyDictionary<string, string> values,
                                    bool urlEncode = false)
        {
            // Символ "@" — маркер подстановки значений в теле запроса
            // (см. GenericPmsService.Shape). В URL и обычных шаблонах он не
            // участвует, поэтому убираем его перед плейсхолдером: иначе
            // "@{amount:0.00}" даст "@5000.00".
            if (!string.IsNullOrEmpty(template))
                template = template.Replace("@{", "{");

            if (string.IsNullOrEmpty(template)) return template;

            var result = new StringBuilder(template.Length + 64);

            for (var i = 0; i < template.Length; i++)
            {
                if (template[i] != '{')
                {
                    result.Append(template[i]);
                    continue;
                }

                var end = template.IndexOf('}', i);
                if (end < 0)
                {
                    result.Append(template[i..]);
                    break;
                }

                var token = template[(i + 1)..end];
                i = end;

                var colon = token.IndexOf(':');
                var name = colon >= 0 ? token[..colon] : token;
                var format = colon >= 0 ? token[(colon + 1)..] : null;

                if (!values.TryGetValue(name, out var value))
                {
                    // Неизвестный плейсхолдер оставляем как есть: так ошибка
                    // в конфиге видна в запросе, а не превращается в пустую строку
                    result.Append('{').Append(token).Append('}');
                    continue;
                }

                value ??= string.Empty;

                if (format is "0.00")
                {
                    if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                        value = d.ToString("0.00", CultureInfo.InvariantCulture);
                }
                else if (!string.IsNullOrEmpty(format))
                {
                    // Даты и прочее: убираем возможные скобки из шаблона
                    value = value.Replace("{", string.Empty).Replace("}", string.Empty);
                }

                result.Append(urlEncode ? Uri.EscapeDataString(value) : value);
            }

            return result.ToString();
        }

        /// <summary>
        /// Строит JSON-тело из шаблона-объекта: значения вида "@{name}"
        /// подставляются как строковые, остальное остаётся как есть.
        /// Так можно описать тело запроса в конфиге целиком.
        /// </summary>
        public static string ExpandJsonBody(object template, IReadOnlyDictionary<string, string> values)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(template);
            return Expand(json, values);
        }
    }
}
