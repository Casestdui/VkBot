using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class PollManager
{
    private readonly Dictionary<long, PollSession> _pollSessions = new();
    private readonly KudaGoService _kudaGoService;
    private readonly Random _random = new Random();

    public PollManager()
    {
        _kudaGoService = new KudaGoService();
    }

    public PollSession GetOrCreateSession(long peerId)
    {
        if (!_pollSessions.ContainsKey(peerId))
        {
            _pollSessions[peerId] = new PollSession();
        }
        return _pollSessions[peerId];
    }

    public BotState GetState(long peerId)
    {
        return GetOrCreateSession(peerId).State;
    }

    public void CreatePoll(long peerId, long creatorId)
    {
        var session = GetOrCreateSession(peerId);
        session.ResetForNewPoll();
        session.CurrentPoll = new Poll { CreatorId = creatorId };
        session.State = BotState.WaitingTitle;
    }

    public string SetTitle(long peerId, string title)
    {
        var session = GetOrCreateSession(peerId);
        session.CurrentPoll.Title = title.Trim();
        session.State = BotState.WaitingOptions;

        return @"✅ Название сохранено.

        Теперь отправляйте варианты по одному.
        Когда закончите, напишите /start";
    }

    public string AddOption(long peerId, string option)
    {
        var session = GetOrCreateSession(peerId);
        option = option.Trim();

        if (string.IsNullOrWhiteSpace(option))
            return "Пустой вариант не добавлен.";

        bool exists = session.CurrentPoll.Options
            .Any(x => x.Trim().Equals(option, StringComparison.OrdinalIgnoreCase));

        if (exists)
            return $"❌ Вариант «{option}» уже в списке.";

        session.CurrentPoll.Options.Add(option);
        return $"✅ Добавлено: {option}";
    }

    public string StartVoting(long peerId)
    {
        var session = GetOrCreateSession(peerId);

        if (session.CurrentPoll.Options.Count < 2)
            return "❌ Минимум 2 варианта для голосования.";

        session.CurrentPoll.IsActive = true;
        session.State = BotState.Voting;
        session.Votes.Clear();

        var sb = new StringBuilder();
        sb.AppendLine("📊 " + session.CurrentPoll.Title);
        sb.AppendLine();

        for (int i = 0; i < session.CurrentPoll.Options.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {session.CurrentPoll.Options[i]}");
        }

        sb.AppendLine();
        sb.AppendLine("Напишите номер варианта для голоса:");

        return sb.ToString();
    }

    public string AddVote(long peerId, long userId, int optionIndex)
    {
        var session = GetOrCreateSession(peerId);

        if (!session.CurrentPoll.IsActive)
            return "❌ Голосование не активно.";

        if (optionIndex < 0 || optionIndex >= session.CurrentPoll.Options.Count)
            return "❌ Неверный номер.";

        if (session.Votes.ContainsKey(userId))
        {
            var previousChoice = session.CurrentPoll.Options[session.Votes[userId]];
            return $"⚠️ Вы уже голосовали за «{previousChoice}».\nОдин голос на одного участника!";
        }

        session.Votes[userId] = optionIndex;
        return $"✅ Голос принят за: {session.CurrentPoll.Options[optionIndex]}";
    }

    public string GetResultsText(long peerId)
    {
        var session = GetOrCreateSession(peerId);

        if (string.IsNullOrWhiteSpace(session.CurrentPoll.Title))
            return "Голосование ещё не создано.";

        var counts = new int[session.CurrentPoll.Options.Count];
        foreach (var vote in session.Votes.Values)
        {
            counts[vote]++;
        }

        var sb = new StringBuilder();
        sb.AppendLine("📊 " + session.CurrentPoll.Title);
        sb.AppendLine();

        if (session.Votes.Count > 0)
        {
            // Находим максимальное количество голосов
            int maxVotes = counts.Max();
            
            // Находим ВСЕ варианты с максимальным количеством голосов
            var winners = new List<int>();
            for (int i = 0; i < counts.Length; i++)
            {
                if (counts[i] == maxVotes)
                {
                    winners.Add(i);
                }
            }

            // Если только один победитель
            if (winners.Count == 1)
            {
                sb.AppendLine($"🏆 Победитель: {session.CurrentPoll.Options[winners[0]]} ({maxVotes} голосов)");
                Logger.Success($"Победитель: {session.CurrentPoll.Options[winners[0]]}");
            }
            // Если ничья - выбираем СЛУЧАЙНОГО победителя
            else
            {
                var randomWinnerIndex = winners[_random.Next(winners.Count)];
                var winnerOption = session.CurrentPoll.Options[randomWinnerIndex];
                
                sb.AppendLine("😅 Ребята у нас с вами одинаковые голоса, поэтому я выберу за вас!");
                sb.AppendLine();
                sb.AppendLine("Варианты-лидеры:");
                foreach (var winnerIdx in winners)
                {
                    sb.AppendLine($"  • {session.CurrentPoll.Options[winnerIdx]} ({maxVotes} голосов)");
                }
                sb.AppendLine();
                sb.AppendLine($"🎲 Я выбираю... {winnerOption}!");
                
                Logger.Warning($"⚠️ Ничья между {winners.Count} вариантами! Случайно выбран: {winnerOption}");
            }
        }
        else
        {
            sb.AppendLine("Голосов еще нет");
        }

        return sb.ToString();
    }

    public string FinishPoll(long peerId)
    {
        var session = GetOrCreateSession(peerId);

        if (string.IsNullOrWhiteSpace(session.CurrentPoll.Title))
            return "Голосование не создано.";

        var result = GetResultsText(peerId);
        session.State = BotState.None;
        session.CurrentPoll = new Poll();
        session.Votes.Clear();

        return result + "\n\n✅ Голосование завершено!";
    }

    public void Cancel(long peerId)
    {
        if (_pollSessions.ContainsKey(peerId))
        {
            _pollSessions[peerId] = new PollSession();
        }
    }

    public async System.Threading.Tasks.Task<string> StartEventSelectionAsync(long peerId, string city)
    {
        var session = GetOrCreateSession(peerId);
        
        if (string.IsNullOrWhiteSpace(city))
            return "⚠️ Укажите город. Пример: /events Воронеж";

        session.SelectedCity = city.Trim();
        session.State = BotState.WaitingEventCategory;

        var categories = await _kudaGoService.GetCategoriesAsync();
        session.AvailableCategories = categories;

        var sb = new StringBuilder();
        sb.AppendLine("🎭 Выбери категорию мероприятий:");
        sb.AppendLine();

        for (int i = 0; i < categories.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {categories[i]}");
        }

        return sb.ToString();
    }

    public async System.Threading.Tasks.Task<string> CreateEventPollAsync(long peerId, int categoryIndex)
    {
        var session = GetOrCreateSession(peerId);

        if (categoryIndex < 0 || categoryIndex >= session.AvailableCategories.Count)
            return "❌ Неверный номер категории.";

        var category = session.AvailableCategories[categoryIndex];
        var events = await _kudaGoService.GetEventsAsync(session.SelectedCity, category);

        if (events.Count < 2)
            return $"❌ Не удалось получить мероприятия для категории '{category}' в городе '{session.SelectedCity}'";

        session.ResetForNewPoll();
        session.CurrentPoll = new Poll
        {
            Title = $"Что посетить в {session.SelectedCity}? ({category})",
            Options = events.Take(5).ToList()
        };
        session.State = BotState.Voting;
        session.CurrentPoll.IsActive = true;

        var sb = new StringBuilder();
        sb.AppendLine("📊 " + session.CurrentPoll.Title);
        sb.AppendLine();

        for (int i = 0; i < session.CurrentPoll.Options.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {session.CurrentPoll.Options[i]}");
        }

        sb.AppendLine();
        sb.AppendLine("Напишите номер варианта для голоса:");

        return sb.ToString();
    }

    public async System.Threading.Tasks.Task<List<string>> GetAvailableCitiesAsync()
    {
        return await _kudaGoService.GetAvailableCitiesAsync();
    }

    public List<string> GetCurrentOptions(long peerId)
    {
        return GetOrCreateSession(peerId).CurrentPoll.Options;
    }
}