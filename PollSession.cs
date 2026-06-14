using System.Collections.Generic;

public class PollSession
{
    public BotState State { get; set; } = BotState.None;
    
    public Poll CurrentPoll { get; set; } = new Poll();
    
    /// <summary>
    /// Ключ: ID пользователя
    /// Значение: индекс выбранного варианта
    /// </summary>
    public Dictionary<long, int> Votes { get; set; } = new();

    /// <summary>
    /// Для функции /events — текущий выбранный город
    /// </summary>
    public string SelectedCity { get; set; } = string.Empty;

    /// <summary>
    /// Для функции /events — доступные категории
    /// </summary>
    public List<string> AvailableCategories { get; set; } = new();

    public void ResetForNewPoll()
    {
        State = BotState.None;
        CurrentPoll = new Poll();
        Votes.Clear();
        SelectedCity = string.Empty;
        AvailableCategories.Clear();
    }
}