using Medallion.Core.Diagnostics;

namespace Medallion.Core.Buffering;

/// <summary>A point in the ring where a clip can legally start.</summary>
internal readonly record struct CutPoint(long Offset, long Pts90k);

public sealed record BufferSnapshot(byte[] Data, double DurationSeconds, int KeyframeCount);

/// <summary>
/// The rolling replay buffer.
///
/// Encoded MPEG-TS from ffmpeg is appended to a single fixed-size circular byte array that
/// is allocated once and never grows, so a session that runs for eight hours uses exactly
/// as much memory as one that ran for eight seconds, and the GC never sees the video data.
///
/// While appending, the stream is parsed just enough to record where each keyframe starts
/// and what its presentation timestamp is. Because the live encoder is configured to resend
/// PAT/PMT before every keyframe and to emit no B-frames, any recorded cut point is a
/// self-contained, immediately decodable entry point. Saving a clip is therefore a memcpy
/// plus a stream-copy remux, with no re-encoding and no interruption to capture.
/// </summary>
public sealed class ReplayRingBuffer
{
    private const int TsPacketSize = 188;
    private const byte SyncByte = 0x47;
    private const long PtsWrapThreshold = 1L << 32;
    private const long PtsModulus = 1L << 33;

    private readonly object _gate = new();
    private readonly byte[] _buffer;

    /// <summary>Absolute count of bytes ever written. Buffer index is this modulo capacity.</summary>
    private long _head;

    /// <summary>Absolute offset the TS parser has consumed up to.</summary>
    private long _parsePos;

    private readonly Queue<CutPoint> _cutPoints = new();

    // Demuxing state
    private int _pmtPid = -1;
    private int _videoPid = -1;
    private long _pendingHeaderOffset = -1;
    private long _lastPts = -1;
    private long _ptsOffset;
    private long _rawPtsPrevious = -1;
    private bool _synced;

    public int Capacity => _buffer.Length;
    public double TargetSeconds { get; }

    public ReplayRingBuffer(double targetSeconds, int videoBitrateKbps, int audioBitrateKbps)
    {
        TargetSeconds = targetSeconds;

        // Size for the requested duration plus 25% headroom. The headroom absorbs
        // rate-control overshoot on scene changes and guarantees a keyframe older than the
        // requested window is still resident when the user presses the hotkey.
        double totalKbps = videoBitrateKbps + audioBitrateKbps + 256; // + muxing overhead
        long bytes = (long)(totalKbps * 1000.0 / 8.0 * targetSeconds * 1.25);

        bytes = Math.Clamp(bytes, 8L * 1024 * 1024, 1024L * 1024 * 1024);
        _buffer = new byte[bytes];

        Log.Info($"Replay buffer allocated: {bytes / (1024 * 1024)} MB for {targetSeconds:0}s " +
                 $"at {videoBitrateKbps} kbps");
    }

