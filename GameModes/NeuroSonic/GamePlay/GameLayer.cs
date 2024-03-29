﻿using System;
using System.Numerics;

using theori;
using theori.Audio;
using theori.Charting;
using theori.Charting.Effects;
using theori.Charting.Playback;
using theori.Graphics;
using theori.Gui;
using theori.IO;
using theori.Resources;

using NeuroSonic.Charting;
using NeuroSonic.GamePlay.Scoring;
using theori.Charting.Serialization;
using System.IO;
using MoonSharp.Interpreter;
using theori.Scripting;
using System.Collections.Generic;

namespace NeuroSonic.GamePlay
{
    [Flags]
    public enum AutoPlay
    {
        None = 0,

        Buttons = 0x01,
        Lasers = 0x02,

        ButtonsAndLasers = Buttons | Lasers,
    }

    public sealed class GameLayer : NscLayer
    {
        public override int TargetFrameRate => 288;

        public override bool BlocksParentLayer => true;

        private readonly AutoPlay m_autoPlay;

        private bool AutoButtons => (m_autoPlay & AutoPlay.Buttons) != 0;
        private bool AutoLasers => (m_autoPlay & AutoPlay.Lasers) != 0;

        private readonly ClientResourceLocator m_locator;
        private readonly ClientResourceManager m_resources;

        private LuaScript m_guiScript;
        private Table m_gameTable, m_metaTable, m_scoringTable;

        private HighwayControl m_highwayControl;
        private HighwayView m_highwayView;

        private ScriptableBackground m_background;

        private CriticalLine m_critRoot;
        private ComboDisplay m_comboDisplay;

        private ChartInfo m_chartInfo;
        private Chart m_chart;
        private SlidingChartPlayback m_playback;
        private MasterJudge m_judge;

        private AudioEffectController m_audioController;
        private AudioTrack m_audio;
        private AudioSample m_slamSample;

        private readonly Entity[] m_activeObjects = new Entity[8];
        private readonly bool[] m_streamHasActiveEffects = new bool[8].Fill(true);

        private readonly bool[] m_cursorsActive = new bool[2];
        private readonly float[] m_cursorAlphas = new float[2];

        private readonly EffectDef[] m_currentEffects = new EffectDef[8];

        private readonly List<EventEntity> m_queuedSlamTiedEvents = new List<EventEntity>();
        private readonly List<time_t> m_queuedSlams = new List<time_t>();

        private time_t CurrentQuarterNodeDuration => m_chart.ControlPoints.MostRecent(m_audioController.Position).QuarterNoteDuration;

        #region Debug Overlay

        private GameDebugOverlay m_debugOverlay;

        #endregion

        internal GameLayer(ClientResourceLocator resourceLocator, ChartInfo chartInfo, AutoPlay autoPlay = AutoPlay.None)
        {
            m_locator = resourceLocator;
            m_resources = new ClientResourceManager(resourceLocator);

            m_chartInfo = chartInfo;
            m_autoPlay = autoPlay;

            m_highwayView = new HighwayView(m_locator);
            m_background = new ScriptableBackground(m_locator);
        }

        internal GameLayer(ClientResourceLocator resourceLocator, Chart chart, AudioTrack audio, AutoPlay autoPlay = AutoPlay.None)
        {
            m_locator = resourceLocator;
            m_resources = new ClientResourceManager(resourceLocator);

            m_chartInfo = chart.Info;
            m_chart = chart;
            m_audio = audio;

            m_autoPlay = autoPlay;

            m_highwayView = new HighwayView(m_locator);
            m_background = new ScriptableBackground(m_locator);
        }

        public override void Destroy()
        {
            base.Destroy();

            m_highwayView.Dispose();
            m_background.Dispose();
            m_resources.Dispose();

            if (m_debugOverlay != null)
            {
                Host.RemoveOverlay(m_debugOverlay);
                m_debugOverlay = null;
            }

            m_audioController?.Stop();
            m_audioController?.Dispose();
        }

