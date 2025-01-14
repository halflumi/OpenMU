﻿// <copyright file="DefaultDropGenerator.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic;

using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.GameLogic.Attributes;
using Nito.AsyncEx;

/// <summary>
/// The default drop generator.
/// </summary>
public class DefaultDropGenerator : IDropGenerator
{
    /// <summary>
    /// The amount of money which is dropped at least, and added to the gained experience.
    /// </summary>
    public static readonly int BaseMoneyDrop = 7;

    private readonly IRandomizer _randomizer;

    /// <summary>
    /// A re-useable list of drop item groups.
    /// </summary>
    private readonly List<DropItemGroup> _dropGroups = new(64);

    private readonly AsyncLock _lock = new();

    private readonly IList<ItemDefinition> _ancientItems;

    private readonly IList<ItemDefinition> _droppableItems;

    private readonly IList<ItemDefinition>?[] _droppableItemsPerMonsterLevel = new IList<ItemDefinition>?[byte.MaxValue + 1];

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultDropGenerator" /> class.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="randomizer">The randomizer.</param>
    public DefaultDropGenerator(GameConfiguration config, IRandomizer randomizer)
    {
        this._randomizer = randomizer;
        this._droppableItems = config.Items.Where(i => i.DropsFromMonsters).ToList();
        this._ancientItems = this._droppableItems.Where(i => i.PossibleItemSetGroups.Any(o => o.Options.Any(o => o.OptionType == ItemOptionTypes.AncientOption))).ToList();
    }

    /// <inheritdoc/>
    public async ValueTask<(IEnumerable<Item> Items, uint? Money)> GenerateItemDropsAsync(MonsterDefinition monster, int gainedExperience, Player player)
    {
        var character = player.SelectedCharacter;
        var map = player.CurrentMap?.Definition;
        if (map is null || character is null)
        {
            return (Enumerable.Empty<Item>(), null);
        }

        using var l = await this._lock.LockAsync();
        this._dropGroups.Clear();
        if (monster.DropItemGroups.MaxBy(g => g.Chance) is { Chance: >= 1.0 } alwaysDrops)
        {
            this._dropGroups.Add(alwaysDrops);
        }
        else if (monster.ObjectKind == NpcObjectKind.Destructible)
        {
            this._dropGroups.AddRange(monster.DropItemGroups ?? Enumerable.Empty<DropItemGroup>());
        }
        else
        {
            this._dropGroups.AddRange(monster.DropItemGroups ?? Enumerable.Empty<DropItemGroup>());
            this._dropGroups.AddRange(character.DropItemGroups ?? Enumerable.Empty<DropItemGroup>());
            this._dropGroups.AddRange(map.DropItemGroups ?? Enumerable.Empty<DropItemGroup>());
            this._dropGroups.AddRange(await GetQuestItemGroupsAsync(player).ConfigureAwait(false) ?? Enumerable.Empty<DropItemGroup>());

            this._dropGroups.RemoveAll(g => !IsGroupRelevant(monster, g));
            this._dropGroups.Sort((x, y) => x.Chance.CompareTo(y.Chance));
        }

        var totalChance = this._dropGroups.Sum(g => g.Chance);
        uint money = 0;
        IList<Item>? droppedItems = null;
        for (int i = 0; i < monster.NumberOfMaximumItemDrops; i++)
        {
#if DOWNSTREAM
            var group = this.SelectRandomGroup(this._dropGroups, totalChance, monster[Stats.Level]);
#else
            var group = this.SelectRandomGroup(this._dropGroups, totalChance);
#endif
            if (group is null)
            {
                continue;
            }

#if DOWNSTREAM
            var item = this.GenerateItemDropOrMoney(monster, group, gainedExperience, await GetPartyCharacterClassesAsync(player).ConfigureAwait(false), out var droppedMoney);
#else
            var item = this.GenerateItemDropOrMoney(monster, group, gainedExperience, out var droppedMoney);
#endif
            if (item is not null)
            {
                droppedItems ??= new List<Item>(1);
                droppedItems.Add(item);
            }

            if (droppedMoney is not null)
            {
                money += droppedMoney.Value;
            }
        }

        this._dropGroups.Clear();
        return (droppedItems ?? Enumerable.Empty<Item>(), money > 0 ? money : null);
    }

