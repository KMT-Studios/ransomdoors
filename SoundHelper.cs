using NAudio.Wave;
using System.IO;

namespace rans0m;

/// <summary>
/// A single playable sound. Disposes its own device and reader once playback finishes.
/// </summary>
public sealed class SoundHandle : IDisposable
{
    private readonly WaveOut _waveOut;
    private readonly WaveFileReader _reader;
    private bool _disposed;
    private bool _looping;

    public SoundHandle(WaveOut waveOut, WaveFileReader reader)
    {
        _waveOut = waveOut;
        _reader = reader;
        _waveOut.PlaybackStopped += OnPlaybackStopped;
    }

    public void Play() => Guard(() => _waveOut.Play());

    /// <summary>
    /// Plays the clip and restarts it from the beginning each time it ends, until
    /// <see cref="Stop"/> is called.
    /// </summary>
    public void PlayLooping()
    {
        Guard(() =>
        {
            _looping = true;
            _waveOut.Play();
        });
    }

    public void Stop()
    {
        _looping = false;

        Guard(() => _waveOut.Stop());
        DisposeOnce();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (_looping)
        {
            // Rewinding rather than re-reading keeps the loop gapless.
            try
            {
                _reader.Position = 0;
                _waveOut.Play();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                DisposeOnce();
            }

            return;
        }

        DisposeOnce();
    }

    private void Guard(Action action)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            action();
        }
        catch (ObjectDisposedException)
        {
            // The sound was already torn down, nothing to do.
        }
    }

    private void DisposeOnce()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _waveOut.PlaybackStopped -= OnPlaybackStopped;

        _waveOut.Dispose();
        _reader.Dispose();
    }

    public void Dispose() => DisposeOnce();
}

/// <summary>
/// Creates <see cref="SoundHandle"/> instances from embedded assets.
/// </summary>
public static class SoundHelper
{
    /// <summary>
    /// Creates a playable sound handle from a WAV stream.
    /// </summary>
    public static SoundHandle Create(Stream wavStream)
    {
        ArgumentNullException.ThrowIfNull(wavStream);

        WaveFileReader reader = new(wavStream);
        WaveOut waveOut = new();

        waveOut.Init(reader);

        return new SoundHandle(waveOut, reader);
    }
}