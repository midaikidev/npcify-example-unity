using System;

namespace Npcify.Sdk.Character
{
    /// <summary>
    /// The idle blinks of a face by the rules of the Web SDK and the rendered video
    /// (<c>IdleFaceTimeline</c> in <c>packages/vrm-renderer/src/idle-face.ts</c>; README of that
    /// package, "Template-face playback"). The numbers are the same; only the clock and the random
    /// numbers differ. Times are in seconds, angles in degrees.
    ///
    /// The numbers follow the blinks of people. A person at rest blinks about 17 times a minute, at
    /// irregular intervals with a long tail, and more often while speaking. The lids close faster
    /// than they open. A blink goes with a large gaze shift, with a turn of the head and with the
    /// start of a new movement.
    /// </summary>
    public sealed class NpcifyIdleBlinks
    {
        /// <summary>The middle interval between the starts of two blinks at rest.</summary>
        public const double MedianIntervalSeconds = 3.2;
        /// <summary>No interval is shorter than this.</summary>
        public const double MinIntervalSeconds = 1.2;
        /// <summary>No interval is longer than this.</summary>
        public const double MaxIntervalSeconds = 9;
        /// <summary>The shape of the interval distribution (log-logistic): larger is more regular.</summary>
        private const double IntervalShape = 4;
        /// <summary>While the character speaks, the intervals are this much shorter.</summary>
        public const double SpeakingFactor = 0.75;
        /// <summary>The lids close in 80 ms, stay closed for 50 ms and open in 170 ms.</summary>
        public const double BlinkSeconds = 0.3;
        private const double CloseSeconds = 0.08;
        private const double HoldSeconds = 0.05;
        private const double OpenSeconds = BlinkSeconds - CloseSeconds - HoldSeconds;
        /// <summary>The lids open fast at first and slowly at the end.</summary>
        private const double OpenEase = 1.7;
        /// <summary>One blink takes 0.85 to 1.25 times the usual length.</summary>
        private const double ShortestScale = 0.85;
        private const double LongestScale = 1.25;
        /// <summary>The part of the blinks that a second blink follows at once.</summary>
        public const double DoubleChance = 0.12;
        /// <summary>The second blink starts 80 to 200 ms after the end of the first.</summary>
        private const double DoubleMinGapSeconds = 0.08;
        private const double DoubleMaxGapSeconds = 0.2;
        private const double DoubleScale = 0.85;
        /// <summary>A blink that goes with a movement is a little faster.</summary>
        private const double ReflexScale = 0.9;
        /// <summary>After the end of a blink, no movement starts a new one for this long.</summary>
        public const double RefractorySeconds = 0.6;
        /// <summary>Requests less than this apart count as one movement: one decision for all.</summary>
        private const double ReflexWindowSeconds = 0.5;
        /// <summary>
        /// A shift of the gaze or a turn of the head is measured against the direction of a moment
        /// ago (a copy that follows with this time constant): a slow change stays small, a fast one
        /// shows nearly its full size.
        /// </summary>
        private const double ShiftLagSeconds = 0.3;
        /// <summary>A shift below this brings no blink.</summary>
        public const double ShiftMinDegrees = 8;
        /// <summary>A shift of this or more always brings a blink; the chance rises evenly between.</summary>
        public const double ShiftSureDegrees = 30;
        /// <summary>Below this, a movement has ended and the next one counts anew.</summary>
        private const double ShiftRestDegrees = 3;
        /// <summary>After a longer gap of the clock, the schedule starts again from now.</summary>
        private const double MaximumCatchUpSeconds = 30;
        /// <summary>After a longer gap, the gaze and the head are taken as they are, without a shift.</summary>
        private const double MaximumShiftGapSeconds = 1;

        /// <summary>The part of the new animations that bring a blink.</summary>
        public const float ActionBlinkChance = 0.9f;
        /// <summary>The part of the returns to the idle animation that bring a blink.</summary>
        public const float IdleReturnBlinkChance = 0.6f;

        private struct ScheduledBlink
        {
            public double Start;
            public double Scale;
            /// <summary>A second blink follows this one at once.</summary>
            public bool Double;
        }

        /// <summary>The watch of one direction (the gaze or the head) for a fast, large shift.</summary>
        private sealed class ShiftWatch
        {
            public double Time = double.NaN;
            /// <summary>The chance that the movement in progress asked with, 0 at rest.</summary>
            public double Asked;

