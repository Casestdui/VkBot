using System;
using System.Collections.Generic;
using System.Linq;

public class CommandHandler
{
    private readonly PollManager _pollManager;
    private const int MaxCityNameLength = 50;

    public CommandHandler(PollManager pollManager)
    {
        _pollManager = pollManager;
    }

    public string HandleMessage(long peerId, long userId, string rawText)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return string.Empty;

            var text = rawText.Trim();
            if (text.Length > 1000)
            {
                Logger.Warning($"Сообщение слишком длинное от пользователя {userId}");
                return "Сообщение слишком длинное.";
            }

            // Нормализуем пользовательский ввод: если это нажатие кнопки с меткой, превращаем в команду.
            var state = _pollManager.GetState(peerId);
            if (state != BotState.WaitingTitle) // не маппим, когда ожидаем название (чтобы не перехватывать обычный текст)
            {
                var mapped = MapLabelToCommand(text);
                if (!string.IsNullOrEmpty(mapped))
                {
                    text = mapped;
                }
            }

            var lower = text.ToLowerInvariant();

            // === ОСНОВНЫЕ КОМАНДЫ ===
            
            if (lower == "/help" || lower == "помощь")
            {
                return GetHelpMessage();
            }

            if (lower == "/cancel" || lower == "отмена")
            {
                _pollManager.Cancel(peerId);
                Logger.Info($"Пользователь {userId} отменил голосование в беседе {peerId}");
                return "Голосование отменено. Можно начать заново с /create";
            }

            if (lower == "/create" || lower == "создать")
            {
                var currentState = _pollManager.GetState(peerId);
                if (currentState == BotState.Voting || currentState == BotState.WaitingOptions)
                {
                    return "Голосование уже идёт. Завершите его /end или отмените /cancel";
                }

                _pollManager.CreatePoll(peerId, userId);
                Logger.Info($"Пользователь {userId} создал новое голосование в беседе {peerId}");
                return "Введите название голосования\n\nПример: Куда пойти в выходной?";
            }

            if (lower == "/result" || lower == "результат")
            {
                if (state == BotState.None)
                    return "Голосование ещё не создано.";

                Logger.Info($"Пользователь {userId} запросил результаты в беседе {peerId}");
                return _pollManager.GetResultsText(peerId);
            }

            if (lower == "/end" || lower == "завершить")
            {
                if (state == BotState.None)
                    return "Нет активного голосования.";

                Logger.Info($"Пользователь {userId} завершил голосование в беседе {peerId}");
                return _pollManager.FinishPoll(peerId);
            }

            if (lower == "/start" || lower == "начать")
            {
                if (state != BotState.WaitingOptions)
                    return "Сначала создайте голосование (/create) и добавьте варианты.";

                Logger.Info($"Пользователь {userId} запустил голосование в беседе {peerId}");
                return _pollManager.StartVoting(peerId);
            }

            if (lower == "/cities" || lower == "города")
            {
                Logger.Info($"Пользователь {userId} запросил список городов");
                var cities = System.Threading.Tasks.Task.Run(async () => 
                    await _pollManager.GetAvailableCitiesAsync()
                ).Result;

                if (cities.Count == 0)
                    return "Не удалось получить список городов";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Доступные города:");
                sb.AppendLine();
                for (int i = 0; i < Math.Min(cities.Count, 30); i++)
                {
                    sb.AppendLine($"{i + 1}. {cities[i]}");
                }
                return sb.ToString();
            }

            if (lower.StartsWith("/events") || lower == "мероприятия" || lower == "события")
            {
                var parts = text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return "Укажите город.\nПример: /events Москва\n\n/cities — посмотреть доступные города";

                var city = string.Join(" ", parts.Skip(1)).Trim();

                if (city.Length > MaxCityNameLength)
                    return $"Название города слишком длинное (максимум {MaxCityNameLength} символов).";

                Logger.Info($"Пользователь {userId} запросил события в городе '{city}' беседе {peerId}");
                
                var result = System.Threading.Tasks.Task.Run(async () => 
                    await _pollManager.StartEventSelectionAsync(peerId, city)
                ).Result;

                return result;
            }

            // Новая команда: показать текущие варианты при создании
            if (lower == "/options" || lower == "варианты")
            {
                var options = _pollManager.GetCurrentOptions(peerId);
                if (options == null || options.Count == 0)
                    return "Варианты ещё не добавлены.";

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("Текущие варианты:");
                sb.AppendLine();
                for (int i = 0; i < options.Count; i++)
                {
                    sb.AppendLine($"{i + 1}. {options[i]}");
                }
                return sb.ToString();
            }

            // === ОБРАБОТКА ПО СОСТОЯНИЮ ===

