using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class EquippedAilmentEffectsTests
{
    [Fact]
    public async Task Equipped_ailments_and_effects_apply_at_their_own_contacts()
    {
        await NearbyChillTracksMovementAndRemoval();
        await DyadusIncreasesIgniteDamageAgainstChilledTargets();
        await ReplicaStampedeScalesTravelHitsOnlyInItsOwnSockets();
        await DevastatorExplodesAnAttackKillForOneTenthOfItsMaximumLife();
        await VerifyPaidManaFlaskCharges();
        await UniqueIgniteDurationModifiersStayInTheIgniteMultiplier();
        await VolkuursGuidanceShortensPoisonFromAPaidSpell();
    }

    static async Task NearbyChillTracksMovementAndRemoval()
    {
        foreach (bool cluster in new[] { false, true })
            await WithContactWitness(async (fixture, clock, target) =>
            {
                var player = fixture.Player;
                var items = Game.ItemsOf(player);
                var jewel = cluster
                    ? AllocateWitnessNotable(player, "AfflictionNotableChillingPresence")
                    : AddWitnessJewel(player, "That Which Was Taken", "AnimalCharmNearbyEnemiesAreChilled", 1, 1);
                var stat = cluster ? Stat.LocalDisplayNearbyEnemiesAreChilled : Stat.NearbyEnemiesAreChilled;
                Assert.Equal(1, Game.CharacterPlayerStats(player).GetValueOrDefault(stat));
                var chill = MonsterElementalAilments.Kind.Chill;
                async Task MoveAndStep(int x)
                {
                    clock.At += 1;
                    player.Walk.Teleport(x, 100, clock.At);
                    player.Area.Grid.Set(new(player.Id), (x, 100));
                    await Game.EnemyDebuffsTick(player, clock.At);
                    player.Area.Zones.Update(moved: true);
                    await Game.EnemyDebuffsTick(player, clock.At);
                }
                await MoveAndStep(140);
                Assert.Equal(0, target.Ailments.Magnitude(chill, clock.At));
                await MoveAndStep(100);
                Assert.Equal(10, target.Ailments.Magnitude(chill, clock.At));
                Assert.Equal(0.9, target.Ailments.ActionFactor(clock.At), 6);
                await MoveAndStep(140);
                Assert.Equal(0, target.Ailments.Magnitude(chill, clock.At));
                await MoveAndStep(100);
                Assert.Equal(10, target.Ailments.Magnitude(chill, clock.At));
                Assert.NotNull(items.Remove(PlayerInventories.PassiveJewels, jewel));
                Game.RecomputePoolsAndSpeed(player);
                Assert.Equal(0, Game.CharacterPlayerStats(player).GetValueOrDefault(stat));
                await MoveAndStep(100);
                Assert.Equal(0, target.Ailments.Magnitude(chill, clock.At));
            });
        await WithContactWitness(async (fixture, clock, target) =>
        {
            var player = fixture.Player;
            using var wire = new PartyFollowerTests.Wire();
            var peer = AddAuraWitnessPeer(player, clock.At, wire);
            AllocateWitnessNotable(player, "AfflictionNotableChillingPresence");
            AddWitnessJewel(peer, "That Which Was Taken", "AnimalCharmNearbyEnemiesAreChilled", 1, 1);
            using var effect = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
            {
                if (ReferenceEquals(owner, peer)) stats[Stat.VirtualChillEffectPlusPct] = 50;
                return stats;
            });
            var chill = MonsterElementalAilments.Kind.Chill;
            Assert.Equal(1, Game.CharacterPlayerStats(peer).GetValueOrDefault(Stat.NearbyEnemiesAreChilled));
            target.Ailments.ApplyAura(chill, 42, 30, clock.At, _ => 0, _ => 0, stated: 7);
            await StepAuraWitness(player, clock.At);
            await StepAuraWitness(peer, clock.At);
            Assert.Equal(15, target.Ailments.Magnitude(chill, clock.At));
            Game.MonsterDebuffsChanged(player, target.ObjectId);
            await StepAuraWitness(player, clock.At);
            Assert.Equal(15, target.Ailments.Magnitude(chill, clock.At));
            Game.ForgetSourceValues(peer);
            Assert.Equal(10, target.Ailments.Magnitude(chill, clock.At));
            Game.ForgetSourceValues(player);
            Assert.Equal(7, target.Ailments.Magnitude(chill, clock.At));
        });
    }

    static async Task DyadusIncreasesIgniteDamageAgainstChilledTargets()
    {
        var axe = NativeUnique("Dyadus", "BurningDamageToChilledEnemiesUniqueOneHandAxe2", 100);
        async Task<double> Ignite(bool equipped, bool priorChill)
        {
            double dps = 0;
            await WithContactWitness(async (fixture, clock, target) =>
            {
                var player = fixture.Player;
                Game.ItemsOf(player).Add(PlayerInventories.Offhand1, 0, 0, equipped ? axe : axe with { Mods = [] });
                Assert.Equal(equipped ? 100 : 0, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.IgniteDamagePlusPctVsChilledEnemies));
                using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
                {
                    if (ReferenceEquals(owner, player))
                    {
                        values[Stat.AlwaysIgnite] = 1;
                        values[Stat.IgniteDamagePlusPct] = 100;
                    }
                    return values;
                });
                ushort fire = Assert.Single(fixture.Equip(("Fireball", 18)));
                if (priorChill) Assert.NotNull(target.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, 10,
                    clock.At, Get(), Get()));
                await LandWitness(fixture, clock, target, fire);
                dps = target.Ignites.PerSecond(target.PlayerHitAt + 0.01);
                Assert.True(dps > 0);
            });
            return dps;
        }
        double baseDps = await Ignite(equipped: false, priorChill: true);
        Assert.Equal(baseDps, await Ignite(equipped: true, priorChill: false));
        Assert.InRange(await Ignite(equipped: true, priorChill: true), 1.49 * baseDps, 1.51 * baseDps);
        await CheckPlacedBrandIgniteScaling(axe);
    }

    static async Task ReplicaStampedeScalesTravelHitsOnlyInItsOwnSockets()
    {
        var boots = NativeUnique("Replica Stampede", "TravelSkillMoreDamageUnique__1", 80);
        foreach (var (name, links, inBoots) in new[]
        {
            ("Frostblink", 2, true), ("Frostblink", 3, true), ("Frostblink", 4, true),
            ("Frostblink", 2, false), ("Frostblink", 3, false), ("Frostblink", 4, false), ("Ice Nova", 2, true),
        })
        {
            uint plain = await Hit(name, links, inBoots, false);
            uint modified = await Hit(name, links, inBoots, true);
            if (name == "Frostblink" && inBoots)
                Assert.InRange((double)modified, plain * 1.8 - 3, plain * 1.8 + 3);
            else Assert.Equal(plain, modified);
        }

        async Task<uint> Hit(string name, int links, bool inBoots, bool equippedMod)
        {
            uint damage = 0;
            await WithContactWitness(async (fixture, clock, target) =>
            {
                var player = fixture.Player;
                var names = new[] { name, "Increased Area of Effect Support", "Added Cold Damage Support", "Controlled Destruction Support" };
                var worn = equippedMod ? boots : boots with { Mods = [] };
                ushort skill;
                if (inBoots)
                {
                    var sockets = names.Take(links).Select(name =>
                    {
                        var gem = Program.Items.FindGem(name)!.Value;
                        return new ItemSocket(SocketColour.White, ItemCatalog.Create(gem.Type) with
                        {
                            GemVariant = gem.Variant.Index, GemExperience = Program.Items.Gem(gem.Type)!.ExperienceFor(18),
                        });
                    }).ToArray();
                    Game.ItemsOf(player).Add(PlayerInventories.Boots, 0, 0, worn with { Sockets = sockets, Links = [(byte)links] });
                    skill = Assert.Single(Game.GemSkillEffects(player)).Id;
                }
                else
                {
                    Game.ItemsOf(player).Add(PlayerInventories.Boots, 0, 0, worn);
                    skill = Assert.Single(fixture.Equip(names.Take(links).Select(n => (n, 18)).ToArray()));
                }
                var gemContext = Game.SupportedSkill(player, skill);
                bool supportedByBoots = equippedMod && inBoots && name == "Frostblink";
                Assert.Equal(links - 1, gemContext.Supports.Count(support => support.Gem.Socket < links));
                Assert.Equal(supportedByBoots ? 1 : 0,
                    gemContext.Supports.Count(support => support.Effect.Id == "ModTravelSkillsDealMoreDamage"));
                Assert.Equal(supportedByBoots ? 80 : 0, gemContext.Stats
                    .GetValueOrDefault(Stat.UniqueReplicaTheStampedeTravelSocketedSkillDamagePlusPctFinal));
                if (name == "Frostblink")
                {
                    player.Use(new(1, "stampede", 350, 350, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
                    player.Walk.Teleport(100, 100, clock.At);
                    Game.RecomputePoolsAndSpeed(player);
                    uint mana = player.PlayerMana;
                    await Game.UseInstantSkillAtLocation(player, skill, (145, 100));
                    Assert.True(player.PlayerMana < mana);
                    Assert.NotEqual((100, 100), player.Walk.Position(Game.ProjectileWorldTime(player)));
                    Assert.InRange(target.Life, 1u, target.MaxLife - 1);
                }
                else await LandWitness(fixture, clock, target, skill);
                damage = target.MaxLife - target.Life;
            });
            return damage;
        }
    }

    static async Task DevastatorExplodesAnAttackKillForOneTenthOfItsMaximumLife()
    {
        foreach (var (name, allocated, passes, other, expected) in new[]
        {
            ("Heavy Strike", true, true, false, 100), ("Heavy Strike", true, false, false, 0),
            ("Heavy Strike", false, true, false, 0), ("Ice Nova", true, true, false, 0),
            ("Ice Nova", true, true, true, 80),
        })
            await WithContactWitness(async (fixture, clock, original) =>
            {
                var player = fixture.Player;
                if (allocated) AllocateWitnessNotable(player, "AfflictionNotableDevastator");
                using var extra = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
                {
                    if (ReferenceEquals(owner, player) && other) stats[Stat.EnemiesExplodeForPctLifeAsPhysicalDamage] = 8;
                    return stats;
                });
                var killed = new SpawnedMonster("Devastator victim", new(104, 100, 0), 0, 0, 1000)
                {
                    ObjectId = original.ObjectId, Hostile = true, NoRewards = true, Life = 1,
                    Variety = Game.Monsters.Find("Metadata/Monsters/Zombies/ZombieBite"),
                };
                player.Area.Monsters[killed.ObjectId] = killed;
                // Ice Nova targets the corpse at its edge. The neighbour is outside the spell and inside the explosion.
                if (name == "Ice Nova")
                {
                    killed.Walk.Teleport(126, 100, clock.At);
                    player.Area.Grid.Set(new(killed.ObjectId), (126, 100));
                }
                var near = AddWitness(player, 901, (name == "Ice Nova" ? 143 : 121, 100));
                var far = AddWitness(player, 902, (165, 100));
                using var rolls = GameEvents.OutgoingDamage.Add("Game", (owner, _, victim, packet) =>
                {
                    if (ReferenceEquals(owner, player) && ReferenceEquals(victim, killed)) SetWitnessRoll(player, new QualifyingRoll(passes));
                    return packet;
                });
                ushort skill = Assert.Single(fixture.Equip((name, 18)));
                Game.RecomputePoolsAndSpeed(player);
                Assert.Equal(allocated ? 15 : 0, Game.CharacterPlayerStats(player)
                    .GetValueOrDefault(Stat.EnemiesExplodeOnDeathByAttackFor10PctLifeAsPhysicalDamage));
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (104, 100), killed.ObjectId));
                Assert.True(player.PlayerMana < mana);
                Assert.Equal(0u, killed.Life);
                Assert.Equal((uint)expected, near.MaxLife - near.Life);
                Assert.Equal(far.MaxLife, far.Life);
                Assert.Equal(expected > 0, killed.CorpseConsumed);
            });
    }

    static async Task VerifyPaidManaFlaskCharges()
    {
        foreach (var (name, starting, paid, passRoll, expected) in new[]
        {
            ("Small Mana Flask", 24, true, true, 1), ("Small Mana Flask", 24, true, false, 0),
            ("Small Mana Flask", 0, false, true, 0), ("Quicksilver Flask", 60, true, true, 0),
        })
            await WithContactWitness(async (fixture, clock, _) =>
            {
                var player = fixture.Player;
                var items = Game.ItemsOf(player);
                AllocateWitnessNotable(player, "AfflictionNotableLiquidInspiration");
                Assert.Equal(25, Game.CharacterPlayerStats(player).GetValueOrDefault(Stat.GainPowerChargeOnManaFlaskUsePctChance));
                var type = Program.Items.Find(name)!;
                var flask = ItemCatalog.Create(type) with { Charges = (ushort)starting };
                const byte slot = 2;
                uint flaskId = items.Add(PlayerInventories.Flask, slot, 0, flask).Id;
                Game.RecomputePoolsAndSpeed(player);
                player.PlayerMana = 10;
                SetWitnessRoll(player);
                if (!passRoll)
                    typeof(AreaFrames).GetProperty(nameof(AreaFrames.Rng))!.SetValue(player.Frames, new Random(17));
                await Game.UseFlask(player, slot);
                Assert.Equal(paid, Assert.Single(items.Items(PlayerInventories.Flask), i => i.Id == flaskId).Item.Charges < starting);
                Assert.Equal(expected, SettleWitnessCharges(player, clock.At));
            });
        await WithContactWitness(async (fixture, clock, _) =>
        {
            var player = fixture.Player;
            var items = Game.ItemsOf(player);
            AllocateWitnessNotable(player, "AfflictionNotableLiquidInspiration");
            var belt = NativeUnique("Umbilicus Immortalis", "CannotBeAffectedByFlasksUnique__1", 1) with
            { Mods = [NativeMod("CannotBeAffectedByFlasksUnique__1", 1), NativeMod("FlasksApplyToMinionsUnique__1", 1)] };
            items.Add(PlayerInventories.Belt, 0, 0, belt);
            fixture.Equip(("Raise Zombie", 18));
            player.Area.Monsters[914] = new("zombie corpse", new(104, 100, 0), 0, 0, 1000)
            {
                ObjectId = 914, Dead = true, Life = 0, Hostile = true, Level = 70,
                Variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie"),
            };
            Game.RecomputePoolsAndSpeed(player);
            uint zombie = await fixture.Summon("RaiseZombie", 914);
            var fighter = player.Summons.Fighters[zombie];
            fighter.Monster.Life = fighter.Monster.MaxLife / 2;
            var hybrid = ItemCatalog.Create(Program.Items.Find("Small Hybrid Flask")!) with { Charges = 40 };
            uint id = items.Add(PlayerInventories.Flask, 2, 0, hybrid).Id;
            uint mana = player.PlayerMana = 10;
            SetWitnessRoll(player);
            await Game.UseFlask(player, 2);
            Assert.True(Assert.Single(items.Items(PlayerInventories.Flask), i => i.Id == id).Item.Charges < hybrid.Charges);
            Assert.Equal(mana, player.PlayerMana);
            Assert.Equal(1, SettleWitnessCharges(player, clock.At));
        });
    }

    static async Task CheckPlacedBrandIgniteScaling(Item axe)
    {
        foreach (bool chilled in new[] { false, true })
        {
            var fresh = await IgniteFromTrap(0, chilled);
            var aged = await IgniteFromTrap(2, chilled);
            double expected = fresh.Dps * aged.Multiplier / fresh.Multiplier;
            Assert.InRange(aged.Dps, expected * .99, expected * 1.01);
        }

        async Task<(double Dps, double Multiplier)> IgniteFromTrap(double age, bool chilled)
        {
            double dps = 0, multiplier = 0;
            await WithContactWitness(async (fixture, clock, target) =>
            {
                var player = fixture.Player;
                Game.ItemsOf(player).Add(PlayerInventories.Offhand1, 0, 0, axe);
                using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
                {
                    if (ReferenceEquals(owner, player)) values[Stat.AlwaysIgnite] = 1;
                    return values;
                });
                ushort brand = Assert.Single(fixture.Equip(("Armageddon Brand", 18), ("Trap Support", 1), ("Invention Support", 1)));
                Game.RecomputePoolsAndSpeed(player);
                if (chilled) Assert.NotNull(target.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill,
                    10, clock.At, Get(), Get()));
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, brand, (100, 100), (104, 100)));
                Assert.True(player.PlayerMana < mana);
                var trap = Assert.Single(player.Placed.All.Values);
                int rate = trap.Own.GetValueOrDefault(Stat.SupportInventionTrapAndMineDamagePlusPctFinalPerSecondPlacedUpTo100);
                Assert.True(rate > 0);
                clock.At = trap.Armed + .01 + age;
                multiplier = 1 + Math.Min(100, rate * (clock.At - trap.LandAt)) / 100d;
                await Game.PlayerMinionsTick(player, clock.At);
                Assert.Empty(player.Placed.All);
                await Game.TickBrands(player, clock.At);
                var active = Assert.Single(Game.ActiveBrandsFor(player));
                Assert.True(active.Attached);
                clock.At = active.NextActivationAt + .01;
                await Game.TickBrands(player, clock.At);
                clock.At += .8;
                await Game.TickBrands(player, clock.At);
                Assert.InRange(target.Life, 1u, target.MaxLife - 1);
                dps = target.Ignites.PerSecond(clock.At);
                Assert.True(dps > 0);
            });
            return (dps, multiplier);
        }
    }

    static uint AddWitnessJewel(Player player, string type, string mod, int value, byte slot)
    {
        var jewel = NativeUnique(type, mod, value);
        player.Character.Passives.Add(Game.PassivesFor(player).JewelNodeForSlot(slot)!.Value);
        player.Character.Passives.Add(31703);
        uint id = Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, slot, 0, jewel).Id;
        if (type == "Megalomaniac")
        {
            var layout = Assert.IsType<ClusterJewelLayout.Layout>(ClusterJewelLayout.For(slot, jewel));
            player.Character.ClusterPassives.Add((ushort)(layout.Base + layout.NotableOrders.Single(row => row.Value == 0).Key));
        }
        return id;
    }

    static SpawnedMonster AddWitness(Player player, uint id, (int X, int Y) at, MonsterRarity rarity = MonsterRarity.Normal)
    {
        var target = new SpawnedMonster("conditional hit witness", new(at.X, at.Y, 0), 0, 0, 100000)
            { ObjectId = id, Hostile = true, NoRewards = true, Rarity = rarity };
        player.Area.Monsters[id] = target;
        player.Area.Grid.Set(new(id), at);
        return target;
    }

    static async Task WithContactWitness(Func<TriggeredSummonTests.Fixture, ContactClock, SpawnedMonster, Task> exercise,
        MonsterRarity rarity = MonsterRarity.Normal)
    {
        var clock = new ContactClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.ProjectileRuntimeState = fixture.Session;
        player.IgniteRandom = new QualifyingRoll();
        player.Walk.Teleport(100, 100, clock.At);
        var items = Game.ItemsOf(player);
        items.Change(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id,
            _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player)) values[Stat.GlobalAlwaysHit] = values[Stat.GlobalCannotCrit] = 1;
            return values;
        });
        var target = AddWitness(player, 900, (104, 100), rarity);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            SetWitnessRoll(player);
            using var rolls = GameEvents.OutgoingDamage.Add("Game", (owner, _, _, packet) =>
            {
                if (ReferenceEquals(owner, player)) SetWitnessRoll(player);
                return packet;
            });
            await exercise(fixture, clock, target);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    static int SettleWitnessCharges(Player player, double at)
    {
        var charges = Game.ChargesOf(player);
        charges.Step(at, Game.CharacterPlayerStats(player).GetValueOrDefault, () => default,
            alive: true, moving: false, inTown: false, new QualifyingRoll());
        return charges.Effective(ChargeType.Power);
    }

    static async Task LandWitness(TriggeredSummonTests.Fixture fixture, ContactClock clock, SpawnedMonster target,
        ushort skill, ushort sequence = 2, Random? random = null)
    {
        var player = fixture.Player;
        Game.RecomputePoolsAndSpeed(player);
        Game.ConfigureLeech(player, Game.CharacterPlayerStats(player).GetValueOrDefault, onLowLife: true);
        player.PlayerLife = player.PlayerMaxLife / 2;
        SetWitnessRoll(player, random);
        uint mana = player.PlayerMana;
        Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (104, 100), target.ObjectId, sequence: sequence));
        Assert.True(player.PlayerMana < mana);
        if (target.Life == target.MaxLife)
        {
            await clock.Wait(600, CancellationToken.None);
            fixture.Session.AdvanceTo(Math.Max(fixture.Session.Runtime.Now, ProjectileRuntime.Time(clock.At)));
        }
        Assert.InRange(target.Life, 1u, target.MaxLife - 1);
    }

    static Player AddAuraWitnessPeer(Player player, double now, PartyFollowerTests.Wire wire)
    {
        var peer = new Player(player.Layout, 2, 1)
        {
            Instance = player.Instance, PlayerLife = 1000, Pools = player.Pools, LeagueModule = player.LeagueModule,
            Character = { Name = "AuraWitness" + Guid.NewGuid().ToString("N"), Experience = player.Character.Experience },
            Connection = { PlayerSent = true, Session = new("aura-witness", wire.Stream, wire.Ciphers) },
        };
        player.Instance.AddPlayer(player);
        player.Instance.AddPlayer(peer);
        peer.Walk.Teleport(103, 100, now);
        peer.Area.Grid.Set(new(peer.Id), (103, 100));
        return peer;
    }

    static async Task StepAuraWitness(Player player, double now)
    {
        await Game.EnemyDebuffsTick(player, now);
        player.Area.Zones.Update(moved: true);
        await Game.EnemyDebuffsTick(player, now);
    }

    sealed class QualifyingRoll(bool pass = true) : Random
    {
        public override int Next(int maxValue) => pass ? 0 : Math.Max(0, maxValue - 1);
        public override int Next(int minValue, int maxValue) => !pass && minValue == 0 && maxValue == 100 ? 99 : minValue;
        public override double NextDouble() => 0;
    }

    sealed class ContactClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    static Func<uint, int> Get(params (uint Name, int Value)[] stats) =>
        name => stats.FirstOrDefault(s => s.Name == name).Value;

    static ItemMod NativeMod(string id, int value) =>
        new(ModSection.Explicit, Program.Items.Mods.ById(id)!.Hash, [value]);

    static Item NativeUnique(string unique, string mod, int value) =>
        Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create(unique, new Random(17))) with
        { Mods = [NativeMod(mod, value)] };

    static uint AllocateWitnessNotable(Player player, string mod) => AddWitnessJewel(player, "Megalomaniac", mod, 1, 20);

    static void SetWitnessRoll(Player player, Random? random = null) =>
        typeof(AreaFrames).GetProperty(nameof(AreaFrames.Rng))!.SetValue(player.Frames, random ?? new QualifyingRoll());

    static async Task UniqueIgniteDurationModifiersStayInTheIgniteMultiplier()
    {
        var previousLogic = Program.GameLogicOrNull;
        try
        {
            Program.SetGameLogic(null!);
            Assert.Equal(4, MonsterIgnites.Settings.From([], "spell", stat =>
                stat is Stat.EmberglowIgniteDurationPlusPctFinal or Stat.UniqueReplicaVolkuursGuidanceIgniteDurationPlusPctFinal
                    ? -50 : 0).Seconds, 6);
        }
        finally { Program.SetGameLogic(previousLogic!); }
        var catalog = new UniqueCatalog(Program.Items);
        var sword = Assert.IsType<Item>(catalog.Create("Skysunder", new Random(17)));
        var gloves = Assert.IsType<Item>(catalog.Create("Replica Volkuur's Guidance", new Random(17)));
        Item Control(Item item, string modName)
        {
            uint hash = Program.Items.Mods.ById(modName)!.Hash;
            Assert.Equal(-50, Assert.Single(Assert.Single(item.Mods, m => m.Hash == hash).Values));
            return item with { Mods = [.. item.Mods.Where(m => m.Hash != hash)] };
        }
        var plainSword = Control(sword, "IgniteDurationEmberglowUnique__1");
        var plainGloves = Control(gloves, "UniqueVolkuursGuidanceIgniteDurationFinal");
        var plain = await Hit(plainSword, plainGloves);
        var emberglow = await Hit(sword, plainGloves);
        var replica = await Hit(plainSword, gloves);
        var combined = await Hit(sword, gloves);
        Console.WriteLine($"Ice Nova ignite/poison seconds: plain {plain}, Skysunder {emberglow}, Replica {replica}, combined {combined}");
        foreach (var (actual, expected) in new[] { (plain, 4d), (emberglow, 2d), (replica, 2d), (combined, 1d) })
        {
            Assert.Equal(expected, actual.Ignite, 6);
            Assert.Equal(2, actual.Poison, 6);
        }

        async Task<(double Ignite, double Poison)> Hit(Item weapon, Item gauntlets)
        {
            var clock = new ContactClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.ProjectileRuntimeState = fixture.Session;
            player.Walk.Teleport(100, 100, clock.Now(player));
            var items = Game.ItemsOf(player);
            uint host = Assert.Single(items.Items(PlayerInventories.Weapon1)).Id;
            items.Change(PlayerInventories.Weapon1, host, _ => weapon);
            items.Add(PlayerInventories.Gloves, 0, 0, gauntlets);
            ushort skill = Assert.Single(fixture.Equip(("Ice Nova", 18), ("Increased Area of Effect Support", 18)));
            using var sources = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, player)) return values;
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.BaseChanceToIgnitePct] = values[Stat.BaseChanceToPoisonOnHitPct] = 100;
                values[Stat.SpellMinimumAddedFireDamage] = values[Stat.SpellMaximumAddedFireDamage] = 100;
                values[Stat.SpellMinimumAddedChaosDamage] = values[Stat.SpellMaximumAddedChaosDamage] = 100;
                return values;
            });
            const uint id = 900;
            var target = new SpawnedMonster("ignite duration target", new(104, 100, 0), 0, 0, 100000) { ObjectId = id };
            player.Area.Monsters[id] = target;
            player.Area.Grid.Set(new(id), (104, 100));
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (104, 100), id));
                Assert.True(player.PlayerMana < mana);
                Assert.InRange(target.Life, 1u, target.MaxLife - 1);
                Assert.Equal(1, target.Poisons.Stacks(clock.Now(player)));
                double applied = target.Ignites.LastApplied, until = target.Ignites.Until;
                Assert.True(target.Ignites.IgnitedAt(until - .001));
                Assert.False(target.Ignites.IgnitedAt(until));
                double one = target.Ignites.PerSecond(applied);
                Assert.True(one > 1);
                Assert.True(target.Ignites.Add(applied, 1, .5));
                Assert.Equal(one, target.Ignites.PerSecond(applied), 6);
                target.Ignites.Stacks = 2;
                Assert.Equal(one + 1, target.Ignites.PerSecond(applied), 6);
                return (until - applied, target.Poisons.Until - target.PlayerHitAt);
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }

    static async Task VolkuursGuidanceShortensPoisonFromAPaidSpell()
    {
        var previousLogic = Program.GameLogicOrNull;
        try
        {
            Program.SetGameLogic(null!);
            Assert.Equal(2, MonsterPoisons.Settings.From([], "spell", stat =>
                stat == Stat.UniqueVolkuursClutchPoisonDurationPlusPctFinal ? -50 : 0).Seconds, 6);
        }
        finally { Program.SetGameLogic(previousLogic!); }
        var gloves = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Volkuur's Guidance", new Random(17)));
        uint mod = Program.Items.Mods.ById("VolkuurLessPoisonDurationUnique__1")!.Hash;
        Assert.Equal(-50, Assert.Single(Assert.Single(gloves.Mods, m => m.Hash == mod).Values));
        var plain = await Cast(gloves with { Mods = [.. gloves.Mods.Where(m => m.Hash != mod)] });
        var shortened = await Cast(gloves);
        Console.WriteLine($"Volkuur's Guidance Ice Nova poison seconds: {plain.Poison} -> {shortened.Poison}");
        Assert.Equal(2, plain.Poison, 6);
        Assert.Equal(1, shortened.Poison, 6);
        Assert.Equal(4, plain.Ignite, 6);
        Assert.Equal(plain.Ignite, shortened.Ignite, 6);

        async Task<(double Poison, double Ignite)> Cast(Item equipped)
        {
            var clock = new ContactClock();
            using var fixture = new TriggeredSummonTests.Fixture(clock);
            var player = fixture.Player;
            player.ProjectileRuntimeState = fixture.Session;
            player.Walk.Teleport(100, 100, clock.Now(player));
            Game.ItemsOf(player).Add(PlayerInventories.Gloves, 0, 0, equipped);
            ushort skill = Assert.Single(fixture.Equip(("Ice Nova", 18)));
            using var sources = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (!ReferenceEquals(owner, player)) return values;
                values[Stat.GlobalCannotCrit] = 1;
                values[Stat.BaseChanceToPoisonOnHitPct] = values[Stat.BaseChanceToIgnitePct] = 100;
                values[Stat.SpellMinimumAddedFireDamage] = values[Stat.SpellMaximumAddedFireDamage] = 100;
                values[Stat.SpellMinimumAddedChaosDamage] = values[Stat.SpellMaximumAddedChaosDamage] = 100;
                return values;
            });
            const uint id = 900;
            var target = new SpawnedMonster("Volkuur's Guidance target", new(104, 100, 0), 0, 0, 100000) { ObjectId = id };
            player.Area.Monsters[id] = target;
            player.Area.Grid.Set(new(id), (104, 100));
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            try
            {
                uint mana = player.PlayerMana;
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (104, 100), id));
                Assert.True(player.PlayerMana < mana);
                Assert.InRange(target.Life, 1u, target.MaxLife - 1);
                Assert.Equal(1, target.Poisons.Stacks(clock.Now(player)));
                double until = target.Poisons.Until;
                Assert.Equal(1, target.Poisons.Stacks(until - .001));
                Assert.Equal(0, target.Poisons.Stacks(until));
                return (until - target.PlayerHitAt, target.Ignites.Until - target.Ignites.LastApplied);
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }
}