        public override bool AsyncLoad()
        {
            if (m_chart == null)
            {
                string chartsDir = Plugin.Config.GetString(NscConfigKey.StandaloneChartsDirectory);
                var setInfo = m_chartInfo.Set;

                var serializer = new ChartSerializer(chartsDir, NeuroSonicGameMode.Instance);
                m_chart = serializer.LoadFromFile(m_chartInfo);

                string audioFile = Path.Combine(chartsDir, setInfo.FilePath, m_chart.Info.SongFileName);
                m_audio = AudioTrack.FromFile(audioFile);
                m_audio.Channel = Host.Mixer.MasterChannel;
                m_audio.Volume = m_chart.Info.SongVolume / 100.0f;
            }

            m_guiScript = new LuaScript();
            m_guiScript.InitResourceLoading(m_locator);

            m_gameTable = m_guiScript.NewTable();
            m_guiScript["game"] = m_gameTable;

            m_gameTable["meta"] = m_metaTable = m_guiScript.NewTable();
            m_gameTable["scoring"] = m_scoringTable = m_guiScript.NewTable();

            m_metaTable["SongTitle"] = m_chart.Info.SongTitle;
            m_metaTable["SongArtist"] = m_chart.Info.SongArtist;

            m_metaTable["DifficultyName"] = m_chart.Info.DifficultyName;
            m_metaTable["DifficultyNameShort"] = m_chart.Info.DifficultyNameShort;
            m_metaTable["DifficultyLevel"] = m_chart.Info.DifficultyLevel;
            m_metaTable["DifficultyColor"] = (m_chart.Info.DifficultyColor ?? new Vector3(1, 1, 1)) * 255;

            m_metaTable["PlayKind"] = "N";

            m_guiScript.LoadFile(m_locator.OpenFileStream("scripts/game/main.lua"));

            if (!m_guiScript.LuaAsyncLoad())
                return false;

            if (!m_highwayView.AsyncLoad())
                return false;
            if (!m_background.AsyncLoad())
                return false;

            m_slamSample = m_resources.QueueAudioSampleLoad("audio/slam");

            if (!m_resources.LoadAll())
                return false;

            return true;
        }

        public override bool AsyncFinalize()
        {
            if (!m_guiScript.LuaAsyncFinalize())
                return false;
            m_guiScript.InitSpriteRenderer(m_locator);

            if (!m_highwayView.AsyncFinalize())
                return false;
            if (!m_background.AsyncFinalize())
                return false;

            if (!m_resources.FinalizeLoad())
                return false;

            m_slamSample.Channel = Host.Mixer.MasterChannel;

            return true;
        }

        public override void ClientSizeChanged(int width, int height)
        {
            m_highwayView.Camera.AspectRatio = Window.Aspect;
        }

