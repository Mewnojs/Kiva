﻿using Kiva.MIDI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kiva
{
    public abstract class MIDIMemoryFile : MIDIFile
    {
        public MIDIEvent[][] MIDINoteEvents { get; private set; } = null;
        public MIDIEvent[] MIDIControlEvents { get; private set; } = null;

        public MIDIEvent[] MIDISysexEvents { get; private set; } = null;

        public int[] FirstRenderNote { get; private set; } = new int[256];
        public int[] FirstUnhitNote { get; private set; } = new int[256];
        public double lastRenderTime { get; set; } = 0;
        public int[] LastColorEvent;
        public ColorEvent[][] ColorEvents;

        public MIDIMemoryFile(string path, MIDILoaderSettings settings, CancellationToken cancel)
            : base(path, settings, cancel)
        {
        }

        public override void Parse()
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
                LastColorEvent = new int[trackcount * 16];
                SetColors();
                ParseFinishedInvoke();
            }
            catch (OperationCanceledException)
            {
                MidiFileReader.Close();
                MidiFileReader.Dispose();
                ParseCancelledInvoke();
            }
        }

        protected void RunSecondPassParse()
        {
            object l = new object();
            int tracksParsed = 0;
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
        }

        protected void MergeAudioEvents()
        {
            int count = LoaderSettings.EventPlayerThreads;
            MIDINoteEvents = new MIDIEvent[count][];
            Parallel.For(0, count, new ParallelOptions() { CancellationToken = cancel }, i =>
            {
                try
                {
                    MIDINoteEvents[i] = TimedMerger<MIDIEvent>.MergeMany(parsers.Select(p => new SkipIterator<MIDIEvent>(p.NoteEvents, i, count)).ToArray(), e =>
                    {
                        return e.time;
                    }).ToArray();
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        protected void MergeControlEvents()
        {
            int count = LoaderSettings.EventPlayerThreads;
            MIDIControlEvents = TimedMerger<MIDIEvent>.MergeMany(parsers.Select(p => p.ControlEvents).ToArray(), e => e.time).ToArray();
        }

        protected void MergeColorEvents()
        {
            List<ColorEvent[]> ce = new List<ColorEvent[]>();
            foreach (var p in parsers) ce.AddRange(p.ColorEvents);
            ColorEvents = ce.ToArray();
        }

        protected void MergeSysexEvents()
        {
            int count = LoaderSettings.EventPlayerThreads;
            MIDISysexEvents = TimedMerger<MIDIEvent>.MergeMany(parsers.Select(p => p.SysexEvents).ToArray(), e => e.time).ToArray();
        }

        protected IEnumerable<Note> RemoveOverlaps(IEnumerable<Note> input)
        {
            List<Note> tickNotes = new List<Note>();
            double currTick = -1;
            double epsilon = 0.00001;
            foreach (var n in input)
            {
                if (n.start > currTick)
                {
                    foreach (var _n in tickNotes) yield return _n;
                    tickNotes.Clear();
                    currTick = n.start + epsilon;
                    tickNotes.Add(n);
                }
                else
                {
                    var count = tickNotes.Count;
                    var end = n.end + epsilon;
                    if (count != 0 && tickNotes[count - 1].end <= end)
                    {
                        int i = count - 1;
                        for (; i >= 0; i--)
                        {
                            if (tickNotes[i].end > end) break;
                        }
                        i++;
                        if (i == 0)
                            tickNotes.Clear();
                        else if (i != count)
                            tickNotes.RemoveRange(i, count - i);
                        tickNotes.Add(n);
                    }
                    else
                    {
                        tickNotes.Add(n);
                    }
                }
            }
            foreach (var _n in tickNotes) yield return _n;
        }

        protected abstract void SecondPassParse();

        public void SetColorEvents(double time)
        {
            if (time < 0) time = 0;
            Parallel.For(0, MidiNoteColors.Length, i =>
            {
                MidiNoteColors[i] = OriginalMidiNoteColors[i];
                var ce = ColorEvents[i];
                var last = LastColorEvent[i];
                if (ce.Length == 0) return;
                if (ce.First().time > time)
                {
                    LastColorEvent[i] = 0;
                    return;
                }
                if (ce.Last().time <= time)
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

    }
}