            public void Restart(double time)
            {
                Time = time;
                Asked = 0;
            }
        }

        private readonly Func<double> _random;
        private double _previousTime = double.NaN;
        private ScheduledBlink _next = new ScheduledBlink { Start = double.NaN, Scale = 1 };
        private double _blinkStart = double.NaN;
        private double _blinkScale = 1;
        private double _lastBlinkEnd = double.NegativeInfinity;
        private bool _held;
        private bool _speaking;
        private double _reflexTime = double.NegativeInfinity;
        private double _reflexChance;

        private readonly ShiftWatch _gazeWatch = new ShiftWatch();
        private readonly ShiftWatch _headWatch = new ShiftWatch();
        private double _pastYaw;
        private double _pastPitch;
        private double _pastX;
        private double _pastY;
        private double _pastZ;
        private double _pastW = 1;

        /// <summary>A schedule on the numbers of <paramref name="random"/> (each in [0, 1)); null: a new random seed.</summary>
        public NpcifyIdleBlinks(Func<double> random = null)
        {
            _random = random ?? CreateSeededRandom(Guid.NewGuid().ToString("N"));
        }

        /// <summary>
        /// The weight of the lids at <paramref name="time"/> seconds after the start of a blink that
        /// takes <paramref name="scale"/> times the usual length. The lids close on an S curve, stay
        /// closed, then open fast at first and slowly at the end.
        /// </summary>
        public static double BlinkWeight(double time, double scale = 1)
        {
            var local = time / scale;
            if (!(local >= 0) || local >= BlinkSeconds)
            {
                return 0;
            }
            if (local < CloseSeconds)
            {
                return SmoothStep(local / CloseSeconds);
            }
            if (local < CloseSeconds + HoldSeconds)
            {
                return 1;
            }
            var opened = (local - CloseSeconds - HoldSeconds) / OpenSeconds;
            return 1 - SmoothStep(1 - Math.Pow(1 - opened, OpenEase));
        }

        /// <summary>
        /// The interval to the next blink for a random number in [0, 1): 0.5 gives the middle
        /// interval, small numbers short intervals and numbers near 1 the rare long ones (a
        /// log-logistic distribution inside the two limits).
        /// </summary>
        public static double BlinkInterval(double random)
        {
            var odds = random / Math.Max(1 - random, 1e-9);
            var interval = MedianIntervalSeconds * Math.Pow(odds, 1 / IntervalShape);
            return Math.Min(MaxIntervalSeconds, Math.Max(MinIntervalSeconds, interval));
        }

        /// <summary>The chance of a blink with a gaze shift or a head turn of <paramref name="degrees"/>.</summary>
        public static double ShiftChance(double degrees)
        {
            return Clamp01((degrees - ShiftMinDegrees) / (ShiftSureDegrees - ShiftMinDegrees));
        }

        /// <summary>
        /// A deterministic random stream (mulberry32 on an FNV-1a hash of the UTF-16 units of the
        /// seed): the same seed gives the same numbers in [0, 1) as <c>createSeededRandom</c> of
        /// @npcify/vrm-renderer, on every machine.
        /// </summary>
        public static Func<double> CreateSeededRandom(string seed)
        {
            unchecked
            {
                var state = 2166136261u;
                var text = seed ?? string.Empty;
                for (var index = 0; index < text.Length; index++)
                {
                    state ^= text[index];
                    state *= 16777619u;
                }
                return () =>
                {
                    unchecked
                    {
                        state += 0x6D2B79F5u;
                        var value = state;
                        value = (value ^ (value >> 15)) * (value | 1u);
                        value ^= value + (value ^ (value >> 7)) * (value | 61u);
                        return (value ^ (value >> 14)) / 4294967296.0;
                    }
                };
            }
        }

        /// <summary>Starts the schedule again at the next sample.</summary>
        public void Reset()
        {
            _previousTime = double.NaN;
            _next = new ScheduledBlink { Start = double.NaN, Scale = 1 };
            _blinkStart = double.NaN;
            _lastBlinkEnd = double.NegativeInfinity;
            _reflexTime = double.NegativeInfinity;
            _reflexChance = 0;
            _gazeWatch.Restart(double.NaN);
            _headWatch.Restart(double.NaN);
        }

