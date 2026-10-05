using System.Text.Json;

namespace p3ppc.P3RShuffleTime;

internal sealed class SkillCardRewardTable
{
    public Dictionary<string, List<SkillCardRewardEntry>> Ranks { get; set; } = new();

    public static SkillCardRewardTable Load(string path)
    {
        string json = File.ReadAllText(path);
        SkillCardRewardTable? table = JsonSerializer.Deserialize<SkillCardRewardTable>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        return table ?? throw new InvalidDataException("skill_cards.json did not contain a valid reward table.");
    }

    public SkillCardRewardEntry? Pick(int rank, int maxSkillCardIndexExclusive = int.MaxValue)
    {
        if (!Ranks.TryGetValue(rank.ToString(), out List<SkillCardRewardEntry>? entries) || entries == null || entries.Count == 0)
        {
            return null;
        }

        int totalWeight = 0;
        foreach (SkillCardRewardEntry entry in entries)
        {
            if (entry.SkillCardIndex < 0 || entry.SkillCardIndex >= maxSkillCardIndexExclusive)
            {
                continue;
            }

            totalWeight += Math.Max(0, entry.Weight);
        }

        if (totalWeight <= 0)
        {
            return null;
        }

        int roll = Random.Shared.Next(totalWeight);

        foreach (SkillCardRewardEntry entry in entries)
        {
            if (entry.SkillCardIndex < 0 || entry.SkillCardIndex >= maxSkillCardIndexExclusive)
            {
                continue;
            }

            int weight = Math.Max(0, entry.Weight);
            if (roll < weight)
            {
                return entry;
            }

            roll -= weight;
        }

        return null;
    }
}

internal sealed class SkillCardRewardEntry
{
    public string Name { get; set; } = string.Empty;
    public int SkillCardIndex { get; set; }
    public int Weight { get; set; } = 1;
}
