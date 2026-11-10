using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.Machines;
using StardewValley.Inventories;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using System;
using Force.DeepCloner;
using NermNermNerm.Stardew.LocalizeFromSource;
using StardewModdingAPI;
using static NermNermNerm.Stardew.LocalizeFromSource.SdvLocalize;

namespace NermNermNerm.Junimatic
{
    public class ObjectMachine
        : GameMachine
    {
        internal ObjectMachine(StardewValley.Object machine, Point accessPoint)
            : base(machine, accessPoint)
        {
        }

        public StardewValley.Object Machine => (StardewValley.Object)this.GameObject;

        internal static ObjectMachine? TryCreate(StardewValley.Object item, Point accessPoint)
        {
            if (item.QualifiedItemId == "(BC)101" || item.QualifiedItemId == "(BC)254") // incubator or ostrich incubator
            {
                // This is the Incubator.  It exists as its own unbreakable object within the coop.  When an egg "hatches"
                // out of it, the egg exists as the 'heldObject' until a player walks into the coop, when it gets turned into
                // whatever critter was in the egg.  If we didn't have this special case, then the Junimos would grab the
                // egg, throw it into a chest and ultimately mayonnaise-ify it.
                return null;
            }
            else if (item is CrabPot crabPot)
            {
                return new CrabPotMachine(crabPot, accessPoint);
            }
            else if (item is IndoorPot indoorPot)
            {
                return new IndoorPotMachine(indoorPot, accessPoint);
            }
            else if (item.GetMachineData() is not null)
            {
                return new ObjectMachine(item, accessPoint);
            }
            else if (item is Chest miniShippingBin && (miniShippingBin.SpecialChestType == Chest.SpecialChestTypes.MiniShippingBin))
            {
                return new MiniShippingBinMachine(miniShippingBin, accessPoint);
            }
            else
            {
                return null;
            }
        }

        /// <inheritdoc/>
        public override MachineState State
        {
            get
            {
                if (this.Machine.readyForHarvest.Value && this.Machine.heldObject.Value is not null)
                {
                    return MachineState.AwaitingPickup;
                }
                else if (this.Machine.MinutesUntilReady == 0 && this.Machine.heldObject.Value is null)
                {
                    return MachineState.Idle;
                }
                else
                {
                    return MachineState.Working;
                }
            }
        }

        public override bool IsStillPresent =>
            this.Machine.Location.getObjectAtTile((int)this.Machine.TileLocation.X, (int)this.Machine.TileLocation.Y) == this.Machine;

        /// <inheritdoc/>>
        protected override IReadOnlyList<EstimatedProduct> EstimatedProducts => [this.HeldObjectToEstimatedProduct(this.Machine.heldObject.Value)];

        /// <inheritdoc/>>
        public override List<Item>? GetRecipeFromChest(GameStorage storage, Func<Item, bool> isShinyTest)
        {
            var machineData = this.Machine.GetMachineData();
            var inputs = new List<Item>();

            var rawNewContents = storage.SafeInventory
                .Where(i => !isShinyTest(i) && (!ModEntry.Config.DontTakeLastItem || i.Stack > 1))
                .ToArray();

            // Extra Machine Config applies some extra logic to game functions that relies on autoLoadFrom being set to
            // the appropriate inventory.  We'll restore it in the 'finally' block as well.
            var oldAutoLoadFrom = StardewValley.Object.autoLoadFrom;

            try
            {
                // The try/finally block is built for this case -- note that we're actually modifying the real inventory.
                // Making a copy of each item would be the safer play, but it's expensive to run (it roughly doubles
                // the amount of time to do the whole WorkFinder.DoJunimos calculation!).  We could also have copypasta'ed
                // methods from MachineDataUtility and added the "leave one item" logic there, but then we'd be
                // more vulnerable to changes in the game code.
                if (ModEntry.Config.DontTakeLastItem)
                {
                    foreach (var i in rawNewContents)
                    {
                        --i.Stack; // pretend that we have one less of everything
                    }
                }

                var sourceInventory = new Inventory();
                sourceInventory.AddRange(rawNewContents.ToArray());

                // Ensure it has the coal (aka all the 'AdditionalConsumedItems')
                if (machineData.AdditionalConsumedItems is not null)
                {
                    if (!MachineDataUtility.HasAdditionalRequirements(sourceInventory, machineData.AdditionalConsumedItems, out _))
                    {
                        return null;
                    }

                    // i.GetOne() instead of the ItemRegistry.Create to make sure we get all the nuances of item.
                    inputs.AddRange(machineData.AdditionalConsumedItems.Select(i => ItemRegistry.Create(i.ItemId, i.RequiredCount)));
                }

                StardewValley.Object.autoLoadFrom = sourceInventory;
                foreach (var item in sourceInventory)
                {
                    if (MachineDataUtility.TryGetMachineOutputRule(this.Machine, machineData, MachineOutputTrigger.ItemPlacedInMachine, item, Game1.MasterPlayer, this.Machine.Location, out var rule, out var triggerRule, out var ruleIgnoringCount, out var triggerIgnoringCount))
                    {
                        var machineItemOutput = MachineDataUtility.GetOutputData(this.Machine, machineData, rule, item, Game1.MasterPlayer, this.Machine.Location);
                        if (machineItemOutput is not null && MachineDataUtility.GetOutputItem(this.Machine, machineItemOutput, item, Game1.MasterPlayer, true, out int? overrideMinutesUntilReady) is not null)
                        {

                            // Add extra fuels from EMC if applicable, making sure to insert them before the primary input
                            // We don't have to check for validity since the (patched) TryGetMachineOutputRule should already handle that
                            var extraMachineConfigApi = ModEntry.Instance.ExtraMachineConfigApi;
                            if (extraMachineConfigApi is not null)
                            {
                                foreach ((string extraItemId, int extraCount) in extraMachineConfigApi.GetExtraRequirements(machineItemOutput))
                                {
                                    var matchingItem = sourceInventory.FirstOrDefault(i => CraftingRecipe.ItemMatchesForCrafting(i, extraItemId), null);
                                    if (matchingItem != null)
                                    {
                                        var itemToAdd = matchingItem.getOne();
                                        itemToAdd.Stack = extraCount;
                                        inputs.Add(itemToAdd);
                                    }
                                }

                                foreach ((string extraContextTags, int extraCount) in extraMachineConfigApi.GetExtraTagsRequirements(machineItemOutput)) {
                                    var matchingItem = sourceInventory.FirstOrDefault(i => ItemContextTagManager.DoesTagQueryMatch(extraContextTags, i?.GetContextTags() ?? new HashSet<string>()), null);
                                    if (matchingItem != null)
                                    {
                                        var itemToAdd = matchingItem.getOne();
                                        itemToAdd.Stack = extraCount;
                                        inputs.Add(itemToAdd);
                                    }
                                }
                            }

                            inputs.Add(ItemRegistry.Create(item.ItemId, amount: triggerRule.RequiredCount, quality: item.Quality));
                            return inputs;
                        }
                    }
                }
            }
            finally
            {
                StardewValley.Object.autoLoadFrom = oldAutoLoadFrom;

                if (ModEntry.Config.DontTakeLastItem)
                {
                    foreach (var i in rawNewContents)
                    {
                        ++i.Stack;
                    }
                }
            }

            return null;
        }

        /// <inheritdoc/>>
        public override bool FillMachineFromChest(GameStorage storage, Func<Item, bool> isShinyTest)
        {
            // Don't attempt to load items into machines that don't accept any input
            // Check for is_machine to avoid breaking crab pots
            if (this.Machine.HasContextTag("is_machine") && !this.Machine.HasContextTag("machine_input"))
            {
                ModEntry.Instance.LogTraceOnce($"Machine {this.Machine.ItemId}:{this.Machine.DisplayName} does not have any inputs.");
                return false;
            }

            // Copied from StardewValley.Object.AttemptAutoLoad, with filtering for items
            if (this.Machine.heldObject.Value != null)
            {
                return false;
            }

            var rawInventory = storage.UnsafeInventory;
            StardewValley.Object.autoLoadFrom = rawInventory;
            bool filledIt = rawInventory.Any(item => item is not null && !isShinyTest(item) && this.Machine.performObjectDropInAction(item, probe: false, Game1.MasterPlayer));
            StardewValley.Object.autoLoadFrom = null;
            return filledIt;
        }

        /// <inheritdoc/>>
        public override void FillMachineFromInventory(Inventory inventory)
        {
            // This "attempt" should be a sure thing, as the recipe was concocted by the machine (so it's valid) and
            //  callers should be ensuring that the machine is in the idle state before calling.
            if (!this.Machine.AttemptAutoLoad(inventory, Game1.MasterPlayer))
            {
                ModEntry.Instance.LogErrorOnce($"Machine fill was attempted on a machine that was busy - {this.Machine.ItemId}:{this.Machine.DisplayName}");
            }
        }

        private static readonly Dictionary<string,HashSet<JunimoType>> cachedCompatList = new();

        private static readonly string[][] tags = [
            [I("category_minerals"), I("category_gem"), I("bone_item"), I("ore_item")],
            [I("egg_item"), I("large_egg_item"), I("slime_egg_item"), I("milk_item")],
            [I("category_vegetable"), I("category_fruit"), I("keg_wine"), I("preserves_pickle"), I("preserves_jelly"), I("coffee_item"),
                I("fruit_item"), I("fruit_tree_item"), I("honey_item"), I("juice_item"), I("mayo_item"), I("pickle_item")],
            [I("category_fish")],
            [I("forage_item"), I("syrup_item"), I("wood_item")],
            [I("fruit_item"), I("fruit_tree_item")]
        ];

        private static int[][] categories = [
            [StardewValley.Object.GemCategory, StardewValley.Object.mineralsCategory, StardewValley.Object.metalResources, StardewValley.Object.monsterLootCategory],
            [StardewValley.Object.EggCategory, StardewValley.Object.MilkCategory, StardewValley.Object.meatCategory, StardewValley.Object.sellAtPierresAndMarnies /* wool, duck feather, etc. */, StardewValley.Object.CookingCategory],
            [StardewValley.Object.VegetableCategory, StardewValley.Object.FruitsCategory, StardewValley.Object.SeedsCategory, StardewValley.Object.flowersCategory, StardewValley.Object.fertilizerCategory],
            [StardewValley.Object.junkCategory, StardewValley.Object.baitCategory, StardewValley.Object.sellAtFishShopCategory],
            [StardewValley.Object.buildingResources],
            []
        ];

        public static bool HasMatchingTag(JunimoType projectType, IEnumerable<string> itemTags)
            => tags[(int)projectType].Intersect(itemTags).Any();

        public static bool IsCategoryCompatibleWithProject(JunimoType projectType, int category)
            => categories[(int)projectType].Contains(category);

        /// <summary>
        ///   Returns true if the machine has a recipe that the Junimo can do.
        /// </summary>
        public override bool IsCompatibleWithJunimo(JunimoType projectType)
        {
            string cacheKey = this.Machine.ItemId;
            if (!cachedCompatList.TryGetValue(cacheKey, out var compatibleJunimoList))
            {
                compatibleJunimoList = this.GetCompatibleJunimosByOutputRules();
                ObjectMachine.cachedCompatList.Add(cacheKey, compatibleJunimoList);
            }
            if (compatibleJunimoList.Count > 0)
            {
                return compatibleJunimoList.Contains(projectType);
            }

            // A common cause of the rule-based system to not work is machines that
            // don't have a direct relationship between an input item and an output.
            // Many of these are spew-an-item-a-day machines.  This system ensures
            // that we can at least empty these machines if not fill them.
            if (this.Machine.heldObject.Value is not null)
            {
                var heldObject = this.Machine.heldObject.Value;
                if (IsCategoryCompatibleWithProject(projectType, heldObject.Category))
                {
                    return true;
                }

                if (HasMatchingTag(projectType, heldObject.GetContextTags()))
                {
                    return true;
                }

                // If the object is just completely unrecognizable, let anything grab it...
                if (!categories.SelectMany(c => c).Contains(heldObject.Category) // If heldObject.Category doesn't match any of our categories
                    && !tags.SelectMany(tagSet => tagSet).Intersect(heldObject.GetContextTags()).Any())
                {
                    return true;
                }
            }

            return false;
        }

        public override string MachineTypeId => this.Machine.QualifiedItemId;

        /// <summary>
        ///   Tries to determine if a machine is compatible with a particular type of junimo by looking at the
        ///   machine's rule data.  Often, the rule data is not conclusive - if that's the case, it returns null.
        /// </summary>
        [NoStrict]
        private HashSet<JunimoType> GetCompatibleJunimosByOutputRules()
        {
            // The MachineData contains clues as to what the assignments should be, but it's definitely fuzzy.
            // The game's notion of "category" is largely a matter of taste and isn't really consistent, and
            // this mod's notion of categorization is also just a matter of taste.  By using categories, we're
            // just trying to make it so there's a fighting chance for getting it right for mods without having
            // to do special configuration.

            var machineData = this.Machine.GetMachineData();

            // Mods can specify exactly which Junimo should service it.  That's the top priority.
            if (machineData?.CustomFields?.TryGetValue("Junimatic.JunimoType", out string? modAssignment) == true)
            {
                if (Enum.TryParse(modAssignment, true, out JunimoType junimoType))
                {
                    ModEntry.Instance.LogInfo($"JobAssignment: {this.Machine.DisplayName} can be handled by {junimoType} because of CustomData");
                    return [junimoType];
                }
                else
                {
                    ModEntry.Instance.LogErrorOnce($"'{this.Machine.Name}' is set up incorrectly - invalid value for Junmatic.JunimoType: '{modAssignment}'.  Allowed values are {string.Join(", ", Enum.GetNames<JunimoType>())}");
                }
            }


            // Special cases.
            switch (this.Machine.ItemId)
            {
                case "12": // keg
                    return [JunimoType.Crops]; // Otherwise it'll return true for Animals, because there's a recipe (forget which) that involves animal stuff.
                case "25": // seed maker
                    return [JunimoType.Crops]; // Otherwise it'll return true for Animals, because there's a recipe (forget which) that involves animal stuff.
                case "182": // Geode Crusher
                    return [JunimoType.Mining];
                case "211": // wood chipper
                    return [JunimoType.Forestry]; // Else it gets to thinking that fishing would work.
                case "10": // bee house
                    return [JunimoType.Animals]; // no good data
                case "20": // recycling machine
                    return [JunimoType.Fishing]; // It has a zillion outputs, so the data isn't a good guide.
                case "154": // worm bin
                case "DeluxeWormBin":
                    return [JunimoType.Fishing]; // The output item makes it perfectly clear, but it's the only thing where OutputItem would add value.
                case "BaitMaker": // This seems like a dangerous machine since the use-case is so specialized that if it gets in the network, it's likely by accident.
                    return [];
                case "21": // Crystalarium
                    return [];
                case "710": // crab pot
                    return [JunimoType.Fishing]; // There's no MachineData
                case "231": // solar panel
                case "9": // lightning rod
                    return [JunimoType.Mining]; // no good data
                case "105": // tapper
                case "264": // heavy tapper
                case "MushroomLog":
                    return [JunimoType.Forestry]; // no good data
                case "Dehydrator":
                    // The Data for this machine will return true for farming Junimos as well, but there's no
                    // category for woodsy stuff and we're kinda blazing our own path with the idea that forestry
                    // Junimos work mushrooms.
                    return [JunimoType.Forestry];
            }

            if (machineData?.OutputRules is null)
            {
                ModEntry.Instance.LogInfoOnce($"JobAssignment: {this.Machine.DisplayName} - {this.Machine.ItemId} - cannot figure out its project type from the rules data");
                return [];
            }

            var compatibleTypes = new HashSet<JunimoType>();
            bool hasArtisanCategory = false;
            foreach (var projectType in Enum.GetValues<JunimoType>())
            {
                bool isCompatible = false;
                foreach (var rule in machineData.OutputRules)
                {
                    foreach (var trigger in rule.Triggers ?? [])
                    {
                        if (trigger.RequiredTags is not null && HasMatchingTag(projectType, trigger.RequiredTags))
                        {
                            ModEntry.Instance.LogInfo($"JobAssignment: {this.Machine.DisplayName} can be handled by {projectType} because of tags: {string.Join(',', trigger.RequiredTags)}");
                            isCompatible = true;
                            break;
                        }

                        if (trigger.RequiredItemId is not null)
                        {
                            var itemData = ItemRegistry.GetData(trigger.RequiredItemId);
                            if (itemData is null)
                            {
                                ModEntry.Instance.LogWarningOnce($"ItemRegistry.GetData failed for {trigger.RequiredItemId} - this could render {this.Machine.Name} unusable to Junimos");
                            }
                            else if (IsCategoryCompatibleWithProject(projectType, itemData.Category))
                            {
                                ModEntry.Instance.LogInfo($"JobAssignment: {this.Machine.DisplayName} can be handled by {projectType} because of trigger category: {itemData.Category}");
                                isCompatible = true;
                                break;
                            }
                            else if (itemData.Category == StardewValley.Object.artisanGoodsCategory)
                            {
                                hasArtisanCategory = true;
                            }
                        }
                    }

                    foreach (var outputItem in rule.OutputItem ?? [])
                    {
                        foreach (string? itemId in (outputItem.RandomItemId ?? []).Append(outputItem.ItemId))
                        {
                            // TODO: Figure out if there's a better thing to do with FLAVORED_ITEM and DROP_IN.  (For Flavored, maybe, drop-in, definitely not)
                            if (!string.IsNullOrEmpty(itemId) && !itemId.StartsWith("FLAVORED") && !itemId.StartsWith("DROP_IN")) // DROP_IN and DROP_IN_PRESERVE are the only ones I kow.
                            {
                                var itemData = ItemRegistry.GetData(itemId);
                                if (itemData is null)
                                {
                                    ModEntry.Instance.LogWarningOnce($"ItemRegistry.GetData failed for {itemId} - this could render {this.Machine.Name} unusable to Junimos");
                                }
                                else if (IsCategoryCompatibleWithProject(projectType, itemData.Category))
                                {
                                    ModEntry.Instance.LogInfo($"JobAssignment: {this.Machine.DisplayName} can be handled by {projectType} because of OutputItem category: {itemData.Category}");
                                    isCompatible = true;
                                    break;
                                }
                                else if (itemData.Category == StardewValley.Object.artisanGoodsCategory)
                                {
                                    hasArtisanCategory = true;
                                }
                            }
                        }

                        if (isCompatible) break;
                    }

                    if (isCompatible) break;
                }

                if (isCompatible)
                {
                    compatibleTypes.Add(projectType);
                }
            }

            if (compatibleTypes.Any())
            {
                ModEntry.Instance.LogInfo($"JobAssignment: {this.Machine.DisplayName} - {this.Machine.ItemId} projects: {string.Join(",", compatibleTypes)}");
                return compatibleTypes;
            }
            else if (hasArtisanCategory)
            {
                ModEntry.Instance.LogInfo($"JobAssignment: {this.Machine.DisplayName} - {this.Machine.ItemId} doesn't have a clear distinguisher, but items are artisan, so going with Animals,Crops");
                return [JunimoType.Animals, JunimoType.Crops];
            }
            else
            {
                ModEntry.Instance.LogWarning($"JobAssignment: {this.Machine.DisplayName} - {this.Machine.ItemId} doesn't have rules that give a clue as to which Junimo should work with it.  Junimos will collect items from it but will not add them.");
                return [];
            }
        }

        public override List<StardewValley.Item> GetProducts()
        {
            // Adapted from Pathoschild.Stardew.Automate.Framework.Machines.GetOutput
            StardewValley.Object machine = this.Machine;
            var result = machine.heldObject.Value;
            MachineData? machineData = machine.GetMachineData();

            // recalculate output if needed (e.g. bee house honey)
            if (machine.lastOutputRuleId.Value != null && machineData != null)
            {
                MachineOutputRule? outputRule = machineData.OutputRules?.FirstOrDefault(p => p.Id == machine.lastOutputRuleId.Value);
                if (outputRule?.RecalculateOnCollect == true)
                {
                    var prevOutput = machine.heldObject.Value;
                    machine.heldObject.Value = null;

                    machine.OutputMachine(machineData, outputRule, machine.lastInputItem.Value, null, machine.Location, false);

                    if (machine.heldObject.Value == null)
                        machine.heldObject.Value = prevOutput;
                }
            }

            // get output
            this.OnOutputCollected(result);
            return [result];
        }

        private void OnOutputCollected(Item item)
        {
            // Adapted from Pathoschild.Stardew.Automate.Framework.Machines.OnOutputCollected
            StardewValley.Object machine = this.Machine;
            MachineData? machineData = machine.GetMachineData();

            // update stats
            MachineDataUtility.UpdateStats(machineData?.StatsToIncrementWhenHarvested, item, item.Stack);

            // reset machine data
            // This needs to happen before the OutputCollected check, which may start producing a new output.
            machine.heldObject.Value = null;
            machine.readyForHarvest.Value = false;
            machine.showNextIndex.Value = false;
            machine.ResetParentSheetIndex();

            // apply OutputCollected rule
            if (MachineDataUtility.TryGetMachineOutputRule(machine, machineData, MachineOutputTrigger.OutputCollected, item.getOne(), null, machine.Location, out MachineOutputRule outputCollectedRule, out _, out _, out _))
                machine.OutputMachine(machineData, outputCollectedRule, machine.lastInputItem.Value, null, machine.Location, false);

            // update tapper
            if (machine.IsTapper())
            {
                if (machine.Location.terrainFeatures.TryGetValue(machine.TileLocation, out TerrainFeature terrainFeature) && terrainFeature is Tree tree)
                    tree.UpdateTapperProduct(machine, item as StardewValley.Object);
            }

        }

        public override string ToString()
        {
            return IF($"{this.Machine.Name} at {this.Machine.TileLocation}");
        }
    }
}
