using robotManager.FiniteStateMachine;
using robotManager.Helpful;
using System.Collections.Generic;
using Wholesome_Auto_Quester.Bot.TaskManagement;
using Wholesome_Auto_Quester.Bot.TaskManagement.Tasks;
using Wholesome_Auto_Quester.Helpers;
using wManager.Wow.Bot.Tasks;
using wManager.Wow.Helpers;
using wManager.Wow.ObjectManager;

namespace Wholesome_Auto_Quester.States
{
    class WAQStateUseItemOnLiveCreature : State, IWAQState
    {
        private readonly IWowObjectScanner _scanner;

        public WAQStateUseItemOnLiveCreature(IWowObjectScanner scanner)
        {
            _scanner = scanner;
        }

        public override string DisplayName { get; set; } = "WAQ Use Item on Live Creature";

        public override bool NeedToRun
        {
            get
            {
                if (!Conditions.InGameAndConnectedAndAliveAndProductStartedNotInPause
                    || _scanner.ActiveWoWObject.wowObject == null
                    || _scanner.ActiveWoWObject.task.InteractionType != TaskInteraction.UseItemOnLiveCreature
                    || !ObjectManager.Me.IsValid)
                    return false;

                DisplayName = _scanner.ActiveWoWObject.task.TaskName;
                return true;
            }
        }

        public override void Run()
        {
            var (gameObject, task) = _scanner.ActiveWoWObject;

            if (ToolBox.ShouldStateBeInterrupted(task, gameObject))
            {
                return;
            }

            Vector3 myPos = ObjectManager.Me.Position;
            WoWUnit target = (WoWUnit)gameObject;
            Vector3 targetPos = target.Position;

            if (ToolBox.HostilesAreAround(target, task))
            {
                return;
            }

            if (!ToolBox.IHaveLineOfSightOn(target))
            {
                if (!MovementManager.InMovement)
                {
                    List<Vector3> pathToUnit = PathFinder.FindPath(targetPos);
                    MovementManager.Go(pathToUnit);
                }
                return;
            }

            // Get within typical /use range.
            if (targetPos.DistanceTo(myPos) > 8)
            {
                if (!MovementManager.InMovement)
                {
                    List<Vector3> pathToUnit = PathFinder.FindPath(targetPos);
                    MovementManager.Go(pathToUnit);
                }
                return;
            }

            MovementManager.StopMove();
            MountTask.DismountMount(false, false);

            // Try to /use the quest item on the live target. The per-GUID anti-spam cache
            // in ToolBox prevents repeated casts on the same mob each tick.
            WAQTaskUseItemOnLiveCreature liveTask = (WAQTaskUseItemOnLiveCreature)task;
            bool used = ToolBox.TryUseQuestItemOnTarget(liveTask.QuestTemplate, target);

            if (!used && target.IsAttackable && !ObjectManager.Me.InCombat)
            {
                // No usable quest item in bag right now (or already cached). Fall back to
                // engaging the mob; if it's hostile this is the kill scenario and Phase A
                // hooks will fire on the corpse.
                Logger.Log($"No quest item available, falling back to fight on {target.Name}");
                Fight.StartFight(target.Guid);
            }

            task.PostInteraction(target);
        }
    }
}
