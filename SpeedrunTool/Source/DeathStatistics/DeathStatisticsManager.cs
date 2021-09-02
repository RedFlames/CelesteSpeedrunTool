using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Celeste.Mod.Helpers;
using Celeste.Mod.SpeedrunTool.Extensions;
using Celeste.Mod.SpeedrunTool.Other;
using Celeste.Mod.SpeedrunTool.SaveLoad;
using Force.DeepCloner;
using Microsoft.Xna.Framework;
using Monocle;
using MonoMod.RuntimeDetour;

namespace Celeste.Mod.SpeedrunTool.DeathStatistics {
    public class DeathStatisticsManager {
        // @formatter:off
        private static readonly Lazy<DeathStatisticsManager> Lazy = new(() => new DeathStatisticsManager());
        public static DeathStatisticsManager Instance => Lazy.Value;
        private DeathStatisticsManager() { }
        // @formatter:on

        public static readonly string PlaybackDir = Path.Combine(typeof(UserIO).GetFieldValue("SavePath").ToString(), "SpeedrunTool", "DeathPlayback");
        private static bool Enabled => SpeedrunToolModule.Settings.Enabled && SpeedrunToolModule.Settings.DeathStatistics;

        private long lastTime;
        private bool died;
        private DeathInfo currentDeathInfo;
        private DeathInfo playbackDeathInfo;

        public void Load() {
            // 尽量晚的 Hook Player.Die 方法，以便可以稳定的从指定的 StackTrace 中找出死亡原因
            using (new DetourContext {After = new List<string> {"*"}}) {
                On.Celeste.Player.Die += PlayerOnDie;
            }

            On.Celeste.PlayerDeadBody.End += PlayerDeadBodyOnEnd;
            On.Celeste.Level.NextLevel += LevelOnNextLevel;
            On.Celeste.Player.Update += PlayerOnUpdate;
            On.Celeste.OuiFileSelectSlot.EnterFirstArea += OuiFileSelectSlotOnEnterFirstArea;
            On.Celeste.ChangeRespawnTrigger.OnEnter += ChangeRespawnTriggerOnOnEnter;
            On.Celeste.Session.SetFlag += UpdateTimerStateOnTouchFlag;
            On.Celeste.LevelLoader.ctor += LevelLoaderOnCtor;
            On.Celeste.Level.LoadLevel += LevelOnLoadLevel;
            On.Monocle.Scene.Begin += SceneOnBegin;

            Hotkeys.CheckDeathStatistics.RegisterPressedAction(scene => {
                if (scene.Tracker.GetEntity<DeathStatisticsUi>() is { } deathStatisticsUi) {
                    deathStatisticsUi.OnESC?.Invoke();
                } else if (scene is Level {Paused: false} level && !level.IsPlayerDead()) {
                    level.Paused = true;
                    DeathStatisticsUi buttonConfigUi = new() {
                        OnClose = () => level.Paused = false
                    };
                    level.Add(buttonConfigUi);
                    level.OnEndOfFrame += level.Entities.UpdateLists;
                }
            });
        }

        public void Unload() {
            On.Celeste.Player.Die -= PlayerOnDie;
            On.Celeste.PlayerDeadBody.End -= PlayerDeadBodyOnEnd;
            On.Celeste.Level.NextLevel -= LevelOnNextLevel;
            On.Celeste.Player.Update -= PlayerOnUpdate;
            On.Celeste.OuiFileSelectSlot.EnterFirstArea -= OuiFileSelectSlotOnEnterFirstArea;
            On.Celeste.ChangeRespawnTrigger.OnEnter -= ChangeRespawnTriggerOnOnEnter;
            On.Celeste.Session.SetFlag -= UpdateTimerStateOnTouchFlag;
            On.Celeste.LevelLoader.ctor -= LevelLoaderOnCtor;
            On.Celeste.Level.LoadLevel -= LevelOnLoadLevel;
            On.Monocle.Scene.Begin -= SceneOnBegin;
        }

        private void SceneOnBegin(On.Monocle.Scene.orig_Begin orig, Scene self) {
            orig(self);
            if (self is Overworld or LevelExit) {
                Clear();
            }
        }

        private void LevelOnLoadLevel(On.Celeste.Level.orig_LoadLevel orig, Level level, Player.IntroTypes playerIntro, bool isFromLoader) {
            if (IsPlayback()) {
                level.Add(new DeathMark(playbackDeathInfo.DeathPosition));

                if (!string.IsNullOrEmpty(playbackDeathInfo.PlaybackFilePath) && File.Exists(playbackDeathInfo.PlaybackFilePath)) {
                    List<Player.ChaserState> chaserStates = PlaybackData.Import(FileProxy.ReadAllBytes(playbackDeathInfo.PlaybackFilePath));
                    PlayerSpriteMode spriteMode = level.Session.Inventory.Backpack ? PlayerSpriteMode.Madeline : PlayerSpriteMode.MadelineNoBackpack;
                    if (SaveData.Instance.Assists.PlayAsBadeline) {
                        spriteMode = PlayerSpriteMode.MadelineAsBadeline;
                    }

                    PlayerPlayback playerPlayback = new(playbackDeathInfo.PlaybackStartPosition, spriteMode, chaserStates) {
                        Depth = Depths.Player
                    };
                    playerPlayback.IgnoreSaveLoad().Sprite.Color *= 0.8f;
                    level.Add(playerPlayback);
                }
            }

            orig(level, playerIntro, isFromLoader);
        }

