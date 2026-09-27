using Newtonsoft.Json.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using AscNet.Common.Util;
using AscNet.Table.V2.share.task;
using AscNet.Table.V2.share.theatre6;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    // 4.8 Theatre6 tasks 140351-140353 author condition type 136012 [Theatre6Character id, count]
    // ("Complete N battles as Nirvatia, Phantom Clash not included"). A stage battle advances only
    // the run character's counter, and the login task snapshot reports pending then achieved.
    private static void ValidateRequiemCharacterBattleMissionChecks()
    {
        using RequiemCase test = new("character-battles");
        test.UnlockGameplay();
        Dictionary<int, ConditionTable> conditions = TableReaderV2.Parse<ConditionTable>().ToDictionary(row => row.Id);
        HashSet<int> missionIds = TableReaderV2.Parse<Theatre6RewardTable>()
            .Where(row => row.TaskTimeLimitId is > 0)
            .SelectMany(row => test.PermanentMissions(row.TaskTimeLimitId!.Value)).ToHashSet();
        TaskTable task = TableReaderV2.Parse<TaskTable>()
            .Where(row => missionIds.Contains(row.Id) && conditions.TryGetValue(row.Condition, out ConditionTable? c) && c.Type == 136012)
            .OrderBy(row => row.Result).FirstOrDefault()
            ?? throw new InvalidDataException("4.8 Theatre6 tables must author a 136012 character-battle mission.");
        ConditionTable condition = conditions[task.Condition];
        int character = condition.Params[0];
        int target = Math.Max(1, task.Result ?? 1);
        Require(TableReaderV2.Parse<Theatre6CharacterTable>().Any(row => Convert.ToInt32(row.Id) == character),
            $"136012 condition {condition.Id} must name an authored Theatre6Character.");

        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6Module");
        Type mutationType = module.GetNestedType("Mutation", BindingFlags.NonPublic)!;
        object mutation = Activator.CreateInstance(mutationType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, [test.Session, true], null)!;
        MethodInfo record = module.GetMethod("RecordMetaProgress", BindingFlags.Static | BindingFlags.NonPublic)!;
        var staged = (Dictionary<int, int>)mutationType.GetProperty("TaskProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mutation)!;
        int other = TableReaderV2.Parse<Theatre6CharacterTable>().Select(row => Convert.ToInt32(row.Id)).First(id => id != character);
        record.Invoke(null, [mutation, "Battle", 1, 3, other]);
        AssertEqual(0, staged.GetValueOrDefault(condition.Id), "Another character's stage battle cannot advance 136012");
        record.Invoke(null, [mutation, "PvpBattle", 1, 2, 0]);
        AssertEqual(0, staged.GetValueOrDefault(condition.Id), "Phantom Clash matches are excluded from 136012");
        record.Invoke(null, [mutation, "Battle", 1, 3, character]);
        AssertEqual(1, staged.GetValueOrDefault(condition.Id), "The run character's stage battle advances 136012");

        // Login snapshot (TaskModule.BuildTaskData) over the persisted mode ledger: pending, then achieved.
        test.State.TaskProgress[condition.Id] = target - 1;
        AssertEqual(target - 1, test.TaskScheduleValue(task.Id), "136012 pending progress below the authored target");
        AssertEqual(1, test.PublicTaskRecord(task.Id).Value<int>("State"), "136012 mission stays pending below target");
        test.State.TaskProgress[condition.Id] = target;
        AssertEqual(target, test.TaskScheduleValue(task.Id), "136012 progress reaches the authored target");
        AssertEqual(3, test.PublicTaskRecord(task.Id).Value<int>("State"), "136012 mission is achieved at target");
    }
}