        /// <summary>
        /// The weight of the lids at <paramref name="time"/> seconds. While a cue drives a blink
        /// expression (<paramref name="held"/>), a blink that is due is skipped, a blink that plays
        /// ends at once and the weight is 0; the schedule goes on. While the character speaks, the
        /// intervals are shorter. A clock that goes back (or jumps more than 30 s) starts the
        /// schedule again.
        /// </summary>
        public double Sample(double time, bool held = false, bool speaking = false)
        {
            _held = held;
            _speaking = speaking;
            if (double.IsNaN(time) || double.IsInfinity(time))
            {
                return 0;
            }
            if (double.IsNaN(_previousTime) || time < _previousTime || time - _next.Start > MaximumCatchUpSeconds)
            {
                Reset();
                _next = BlinkAfter(new ScheduledBlink { Start = time, Scale = 1 });
            }
            _previousTime = time;
            return BlinkAt(time, held);
        }

        /// <summary>
        /// Asks for a blink at <paramref name="time"/> because the character starts a movement: a new
        /// animation, a large gaze shift or a turn of the head. <paramref name="chance"/> is the part of
        /// such movements that bring a blink. No blink starts while one plays, within 0.6 s after one,
        /// or while a cue drives a blink expression. Requests less than half a second apart count as
        /// one movement: together they bring a blink with the largest of their chances. The next idle
        /// blink then comes an interval after this one. Returns whether a blink starts.
        /// </summary>
        public bool RequestBlink(double time, double chance = 1)
        {
            if (double.IsNaN(time) || double.IsInfinity(time) ||
                _held ||
                !double.IsNaN(_blinkStart) ||
                time - _lastBlinkEnd < RefractorySeconds)
            {
                return false;
            }
            if (time < _reflexTime || time - _reflexTime > ReflexWindowSeconds)
            {
                _reflexChance = 0;
            }
            _reflexTime = time;
            var wanted = Clamp01(chance);
            if (wanted <= _reflexChance)
            {
                return false;
            }
            // The part of the chance that the decisions before did not use. A sure blink takes no
            // number from the stream.
            var remaining = (wanted - _reflexChance) / (1 - _reflexChance);
            _reflexChance = wanted;
            if (remaining < 1 && _random() >= remaining)
            {
                return false;
            }
            _blinkStart = time;
            _blinkScale = ReflexScale;
            _next = BlinkAfter(new ScheduledBlink { Start = time, Scale = 1 });
            return true;
        }

        /// <summary>
        /// Takes the gaze (the direction of the eyes, <paramref name="yaw"/> and
        /// <paramref name="pitch"/> in degrees) at <paramref name="time"/>. A fast and large shift
        /// of the gaze asks for a blink (<see cref="ShiftChance"/>).
        /// </summary>
        public void WatchGaze(double time, double yaw, double pitch)
        {
            var delta = Advance(_gazeWatch, time);
            if (double.IsNaN(delta))
            {
                _pastYaw = yaw;
                _pastPitch = pitch;
                return;
            }
            WatchShift(_gazeWatch, time, Hypot(yaw - _pastYaw, pitch - _pastPitch));
            var follow = 1 - Math.Exp(-delta / ShiftLagSeconds);
            _pastYaw += (yaw - _pastYaw) * follow;
            _pastPitch += (pitch - _pastPitch) * follow;
        }

        /// <summary>
        /// Takes the rotation of the head in the world at <paramref name="time"/> (the four numbers
        /// of a quaternion). A fast and large turn of the head asks for a blink, as a gaze shift
        /// does. Call it in each frame.
        /// </summary>
        public void TrackHead(double time, double x, double y, double z, double w)
        {
            var delta = Advance(_headWatch, time);
            if (double.IsNaN(delta))
            {
                _pastX = x;
                _pastY = y;
                _pastZ = z;
                _pastW = w;
                return;
            }
            var dot = _pastX * x + _pastY * y + _pastZ * z + _pastW * w;
            var degrees = 2 * Math.Acos(Math.Min(1, Math.Abs(dot))) * 180 / Math.PI;
            WatchShift(_headWatch, time, degrees);
            // The copy follows the head: a normalized blend along the short way.
            var follow = 1 - Math.Exp(-delta / ShiftLagSeconds);
            var sign = dot < 0 ? -1 : 1;
            var nx = _pastX + (x * sign - _pastX) * follow;
            var ny = _pastY + (y * sign - _pastY) * follow;
            var nz = _pastZ + (z * sign - _pastZ) * follow;
            var nw = _pastW + (w * sign - _pastW) * follow;
            var length = Math.Sqrt(nx * nx + ny * ny + nz * nz + nw * nw);
            if (!(length > 0))
            {
                length = 1;
            }
            _pastX = nx / length;
            _pastY = ny / length;
            _pastZ = nz / length;
            _pastW = nw / length;
        }

