using System.Net.Sockets;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.GameServer;
using AscNet.GameServer.Game;
using MongoDB.Driver;

namespace AscNet.Test;

internal partial class Program
{
    private static void ValidateCharacterSkillEffectiveCapDatabaseCompatibility()
    {
        List<Character> records = Character.collection.AsQueryable().ToList();
        List<Player> players = Player.collection.AsQueryable().ToList();
        if (records.Count == 0)
            throw new InvalidOperationException("No character records found for the all-character skill-cap check.");

        using TcpClient client = new();
        Session session = new("character-skill-cap-test", client);
        int characterCount = 0;
        int skillCount = 0;
        int clampedSkillCount = 0;
        int peakEffectiveLevel = 0;
        bool verifiedSelenaRegression = false;
        foreach (Character record in records)
        {
            Player? player = players.FirstOrDefault(candidate => candidate.PlayerData.Id == record.Uid);
            if (player is null)
                throw new InvalidOperationException($"Missing player state for character record {record.Uid}.");
            session.player = player;
            session.character = record;

            foreach (CharacterData character in record.Characters)
            {
                character.SkillList = character.SkillList
                    .Select(skill => new CharacterSkill { Id = skill.Id, Level = 35 })
                    .ToList();
                Dictionary<int, int> bonuses = CharacterPower.CharacterSkillBonusesForClient(session, character);
                if (CharacterPower.ClampCharacterSkillLevelsForClient(session, character))
                    clampedSkillCount++;
                Dictionary<int, int> caps = CharacterPower.CharacterSkillMaxLevelsForClient(session, character);
                foreach (CharacterSkill skill in character.SkillList)
                {
                    int bonus = bonuses.GetValueOrDefault((int)skill.Id);
                    if (skill.Level > caps.GetValueOrDefault((int)skill.Id)
                        || skill.Level + bonus > Character.CharacterSkillEffectiveLevelCap)
                        throw new InvalidOperationException(
                            $"Character {character.Id} skill {skill.Id} exceeds the effective client cap.");
                    peakEffectiveLevel = Math.Max(peakEffectiveLevel, skill.Level + bonus);
                    skillCount++;
                }
                if (character.Id == 1531005)
                {
                    AssertEqual(10, bonuses.GetValueOrDefault(153521), "Selena's resonance, overrun, and assignment bonuses");
                    AssertEqual(25, character.SkillList.First(skill => skill.Id == 153521).Level, "Selena's resonant skill level after cap");
                    verifiedSelenaRegression = true;
                }
                characterCount++;
            }
        }

        if (characterCount == 0 || skillCount == 0 || !verifiedSelenaRegression)
            throw new InvalidOperationException("The all-character skill-cap check did not include the Selena crash case.");
        Console.WriteLine($"All-character skill-cap stress check: characters={characterCount}, skills={skillCount}, clamped={clampedSkillCount}, peakEffectiveLevel={peakEffectiveLevel}; input levels were set to 35 in memory only.");
    }
}
