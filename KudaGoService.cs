using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class KudaGoService
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://kudago.com/public-api/v1.4";

    private List<CategoryInfo>? _cachedCategories;

    private static readonly Dictionary<string, string> CityAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "москва", "msk" },
        { "msk", "msk" },

        { "санкт-петербург", "spb" },
        { "санкт петербург", "spb" },
        { "спб", "spb" },
        { "spb", "spb" },

        { "екатеринбург", "ekb" },
        { "ekb", "ekb" },

        { "новосибирск", "nsk" },
        { "nsk", "nsk" },

        { "казань", "kzn" },
        { "kzn", "kzn" },

        { "сочи", "sochi" },
        { "sochi", "sochi" },

        { "нижний новгород", "nnv" },
        { "нижнийновгород", "nnv" },
        { "nnv", "nnv" },

        { "пермь", "perm" },
        { "perm", "perm" },

        { "воронеж", "voronezh" },
        { "voronezh", "voronezh" },

        { "тюмень", "tyumen" },
        { "tyumen", "tyumen" },

        { "красноярск", "krasnoyarsk" },
        { "krasnoyarsk", "krasnoyarsk" },

        { "уфа", "ufa" },
        { "ufa", "ufa" },

        { "ростов-на-дону", "rostov" },
        { "ростов на дону", "rostov" },
        { "ростов", "rostov" },
        { "rostov", "rostov" },

        { "пенза", "penza" },
        { "penza", "penza" },

        { "тула", "tula" },
        { "tula", "tula" },

        { "калининград", "kaliningrad" },
        { "kaliningrad", "kaliningrad" },

        { "саратов", "saratov" },
        { "saratov", "saratov" }
    };

    public KudaGoService()
    {
        _httpClient = new HttpClient(new HttpClientHandler());
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; VkBot/1.0)");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<List<string>> GetCategoriesAsync()
    {
        var categories = await LoadCategoriesAsync();
        return categories
            .Select(c => c.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
    }

    public async Task<List<string>> GetEventsAsync(string city, string category)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(city))
            {
                Logger.Warning("KudaGo: пустой город в запросе мероприятий");
                return GetDefaultEvents();
            }

            var normalizedCity = NormalizeKey(city);
            var citySlug = ResolveCitySlug(city);

            // Воронеж держим отдельным локальным fallback'ом, потому что KudaGo
            // в этом проекте не отдаёт по нему стабильный ответ.
            if (string.IsNullOrWhiteSpace(citySlug))
            {
                Logger.Warning($"⚠️ KudaGo: город '{city}' не найден");
                return IsVoronezh(normalizedCity)
                    ? GetVoronezhFallbackEvents()
                    : GetDefaultEvents();
            }

            var categorySlug = await ResolveCategorySlugAsync(category);

            Logger.Info($"🏙️ KudaGo city slug: {citySlug}");
            if (!string.IsNullOrWhiteSpace(categorySlug))
                Logger.Info($"🏷️ KudaGo category slug: {categorySlug}");

            var actualSince = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var actualUntil = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();

            var query = new List<string>
            {
                $"location={Uri.EscapeDataString(citySlug)}",
                $"actual_since={actualSince}",
                $"actual_until={actualUntil}",
                $"page_size=50",
                $"fields=title,slug,dates,location,categories,site_url",
                $"expand=dates,location"
            };

            if (!string.IsNullOrWhiteSpace(categorySlug))
                query.Add($"categories={Uri.EscapeDataString(categorySlug)}");

            var url = $"{BaseUrl}/events/?{string.Join("&", query)}";

            Logger.Info($"🌐 KudaGo запрос мероприятий: {url}");

            var response = await _httpClient.GetAsync(url);
            Logger.Info($"📡 KudaGo ответ: статус {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning($"❌ KudaGo вернул код {response.StatusCode}");
                return IsVoronezh(citySlug)
                    ? GetVoronezhFallbackEvents()
                    : GetDefaultEvents();
            }

            var content = await response.Content.ReadAsStringAsync();
            Logger.Info($"📄 Получено {content.Length} байт данных");

            using var doc = JsonDocument.Parse(content);

            if (!TryGetItemsArray(doc.RootElement, out var items))
            {
                Logger.Warning("⚠️ KudaGo: неожиданная структура JSON");
                return IsVoronezh(citySlug)
                    ? GetVoronezhFallbackEvents()
                    : GetDefaultEvents();
            }

            var events = new List<string>();

            foreach (var item in items.EnumerateArray())
            {
                var title = GetString(item, "title") ?? GetString(item, "name") ?? GetString(item, "short_title");
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                var dateText = ExtractDateText(item);
                events.Add(string.IsNullOrWhiteSpace(dateText) || dateText == "Дата не указана"
                    ? title
                    : $"{title} — {dateText}");

                Logger.Info($"   ✓ Событие: {title}");
                if (!string.IsNullOrWhiteSpace(dateText) && dateText != "Дата не указана")
                    Logger.Info($"      📅 Дата: {dateText}");
            }

            if (events.Any())
            {
                var result = events
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct()
                    .Take(5)
                    .ToList();

                Logger.Success($"✅ KudaGo: получено {result.Count} мероприятий для {city}");
                return result.Count > 0
                    ? result
                    : (IsVoronezh(citySlug) ? GetVoronezhFallbackEvents() : GetDefaultEvents());
            }

            Logger.Warning($"⚠️ KudaGo: мероприятий не найдено для {city}");
            return IsVoronezh(citySlug)
                ? GetVoronezhFallbackEvents()
                : GetDefaultEvents();
        }
        catch (HttpRequestException ex)
        {
            Logger.Error($"🌐 Ошибка сети KudaGo (мероприятия): {ex.Message}");
            return IsVoronezh(city) ? GetVoronezhFallbackEvents() : GetDefaultEvents();
        }
        catch (JsonException ex)
        {
            Logger.Error($"📄 Ошибка парсинга JSON KudaGo (мероприятия): {ex.Message}");
            return IsVoronezh(city) ? GetVoronezhFallbackEvents() : GetDefaultEvents();
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Ошибка KudaGo (мероприятия): {ex.Message}");
            return IsVoronezh(city) ? GetVoronezhFallbackEvents() : GetDefaultEvents();
        }
    }

    public Task<List<string>> GetAvailableCitiesAsync()
    {
        var displayCities = new List<string>
        {
            "Москва",
            "Санкт-Петербург",
            "Екатеринбург",
            "Новосибирск",
            "Казань",
            "Сочи",
            "Нижний Новгород",
            "Пермь",
            "Воронеж",
            "Тюмень",
            "Красноярск",
            "Уфа",
            "Ростов-на-Дону",
            "Пенза",
            "Тула",
            "Калининград",
            "Саратов"
        };

        Logger.Success($"✅ Доступно {displayCities.Count} городов");
        return Task.FromResult(displayCities);
    }

    private string? ResolveCitySlug(string cityName)
    {
        if (string.IsNullOrWhiteSpace(cityName))
            return null;

        var normalized = NormalizeKey(cityName);

        if (CityAliases.TryGetValue(normalized, out var slug))
            return slug;

        return null;
    }

    private async Task<string?> ResolveCategorySlugAsync(string categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
            return null;

        var normalized = NormalizeKey(categoryName);
        var categories = await LoadCategoriesAsync();

        var match = categories.FirstOrDefault(c =>
            NormalizeKey(c.Name) == normalized ||
            NormalizeKey(c.Slug) == normalized);

        if (!string.IsNullOrWhiteSpace(match?.Slug))
            return match.Slug;

        Logger.Warning($"⚠️ KudaGo: категория '{categoryName}' не найдена");
        return null;
    }

    private async Task<List<CategoryInfo>> LoadCategoriesAsync()
    {
        if (_cachedCategories != null && _cachedCategories.Count > 0)
            return _cachedCategories;

        try
        {
            var url = $"{BaseUrl}/event-categories/?lang=ru&fields=slug,name&order_by=slug";
            Logger.Info($"🌐 KudaGo запрос категорий: {url}");

            var response = await _httpClient.GetAsync(url);
            Logger.Info($"📡 KudaGo ответ категорий: статус {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning($"❌ KudaGo вернул код {response.StatusCode} при получении категорий");
                _cachedCategories = GetDefaultCategoryInfos();
                return _cachedCategories;
            }

            var content = await response.Content.ReadAsStringAsync();
            Logger.Info($"📄 Получено {content.Length} байт данных по категориям");

            using var doc = JsonDocument.Parse(content);

            if (!TryGetItemsArray(doc.RootElement, out var items))
            {
                Logger.Warning("⚠️ KudaGo: неожиданная структура JSON категорий");
                _cachedCategories = GetDefaultCategoryInfos();
                return _cachedCategories;
            }

            var categories = new List<CategoryInfo>();

            foreach (var item in items.EnumerateArray())
            {
                var slug = GetString(item, "slug");
                var name = GetString(item, "name");

                if (!string.IsNullOrWhiteSpace(slug) && !string.IsNullOrWhiteSpace(name))
                {
                    categories.Add(new CategoryInfo(slug!, name!));
                    Logger.Info($"   ✓ Категория: {name} ({slug})");
                }
            }

            _cachedCategories = categories.Count > 0 ? categories : GetDefaultCategoryInfos();
            return _cachedCategories;
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Ошибка загрузки категорий KudaGo: {ex.Message}");
            _cachedCategories = GetDefaultCategoryInfos();
            return _cachedCategories;
        }
    }

    private static bool TryGetItemsArray(JsonElement root, out JsonElement items)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            items = root;
            return true;
        }

        if (root.TryGetProperty("results", out items) && items.ValueKind == JsonValueKind.Array)
            return true;

        if (root.TryGetProperty("data", out items) && items.ValueKind == JsonValueKind.Array)
            return true;

        if (root.TryGetProperty("events", out items) && items.ValueKind == JsonValueKind.Array)
            return true;

        items = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.String)
            return prop.GetString();

        if (prop.ValueKind == JsonValueKind.Number)
            return prop.ToString();

        return null;
    }

    private static string ExtractDateText(JsonElement item)
    {
        if (TryGetStringValue(item, "date_display", out var value)) return value;
        if (TryGetStringValue(item, "start_date", out value)) return value;
        if (TryGetStringValue(item, "starts_at", out value)) return value;
        if (TryGetStringValue(item, "date", out value)) return value;

        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("datetime", out var datetime))
        {
            if (datetime.ValueKind == JsonValueKind.Number && datetime.TryGetInt64(out var unix))
                return DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("dd.MM.yyyy HH:mm");

            if (datetime.ValueKind == JsonValueKind.String)
            {
                var text = datetime.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("dates", out var dates))
        {
            if (dates.ValueKind == JsonValueKind.Array && dates.GetArrayLength() > 0)
                return ExtractDateText(dates[0]);

            if (dates.ValueKind == JsonValueKind.Object)
                return ExtractDateText(dates);
        }

        return "Дата не указана";
    }

    private static bool TryGetStringValue(JsonElement element, string propertyName, out string? value)
    {
        value = null;

        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (!element.TryGetProperty(propertyName, out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        return false;
    }

    private static string NormalizeKey(string value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .ToArray())
            .ToLowerInvariant();
    }

    private static List<CategoryInfo> GetDefaultCategoryInfos()
    {
        return new List<CategoryInfo>
        {
            new("concert", "Концерты"),
            new("exhibition", "Выставки"),
            new("theater", "Спектакли"),
            new("festival", "Фестивали"),
            new("cinema", "Кинопоказы"),
            new("education", "Обучение"),
            new("entertainment", "Развлечения"),
            new("recreation", "Активный отдых"),
            new("party", "Вечеринки"),
            new("quest", "Квесты"),
            new("tour", "Экскурсии")
        };
    }

    private static bool IsVoronezh(string value)
    {
        var normalized = NormalizeKey(value);
        return normalized == "voronezh";
    }

    private List<string> GetVoronezhFallbackEvents()
    {
        Logger.Info("📋 Используются локальные мероприятия для Воронежа");

        return new List<string>
        {
            "Постоянная экспозиция Воронежского областного художественного музея им. И. Н. Крамского",
            "Выставка «Три века истории Воронежской губернии»",
            "Выставка «Их судьбы схожи»",
            "Музейное занятие «Удивительный мир изобразительного искусства»",
            "Спектакль «Дон Жуан. Незаконченная история»",
            "Спектакль «Как дети»"
        };
    }

    private List<string> GetDefaultEvents()
    {
        Logger.Info("📋 Используются мероприятия по умолчанию (KudaGo)");
        return new List<string>
        {
            "Кинотеатр - Кино",
            "Театр имени Платонова",
            "Выставка современного искусства",
            "Концерт классической музыки",
            "Спортивное событие"
        };
    }

    private sealed record CityInfo(string Slug, string Name);
    private sealed record CategoryInfo(string Slug, string Name);
}
