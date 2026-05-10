using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using robotManager.Helpful;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Database.Models;
using WholesomeToolbox;
using wManager;
using wManager.Wow.Bot.Tasks;
using wManager.Wow.Enums;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;
using static wManager.Wow.Helpers.PathFinder;

namespace Wholesome_Auto_Quester.Helpers
{
    public static class ToolBox
    {
        private static Dictionary<int, bool[]> _objectiveCompletionDict = new Dictionary<int, bool[]>();

        // Anti-spam cache: GUID of object we already cast a quest item on, with timestamp.
        private static readonly Dictionary<ulong, DateTime> _itemUsedOnGuids = new Dictionary<ulong, DateTime>();
        private static readonly TimeSpan _itemUsedTtl = TimeSpan.FromSeconds(60);

        // Anti-spam cache: QuestId for which we already used a no-target quest item, with timestamp.
        private static readonly Dictionary<int, DateTime> _itemUsedFromBagForQuests = new Dictionary<int, DateTime>();
        private static readonly TimeSpan _itemUsedFromBagTtl = TimeSpan.FromSeconds(120);

        public static List<Vector3> GetPointsAlongPath(
            List<Vector3> path,
            float distanceBetweenPoints,
            float maxDistance)
        {
            List<Vector3> result = new List<Vector3>();
            float remainder = 0f;
            float totalDistance = 0f;

            if (path.Count <= 0)
            {
                return result;
            }

            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector3 segmentStart = path[i];
                Vector3 segmentEnd = path[i + 1];
                float segmentLength = segmentStart.DistanceTo(segmentEnd);

                if (totalDistance > maxDistance) break;

                for (float offsetIndex = distanceBetweenPoints; offsetIndex < segmentLength; offsetIndex += distanceBetweenPoints)
                {
                    if (remainder > 0)
                    {
                        offsetIndex -= remainder;
                        remainder = 0;
                    }

                    if (offsetIndex + distanceBetweenPoints > segmentLength)
                    {
                        remainder = segmentLength - offsetIndex;
                    }

                    Vector3 vector = new Vector3(segmentEnd.X - segmentStart.X, segmentEnd.Y - segmentStart.Y, segmentEnd.Z - segmentStart.Z);
                    double c = System.Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
                    double a = offsetIndex / c;
                    Vector3 offset = new Vector3(segmentStart.X + vector.X * a, segmentStart.Y + vector.Y * a, segmentStart.Z + vector.Z * a);

                    totalDistance += distanceBetweenPoints;
                    if (totalDistance > maxDistance) break;
                    result.Add(offset);
                }
            }

            return result;
        }

        public static void CheckIfZReachable(Vector3 checkPosition)
        {
            if (checkPosition.DistanceTo(new Vector3(-815.609, 2614.2, 124.3904, "None")) < 3) return; // Honor Hold exception
            if (checkPosition.DistanceTo(new Vector3(-252.359, 5499.21, 66.60029, "None")) < 3) return; // Cenarion refuge exception
            if (checkPosition.DistanceTo2D(ObjectManager.Me.Position) <= 3 && WTLocation.GetZDifferential(checkPosition) > 3)
            {
                BlacklistHelper.AddZone(checkPosition, 2, $"Unreachable Z");
            }
        }

        public static bool IHaveLineOfSightOn(WoWObject wowObject)
        {
            Vector3 myPos = ObjectManager.Me.Position;
            Vector3 objectPos = (wowObject is WoWUnit) ? new Vector3(wowObject.Position.X, wowObject.Position.Y, wowObject.Position.Z + 2) : wowObject.Position;
            return !TraceLine.TraceLineGo(new Vector3(myPos.X, myPos.Y, myPos.Z + 2),
                objectPos,
                CGWorldFrameHitFlags.HitTestSpellLoS | CGWorldFrameHitFlags.HitTestLOS);
        }

