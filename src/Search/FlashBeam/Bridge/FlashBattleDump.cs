using CombatSolver.Engine.InCombat.Flash;

namespace CombatSolver.FlashBeam;

/// <summary>Field-level text dump of a <see cref="FlashBattle"/> for differential reports.</summary>
internal static class FlashBattleDump
{
    public static List<string> Lines(in FlashBattle battle)
    {
        List<string> lines =
        [
            $"Round={battle.Round}", $"PlayerTurn={battle.PlayerTurn}", $"ShuffleEventCount={battle.ShuffleEventCount}",
            $"ShufflesCrossed={battle.ShufflesCrossed}", $"NextCombatId={battle.NextCombatId}", $"Energy={battle.Energy}",
            $"Stars={battle.Stars}", $"Gold={battle.Gold}", $"Side={battle.Side}", $"Phase={battle.Phase}",
            $"Outcome={battle.Outcome}", $"Flags={battle.Flags}", $"MaxHandSize={battle.MaxHandSize}",
            $"Player={Creature(battle.Player)}", $"Osty={Creature(battle.Osty)}",
            $"OrbCapacity={battle.OrbCapacity}", $"PotionSlotCount={battle.PotionSlotCount}",
        ];
        for (int counter = 0; counter < FlashLimits.Counters; counter++)
        {
            if (battle.Counters[counter] != 0)
                lines.Add($"Counter[{(FlashCounter)counter}]={battle.Counters[counter]}");
        }
        for (int stream = 0; stream < FlashLimits.RngStreams; stream++)
        {
            FlashRngState rng = battle.Rng[stream];
            lines.Add($"Rng[{(FlashRngStream)stream}]={rng.Counter}:{rng.S0:X}:{rng.S1:X}:{rng.S2:X}:{rng.S3:X}");
        }
        for (int index = 0; index < battle.OrbCount; index++)
            lines.Add($"Orb[{index}]={FlashRegistry.Orb(battle.Orbs[index].Id).NativeId} aux={battle.Orbs[index].Aux}");
        List<byte> roster = [];
        for (int position = 0; position < battle.RosterCount; position++)
            roster.Add(battle.Roster[position]);
        lines.Add("Roster=" + string.Join(",", roster));
        for (int index = 0; index < battle.EnemyCount; index++)
        {
            FlashEnemy enemy = battle.Enemies[index];
            string monster = FlashRegistry.Monster(enemy.MonsterId).NativeId;
            List<byte> recentMoves = [];
            for (int position = 0; position < Math.Min((int)enemy.LoggedMoves, FlashLimits.EnemyRecentMoves); position++)
                recentMoves.Add(enemy.RecentMoves[position]);
            string recent = string.Join(",", recentMoves);
            lines.Add($"Enemy[{index}]={monster} {Creature(enemy.Creature)} move={enemy.Move} flags={enemy.Flags} death={enemy.DeathPhase} "
                + $"combatId={enemy.CombatId} rules={enemy.Rules} slot={enemy.Slot} logged={enemy.LoggedMoves} recent=[{recent}] everUsed={enemy.EverUsedMoves:X} "
                + $"ai=[{enemy.Ai[0]},{enemy.Ai[1]},{enemy.Ai[2]},{enemy.Ai[3]}] steam={enemy.SteamPhase}");
        }
        for (int index = 0; index < battle.PowerCount; index++)
        {
            FlashPower power = battle.Powers[index];
            lines.Add($"Power[{index}]={FlashRegistry.Power(power.Id).NativeId} owner={power.Owner} applier={power.Applier} "
                + $"target={power.Target} amount={power.Amount} onTurnStart={power.AmountOnTurnStart} flags={power.Flags} "
                + $"aux={power.Aux0}/{power.Aux1}/{power.Aux2}");
        }
        foreach (FlashPile pile in Enum.GetValues<FlashPile>())
        {
            ReadOnlySpan<FlashCard> cards = FlashPiles.ReadCards(battle, pile);
            for (int index = 0; index < cards.Length; index++)
                lines.Add($"{pile}[{index}]={Card(cards[index])}");
        }
        for (int slot = 0; slot < FlashLimits.DetachedCards; slot++)
        {
            if (battle.DetachedCards[slot].Id != 0)
                lines.Add($"Detached[{slot}]={Card(battle.DetachedCards[slot])}");
        }
        for (int index = 0; index < battle.RelicCount; index++)
            lines.Add($"Relic[{index}]={FlashRegistry.Relic(battle.Relics[index].Id).NativeId} flags={battle.Relics[index].Flags} counter={battle.Relics[index].Counter}");
        for (int slot = 0; slot < battle.PotionSlotCount; slot++)
            lines.Add($"Potion[{slot}]={(battle.Potions[slot] == 0 ? "-" : FlashRegistry.Potion((ushort)(battle.Potions[slot] - 1)).NativeId)}");
        return lines;
    }

    /// <summary>Lines present on one side only, prefixed with the side name.</summary>
    public static List<string> Diff(in FlashBattle expected, in FlashBattle actual, string expectedName, string actualName)
    {
        List<string> left = Lines(expected);
        List<string> right = Lines(actual);
        HashSet<string> rightSet = [.. right];
        HashSet<string> leftSet = [.. left];
        List<string> result = [];
        result.AddRange(left.Where(line => !rightSet.Contains(line)).Select(line => $"{expectedName}: {line}"));
        result.AddRange(right.Where(line => !leftSet.Contains(line)).Select(line => $"{actualName}: {line}"));
        if (result.Count == 0 && !FlashFingerprint.Equal(expected, actual))
            result.Add("字节不同但字段转储相同（检查未转储的字段或填充）。");
        return result;
    }

    private static string Creature(in FlashCreature creature)
        => $"hp={creature.CurrentHp}/{creature.MaxHp} block={creature.Block} flags={creature.Flags}";

    private static string Card(in FlashCard card)
        => $"{FlashRegistry.Card(card.Id).NativeId}+{card.Upgrade} flags={card.Flags} kw={card.Keywords} cost={card.BaseCost} "
            + $"star={card.BaseStarCost} replay={card.ReplayCount} mods={card.EnergyModifierCount} state={card.CostState} "
            + $"vars={card.Var0}/{card.Var1} private={card.Private} dampen={card.DampenOriginalUpgrade}";
}
