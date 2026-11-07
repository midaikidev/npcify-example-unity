using System;
using System.Collections.Generic;

namespace Npcify.Sdk.Motion
{
    /// <summary>
    /// How the game uses the root of live motion. Anchored (default) removes the horizontal hips
    /// movement of the turn, so the character stays where the game placed it; height and turns
    /// stay. World applies the root as generated.
    /// </summary>
    public enum NpcifyLiveMotionRootMode
    {
        Anchored,
        World,
    }

    [Serializable]
    public sealed class NpcifyLiveMotionOptions
    {
        /// <summary>
        /// Declare <c>motion_frames</c> and play live motion when the character has it on. Off: the
        /// character keeps its prepared animation clips.
        /// </summary>
        public bool Enabled = true;
        public NpcifyLiveMotionRootMode RootMode = NpcifyLiveMotionRootMode.Anchored;
        /// <summary>Seconds to blend from idle into live frames.</summary>
        public double BlendInSeconds = 0.3;
        /// <summary>Seconds to blend from live frames back to idle.</summary>
        public double BlendOutSeconds = 0.5;
    }

    /// <summary>Counters of the live frames of one staging, for logs and tests.</summary>
    public sealed class NpcifyLiveMotionStats
    {
        public string StagingId = string.Empty;
        /// <summary>Distinct frames received for the staging (events and stored frames).</summary>
        public int FramesReceived;
        /// <summary>Updates that applied a live frame of the staging.</summary>
        public int FramesPlayed;
        /// <summary>Staging time in seconds of the first applied live frame, or -1.</summary>
        public double FirstFrameTime = -1;
        public long LastRevision = -1;
    }

    /// <summary>
    /// Keeps the live motion frames of each staging and samples the pose of the staging clock,
    /// like the Web SDK's LiveMotionPlayer (ADR 0011). Engine adapters read the sampled pose with
    /// <see cref="TryGetRotation"/> and <see cref="RawHipsPosition"/>, and blend it over their
    /// animation with <see cref="Weight"/>. Use it from one thread.
    /// </summary>
    public sealed class NpcifyLiveMotionPlayer
    {
        /// <summary>Keep at most this many stagings of frames.</summary>
        public const int MaxTracks = 4;

        private sealed class Frame
        {
            public long Revision;
            public float[] Values;
            public int Offset;
        }

        private sealed class Track
        {
            public string StagingId;
            public double Fps;
            public string[] Bones;
            public Dictionary<string, int> Slots;
            public double RestHipsHeight;
            public int Width;
            public readonly Dictionary<long, Frame> Frames = new Dictionary<long, Frame>();
            public bool HasOrigin;
            public double OriginX;
            public double OriginZ;
            public readonly NpcifyLiveMotionStats Stats = new NpcifyLiveMotionStats();
        }

        private readonly Dictionary<string, Track> _tracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        private readonly List<string> _order = new List<string>();
        private readonly NpcifyLiveMotionOptions _options;
        private Track _poseTrack;
        private float[] _poseValues;
        private int _poseOffset;
        private float[] _scratch = Array.Empty<float>();
        private double _weight;

        public NpcifyLiveMotionPlayer(NpcifyLiveMotionOptions options = null)
        {
            _options = options ?? new NpcifyLiveMotionOptions();
        }

        public NpcifyLiveMotionOptions Options => _options;

        public NpcifyLiveMotionRootMode RootMode => _options.RootMode;

        /// <summary>The live weight, 0 (idle) to 1 (live frames).</summary>
        public double Weight => _weight;

        /// <summary>True when a live pose blends over the animation now.</summary>
        public bool HasPose => _poseTrack != null && _weight > 0;

        /// <summary>The rest hips height of the frames of the current pose, in meters.</summary>
        public double RestHipsHeight => _poseTrack?.RestHipsHeight ?? 1.0;

        /// <summary>The staging of the current pose, or null.</summary>
        public string PoseStagingId => _poseTrack?.StagingId;

        /// <summary>Raised with the staging ID and its staging time when its first live frame plays.</summary>
        public event Action<string, double> FirstFramePlayed;

