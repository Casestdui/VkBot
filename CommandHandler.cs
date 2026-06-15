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

            var lower = text.ToLowerInvariant();
            var state = _pollManager.GetState(peerId);

            // === ОСНОВНЫЕ КОМАНДЫ ===
            
            if (lower == "/help")
            {
                return GetHelpMessage();
            }

            if (lower == "/cancel")
            {
                _pollManager.Cancel(peerId);
                Logger.Info($"Пользователь {userId} отменил голосование в беседе {peerId}");
                return "Голосование отменено. Можно начать заново с /create";
            }

            if (lower == "/create")
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

            if (lower == "/result")
            {
                if (state == BotState.None)
                    return "Голосование ещё не создано.";

                Logger.Info($"Пользователь {userId} запросил результаты в беседе {peerId}");
                return _pollManager.GetResultsText(peerId);
            }

            if (lower == "/end")
            {
                if (state == BotState.None)
                    return "Нет активного голосования.";

                Logger.Info($"Пользователь {userId} завершил голосование в беседе {peerId}");
                return _pollManager.FinishPoll(peerId);
            }

            if (lower == "/start")
            {
                if (state != BotState.WaitingOptions)
                    return "Сначала создайте голосование (/create) и добавьте варианты.";

                Logger.Info($"Пользователь {userId} запустил голосование в беседе {peerId}");
                return _pollManager.StartVoting(peerId);
            }

            if (lower == "/cities")
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

            if (lower.StartsWith("/events"))
            {
                var parts = text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    return "Укажите город.\nПример: /events Москва\n\n/cities — посмотреть доступные города";

                var city = string.Join(" ", parts.Skip(1));

                if (city.Length > MaxCityNameLength)
                    return $"Название города слишком длинное (максимум {MaxCityNameLength} символов).";

                Logger.Info($"Пользователь {userId} запросил события в городе '{city}' беседе {peerId}");
                
                var result = System.Threading.Tasks.Task.Run(async () => 
                    await _pollManager.StartEventSelectionAsync(peerId, city)
                ).Result;

                return result;
            }

            // === ОБРАБОТКА ПО СОСТОЯНИЮ ===

            if (state == BotState.WaitingTitle)
            {
                if (lower.StartsWith("/"))
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
                if (lower == "/start")
                {
                    Logger.Info($"Пользователь {userId} запустил голосование из WaitingOptions");
                    return _pollManager.StartVoting(peerId);
                }

                if (lower == "/cancel")
                {
                    _pollManager.Cancel(peerId);
                    Logger.Info($"Пользователь {userId} отменил создание голосования");
                    return "Создание голосования отменено.";
                }

                if (lower.StartsWith("/"))
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
                if (lower == "/result")
                {
                    Logger.Info($"Пользователь {userId} запросил результаты во время голосования");
                    return _pollManager.GetResultsText(peerId);
                }

                if (lower == "/end")
                {
                    Logger.Info($"Пользователь {userId} завершил голосование");
                    return _pollManager.FinishPoll(peerId);
                }

                if (lower == "/cancel" || lower == "/start" || lower == "/create")
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
                if (lower == "/create")
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
            Logger.Error($"Критическая ошибка в CommandHandler для пользователя {userId}: {ex.Message}");
            return "Произошла ошибка. Попробуйте позже.";
        }
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
        /help — эта справка

        МЕРОПРИЯТИЯ:
        /events [город] — создать голосование из мероприятий
        /cities — список доступных городов

        Примеры:
        /events Москва
        /events Воронеж";
    }
}