        public static bool HostilesAreAround(WoWObject POI, IWAQTask task)
        {
            if (POI.Entry == 1776 // swamp of sorrows Magtoor
                || POI.Entry == 19256 // Sergeant SHatterskull
                || POI.Entry == 27266 // Sergeant Thurkin
                || POI.Entry == 191519 // Sparksocket's Tools
                || POI.Entry == 9536 // Maxwort Uberglint
                || POI.Entry == 10267 // Tinkee Steamboil
                || POI.Entry == 9563 // Ragged John
                || POI.Entry == 9836 // Mathredis Firestar
                || POI.Entry == 19442) // Kruush
            {
                return false;
            }

            WoWUnit poiUnit = POI is WoWUnit ? (WoWUnit)POI : null;
            WoWUnit me = ObjectManager.Me;
            Vector3 myPosition = me.Position;
            Vector3 poiPosition = POI.Position;

            if (me.IsMounted && (me.InCombatFlagOnly || POI.Position.DistanceTo(myPosition) < 60 && poiUnit?.Reaction == Reaction.Hostile))
            {
                MountTask.DismountMount(false, false);
            }

            if (ObjectManager.GetNumberAttackPlayer() > 0)
            {
                return true;
            }

            List<WoWUnit> hostiles = GetListObjManagerHostiles();
            Dictionary<WoWUnit, float> hostileUnits = new Dictionary<WoWUnit, float>();
            float myDistanceToPOI = me.Position.DistanceTo(poiPosition);
            foreach (WoWUnit unit in hostiles)
            {
                if (unit.Guid != POI.Guid && unit.Position.DistanceTo(poiPosition) < myDistanceToPOI)
                {
                    WAQPath pathFromPoi = GetWAQPath(unit.Position, poiPosition);
                    if (pathFromPoi.Distance < myDistanceToPOI)
                    {
                        hostileUnits.Add(unit, pathFromPoi.Distance);
                    }
                }
            }

            bool poiIsUnit = poiUnit != null;
            int maxCount = poiIsUnit ? 2 : 3;

            if (WholesomeAQSettings.CurrentSetting.BlacklistDangerousZones)
            {
                if (hostileUnits.Where(u => u.Key.Level >= me.Level && poiPosition.DistanceTo(u.Key.Position) < 18).Count() >= maxCount
                    || hostileUnits.Where(u => u.Key.Level >= me.Level - 2 && poiPosition.DistanceTo(u.Key.Position) < 18).Count() >= maxCount + 1)
                {
                    if (Fight.InFight) Fight.StopFight();
                    MovementManager.StopMove();
                    BlacklistHelper.AddNPC(POI.Guid, "Surrounded by hostiles");
                    BlacklistHelper.AddZone(poiPosition, 20, "Surrounded by hostiles");
                    task.PutTaskOnTimeout($"{POI.Name} is surrounded by hostiles", 60 * 30);
                    return true;
                }
            }
            return false;
        }

        public static T TakeHighest<T>(this IEnumerable<T> list, Func<T, int> takeValue, out int amount)
        {
            var highest = int.MinValue;
            T curHighestElement = default;

            foreach (T element in list)
            {
                int curValue = takeValue(element);
                if (curValue > highest)
                {
                    highest = curValue;
                    curHighestElement = element;
                }
            }

            amount = highest;
            return curHighestElement;
        }

        public static T TakeHighest<T>(this IEnumerable<T> list, Func<T, int> takeValue) =>
            list.TakeHighest(takeValue, out _);

        public static float PathLength(List<Vector3> path)
        {
            var length = 0f;
            for (var i = 0; i < path.Count - 1; i++) length += path[i].DistanceTo(path[i + 1]);

            return length;
        }

        public static WoWUnit FindClosestUnitByEntry(int entry)
        {
            Vector3 myPos = ObjectManager.Me.PositionWithoutType;
            return ObjectManager.GetWoWUnitByEntry(entry)
                .TakeHighest(unit => (int)-unit.PositionWithoutType.DistanceTo(myPos));
        }

