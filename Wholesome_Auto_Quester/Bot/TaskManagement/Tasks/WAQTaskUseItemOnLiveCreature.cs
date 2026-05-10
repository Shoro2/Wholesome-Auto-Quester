using Wholesome_Auto_Quester.Bot.ContinentManagement;
using Wholesome_Auto_Quester.Database.Models;
using Wholesome_Auto_Quester.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.Bot.TaskManagement.Tasks
{
    /// <summary>
    /// Charm/Tame-style task: target a live creature and /use a quest-bound item on it.
    /// Generated INSTEAD OF WAQTaskKill when the quest hands the player an item with a
    /// spell attached. The state handler tries the /use first; if it doesn't credit and
    /// the mob attacks back, the bot's normal Defend/Fight states will engage and Phase A
    /// will fire on the corpse as a fallback.
    /// </summary>
    public class WAQTaskUseItemOnLiveCreature : WAQBaseScannableTask
    {
        public ModelQuestTemplate QuestTemplate { get; }

        public WAQTaskUseItemOnLiveCreature(ModelQuestTemplate questTemplate, ModelCreatureTemplate creatureTemplate, ModelCreature creature, IContinentManager continentManager)
            : base(creature.GetSpawnPosition, creature.map, $"Use quest item on {creatureTemplate.Name} for {questTemplate.LogTitle}", creatureTemplate.Entry,
                  creature.spawnTimeSecs, creature.guid, continentManager)
        {
            QuestTemplate = questTemplate;

            // Slightly higher than WAQTaskKill so the bot tries the item path first.
            PriorityShift = 3;
            if (QuestTemplate.QuestAddon?.AllowableClasses > 0)
            {
                PriorityShift = 4;
            }
            if (QuestTemplate.TimeAllowed > 0)
            {
                PriorityShift = 8;
            }
        }

        public new void PutTaskOnTimeout(string reason, int timeInSeconds, bool exponentiallyLonger)
            => base.PutTaskOnTimeout(reason, timeInSeconds > 0 ? timeInSeconds : DefaultTimeOutDuration, exponentiallyLonger);

        public override bool IsObjectValidForTask(WoWObject wowObject)
        {
            if (wowObject is WoWUnit unit)
            {
                return unit.IsAlive && !unit.IsTaggedByOther;
            }
            return false;
        }

        public override void PostInteraction(WoWObject wowObject)
        {
            // Server has up to 30s to credit the objective. If it doesn't, this task
            // becomes valid again and we'll retry on the same or another mob.
            PutTaskOnTimeout("Used quest item on live target, waiting for credit", 30, true);
        }

        public override string TrackerColor => "Gold";
        public override TaskInteraction InteractionType => TaskInteraction.UseItemOnLiveCreature;
        protected override bool HasEnoughSkillForTask => true;
        protected override string ReputationMismatch => QuestTemplate.ReputationMismatch;
    }
}