        public override void Init()
        {
            base.Init();

            m_highwayControl = new HighwayControl(HighwayControlConfig.CreateDefaultKsh168());
            m_background.Init();

            m_playback = new SlidingChartPlayback(m_chart);
            var hispeedKind = Plugin.Config.GetEnum<HiSpeedMod>(NscConfigKey.HiSpeedModKind);
            switch (hispeedKind)
            {
                case HiSpeedMod.Default:
                {
                    double hiSpeed = Plugin.Config.GetFloat(NscConfigKey.HiSpeed);
                    m_playback.LookAhead = 8 * 60.0 / (m_chart.ControlPoints.ModeBeatsPerMinute * hiSpeed);
                } break;
                case HiSpeedMod.MMod:
                {
                    var modSpeed = Plugin.Config.GetFloat(NscConfigKey.ModSpeed);
                    double hiSpeed = modSpeed / m_chart.ControlPoints.ModeBeatsPerMinute;
                    m_playback.LookAhead = 8 * 60.0 / (m_chart.ControlPoints.ModeBeatsPerMinute * hiSpeed);
                } break;
                case HiSpeedMod.CMod:
                {
                } goto case HiSpeedMod.Default; //break;
            }

            m_playback.ObjectHeadCrossPrimary += (dir, entity) =>
            {
                if (dir == PlayDirection.Forward)
                {
                    m_highwayView.RenderableObjectAppear(entity);
                    if (entity is EventEntity evt)
                    {
                        switch (evt)
                        {
                            case SpinImpulseEvent _:
                            case SwingImpulseEvent _:
                            case WobbleImpulseEvent _:
                                m_queuedSlamTiedEvents.Add(evt);
                                break;
                        }
                    }
                }
                else m_highwayView.RenderableObjectDisappear(entity);
            };
            m_playback.ObjectTailCrossSecondary += (dir, obj) =>
            {
                if (dir == PlayDirection.Forward)
                    m_highwayView.RenderableObjectDisappear(obj);
                else m_highwayView.RenderableObjectAppear(obj);
            };

            // TODO(local): Effects wont work with backwards motion, but eventually the
            //  editor (with the only backwards motion support) will pre-render audio instead.
            m_playback.ObjectHeadCrossCritical += (dir, obj) =>
            {
                if (dir != PlayDirection.Forward) return;

                if (obj is EventEntity evt)
                    PlaybackEventTrigger(evt, dir);
                else PlaybackObjectBegin(obj);
            };
            m_playback.ObjectTailCrossCritical += (dir, obj) =>
            {
                if (dir == PlayDirection.Backward && obj is EventEntity evt)
                    PlaybackEventTrigger(evt, dir);
                else PlaybackObjectEnd(obj);
            };

            m_highwayView.ViewDuration = m_playback.LookAhead;

            ForegroundGui = new Panel()
            {
                Children = new GuiElement[]
                {
                    m_critRoot = new CriticalLine(m_resources),
                    m_comboDisplay = new ComboDisplay(m_resources)
                    {
                        RelativePositionAxes = Axes.Both,
                        Position = new Vector2(0.5f, 0.7f)
                    },
                }
            };

            m_judge = new MasterJudge(m_chart);
            for (int i = 0; i < 6; i++)
            {
                var judge = (ButtonJudge)m_judge[i];
                judge.JudgementOffset = Plugin.Config.GetInt(NscConfigKey.InputOffset) / 1000.0f;
                judge.AutoPlay = AutoButtons;
                judge.OnChipPressed += Judge_OnChipPressed;
                judge.OnTickProcessed += Judge_OnTickProcessed;
                judge.OnHoldPressed += Judge_OnHoldPressed;
                judge.OnHoldReleased += Judge_OnHoldReleased;
            }
            for (int i = 0; i < 2; i++)
            {
                int iStack = i;

                var judge = (LaserJudge)m_judge[i + 6];
                judge.JudgementOffset = Plugin.Config.GetInt(NscConfigKey.InputOffset) / 1000.0f;
                judge.AutoPlay = AutoLasers;
                judge.OnShowCursor += () => m_cursorsActive[iStack] = true;
                judge.OnHideCursor += () => m_cursorsActive[iStack] = false;
                judge.OnTickProcessed += Judge_OnTickProcessed;
                judge.OnSlamHit += (position, entity) =>
                {
                    if (position < entity.AbsolutePosition)
                        m_queuedSlams.Add(entity.AbsolutePosition);
                    else m_slamSample.Play();
                };
            }

            m_highwayControl = new HighwayControl(HighwayControlConfig.CreateDefaultKsh168());
            m_highwayView.Reset();

            m_audio.Volume = 0.8f;
            m_audio.Position = m_chart.Offset;
            m_audioController = new AudioEffectController(8, m_audio, true)
            {
                RemoveFromChannelOnFinish = true,
            };
            m_audioController.Finish += () =>
            {
                Logger.Log("track complete");
                Host.PopToParent(this);
            };

            m_audioController.Position = MathL.Min(0.0, (double)m_chart.TimeStart - 2);

            m_gameTable["Begin"] = (Action)Begin;
            m_guiScript.CallIfExists("Init");
        }

        public void Begin()
        {
            m_audioController.Play();
        }

        public override void Suspended()
        {
            throw new Exception("Cannot suspend gameplay layer");
        }

        public override void Resumed()
        {
            throw new Exception("Cannot suspend gameplay layer");
        }