        public static WoWGameObject FindClosestGameObjectByEntry(int entry)
        {
            Vector3 myPos = ObjectManager.Me.PositionWithoutType;
            return ObjectManager.GetWoWGameObjectByEntry(entry)
                .TakeHighest(gameObject => (int)-gameObject.Position.DistanceTo(myPos));
        }

        public static bool SaveQuestAsCompleted(int questId)
        {
            if (!WholesomeAQSettings.CurrentSetting.ListCompletedQuests.Contains(questId) && !Quest.HasQuest(questId))
            {
                Logger.Log($"Saved quest {questId} as completed");
                WholesomeAQSettings.CurrentSetting.ListCompletedQuests.Add(questId);
                return true;
            }
            return false;
        }

        public static bool IsQuestCompleted(int questId) => WholesomeAQSettings.CurrentSetting.ListCompletedQuests.Contains(questId);

        public static bool JSONFileIsPresent() => File.Exists(Others.GetCurrentDirectory + @"\Data\WAQquests.json");

        public static bool ZippedJSONIsPresent() => File.Exists(Others.GetCurrentDirectory + @"\Data\WAQquests.zip");

        public static void ZipJSONFile()
        {
            try
            {
                if (!JSONFileIsPresent())
                {
                    Logger.LogError("The JSON file is not present in Data");
                    return;
                }

                if (ZippedJSONIsPresent())
                    File.Delete(Others.GetCurrentDirectory + @"\Data\WAQquests.zip");

                using (ZipArchive zip = ZipFile.Open(Others.GetCurrentDirectory + @"\Data\WAQquests.zip",
                    ZipArchiveMode.Create))
                {
                    ZipArchiveEntry entry = zip.CreateEntry("WAQquests.json");
                    entry.LastWriteTime = DateTimeOffset.Now;

                    using (FileStream stream = File.OpenRead(Others.GetCurrentDirectory + @"\Data\WAQquests.json"))
                    using (Stream entryStream = entry.Open())
                    {
                        stream.CopyTo(entryStream);
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError("ZipJSONFile > " + e.Message);
            }
        }

        public static bool ShouldStateBeInterrupted(IWAQTask task, WoWObject gameObject)
        {
            if (gameObject == null)
            {
                return true;
            }

            if (wManagerSetting.IsBlackListedZone(gameObject.Position)
                || wManagerSetting.IsBlackListed(gameObject.Guid))
            {
                MovementManager.StopMove();
                return true;
            }

            if (wManagerSetting.IsBlackListedZone(task.Location))
            {
                MovementManager.StopMove();
                return true;
            }

            return false;
        }

        public static void UpdateObjectiveCompletionDict(int[] questIds)
        {
            if (questIds.Length <= 0)
                return;
            _objectiveCompletionDict = GetObjectiveCompletionDict(questIds);
        }

        private static Dictionary<int, bool[]> GetObjectiveCompletionDict(int[] questIds)
        {
            var resultDict = new Dictionary<int, bool[]>();
            string[] questIdStrings = questIds.Select(id => id.ToString()).ToArray();
            var inputTable = new StringBuilder("{",
                2 + questIdStrings.Aggregate(0, (last, str) => last + str.Length) + questIdStrings.Length - 1);
            for (var i = 0; i < questIdStrings.Length; i++)
            {
                inputTable.Append(questIdStrings[i]);
                if (i < questIdStrings.Length - 1) inputTable.Append(",");
            }

            inputTable.Append("}");

            bool[] outputTable = Lua.LuaDoString<bool[]>($@"
            local inputTable = {inputTable};
            local outputTable = {{}};
            
            for _, entry in pairs(inputTable) do
                local qId = 0;
                local i = 1
                while GetQuestLogTitle(i) do
            		local questTitle, level, questTag, suggestedGroup, isHeader, isCollapsed, isComplete, isDaily, questID = GetQuestLogTitle(i)
            		if ( not isHeader ) and questID == entry then
            			qId = i;
            		end
            		i = i + 1
                end
            	
            	for j=1, 6 do
            		if not qId then
            			table.insert(outputTable, false);
            		else
            			local description, objectiveType, isCompleted = GetQuestLogLeaderBoard(j,qId);
            			if not (description == nil) then  
            				table.insert(outputTable, isCompleted == 1);
            			else
            				table.insert(outputTable, false);
            			end
            		end
            	end
            end
            return unpack(outputTable)");

            if (outputTable.Length != questIds.Length * 6)
            {
                Logger.Log(
                    $"Expected {questIds.Length * 6} entries in GetObjectiveCompletionArray but got {outputTable.Length} instead.");
                return resultDict;
            }

            for (var i = 0; i < questIds.Length; i++)
            {
                var completionArray = new bool[6];
                for (var j = 0; j < completionArray.Length; j++)
                    completionArray[j] = outputTable[i * completionArray.Length + j];

                resultDict.Add(questIds[i], completionArray);
            }

            return resultDict;
        }

        public static bool IsObjectiveCompleted(int objectiveId, int questId)
        {
            if (objectiveId == -1)
                return false;

            if (objectiveId < 1 || objectiveId > 6)
            {
                Logger.LogError($"Tried to call GetObjectiveCompletion with objectiveId: {objectiveId}");
                return false;
            }

            if (_objectiveCompletionDict.TryGetValue(questId, out bool[] completionArray))
            {
                return completionArray[objectiveId - 1];
            }

            Logger.LogDebug($"Individual update");
            Dictionary<int, bool[]> tempDic = GetObjectiveCompletionDict(new int[] { questId });
            if (tempDic.TryGetValue(questId, out bool[] tempCompArray))
            {
                return tempCompArray[objectiveId - 1];
            }

            Logger.LogError($"Did not have quest {questId} in completion dictionary.");
            return false;
        }

        public static Factions GetFaction() =>
            (PlayerFactions)ObjectManager.Me.Faction switch
            {
                PlayerFactions.Human => Factions.Human,
                PlayerFactions.Orc => Factions.Orc,
                PlayerFactions.Dwarf => Factions.Dwarf,
                PlayerFactions.NightElf => Factions.NightElf,
                PlayerFactions.Undead => Factions.Undead,
                PlayerFactions.Tauren => Factions.Tauren,
                PlayerFactions.Gnome => Factions.Gnome,
                PlayerFactions.Troll => Factions.Troll,
                PlayerFactions.Goblin => Factions.Goblin,
                PlayerFactions.BloodElf => Factions.BloodElf,
                PlayerFactions.Draenei => Factions.Draenei,
                PlayerFactions.Worgen => Factions.Worgen,
                _ => Factions.Unknown
            };

        public static Classes GetClass() =>
            ObjectManager.Me.WowClass switch
            {
                WoWClass.Warrior => Classes.Warrior,
                WoWClass.Paladin => Classes.Paladin,
                WoWClass.Hunter => Classes.Hunter,
                WoWClass.Rogue => Classes.Rogue,
                WoWClass.Priest => Classes.Priest,
                WoWClass.DeathKnight => Classes.DeathKnight,
                WoWClass.Shaman => Classes.Shaman,
                WoWClass.Mage => Classes.Mage,
                WoWClass.Warlock => Classes.Warlock,
                WoWClass.Druid => Classes.Druid,
                _ => Classes.Unknown
            };

        public static WAQPath GetWAQPath(Vector3 from, Vector3 to)
        {
            float distance = 0f;
            List<Vector3> path = FindPath(from, to, skipIfPartiel: false, resultSuccess: out bool isReachable);
            for (var i = 0; i < path.Count - 1; ++i) distance += path[i].DistanceTo(path[i + 1]);
            if (!isReachable && distance < 100)
            {
                return new WAQPath(path, 0);
            }
            return new WAQPath(path, distance);
        }

        public static Dictionary<int, int> QuestModifiedLevel = new Dictionary<int, int>()
        {
            { 354, 3 },
            { 843, 3 },
            { 6548, 3 },
            { 6629, 3 },
            { 216, 2 },
            { 541, 4 },
            { 5501, 3 },
            { 8885, 3 },
            { 1389, 3 },
            { 582, 3 },
            { 1177, 3 },
            { 1054, 3 },
            { 115, 3 },
            { 180, 3 },
            { 323, 3 },
            { 464, 3 },
            { 203, 3 },
            { 505, 3 },
            { 1439, 5 },
            { 213, 5 },
            { 1398, 4 },
            { 2870, 4 },
            { 12043, 3 },
            { 12044, 3 },
            { 12120, 3 },
        };

        public static List<WoWUnit> GetListObjManagerHostiles()
        {
            Vector3 myPosition = ObjectManager.Me.Position;
            return ObjectManager.GetObjectWoWUnit()
               .FindAll(u => u.IsAttackable
                   && u.Reaction == Reaction.Hostile
                   && u.IsAlive
                   && u.IsValid
                   && !u.IsElite
                   && !u.IsTaggedByOther
                   && !u.PlayerControlled
                   && u.Position.DistanceTo(myPosition) < 50
                   && u.Level < ObjectManager.Me.Level + 4
                   && u.Level >= ObjectManager.Me.Level - 6)
               .OrderBy(u => u.Position.DistanceTo(myPosition))
               .ToList();
        }

        /// <summary>
        /// Casts the spell from a quest's "active" item (StartItem, ItemDrop1..4 or RequiredItem1..6 with HasASpellAttached)
        /// onto the given object (creature corpse or game object). Used for "Salve via Hunting"-type and
        /// "Place explosive on door"-type quests where the kill/interact alone doesn't credit the
        /// objective; the server expects the player to use a quest item on the corpse/object.
        /// Has a per-GUID anti-loop cache so the bot doesn't spam the same item on the same target each tick.
        /// Returns true if a /use was actually issued.
        /// </summary>
        public static bool TryUseQuestItemOnTarget(ModelQuestTemplate quest, WoWObject target)
        {
            if (quest == null || target == null || target.Guid == 0)
            {
                return false;
            }

            PruneItemUsedCache();
            if (_itemUsedOnGuids.ContainsKey(target.Guid))
            {
                return false;
            }

            ModelItemTemplate itemToUse = FindActiveQuestItemInBag(quest);
            if (itemToUse == null)
            {
                return false;
            }

            Logger.Log($"Using quest item {itemToUse.Name} on {target.Name} for {quest.LogTitle}");
            Interact.InteractGameObject(target.GetBaseAddress);
            Thread.Sleep(200);
            Lua.RunMacroText($"/use {itemToUse.Name}");
            Thread.Sleep(1500);

            _itemUsedOnGuids[target.Guid] = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// Uses a quest's active item without a target (Tome of Divinity-style channel/self-cast quests).
        /// Cached per QuestId so the bot doesn't spam /use every tick. Returns true if a /use was issued.
        /// </summary>
        public static bool TryUseQuestItemFromBag(ModelQuestTemplate quest)
        {
            if (quest == null)
            {
                return false;
            }

            PruneItemUsedFromBagCache();
            if (_itemUsedFromBagForQuests.ContainsKey(quest.Id))
            {
                return false;
            }

            ModelItemTemplate itemToUse = FindActiveQuestItemInBag(quest);
            if (itemToUse == null)
            {
                return false;
            }

            Logger.Log($"Using quest item {itemToUse.Name} from bag for {quest.LogTitle}");
            Lua.RunMacroText("/cleartarget");
            Thread.Sleep(100);
            Lua.RunMacroText($"/use {itemToUse.Name}");
            Thread.Sleep(1500);

            _itemUsedFromBagForQuests[quest.Id] = DateTime.UtcNow;
            return true;
        }

        public static ModelItemTemplate FindActiveQuestItemInBag(ModelQuestTemplate quest)
        {
            ModelItemTemplate[] candidates = {
                quest.StartItemTemplate,
                quest.ItemDrop1Template,
                quest.ItemDrop2Template,
                quest.ItemDrop3Template,
                quest.ItemDrop4Template,
                quest.RequiredItem1Template,
                quest.RequiredItem2Template,
                quest.RequiredItem3Template,
                quest.RequiredItem4Template,
                quest.RequiredItem5Template,
                quest.RequiredItem6Template,
            };

            List<WoWItem> bagItems = Bag.GetBagItem();
            foreach (ModelItemTemplate candidate in candidates)
            {
                if (candidate == null || !candidate.HasASpellAttached) continue;
                if (bagItems.Any(b => b.Entry == candidate.Entry))
                {
                    return candidate;
                }
            }
            return null;
        }

        /// <summary>
        /// Returns true if any of the quest's item slots (StartItem, ItemDrop1..4, RequiredItem1..6)
        /// has a spell attached. Used by task-generation to decide whether to schedule the
        /// charm/tame variant of WAQTaskKill (UseItemOnLiveCreature) instead of the plain Kill task.
        /// </summary>
        public static bool QuestHasActiveItemWithSpell(ModelQuestTemplate quest)
        {
            if (quest == null) return false;
            return (quest.StartItemTemplate != null && quest.StartItemTemplate.HasASpellAttached)
                || (quest.ItemDrop1Template != null && quest.ItemDrop1Template.HasASpellAttached)
                || (quest.ItemDrop2Template != null && quest.ItemDrop2Template.HasASpellAttached)
                || (quest.ItemDrop3Template != null && quest.ItemDrop3Template.HasASpellAttached)
                || (quest.ItemDrop4Template != null && quest.ItemDrop4Template.HasASpellAttached)
                || (quest.RequiredItem1Template != null && quest.RequiredItem1Template.HasASpellAttached)
                || (quest.RequiredItem2Template != null && quest.RequiredItem2Template.HasASpellAttached)
                || (quest.RequiredItem3Template != null && quest.RequiredItem3Template.HasASpellAttached)
                || (quest.RequiredItem4Template != null && quest.RequiredItem4Template.HasASpellAttached)
                || (quest.RequiredItem5Template != null && quest.RequiredItem5Template.HasASpellAttached)
                || (quest.RequiredItem6Template != null && quest.RequiredItem6Template.HasASpellAttached);
        }

        private static void PruneItemUsedCache()
        {
            DateTime now = DateTime.UtcNow;
            List<ulong> expired = _itemUsedOnGuids
                .Where(kv => now - kv.Value > _itemUsedTtl)
                .Select(kv => kv.Key)
                .ToList();
            foreach (ulong key in expired)
            {
                _itemUsedOnGuids.Remove(key);
            }
        }

        private static void PruneItemUsedFromBagCache()
        {
            DateTime now = DateTime.UtcNow;
            List<int> expired = _itemUsedFromBagForQuests
                .Where(kv => now - kv.Value > _itemUsedFromBagTtl)
                .Select(kv => kv.Key)
                .ToList();
            foreach (int key in expired)
            {
                _itemUsedFromBagForQuests.Remove(key);
            }
        }
    }
}


public class ShouldSerializeContractResolver : DefaultContractResolver
{
    public static readonly ShouldSerializeContractResolver Instance = new ShouldSerializeContractResolver();

    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        JsonProperty property = base.CreateProperty(member, memberSerialization);

        if (property.PropertyType != typeof(string))
        {
            if (property.PropertyType.GetInterface(nameof(IEnumerable)) != null)
                property.ShouldSerialize =
                    instance => (instance?.GetType().GetProperty(property.UnderlyingName)?.GetValue(instance) as IEnumerable)?.OfType<object>().Count() > 0;
        }

        return property;
    }
}
