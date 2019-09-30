﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.WPF;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using SharpDX.Direct3D;
using System.Collections.Concurrent;
using IO = System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Kiva_MIDI
{
    class MIDIRenderer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, Pack = 16)]
        struct NotesGlobalConstants
        {
            public float NoteLeft;
            public float NoteRight;
            public float NoteBorder;
            public float ScreenAspect;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RenderNote
        {
            public float start;
            public float end;
            public NoteCol color;
        }

        public MIDIFile File { get; set; }
        public PlayingState Time { get; set; } = new PlayingState();

        public long LastRenderedNoteCount { get; private set; } = 0;

        ShaderManager notesShader;
        InputLayout noteLayout;
        InputLayout keyLayout;
        Buffer globalNoteConstants;

        NotesGlobalConstants noteConstants;

        int noteBufferLength = 1 << 10;
        Buffer noteBuffer;

        bool[] blackKeys = new bool[257];
        int[] keynum = new int[257];

        Settings settings;

        ConcurrentQueue<RenderNote[]>[] noteRenderQueue = new ConcurrentQueue<RenderNote[]>[257];
        bool renderThreadStarted = false;
        DeviceContext storedContext;

        public MIDIRenderer(Device device, Settings settings)
        {
            for (int i = 0; i < 257; i++)
            {
                noteRenderQueue[i] = new ConcurrentQueue<RenderNote[]>();
            }

            this.settings = settings;
            string noteShaderData;
            if (IO.File.Exists("Notes.fx"))
            {
                noteShaderData = IO.File.ReadAllText("Notes.fx");
            }
            else
            {
                var assembly = Assembly.GetExecutingAssembly();
                var names = assembly.GetManifestResourceNames();
                using (var stream = assembly.GetManifestResourceStream("Kiva_MIDI.Notes.fx"))
                using (var reader = new IO.StreamReader(stream))
                    noteShaderData = reader.ReadToEnd();
            }
            notesShader = new ShaderManager(
                device,
                ShaderBytecode.Compile(noteShaderData, "VS_Note", "vs_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(noteShaderData, "PS", "ps_4_0", ShaderFlags.None, EffectFlags.None),
                ShaderBytecode.Compile(noteShaderData, "GS_Note", "gs_4_0", ShaderFlags.None, EffectFlags.None)
            );

            noteLayout = new InputLayout(device, ShaderSignature.GetInputSignature(notesShader.vertexShaderByteCode), new[] {
                new InputElement("START",0,Format.R32_Float,0,0),
                new InputElement("END",0,Format.R32_Float,4,0),
                new InputElement("COLORL",0,Format.R32G32B32A32_Float,8,0),
                new InputElement("COLORR",0,Format.R32G32B32A32_Float,24,0),
            });

            noteConstants = new NotesGlobalConstants()
            {
                NoteBorder = 0.002f,
                NoteLeft = -0.2f,
                NoteRight = 0.0f,
                ScreenAspect = 1f
            };

            noteBuffer = new Buffer(device, new BufferDescription()
            {
                BindFlags = BindFlags.VertexBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
                SizeInBytes = 40 * noteBufferLength,
                Usage = ResourceUsage.Dynamic,
                StructureByteStride = 0
            });

            globalNoteConstants = new Buffer(device, new BufferDescription()
            {
                BindFlags = BindFlags.ConstantBuffer,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None,
                SizeInBytes = 16,
                Usage = ResourceUsage.Dynamic,
                StructureByteStride = 0
            });

            for (int i = 0; i < blackKeys.Length; i++) blackKeys[i] = isBlackNote(i);
            int b = 0;
            int w = 0;
            for (int i = 0; i < keynum.Length; i++)
            {
                if (blackKeys[i]) keynum[i] = b++;
                else keynum[i] = w++;
            }
        }

        void SetNoteShaderConstants(DeviceContext context, NotesGlobalConstants constants)
        {
            DataStream data;
            context.MapSubresource(globalNoteConstants, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out data);
            data.Write(constants);
            context.UnmapSubresource(globalNoteConstants, 0);
            context.VertexShader.SetConstantBuffer(0, globalNoteConstants);
            context.GeometryShader.SetConstantBuffer(0, globalNoteConstants);
        }

        double[] x1array = new double[257];
        double[] wdtharray = new double[257];

        public void Render(Device device, RenderTargetView target, DrawEventArgs args)
        {
            var context = device.ImmediateContext;
            context.InputAssembler.InputLayout = noteLayout;

            if (!renderThreadStarted)
            {
                storedContext = context;
                Task.Factory.StartNew(() => { FlushNoteBufferThread(storedContext); });
                renderThreadStarted = true;
            }

            double time = Time.GetTime();
            double timeScale = settings.Volatile.Size;
            double renderCutoff = time + timeScale;
            int firstNote = 0;
            int lastNote = 128;
            int kbfirstNote = firstNote;
            int kblastNote = lastNote;
            if (blackKeys[firstNote]) kbfirstNote--;
            if (blackKeys[lastNote - 1]) kblastNote++;

            double wdth;

            double knmfn = keynum[firstNote];
            double knmln = keynum[lastNote - 1];
            if (blackKeys[firstNote]) knmfn = keynum[firstNote - 1] + 0.5;
            if (blackKeys[lastNote - 1]) knmln = keynum[lastNote] - 0.5;
            for (int i = 0; i < 257; i++)
            {
                if (!blackKeys[i])
                {
                    x1array[i] = (float)(keynum[i] - knmfn) / (knmln - knmfn + 1);
                    wdtharray[i] = 1.0f / (knmln - knmfn + 1);
                }
                else
                {
                    int _i = i + 1;
                    wdth = 0.6f / (knmln - knmfn + 1);
                    int bknum = keynum[i] % 5;
                    double offset = wdth / 2;
                    if (bknum == 0 || bknum == 2)
                    {
                        offset *= 1.3;
                    }
                    else if (bknum == 1 || bknum == 4)
                    {
                        offset *= 0.7;
                    }
                    x1array[i] = (float)(keynum[_i] - knmfn) / (knmln - knmfn + 1) - offset;
                    wdtharray[i] = wdth;
                }
            }

            notesShader.SetShaders(context);
            noteConstants.ScreenAspect = (float)(args.RenderSize.Height / args.RenderSize.Width);
            noteConstants.NoteBorder = 0.0015f;
            SetNoteShaderConstants(context, noteConstants);

            context.ClearRenderTargetView(target, new Color4(0, 0, 0, 0.6f));

            if (File != null)
            {
                File.SetColorEvents(time);

                var colors = File.MidiNoteColors;
                var lastTime = File.lastRenderTime;

                long notesRendered = 0;
                object addLock = new object();

                for (int black = 0; black < 2; black++)
                {
                    Parallel.For(firstNote, lastNote, new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount }, k =>
                    {
                        if ((blackKeys[k] && black == 0) || (!blackKeys[k] && black == 1)) return;
                        long _notesRendered = 0;
                        unsafe
                        {
                            RenderNote* rn = stackalloc RenderNote[noteBufferLength];
                            int nid = 0;
                            int noff = File.FirstRenderNote[k];
                            Note[] notes = File.Notes[k];
                            if (lastTime > time)
                            {
                                for (noff = 0; noff < notes.Length; noff++)
                                {
                                    if (notes[noff].end > time)
                                    {
                                        File.FirstRenderNote[k] = noff;
                                        break;
                                    }
                                }
                            }
                            else if (lastTime < time)
                            {
                                for (; noff < notes.Length; noff++)
                                {
                                    if (notes[noff].end > time)
                                    {
                                        File.FirstRenderNote[k] = noff;
                                        break;
                                    }
                                }
                            }
                            while (noff != notes.Length && notes[noff].start < renderCutoff)
                            {
                                var n = notes[noff++];
                                if (n.end < time) continue;
                                _notesRendered++;
                                rn[nid++] = new RenderNote()
                                {
                                    start = (float)((n.start - time) / timeScale),
                                    end = (float)((n.end - time) / timeScale),
                                    color = colors[n.colorPointer]
                                };
                                if (nid == noteBufferLength)
                                {
                                    PushNoteBuffer(k, (IntPtr)rn, nid);
                                    nid = 0;
                                }
                            }
                            PushNoteBuffer(k, (IntPtr)rn, nid);
                            lock (addLock)
                            {
                                notesRendered += _notesRendered;
                            }
                        }
                    });
                }

                LastRenderedNoteCount = notesRendered;
                File.lastRenderTime = time;
            }
            else
            {
                LastRenderedNoteCount = 0;
            }
            //context.Flush();
        }

        unsafe void PushNoteBuffer(int key, IntPtr data, int count)
        {
            // we need to reallocate and copy the data, since it was previously allocated on the stack
            if (count == 0) return;
            RenderNote[] noteArray = new RenderNote[count];
            fixed (RenderNote* noteArrayPtr = noteArray) {
                Unsafe.CopyBlock((void*)noteArrayPtr, (void*)data, Convert.ToUInt32(count * sizeof(RenderNote)));
            }
            //SpinWait.SpinUntil(() => { return noteRenderQueue[key].Count < 1024; });
            noteRenderQueue[key].Enqueue(noteArray);
        }

        unsafe void FlushNoteBufferThread(DeviceContext context)
        {
            while (true)
            {
                for (int i = 0; i < 257; i++)
                {
                    if (!noteRenderQueue[i].TryDequeue(out RenderNote[] noteArray))
                        continue;
                    DataStream data;
                    context.MapSubresource(noteBuffer, 0, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out data);
                    data.Position = 0;
                    fixed (RenderNote* notes = noteArray)
                    {
                        data.WriteRange((IntPtr)notes, noteArray.Length * sizeof(RenderNote));
                    };
                    context.UnmapSubresource(noteBuffer, 0);
                    context.InputAssembler.PrimitiveTopology = PrimitiveTopology.PointList;
                    context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(noteBuffer, 40, 0));
                    noteConstants.NoteLeft = (float)x1array[i];
                    noteConstants.NoteRight = (float)(x1array[i] + wdtharray[i]);
                    SetNoteShaderConstants(context, noteConstants);
                    context.Draw(noteArray.Length, 0);
                }
            }
        }

        private DeviceContext GetInternalContext(Device device)
        {
            var props = device.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var prop = device.GetType().GetField("Context", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            DeviceContext context = prop.GetValue(device) as DeviceContext;
            return context;
        }

        public void Dispose()
        {
            Disposer.SafeDispose(ref noteLayout);
            Disposer.SafeDispose(ref keyLayout);
            Disposer.SafeDispose(ref notesShader);
            Disposer.SafeDispose(ref globalNoteConstants);
            Disposer.SafeDispose(ref noteBuffer);
        }

        bool isBlackNote(int n)
        {
            n = n % 12;
            return n == 1 || n == 3 || n == 6 || n == 8 || n == 10;
        }
    }
}