        private void PlaybackObjectBegin(Entity entity)
        {
            if (entity is AnalogEntity aobj)
            {
                if (entity.IsInstant)
                {
                    int dir = -MathL.Sign(aobj.FinalValue - aobj.InitialValue);
                    m_highwayControl.ShakeCamera(dir);

                    if (aobj.InitialValue == (aobj.Lane == 6 ? 0 : 1) && aobj.NextConnected == null)
                        m_highwayControl.ApplyRollImpulse(-dir);

                    //if (m_judge[(int)entity.Lane].IsBeingPlayed) m_slamSample.Play();
                }

                if (aobj.PreviousConnected == null)
                {
                    if (!AreLasersActive) m_audioController.SetEffect(6, CurrentQuarterNodeDuration, currentLaserEffectDef, BASE_LASER_MIX);
                    currentActiveLasers[(int)entity.Lane - 6] = true;
                }

                m_activeObjects[(int)entity.Lane] = aobj.Head;
            }
            else if (entity is ButtonEntity bobj)
            {
                //if (bobj.HasEffect) m_audioController.SetEffect(obj.Stream, CurrentQuarterNodeDuration, bobj.Effect);
                //else m_audioController.RemoveEffect(obj.Stream);

                // NOTE(local): can move this out for analog as well, but it doesn't matter RN
                if (!bobj.IsInstant)
                    m_activeObjects[(int)entity.Lane] = entity;
            }
        }

        private void PlaybackObjectEnd(Entity obj)
        {
            if (obj is AnalogEntity aobj)
            {
                if (aobj.NextConnected == null)
                {
                    currentActiveLasers[(int)obj.Lane - 6] = false;
                    if (!AreLasersActive) m_audioController.RemoveEffect(6);

                    if (m_activeObjects[(int)obj.Lane] == aobj.Head)
                        m_activeObjects[(int)obj.Lane] = null;
                }
            }
            if (obj is ButtonEntity bobj)
            {
                //m_audioController.RemoveEffect(obj.Stream);

                // guard in case the Begin function already overwrote us
                if (m_activeObjects[(int)obj.Lane] == obj)
                    m_activeObjects[(int)obj.Lane] = null;
            }
        }

        private void Judge_OnTickProcessed(Entity entity, time_t position, JudgeResult result)
        {
            //Logger.Log($"[{ obj.Stream }] { result.Kind } :: { (int)(result.Difference * 1000) } @ { position }");

            if (result.Kind == JudgeKind.Miss || result.Kind == JudgeKind.Bad)
                m_comboDisplay.Combo = 0;
            else m_comboDisplay.Combo++;

            if ((int)entity.Lane >= 6) return;

            if (entity.IsInstant)
            {
                if (result.Kind != JudgeKind.Miss)
                    CreateKeyBeam((int)entity.Lane, result.Kind, result.Difference < 0.0);
            }
        }

        private void Judge_OnChipPressed(time_t position, Entity obj)
        {
        }

        private void Judge_OnHoldReleased(time_t position, Entity obj)
        {
        }

        private void Judge_OnHoldPressed(time_t position, Entity obj)
        {
            CreateKeyBeam((int)obj.Lane, JudgeKind.Passive, false);
        }

        private void PlaybackEventTrigger(EventEntity evt, PlayDirection direction)
        {
            if (direction == PlayDirection.Forward)
            {
                switch (evt)
                {
                    case EffectKindEvent effectKind:
                    {
                        var effect = m_currentEffects[effectKind.EffectIndex] = effectKind.Effect;
                        if (effect == null)
                            m_audioController.RemoveEffect(effectKind.EffectIndex);
                        else m_audioController.SetEffect(effectKind.EffectIndex, CurrentQuarterNodeDuration, effect, 1.0f);
                    }
                    break;

                    case LaserApplicationEvent app: m_highwayControl.LaserApplication = app.Application; break;

                    // TODO(local): left/right lasers separate + allow both independent if needed
                    case LaserFilterGainEvent filterGain: laserGain = filterGain.Gain; break;
                    case LaserFilterKindEvent filterKind:
                    {
                        m_audioController.SetEffect(6, CurrentQuarterNodeDuration, currentLaserEffectDef = filterKind.Effect, m_audioController.GetEffectMix(6));
                    }
                    break;

                    case LaserParamsEvent pars:
                    {
                        if (pars.LaserIndex.HasFlag(LaserIndex.Left)) m_highwayControl.LeftLaserParams = pars.Params;
                        if (pars.LaserIndex.HasFlag(LaserIndex.Right)) m_highwayControl.RightLaserParams = pars.Params;
                    }
                    break;

                    case SlamVolumeEvent pars: m_slamSample.Volume = pars.Volume; break;
                }
            }
        }

