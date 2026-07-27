using System;
using Microsoft.Xna.Framework.Audio;
using NVorbis;

namespace StardewModdingAPI.Mobile.Facade;

public class OggStream : IDisposable
{
    private readonly string oggFileName;

    protected DynamicSoundEffectInstance? _instance;

    private const int DefaultBufferSize = 44100;

    private const int BytesPerSample = 2;

    private readonly float[] readSampleBuffer;

    private readonly short[] castBuffer;

    private readonly byte[] xnaBuffer;

    protected int bufferSize = 44100;

    private float volume;

    internal VorbisReader? Reader { get; private set; }

    internal bool Ready { get; private set; }

    internal bool Preparing { get; private set; }

    public Action? FinishedAction { get; }

    private DynamicSoundEffectInstance Instance => this._instance
        ?? throw new InvalidOperationException("The Ogg stream is not prepared.");

    private VorbisReader ActiveReader => this.Reader
        ?? throw new InvalidOperationException("The Ogg stream is not open.");

    public float Volume
    {
        get
        {
            return this.volume;
        }
        set
        {
            this.volume = value;
            this.Instance.Volume = value;
        }
    }

    public uint LoopCount
    {
        get
        {
            return this.Instance.LoopCount;
        }
        set
        {
            this.Instance.LoopCount = value;
        }
    }

    public OggStream(string filename, Action? finishedAction = null, int buffer_size = 44100)
    {
        this.oggFileName = filename;
        this.FinishedAction = finishedAction;
        this.bufferSize = buffer_size;
        this.readSampleBuffer = new float[this.bufferSize];
        this.castBuffer = new short[this.bufferSize];
        this.xnaBuffer = new byte[this.bufferSize * 2];
    }

    public void Prepare()
    {
        if (!this.Preparing && !this.Ready)
        {
            this.Preparing = true;
            this.Open(precache: true);
        }
    }

    public void Play()
    {
        if (this._instance != null)
        {
            if (this._instance.PendingBufferCount == 0)
            {
                this.SubmitBuffer();
            }
            this._instance.Play();
        }
    }

    public void Pause()
    {
        this.Instance.Pause();
    }

    public void Resume()
    {
        this.Instance.Resume();
    }

    public void Stop()
    {
        this.SeekToPosition(new TimeSpan(0L));
        this.Instance.Stop();
    }

    public void SeekToPosition(TimeSpan pos)
    {
        this.ActiveReader.TimePosition = pos;
    }

    public TimeSpan GetPosition()
    {
        if (this.Reader == null)
        {
            return TimeSpan.Zero;
        }
        return this.Reader.TimePosition;
    }

    public TimeSpan GetLength()
    {
        return this.ActiveReader.TotalTime;
    }

    public void Dispose()
    {
        if (this._instance != null)
        {
            this._instance.Dispose();
            this._instance = null;
        }
    }

    internal void Open(bool precache = false)
    {
        var reader = new VorbisReader(this.oggFileName);
        this.Reader = reader;
        var instance = new OggStreamSoundEffectInstance(reader.SampleRate, reader.Channels == 1 ? AudioChannels.Mono : AudioChannels.Stereo);
        this._instance = instance;
        instance.BufferNeeded += delegate
        {
            this.SubmitBuffer();
        };
        if (precache)
        {
            this.SubmitBuffer();
        }
        this.Ready = true;
        this.Preparing = false;
    }

    public virtual void SubmitBuffer()
    {
        var reader = this.ActiveReader;
        var instance = this.Instance;
        if (reader.SamplePosition >= reader.TotalSamples)
        {
            if (this.LoopCount == 0)
            {
                if (instance.PendingBufferCount == 0)
                {
                    if (this.FinishedAction != null)
                    {
                        this.FinishedAction();
                    }
                    instance.FinishedQueueing();
                }
                return;
            }
            reader.SamplePosition = 0L;
        }
        int read_samples = reader.ReadSamples(this.readSampleBuffer, 0, this.bufferSize);
        CastBuffer(this.readSampleBuffer, this.castBuffer, read_samples);
        Buffer.BlockCopy(this.castBuffer, 0, this.xnaBuffer, 0, read_samples * 2);
        instance.SubmitBuffer(this.xnaBuffer, 0, read_samples * 2);
    }

    public static void CastBuffer(float[] inBuffer, short[] outBuffer, int length)
    {
        for (int i = 0; i < length; i++)
        {
            int temp = (int)(32767f * inBuffer[i]);
            if (temp > 32767)
            {
                temp = 32767;
            }
            else if (temp < -32768)
            {
                temp = -32768;
            }
            outBuffer[i] = (short)temp;
        }
    }

    public DynamicSoundEffectInstance GetSoundEffectInstance()
    {
        return this.Instance;
    }

    internal void Close()
    {
        if (this.Reader != null)
        {
            this.Reader.Dispose();
            this.Reader = null;
        }
        if (this._instance != null)
        {
            this._instance.Dispose();
            this._instance = null;
        }
        this.Ready = false;
    }
}
