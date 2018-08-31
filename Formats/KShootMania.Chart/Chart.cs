﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace KShootMania
{
    public class Block
    {
        public readonly List<Tick> Ticks = new List<Tick>();

        public int TickCount => Ticks.Count;
        public Tick this[int index] => Ticks[index];
    }

    public class Tick
    {
        public readonly List<TickSetting> Settings = new List<TickSetting>();
        
        public readonly ButtonData[] Bt = new ButtonData[4];
        public readonly ButtonData[] Fx = new ButtonData[2];
        public readonly LaserData[] Laser = new LaserData[2];

        public AddData Add;
    }

    public struct TickSetting
    {
        public string Key;
        public Variant Value;

        public TickSetting(string key, Variant value)
        {
            Key = key;
            Value = value;
        }
    }

    public enum ButtonState
    {
        Off, Chip, Hold, ChipSample,
    }
    
    public enum LaserState
    {
        Inactive, Lerp, Position,
    }

    public struct ButtonData
    {
        public ButtonState State;
        public FxKind FxKind;
    }

    public enum FxKind
    {
        None = 0,

        BitCrush = 'B',
        
        Gate4  = 'G',
        Gate8  = 'H',
        Gate16 = 'I',
        Gate32 = 'J',
        Gate12 = 'K',
        Gate24 = 'L',
        
        Retrigger8  = 'S',
        Retrigger16 = 'T',
        Retrigger32 = 'U',
        Retrigger12 = 'V',
        Retrigger24 = 'W',

        Phaser = 'Q',
        Flanger = 'F',
        Wobble = 'X',
        SideChain = 'D',
        TapeStop = 'A',
    }

    public struct LaserData
    {
        public LaserState State;
        public LaserPosition Position;
    }

    public struct LaserPosition
    {
        public const int Resolution = 51;

        class Chars : Dictionary<char, int>
        {
            public int NumChars;

            public Chars()
            {
                void AddRange(char start, char end)
                {
                    for (char c = start; c <= end; c++)
                        this[c] = NumChars++;
                }

			    AddRange('0', '9');
			    AddRange('A', 'Z');
			    AddRange('a', 'o');

                Debug.Assert(NumChars == Resolution);
            }
        }

        static Chars chars = new Chars();

        public float Alpha
        {
            get => value / (float)(Resolution - 1);
            set => Value = (int)Math.Round(value * (Resolution - 1));
        }

        private int value;
        public int Value
        {
            get => value;
            set => this.value = MathL.Clamp(value, 0, chars.NumChars - 1);
        }

        public char Image
        {
            get
            {
                int v = Value;
                return chars.Where(kvp => kvp.Value == v).Single().Key;
            }

            set => chars.TryGetValue(value, out this.value);
        }

        public LaserPosition(int value)
        {
            this.value = MathL.Clamp(value, 0, chars.NumChars - 1);
        }

        public LaserPosition(char image)
        {
            chars.TryGetValue(image, out value);
        }
    }

    public enum AddKind
    {
        None, Spin, Swing, Wobble
    }

    public struct AddData
    {
        public AddKind Kind;
        public int Direction;
        public int Duration;
        public int Amplitude;
        public int Frequency;
        public int Decay;
    }

    public struct TickRef
    {
        public int Block, Index, MaxIndex;
        public Tick Tick;
    }

    public class EffectDefinition
    {
        public string Name;
        public readonly Dictionary<string, string> Parameters = new Dictionary<string, string>();

        public EffectDefinition(string name)
        {
            Name = name;
        }
    }

    /// <summary>
    /// Contains all relevant data for a single chart.
    /// </summary>
    public sealed class Chart : IEnumerable<TickRef>
    {
        internal const string SEP = "--";

        public static Chart CreateFromFile(string fileName)
        {
            using (var reader = File.OpenText(fileName))
                return Create(reader);
        }

        public static Chart Create(StreamReader reader)
        {
            var chart = new Chart
            {
                Metadata = ChartMetadata.Create(reader)
            };

            var block = new Block();
            var tick = new Tick();

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrEmpty(line))
                    continue;
                line = line.Trim();

                if (line[0] == '#')
                {
                    if (!line.TrySplit(' ', out string defKind, out string fxType, out string args))
                        continue;

                    var def = new EffectDefinition(fxType);
                    foreach (string a in args.Split(';'))
                    {
                        if (!a.TrySplit('=', out string k, out string v))
                            continue;
                        def.Parameters[k] = v;
                    }
                    
                    if (defKind == "#define_fx")
                        chart.FxDefines[fxType] = def;
                    else if (defKind == "#define_filter")
                        chart.FilterDefines[fxType] = def;
                }
                if (line.TrySplit('=', out string key, out string value))
                    tick.Settings.Add(new TickSetting(key, value));
                if (line == SEP)
                {
                    chart.m_blocks.Add(block);
                    block = new Block();
                }
                else
                {
                    if (!line.TrySplit('|', out string bt, out string fx, out string vol))
                        continue;

                    if (vol.Length > 2)
                    {
                        string add = vol.Substring(2);
                        vol = vol.Substring(0, 2);

                        if (add.Length >= 2)
                        {
                            string[] args = add.Substring(2).Split(';');

                            char c = add[0];
                            switch (c)
                            {
                                case '@':
                                {
                                    char d = add[1];
                                    switch (d)
                                    {
                                        case '(': case ')': tick.Add.Kind = AddKind.Spin; break;
                                        case '<': case '>': tick.Add.Kind = AddKind.Swing; break;
                                    }
                                    switch (d)
                                    {
                                        case '(': case '<': tick.Add.Direction = -1; break;
                                        case ')': case '>': tick.Add.Direction =  1; break;
                                    }
                                    ParseArg(0, out tick.Add.Duration);
                                    tick.Add.Amplitude = 100;
                                } break;
                                
                                case 'S':
                                {
                                    char d = add[1];
                                    tick.Add.Kind = AddKind.Wobble;
                                    tick.Add.Direction = d == '<' ? -1 : (d == '>' ? 1 : 0);
                                    ParseArg(0, out tick.Add.Duration);
                                    ParseArg(1, out tick.Add.Amplitude);
                                    ParseArg(2, out tick.Add.Frequency);
                                    ParseArg(3, out tick.Add.Decay);
                                } break;
                            }

                            void ParseArg(int i, out int v)
                            {
                                if (args.Length > i) int.TryParse(args[i], out v);
                                else v = 0;
                            }
                        }
                    }

                    for (int i = 0; i < MathL.Min(4, bt.Length); i++)
                    {
                        char c = bt[i];
                        switch (c)
                        {
                            case '0': tick.Bt[i].State = ButtonState.Off; break;
                            case '1': tick.Bt[i].State = ButtonState.Chip; break;
                            case '2': tick.Bt[i].State = ButtonState.Hold; break;
                        }
                    }

                    for (int i = 0; i < MathL.Min(2, fx.Length); i++)
                    {
                        char c = fx[i];
                        switch (c)
                        {
                            case '0': tick.Fx[i].State = ButtonState.Off; break;
                            case '1': tick.Fx[i].State = ButtonState.Hold; break;
                            case '2': tick.Fx[i].State = ButtonState.Chip; break;
                            case '3': tick.Fx[i].State = ButtonState.ChipSample; break;
                                
                            default:
                            {
                                var kind = (FxKind)c;
                                if (Enum.IsDefined(typeof(FxKind), kind) && kind != FxKind.None)
                                {
                                    tick.Fx[i].State = ButtonState.Hold;
                                    tick.Fx[i].FxKind = kind;
                                }
                            } break;
                        }
                    }

                    for (int i = 0; i < MathL.Min(2, vol.Length); i++)
                    {
                        char c = vol[i];
                        switch (c)
                        {
                            case '-': tick.Laser[i].State = LaserState.Inactive; break;
                            case ':': tick.Laser[i].State = LaserState.Lerp; break;
                            default:
                            {
                                tick.Laser[i].State = LaserState.Position;
                                tick.Laser[i].Position.Image = c;
                            } break;
                        }
                    }

                    block.Ticks.Add(tick);
                    tick = new Tick();
                }
            }

            return chart;
        }

        public ChartMetadata Metadata;

        private List<Block> m_blocks = new List<Block>();
        public Tick this[int block, int tick] => m_blocks[block][tick];

        public readonly Dictionary<string, EffectDefinition> FxDefines = new Dictionary<string, EffectDefinition>();
        public readonly Dictionary<string, EffectDefinition> FilterDefines = new Dictionary<string, EffectDefinition>();
        
        public int BlockCount => m_blocks.Count;

        IEnumerator<TickRef> IEnumerable<TickRef>.GetEnumerator() => new TickEnumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<TickRef>)this).GetEnumerator();

        class TickEnumerator : IEnumerator<TickRef>
        {
            private Chart m_chart;
            private int m_block, m_tick = -1;
            
            object IEnumerator.Current => Current;
            public TickRef Current => new TickRef()
            {
                Block = m_block,
                Index = m_tick,
                MaxIndex = m_chart.m_blocks[m_block].TickCount,
                Tick = m_chart[m_block, m_tick],
            };

            public TickEnumerator(Chart c)
            {
                m_chart = c;
            }

            public void Dispose() => m_chart = null;

            public bool MoveNext()
            {
                if (m_tick == m_chart.m_blocks[m_block].TickCount - 1)
                {
                    m_block++;
                    m_tick = 0;

                    return m_block < m_chart.m_blocks.Count;
                }
                else m_tick++;

                return true;
            }

            public void Reset()
            {
                m_block = 0;
                m_tick = 0;
            }
        }
    }
}