        protected internal override bool ControllerButtonPressed(ControllerInput input)
        {
            switch (input)
            {
                case ControllerInput.BT0: UserInput_BtPress(0); break;
                case ControllerInput.BT1: UserInput_BtPress(1); break;
                case ControllerInput.BT2: UserInput_BtPress(2); break;
                case ControllerInput.BT3: UserInput_BtPress(3); break;
                case ControllerInput.FX0: UserInput_BtPress(4); break;
                case ControllerInput.FX1: UserInput_BtPress(5); break;

                case ControllerInput.Start:
                    break;

                case ControllerInput.Back: Host.PopToParent(this); break;

                default: return false;
            }

            return true;
        }

        protected internal override bool ControllerButtonReleased(ControllerInput input)
        {
            switch (input)
            {
                case ControllerInput.BT0: UserInput_BtRelease(0); break;
                case ControllerInput.BT1: UserInput_BtRelease(1); break;
                case ControllerInput.BT2: UserInput_BtRelease(2); break;
                case ControllerInput.BT3: UserInput_BtRelease(3); break;
                case ControllerInput.FX0: UserInput_BtRelease(4); break;
                case ControllerInput.FX1: UserInput_BtRelease(5); break;

                case ControllerInput.Start:
                case ControllerInput.Back:
                    break;

                default: return false;
            }

            return true;
        }

        protected internal override bool ControllerAxisChanged(ControllerInput input, float delta)
        {
            switch (input)
            {
                case ControllerInput.Laser0Axis: UserInput_VolPulse(0, delta); break;
                case ControllerInput.Laser1Axis: UserInput_VolPulse(1, delta); break;

                default: return false;
            }

            return true;
        }

        public override bool KeyPressed(KeyInfo key)
        {
            if ((key.Mods & KeyMod.ALT) != 0 && key.KeyCode == KeyCode.D)
            {
                if (m_debugOverlay != null)
                {
                    Host.RemoveOverlay(m_debugOverlay);
                    m_debugOverlay = null;
                }
                else
                {
                    m_debugOverlay = new GameDebugOverlay(m_resources);
                    Host.AddOverlay(m_debugOverlay);
                }
                return true;
            }

            switch (key.KeyCode)
            {
                case KeyCode.PAGEUP:
                {
                    m_audioController.Position += m_chart.ControlPoints.MostRecent(m_audioController.Position).MeasureDuration;
                } break;

                case KeyCode.ESCAPE:
                {
                    Host.PopToParent(this);
                } break;

                // TODO(local): consume whatever the controller does
                default: return false;
            }

            return true;
        }

        void UserInput_BtPress(int lane)
        {
            if (AutoButtons) return;

            var result = (m_judge[lane] as ButtonJudge).UserPressed(m_judge.Position);
            if (result == null)
                m_highwayView.CreateKeyBeam(lane, Vector3.One);
            else m_debugOverlay?.AddTimingInfo(result.Value.Difference, result.Value.Kind);
            //else CreateKeyBeam(streamIndex, result.Value.Kind, result.Value.Difference < 0.0);
        }

        void UserInput_BtRelease(int lane)
        {
            if (AutoButtons) return;

            (m_judge[lane] as ButtonJudge).UserReleased(m_judge.Position);
        }

        void UserInput_VolPulse(int lane, float amount)
        {
            if (AutoLasers) return;
            amount *= 0.5f;

            (m_judge[lane + 6] as LaserJudge).UserInput(amount, m_judge.Position);
        }

