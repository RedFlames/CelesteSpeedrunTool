using System;
using System.Collections.Generic;
using Celeste.Mod.SpeedrunTool.SaveLoad.RestoreActions.ActorActions;
using Celeste.Mod.SpeedrunTool.SaveLoad.RestoreActions.EntityActions;
using Monocle;

namespace Celeste.Mod.SpeedrunTool.SaveLoad.RestoreActions {
    public class EntityRestoreAction : RestoreAction {
        public static List<RestoreAction> AllRestoreActions => AllRestoreActionsLazy.Value;

        private static readonly Lazy<List<RestoreAction>> AllRestoreActionsLazy =
            new Lazy<List<RestoreAction>>(
                () => {
                    List<RestoreAction> result = new List<RestoreAction>();

                    void AddThisAndSubclassRestoreActions(RestoreAction action) {
                        result.Add(action);
                        action.SubclassRestoreActions.ForEach(AddThisAndSubclassRestoreActions);
                    }

                    AddThisAndSubclassRestoreActions(Instance);

                    return result;
                });

        private static readonly EntityRestoreAction Instance = new EntityRestoreAction(
            new List<RestoreAction> {
                new PlayerRestoreAction(),
                new KeyRestoreAction(),
                new StrawberryRestoreAction(),
                new TriggerSpikesRestoreAction(),
            }
        );

        private EntityRestoreAction(List<RestoreAction> subclassRestoreActions) : base(typeof(Entity),
            subclassRestoreActions) { }

        public override void AfterEntityCreateAndUpdate1Frame(Entity loadedEntity, Entity savedEntity) {
            // Player 需要特殊处理，由 PlayerRestoreAction 负责
            if (loadedEntity is Player) return;

            // CrystalStaticSpinner 看不见的地方等于不存在，ch9 g-06 保存恢复后屏幕外的刺无法恢复显示，所以只恢复位置就好
            if (loadedEntity is CrystalStaticSpinner) {
                loadedEntity.Position = savedEntity.Position;
                return;
            }

            loadedEntity.CopyAllFrom(savedEntity, typeof(Entity));

            IEnumerable<Tween> loadedTweens = loadedEntity.Components.GetAll<Tween>();
            IEnumerable<Tween> savedTweens = savedEntity.Components.GetAll<Tween>();
            foreach (Tween loadedTween in loadedTweens) {
                foreach (Tween savedTween in savedTweens) {
                    if ( loadedTween.OnStart?.Method == savedTween.OnStart?.Method &&
                        loadedTween.OnUpdate?.Method == savedTween.OnUpdate?.Method &&
                        loadedTween.OnComplete?.Method == savedTween.OnComplete?.Method) {
                        loadedTween.CopySpecifiedType(savedTween);
                    }
                }
            }

            // if (loadedEntity is DreamBlock loadedBlock && savedEntity is DreamBlock savedBlock) {
            //     Tween loadedTween = loadedBlock.Get<Tween>();
            //     Tween savedTween = savedBlock.Get<Tween>();
            //     if(loadedTween == null ||savedTween == null) return;
            //     loadedTween.CopyAllFrom(savedTween, typeof(Component));
            // }
        }
    }
}