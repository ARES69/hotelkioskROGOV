using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace HotelKiosk.Infrastructure.Adapters.GenericPms
{
    /// <summary>Выбранная в конфигурации PMS.</summary>
    public sealed class PmsSelection
    {
        public const string SectionName = "Pms";

        /// <summary>
        /// Код PMS: "logus" (боевой адаптер с подтверждённым контрактом) или
        /// "generic" (универсальный HTTP/JSON, конфигурируется в Pms:Generic).
        /// Пусто = logus, чтобы поведение не менялось при неполном конфиге.
        /// </summary>
        public string Provider { get; init; } = PmsProviders.Logus;

        /// <summary>Человекочитаемое имя для UI и логов.</summary>
        public string DisplayName { get; init; }

        public bool IsGeneric => string.Equals(Provider, PmsProviders.Generic,
            StringComparison.OrdinalIgnoreCase);

        public static PmsSelection FromConfiguration(IConfiguration configuration)
        {
            var section = configuration?.GetSection(SectionName);
            var provider = (section?["Provider"] ?? "").Trim();

            if (string.IsNullOrEmpty(provider)) provider = PmsProviders.Logus;

            var generic = configuration
                ?.GetSection(GenericPmsOptions.SectionName)
                .Get<GenericPmsOptions>() ?? new GenericPmsOptions();

            var display = (section?["DisplayName"] ?? "").Trim();

            return new PmsSelection
            {
                Provider = provider,
                DisplayName = string.IsNullOrEmpty(display)
                    ? (provider.Equals(PmsProviders.Generic, StringComparison.OrdinalIgnoreCase)
                        ? generic.Vendor
                        : provider)
                    : display
            };
        }
    }

    public static class PmsProviders
    {
        /// <summary>Боевой адаптер: контракт сверен с живым API.</summary>
        public const string Logus = "logus";

        /// <summary>Универсальный HTTP/JSON-адаптер для остальных PMS.</summary>
        public const string Generic = "generic";

        public static readonly IReadOnlyList<string> Known = new[] { Logus, Generic };
    }
}