        private void CreateKeyBeam(int streamIndex, JudgeKind kind, bool isEarly)
        {
            Vector3 color = Vector3.One;

            switch (kind)
            {
                case JudgeKind.Passive:
                case JudgeKind.Perfect: color = new Vector3(1, 1, 0); break;
                case JudgeKind.Critical: color = new Vector3(1, 1, 0); break;
                case JudgeKind.Near: color = isEarly ? new Vector3(1.0f, 0, 1.0f) : new Vector3(0.5f, 1, 0.25f); break;
                case JudgeKind.Bad:
                case JudgeKind.Miss: color = new Vector3(1, 0, 0); break;
            }

            m_highwayView.CreateKeyBeam(streamIndex, color);
        }

        private void SetLuaDynamicData()
        {
            m_scoringTable["CurrentBpm"] = m_chart.ControlPoints.MostRecent(m_audioController.Position).BeatsPerMinute;
            m_scoringTable["CurrentHiSpeed"] = 1.0;

            m_scoringTable["Progress"] = MathL.Clamp01((double)(m_audioController.Position / m_chart.LastObjectTime));
            m_scoringTable["Gauge"] = 0.0;
            m_scoringTable["Score"] = m_judge.Score;
        }

        public override void Update(float delta, float total)
        {
            base.Update(delta, total);

            time_t position = m_audio?.Position ?? 0;
            m_judge.Position = position;
            m_highwayControl.Position = position;
            m_playback.Position = position;

            float GetPathValueLerped(LaneLabel stream)
            {
                var s = m_playback.Chart[stream];

                var mrPoint = s.MostRecent<GraphPointEvent>(position);
                if (mrPoint == null)
                    return ((GraphPointEvent)s.First)?.Value ?? 0;

                if (mrPoint.HasNext)
                {
                    float alpha = (float)((position - mrPoint.AbsolutePosition).Seconds / (mrPoint.Next.AbsolutePosition - mrPoint.AbsolutePosition).Seconds);
                    return MathL.Lerp(mrPoint.Value, ((GraphPointEvent)mrPoint.Next).Value, alpha);
                }
                else return mrPoint.Value;
            }

            for (int i = 0; i < m_queuedSlams.Count;)
            {
                time_t slam = m_queuedSlams[i];
                if (slam < position)
                {
                    m_queuedSlams.RemoveAt(i);
                    m_slamSample.Play();

                    for (int e = 0; e < m_queuedSlamTiedEvents.Count;)
                    {
                        var evt = m_queuedSlamTiedEvents[e];
                        if (evt.AbsolutePosition < position)
                        {
                            switch (evt)
                            {
                                case SpinImpulseEvent spin: m_highwayControl.ApplySpin(spin.Params, spin.AbsolutePosition); break;
                                case SwingImpulseEvent swing: m_highwayControl.ApplySwing(swing.Params, swing.AbsolutePosition); break;
                                case WobbleImpulseEvent wobble: m_highwayControl.ApplyWobble(wobble.Params, wobble.AbsolutePosition); break;

                                default: e++; continue;
                            }

                            m_queuedSlamTiedEvents.RemoveAt(e);
                        }
                        else e++;
                    }
                }
                else i++;
            }

            m_highwayControl.MeasureDuration = m_chart.ControlPoints.MostRecent(position).MeasureDuration;

            float leftLaserValue = GetTempRollValue(position, 6, out float _);
            float rightLaserValue = GetTempRollValue(position, 7, out float _, true);

            m_highwayControl.LeftLaserInput = leftLaserValue;
            m_highwayControl.RightLaserInput = rightLaserValue;

            m_highwayControl.Zoom = GetPathValueLerped(NscLane.CameraZoom);
            m_highwayControl.Pitch = GetPathValueLerped(NscLane.CameraPitch);
            m_highwayControl.Offset = GetPathValueLerped(NscLane.CameraOffset);
            m_highwayControl.Roll = GetPathValueLerped(NscLane.CameraTilt);

            m_highwayView.PlaybackPosition = position;

            for (int i = 0; i < 8; i++)
            {
                var judge = m_judge[i];
                m_streamHasActiveEffects[i] = judge.IsBeingPlayed;
            }

            for (int i = 0; i < 8; i++)
            {
                bool active = m_streamHasActiveEffects[i] && m_activeObjects[i] != null;
                if (i == 6)
                    active |= m_streamHasActiveEffects[i + 1] && m_activeObjects[i + 1] != null;
                m_audioController.SetEffectActive(i, active);
            }

            UpdateEffects();
            m_audioController.EffectsActive = true;

            m_highwayControl.Update(Time.Delta);
            m_highwayControl.ApplyToView(m_highwayView);

            for (int i = 0; i < 8; i++)
            {
                var obj = m_activeObjects[i];

                m_highwayView.SetStreamActive(i, m_streamHasActiveEffects[i]);
                m_debugOverlay?.SetStreamActive(i, m_streamHasActiveEffects[i]);

                if (obj == null) continue;

                float glow = -0.5f;
                int glowState = 0;

                if (m_streamHasActiveEffects[i])
                {
                    glow = MathL.Cos(10 * MathL.TwoPi * (float)position) * 0.35f;
                    glowState = 2 + MathL.FloorToInt(position.Seconds * 20) % 2;
                }

                m_highwayView.SetObjectGlow(obj, glow, glowState);
            }
            m_highwayView.Update();

            {
                var camera = m_highwayView.Camera;

                var defaultTransform = m_highwayView.DefaultTransform;
                var defaultZoomTransform = m_highwayView.DefaultZoomedTransform;
                var totalWorldTransform = m_highwayView.WorldTransform;
                var critLineTransform = m_highwayView.CritLineTransform;

                Vector2 comboLeft = camera.Project(defaultTransform, new Vector3(-0.8f / 6, 0, 0));
                Vector2 comboRight = camera.Project(defaultTransform, new Vector3(0.8f / 6, 0, 0));

                m_comboDisplay.DigitSize = (comboRight.X - comboLeft.X) / 4;

                Vector2 critRootPosition = camera.Project(critLineTransform, Vector3.Zero);
                Vector2 critRootPositionWest = camera.Project(critLineTransform, new Vector3(-1, 0, 0));
                Vector2 critRootPositionEast = camera.Project(critLineTransform, new Vector3(1, 0, 0));
                Vector2 critRootPositionForward = camera.Project(critLineTransform, new Vector3(0, 0, -1));

                for (int i = 0; i < 2; i++)
                {
                    if (m_cursorsActive[i])
                        m_cursorAlphas[i] = MathL.Min(1, m_cursorAlphas[i] + delta * 3);
                    else m_cursorAlphas[i] = MathL.Max(0, m_cursorAlphas[i] - delta * 5);
                }

                void GetCursorPosition(int lane, out float pos, out float range)
                {
                    var judge = (LaserJudge)m_judge[lane + 6];
                    pos = judge.CursorPosition;
                    range = judge.LaserRange;
                }

                float GetCursorPositionWorld(float xWorld)
                {
                    var critRootCenter = camera.Project(defaultZoomTransform, Vector3.Zero);
                    var critRootCursor = camera.Project(defaultZoomTransform, new Vector3(xWorld, 0, 0));
                    return critRootCursor.X - critRootCenter.X;
                }

                GetCursorPosition(0, out float leftLaserPos, out float leftLaserRange);
                GetCursorPosition(1, out float rightLaserPos, out float rightLaserRange);

                m_critRoot.LeftCursorPosition = GetCursorPositionWorld((leftLaserPos - 0.5f) * 5.0f / 6 * leftLaserRange);
                m_critRoot.LeftCursorAlpha = m_cursorAlphas[0];

                m_critRoot.RightCursorPosition = GetCursorPositionWorld((rightLaserPos - 0.5f) * 5.0f / 6 * rightLaserRange);
                m_critRoot.RightCursorAlpha = m_cursorAlphas[1];

                Vector2 critRotationVector = critRootPositionEast - critRootPositionWest;
                float critRootRotation = MathL.Atan(critRotationVector.Y, critRotationVector.X);

                //m_critRoot.Roll = m_highwayView.LaserRoll;
                //m_critRoot.EffectRoll = m_highwayControl.EffectRoll;
                //m_critRoot.EffectOffset = m_highwayControl.EffectOffset;
                m_critRoot.Position = critRootPosition;
                m_critRoot.Rotation = MathL.ToDegrees(critRootRotation) + m_highwayControl.CritLineEffectRoll * 25;
            }

            m_background.HorizonHeight = m_highwayView.HorizonHeight;
            m_background.CombinedTilt = m_highwayControl.LaserRoll + m_highwayControl.Roll * 360;
            m_background.EffectRotation = m_highwayControl.EffectRoll * 360;
            m_background.SpinTimer = m_highwayControl.SpinTimer;
            m_background.SwingTimer = m_highwayControl.SwingTimer;
            m_background.Update(delta, total);

            SetLuaDynamicData();
            m_guiScript.Update(delta, total);
        }