        private void LevelLoaderOnCtor(On.Celeste.LevelLoader.orig_ctor orig, LevelLoader self, Session session,
            Vector2? startPosition) {
            orig(self, session, startPosition);

            lastTime = SaveData.Instance.Time;
        }

        private void UpdateTimerStateOnTouchFlag(On.Celeste.Session.orig_SetFlag origSetFlag, Session session,
            string flag, bool setTo) {
            origSetFlag(session, flag, setTo);

            if (flag.StartsWith("summit_checkpoint_") && setTo) {
                lastTime = SaveData.Instance.Time;
            }
        }

        private void ChangeRespawnTriggerOnOnEnter(On.Celeste.ChangeRespawnTrigger.orig_OnEnter orig, ChangeRespawnTrigger self, Player player) {
            Level level = player.SceneAs<Level>();
            Vector2? oldPoint = level.Session.RespawnPoint;
            orig(self, player);
            Vector2? newPoint = level.Session.RespawnPoint;

            if (oldPoint != newPoint) {
                lastTime = SaveData.Instance.Time;
            }
        }

        private void OuiFileSelectSlotOnEnterFirstArea(On.Celeste.OuiFileSelectSlot.orig_EnterFirstArea orig,
            OuiFileSelectSlot self) {
            orig(self);

            if (!Enabled) {
                return;
            }

            lastTime = 0;
        }

        private void PlayerOnUpdate(On.Celeste.Player.orig_Update orig, Player player) {
            orig(player);

            if (Enabled && died && player.StateMachine.State is Player.StNormal or Player.StSwim) {
                died = false;
                LoggingData();
            }
        }

        private void ExportPlayback(Player player) {
            string filePath = Path.Combine(PlaybackDir, $"{DateTime.Now.Ticks}.bin");
            if (!Directory.Exists(PlaybackDir)) {
                Directory.CreateDirectory(PlaybackDir);
            }

            if (player.ChaserStates.Count > 0) {
                PlaybackData.Export(player.ChaserStates, filePath);
                currentDeathInfo.PlaybackStartPosition = player.ChaserStates[0].Position;
                currentDeathInfo.PlaybackFilePath = filePath;
            } else {
                currentDeathInfo.PlaybackStartPosition = default;
                currentDeathInfo.PlaybackFilePath = string.Empty;
            }
        }

        private void LevelOnNextLevel(On.Celeste.Level.orig_NextLevel orig, Level self, Vector2 at, Vector2 dir) {
            orig(self, at, dir);

            if (Enabled) {
                lastTime = SaveData.Instance.Time;
            }
        }

        private PlayerDeadBody PlayerOnDie(On.Celeste.Player.orig_Die orig, Player self, Vector2 direction,
            bool evenIfInvincible, bool registerDeathInStats) {
            PlayerDeadBody playerDeadBody = orig(self, direction, evenIfInvincible, registerDeathInStats);

            if (playerDeadBody != null && Enabled) {
                if (IsPlayback()) {
                    currentDeathInfo = null;
                    playbackDeathInfo = null;
                } else {
                    currentDeathInfo = new DeathInfo {
                        CauseOfDeath = GetCauseOfDeath(),
                        DeathPosition = self.Position
                    };
                    ExportPlayback(self);
                }
            }

            return playerDeadBody;
        }

        private void PlayerDeadBodyOnEnd(On.Celeste.PlayerDeadBody.orig_End orig, PlayerDeadBody self) {
            orig(self);
            if (Enabled) {
                died = true;
            }
        }

        private bool IsPlayback() {
            Level level = Engine.Scene switch {
                Level lvl => lvl,
                LevelLoader levelLoader => levelLoader.Level,
                _ => null
            };
            return level != null && playbackDeathInfo != null && playbackDeathInfo.Area == level.Session.Area &&
                   playbackDeathInfo.Room == level.Session.Level;
        }

        private void LoggingData() {
            // 传送到死亡地点练习时产生的第一次死亡不记录，清除死亡地点
            if (Engine.Scene is not Level level || IsPlayback() || currentDeathInfo == null) {
                Clear();
                return;
            }

            currentDeathInfo.CopyFromSession(level.Session);
            currentDeathInfo.LostTime = SaveData.Instance.Time - lastTime;
            SpeedrunToolModule.SaveData.Add(currentDeathInfo);
            lastTime = SaveData.Instance.Time;
            currentDeathInfo = null;
        }

        private string GetCauseOfDeath() {
            StackTrace stackTrace = new(3);
            MethodBase deathMethod = stackTrace.GetFrame(0).GetMethod();
            string death = deathMethod.ReflectedType?.Name ?? "";

            if (death == "Level") {
                death = "Fall";
            } else if (death.Contains("DisplayClass")) {
                death = "Retry";
            } else if (death == "Player") {
                death = deathMethod.Name;
                if (death == "OnSquish") {
                    death = "Crushed";
                } else if (death == "DreamDashUpdate") {
                    death = "Dream Dash";
                } else if (death == "BirdDashTutorialCoroutine") {
                    death = "Bird Dash Tutorial";
                }
            } else {
                death = Regex.Replace(death, @"([a-z])([A-Z])", "$1 $2");
            }

            return death;
        }

        public void TeleportToDeathPosition(DeathInfo deathInfo) {
            playbackDeathInfo = deathInfo;
            Engine.Scene = new LevelLoader(deathInfo.Session.DeepClone());
        }

        public void Clear() {
            died = false;
            lastTime = SaveData.Instance?.Time ?? 0;
            currentDeathInfo = null;
            playbackDeathInfo = null;
        }
    }
}