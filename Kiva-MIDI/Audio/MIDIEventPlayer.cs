using Kiva.Audio.APIs;
using Kiva.MIDI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Kiva.Audio
{
    class MIDIEventPlayer : IDisposable
    {
        [DllImport("ntdll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int NtDelayExecution([MarshalAs(UnmanagedType.I1)] bool alertable, ref Int64 DelayInterval);

        KivaSettings settings;

        public int BufferLen => eventFeed == null ? 0 : eventFeed.Count;

        public int DeviceID
        {
            get => deviceID;
            set
            {
                if (deviceID != value)
                {
                    deviceID = value;
                    if (cancelConsumer != null)
                        cancelConsumer.Cancel();
                    if (deviceThread != null)
                        deviceThread.GetAwaiter().GetResult();
                    cancelConsumer = new CancellationTokenSource();
                    if (deviceID < 0)
                        deviceThread = Task.Factory.StartNew(() => RunEventConsumerKDMAPI(cancelConsumer.Token), TaskCreationOptions.LongRunning);
                    else
                        deviceThread = Task.Factory.StartNew(() => RunEventConsumerWINMM(cancelConsumer.Token), TaskCreationOptions.LongRunning);
                }
            }
        }

        public MIDIFile File
        {
            get => file;
            set
            {
                Task.Run(() =>
                {
                    lock (this)
                    {
                        if (file != null)
                        {
                            file = null;
                            lock (tasks)
                            {
                                foreach (var t in tasks) t.GetAwaiter().GetResult();
                                tasks.Clear();
                            }
                            changed = true;
                        }
                        file = value;
                    }
                });
            }
        }

        BlockingCollection<MIDIEvent> eventFeed;

        MIDIFile file = null;
        public GlobalTime Time { get; set; } = new GlobalTime();
        bool disposed = false;
        Task playerThread = null;

        List<Task> tasks = new List<Task>();
        private int deviceID = -1;

        IntPtr device = IntPtr.Zero;
        Task deviceThread = null;

        IntPtr hdr = IntPtr.Zero;
        IntPtr hdrBuffer = IntPtr.Zero;

        CancellationTokenSource cancelConsumer;

        public MIDIEventPlayer(KivaSettings settings)
        {
            this.settings = settings;
        }

        void ResetBuffer()
        {
            //eventFeed.;
        }

        public void Dispose()
        {
            File = null;
            disposed = true;
            playerThread.GetAwaiter().GetResult();
            deviceThread.GetAwaiter().GetResult();
        }

        public void RunPlayer()
        {
            if (cancelConsumer != null)
                cancelConsumer.Cancel();
            if (deviceThread != null)
                deviceThread.GetAwaiter().GetResult();
            cancelConsumer = new CancellationTokenSource();
            eventFeed = new BlockingCollection<MIDIEvent>();
            if (deviceID < 0)
                deviceThread = Task.Factory.StartNew(() => RunEventConsumerKDMAPI(cancelConsumer.Token), TaskCreationOptions.LongRunning);
            else
                deviceThread = Task.Factory.StartNew(() => RunEventConsumerWINMM(cancelConsumer.Token), TaskCreationOptions.LongRunning);
            playerThread = Task.Run(() =>
            {
                while (!disposed)
                {
                    SpinWait.SpinUntil(() => file != null || disposed);
                    if (disposed) break;
                    try
                    {
                        tasks.Add(RunPlayerThread(-1));
                        tasks.Add(RunPlayerThread(-2));
                        for (int i = 0; i < (file as MIDIMemoryFile).MIDINoteEvents.Length; i++)
                        {
                            tasks.Add(RunPlayerThread(i));
                        }
                        lock (tasks)
                        {
                            foreach (var t in tasks) t.GetAwaiter().GetResult();
                            lock (tasks)
                                tasks.Clear();
                        }
                    }
                    catch { }
                }
                eventFeed.CompleteAdding();
            });
        }

        bool changed = true;

        void ResetControllers()
        {
            //for (int c = 0; c < 16; c++)
            //{
            //    var ce = 0b1011000 | c + (123 << 8);
            //    eventFeed.Add(new MIDIEvent() { data = (uint)ce, time = (float)Time.GetTime(), vel = 127 });
            //}
            if (deviceID == -1)
                KDMAPI.ResetKDMAPIStream();
            else if (device != IntPtr.Zero)
                try
                {
                    WinMM.midiOutReset(device);
                }
                catch { }
        }

        Task RunPlayerThread(int i)
        {
            return Task.Factory.StartNew(() =>
            {
                changed = true;
                MIDIEvent[] events;
                if (i == -1)
                    try
                    {
                        events = (file as MIDIMemoryFile).MIDIControlEvents;
                    }
                    catch { return; }
                else if (i == -2)
                    try
                    {
                        events = (file as MIDIMemoryFile).MIDISysexEvents;
                    }
                    catch { return; }
                else
                    try
                    {
                        events = (file as MIDIMemoryFile).MIDINoteEvents[i];
                    }
                    catch { return; }
                if (events.Length == 0) return;
                int evid = 0;
                double lastTime = 0;
                Action onChanged = () => changed = true;
                Time.TimeChanged += onChanged;
                while (file != null)
                {
                    reset:
                    int time = Time.GetTimeInt();
                    if (Time.Paused)
                    {
                        ResetControllers();
                        while (Time.Paused)
                        {
                            Thread.Sleep(50);
                            if (file == null) goto dispose;
                        }
                        evid = GetEventPos(events, time) - 10;
                        if (evid < 0 || i <= -1) evid = 0;
                    }
                    while (evid == events.Length && !changed)
                    {
                        Thread.Sleep(50);
                        if (file == null) goto dispose;
                    }
                    if (changed || lastTime > time)
                    {
                        time = Time.GetTimeInt();
                        ResetControllers();

                        evid = GetEventPos(events, time) - 10;
                        if (evid < 0 || i <= -1) evid = 0;
                        changed = false;
                    }
                    lastTime = time;
                    if (evid < events.Length)
                    {
                        var ev = events[evid];
                        var delay = (ev.time - time) / Time.Speed;
                        while (delay > Constants.TimeToInt(0.2) && file != null)
                        {
                            Thread.Sleep(20);
                            delay = (ev.time - Time.GetTimeInt()) / Time.Speed;
                            if (changed) break;
                            if (file == null) goto dispose;
                        }
                        if (changed) goto reset;
                        if (delay > 0)
                        {
                            //Int64 s = (long)(delay * -10000000 / Constants.TicksPerSecond);
                            //NtDelayExecution(false, ref s);
                            Thread.Sleep(new TimeSpan((long)(delay * 10000000 / Constants.TicksPerSecond)));
                        }
                        if ((eventFeed.Count > events[evid].vel * 100 || delay < -1) && i > -1)
                            while ((evid < events.Length && events[evid].time < Time.GetTimeInt() && (eventFeed.Count > events[evid].vel * 100 || delay < -1)))
                            {
                                if (events[evid].vel > 80)
                                { }
                                evid++;
                            }
                        else
                            eventFeed.Add(ev);
                    }
                    else
                    {
                        while (Time.GetTimeInt() > events[events.Length - 1].time)
                        {
                            Thread.Sleep(50);
                            if (file == null) goto dispose;
                        }
                        evid = GetEventPos(events, time);
                    }
                    evid++;
                }
                dispose:
                Time.TimeChanged -= onChanged;
            }, TaskCreationOptions.LongRunning);
        }

        void RunEventConsumerKDMAPI(CancellationToken cancel)
        {
            try
            {
                KDMAPI.InitializeKDMAPIStream();
            }
            catch
            {
                settings.General.SelectedAudioEngine = AudioEngine.WinMM;
                return;
            }
            int hdr_size = Marshal.SizeOf(typeof(MIDIHDR));
            try
            {
                hdrBuffer = Marshal.AllocHGlobal(WinMM.STREAM_BUFFER_LENGTH_MAX); // Maximum buffer size for WinMM API
                var managed_hdr = new MIDIHDR()
                {
                    lpData = hdrBuffer,
                    dwBufferLength = WinMM.STREAM_BUFFER_LENGTH_MAX,
                };
                this.hdr = hdr = Marshal.AllocHGlobal(hdr_size);
                Marshal.StructureToPtr(managed_hdr, hdr, false);
                KDMAPI.PrepareLongData(hdr, (uint)hdr_size);
                managed_hdr = Marshal.PtrToStructure<MIDIHDR>(hdr);

                foreach (var e in eventFeed.GetConsumingEnumerable(cancel))
                {
                    if ((byte)(e.data & 0xFF) == 0b11110000)
                    {

                        int data_size = e.dataLong.Length;
                        if (data_size > WinMM.STREAM_BUFFER_LENGTH_MAX)
                            throw new Exception($"Sysex length exceeded at time {e.time}");
                        Marshal.Copy(e.dataLong, 0, hdrBuffer, data_size);
                        managed_hdr.dwBytesRecorded = (uint)data_size;
                        Marshal.StructureToPtr(managed_hdr, hdr, false);

                        KDMAPI.SendDirectLongData(hdr, (uint)hdr_size);
                    }
                    else
                    {
                        KDMAPI.SendDirectDataNoBuf(e.data);
                    }
                    if (deviceID != -1 || disposed) break;
                }
            }
            catch (OperationCanceledException) { }

            KDMAPI.UnprepareLongData(hdr, (uint)hdr_size);
            Marshal.FreeHGlobal(hdr);
            Marshal.FreeHGlobal(hdrBuffer);

            KDMAPI.TerminateKDMAPIStream();
        }

        void RunEventConsumerWINMM(CancellationToken cancel)
        {
            var id = deviceID;
            int hdr_size = Marshal.SizeOf(typeof(MIDIHDR));
            try
            {
                IntPtr device;
                IntPtr hdr;
                WinMM.midiOutOpen(out device, id, (
                    IntPtr hMidiOut,
                    uint wMsg,
                    UIntPtr dwInstance,
                    UIntPtr dwParam1,
                    UIntPtr dwParam2) =>
                { }, Process.GetCurrentProcess().Handle, WinMM.CALLBACK_FUNCTION);
                this.device = device;
                this.device = device;

                hdrBuffer = Marshal.AllocHGlobal(WinMM.STREAM_BUFFER_LENGTH_MAX); // Maximum buffer size for WinMM API
                var managed_hdr = new MIDIHDR()
                {
                    lpData = hdrBuffer,
                    dwBufferLength = WinMM.STREAM_BUFFER_LENGTH_MAX,
                };
                this.hdr = hdr = Marshal.AllocHGlobal(hdr_size);
                Marshal.StructureToPtr(managed_hdr, hdr, false);
                WinMM.midiOutPrepareHeader(device, hdr, (uint)hdr_size);
                managed_hdr = Marshal.PtrToStructure<MIDIHDR>(hdr);
                
                foreach (var e in eventFeed.GetConsumingEnumerable(cancel))
                {
                    if ((byte)(e.data & 0xFF) == 0b11110000)
                    {
                        int data_size = e.dataLong.Length;
                        if (data_size > WinMM.STREAM_BUFFER_LENGTH_MAX) 
                            throw new Exception($"Sysex length exceeded at time {e.time}");
                        Marshal.Copy(e.dataLong, 0, hdrBuffer, data_size);
                        managed_hdr.dwBytesRecorded = (uint)data_size;
                        Marshal.StructureToPtr(managed_hdr, hdr, false);

                        WinMM.midiOutLongMsg(device, hdr, (uint)hdr_size);
                    }
                    else
                    {
                        WinMM.midiOutShortMsg(device, e.data);
                    }
                    if (deviceID != id || disposed) break;
                }
            }
            catch { }
            try
            {
                WinMM.midiOutClose(device);
                WinMM.midiOutUnprepareHeader(device, hdr, (uint)hdr_size);
                Marshal.FreeHGlobal(hdr);
                Marshal.FreeHGlobal(hdrBuffer);
            }
            catch { }

            this.device = IntPtr.Zero;
        }

        int GetEventPos(MIDIEvent[] events, int time)
        {
            if (time < events[0].time) return 0;
            if (time > events[events.Length - 1].time) return events.Length;
            int min = 0;
            int max = events.Length - 1;
            while (min <= max)
            {
                int mid = (min + max) / 2;
                if (events[mid].time > time)
                {
                    max = mid - 1;
                }
                else if (mid + 1 != events.Length && events[mid + 1].time < time)
                {
                    min = mid + 1;
                }
                else
                {
                    return mid;
                }
            }
            return events.Length;
        }
    }
}
