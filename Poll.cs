// модель голосования
using System.Collections.Generic;

public class Poll
{
    public string Title { get; set; } = string.Empty;

    public List<string> Options { get; set; } = new();

    public Dictionary<long, int> Votes { get; set; } = new();

    public bool IsActive { get; set; }

    public long CreatorId { get; set; }
}