        /// <summary>
        /// Takes the next rotation of the head as it is, without a turn: call it when the head is
        /// another bone, for example on the next model of an appearance variant, whose head bone
        /// can have another rest rotation.
        /// </summary>
        public void RestartHead()
        {
            _headWatch.Restart(double.NaN);
        }

        /// <summary>The time since the watch's sample before, or NaN when the watch starts (again).</summary>
        private static double Advance(ShiftWatch watch, double time)
        {
            if (double.IsNaN(time) || double.IsInfinity(time))
            {
                return double.NaN;
            }
            var delta = time - watch.Time;
            if (!(delta >= 0) || delta > MaximumShiftGapSeconds)
            {
                watch.Restart(time);
                return double.NaN;
            }
            watch.Time = time;
            return delta;
        }

        /// <summary>
        /// One movement asks for a blink with the chance of its largest shift. It asks again while
        /// the shift grows (each time only the new part of the chance counts), so the blink starts
        /// early in a large movement. A movement that stays fast asks no more.
        /// </summary>
        private void WatchShift(ShiftWatch watch, double time, double degrees)
        {
            if (degrees < ShiftRestDegrees)
            {
                watch.Asked = 0;
                return;
            }
            var chance = ShiftChance(degrees);
            if (chance > watch.Asked)
            {
                watch.Asked = chance;
                RequestBlink(time, chance);
            }
        }

        private double BlinkAt(double time, bool held)
        {
            if (!double.IsNaN(_blinkStart))
            {
                var end = _blinkStart + BlinkSeconds * _blinkScale;
                if (!held && time < end)
                {
                    return BlinkWeight(time - _blinkStart, _blinkScale);
                }
                // The blink ended, or a cue took the lids.
                _lastBlinkEnd = Math.Min(time, end);
                _blinkStart = double.NaN;
            }
            while (time >= _next.Start)
            {
                var blink = _next;
                var end = blink.Start + BlinkSeconds * blink.Scale;
                _next = BlinkAfter(blink);
                if (time >= end)
                {
                    // The blink ended before this sample (a long frame).
                    _lastBlinkEnd = end;
                    continue;
                }
                if (held)
                {
                    continue;
                }
                _blinkStart = blink.Start;
                _blinkScale = blink.Scale;
                return BlinkWeight(time - blink.Start, blink.Scale);
            }
            return 0;
        }

        /// <summary>The blink after <paramref name="blink"/>. Three numbers for each: the stream stays aligned.</summary>
        private ScheduledBlink BlinkAfter(ScheduledBlink blink)
        {
            var wait = _random();
            var isDouble = _random() < DoubleChance;
            var scale = ShortestScale + (LongestScale - ShortestScale) * _random();
            if (blink.Double)
            {
                return new ScheduledBlink
                {
                    Start = blink.Start + BlinkSeconds * blink.Scale + DoubleMinGapSeconds +
                        (DoubleMaxGapSeconds - DoubleMinGapSeconds) * wait,
                    Scale = scale * DoubleScale,
                    Double = false,
                };
            }
            var pace = _speaking ? SpeakingFactor : 1;
            return new ScheduledBlink
            {
                Start = blink.Start + Math.Max(MinIntervalSeconds, BlinkInterval(wait) * pace),
                Scale = scale,
                Double = isDouble,
            };
        }

        private static double Hypot(double x, double y)
        {
            return Math.Sqrt(x * x + y * y);
        }

        private static double SmoothStep(double value)
        {
            return value * value * (3 - 2 * value);
        }

        private static double Clamp01(double value)
        {
            return value <= 0 ? 0 : value >= 1 ? 1 : value;
        }
    }
}