internal partial class Program
{
    // 4.8 Nirvatia tag buffs. Buff 10 ("Starts with 1 Dreamlure Skill", start-skill family 22) resolves by
    // AscNet policy through build tag 31 "Dreamlure"; a character without such skills still fails closed.
    // Buff 12 (trigger 5, "each time a skill levels up") applies its authored attribute pairs when the
    // granted skill later merges one level up, after the run survives a BSON save/reload.
    private static void ValidateRequiemNirvatiaTagBuffChecks()
    {
        // Production path: Theatre6PlayModeStartFightRequest -> StartRun -> InitializeCharacter ->
        // AddBuff(TagBuffIds[0] = 10) -> effect 5 -> GrantStartSkill(family 22). The committed run keeps
        // exactly one Nirvatia Dreamlure skill across a relog from the durable player document.
        (int nirvatia, int nirvatiaFashion, int initBuff, int nirvatiaDifficulty) = RequiemBuild(3, 0);
        AssertEqual(5, nirvatia, "Requiem build index 3 is Theatre6Character 5 (Nirvatia)");
        AssertEqual(10, initBuff, "Nirvatia's first authored tag buff is buff 10");
        using (RequiemCase start = new("nirvatia-start"))
        {
            start.UnlockGameplay();
            string modeKey = start.StartGameplayRun(nirvatia, nirvatiaFashion, initBuff, nirvatiaDifficulty);
            AssertRequiemStartSkill(start, modeKey, nirvatia, initBuff, 10281031); // Mourning Slash, lowest-id Dreamlure (tag 31)
            start.Relog("nirvatia-start");
            List<int> dreamlure = (start.Mode(modeKey)["Skills"] as JArray ?? []).Children<JObject>()
                .Select(skill => skill.Value<int>("SkillId"))
                .Where(id => TableReaderV2.Parse<Theatre6SkillTable>().Single(row => Convert.ToInt32(row.Id) == id).BuildTags.Contains(31))
                .ToList();
            AssertIntegerList([10281031], dreamlure.Select(Convert.ToInt64).ToArray(), "Relogged Nirvatia run keeps exactly the buff 10 Dreamlure grant");
        }

        using RequiemCase test = new("nirvatia-buffs");
        test.UnlockGameplay();
        Type module = RequiredAscNetGameServerType("AscNet.GameServer.Handlers.Theatre6Module");
        Type mutationType = module.GetNestedType("Mutation", BindingFlags.NonPublic)!;
        object mutation = Activator.CreateInstance(mutationType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, [test.Session, true], null)!;
        MethodInfo addBuff = module.GetMethod("AddBuff", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo grantSkill = module.GetMethod("GrantSkill", BindingFlags.Static | BindingFlags.NonPublic)!;
        var skills = TableReaderV2.Parse<Theatre6SkillTable>();
        var run = new AscNet.Common.Database.Theatre6RunState { StageId = 115, NextBuffUid = 1 };
        run.File.CharacterId = 5;

        var buff12 = TableReaderV2.Parse<Theatre6StageBuffTable>().Single(row => Convert.ToInt32(row.Id) == 12);
        Require(Convert.ToInt32(buff12.BuffTriggerType) == 5 && Convert.ToInt32(buff12.BuffEffectType) == 7,
            "4.8 buff 12 must author trigger 5 with attribute effect 7.");
        List<int> pairs = buff12.BuffEffectParams.Select(Convert.ToInt32).ToList();
        addBuff.Invoke(null, [mutation, run, 12, 0]);

        // Buff 10 grants exactly one Nirvatia level-1 in-pool Dreamlure (build tag 31) skill.
        addBuff.Invoke(null, [mutation, run, 10, 0]);
        Require(run.File.Skills.Count == 1, "Buff 10 grants exactly one starting skill.");
        int grantedId = run.File.Skills.Single().SkillId;
        var granted = skills.Single(row => Convert.ToInt32(row.Id) == grantedId);
        Require(Convert.ToInt32(granted.Character) == 5 && Convert.ToInt32(granted.Level) == 1 && !Convert.ToBoolean(granted.IsOutPool)
            && granted.BuildTags.Select(Convert.ToInt32).Contains(31), $"Buff 10 granted {grantedId}, not a Nirvatia level-1 Dreamlure skill.");

        // Save/reload the run, then a second copy merges one level up and fires buff 12.
        run = BsonSerializer.Deserialize<AscNet.Common.Database.Theatre6RunState>(run.ToBson());
        AssertEqual(grantedId, run.File.Skills.Single().SkillId, "Buff 10 skill survives run reload");
        Dictionary<int, int> before = run.File.Attrs.ToDictionary(attr => attr.AttrId, attr => attr.Value);
        grantSkill.Invoke(null, [mutation, run, grantedId, false]);
        int levelTwo = Convert.ToInt32(skills.Single(row => row.SkillKey == granted.SkillKey && Convert.ToInt32(row.Level) == 2).Id);
        AssertEqual(levelTwo, run.File.Skills.Single().SkillId, "Second Dreamlure copy merges into its level-2 row");
        for (int index = 0; index + 1 < pairs.Count; index += 2)
            AssertEqual(before.GetValueOrDefault(pairs[index]) + pairs[index + 1],
                run.File.Attrs.Single(attr => attr.AttrId == pairs[index]).Value, $"Buff 12 raises attr {pairs[index]} on skill level-up");

        // A different character state (Alpha, no Dreamlure skills) cannot borrow Nirvatia's pool.
        var alpha = new AscNet.Common.Database.Theatre6RunState { StageId = 115, NextBuffUid = 1 };
        alpha.File.CharacterId = 2;
        bool rejected = false;
        try { addBuff.Invoke(null, [mutation, alpha, 10, 0]); }
        catch (TargetInvocationException error) when (error.InnerException is AscNet.Common.ServerCodeException) { rejected = true; }
        Require(rejected && alpha.File.Skills.Count == 0, "Family 22 must not grant a non-Dreamlure skill to a character without candidates.");
    }
}