        private void UpdateEffects()
        {
            UpdateLaserEffects();
        }

        private EffectDef currentLaserEffectDef = BiQuadFilterDef.CreateDefaultPeak();
        private readonly bool[] currentActiveLasers = new bool[2];
        private readonly float[] currentActiveLaserAlphas = new float[2];

        private bool AreLasersActive => currentActiveLasers[0] || currentActiveLasers[1];

        private const float BASE_LASER_MIX = 0.8f;
        private float laserGain = 0.5f;

        private float GetTempRollValue(time_t position, LaneLabel label, out float valueMult, bool oneMinus = false)
        {
            var s = m_playback.Chart[label];
            valueMult = 1.0f;

            var mrAnalog = s.MostRecent<AnalogEntity>(position);
            if (mrAnalog == null || position > mrAnalog.AbsoluteEndPosition)
                return 0;

            if (mrAnalog.RangeExtended)
                valueMult = 2.0f;
            float result = mrAnalog.SampleValue(position);
            if (oneMinus)
                return 1 - result;
            else return result;
        }

        private void UpdateLaserEffects()
        {
            if (!AreLasersActive)
            {
                m_audioController.SetEffectMix(6, 0);
                return;
            }

            float LaserAlpha(int index)
            {
                return GetTempRollValue(m_audio.Position, index + 6, out float _, index == 1);
            }

            if (currentActiveLasers[0])
                currentActiveLaserAlphas[0] = LaserAlpha(0);
            if (currentActiveLasers[1])
                currentActiveLaserAlphas[1] = LaserAlpha(1);

            float alpha;
            if (currentActiveLasers[0] && currentActiveLasers[1])
                alpha = Math.Max(currentActiveLaserAlphas[0], currentActiveLaserAlphas[1]);
            else if (currentActiveLasers[0])
                alpha = currentActiveLaserAlphas[0];
            else alpha = currentActiveLaserAlphas[1];

            m_audioController.UpdateEffect(6, CurrentQuarterNodeDuration, alpha);

            float mix = laserGain;
            if (currentLaserEffectDef != null)
            {
                if (currentLaserEffectDef is BiQuadFilterDef bqf && bqf.FilterType == FilterType.Peak)
                {
                    mix *= BASE_LASER_MIX;
                    if (alpha < 0.2f)
                        mix *= alpha / 0.2f;
                    else if (alpha > 0.8f)
                        mix *= 1 - (alpha - 0.8f) / 0.2f;
                }
                else
                {
                    // TODO(local): a lot of these (all?) don't need to have special mixes. idk why these got here but they're needed for some reason? fix
                    switch (currentLaserEffectDef)
                    {
                        case BitCrusherDef _:
                            mix *= currentLaserEffectDef.Mix.Sample(alpha);
                            break;

                        case GateDef _:
                        case RetriggerDef _:
                        case TapeStopDef _:
                            mix = currentLaserEffectDef.Mix.Sample(alpha);
                            break;

                        case BiQuadFilterDef _: break;
                    }
                }
            }

            m_audioController.SetEffectMix(6, mix);
        }

        public override void Render()
        {
            m_background.Render();
            m_highwayView.Render();
        }

        public override void LateRender()
        {
            m_guiScript.Draw();
        }
    }
}
