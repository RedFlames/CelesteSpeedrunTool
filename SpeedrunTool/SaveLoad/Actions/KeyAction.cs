using System.Collections.Generic;
using System.Linq;

namespace Celeste.Mod.SpeedrunTool.SaveLoad.Actions
{
    public class KeyAction : AbstractEntityAction
    {
        private Dictionary<EntityID, Key> _savedKeys = new Dictionary<EntityID, Key>();

        public override void OnQuickSave(Level level)
        {
            _savedKeys = level.Tracker.GetCastEntities<Key>().ToDictionary(key => key.ID);
        }

        private void RestoreKeyPosition(On.Celeste.Key.orig_ctor_Player_EntityID orig,
            Key self, Player player,
            EntityID entityId)
        {
            orig(self, player, entityId);

            if (IsLoadStart && _savedKeys.ContainsKey(entityId))
            {
                self.Position = _savedKeys[entityId].Position;
            }
        }

        public override void OnClear()
        {
            _savedKeys.Clear();
        }

        public override void OnLoad()
        {
            On.Celeste.Key.ctor_Player_EntityID += RestoreKeyPosition;
        }

        public override void OnUnload()
        {
            On.Celeste.Key.ctor_Player_EntityID -= RestoreKeyPosition;
        }

        public override void OnInit()
        {
            typeof(Key).AddToTracker();
        }

        public override void OnUpdateEntitiesWhenFreeze(Level level)
        {
            level.UpdateEntities<Key>();
        }
    }
}