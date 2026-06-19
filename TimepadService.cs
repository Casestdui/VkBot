using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class TimepadService
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://api.timepad.ru/v1";

    public TimepadService()
    {
        var handler = new HttpClientHandler();
        _httpClient = new HttpClient(handler);
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; VkBot/1.0)");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>
    /// Получить список категорий — у Timepad нет однозначного публичного endpoint для категорий,
    /// поэтому возвращаем заранее подготовленный список, подходящий для событийной тематики.
    /// При необходимости можно расширить получение из API Timepad (tags, themes и т.д.).
    /// </summary>
    public async Task<List<string>> GetCategoriesAsync()
    {
        try
        {
            // Временный (но понятный) набор категорий, используемый в UI.
            var categories = new List<string>
            {
                "Концерты",
                "Конференции",
                "Курсы и мастер-классы",
                "Фестивали",
                "Выставки",
                "Кино",
                "Театр",
                "Спорт"
            };

            return await Task.FromResult(categories);
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Ошибка получения категорий Timepad: {ex.Message}");
            return new List<string> { "Концерты", "Выставки", "Фестивали", "Кино", "Театр" };
        }
    }

    /// <summary>
    /// Получить мероприятия по городу и категории (попытка через публичный events endpoint Timepad).
    /// Возвращает список названий мероприятий (максимум 5).
    /// </summary>
    public async Task<List<string>> GetEventsAsync(string city, string category)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(city))
            {
                Logger.Warning("Timepad: пустой город в запросе мероприятий");
                return GetDefaultEvents();
            }

            // Формируем query. Timepad поддерживает q (поиск), city, per_page и starts_at_min/starts_at_max (зависит от API версии).
            // Мы используем максимально безопасный набор параметров — API либо проигнорирует неизвестные параметры, либо вернёт корректный результат.
            var q = Uri.EscapeDataString(category ?? string.Empty);
            var cityEncoded = Uri.EscapeDataString(city ?? string.Empty);
            // По умолчанию запрашиваем ближайшие события (фильтр по дате)
            var startsAtMin = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            var url = $"{BaseUrl}/events.json?q={q}&city={cityEncoded}&per_page=50&starts_at_min={startsAtMin}";

            Logger.Info($"🌐 Timepad запрос мероприятий: {url}");

            var response = await _httpClient.GetAsync(url);

            Logger.Info($"📡 Timepad ответ: статус {response.StatusCode}");

            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning($"❌ Timepad вернул код {response.StatusCode}");
                return GetDefaultEvents();
            }

            var content = await response.Content.ReadAsStringAsync();
            Logger.Info($"📄 Получено {content.Length} байт данных от Timepad");

            using (JsonDocument doc = JsonDocument.Parse(content))
            {
                var root = doc.RootElement;
                JsonElement itemsElement;
                var found = false;

                // Возможные варианты структуры ответа: root is array, root.events, root.results, root.data
                if (root.ValueKind == JsonValueKind.Array)
                {
                    itemsElement = root;
                    found = true;
                }
                else if (root.TryGetProperty("events", out var eventsProp) && eventsProp.ValueKind == JsonValueKind.Array)
                {
                    itemsElement = eventsProp;
                    found = true;
                }
                else if (root.TryGetProperty("results", out var resultsProp) && resultsProp.ValueKind == JsonValueKind.Array)
                {
                    itemsElement = resultsProp;
                    found = true;
                }
                else if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                {
                    itemsElement = dataProp;
                    found = true;
                }
                else
                {
                    // Неизвестная структура
                    Logger.Warning("⚠️ Timepad: неожиданная структура JSON");
                    return GetDefaultEvents();
                }

                var events = new List<(string title, string date)>();

                foreach (var item in itemsElement.EnumerateArray())
                {
                    string title = null;
                    string dateStr = "Дата не указана";

                    // Попытки получить заголовок
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        if (item.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                            title = nameEl.GetString();

                        if (title == null && item.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                            title = titleEl.GetString();

                        // Время начала может быть в starts_at или start_date или starts_at_local
                        if (item.TryGetProperty("starts_at", out var startsAt) && startsAt.ValueKind == JsonValueKind.String)
                            dateStr = startsAt.GetString();
                        else if (item.TryGetProperty("start_date", out var startDate) && startDate.ValueKind == JsonValueKind.String)
                            dateStr = startDate.GetString();
                        else if (item.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
                            dateStr = dateEl.GetString();
                    }

                    if (string.IsNullOrWhiteSpace(title))
                        continue;

                    events.Add((title, dateStr));
                    Logger.Info($"   ✓ Событие: {title} ({dateStr})");
                }

                if (events.Any())
                {
                    var result = events
                        .Take(5)
                        .Select(e => e.title)
                        .ToList();

                    Logger.Success($"✅ Timepad: получено {result.Count} мероприятий для {city}");
                    return result;
                }
                else
                {
                    Logger.Warning($"⚠️ Timepad: мероприятий не найдено для {city}");
                    return GetDefaultEvents();
                }
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.Error($"🌐 Ошибка сети Timepad (мероприятия): {ex.Message}");
            return GetDefaultEvents();
        }
        catch (JsonException ex)
        {
            Logger.Error($"📄 Ошибка парсинга JSON Timepad (мероприятия): {ex.Message}");
            return GetDefaultEvents();
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Ошибка Timepad (мероприятия): {ex.Message}");
            return GetDefaultEvents();
        }
    }

    /// <summary>
    /// Получить доступные города — возвращаем список популярных городов (можно расширить/синхронизировать с Timepad при необходимости).
    /// </summary>
    public async Task<List<string>> GetAvailableCitiesAsync()
    {
        try
        {
            var popularCities = new List<string>
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

            return await Task.FromResult(popularCities);
        }
        catch (Exception ex)
        {
            Logger.Error($"❌ Ошибка получения городов Timepad: {ex.Message}");
            return new List<string>();
        }
    }

    private List<string> GetDefaultEvents()
    {
        Logger.Info("📋 Используются мероприятия по умолчанию (Timepad)");
        return new List<string>
        {
            "Кинотеатр - Кино",
            "Театр – Премьера",
            "Выставка современного искусства",
            "Концерт популярного исполнителя",
            "Мастер-класс"
        };
    }
}