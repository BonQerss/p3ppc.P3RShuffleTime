using System.Globalization;
using System.Text.Json;

namespace p3ppc.P3RShuffleTime;

internal sealed class SkillCardRewardTable
{
    public Dictionary<string, List<SkillCardRewardEntry>> Ranks { get; set; } = new();

    public static SkillCardRewardTable Load(string path)
    {
        JsonSerializerOptions options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        return JsonSerializer.Deserialize<SkillCardRewardTable>(File.ReadAllText(path), options) ?? throw new InvalidDataException("skill_cards.json isn't a valid reward table.");
    }

    public SkillCardRewardEntry? Pick(int rank) => Pick(rank, null);

    // Leaves out cards already given to another slot this Shuffle, unless that leaves nothing to pick
    public SkillCardRewardEntry? Pick(int rank, IReadOnlySet<string>? taken)
    {
        if (!Ranks.TryGetValue(rank.ToString(CultureInfo.InvariantCulture), out List<SkillCardRewardEntry>? entries) || entries == null || entries.Count == 0)
        {
            return null;
        }

        if (taken != null && taken.Count > 0)
        {
            List<SkillCardRewardEntry> fresh = entries.FindAll(e => !taken.Contains(e.ItemId) && e.Weight > 0);
            if (fresh.Count > 0)
            {
                entries = fresh;
            }
        }

        int totalWeight = entries.Sum(e => Math.Max(0, e.Weight));
        if (totalWeight <= 0)
        {
            return null;
        }

        int roll = Random.Shared.Next(totalWeight);
        foreach (SkillCardRewardEntry entry in entries)
        {
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

    // Item ID from the P3P item list such as 0x770 = Tarukaja
    public string ItemId { get; set; } = string.Empty;

    public int Weight { get; set; } = 1;

    public bool TryGetItemId(out int itemId)
    {
        string value = ItemId.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out itemId);
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out itemId);
    }
}
