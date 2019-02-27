using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.SpeedrunTool.RoomTimer {
    [Tracked]
    public class EndPoint : Entity {
        public enum SpriteStyle {
            Flag,
            Madeline,
            Badeline,
            Granny,
            Theo,
            Oshiro,
            Bird,
            EyeBat,
            Ogmo,
            Skytorn,
            Towerfall,
            Yuri,
            Random
        }

        private static readonly Color StarFlyColor = Calc.HexToColor("ffd65c");
        public readonly string LevelName;
        private readonly Player player;
        private SpriteStyle spriteStyle;
        public bool Activated;
        private PlayerHair playerHair;
        private PlayerSprite playerSprite;
        private readonly Facings facing;

        public EndPoint(Player player) {
            this.player = player;
            facing = player.Facing;
            LevelName = player.SceneAs<Level>().Session.Level;

            Collidable = false;
            Collider = new Hitbox(8f, 11f, -4f, -11f);
            Position = player.Position;
            Depth = player.Depth + 1;
            Add(new PlayerCollider(OnCollidePlayer));

            // saved madeline sprite
            CreateMadelineSprite();
            SetSprite();
        }

        private void SetSprite() {
            spriteStyle = SpeedrunToolModule.Settings.EndPointSpriteStyle;
            if (spriteStyle == SpriteStyle.Random) {
                spriteStyle = (SpriteStyle) new Random().Next(Enum.GetNames(typeof(SpriteStyle)).Length - 1);
            }

            switch (spriteStyle) {
                case SpriteStyle.Flag:
                    CreateFlag();
                    break;
                case SpriteStyle.Madeline:
                    CreateMadelineSprite();
                    AddMadelineSprite();
                    break;
                case SpriteStyle.Badeline:
                    CreateMadelineSprite(true);
                    AddMadelineSprite();
                    break;
                case SpriteStyle.Granny:
                case SpriteStyle.Theo:
                case SpriteStyle.Oshiro:
                case SpriteStyle.Bird:
                    CreateNpcSprite();
                    break;
                case SpriteStyle.EyeBat:
                case SpriteStyle.Ogmo:
                case SpriteStyle.Skytorn:
                case SpriteStyle.Towerfall:
                case SpriteStyle.Yuri:
                    CreateSecretSprite();
                    break;
                // ReSharper disable once RedundantCaseLabel
                case SpriteStyle.Random:
                default:
                    throw new ArgumentOutOfRangeException(nameof(spriteStyle), spriteStyle, null);
            }
        }

        private void CreateFlag() {
            AreaKey areaKey = player.SceneAs<Level>().Session.Area;
            int flagNumber = areaKey.ID;
            string numberString;
            if (SaveData.Instance.LevelSet == "Celeste") {
                if (flagNumber == 8) {
                    flagNumber = 0;
                }
                else if (flagNumber > 8) {
                    flagNumber--;
                }

                if (flagNumber == 0) {
                    numberString = flagNumber.ToString("D2");
                }
                else {
                    numberString = flagNumber.ToString() + (char) ('A' + areaKey.Mode);
                }
            }
            else {
                numberString = flagNumber.ToString("D2");
            }

            Add(new FlagComponent(numberString, spriteStyle == SpriteStyle.Flag));
        }

        public void ResetSprite() {
            Get<Sprite>()?.RemoveSelf();
            Get<PlayerHair>()?.RemoveSelf();
            Get<PlayerSprite>()?.RemoveSelf();
            Get<FlagComponent>()?.RemoveSelf();
            SetSprite();
        }

        public void ReAdded(Level level) {
            Collidable = true;
            Activated = false;
            level.Add(this);
        }

        private void OnCollidePlayer(Player _) {
            RoomTimerManager.Instance.UpdateTimerState(true);
        }

        public void StopTime() {
            if (!Activated) {
                Activated = true;
                SceneAs<Level>().Displacement.AddBurst(TopCenter, 0.5f, 4f, 24f, 0.5f);
                Scene.Add(new ConfettiRenderer(TopCenter));
                Audio.Play("event:/game/07_summit/checkpoint_confetti", TopCenter + Vector2.UnitX);
            }
        }

        private void CreateMadelineSprite(bool badeline = false) {
            PlayerSpriteMode mode;
            if (badeline) {
                mode = PlayerSpriteMode.Badeline;
            }
            else {
                bool backpack = player.SceneAs<Level>()?.Session.Inventory.Backpack ?? true;
                mode = backpack ? PlayerSpriteMode.Madeline : PlayerSpriteMode.MadelineNoBackpack;
            }

            PlayerSprite origSprite = player.Sprite;
            if (playerSprite != null) {
                origSprite = playerSprite;
            }

            playerSprite = new PlayerSprite(mode) {
                Position = origSprite.Position,
                Rotation = origSprite.Rotation,
                HairCount = origSprite.HairCount,
                Scale = origSprite.Scale,
                Rate = origSprite.Rate,
                Justify = origSprite.Justify
            };
            if (player.StateMachine.State == Player.StStarFly) {
                playerSprite.Color = StarFlyColor;
            }

            playerSprite.Scale.X = playerSprite.Scale.Abs().X * (int) facing;

            playerSprite.Active = false;
            try {
                if (!string.IsNullOrEmpty(origSprite.CurrentAnimationID)) {
                    playerSprite.Play(origSprite.CurrentAnimationID);
                    playerSprite.SetAnimationFrame(origSprite.CurrentAnimationFrame);
                }
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch (Exception) { }

            if (playerHair == null) {
                playerHair = new PlayerHair(playerSprite) {
                    Alpha = player.Hair.Alpha,
                    Facing = facing,
                    Border = player.Hair.Border,
                    DrawPlayerSpriteOutline = player.Hair.DrawPlayerSpriteOutline
                };
                Vector2[] nodes = new Vector2[player.Hair.Nodes.Count];
                player.Hair.Nodes.CopyTo(nodes);
                playerHair.Nodes = nodes.ToList();
                playerHair.Active = false;
            }

            Color hairColor = Player.NormalHairColor;
            if (player.StateMachine.State != Player.StStarFly) {
                switch (player.Dashes) {
                    case 0:
                        hairColor = badeline ? Player.UsedBadelineHairColor : Player.UsedHairColor;
                        break;
                    case 1:
                        hairColor = badeline ? Player.NormalBadelineHairColor : Player.NormalHairColor;
                        break;
                    case 2:
                        hairColor = badeline ? Player.TwoDashesBadelineHairColor : Player.TwoDashesHairColor;
                        break;
                }
            }
            else {
                hairColor = StarFlyColor;
            }

            playerHair.Color = hairColor;
        }

        private void AddMadelineSprite() {
            Add(playerHair);
            Add(playerSprite);
        }

        private void CreateNpcSprite() {
            Sprite sprite = GFX.SpriteBank.Create(Enum.GetNames(typeof(SpriteStyle))[(int) spriteStyle].ToLower());
            if (spriteStyle == SpriteStyle.Oshiro) {
                sprite.Position += Vector2.UnitY * 7;
            }

            sprite.Scale.X *= (int) facing;
            Add(sprite);
        }

        private void CreateSecretSprite() {
            string id = "secret_" + Enum.GetNames(typeof(SpriteStyle))[(int) spriteStyle].ToLower();
            Sprite sprite = new Sprite(GFX.Game, "decals/6-reflection/" + id);
            sprite.AddLoop(id, "", 0.1f);
            sprite.Play(id);
            sprite.CenterOrigin();

            Vector2 offset = Vector2.UnitY * -8;
            if (spriteStyle != SpriteStyle.EyeBat) {
                offset = Vector2.UnitY * -16;
            }

            sprite.RenderPosition += offset;

            sprite.Scale.X *= (int) facing;
            if (spriteStyle == SpriteStyle.Towerfall) {
                sprite.Scale.X = -sprite.Scale.X;
            }

            Add(sprite);
        }
    }
}