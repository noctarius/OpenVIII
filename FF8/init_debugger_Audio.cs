﻿using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

#if _WINDOWS && !_X64
using DirectMidi;
#endif

using System.Runtime.InteropServices;
using NAudio.Vorbis;
using FFmpeg.AutoGen;
using System.Diagnostics;
using System.Linq;

namespace FF8
{
#pragma warning disable IDE1006 // Naming Styles

    internal static class init_debugger_Audio
#pragma warning restore IDE1006 // Naming Styles
    {
        private static IntPtr driver;
        private static IntPtr synth;
        private static IntPtr settings;
        private static IntPtr player;
        enum ThreadFluidState
        {
            /// <summary>
            /// Idle means player is ready for new action. Either it finished playing or was never run
            /// </summary>
            idle,
            /// <summary>
            /// Playing means the player is actively running the sequence and working with synthesizer. Every kind of seeking and music handling is automatic
            /// </summary>
            playing,
            /// <summary>
            /// Paused actually makes it possible to pause the sequencer in place. Change to running to continue playing
            /// </summary>
            paused,
            /// <summary>
            /// Reset stops playing, clears the sequence and all related helpers and falls back into idle mode
            /// </summary>
            reset,
            /// <summary>
            /// New song is a special state, where it does the resetting, idle and then goes into playing state. You should always set state to newSong when you point to new music collection.
            /// </summary>
            newSong,
            /// <summary>
            /// Call only at the very exit of application. This resets and aborts the thread
            /// </summary>
            kill
        }
        private static ThreadFluidState fluidState;
#if _WINDOWS && !_X64
        private static CDirectMusic cdm;
        private static CDLSLoader loader;
        private static CSegment segment;
        private static CAPathPerformance path;
        public static CPortPerformance cport; //public explicit
        private static COutputPort outport;
        private static CCollection ccollection;
        private static CInstrument[] instruments;
#endif
#if _WINDOWS
        private const string fluidLibName = "x64/libfluidsynth-2.dll";
#else
        private const string fluidLibName = "x64/libfluidsynth-2.so";
#endif

#if _X64
        [DllImport(fluidLibName)]
        public static extern IntPtr new_fluid_settings();

        [DllImport(fluidLibName)]
        public static extern IntPtr new_fluid_synth(IntPtr settings);

        [DllImport(fluidLibName)]
        public static extern IntPtr new_fluid_player(IntPtr synth);

        [DllImport(fluidLibName)]
        public static extern IntPtr delete_fluid_player(IntPtr player);

        [DllImport(fluidLibName)]
        public static extern IntPtr new_fluid_audio_driver(IntPtr settings, IntPtr synth);

        [DllImport(fluidLibName)]
        public static extern int fluid_player_play(IntPtr player);

        [DllImport(fluidLibName)]
        public static extern int fluid_player_stop(IntPtr player);

        [DllImport(fluidLibName)]
        public static extern int fluid_player_join(IntPtr player);

        [DllImport(fluidLibName)]
        public static extern int fluid_player_add(IntPtr player, string mid);

        [DllImport(fluidLibName)]
        public static extern int fluid_player_add_mem(IntPtr player, byte[] mid, uint len); //use this one instead of file dumping!

        [DllImport(fluidLibName)]
        public static extern int fluid_synth_sfload(IntPtr synth, string sf2, int reset_presets);

        [DllImport(fluidLibName)]
        public static extern int fluid_settings_setstr(IntPtr settings, string name, string str);

        [DllImport(fluidLibName)]
        public static extern int fluid_synth_noteon(IntPtr synth, int channel, int key, int velocity);

        [DllImport(fluidLibName)]
        public static extern int fluid_synth_noteoff(IntPtr synth, int channel, int key);

        [DllImport(fluidLibName)]
        public static extern int fluid_synth_all_notes_off(IntPtr synth, int channel);

        [DllImport(fluidLibName)]
        public static extern int fluid_synth_bank_select(IntPtr synth, int channel, int bank);

        [DllImport(fluidLibName)]
        public static extern int fluid_synth_program_change(IntPtr synth, int channel, int prog);

        [DllImport(fluidLibName)]
        public static extern int fluid_synth_program_reset(IntPtr synth);
#endif

        private static byte[] getBytes(object aux)
        {
            int length = Marshal.SizeOf(aux);
            IntPtr ptr = Marshal.AllocHGlobal(length);
            byte[] myBuffer = new byte[length];

            Marshal.StructureToPtr(aux, ptr, true);
            Marshal.Copy(ptr, myBuffer, 0, length);
            Marshal.FreeHGlobal(ptr);

            return myBuffer;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        private struct SoundEntry
        {
            public UInt32 Size;
            public UInt32 Offset;
            private UInt32 output_TotalSize => Size + 70; // Total bytes of file -8 because for some reason 8 bytes don't count
            private const UInt32 output_HeaderSize = 50; //Total bytes of Header
            private UInt32 output_DataSize => Size; //Total bytes of Data Section

            //public byte[] UNK; //12
            //public WAVEFORMATEX WAVFORMATEX; //18 header starts here
            //public ushort SamplesPerBlock; //2
            //public ushort ADPCM; //2
            //public ADPCMCOEFSET[] ADPCMCoefSets; //array should be of [ADPCM] size //7*4 = 28
            public byte[] HeaderData;

            public void fillHeader(BinaryReader br)
            {
                if (HeaderData == null)
                {
                    HeaderData = new byte[output_HeaderSize + 28];
                    using (MemoryStream ms = new MemoryStream(HeaderData))
                    {
                        ms.Write(Encoding.ASCII.GetBytes("RIFF"), 0, 4);
                        ms.Write(getBytes(output_TotalSize), 0, 4);
                        ms.Write(Encoding.ASCII.GetBytes("WAVEfmt "), 0, 8);
                        ms.Write(getBytes(output_HeaderSize), 0, 4);
                        ms.Write(br.ReadBytes((int)output_HeaderSize), 0, (int)output_HeaderSize);
                        ms.Write(Encoding.ASCII.GetBytes("data"), 0, 4);
                        ms.Write(getBytes(output_DataSize), 0, 4);
                    }
                }
            }
        }

#pragma warning disable CS0649

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
        private struct ADPCMCOEFSET
        {
            public short iCoef1;
            public short iCoef2;
        };

#pragma warning restore CS0649

        private static SoundEntry[] soundEntries;
        public static int soundEntriesCount;

        public const int S_OK = 0x00000000;
        public const int MaxSoundChannels = 20;

        /// <summary>
        /// This is for short lived sound effects. The Larger the array is the more sounds can be
        /// played at once. If you want sounds to loop of have volume you'll need to have a
        /// SoundEffectInstance added to ffcc, and have those sounds be played like music where they
        /// loop in the background till stop.
        /// </summary>
        public static Ffcc[] SoundChannels { get; } = new Ffcc[MaxSoundChannels];

        public static int CurrentSoundChannel
        {
            get => _currentSoundChannel;
            set
            {
                if (value >= MaxSoundChannels)
                {
                    value = 0;
                }
                else if (value < 0)
                {
                    value = MaxSoundChannels - 1;
                }

                _currentSoundChannel = value;
            }
        }

        internal static void DEBUG()
        {
            string dmusic_pt = "", RaW_ogg_pt = "", music_pt = "", music_wav_pt = "";
            //Roses and Wine V07 moves most of the sgt files to dmusic_backup
            //it leaves a few files behind. I think because RaW doesn't replace everything.
            //ogg files stored in:
            RaW_ogg_pt = MakiExtended.GetUnixFullPath(Path.Combine(Memory.FF8DIR, "../../RaW/GLOBAL/Music"));
            if (!Directory.Exists(RaW_ogg_pt))
            {
                RaW_ogg_pt = null;
            }
            // From what I gather the OGG files and the sgt files have the same numerical prefix. I
            // might try to add the functionality to the debug screen monday.

            dmusic_pt = MakiExtended.GetUnixFullPath(Path.Combine(Memory.FF8DIR, "../Music/dmusic_backup/"));
            if (!Directory.Exists(dmusic_pt))
            {
                dmusic_pt = null;
            }

            music_pt = MakiExtended.GetUnixFullPath(Path.Combine(Memory.FF8DIR, "../Music/dmusic/"));
            if (!Directory.Exists(music_pt))
            {
                music_pt = null;
            }

            music_wav_pt = MakiExtended.GetUnixFullPath(Path.Combine(Memory.FF8DIR, "../Music/"));
            if (!Directory.Exists(music_wav_pt))
            {
                music_wav_pt = null;
            }

            // goal of dicmusic is to be able to select a track by prefix. it adds an list of files
            // with the same prefix. so you can later on switch out which one you want.
            if (RaW_ogg_pt != null)
            {
                Memory.musices = Directory.GetFiles(RaW_ogg_pt, "*.ogg");
                foreach (string m in Memory.musices)
                {
                    if (ushort.TryParse(Path.GetFileName(m).Substring(0, 3), out ushort key))
                    {
                        //mismatched prefix's go here
                        if (key == 512)
                        {
                            key = 0; //loser.ogg and sgt don't match.
                        }

                        if (!Memory.dicMusic.ContainsKey(key))
                        {
                            Memory.dicMusic.Add(key, new List<string> { m });
                        }
                        else
                        {
                            Memory.dicMusic[key].Add(m);
                        }
                    }
                }
            }
            if (dmusic_pt != null)
            {
                Memory.musices = Directory.GetFiles(dmusic_pt, "*.sgt");

                foreach (string m in Memory.musices)
                {
                    if (ushort.TryParse(Path.GetFileName(m).Substring(0, 3), out ushort key))
                    {
                        if (!Memory.dicMusic.ContainsKey(key))
                        {
                            Memory.dicMusic.Add(key, new List<string> { m });
                        }
                        else
                        {
                            Memory.dicMusic[key].Add(m);
                        }
                    }
                    else
                    {
                        if (!Memory.dicMusic.ContainsKey(999)) //gets any music w/o prefix
                        {
                            Memory.dicMusic.Add(999, new List<string> { m });
                        }
                        else
                        {
                            Memory.dicMusic[999].Add(m);
                        }
                    }
                }
            }
            if (music_pt != null)
            {
                Memory.musices = Directory.GetFiles(music_pt, "*.sgt");

                foreach (string m in Memory.musices)
                {
                    if (ushort.TryParse(Path.GetFileName(m).Substring(0, 3), out ushort key))
                    {
                        if (!Memory.dicMusic.ContainsKey(key))
                        {
                            Memory.dicMusic.Add(key, new List<string> { m });
                        }
                        else
                        {
                            Memory.dicMusic[key].Add(m);
                        }
                    }
                    else
                    {
                        if (!Memory.dicMusic.ContainsKey(999)) //gets any music w/o prefix
                        {
                            Memory.dicMusic.Add(999, new List<string> { m });
                        }
                        else
                        {
                            Memory.dicMusic[999].Add(m);
                        }
                    }
                }
            }
            if (music_wav_pt != null)
            {
                Memory.musices = Directory.GetFiles(music_pt, "*.wav");

                foreach (string m in Memory.musices)
                {
                    if (ushort.TryParse(Path.GetFileName(m).Substring(0, 3), out ushort key))
                    {
                        if (!Memory.dicMusic.ContainsKey(key))
                        {
                            Memory.dicMusic.Add(key, new List<string> { m });
                        }
                        else
                        {
                            Memory.dicMusic[key].Add(m);
                        }
                    }
                    else
                    {
                        if (!Memory.dicMusic.ContainsKey(999)) //gets any music w/o prefix
                        {
                            Memory.dicMusic.Add(999, new List<string> { m });
                        }
                        else
                        {
                            Memory.dicMusic[999].Add(m);
                        }
                    }
                }
            }
            settings = new_fluid_settings();
#if !_WINDOWS
            fluid_settings_setstr(settings, "audio.driver", "alsa");
#endif
            synth = new_fluid_synth(settings);
            driver = new_fluid_audio_driver(settings, synth);
            string dlsPath = Path.Combine(Path.GetDirectoryName(music_pt), "FF8.dls");
            fluid_synth_sfload(synth, dlsPath, 1);
            player = new_fluid_player(synth);
            GCHandle.Alloc(settings, GCHandleType.Pinned);
            GCHandle.Alloc(synth, GCHandleType.Pinned);
            GCHandle.Alloc(driver, GCHandleType.Pinned);
            GCHandle.Alloc(player, GCHandleType.Pinned);
            System.Threading.Thread fluidThread = new System.Threading.Thread(new System.Threading.ThreadStart(FluidWorker));
            fluidThread.Start();
        }


        private struct fluidThreadKey
        {
            public int channel;
            public int key;
            public double time;

        }
        private static double fluidWorkerAbsTime;
        private static int fluidCurrentIndex;
        private static List<fluidThreadKey> fluidThreadKeys;
        private const int fluidTimeFrame = 1; //test
        private const int DMUS_PPQ = 768; //DirectMusic PulsePerQuarterNote
        private const int DMUS_MusicTimeMilisecond = 60000000; //not really sure why 60 000 000 instead of 60 000, but it works
        private static float fluidDivider = 1000f; //test
        static void FluidWorker()
        {
            while (true)
            {
                switch (fluidState)
                {
                    //we are in the idle mode. We do nothing.
                    case ThreadFluidState.idle:
                        continue;

                        //This is almost the same as idle, but the paused mode is never meant to be destroyed or ignored. In idle mode the engine thinks the player has no song loaded and is available.
                    case ThreadFluidState.paused:
                        continue;

                        //We received the reset state. We have to clear all lists and helpers that were used for playing music.
                    case ThreadFluidState.reset:
                        //FluidWorker_Reset();
                        fluidState = ThreadFluidState.idle;
                        continue;

                    //We received the newSong state. We are resetting as in reset, but in the end we fall into playing
                    case ThreadFluidState.newSong:
                        FluidWorker_Reset();
                        FluidWorker_ProduceMid();
                        //FluidWorket_SetTempo();
                        //FluidWorket_SetBanks();
                        if(player != IntPtr.Zero)
                        {
                            delete_fluid_player(player);
                            player = new_fluid_player(synth);
                        }
                        fluid_player_add(player, "D:/mid.mid");
                        fluid_player_play(player);
                        fluidState = ThreadFluidState.playing;
                        continue;

                    //The most important state- it handles the real-time transmission to synth driver
                    case ThreadFluidState.playing:

                        //UpdateMusic();
                        continue;

                    case ThreadFluidState.kill:
                        FluidWorker_Reset();
                        System.Threading.Thread.CurrentThread.Abort();
                        break;
                }
            }
        }

        private static void FluidWorker_ProduceMid()
        {
            NAudio.Midi.MidiEventCollection mid = new NAudio.Midi.MidiEventCollection(1, DMUS_PPQ);
            mid.AddTrack();
            for(int i  = 0; i<lbinbins.Count; i++)
            {
                var lbin = lbinbins[i];
                int patch_ = (int)(lbin.dwPatch & 0xFF); //MSB, LSB + patch on the least 8 bits
                NAudio.Midi.PatchChangeEvent patch = new NAudio.Midi.PatchChangeEvent(0, (int)lbin.dwPChannel+1, patch_);
                mid.AddEvent(patch, 0);
            }
            mid.AddEvent(new NAudio.Midi.TempoEvent((int)(DMUS_MusicTimeMilisecond / tetr.dblTempo), 0), 0);
            for(int i =0; i<tims.Count; i++)
            {
                var tim = tims[i];
                //NAudio.Midi.TimeSignatureEvent time = new NAudio.Midi.TimeSignatureEvent(tim.lTime, ,,tim);
            }
            for (int i = 0; i<seqt.Count; i++)
            {
                var ss = seqt[i];
                NAudio.Midi.NoteEvent note = new NAudio.Midi.NoteEvent(ss.mtTime, (int)ss.dwPChannel+1, NAudio.Midi.MidiCommandCode.NoteOn, ss.bByte1, ss.bByte2);
                mid.AddEvent(note, 0);
                note = new NAudio.Midi.NoteEvent(ss.mtTime + ss.mtDuration, (int)ss.dwPChannel+1, NAudio.Midi.MidiCommandCode.NoteOff, ss.bByte1, ss.bByte2);
                mid.AddEvent(note, 0);
            }
            NAudio.Midi.MidiFile.Export("D:/mid.mid", mid); //DEBUG

        }

        private static void FluidWorket_SetBanks()
        {
            for(int i = 0; i<lbinbins.Count; i++)
                fluid_synth_program_change(synth, (int)lbinbins[0].dwPChannel, (int)lbinbins[0].dwPatch);
            //int reseter = fluid_synth_program_reset(synth);
        }

        private static void FluidWorket_SetTempo()
        {
            fluidDivider = (float)(150f * tetr.dblTempo);
        }

        /// <summary>
        /// THREAD: Updates the music. seqt[i] are sorted by absTime. We store the fluidCurrentIndex and check for absTime via one If. If it proceeds
        /// it puts the key into list for duration and increments the fluidCurrentIndex. If nothing, then depletes the duration time in key list (if any)
        /// and then absTime is the only that gets incremented.
        /// </summary>
        private static void UpdateMusic()
        {
            if (fluidCurrentIndex >= seqt.Count - 1)
                fluidState = ThreadFluidState.idle;
            var key = seqt[fluidCurrentIndex];
            if(fluidWorkerAbsTime>=key.mtTime)
            {
                fluidThreadKeys.Add(new fluidThreadKey() { channel = (int)key.dwPChannel, key = key.bByte1, time = (int)key.mtDuration });
                fluid_synth_noteon(synth, (int)key.dwPChannel, key.bByte1, key.bByte2);
                fluidCurrentIndex++;
            }
            //BELOW COMMENTED CODE IS AWFUL OPTIMALIZED- Change to array with count of channels and then simply put the data in there
            //for (int i = fluidThreadKeys.Count; i > 0; i--) //we need to reverse the loop, because we are deleting items in list and incrementing on it
                //if (fluidThreadKeys[i-1].time < 0)
                //{
                //    fluid_synth_noteoff(synth, fluidThreadKeys[i-1].channel, fluidThreadKeys[i-1].key);
                //Console.WriteLine($"fluid_synth_noteoff: {fluidThreadKeys[i - 1].channel}, {fluidThreadKeys[i - 1].key}");
                //    fluidThreadKeys.Remove(fluidThreadKeys[i-1]);
                //}
                //else
                    //fluidThreadKeys[i-1] = new fluidThreadKey() { channel = fluidThreadKeys[i-1].channel, key = fluidThreadKeys[i-1].key, time = fluidThreadKeys[i-1].time - fluidTimeFrame/ (fluidDivider*2f) };
           
             fluidWorkerAbsTime += fluidTimeFrame/fluidDivider;

        }

        private static void FluidWorker_Reset()
        {
            fluidThreadKeys = new List<fluidThreadKey>();
            fluidWorkerAbsTime = 0;
            fluidCurrentIndex = 0;
        }


        //I messed around here as figuring out how things worked probably didn't need to mess with this.
        internal static void DEBUG_SoundAudio()
        {
            string path = Path.Combine(Memory.FF8DIR, "../Sound/audio.fmt");
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                soundEntries = new SoundEntry[br.ReadUInt32()];
                fs.Seek(36, SeekOrigin.Current);
                for (int i = 0; i < soundEntries.Length - 1; i++)
                {
                    UInt32 sz = br.ReadUInt32();
                    if (sz == 0)
                    {
                        fs.Seek(34, SeekOrigin.Current); continue;
                    }

                    soundEntries[i] = new SoundEntry
                    {
                        Size = sz,
                        Offset = br.ReadUInt32()
                    };
                    fs.Seek(12, SeekOrigin.Current);
                    soundEntries[i].fillHeader(br);
                }
            }
            soundEntriesCount = soundEntries.Length;
        }

        internal static void PlaySound(int soundID)
        {
            if (soundEntries == null || soundEntries[soundID].Size == 0)
            {
                return;
            }
            SoundChannels[CurrentSoundChannel] = new Ffcc(
                new Ffcc.Buffer_Data { DataSeekLoc = soundEntries[soundID].Offset, DataSize = soundEntries[soundID].Size, HeaderSize = (uint)soundEntries[soundID].HeaderData.Length },
                soundEntries[soundID].HeaderData,
                Path.Combine(Memory.FF8DIR, "../Sound/audio.dat"));
            SoundChannels[CurrentSoundChannel++].Play();
        }

        public static void StopSound()
        {
            //waveout.Stop();
        }

        internal static void Update()
        {
            //checks to see if music buffer is running low and getframe triggers a refill.
            if (ffccMusic != null && !ffccMusic.Ahead)
            {
                ffccMusic.Next();
            }
        }

        //callable test

        public static byte[] ReadFullyByte(Stream stream)
        {
            // following formula goal is to calculate the number of bytes to make buffer. might be wrong.
            long size = stream.Length; // stream.Length should be in bytes. will error later if short.
            int start = 0;
            byte[] buffer = new byte[size];
            int read = 0;
            //do
            //{
            read = stream.Read(buffer, start, buffer.Length);
            start++;
            //}
            //while (read == 0 && start < size);
            if (read == 0)
            {
                return null;
            }

            if (read < size)
            {
                Array.Resize<byte>(ref buffer, read);
            }

            return buffer;
        }

        public static byte[] ReadFullyFloat(VorbisWaveReader stream)
        {
            // following formula goal is to calculate the number of bytes to make buffer. might be wrong.
            long size = (stream.Length / sizeof(float)) + 100; //unsure why but read was > than size so added 100; will error if the size is too small.

            float[] buffer = new float[size];

            int read = stream.Read(buffer, 0, buffer.Length);
            return GetSamplesWaveData(buffer, read);
        }

        public static byte[] GetSamplesWaveData(byte[] samples, int samplesCount)
        {
            float[] f = new float[(samplesCount / sizeof(float))];
            int i = 0;
            for (int n = 0; n < samples.Length; n += sizeof(float))
            {
                f[i++] = BitConverter.ToSingle(samples, n);
            }
            return GetSamplesWaveData(f, samplesCount / sizeof(float));
        }

        public static byte[] GetSamplesWaveData(float[] samples, int samplesCount)
        { // converts 32 bit float samples to 16 bit pcm. I think :P
            // https://stackoverflow.com/questions/31957211/how-to-convert-an-array-of-int16-sound-samples-to-a-byte-array-to-use-in-monogam/42151979#42151979
            byte[] pcm = new byte[samplesCount * 2];
            int sampleIndex = 0,
                pcmIndex = 0;

            while (sampleIndex < samplesCount)
            {
                short outsample = (short)(samples[sampleIndex] * short.MaxValue);
                pcm[pcmIndex] = (byte)(outsample & 0xff);
                pcm[pcmIndex + 1] = (byte)((outsample >> 8) & 0xff);

                sampleIndex++;
                pcmIndex += 2;
            }

            return pcm;
        }

        private static bool musicplaying = false;
        private static int lastplayed = -1;

        public static void PlayStopMusic()
        {
            if (!musicplaying || lastplayed != Memory.MusicIndex)
            {
                PlayMusic();
            }
            else
            {
                StopAudio();
            }
        }

        private static Ffcc ffccMusic = null; // testing using class to play music instead of Naudio / Nvorbis
        private static int _currentSoundChannel;

        public static void PlayMusic()
        {
            string ext = "";
            bool bFakeLinux = true; //set to force linux behaviour on windows; To delete after Linux music playable
            if (Memory.dicMusic[Memory.MusicIndex].Count > 0)
            {
                ext = Path.GetExtension(Memory.dicMusic[Memory.MusicIndex][0]).ToLower();
            }

            string pt = Memory.dicMusic[Memory.MusicIndex][0];

            StopAudio();

            switch (ext)
            {
                case ".ogg":
                    //ffccMusic = new Ffcc(@"c:\eyes_on_me.wav", AVMediaType.AVMEDIA_TYPE_AUDIO, Ffcc.FfccMode.STATE_MACH);
                    ffccMusic = new Ffcc(pt, AVMediaType.AVMEDIA_TYPE_AUDIO, Ffcc.FfccMode.STATE_MACH);
                    ffccMusic.Play(.5f);
                    break;

                case ".sgt":
                    if(MakiExtended.IsLinux || bFakeLinux)
                    {
                        fluidState = ThreadFluidState.reset;
                        while (fluidState != ThreadFluidState.idle)
                            ; //we are waiting for reset end on fluidThread
                        ReadSegmentFileManually(pt);
                        Console.WriteLine($"segh: {segh.mtLength}");
                    }
                        SynthPlay();
                    
                    break;
            }

            musicplaying = true;
            lastplayed = Memory.MusicIndex;
        }

        private static void SynthPlay()
        {
            fluidState = ThreadFluidState.newSong;
        }

        public static void KillAudio()
        {
            //if (Sound != null && !Sound.IsDisposed)
            //{
            //    Sound.Dispose();
            //}
            fluidState = ThreadFluidState.kill;
            
            for (int i = 0; i < MaxSoundChannels; i++)
            {
                if (SoundChannels[i] != null && !SoundChannels[i].isDisposed)
                {
                    SoundChannels[i].Dispose();
                    SoundChannels[i] = null;
                }
            }

            try
            {
                if (MakiExtended.IsLinux)
                {
#if _WINDOWS && !_X64
                    cport.StopAll();
                    cport.Dispose();
                    ccollection.Dispose();
                    loader.Dispose();
                    outport.Dispose();
                    path.Dispose();
                    cdm.Dispose();
#endif
                }
            }
            catch
            {
            }
        }

        public static void StopAudio()
        {
            musicplaying = false;
            if (ffccMusic != null)
            {
                ffccMusic.Dispose();
                ffccMusic = null;
            }
            fluid_synth_all_notes_off(synth, -1);
            fluid_player_stop(player);
            fluidState = ThreadFluidState.idle;



#if _WINDOWS && !_X64
            try
            {
                if (!MakiExtended.IsLinux)
                {
                    cport.StopAll();
                }
            }
            catch { }
#endif
        }
        //MUSIC_TIME=LONG->int32; REFERENCE_TIME=LONGLONG->long
        [StructLayout(LayoutKind.Sequential, Pack =1, Size =24)]
        struct DMUS_IO_SEGMENT_HEADER
        {
            public uint dwRepeats;
            public int mtLength;
            public int mtPlayStart;
            public int mtLoopStart;
            public int mtLoopEnd;
            public uint dwResolution;
        }

        [StructLayout(LayoutKind.Sequential, Pack =1, Size =8)]
        struct DMUS_IO_VERSION
        {
            uint dwVersionMS;
            uint dwVersionLS;
        }

        [StructLayout(LayoutKind.Sequential, Pack =1, Size =32)]
        struct DMUS_IO_TRACK_HEADER
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst =16)]
            byte[] guidClassID;
            uint dwPosition;
            uint dwGroup;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst =4)]
            char[] _ckid;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
            char[] _fccType;

            public string Ckid { get => new string(_ckid).Trim('\0'); }
            public string FccType { get => new string(_fccType).Trim('\0'); }

        }

        [StructLayout(LayoutKind.Sequential, Pack =1, Size =8)]
        struct DMUS_IO_TIMESIGNATURE_ITEM
        {
            public uint lTime;
            public byte bBeatsPerMeasure;
            public byte bBeat;
            public ushort wGridsPerBeat;
        }

        struct DMUS_IO_TEMPO_ITEM
        {
            public int lTime;
            public double dblTempo;
        }

        [StructLayout(LayoutKind.Sequential, Pack =1, Size =23, CharSet = CharSet.Unicode)]
        struct DMUS_IO_CHORD
        {
            [MarshalAs(UnmanagedType.ByValArray,ArraySubType = UnmanagedType.ByValTStr, SizeConst =16)] //wchars are used, therefore we need to force 2 bytes per char
            char[] wszName;
            uint mtTime;
            ushort wMeasure;
            byte bBeat;
            byte padding;
        }

        [StructLayout(LayoutKind.Sequential, Pack =1, Size =18)]
        struct DMUS_IO_SUBCHORD
        {
            uint dwChordPattern;
            uint dwScalePattern;
            uint dwInversionPoints;
            uint dwLevels;
            byte bChordRoot;
            byte bScaleRoot;
        }

        struct DMUS_IO_SEQ_ITEM
        {
            public uint mtTime; //EVENT TIME
            public uint mtDuration; //DURATION OF THE EVENT
            public uint dwPChannel;
            public short nOffset; //Grid=Subdivision of a beat. The number of grids per beat is part of the Microsoft® DirectMusic® time signature.
            public byte bStatus; //MIDI event type
            public byte bByte1; //1st MIDI data
            public byte bByte2; //2nd MIDI data
        }

        struct DMUS_IO_CURVE_ITEM
        {
            uint mtStart;
            uint mtDuration;
            uint mtResetDuration;
            uint dwPChannel;
            short nOffset;
            short nStartValue;
            short nEndValue;
            short nResetValue;
            byte bType;
            byte bCurveShape;
            byte bCCData;
            byte bFlags;
        }

        [StructLayout(LayoutKind.Sequential, Pack =1, Size =42)]
        struct DMUS_IO_INSTRUMENT
        {
            public uint dwPatch;
            uint dwAssignPatch;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst =4)]
            uint[] dwNoteRanges;
            public uint dwPChannel;
            uint dwFlags;
            byte bPan;
            byte bVolume;
            short nTranspose;
            uint dwChannelPriority;
        }

        static DMUS_IO_SEGMENT_HEADER segh = new DMUS_IO_SEGMENT_HEADER();
        static DMUS_IO_VERSION vers = new DMUS_IO_VERSION();
        static List<DMUS_IO_TIMESIGNATURE_ITEM> tims;
        static List<DMUS_IO_TRACK_HEADER> trkh;
        static DMUS_IO_TEMPO_ITEM tetr;
        static DMUS_IO_CHORD crdh;
        static List<DMUS_IO_SUBCHORD> crdb;
        static List<DMUS_IO_SEQ_ITEM> seqt;
        static List<DMUS_IO_CURVE_ITEM> curl;
        static List<DMUS_IO_INSTRUMENT> lbinbins;
        /// <summary>
        /// [LINUX]: This method manually reads DirectMusic Segment files
        /// </summary>
        /// <param name="pt"></param>
        private static void ReadSegmentFileManually(string pt)
        {
            using (FileStream fs = new FileStream(pt, FileMode.Open, FileAccess.Read))
            using (BinaryReader br = new BinaryReader(fs))
            {
                if(ReadFourCc(br) != "RIFF")
                {
                    Console.WriteLine($"init_debugger_Audio::ReadSegmentFileManually: NOT RIFF!");
                    return;
                }
                fs.Seek(4, SeekOrigin.Current);
                if(ReadFourCc(br) != "DMSG")
                {
                    Console.WriteLine($"init_debugger_Audio::ReadSegmentFileManually: Broken structure. Expected DMSG!");
                    return;
                }
                ReadSegmentForm(fs, br);
                if(seqt == null)
                {
                    Console.WriteLine("init_debugger_Audio::ReadSegmentFileManually: Critical error. No sequences read!!!");
                    return;
                }
            }
        }

        private static void ReadSegmentForm(FileStream fs, BinaryReader br)
        {
            string fourCc;
            trkh = new List<DMUS_IO_TRACK_HEADER>();
            tims = new List<DMUS_IO_TIMESIGNATURE_ITEM>();
            crdb = new List<DMUS_IO_SUBCHORD>();
            seqt = new List<DMUS_IO_SEQ_ITEM>();
            curl = new List<DMUS_IO_CURVE_ITEM>();
            lbinbins = new List<DMUS_IO_INSTRUMENT>();
            if ((fourCc = ReadFourCc(br)) != "segh")
                { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: Broken structure. Expected segh, got={fourCc}");return;}
            uint chunkSize = br.ReadUInt32();
            if (chunkSize != Marshal.SizeOf(segh))
                { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: chunkSize={chunkSize} is different than DMUS_IO_SEGMENT_HEADER sizeof={Marshal.SizeOf(segh)}");return;}
            segh = MakiExtended.ByteArrayToStructure<DMUS_IO_SEGMENT_HEADER>(br.ReadBytes((int)chunkSize));
            if((fourCc = ReadFourCc(br)) != "guid")
                {Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected guid, got={fourCc}");return;}
            byte[] guid = br.ReadBytes(br.ReadInt32());
            if ((fourCc = ReadFourCc(br)) != "LIST")
                { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected LIST, got={fourCc}");return;}
            //let's skip segment data for now, looks like it's not needed, it's not even oficially a part of segh
            fs.Seek(br.ReadUInt32(), SeekOrigin.Current);
            if ((fourCc = ReadFourCc(br)) != "vers")
                { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected vers, got={fourCc}"); return;}
            if ((chunkSize = br.ReadUInt32()) != Marshal.SizeOf(vers))
                { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: vers expected sizeof={Marshal.SizeOf(vers)}, got={chunkSize}");return;}
            vers = MakiExtended.ByteArrayToStructure<DMUS_IO_VERSION>(br.ReadBytes((int)chunkSize));
            if ((fourCc = ReadFourCc(br)) != "LIST")
                { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected LIST, got={fourCc}");return;}
            //this list should now contain metadata like name, authors and etc. It's completely useless in this project scope
            fs.Seek(br.ReadUInt32(), SeekOrigin.Current); //therefore let's just skip whole UNFO and etc.
            if ((fourCc = ReadFourCc(br)) != "LIST")
                { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected LIST, got={fourCc}"); return; }
            chunkSize = br.ReadUInt32();
            if ((fourCc = ReadFourCc(br)) != "trkl")
            { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected trkl, got={fourCc}"); return; }
            //at this point we are free to read the file up to the end by reading all available DMTK RIFFs;
            uint eof = (uint)fs.Position + chunkSize-4;
            while(fs.Position < eof)
            {
                if ((fourCc = ReadFourCc(br)) != "RIFF")
                { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected RIFF, got={fourCc}"); return; }
                chunkSize = br.ReadUInt32();
                long skipTell = fs.Position;
                Console.WriteLine($"RIFF entry: {ReadFourCc(br)}/{ReadFourCc(br)}");
                var trkhEntry = MakiExtended.ByteArrayToStructure<DMUS_IO_TRACK_HEADER>(br.ReadBytes((int)br.ReadUInt32()));
                trkh.Add(trkhEntry);
                string moduleName = string.IsNullOrEmpty(trkhEntry.Ckid) ? trkhEntry.FccType : trkhEntry.Ckid;
                switch(moduleName.ToLower())
                {
                    case "cord": //Chord track list =[DONE]
                        if ((fourCc = ReadFourCc(br)) != "LIST")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected cord/LIST, got={fourCc}"); break; }
                        uint cordListChunkSize = br.ReadUInt32();
                        if ((fourCc = ReadFourCc(br)) != "cord")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected cord/cord, got={fourCc}"); break; }
                        if ((fourCc = ReadFourCc(br)) != "crdh")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected cord/crdh, got={fourCc}"); break; }
                        fs.Seek(4, SeekOrigin.Current); //crdh size. It's always one DWORD, so...
                        uint crdhDword = br.ReadUInt32();
                        byte crdhRoot = (byte)(crdhDword >> 24);
                        uint crdhScale = crdhDword & 0xFFFFFF;
                        if ((fourCc = ReadFourCc(br)) != "crdb")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected cord/crdb, got={fourCc}"); break; }
                        uint crdbChunkSize = br.ReadUInt32();
                        crdh = MakiExtended.ByteArrayToStructure<DMUS_IO_CHORD>(br.ReadBytes((int)br.ReadUInt32()));
                        uint cSubChords = br.ReadUInt32();
                        uint subChordSize = br.ReadUInt32();
                        for(int k = 0; k<cSubChords; k++)
                            crdb.Add(MakiExtended.ByteArrayToStructure<DMUS_IO_SUBCHORD>(br.ReadBytes((int)subChordSize)));
                        break;
                    case "tetr":
                        if ((fourCc = ReadFourCc(br)) != "tetr")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected tetr/tetr, got={fourCc}"); break; }
                        uint tetrChunkSize = br.ReadUInt32();
                        uint tetrEntrySize = br.ReadUInt32();
                        fs.Seek(4, SeekOrigin.Current); //???
                        tetr = MakiExtended.ByteArrayToStructure<DMUS_IO_TEMPO_ITEM>(br.ReadBytes((int)tetrEntrySize - 4));
                        byte[] doubleBuffer = BitConverter.GetBytes(tetr.dblTempo);
                        byte[] newDoubleBUffer = new byte[8];
                        Array.Copy(doubleBuffer, 4, newDoubleBUffer, 0, 4);
                        Array.Copy(doubleBuffer, 0, newDoubleBUffer, 4, 4);
                        tetr.dblTempo = BitConverter.ToDouble(newDoubleBUffer, 0);
                        break;
                    case "seqt": //Sequence Track Chunk
                        if ((fourCc = ReadFourCc(br)) != "seqt")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected seqt/seqt, got={fourCc}"); break; }
                        uint seqtChunkSize = br.ReadUInt32();
                        if ((fourCc = ReadFourCc(br)) != "evtl")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected seqt/evtl, got={fourCc}"); break; }
                        uint evtlChunkSize = br.ReadUInt32();
                        uint sequenceItemSize = br.ReadUInt32();
                        uint sequenceItemsCount = (evtlChunkSize - 4) / sequenceItemSize;
                        for (int k = 0; k < sequenceItemsCount; k++)
                            seqt.Add(MakiExtended.ByteArrayToStructure<DMUS_IO_SEQ_ITEM>(br.ReadBytes((int)sequenceItemSize)));
                        if ((fourCc = ReadFourCc(br)) != "curl")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected seqt/curl, got={fourCc}"); break; }
                        uint curlChunkSize = br.ReadUInt32();
                        uint curveItemSize = br.ReadUInt32();
                        uint curvesItemCount = (curlChunkSize - 4) / curveItemSize;
                        for (int k = 0; k < curvesItemCount; k++)
                            curl.Add(MakiExtended.ByteArrayToStructure<DMUS_IO_CURVE_ITEM>(br.ReadBytes((int)curveItemSize)));
                        break;
                    case "tims": //Time Signature Track List  =[DONE]
                        if ((fourCc = ReadFourCc(br)) != "tims")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected tims/tims, got={fourCc}"); break; }
                        uint timsChunkSize = br.ReadUInt32();
                        uint timsEntrySize = br.ReadUInt32();
                        for (int n = 0; n < (timsChunkSize - 4) / 8; n++)
                            tims.Add(MakiExtended.ByteArrayToStructure<DMUS_IO_TIMESIGNATURE_ITEM>(br.ReadBytes((int)timsEntrySize)));
                        break;
                    case "dmbt": //Band segment
                        fs.Seek(12, SeekOrigin.Current); //We are skipping RIFF and the segment size. Useless for us
                        if ((fourCc = ReadFourCc(br)) != "LIST")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/LIST, got={fourCc}"); break; }
                        uint lbdlChunkSize = br.ReadUInt32();
                        if ((fourCc = ReadFourCc(br)) != "lbdl")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/lbdl, got={fourCc}"); break; }
                        if ((fourCc = ReadFourCc(br)) != "LIST")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/LIST, got={fourCc}"); break; }
                        _ = br.ReadUInt32();
                        if ((fourCc = ReadFourCc(br)) != "lbnd")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/lbnd, got={fourCc}"); break; }
                        if ((fourCc = ReadFourCc(br)) != "bdih")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/bdih, got={fourCc}"); break; }
                        fs.Seek(br.ReadUInt32(), SeekOrigin.Current);
                        if ((fourCc = ReadFourCc(br)) != "RIFF")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/RIFF, got={fourCc}"); break; }
                        _ = br.ReadUInt32();


                        //Band SEGMENT
                        if ((fourCc = ReadFourCc(br)) != "DMBD")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/DMBD, got={fourCc}"); break; }
                        if ((fourCc = ReadFourCc(br)) != "guid")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/guid, got={fourCc}"); break; }
                        fs.Seek(br.ReadUInt32(), SeekOrigin.Current); //No one cares for guid

                        if ((fourCc = ReadFourCc(br)) != "LIST")
                        { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/LIST, got={fourCc}"); break; }
                        fs.Seek(br.ReadUInt32()+4, SeekOrigin.Current); //we skip the UNFOunam, we don't care for this too
                        uint lbilSegmentSize = br.ReadUInt32();
                        byte[] lbilSegment = br.ReadBytes((int)lbilSegmentSize);

                        //now the list is varied- therefore we need to work on the segment and iterate. Let's create memorystream from memory buffer of segment
                        using (MemoryStream msB = new MemoryStream(lbilSegment))
                        using (BinaryReader brB = new BinaryReader(msB))
                        {
                            if (msB.Position == msB.Length)
                                break;
                            if ((fourCc = ReadFourCc(brB)) != "lbil")
                            { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/lbil, got={fourCc}"); break; }
                            while(true) //this is LIST loop. Always starts with loop and determines the segment true data by the sizeof
                            {
                                if ((fourCc = ReadFourCc(brB)) != "LIST")
                                { if (msB.Position == msB.Length) break; else { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: expected dmbt/LIST, got={fourCc}"); break; } } 
                                uint listBufferSize = brB.ReadUInt32();
                                if(listBufferSize != 52)
                                {
                                    msB.Seek(listBufferSize, SeekOrigin.Current); //the other data is useless for us. We want the bands only.
                                    //Actually there's pointer to DLS file, but who is going to replace the DLS in file when you can do it much easier
                                    //with a mod manager/code modification. You can change the constant FF8.DLS to other filename somewhere above here. 
                                    continue;
                                }
                                else
                                {
                                    string bandHeader = $"{ReadFourCc(brB)}{ReadFourCc(brB)}";
                                    if(bandHeader!= "lbinbins")
                                    { Console.WriteLine($"init_debugger_Audio::ReadSegmentForm: the band LIST reader got this magic: {bandHeader} instead of lbinbins"); break; }
                                    uint sizeofDMUS_IO_INSTRUMENT = brB.ReadUInt32();
                                    DMUS_IO_INSTRUMENT instrument = MakiExtended.ByteArrayToStructure<DMUS_IO_INSTRUMENT>(brB.ReadBytes((int)sizeofDMUS_IO_INSTRUMENT));
                                    lbinbins.Add(instrument);
                                    continue;
                                }
                            }
                        }


                        break;
                    default:
                        break;
                }
                fs.Seek(skipTell+chunkSize, SeekOrigin.Begin);

            }
        }

        private static string ReadFourCc(BinaryReader br) => new string(br.ReadChars(4));
    }
}