    /// <inheritdoc/>
    public Item? GenerateItemDrop(DropItemGroup selectedGroup)
    {
        var item = selectedGroup.ItemType == SpecialItemType.Ancient
            ? this.GenerateRandomAncient()
            : this.GenerateRandomItem(selectedGroup.PossibleItems);

        if (item is null)
        {
            return null;
        }

        if (selectedGroup is ItemDropItemGroup itemDropItemGroup)
        {
            item.Level = (byte)this._randomizer.NextInt(itemDropItemGroup.MinimumLevel, itemDropItemGroup.MaximumLevel + 1);
        }
        else if (selectedGroup.ItemLevel is { } itemLevel)
        {
            item.Level = itemLevel;
        }
        else
        {
            // no level defined, so it stays at 0.
        }

        item.Level = Math.Min(item.Level, item.Definition!.MaximumItemLevel);

        if (selectedGroup.ItemType == SpecialItemType.Ancient)
        {
            this.ApplyRandomAncientOption(item);
        }
        else if (selectedGroup.ItemType == SpecialItemType.Excellent)
        {
            this.AddRandomExcOptions(item);
        }
        else
        {
            // nothing to add, others make no sense here.
        }

        return item;
    }

    /// <inheritdoc/>
    public (Item? Item, uint? Money, ItemDropEffect DropEffect) GenerateItemDrop(IEnumerable<DropItemGroup> groups)
    {
        var group = this.SelectRandomGroup(groups.OrderBy(group => group.Chance), 1.0);
        if (group is null)
        {
            return (null, null, ItemDropEffect.Undefined);
        }

        if (@group is ItemDropItemGroup itemDropItemGroup)
        {
            if (group.ItemType == SpecialItemType.Money)
            {
                return (null, (uint)itemDropItemGroup.MoneyAmount, itemDropItemGroup.DropEffect);
            }
        }

        return (this.GenerateItemDrop(group), null, ItemDropEffect.Undefined);
    }

    /// <summary>
    /// Gets a random item.
    /// </summary>
    /// <param name="monsterLvl">The monster level.</param>
    /// <param name="socketItems">If set to <c>true</c>, it selects only items with sockets.</param>
    /// <returns>A random item.</returns>
    protected Item? GenerateRandomItem(int monsterLvl, bool socketItems)
    {
        var possible = this.GetPossibleList(monsterLvl, socketItems);
        var item = this.GenerateRandomItem(possible);
        if (item is null)
        {
            return null;
        }

        item.Level = GetItemLevelByMonsterLevel(item.Definition!, monsterLvl);
        return item;
    }

    /// <summary>
    /// Applies random options to the item.
    /// </summary>
    /// <param name="item">The item.</param>
    protected void ApplyRandomOptions(Item item)
    {
        item.Durability = item.GetMaximumDurabilityOfOnePiece();
        foreach (var option in item.Definition!.PossibleItemOptions.Where(o => o.AddsRandomly))
        {
            for (int i = 0; i < option.MaximumOptionsPerItem; i++)
            {
                if (this._randomizer.NextRandomBool(option.AddChance))
                {
                    var remainingOptions = option.PossibleOptions.Where(possibleOption => item.ItemOptions.All(link => link.ItemOption != possibleOption));
                    var newOption = remainingOptions.SelectRandom(this._randomizer);
                    var itemOptionLink = new ItemOptionLink();
                    itemOptionLink.ItemOption = newOption;
                    itemOptionLink.Level = newOption?.LevelDependentOptions.Select(l => l.Level).SelectRandom() ?? 0;
                    item.ItemOptions.Add(itemOptionLink);
                }
            }
        }

        if (item.Definition.MaximumSockets > 0)
        {
            item.SocketCount = this._randomizer.NextInt(1, item.Definition.MaximumSockets + 1);
        }

        if (item.CanHaveSkill())
        {
            item.HasSkill = this._randomizer.NextRandomBool(50);
        }
    }

    /// <summary>
    /// Gets a random excellent item.
    /// </summary>
    /// <param name="monsterLvl">The monster level.</param>
    /// <returns>A random excellent item.</returns>
#if DOWNSTREAM
    protected Item? GenerateRandomExcellentItem(int monsterLvl, ICollection<CharacterClass> classes)
#else
    protected Item? GenerateRandomExcellentItem(int monsterLvl)
#endif
    {
#if DOWNSTREAM
        // In an excellent-only config, items that can't be excellent also get affected by the 25-level penalty of excellent drops.
        // Instead of rolling at -25 level, join the non-excellent-able items at the monster level and excellent-able items at -25 levels for a roll.
        // Also, instead of making nothing drop below level 25, monster of level <= 37 has an increasing chance of dropping level 1~12 excellent items.
        static bool CanBeExcellent(ItemDefinition definition) => definition.PossibleItemOptions.Any(o => o.PossibleOptions.Any(p => p.OptionType == ItemOptionTypes.Excellent));

        var possibleCommon = this.GetPossibleList(monsterLvl)?.Where(def => !CanBeExcellent(def)).ToList();

        IList<ItemDefinition>? possibleEx = null;
        if (monsterLvl >= 37)
        {
            possibleEx = this.GetPossibleList(monsterLvl - 25)?.Where(CanBeExcellent).ToList();
        }
        else if (this._randomizer.NextInt(0, 37) < monsterLvl) // n/37 chance to drop an excellent item.
        {
            possibleEx = this.GetPossibleList(12)?.Where(CanBeExcellent).ToList();
        }

        bool Suitable(ItemDefinition definition) => definition.QualifiedCharacters.Count == 0 || classes.Count == 0 || definition.QualifiedCharacters.Intersect(classes).Any();

        List<ItemDefinition> possible = new(64);
        possible.AddRange(possibleCommon?.Where(Suitable) ?? Enumerable.Empty<ItemDefinition>());
        possible.AddRange(possibleEx?.Where(Suitable) ?? Enumerable.Empty<ItemDefinition>());
#else
        if (monsterLvl < 25)
        {
            return null;
        }

        var possible = this.GetPossibleList(monsterLvl - 25);
#endif
        var item = this.GenerateRandomItem(possible);
        if (item is null)
        {
            return null;
        }

        item.HasSkill = item.CanHaveSkill(); // every excellent item got skill

        this.AddRandomExcOptions(item);
        return item;
    }

