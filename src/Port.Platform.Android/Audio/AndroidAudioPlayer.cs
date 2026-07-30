using Android.Media;
using Port.Content;

namespace Port.Platform.Android.Audio;

/// <summary>
/// Minimal PC SharedAudioSystem stand-in: play networked AudioComponent .ogg files
/// with distance attenuation vs the ghost eye.
/// </summary>
public sealed class AndroidAudioPlayer : IDisposable
{
    readonly object _gate = new();
    SoundPool? _pool;
    readonly Dictionary<string, int> _soundIds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, byte> _loading = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, StreamPlay> _streams = new(); // entity id → play
    readonly HashSet<int> _oneShotPlayed = new();
    readonly Queue<(string Path, int SoundId)> _loadDone = new();
    string? _contentRoot;
    AczOnDemandFetcher? _fetcher;
    float _earX, _earY;
    float _master = 0.85f;
    bool _ready;

    sealed class StreamPlay
    {
        public int StreamId;
        public string Path = "";
        public bool Loop;
        public long LastSeenFrame;
    }

    long _frame;

    public void SetContentRoot(string? root)
    {
        lock (_gate) _contentRoot = root;
    }

    public void SetFetcher(AczOnDemandFetcher? fetcher)
    {
        lock (_gate) _fetcher = fetcher;
    }

    public void SetMasterVolume(float v)
    {
        lock (_gate) _master = Math.Clamp(v, 0f, 1f);
    }

    public void SetEar(float x, float y)
    {
        lock (_gate)
        {
            _earX = x;
            _earY = y;
        }
    }

    void EnsurePool()
    {
        if (_pool is not null)
            return;
        _pool = new SoundPool(12, global::Android.Media.Stream.Music, 0);
        _pool.SetOnLoadCompleteListener(new LoadListener(this));
        _ready = true;
    }

    public void PlayGlobalOneShot(string fileName, float volumeDb = 0f)
    {
        lock (_gate)
        {
            EnsurePool();
            var path = NormalizeAudioPath(fileName);
            var soundId = EnsureSoundLocked(path);
            if (soundId == 0)
                return;
            var gain = VolumeDbToGain(volumeDb) * _master;
            if (gain < 0.01f)
                return;
            try { _pool?.Play(soundId, gain, gain, 1, 0, 1f); } catch { /* */ }
        }
    }

    public void Tick(IReadOnlyList<Port.Net.WorldAudioCue> cues)
    {
        lock (_gate)
        {
            EnsurePool();
            _frame++;
            DrainLoaded();

            var seen = new HashSet<int>();
            foreach (var cue in cues)
            {
                if (!cue.Playing || string.IsNullOrWhiteSpace(cue.FileName))
                    continue;

                var id = cue.Entity.Id;
                seen.Add(id);

                var gain = VolumeDbToGain(cue.VolumeDb) * _master;
                if (!cue.Global)
                {
                    var dx = cue.X - _earX;
                    var dy = cue.Y - _earY;
                    var dist = MathF.Sqrt(dx * dx + dy * dy);
                    var maxD = MathF.Max(1f, cue.MaxDistance);
                    if (dist > maxD)
                        continue;
                    // Inverse-distance style falloff (PC OpenAL InverseDistanceClamped approx).
                    var refD = 1f;
                    var atten = refD / (refD + MathF.Max(0f, dist - refD));
                    gain *= Math.Clamp(atten, 0.05f, 1f);
                }

                if (gain < 0.01f)
                    continue;

                var path = NormalizeAudioPath(cue.FileName);
                if (_streams.TryGetValue(id, out var existing))
                {
                    existing.LastSeenFrame = _frame;
                    try { _pool?.SetVolume(existing.StreamId, gain, gain); } catch { /* */ }
                    continue;
                }

                // One-shots: play once per audio entity id.
                if (!cue.Loop && _oneShotPlayed.Contains(id))
                    continue;

                var soundId = EnsureSoundLocked(path);
                if (soundId == 0)
                    continue;

                var loop = cue.Loop ? -1 : 0;
                var stream = _pool!.Play(soundId, gain, gain, 1, loop, 1f);
                if (stream == 0)
                    continue;

                _streams[id] = new StreamPlay
                {
                    StreamId = stream,
                    Path = path,
                    Loop = cue.Loop,
                    LastSeenFrame = _frame,
                };
                if (!cue.Loop)
                    _oneShotPlayed.Add(id);
            }

            // Stop streams whose entities left PVS / finished.
            List<int>? dead = null;
            foreach (var (id, play) in _streams)
            {
                if (seen.Contains(id) && _frame - play.LastSeenFrame < 2)
                    continue;
                dead ??= new List<int>();
                dead.Add(id);
            }

            if (dead is not null)
            {
                foreach (var id in dead)
                {
                    if (_streams.Remove(id, out var play))
                    {
                        try { _pool?.Stop(play.StreamId); } catch { /* */ }
                    }
                }
            }

            // Bound one-shot memory.
            if (_oneShotPlayed.Count > 400)
                _oneShotPlayed.Clear();
        }
    }