            if (state == BotState.WaitingTitle)
            {
                if (text.StartsWith("/"))
                    return "Введите название голосования обычным текстом.\n/cancel — отменить создание";

                if (text.Length > 100)
                    return "Название слишком длинное (макс 100 символов)";

                if (text.Length < 3)
                    return "Название слишком короткое (минимум 3 символа)";

                Logger.Info($"Пользователь {userId} установил название: '{text}'");
                return _pollManager.SetTitle(peerId, text);
            }

            if (state == BotState.WaitingOptions)
            {
                if (lower == "/start" || lower == "начать")
                {
                    Logger.Info($"Пользователь {userId} запустил голосование из WaitingOptions");
                    return _pollManager.StartVoting(peerId);
                }

                if (lower == "/cancel" || lower == "отмена")
                {
                    _pollManager.Cancel(peerId);
                    Logger.Info($"Пользователь {userId} отменил создание голосования");
                    return "Создание голосования отменено.";
                }

                if (text.StartsWith("/"))
                    return "Добавляйте варианты текстом или напишите /start";

                if (text.Length > 50)
                    return "Вариант слишком длинный (макс 50 символов)";

                if (text.Length < 2)
                    return "Вариант слишком короткий (минимум 2 символа)";

                Logger.Info($"Пользователь {userId} добавил вариант: '{text}'");
                return _pollManager.AddOption(peerId, text);
            }

            if (state == BotState.WaitingEventCategory)
            {
                if (int.TryParse(text, out int choice))
                {
                    choice--;
                    Logger.Info($"Пользователь {userId} выбрал категорию #{choice + 1}");
                    
                    var result = System.Threading.Tasks.Task.Run(async () => 
                        await _pollManager.CreateEventPollAsync(peerId, choice)
                    ).Result;

                    return result;
                }

                return "Отправьте номер категории.";
            }

            if (state == BotState.Voting)
            {
                if (lower == "/result" || lower == "результат")
                {
                    Logger.Info($"Пользователь {userId} запросил результаты во время голосования");
                    return _pollManager.GetResultsText(peerId);
                }

                if (lower == "/end" || lower == "завершить")
                {
                    Logger.Info($"Пользователь {userId} завершил голосование");
                    return _pollManager.FinishPoll(peerId);
                }

                if (lower == "/cancel" || lower == "/start" || lower == "/create" || lower == "отмена" || lower == "начать" || lower == "создать")
                    return "Голосование в процессе. Используйте /result или /end";

                if (int.TryParse(text, out int choice))
                {
                    choice--;
                    Logger.Info($"Пользователь {userId} голосует за вариант #{choice + 1}");
                    return _pollManager.AddVote(peerId, userId, choice);
                }

                return string.Empty;
            }

            if (state == BotState.Finished)
            {
                if (lower == "/create" || lower == "создать")
                {
                    _pollManager.CreatePoll(peerId, userId);
                    Logger.Info($"Пользователь {userId} создал новое голосование (после завершения)");
                    return "Введите название голосования";
                }

                return string.Empty;
            }

            // === FALLBACK ===
            if (!text.StartsWith("/"))
            {
                return string.Empty;
            }

            Logger.Warning($"Неизвестная команда от пользователя {userId}: '{text}'");
            return "Неизвестная команда.\n/help — список команд";
        }
        catch (Exception ex)
        {
            Logger.Error($"Критическая ошибка в CommandHandler для пользователя {userId}: {ex}");
            return "Произошла ошибка. Попробуйте позже.";
        }
    }

    private string? MapLabelToCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var t = text.Trim();

        // если уже команда с '/', оставляем как есть
        if (t.StartsWith("/"))
            return t;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "создать", "/create" },
            { "create", "/create" },
            { "начать", "/start" },
            { "start", "/start" },
            { "результат", "/result" },
            { "result", "/result" },
            { "завершить", "/end" },
            { "отмена", "/cancel" },
            { "cancel", "/cancel" },
            { "помощь", "/help" },
            { "help", "/help" },
            { "города", "/cities" },
            { "cities", "/cities" },
            { "мероприятия", "/events" },
            { "события", "/events" },
            { "варианты", "/options" },
            { "options", "/options" },
            { "список", "/options" }
        };

        if (map.TryGetValue(t, out var cmd))
            return cmd;

        return null;
    }

    private string GetHelpMessage()
    {
        return @"Доступные команды:

        ОСНОВНЫЕ:
        /create — начать новое голосование
        /start — запустить голосование после добавления вариантов
        /result — показать текущие результаты
        /end — завершить голосование и показать итоги

        ДРУГОЕ:
        /cancel — отменить текущее голосование
        /options — показать текущие варианты (при создании)
        /help — эта справка

        МЕРОПРИЯТИЯ:
        /events [город] — создать голосование из мероприятий
        /cities — список доступных городов

        Примеры:
        /events Москва
        /events Воронеж";
    }
}