        /// <summary>Adds the frames of a motion_frames event. Later revisions replace earlier frames.</summary>
        public bool Push(NpcifyMotionFramesEvent motionEvent)
        {
            if (motionEvent == null || string.IsNullOrEmpty(motionEvent.StagingId) || motionEvent.Bones == null ||
                motionEvent.Values == null || motionEvent.Fps <= 0 ||
                motionEvent.Values.Length != motionEvent.FrameCount * NpcifyMotionFrames.FrameWidth(motionEvent.Bones.Length))
            {
                return false;
            }
            var track = GetOrCreate(motionEvent.StagingId, motionEvent.Fps, motionEvent.Bones, motionEvent.RestHipsHeight);
            for (var frame = 0; frame < motionEvent.FrameCount; frame++)
            {
                var index = motionEvent.StartFrame + frame;
                if (track.Frames.TryGetValue(index, out var existing))
                {
                    if (existing.Revision > motionEvent.Revision)
                    {
                        continue;
                    }
                }
                else
                {
                    track.Stats.FramesReceived++;
                }
                track.Frames[index] = new Frame
                {
                    Revision = motionEvent.Revision,
                    Values = motionEvent.Values,
                    Offset = frame * track.Width,
                };
            }
            track.Stats.LastRevision = Math.Max(track.Stats.LastRevision, motionEvent.Revision);
            return true;
        }

        /// <summary>Loads the stored frames of a completed staging (a replay of timeline.motion).</summary>
        public void Load(string stagingId, StagingMotionTrack stored, byte[] bytes)
        {
            if (string.IsNullOrEmpty(stagingId) || stored == null || _tracks.ContainsKey(stagingId))
            {
                return;
            }
            var values = NpcifyMotionFrames.Read(bytes ?? stored.FramesData, stored.FrameCount, stored.Bones.Length);
            var track = GetOrCreate(stagingId, stored.Fps, stored.Bones, stored.RestHipsHeight);
            for (var frame = 0; frame < stored.FrameCount; frame++)
            {
                track.Frames[frame] = new Frame { Revision = long.MaxValue, Values = values, Offset = frame * track.Width };
            }
            track.Stats.FramesReceived = stored.FrameCount;
        }

        public bool Has(string stagingId)
        {
            return stagingId != null && _tracks.TryGetValue(stagingId, out var track) && track.Frames.Count > 0;
        }

        public void Forget(string stagingId)
        {
            if (stagingId != null && _tracks.Remove(stagingId))
            {
                _order.Remove(stagingId);
            }
        }

        /// <summary>Forgets every staging and ends the live pose at once.</summary>
        public void Clear()
        {
            _tracks.Clear();
            _order.Clear();
            _poseTrack = null;
            _poseValues = null;
            _weight = 0;
        }

        /// <summary>The counters of a staging, or null when it has no frames.</summary>
        public NpcifyLiveMotionStats GetStats(string stagingId)
        {
            if (stagingId == null || !_tracks.TryGetValue(stagingId, out var track))
            {
                return null;
            }
            var stats = track.Stats;
            return new NpcifyLiveMotionStats
            {
                StagingId = stats.StagingId,
                FramesReceived = stats.FramesReceived,
                FramesPlayed = stats.FramesPlayed,
                FirstFrameTime = stats.FirstFrameTime,
                LastRevision = stats.LastRevision,
            };
        }

        /// <summary>
        /// Samples the pose of <paramref name="stagingId"/> at <paramref name="time"/> seconds of its
        /// staging clock and moves the weight; a null time means that no staging plays now.
        /// </summary>
        public void Update(double deltaSeconds, string stagingId, double? time)
        {
            Track track = null;
            var sampled = false;
            if (stagingId != null && time.HasValue && time.Value >= 0 && _tracks.TryGetValue(stagingId, out track))
            {
                sampled = Sample(track, time.Value);
            }
            var delta = Math.Max(0, deltaSeconds);
            _weight = Approach(_weight, sampled ? 1 : 0, delta, sampled ? _options.BlendInSeconds : _options.BlendOutSeconds);
            if (sampled)
            {
                if (track.Stats.FramesPlayed == 0)
                {
                    track.Stats.FirstFrameTime = time.Value;
                    FirstFramePlayed?.Invoke(track.StagingId, time.Value);
                }
                track.Stats.FramesPlayed++;
            }
            else if (_weight <= 0)
            {
                // Keep the last pose while the weight blends out of it.
                _poseTrack = null;
                _poseValues = null;
            }
        }