    /// <summary>
    /// Gets a random ancient item.
    /// </summary>
    /// <returns>A random ancient item.</returns>
    protected Item? GenerateRandomAncient()
    {
        var item = this.GenerateRandomItem(this._ancientItems);
        if (item is null)
        {
            return null;
        }

        item.HasSkill = item.CanHaveSkill(); // every ancient item got skill

        this.ApplyRandomAncientOption(item);
        return item;
    }

    private static byte GetItemLevelByMonsterLevel(ItemDefinition itemDefinition, int monsterLevel)
    {
        return Math.Min((byte)((monsterLevel - itemDefinition.DropLevel) / 3), itemDefinition.MaximumItemLevel);
    }

    private static async ValueTask<IEnumerable<DropItemGroup>> GetQuestItemGroupsAsync(Player player)
    {
        if (player.SelectedCharacter is not { } character)
        {
            return Enumerable.Empty<DropItemGroup>();
        }

        if (player.Party is { } party)
        {
            return await party.GetQuestDropItemGroupsAsync(player).ConfigureAwait(false);
        }

        return character.GetQuestDropItemGroups();
    }

    private static bool IsGroupRelevant(MonsterDefinition monsterDefinition, DropItemGroup group)
    {
        if (group is null)
        {
            return false;
        }

        if (group.MinimumMonsterLevel.HasValue && monsterDefinition[Stats.Level] < group.MinimumMonsterLevel)
        {
            return false;
        }

        if (group.MaximumMonsterLevel.HasValue && monsterDefinition[Stats.Level] > group.MaximumMonsterLevel)
        {
            return false;
        }

        if (group.Monster is { } monster && !monster.Equals(monsterDefinition))
        {
            return false;
        }

        return true;
    }

#if DOWNSTREAM
    private static async ValueTask<ICollection<CharacterClass>> GetPartyCharacterClassesAsync(Player player)
    {
        if (player.SelectedCharacter is not { } character)
        {
            return new List<CharacterClass>();
        }

        if (player.Party is { } party)
        {
            return await party.GetCharacterClassesAsync(player).ConfigureAwait(false);
        }

        return new List<CharacterClass> { character.CharacterClass! };
    }
#endif

    private Item? GenerateRandomItem(ICollection<ItemDefinition>? possibleItems)
    {
        if (possibleItems is null || !possibleItems.Any())
        {
            return null;
        }

        var item = new TemporaryItem
        {
            Definition = possibleItems.ElementAt(this._randomizer.NextInt(0, possibleItems.Count)),
        };

        this.ApplyRandomOptions(item);

        return item;
    }

    private void ApplyRandomAncientOption(Item item)
    {
        var ancientSet = item.Definition?.PossibleItemSetGroups.Where(g => g!.Options.Any(o => o.OptionType == ItemOptionTypes.AncientOption)).SelectRandom(this._randomizer);
        if (ancientSet is null)
        {
            return;
        }

        var itemOfSet = ancientSet.Items.First(i => i.ItemDefinition == item.Definition);
        item.ItemSetGroups.Add(itemOfSet);
        var bonusOption = itemOfSet.BonusOption ?? throw Error.NotInitializedProperty(itemOfSet, nameof(itemOfSet.BonusOption)); // for example: +5str or +10str
        var bonusOptionLink = new ItemOptionLink();
        bonusOptionLink.ItemOption = bonusOption;
        bonusOptionLink.Level = bonusOption.LevelDependentOptions.Select(o => o.Level).SelectRandom();
        item.ItemOptions.Add(bonusOptionLink);
    }

