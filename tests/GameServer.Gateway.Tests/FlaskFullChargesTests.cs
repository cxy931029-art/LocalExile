using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class FlaskFullChargesTests
{
    [Fact]
    public async Task Standing_full_flask_uses_once_and_rechecks_charges_after_waiting()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        var catalog = Program.Items;
        var type = catalog.All.First(t => t.Class == "UtilityFlask" && t.Name == "Granite Flask");
        var utility = catalog.UtilityFlaskOf(type)!;
        var plain = ItemCatalog.Create(type) with { Charges = utility.MaxCharges };
        var trigger = catalog.Mods.ById("FlaskEnchantmentInjectorOnFullCharges__")!.Roll(ModSection.Enchant, new Random(2205));
        var inventory = Game.ItemsOf(player);
        var flask = inventory.Put(PlayerInventories.Flask, new(301, 0, 0, plain with { Mods = [trigger] }));
        inventory.Put(PlayerInventories.Flask, new(302, 1, 0, plain));
        int Held() => inventory.Items(PlayerInventories.Flask).Single(i => i.Id == flask.Id).Item.Charges;

        await player.FlaskAutomation.UseGate.WaitAsync();
        Task drain;
        try
        {
            Game.QueueFullFlasks(player);
            Game.QueueFullFlasks(player);
            Assert.Equal(flask.Id, Assert.Single(player.FlaskAutomation.Events).Item);
            drain = Game.DrainFlaskTriggers(player);
            Assert.False(drain.IsCompleted);
            // A manual use already held the gate when the automatic use saw a full bar.
            inventory.Change(PlayerInventories.Flask, flask.Id, i => i with { Charges = utility.MaxCharges - utility.ChargesPerUse });
        }
        finally { player.FlaskAutomation.UseGate.Release(); }
        await drain;
        Assert.Equal(utility.MaxCharges - utility.ChargesPerUse, Held());
        Assert.Null(player.UtilityFlaskEffects.Of(flask.Id));

        inventory.Change(PlayerInventories.Flask, flask.Id, i => i with { Charges = utility.MaxCharges });
        await Game.DrainFlaskTriggers(player);
        await Game.DrainFlaskTriggers(player);
        Assert.Equal(utility.MaxCharges - utility.ChargesPerUse, Held());
        Assert.NotNull(player.UtilityFlaskEffects.Of(flask.Id));
        inventory.Change(PlayerInventories.Flask, flask.Id, i => i with { Charges = utility.MaxCharges });
        using (GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (owner == player) stats[Stat.FlaskChargesUsedPlusPct] = -100;
            return stats;
        }))
        {
            Game.QueueFullFlasks(player);
            Assert.Empty(player.FlaskAutomation.Events);
        }
        Assert.Equal(utility.MaxCharges, inventory.Items(PlayerInventories.Flask).Single(i => i.Id == 302).Item.Charges);

        // Hideouts have IsTown = 0, IsHideout = 1: standing full-charge auto-use must not fire in Hideouts.
        var hideout = Assert.IsType<HideoutCatalog.Hideout>(HideoutCatalog.Default);
        player.Use(new AreaLayout(hideout.AreaHash16, hideout.AreaId, 100, 100, 50, 50, (_, _) => true, []));
        inventory.Change(PlayerInventories.Flask, flask.Id, i => i with { Charges = utility.MaxCharges });
        await Game.DrainFlaskTriggers(player);
        Assert.Empty(player.FlaskAutomation.Events);
        Assert.Equal(utility.MaxCharges, Held());

        // Charge gains queue a separate full-charge event, even in a hideout.
        inventory.Change(PlayerInventories.Flask, flask.Id, i => i with { Charges = utility.MaxCharges - 1 });
        await Game.GainFlaskCharges(player, (_, _) => 1, "hideout refill");
        await Game.DrainFlaskTriggers(player);
        Assert.Equal(utility.MaxCharges, Held());
        await DoedresElixirAppliesItsPaidUseEffects();
    }

    static async Task DoedresElixirAppliesItsPaidUseEffects()
    {
        var clock = new ElixirClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Character.Passives.Add(31703); // Pain Attunement
        fixture.Equip(("Ice Nova", 18), ("Double Strike", 18), ("Vaal Fireball", 18));
        var items = Game.ItemsOf(player);
        foreach (var initial in items.Items(PlayerInventories.Flask)) items.Remove(PlayerInventories.Flask, initial.Id);
        var elixir = Assert.IsType<Item>(new UniqueCatalog(Program.Items).Create("Doedre's Elixir", new Random(17)));
        ItemMod Roll(string name, int value) => Assert.Single(elixir.Mods,
            mod => mod.Hash == Program.Items.Mods.ById(name)!.Hash) with { Values = [value] };
        var shieldMod = Roll("FlaskRemovePercentageOfEnergyShieldUniqueFlask2", 80);
        var chaosMod = Roll("FlaskTakeChaosDamagePercentageOfLifeUniqueFlask2", 50);
        ItemMod[] chargeMods =
        [
            Roll("FlaskGainFrenzyChargeUniqueFlask2", 1),
            Roll("FlaskGainPowerChargeUniqueFlask2", 2),
            Roll("FlaskGainEnduranceChargeUniqueFlask2", 3),
        ];
        var cost = Roll("LocalFlaskChargesUsedUniqueFlask2", 290);
        uint slot = items.Add(PlayerInventories.Flask, 0, 0, elixir)!.Id;
        uint adjacent = items.Add(PlayerInventories.Flask, 1, 0,
            ItemCatalog.Create(Program.Items.Find("Small Life Flask")!) with { Charges = 21 })!.Id;
        Assert.Equal(slot, Assert.Single(items.Items(PlayerInventories.Flask), item => item.X == 0).Id);
        using var statSource = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player))
            {
                values[Stat.BaseMaximumEnergyShield] = 500;
                values[Stat.BaseChaosDamageResistancePct] = 50;
            }
            return values;
        });
        Game.RecomputePoolsAndSpeed(player);
        Assert.Equal(50, Game.Resistance(player, DamageType.Chaos));
        uint maximumShield = player.EnergyShield.Maximum;
        uint shieldLoss = maximumShield * 80 / 100;
        uint chaosLoss = player.PlayerMaxLife / 2 * 50 / 100;
        Assert.True(shieldLoss > 0 && chaosLoss > 0);

        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            var plain = await Use(36);
            var shield = await Use(36, shieldMod);
            var chaos = await Use(36, chaosMod);
            var charges = await Use(36, chargeMods);
            var combined = await Use(36, [shieldMod, chaosMod, .. chargeMods]);
            var refused = await Use(1, [shieldMod, chaosMod, .. chargeMods]);
            Console.WriteLine($"Doedre's Elixir (held, ES lost, Life lost, Frenzy, Power, Endurance): plain {plain}; shield {shield}; chaos {chaos}; charges {charges}; combined {combined}; refused {refused}");
            Assert.Equal((1, 0u, 0u, 0, 0, 0), plain);
            Assert.Equal((1, shieldLoss, 0u, 0, 0, 0), shield);
            Assert.Equal((1, 0u, chaosLoss, 0, 0, 0), chaos);
            Assert.Equal((1, 0u, 0u, 1, 2, 3), charges);
            Assert.Equal((1, shieldLoss, chaosLoss, 1, 2, 3), combined);
            Assert.Equal((1, 0u, 0u, 0, 0, 0), refused);
            Assert.Equal(0, player.PlayerDeaths);
            var lethal = await UseAtLife(36, 1, [shieldMod, chaosMod, .. chargeMods]);
            Assert.Equal((1, shieldLoss, 1u, 0, 0, 0), lethal);
            Assert.Equal(0u, player.PlayerLife);
            Assert.Equal(1, player.PlayerDeaths);
            Assert.Equal(player.IncomingHitEpoch, player.DeathNotificationEpoch);
            await Game.UseFlask(player, 0);
            Assert.Equal(1, player.PlayerDeaths);
            Assert.Equal(1, items.Items(PlayerInventories.Flask).Single(i => i.Id == slot).Item.Charges);
        }
        finally { Game.AreaUpdateContext.Value = previous; }

        Task<(int Held, uint ShieldLoss, uint LifeLoss, int Frenzy, int Power, int Endurance)> Use(
            int initialCharges, params ItemMod[] mods) => UseAtLife(initialCharges, player.PlayerMaxLife / 3, mods);

        async Task<(int Held, uint ShieldLoss, uint LifeLoss, int Frenzy, int Power, int Endurance)> UseAtLife(
            int initialCharges, uint lifeBefore, ItemMod[] mods)
        {
            var chargeSession = Game.ChargesOf(player);
            chargeSession.Die();
            items.Change(PlayerInventories.Flask, slot, _ => elixir with { Charges = initialCharges, Mods = [cost, .. mods] });
            player.PlayerLife = lifeBefore;
            player.PlayerMana = 1;
            player.EnergyShield.Recover(maximumShield);
            await Game.UseFlask(player, 0);
            var stats = Game.CharacterPlayerStats(player);
            chargeSession.Step(clock.At, stats.GetValueOrDefault, () => Game.WeaponProfileFor(player, activeSet: true),
                alive: player.PlayerLife > 0, moving: false, inTown: false, new Random(17));
            int held = items.Items(PlayerInventories.Flask).Single(i => i.Id == slot).Item.Charges;
            Assert.Equal(21, items.Items(PlayerInventories.Flask).Single(i => i.Id == adjacent).Item.Charges);
            clock.At += 10;
            return (held, maximumShield - player.EnergyShield.Current, lifeBefore - player.PlayerLife,
                chargeSession.Effective(ChargeType.Frenzy), chargeSession.Effective(ChargeType.Power),
                chargeSession.Effective(ChargeType.Endurance));
        }
    }

    sealed class ElixirClock : IPlayerUpdateSchedule
    {
        public double At = 10;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
