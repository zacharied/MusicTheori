﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenRM.Audio.Effects;
using OpenRM.Voltex;

namespace OpenRM.Convert
{
    public static class ChartExt_Ksh2Voltex
    {
        class TempButtonState
        {
            public tick_t StartPosition;

            public EffectDef EffectDef;

            public byte SampleIndex = 0xFF;
            public bool UsingSample = false;

            public TempButtonState(tick_t pos)
            {
                StartPosition = pos;
            }
        }

        class TempLaserState
        {
            public ControlPoint ControlPoint;

            public tick_t StartPosition;
            public float StartAlpha;
            
            public EffectDef EffectDef;

            public TempLaserState(tick_t pos, ControlPoint cp)
            {
                StartPosition = pos;
                ControlPoint = cp;
            }
        }

        public static Chart ToVoltex(this KShootMania.Chart ksh)
        {
            var voltex = new Chart(StreamIndex.COUNT)
            {
                Offset = ksh.Metadata.OffsetMillis / 1_000.0
            };

            {
                if (double.TryParse(ksh.Metadata.BeatsPerMinute, out double bpm))
                    voltex.ControlPoints.Root.BeatsPerMinute = bpm;
                
                var laserParams = voltex[StreamIndex.LaserParams].Add<LaserParamsEvent>(0);
                laserParams.LaserIndex = LaserIndex.Both;

                var laserGain = voltex[StreamIndex.LaserFilterGain].Add<LaserFilterGainEvent>(0);
                laserGain.LaserIndex = LaserIndex.Both;
                laserGain.Gain = ksh.Metadata.PFilterGain / 100.0f;
                
                var laserFilter = voltex[StreamIndex.LaserFilterKind].Add<LaserFilterKindEvent>(0);
                laserFilter.LaserIndex = LaserIndex.Both;
                laserFilter.FilterEffect = ParseFilterType(ksh.Metadata.FilterType);

                var slamVoume = voltex[StreamIndex.SlamVolume].Add<SlamVolumeEvent>(0);
                slamVoume.Volume = ksh.Metadata.SlamVolume / 100.0f;
            }

            var lastCp = voltex.ControlPoints.Root;
            int lastTsBlock = 0;

            var buttonStates = new TempButtonState[6];
            var laserStates = new TempLaserState[2];

            var currentFx = new EffectDef[6];
            bool[] laserIsExtended = new bool[2] { false, false };
            
            EffectDef ParseFilterType(string str)
            {
                switch (str)
                {
                    case "hpf1":
                    case "HighPass": return EffectDef.GetDefault(EffectType.HighPassFilter);
                        
                    case "lpf1":
                    case "LowPass": return EffectDef.GetDefault(EffectType.LowPassFilter);
                        
                    case "peak":
                    case "Peaking": return EffectDef.GetDefault(EffectType.PeakingFilter);

                    case "bitc": case "fx;bitc":
                    case "BitCrusher": return EffectDef.GetDefault(EffectType.BitCrush);
                        
                    default:
                    {
                        Console.WriteLine($"Unrecognized filter type { str }");
                        if (ksh.FilterDefines.TryGetValue(str, out var def))
                        {
                            Console.WriteLine("Temporarily ignoring effect defines");
                        }
                        return null;
                    }
                }
            }
            
            EffectDef CreateFx(string fx)
            {
                if (!ksh.FxDefines.ContainsKey(fx))
                    return null;

                var def = ksh.FxDefines[fx];
                switch (def.EffectName)
                {
                    case "Retrigger": return new RetriggerEffectDef(1, 0.7f,
                        (float)lastCp.QuarterNoteDuration.Seconds * 4 * def["waveLength"].Number);

                    case "Gate": return new GateEffectDef(1, 0.7f,
                        (float)lastCp.QuarterNoteDuration.Seconds * 4 * def["waveLength"].Number);

                    case "BitCrusher": return new BitCrusherEffectDef(1, def["reduction"].Number);

                    case "SideChain": return new SideChainEffectDef(1, 1.0f,
                        (float)lastCp.QuarterNoteDuration.Seconds * 4 * def["period"].Number);

                    case "Wobble": return new WobbleEffectDef(1,
                        (float)lastCp.QuarterNoteDuration.Seconds * 4 * def["waveLength"].Number);

                    case "TapeStop": return new TapeStopEffectDef(1, 16.0f / MathL.Max(def["speed"].Number, 1));

                    case "Flanger": return new FlangerEffectDef(1.0f);

                    case "Phaser": return new PhaserEffectDef(0.5f);

                    default: return null;
                }
            }

            foreach (var tickRef in ksh)
            {
                var tick = tickRef.Tick;
                
                int blockOffset = tickRef.Block;
                tick_t chartPos = blockOffset + (double)tickRef.Index / tickRef.MaxIndex;

                //System.Diagnostics.Trace.WriteLine(chartPos);

                // TODO(local): actually worry about param storage
                foreach (var setting in tick.Settings)
                {
                    string key = setting.Key;
                    switch (key)
                    {
                        // TODO(local): provide a log hook?
                        // TODO(local): check parsing as well
                        case "beat":
                        {
                            if (!setting.Value.ToString().TrySplit('/', out string n, out string d))
                            {
                                n = d = "4";
                                Console.WriteLine($"Chart Error: { setting.Value } is not a valid time signature.");
                            }

                            tick_t pos = MathL.Ceil((double)chartPos);
                            ControlPoint cp = voltex.ControlPoints.GetOrCreate(pos, true);
                            cp.BeatCount = int.Parse(n);
                            cp.BeatKind = int.Parse(d);
                            lastCp = cp;
                        } break;

                        case "t":
                        {
                            tick_t pos = MathL.Ceil((double)chartPos);
                            ControlPoint cp = voltex.ControlPoints.GetOrCreate(pos, true);
                            cp.BeatsPerMinute = double.Parse(setting.Value.ToString());
                            lastCp = cp;
                        } break;

                        //case "fx-l": currentFx[4] = ParseFxAndParams(setting.Value.ToString()); break;
                        //case "fx-r": currentFx[5] = ParseFxAndParams(setting.Value.ToString()); break;

                        case "fx-l": currentFx[4] = CreateFx(setting.Value.ToString()); break;
                        case "fx-r": currentFx[5] = CreateFx(setting.Value.ToString()); break;

                        case "fx-l_param1":
                        {
                        } break;

                        case "fx-r_param1":
                        {
                        } break;

                        case "pfiltergain":
                        {
                            var laserGain = voltex[StreamIndex.LaserFilterGain].Add<LaserFilterGainEvent>(chartPos);
                            laserGain.LaserIndex = LaserIndex.Both;
                            laserGain.Gain = setting.Value.ToInt() / 100.0f;
                        } break;

                        case "filtertype":
                        {
                            var laserFilter = voltex[StreamIndex.LaserFilterKind].Add<LaserFilterKindEvent>(chartPos);
                            laserFilter.LaserIndex = LaserIndex.Both;
                            laserFilter.FilterEffect = ParseFilterType(setting.Value.ToString());
                        } break;

                        case "chokkakuvol":
                        {
                            var slamVoume = voltex[StreamIndex.SlamVolume].Add<SlamVolumeEvent>(chartPos);
                            slamVoume.Volume = setting.Value.ToInt() / 100.0f;
                        } break;

                        case "laserrange_l": { laserIsExtended[0] = true; } break;
                        case "laserrange_r": { laserIsExtended[1] = true; } break;
                        
                        case "zoom_bottom":
                        {
                            var point = voltex[StreamIndex.Zoom].Add<PathPointEvent>(chartPos);
                            point.Value = setting.Value.ToInt() / 100.0f;
                            //System.Diagnostics.Trace.WriteLine($"ZOOM_BOTTOM @ { chartPos }: { setting.Value } -> { point.Value }");
                        } break;
                        
                        case "zoom_top":
                        {
                            var point = voltex[StreamIndex.Pitch].Add<PathPointEvent>(chartPos);
                            point.Value = setting.Value.ToInt() / 100.0f;
                        } break;
                        
                        case "zoom_side":
                        {
                            var point = voltex[StreamIndex.Offset].Add<PathPointEvent>(chartPos);
                            point.Value = setting.Value.ToInt() / 100.0f;
                        } break;
                        
                        case "roll":
                        {
                            var point = voltex[StreamIndex.Roll].Add<PathPointEvent>(chartPos);
                            point.Value = setting.Value.ToInt() / 360.0f;
                        } break;

                        case "tilt":
                        {
                            var laserApps = voltex[StreamIndex.LaserParams].Add<LaserApplicationEvent>(chartPos);

                            string v = setting.Value.ToString();
                            if (v.StartsWith("keep_"))
                            {
                                laserApps.Application = LaserApplication.Additive | LaserApplication.KeepMax;
                                v = v.Substring(5);
                            }
                            
                            var laserParams = voltex[StreamIndex.LaserParams].Add<LaserParamsEvent>(chartPos);
                            laserParams.LaserIndex = LaserIndex.Both;

                            switch (v)
                            {
                                default:
                                case "zero": laserParams.Params.Function = LaserFunction.Zero; break;
                                case "normal": laserParams.Params.Scale = LaserScale.Normal; break;
                                case "bigger": laserParams.Params.Scale = LaserScale.Bigger; break;
                                case "biggest": laserParams.Params.Scale = LaserScale.Biggest; break;
                            }
                        } break;

                        case "fx_sample":
                        {
                        } break;

                        case "stop":
                        {
                        } break;

                        case "lane_toggle":
                        {
                        } break;
                    }
                }

                for (int b = 0; b < 6; b++)
                {
                    bool isFx = b >= 4;
                    
                    var data = isFx ? tick.Fx[b - 4] : tick.Bt[b];
                    var fxKind = data.FxKind;

                    void CreateHold(tick_t endPos)
                    {
                        var state = buttonStates[b];

                        var startPos = state.StartPosition;
                        var button = voltex[b].Add<ButtonObject>(startPos, endPos - startPos);
                        //System.Diagnostics.Trace.WriteLine($"{ endPos } - { startPos } = { endPos - startPos }");
                        button.Effect = state.EffectDef;
                    }

                    switch (data.State)
                    {
                        case KShootMania.ButtonState.Off:
                        {
                            if (buttonStates[b] != null)
                                CreateHold(chartPos);
                            buttonStates[b] = null;
                        } break;

                        case KShootMania.ButtonState.Chip:
                        case KShootMania.ButtonState.ChipSample:
                        {
                            //System.Diagnostics.Trace.WriteLine(b);
                            voltex[b].Add<ButtonObject>(chartPos);
                        } break;
                        
                        case KShootMania.ButtonState.Hold:
                        {
                            if (buttonStates[b] == null)
                            {
                                buttonStates[b] = new TempButtonState(chartPos)
                                {
                                    EffectDef = currentFx[b],
                                };
                            }
                        } break;
                    }
                }

                for (int l = 0; l < 2; l++)
                {
                    var data = tick.Laser[l];
                    var state = data.State;

                    tick_t CreateSegment(tick_t endPos, float endAlpha)
                    {
                        var startPos = laserStates[l].StartPosition;
                        float startAlpha = laserStates[l].StartAlpha;

                        var duration = endPos - startPos;
                        if (duration <= tick_t.FromFraction(1, 32))
                            duration = 0;

                        var analog = voltex[l + 6].Add<AnalogObject>(startPos, duration);
                        //System.Diagnostics.Trace.WriteLine($"{ startPos } -> { endPos } ({ duration }) :: { startAlpha }, { endAlpha }");
                        analog.InitialValue = startAlpha;
                        analog.FinalValue = endAlpha;
                        analog.RangeExtended = laserIsExtended[l];

                        return startPos + duration;
                    }

                    switch (state)
                    {
                        case KShootMania.LaserState.Inactive:
                        {
                            if (laserStates[l] != null)
                            {
                                laserStates[l] = null;
                                laserIsExtended[l] = false;
                            }
                        } break;

                        case KShootMania.LaserState.Lerp:
                        {
                        } break;
                        
                        case KShootMania.LaserState.Position:
                        {
                            var alpha = data.Position;
                            var startPos = chartPos;

                            if (laserStates[l] != null)
                                startPos = CreateSegment(chartPos, alpha.Alpha);

                            laserStates[l] = new TempLaserState(startPos, lastCp)
                            {
                                StartAlpha = alpha.Alpha,
                            };
                        } break;
                    }
                }

                switch (tick.Add.Kind)
                {
                    case KShootMania.AddKind.None: break;

                    case KShootMania.AddKind.Spin:
                    {
                        tick_t duration = tick_t.FromFraction(tick.Add.Duration * 2, 192);
                        var spin = voltex[StreamIndex.HighwayEffect].Add<SpinImpulseEvent>(chartPos, duration);
                        spin.Direction = (AngularDirection)tick.Add.Direction;
                    } break;

                    case KShootMania.AddKind.Swing:
                    {
                        tick_t duration = tick_t.FromFraction(tick.Add.Duration * 2, 192);
                        var swing = voltex[StreamIndex.HighwayEffect].Add<SwingImpulseEvent>(chartPos, duration);
                        swing.Direction = (AngularDirection)tick.Add.Direction;
                        swing.Amplitude = tick.Add.Amplitude * 70 / 100.0f;
                    } break;

                    case KShootMania.AddKind.Wobble:
                    {
                        tick_t duration = tick_t.FromFraction(tick.Add.Duration, 192);
                        var wobble = voltex[StreamIndex.HighwayEffect].Add<WobbleImpulseEvent>(chartPos, duration);
                        wobble.Direction = (LinearDirection)tick.Add.Direction;
                        wobble.Amplitude = tick.Add.Amplitude / 250.0f;
                        wobble.Decay = (Decay)tick.Add.Decay;
                        wobble.Frequency = tick.Add.Frequency;
                    } break;
                }
            }

            return voltex;
        }
    }
}