    private void AddRandomExcOptions(Item item)
    {
        var possibleItemOptions = item.Definition!.PossibleItemOptions;
        var excellentOptions = possibleItemOptions.FirstOrDefault(o => o.PossibleOptions.Any(p => p.OptionType == ItemOptionTypes.Excellent));
        if (excellentOptions is null)
        {
            return;
        }

        for (int i = item.ItemOptions.Count(o => o.ItemOption?.OptionType == ItemOptionTypes.Excellent); i < excellentOptions.MaximumOptionsPerItem; i++)
        {
            if (i == 0)
            {
                var itemOptionLink = new ItemOptionLink();
                itemOptionLink.ItemOption = excellentOptions.PossibleOptions.SelectRandom(this._randomizer);
                item.ItemOptions.Add(itemOptionLink);
                continue;
            }

            if (this._randomizer.NextRandomBool(excellentOptions.AddChance))
            {
                var option = excellentOptions.PossibleOptions.SelectRandom(this._randomizer);
                while (item.ItemOptions.Any(o => o.ItemOption == option))
                {
                    option = excellentOptions.PossibleOptions.SelectRandom(this._randomizer);
                }

                var itemOptionLink = new ItemOptionLink();
                itemOptionLink.ItemOption = option;
                item.ItemOptions.Add(itemOptionLink);
            }
        }
    }

#if DOWNSTREAM
    private Item? GenerateItemDropOrMoney(MonsterDefinition monster, DropItemGroup selectedGroup, int gainedExperience, ICollection<CharacterClass> classes, out uint? droppedMoney)
#else
    private Item? GenerateItemDropOrMoney(MonsterDefinition monster, DropItemGroup selectedGroup, int gainedExperience, out uint? droppedMoney)
#endif
    {
        droppedMoney = null;
        if (selectedGroup.PossibleItems?.Count > 0)
        {
            return this.GenerateItemDrop(selectedGroup);
        }

        switch (selectedGroup.ItemType)
        {
            case SpecialItemType.Ancient:
                return this.GenerateRandomAncient();
            case SpecialItemType.Excellent:
#if DOWNSTREAM
                return this.GenerateRandomExcellentItem((int)monster[Stats.Level], classes);
#else
                return this.GenerateRandomExcellentItem((int)monster[Stats.Level]);
#endif
            case SpecialItemType.RandomItem:
                return this.GenerateRandomItem((int)monster[Stats.Level], false);
            case SpecialItemType.SocketItem:
                return this.GenerateRandomItem((int)monster[Stats.Level], true);
            case SpecialItemType.Money:
                droppedMoney = (uint)(gainedExperience + BaseMoneyDrop);
                return null;
            default:
                // none
                return null;
        }
    }

#if DOWNSTREAM
    private DropItemGroup? SelectRandomGroup(IEnumerable<DropItemGroup> groups, double totalChance, float? monsterLvl = null)
#else
    private DropItemGroup? SelectRandomGroup(IEnumerable<DropItemGroup> groups, double totalChance)
#endif
    {
#if DOWNSTREAM
        // Give Jewels higher drop rates, proportional to the monster level.
        // Formula: f(x, m) = 9 * ((x - m) / (148 - m))^2
        double GetBonusChance(DropItemGroup group)
        {
            if (!group.Description.StartsWith("The Jewel") || monsterLvl is not { } lvl)
            {
                return 0.0;
            }

            double min_drop_level = group.MinimumMonsterLevel ?? 0.0;
            double normalization = (lvl - min_drop_level) / (148.0 - min_drop_level);
            return group.Chance * (9.0 * Math.Pow(normalization, 2.0));
        }

        totalChance += groups.Sum(GetBonusChance);
#endif

        var lot = this._randomizer.NextDouble();
        if (totalChance > 1.0)
        {
            lot *= totalChance;
        }

        foreach (var group in groups)
        {
#if DOWNSTREAM
            var calibedChance = group.Chance + GetBonusChance(group);
            if (lot > calibedChance)
            {
                lot -= calibedChance;
            }
#else
            if (lot > group.Chance)
            {
                lot -= group.Chance;
            }
#endif
            else
            {
                return group;
            }
        }

        return null;
    }

    private IList<ItemDefinition>? GetPossibleList(int monsterLevel, bool socketItems = false)
    {
        if (monsterLevel is < byte.MinValue or > byte.MaxValue)
        {
            return null;
        }

        return this._droppableItemsPerMonsterLevel[monsterLevel]
            ??= (from it in this._droppableItems
            where (it.DropLevel <= monsterLevel)
                  && (it.DropLevel > monsterLevel - 12)
                  && (!socketItems || it.MaximumSockets > 0)
            select it).ToList();
    }
}