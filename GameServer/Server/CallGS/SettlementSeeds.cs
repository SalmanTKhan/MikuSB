using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace MikuSB.GameServer.Server.CallGS;

/// <summary>
/// Ties a settlement to the seed its enter-level call issued, so one run pays once. The client retries a
/// settlement when the reply is lost, so a repeat of the same seed gets the first reply again. Entries live
/// for the server's life: a run still open across a restart is rejected.
/// </summary>
public static class SettlementSeeds
{
    private sealed class Entry
    {
        public uint Seed;
        public bool Entered = true;
        public string? Reply;
        public readonly HashSet<uint> Used = [];
    }

    public enum Outcome { Accept, Replay, Reject }

    // Settlements whose payload is known to echo nSeed and whose enter call replies with it.
    private static readonly HashSet<string> Guarded =
    [
        "FunTimed_LevelSettlement", "DonoDuelArena_LevelSettlement", "AlienDefense_LevelSettlement",
        "SeasonBoss_LevelSettlement", "FarmWarLogic_LevelSettlement", "OnlineTraining_LevelSettlement",
        "BagRogue_LevelSettlement"
    ];

    private static readonly ConcurrentDictionary<(int Uid, string Family), Entry> Entries = new();

    private static string Family(string api) => api.Split('_')[0];

    /// <summary>Records the seed if this was an enter-level reply.</summary>
    public static void RecordEnter(int uid, string api, string argument)
    {
        if (!api.EndsWith("_EnterLevel", StringComparison.Ordinal))
            return;

        uint seed = 0;
        try
        {
            if (JsonNode.Parse(argument) is JsonObject reply)
                uint.TryParse(reply["nSeed"]?.ToString(), out seed);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }

        Issue(uid, Family(api), seed);
    }

    public static void Issue(int uid, string family, uint seed) =>
        Entries[(uid, family)] = new Entry { Seed = seed };

    public static bool ConsumeEntry(int uid, string family)
    {
        if (!Entries.TryGetValue((uid, family), out var entry) || !entry.Entered)
            return false;
        entry.Entered = false;
        return true;
    }

    /// <summary>True the first time a key is used since the last enter; false with no enter at all.</summary>
    public static bool TryUseOnce(int uid, string family, uint key)
    {
        if (!Entries.TryGetValue((uid, family), out var entry))
            return false;
        lock (entry.Used)
            return entry.Used.Add(key);
    }

    public static bool IsGuarded(string api) => Guarded.Contains(api);

    public static bool Matches(int uid, string family, uint seed) =>
        seed != 0 && Entries.TryGetValue((uid, family), out var entry) && entry.Seed == seed;

    public static Outcome Check(int uid, string? sCmd, JsonNode? tbParam, out string? reply)
    {
        reply = null;
        if (sCmd == null || !Guarded.Contains(sCmd))
            return Outcome.Accept;

        uint seed = 0;
        if (tbParam is JsonObject param)
            uint.TryParse(param["nSeed"]?.ToString(), out seed);

        if (seed == 0 || !Entries.TryGetValue((uid, Family(sCmd)), out var entry) || entry.Seed != seed)
            return Outcome.Reject;

        reply = entry.Reply;
        return reply == null ? Outcome.Accept : Outcome.Replay;
    }

    public static void Complete(int uid, string? sCmd, string reply)
    {
        if (sCmd != null && Guarded.Contains(sCmd) && Entries.TryGetValue((uid, Family(sCmd)), out var entry))
            entry.Reply = reply;
    }
}