        /// <summary>The rotation of a bone in the current pose (glTF space); identity for a bone that the frames lack.</summary>
        public bool TryGetRotation(string bone, out NpcifyQuaternion rotation)
        {
            rotation = NpcifyQuaternion.Identity;
            if (_poseTrack == null || _poseValues == null)
            {
                return false;
            }
            if (_poseTrack.Slots.TryGetValue(bone, out var slot))
            {
                var offset = _poseOffset + 3 + slot * 4;
                rotation = new NpcifyQuaternion(
                    _poseValues[offset],
                    _poseValues[offset + 1],
                    _poseValues[offset + 2],
                    _poseValues[offset + 3]).Normalized();
            }
            return true;
        }

        /// <summary>
        /// The hips position of the current pose in meters (glTF space). In anchored mode the
        /// horizontal position of the turn's first sampled frame is subtracted.
        /// </summary>
        public NpcifyVector3 RawHipsPosition
        {
            get
            {
                if (_poseTrack == null || _poseValues == null)
                {
                    return new NpcifyVector3(0, RestHipsHeight, 0);
                }
                var x = (double)_poseValues[_poseOffset];
                var y = (double)_poseValues[_poseOffset + 1];
                var z = (double)_poseValues[_poseOffset + 2];
                if (_options.RootMode == NpcifyLiveMotionRootMode.Anchored && _poseTrack.HasOrigin)
                {
                    x -= _poseTrack.OriginX;
                    z -= _poseTrack.OriginZ;
                }
                return new NpcifyVector3(x, y, z);
            }
        }

        private Track GetOrCreate(string stagingId, double fps, string[] bones, double restHipsHeight)
        {
            if (_tracks.TryGetValue(stagingId, out var track))
            {
                return track;
            }
            track = new Track
            {
                StagingId = stagingId,
                Fps = fps,
                Bones = (string[])bones.Clone(),
                Slots = new Dictionary<string, int>(StringComparer.Ordinal),
                RestHipsHeight = restHipsHeight > 0 ? restHipsHeight : 1.0,
                Width = NpcifyMotionFrames.FrameWidth(bones.Length),
            };
            track.Stats.StagingId = stagingId;
            for (var slot = 0; slot < bones.Length; slot++)
            {
                track.Slots[bones[slot]] = slot;
            }
            _tracks[stagingId] = track;
            _order.Add(stagingId);
            while (_order.Count > MaxTracks)
            {
                var oldest = _order[0];
                if (oldest == stagingId)
                {
                    break;
                }
                _order.RemoveAt(0);
                _tracks.Remove(oldest);
            }
            return track;
        }

        private bool Sample(Track track, double time)
        {
            var position = time * track.Fps;
            var first = (long)Math.Floor(position);
            if (!track.Frames.TryGetValue(first, out var a))
            {
                return false;
            }
            if (!track.HasOrigin)
            {
                track.HasOrigin = true;
                track.OriginX = a.Values[a.Offset];
                track.OriginZ = a.Values[a.Offset + 2];
            }
            var amount = position - first;
            _poseTrack = track;
            if (amount <= 0 || !track.Frames.TryGetValue(first + 1, out var b))
            {
                _poseValues = a.Values;
                _poseOffset = a.Offset;
                return true;
            }
            if (_scratch.Length != track.Width)
            {
                _scratch = new float[track.Width];
            }
            for (var index = 0; index < 3; index++)
            {
                _scratch[index] = (float)(a.Values[a.Offset + index] * (1 - amount) + b.Values[b.Offset + index] * amount);
            }
            for (var offset = 3; offset < track.Width; offset += 4)
            {
                var from = new NpcifyQuaternion(
                    a.Values[a.Offset + offset], a.Values[a.Offset + offset + 1],
                    a.Values[a.Offset + offset + 2], a.Values[a.Offset + offset + 3]);
                var to = new NpcifyQuaternion(
                    b.Values[b.Offset + offset], b.Values[b.Offset + offset + 1],
                    b.Values[b.Offset + offset + 2], b.Values[b.Offset + offset + 3]);
                var q = NpcifyQuaternion.Slerp(from, to, amount);
                _scratch[offset] = (float)q.X;
                _scratch[offset + 1] = (float)q.Y;
                _scratch[offset + 2] = (float)q.Z;
                _scratch[offset + 3] = (float)q.W;
            }
            _poseValues = _scratch;
            _poseOffset = 0;
            return true;
        }

        private static double Approach(double value, double target, double delta, double seconds)
        {
            if (seconds <= 0)
            {
                return target;
            }
            var step = delta / seconds;
            return value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);
        }
    }
}