    int EnsureSoundLocked(string relativePath)
    {
        if (_soundIds.TryGetValue(relativePath, out var id))
            return id;
        if (_loading.ContainsKey(relativePath))
            return 0;

        var full = ResolveLocal(relativePath);
        if (full is null)
        {
            _fetcher?.EnsureFile(
                relativePath.StartsWith("Audio/", StringComparison.OrdinalIgnoreCase)
                    ? relativePath
                    : "Audio/" + relativePath.TrimStart('/'));
            return 0;
        }

        try
        {
            _loading[relativePath] = 0;
            var sid = _pool!.Load(full, 1);
            if (sid == 0)
            {
                _loading.Remove(relativePath);
                return 0;
            }

            // OnLoadComplete will publish; some devices load sync — stash anyway.
            _soundIds[relativePath] = sid;
            return sid;
        }
        catch
        {
            _loading.Remove(relativePath);
            return 0;
        }
    }

    string? ResolveLocal(string relativePath)
    {
        relativePath = relativePath.Replace('\\', '/').TrimStart('/');
        if (_contentRoot is null)
            return _fetcher?.TryLocalPath(relativePath)
                   ?? _fetcher?.TryLocalPath("Audio/" + relativePath);

        string[] candidates =
        [
            Path.Combine(_contentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(_contentRoot, "Resources", relativePath.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(_contentRoot, "Audio", relativePath.Replace('/', Path.DirectorySeparatorChar)),
        ];
        if (relativePath.StartsWith("Audio/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = relativePath["Audio/".Length..];
            candidates =
            [
                Path.Combine(_contentRoot, "Audio", rest.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(_contentRoot, "Resources", "Audio", rest.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(_contentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)),
            ];
        }

        foreach (var c in candidates)
        {
            if (File.Exists(c))
                return c;
        }

        return _fetcher?.TryLocalPath(relativePath)
               ?? _fetcher?.TryLocalPath(relativePath.StartsWith("Audio/", StringComparison.OrdinalIgnoreCase)
                   ? relativePath
                   : "Audio/" + relativePath);
    }

    void DrainLoaded()
    {
        while (_loadDone.Count > 0)
        {
            var (path, sid) = _loadDone.Dequeue();
            _soundIds[path] = sid;
            _loading.Remove(path);
        }
    }

    void OnLoaded(int sampleId, bool success)
    {
        lock (_gate)
        {
            if (!success)
            {
                foreach (var (k, _) in _loading.ToArray())
                {
                    if (_soundIds.ContainsKey(k)) continue;
                    // Can't map sampleId→path easily; leave loading until retry.
                }
                return;
            }

            // SoundPool load-complete: sample already stored in EnsureSoundLocked for sync loads.
            foreach (var k in _loading.Keys.ToArray())
            {
                if (_soundIds.ContainsKey(k))
                    _loading.Remove(k);
            }
        }
    }

    static string NormalizeAudioPath(string fileName)
    {
        var p = fileName.Replace('\\', '/').Trim();
        if (p.StartsWith('/'))
            p = p.TrimStart('/');
        if (p.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase))
            p = p["Resources/".Length..];
        return p;
    }

    /// <summary>PC SharedAudioSystem.VolumeToGain — dB → linear gain.</summary>
    public static float VolumeDbToGain(float volumeDb)
    {
        if (float.IsNegativeInfinity(volumeDb) || volumeDb < -60f)
            return 0f;
        return MathF.Pow(10f, volumeDb / 10f);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var play in _streams.Values)
            {
                try { _pool?.Stop(play.StreamId); } catch { /* */ }
            }

            _streams.Clear();
            _pool?.Release();
            _pool = null;
            _ready = false;
        }
    }

    sealed class LoadListener : Java.Lang.Object, SoundPool.IOnLoadCompleteListener
    {
        readonly AndroidAudioPlayer _owner;
        public LoadListener(AndroidAudioPlayer owner) => _owner = owner;
        public void OnLoadComplete(SoundPool? soundPool, int sampleId, int status)
            => _owner.OnLoaded(sampleId, status == 0);
    }
}