    /// <summary>Seconds of footage currently retained and reachable from a cut point.</summary>
    public double BufferedSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_cutPoints.Count == 0 || _lastPts < 0) return 0;
                return Math.Max(0, (_lastPts - _cutPoints.Peek().Pts90k) / 90000.0);
            }
        }
    }

    public long BytesBuffered
    {
        get { lock (_gate) return Math.Min(_head, _buffer.Length); }
    }

    public int CutPointCount
    {
        get { lock (_gate) return _cutPoints.Count; }
    }

    /// <summary>Drops all state. Called when the capture process restarts and PIDs/PTS reset.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _head = 0;
            _parsePos = 0;
            _cutPoints.Clear();
            _pmtPid = -1;
            _videoPid = -1;
            _pendingHeaderOffset = -1;
            _lastPts = -1;
            _ptsOffset = 0;
            _rawPtsPrevious = -1;
            _synced = false;
        }
    }

    /// <summary>
    /// Appends freshly encoded bytes. Called on the capture reader thread only; the lock is
    /// uncontended except for the brief moment a snapshot is taken.
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;

        lock (_gate)
        {
            WriteToRing(data);
            ParseAvailable();
            TrimExpiredCutPoints();
        }
    }

    private void WriteToRing(ReadOnlySpan<byte> data)
    {
        // A single write larger than the whole ring can only leave its own tail behind.
        if (data.Length >= _buffer.Length)
        {
            data[^_buffer.Length..].CopyTo(_buffer);
            _head += data.Length;
            _parsePos = _head - _buffer.Length;
            _synced = false;
            return;
        }

        int index = (int)(_head % _buffer.Length);
        int firstChunk = Math.Min(data.Length, _buffer.Length - index);

        data[..firstChunk].CopyTo(_buffer.AsSpan(index));
        if (firstChunk < data.Length)
            data[firstChunk..].CopyTo(_buffer.AsSpan(0));

        _head += data.Length;

        // If the writer lapped the parser, skip ahead: those bytes are gone.
        long oldest = Math.Max(0, _head - _buffer.Length);
        if (_parsePos < oldest)
        {
            _parsePos = oldest;
            _synced = false;
        }
    }

    private void ReadFromRing(long absoluteOffset, Span<byte> destination)
    {
        int index = (int)(absoluteOffset % _buffer.Length);
        int firstChunk = Math.Min(destination.Length, _buffer.Length - index);

        _buffer.AsSpan(index, firstChunk).CopyTo(destination);
        if (firstChunk < destination.Length)
            _buffer.AsSpan(0, destination.Length - firstChunk).CopyTo(destination[firstChunk..]);
    }

    private byte ByteAt(long absoluteOffset) => _buffer[(int)(absoluteOffset % _buffer.Length)];

    private void ParseAvailable()
    {
        Span<byte> packet = stackalloc byte[TsPacketSize];

        while (_head - _parsePos >= TsPacketSize)
        {
            if (!_synced && !Resynchronize()) return;

            if (ByteAt(_parsePos) != SyncByte)
            {
                _synced = false;
                continue;
            }

            ReadFromRing(_parsePos, packet);
            HandlePacket(packet, _parsePos);
            _parsePos += TsPacketSize;
        }
    }

    /// <summary>
    /// Finds the next plausible packet boundary after a discontinuity, confirming with a
    /// second sync byte one packet later so random 0x47 bytes inside video data are ignored.
    /// </summary>
    private bool Resynchronize()
    {
        while (_head - _parsePos >= TsPacketSize * 2L)
        {
            if (ByteAt(_parsePos) == SyncByte && ByteAt(_parsePos + TsPacketSize) == SyncByte)
            {
                _synced = true;
                return true;
            }
            _parsePos++;
        }
        return false;
    }

    private void HandlePacket(ReadOnlySpan<byte> packet, long offset)
    {
        bool payloadStart = (packet[1] & 0x40) != 0;
        int pid = ((packet[1] & 0x1F) << 8) | packet[2];
        int adaptationControl = (packet[3] >> 4) & 0x03;

        if (adaptationControl is 0 or 2 && pid != _videoPid) return;

        int payloadOffset = 4;
        bool randomAccess = false;

        if (adaptationControl is 2 or 3)
        {
            int adaptationLength = packet[4];
            if (adaptationLength > 0)
            {
                if (5 + adaptationLength > TsPacketSize) return;
                randomAccess = (packet[5] & 0x40) != 0;
            }
            payloadOffset = 5 + adaptationLength;
        }

        if (payloadOffset >= TsPacketSize) return;
        var payload = packet[payloadOffset..];

        switch (pid)
        {
            case 0:
                // PAT. With +resend_headers this immediately precedes each keyframe, so it
                // is the correct place to start a clip.
                _pendingHeaderOffset = offset;
                if (payloadStart) ParsePat(payload);
                return;

            case 0x1FFF:
                return; // null padding
        }

        if (pid == _pmtPid)
        {
            if (payloadStart) ParsePmt(payload);
            return;
        }

        if (pid != _videoPid || !payloadStart) return;

        // Every video frame advances "now", which is what makes a clip end at the moment
        // the hotkey was pressed rather than at the last keyframe.
        long pts = ParsePts(payload);
        if (pts >= 0) _lastPts = Unwrap(pts);

        if (!randomAccess)
        {
            // A non-key frame started, so any PAT we are holding belongs to the previous
            // GOP and must not be used as a clip start.
            _pendingHeaderOffset = -1;
            return;
        }

        if (pts >= 0)
        {
            // Prefer the PAT ffmpeg resent just before this keyframe; fall back to the
            // frame itself if headers were not resent.
            long start = _pendingHeaderOffset >= 0 ? _pendingHeaderOffset : offset;
            _cutPoints.Enqueue(new CutPoint(start, _lastPts));
        }

        _pendingHeaderOffset = -1;
    }

    private void ParsePat(ReadOnlySpan<byte> payload)
    {
        int pointer = payload[0];
        int p = 1 + pointer;
        if (p + 12 > payload.Length) return;
        if (payload[p] != 0x00) return; // table_id 0 = PAT

        int sectionLength = ((payload[p + 1] & 0x0F) << 8) | payload[p + 2];
        int entries = (sectionLength - 9) / 4;
        int entryStart = p + 8;

        for (int i = 0; i < entries; i++)
        {
            int e = entryStart + i * 4;
            if (e + 4 > payload.Length) return;

            int programNumber = (payload[e] << 8) | payload[e + 1];
            int pid = ((payload[e + 2] & 0x1F) << 8) | payload[e + 3];
            if (programNumber != 0) { _pmtPid = pid; return; }
        }
    }

    private void ParsePmt(ReadOnlySpan<byte> payload)
    {
        int pointer = payload[0];
        int p = 1 + pointer;
        if (p + 12 > payload.Length) return;
        if (payload[p] != 0x02) return; // table_id 2 = PMT

        int sectionLength = ((payload[p + 1] & 0x0F) << 8) | payload[p + 2];
        int programInfoLength = ((payload[p + 10] & 0x0F) << 8) | payload[p + 11];
        int cursor = p + 12 + programInfoLength;
        int end = Math.Min(p + 3 + sectionLength - 4, payload.Length);

        while (cursor + 5 <= end)
        {
            int streamType = payload[cursor];
            int elementaryPid = ((payload[cursor + 1] & 0x1F) << 8) | payload[cursor + 2];
            int esInfoLength = ((payload[cursor + 3] & 0x0F) << 8) | payload[cursor + 4];

            // 0x1B = H.264, 0x24 = HEVC, 0x33 = VVC
            if (streamType is 0x1B or 0x24 or 0x33)
            {
                if (_videoPid != elementaryPid) _videoPid = elementaryPid;
                return;
            }

            cursor += 5 + esInfoLength;
        }
    }

    /// <summary>Reads the 33-bit PTS out of a PES header, or -1 when absent.</summary>
    private static long ParsePts(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 14) return -1;
        if (payload[0] != 0x00 || payload[1] != 0x00 || payload[2] != 0x01) return -1;

        byte streamId = payload[3];
        if (streamId is < 0xE0 or > 0xEF) return -1; // video stream ids only

        if ((payload[7] & 0x80) == 0) return -1; // no PTS present

        return ((long)(payload[9] & 0x0E) << 29)
             | ((long)payload[10] << 22)
             | ((long)(payload[11] & 0xFE) << 14)
             | ((long)payload[12] << 7)
             | ((long)payload[13] >> 1);
    }

    /// <summary>Turns the 33-bit wrapping PTS into a monotonic value.</summary>
    private long Unwrap(long rawPts)
    {
        if (_rawPtsPrevious >= 0 && rawPts + PtsWrapThreshold < _rawPtsPrevious)
            _ptsOffset += PtsModulus;

        _rawPtsPrevious = rawPts;
        return rawPts + _ptsOffset;
    }

    /// <summary>
    /// Forgets cut points whose bytes have been overwritten, and those older than the
    /// retention window. Without the second rule the ring would keep advertising every
    /// keyframe still physically present - up to the full capacity, well past the duration
    /// the user asked for - which makes the reported buffer depth meaningless.
    /// </summary>
    private void TrimExpiredCutPoints()
    {
        long oldestResident = _head - _buffer.Length;
        while (_cutPoints.Count > 0 && _cutPoints.Peek().Offset < oldestResident)
            _cutPoints.Dequeue();

        if (_lastPts < 0) return;

        long horizon = _lastPts - (long)(TargetSeconds * 90000);

        // Always leave two: one to start a clip from, one to measure depth against.
        while (_cutPoints.Count > 2 && _cutPoints.Peek().Pts90k < horizon)
            _cutPoints.Dequeue();
    }

    /// <summary>
    /// Copies out the most recent <paramref name="seconds"/> of footage, starting at the
    /// newest keyframe that is at least that far back. Returns null when nothing decodable
    /// has been buffered yet.
    ///
    /// The copy happens under the lock so the capture thread cannot overwrite the region
    /// mid-read; for a 30 second 15 Mbps buffer that is a ~60 MB memcpy, tens of
    /// milliseconds, absorbed by the OS pipe buffer without dropping a frame.
    /// </summary>
    public BufferSnapshot? Snapshot(double seconds)
    {
        lock (_gate)
        {
            if (_cutPoints.Count == 0 || _lastPts < 0) return null;

            long wanted = _lastPts - (long)(seconds * 90000);
            long oldestResident = Math.Max(0, _head - _buffer.Length);

            CutPoint chosen = default;
            bool found = false;

            // Newest cut point at or before the requested start; otherwise the oldest we still have.
            foreach (var cp in _cutPoints)
            {
                if (cp.Offset < oldestResident) continue;
                if (!found) { chosen = cp; found = true; continue; }
                if (cp.Pts90k <= wanted) chosen = cp;
            }

            if (!found) return null;

            long length = _head - chosen.Offset;
            if (length <= 0 || length > _buffer.Length) return null;

            var data = new byte[length];
            ReadFromRing(chosen.Offset, data);

            double duration = (_lastPts - chosen.Pts90k) / 90000.0;
            return new BufferSnapshot(data, duration, _cutPoints.Count);
        }
    }
}
