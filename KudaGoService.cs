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

    public KudaGoService()
    {
        var handler = new HttpClientHandler();
        _httpClient = new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// Получить список категорий мероприятий
    /// </summary>
    public async Task<List<string>> GetCategoriesAsync()
    {
        try
        {
            var url = $"{BaseUrl}/event-categories/";
            Logger.Info($"🌐 KudaGo запрос категорий: {url}");

            var response = await _httpClient.GetAsync(url);
            
            Logger.Info($"📡 KudaGo ответ: статус {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning($"❌ KudaGo вернул код {response.StatusCode}");
                return GetDefaultCategories();
            }

            var content = await response.Content.ReadAsStringAsync();
            Logger.Info($"📄 Получено {content.Length} байт данных");

            using (JsonDocument doc = JsonDocument.Parse(content))
            {
                var categories = new List<string>();
                
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var name))
                        {
                            var catName = name.GetString();
                            if (!string.IsNullOrWhiteSpace(catName))
                            {
                                categories.Add(catName);
                                Logger.Info($"   ✓ Категория: {catName}");
                            }
                        }
                    }
                }
                else if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var name))
                        {
                            var catName = name.GetString();
                            if (!string.IsNullOrWhiteSpace(catName))
                            {
                                categories.Add(catName);
                                Logger.Info($"   ✓ Категория: {catName}");
                            }
                        }
                    }
                }
                
                if (categories.Any())
                {
                    Logger.Success($"✅ KudaGo: получено {categories.Count} категорий");
                    return categories;
                }
                else
                {
                    Logger.Warning("⚠️ KudaGo: категории пусты, используются заглушки");
                    return GetDefaultCategories();
                }
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.Error($"🌐 Ошибка сети KudaGo (категории): {ex.Message}");
            return GetDefaultCategories();
        }
        catch (JsonException ex)
        {
            Logger.Error($"📄 Ошибка парсинга JSON KudaGo (категории): {ex.Message}");
            return GetDefaultCategories();
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Ошибка KudaGo (категории): {ex.Message}");
            return GetDefaultCategories();
        }
    }

    /// <summary>
    /// Получить АКТУАЛЬНЫЕ мероприятия по городу и категории
    /// </summary>
    public async Task<List<string>> GetEventsAsync(string city, string category)
    {
        try
        {
            var cityId = await GetCityIdAsync(city);
            if (cityId == null)
            {
                Logger.Warning($"⚠️ Город '{city}' не найден, используются заглушки");
                return GetDefaultEvents();
            }

            Logger.Info($"🏙️ ID города '{city}': {cityId}");

            // Получаем только актуальные события (от сегодня и дальше)
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var url = $"{BaseUrl}/events/?city={cityId}&actual_since={today}&order=-publication_date&limit=50";

            Logger.Info($"🌐 KudaGo запрос АКТУАЛЬНЫХ мероприятий: {url}");
            Logger.Info($"📅 Фильтр: только мероприятия от {today}");

            var response = await _httpClient.GetAsync(url);

            Logger.Info($"📡 KudaGo ответ: статус {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning($"❌ KudaGo вернул код {response.StatusCode}");
                return GetDefaultEvents();
            }

            var content = await response.Content.ReadAsStringAsync();
            Logger.Info($"📄 Получено {content.Length} байт данных");

            using (JsonDocument doc = JsonDocument.Parse(content))
            {
                var events = new List<(string title, string date)>();
                
                JsonElement resultsElement;
                if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    resultsElement = results;
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    resultsElement = doc.RootElement;
                }
                else
                {
                    Logger.Warning("⚠️ Неожиданная структура JSON");
                    return GetDefaultEvents();
                }

                foreach (var item in resultsElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("title", out var title))
                        continue;

                    var eventTitle = title.GetString();
                    if (string.IsNullOrWhiteSpace(eventTitle))
                        continue;

                    // Пытаемся получить дату
                    var dateStr = "Дата не указана";
                    if (item.TryGetProperty("date_display", out var dateDisplay))
                    {
                        dateStr = dateDisplay.GetString() ?? dateStr;
                    }
                    else if (item.TryGetProperty("start_date", out var startDate))
                    {
                        dateStr = startDate.GetString() ?? dateStr;
                    }

                    events.Add((eventTitle, dateStr));
                    Logger.Info($"   ✓ Событие: {eventTitle}");
                    Logger.Info($"      📅 Дата: {dateStr}");
                }

                if (events.Any())
                {
                    var result = events
                        .Take(5)
                        .Select(e => e.title)
                        .ToList();

                    Logger.Success($"✅ KudaGo: получено {result.Count} АКТУАЛЬНЫХ мероприятий для {city}");
                    return result;
                }
                else
                {
                    Logger.Warning($"⚠️ KudaGo: актуальные мероприятия не найдены для {city} сегодня ({today})");
                    Logger.Warning($"⚠️ Используются заглушки");
                    return GetDefaultEvents();
                }
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.Error($"🌐 Ошибка сети KudaGo (мероприятия): {ex.Message}");
            return GetDefaultEvents();
        }
        catch (JsonException ex)
        {
            Logger.Error($"📄 Ошибка парсинга JSON KudaGo (мероприятия): {ex.Message}");
            return GetDefaultEvents();
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Ошибка KudaGo (мероприятия): {ex.Message}");
            return GetDefaultEvents();
        }
    }

    /// <summary>
    /// Получить все доступные города
    /// </summary>
    public async Task<List<string>> GetAvailableCitiesAsync()
    {
        try
        {
            var popularCities = new List<(string id, string name)>
            {
                ("msk", "Москва"),
                ("spb", "Санкт-Петербург"),
                ("ekb", "Екатеринбург"),
                ("nsk", "Новосибирск"),
                ("kzn", "Казань"),
                ("sochi", "Сочи"),
                ("nn", "Нижний Новгород"),
                ("perm", "Пермь"),
                ("voronezh", "Воронеж"),
                ("tyumen", "Тюмень"),
                ("krasnoyarsk", "Красноярск"),
                ("ufa", "Уфа"),
                ("rostov", "Ростов-на-Дону"),
                ("penza", "Пенза"),
                ("tula", "Тула"),
                ("kaliningrad", "Калининград"),
                ("saratov", "Саратов"),
                ("yekaterinburg", "Екатеринбург")
            };

            Logger.Success($"✅ Доступно {popularCities.Count} городов");
            return popularCities.Select(c => c.name).ToList();
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Ошибка получения городов: {ex.Message}");
            return new List<string>();
        }
    }

    /// <summary>
    /// Получить ID города по названию
    /// </summary>
    private async Task<string> GetCityIdAsync(string cityName)
    {
        try
        {
            var cityMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "москва", "msk" },
                { "санкт-петербург", "spb" },
                { "спб", "spb" },
                { "екатеринбург", "ekb" },
                { "новосибирск", "nsk" },
                { "казань", "kzn" },
                { "сочи", "sochi" },
                { "нижний новгород", "nn" },
                { "пермь", "perm" },
                { "воронеж", "voronezh" },
                { "тюмень", "tyumen" },
                { "красноярск", "krasnoyarsk" },
                { "уфа", "ufa" },
                { "ростов-на-дону", "rostov" },
                { "ростов", "rostov" },
                { "пенза", "penza" },
                { "тула", "tula" },
                { "калининград", "kaliningrad" },
                { "саратов", "saratov" }
            };

            if (cityMap.TryGetValue(cityName, out var cityId))
            {
                Logger.Info($"🏙️ Найден ID для города '{cityName}': {cityId}");
                return cityId;
            }

            Logger.Warning($"⚠️ Город '{cityName}' не в списке популярных");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error($"Ошибка получения ID города: {ex.Message}");
            return null;
        }
    }

    private List<string> GetDefaultCategories()
    {
        Logger.Info("📋 Используются категории по умолчанию");
        return new List<string>
        {
            "Кино",
            "Театр",
            "Концерты",
            "Выставки",
            "Спорт"
        };
    }

    private List<string> GetDefaultEvents()
    {
        Logger.Info("📋 Используются мероприятия по умолчанию (актуальные не найдены)");
        return new List<string>
        {
            "Кинотеатр - Кино",
            "Театр имени Платонова",
            "Выставка современного искусства",
            "Концерт классической музыки",
            "Спортивное событие"
        };
    }
}