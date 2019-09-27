﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kiva_MIDI
{
    [StructLayout(LayoutKind.Sequential)]
    public struct NoteCol
    {
        public float r, g, b, a;
        public float r2, g2, b2, a2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Note
    {
        public double start, end;
        public int colorPointer;
        public int skip;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIDIEvent
    {
        public double time;
        public uint data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ColorEvent
    {
        public double time;
        public NoteCol color;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TempoEvent
    {
        public long time;
        public int tempo;
    }

    public enum ParsingStage
    {
        Opening,
        FirstPass,
        SecondPass,
        MergingKeys,
        MergingEvents,
    }

    public class MIDIFile
    {
        List<long> trackBeginnings = new List<long>();
        List<uint> trackLengths = new List<uint>();

        MIDITrackParser[] parsers;
        TempoEvent[] globalTempos;

        public event Action ParseFinished;
        public event Action ParseCancelled;

        //Persistent values
        public MIDIEvent[][] MIDIEvents { get; private set; } = null;
        public Note[][] Notes { get; private set; } = new Note[256][];
        public int[] FirstRenderNote { get; private set; } = new int[256];
        public double lastRenderTime { get; set; } = 0;
        public NoteCol[] OriginalMidiNoteColors { get; private set; } = null;
        public NoteCol[] MidiNoteColors { get; private set; } = null;
        public int[] LastColorEvent;
        public ColorEvent[][] ColorEvents;
        public double MidiLength { get; private set; } = 0;
        public int FirstKey { get; private set; } = 0;
        public int LastKey { get; private set; } = 255;

        public ParsingStage ParseStage { get; private set; } = ParsingStage.Opening;
        public double ParseNumber { get; private set; }
        public string ParseStatusText { get; private set; }

        public ushort division { get; private set; }
        public int trackcount { get; private set; }
        public ushort format { get; private set; }

        Stream MidiFileReader;
        MIDILoaderSettings loaderSettings;

        string filepath;
        CancellationToken cancel;

        public MIDIFile(string path, MIDILoaderSettings settings, CancellationToken cancel)
        {
            loaderSettings = settings;
            this.cancel = cancel;
            filepath = path;
        }

        void AssertText(string text)
        {
            foreach (char c in text)
            {
                if (MidiFileReader.ReadByte() != c)
                {
                    throw new Exception("Corrupt chunk headers");
                }
            }
        }

        uint ReadInt32()
        {
            uint length = 0;
            for (int i = 0; i != 4; i++)
                length = (uint)((length << 8) | (byte)MidiFileReader.ReadByte());
            return length;
        }

        ushort ReadInt16()
        {
            ushort length = 0;
            for (int i = 0; i != 2; i++)
                length = (ushort)((length << 8) | (byte)MidiFileReader.ReadByte());
            return length;
        }

        void ParseHeaderChunk()
        {
            AssertText("MThd");
            uint length = ReadInt32();
            if (length != 6) throw new Exception("Header chunk size isn't 6");
            format = ReadInt16();
            ReadInt16();
            division = ReadInt16();
            if (format == 2) throw new Exception("Midi type 2 not supported");
            if (division < 0) throw new Exception("Division < 0 not supported");
        }

        void ParseTrackChunk()
        {
            AssertText("MTrk");
            uint length = ReadInt32();
            trackBeginnings.Add(MidiFileReader.Position);
            trackLengths.Add(length);
            MidiFileReader.Position += length;
            trackcount++;
            ParseStatusText = "Checking MIDI\nFound " + trackcount + " tracks";
            Console.WriteLine("Track " + trackcount + ", Size " + length);
        }

        public void Parse()
        {
            try
            {
                Open();
                FirstPassParse();
                foreach (var p in parsers)
                {
                    p.globaTempos = globalTempos;
                    p.PrepareForSecondPass();
                }
                SecondPassParse();
                MidiLength = parsers.Select(p => p.trackSeconds).Max();
                foreach (var p in parsers) p.Dispose();
                parsers = null;
                globalTempos = null;
                trackBeginnings = null;
                trackLengths = null;
                MidiFileReader.Dispose();
                MidiFileReader = null;
                GC.Collect();
                cancel.ThrowIfCancellationRequested();
                SetColors();
                ParseFinished?.Invoke();
            }
            catch (OperationCanceledException)
            {
                MidiFileReader.Close();
                MidiFileReader.Dispose();
                ParseCancelled?.Invoke();
            }
        }

        void Open()
        {
            MidiFileReader = File.Open(filepath, FileMode.Open);
            ParseHeaderChunk();
            while (MidiFileReader.Position < MidiFileReader.Length)
            {
                ParseTrackChunk();
                ParseNumber += 2;
                cancel.ThrowIfCancellationRequested();
            }
            parsers = new MIDITrackParser[trackcount];
        }

        void FirstPassParse()
        {
            object l = new object();
            int tracksParsed = 0;
            ParseStage = ParsingStage.FirstPass;
            Parallel.For(0, parsers.Length, new ParallelOptions() { CancellationToken = cancel }, (i) =>
            {
                var reader = new BufferByteReader(MidiFileReader, 10000, trackBeginnings[i], trackLengths[i]);
                parsers[i] = new MIDITrackParser(reader, division, i, loaderSettings);
                parsers[i].FirstPassParse();
                lock (l)
                {
                    tracksParsed++;
                    ParseNumber += 20;
                    ParseStatusText = "Analyzing MIDI\nTracks " + tracksParsed + " of " + parsers.Length;
                    Console.WriteLine("Pass 1 Parsed track " + tracksParsed + "/" + parsers.Length);
                }
            });
            cancel.ThrowIfCancellationRequested();
            var temposMerge = TimedMerger<TempoEvent>.MergeMany(parsers.Select(p => p.Tempos).ToArray(), t => t.time);
            globalTempos = temposMerge.Cast<TempoEvent>().ToArray();
        }

        void SecondPassParse()
        {
            object l = new object();
            int tracksParsed = 0;
            ParseStage = ParsingStage.SecondPass;
            Parallel.For(0, parsers.Length, (i) =>
            {
                parsers[i].SecondPassParse();
                lock (l)
                {
                    tracksParsed++;
                    ParseNumber += 20;
                    ParseStatusText = "Loading MIDI\nTracks " + tracksParsed + " of " + parsers.Length;
                    Console.WriteLine("Pass 2 Parsed track " + tracksParsed + "/" + parsers.Length);
                }
            });
            cancel.ThrowIfCancellationRequested();
            int keysMerged = 0;
            var eventMerger = Task.Run(() =>
            {
                int count = loaderSettings.EventPlayerThreads;
                MIDIEvents = new MIDIEvent[count][];
                Parallel.For(0, count, new ParallelOptions() { CancellationToken = cancel }, i =>
                {
                    try
                    {
                        MIDIEvents[i] = TimedMerger<MIDIEvent>.MergeMany(parsers.Select(p => new SkipIterator<MIDIEvent>(p.Events, i, count)).ToArray(), e =>
                        {
                            return e.time;
                        }).ToArray();
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });
            });
            cancel.ThrowIfCancellationRequested();
            ParseStage = ParsingStage.MergingKeys;
            Parallel.For(0, 256, new ParallelOptions() { CancellationToken = cancel }, (i) =>
            {
                Notes[i] = TimedMerger<Note>.MergeMany(parsers.Select(p => p.Notes[i]).ToArray(), n => n.start).ToArray();
                lock (l)
                {
                    keysMerged++;
                    ParseNumber += 10;
                    ParseStatusText = "Merging Notes\nMerged keys " + keysMerged + " of " + 256;
                    Console.WriteLine("Merged key " + keysMerged + "/" + 256);
                }
            });
            List<ColorEvent[]> ce = new List<ColorEvent[]>();
            foreach (var p in parsers) ce.AddRange(p.ColorEvents);
            ColorEvents = ce.ToArray();
            cancel.ThrowIfCancellationRequested();
            ParseStatusText = "Merging Events...";
            ParseStage = ParsingStage.MergingEvents;
            Console.WriteLine("Merging events...");
            eventMerger.GetAwaiter().GetResult();
            ParseStatusText = "Done!";
        }

        void SetColors()
        {
            MidiNoteColors = new NoteCol[trackcount * 16];
            OriginalMidiNoteColors = new NoteCol[trackcount * 16];
            LastColorEvent = new int[trackcount * 16];
            for (int i = 0; i < OriginalMidiNoteColors.Length; i++)
            {
                int r, g, b;
                HsvToRgb((i * 40) % 360, 1, 1, out r, out g, out b);
                OriginalMidiNoteColors[i] = new NoteCol()
                {
                    r = r / 255.0f,
                    g = g / 255.0f,
                    b = b / 255.0f,
                    a = 1,
                    r2 = r / 255.0f,
                    g2 = g / 255.0f,
                    b2 = b / 255.0f,
                    a2 = 1
                };
            }
        }

        public void SetColorEvents(double time)
        {
            Parallel.For(0, MidiNoteColors.Length, i => {
                MidiNoteColors[i] = OriginalMidiNoteColors[i];
                var ce = ColorEvents[i];
                var last = LastColorEvent[i];
                if (ce.Length == 0) return;
                if (ce.First().time > time)
                {
                    LastColorEvent[i] = 0;
                    return;
                }
                if ( ce.Last().time <= time)
                {
                    MidiNoteColors[i] = ce.Last().color;
                    return;
                }
                if (ce[last].time < time)
                {
                    for (int j = last; j < ce.Length; j++)
                        if (ce[j + 1].time > time)
                        {
                            LastColorEvent[i] = j;
                            MidiNoteColors[i] = ce[j].color;
                            return;
                        }
                }
                else
                {
                    for (int j = last; j >= 0; j--)
                        if (ce[j].time <= time)
                        {
                            LastColorEvent[i] = j;
                            MidiNoteColors[i] = ce[j].color;
                            return;
                        }
                }
            });
        }

        void HsvToRgb(double h, double S, double V, out int r, out int g, out int b)
        {
            double H = h;
            while (H < 0) { H += 360; };
            while (H >= 360) { H -= 360; };
            double R, G, B;
            if (V <= 0)
            { R = G = B = 0; }
            else if (S <= 0)
            {
                R = G = B = V;
            }
            else
            {
                double hf = H / 60.0;
                int i = (int)Math.Floor(hf);
                double f = hf - i;
                double pv = V * (1 - S);
                double qv = V * (1 - S * f);
                double tv = V * (1 - S * (1 - f));
                switch (i)
                {

                    // Red is the dominant color

                    case 0:
                        R = V;
                        G = tv;
                        B = pv;
                        break;

                    // Green is the dominant color

                    case 1:
                        R = qv;
                        G = V;
                        B = pv;
                        break;
                    case 2:
                        R = pv;
                        G = V;
                        B = tv;
                        break;

                    // Blue is the dominant color

                    case 3:
                        R = pv;
                        G = qv;
                        B = V;
                        break;
                    case 4:
                        R = tv;
                        G = pv;
                        B = V;
                        break;

                    // Red is the dominant color

                    case 5:
                        R = V;
                        G = pv;
                        B = qv;
                        break;

                    // Just in case we overshoot on our math by a little, we put these here. Since its a switch it won't slow us down at all to put these here.

                    case 6:
                        R = V;
                        G = tv;
                        B = pv;
                        break;
                    case -1:
                        R = V;
                        G = pv;
                        B = qv;
                        break;

                    // The color is not defined, we should throw an error.

                    default:
                        //LFATAL("i Value error in Pixel conversion, Value is %d", i);
                        R = G = B = V; // Just pretend its black/white
                        break;
                }
            }
            r = Clamp((int)(R * 255.0));
            g = Clamp((int)(G * 255.0));
            b = Clamp((int)(B * 255.0));
        }

        int Clamp(int i)
        {
            if (i < 0) return 0;
            if (i > 255) return 255;
            return i;
        }
    }
}
