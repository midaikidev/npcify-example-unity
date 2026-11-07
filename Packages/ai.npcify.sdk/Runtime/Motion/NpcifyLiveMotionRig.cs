using System;
using System.Collections.Generic;

namespace Npcify.Sdk.Motion
{
    /// <summary>
    /// Maps live motion frames (glTF axes: Y up, the body faces +Z, its left is +X) onto a
    /// target rig. The rig reads its axes from the target's T-pose: up from the feet to the
    /// upper legs, left from the right to the left upper arm, and forward from the feet to the toes.
    /// So an engine's own axis convention (Unity, Unreal, a VRM 0.x model that faces -Z)
    /// needs no table. The axes come from the legs and the upper arms, which a VRM T-pose
    /// keeps straight. Rotations and positions come out in the target space of the T-pose
    /// positions, for example the UniVRM control rig root or an Unreal component.
    /// </summary>
    public sealed class NpcifyLiveMotionRig
    {
        private readonly HashSet<string> _present;
        private readonly Dictionary<string, string> _parents = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly NpcifyVector3 _left;
        private readonly NpcifyVector3 _up;
        private readonly NpcifyVector3 _forward;
        private readonly double _handedness;
        private readonly NpcifyVector3 _origin;
        private readonly NpcifyVector3 _restHips;
        private readonly double _hipsHeight;

        private NpcifyLiveMotionRig(
            HashSet<string> present,
            NpcifyVector3 left,
            NpcifyVector3 up,
            NpcifyVector3 forward,
            double handedness,
            NpcifyVector3 origin,
            NpcifyVector3 restHips)
        {
            _present = present;
            _left = left;
            _up = up;
            _forward = forward;
            _handedness = handedness;
            _origin = origin;
            _restHips = restHips;
            _hipsHeight = NpcifyVector3.Dot(restHips - origin, up);
            foreach (var bone in NpcifyMotionFrames.Bones)
            {
                if (!present.Contains(bone) || bone == "hips")
                {
                    continue;
                }
                var parent = NpcifyMotionFrames.Parents[bone];
                while (parent != null && !present.Contains(parent))
                {
                    parent = NpcifyMotionFrames.Parents.TryGetValue(parent, out var next) ? next : null;
                }
                _parents[bone] = parent;
            }
        }

        /// <summary>The target's model axes in target space.</summary>
        public NpcifyVector3 Left => _left;

        public NpcifyVector3 Up => _up;

        public NpcifyVector3 Forward => _forward;

        /// <summary>-1 when the target space is left-handed against glTF (Unity, Unreal), else 1.</summary>
        public double Handedness => _handedness;

        /// <summary>The hips height of the T-pose above the model origin, in target units.</summary>
        public double HipsHeight => _hipsHeight;

        public bool Has(string bone) => _present.Contains(bone);

        /// <summary>The nearest present VRM ancestor of a present bone, or null for the hips.</summary>
        public string ParentOf(string bone) => _parents.TryGetValue(bone, out var parent) ? parent : null;

        /// <summary>The present bones of the 22 frame bones, parents first.</summary>
        public IEnumerable<string> PresentBones
        {
            get
            {
                foreach (var bone in NpcifyMotionFrames.Bones)
                {
                    if (_present.Contains(bone))
                    {
                        yield return bone;
                    }
                }
            }
        }

        /// <summary>
        /// Builds the rig from the T-pose positions (target space) of the target's humanoid bones,
        /// keyed by VRM bone name, and the model origin (the floor under the model). It needs the
        /// hips, the head and both upper arms.
        /// </summary>
        public static NpcifyLiveMotionRig Create(IReadOnlyDictionary<string, NpcifyVector3> tPose, NpcifyVector3 origin)
        {
            if (tPose == null)
            {
                throw new ArgumentNullException(nameof(tPose));
            }
            if (!tPose.TryGetValue("hips", out var hips) || !tPose.TryGetValue("head", out var head) ||
                !tPose.TryGetValue("leftUpperArm", out var leftArm) || !tPose.TryGetValue("rightUpperArm", out var rightArm))
            {
                throw new ArgumentException("Live motion needs the hips, the head and both upper arms of the rig.");
            }
            // The legs hang straight down in a VRM T-pose; the spine can lean.
            var legs = NpcifyVector3.Zero;
            if (tPose.TryGetValue("leftUpperLeg", out var leftThigh) && tPose.TryGetValue("leftFoot", out var leftAnkle))
            {
                legs += leftThigh - leftAnkle;
            }
            if (tPose.TryGetValue("rightUpperLeg", out var rightThigh) && tPose.TryGetValue("rightFoot", out var rightAnkle))
            {
                legs += rightThigh - rightAnkle;
            }
            var up = (legs.Length > 1e-6 ? legs : head - hips).Normalized();
            var across = leftArm - rightArm;
            var left = (across - up * NpcifyVector3.Dot(across, up)).Normalized();
            if (up.Length < 0.5 || left.Length < 0.5)
            {
                throw new ArgumentException("The rig is not in a T-pose: its axes cannot be read.");
            }
            var cross = NpcifyVector3.Cross(left, up);
            // Unity and Unreal are left-handed against glTF; the toes confirm the facing direction.
            var handedness = -1.0;
            var feet = NpcifyVector3.Zero;
            var feetCount = 0;
            if (tPose.TryGetValue("leftFoot", out var leftFoot) && tPose.TryGetValue("leftToes", out var leftToes))
            {
                feet += leftToes - leftFoot;
                feetCount++;
            }
            if (tPose.TryGetValue("rightFoot", out var rightFoot) && tPose.TryGetValue("rightToes", out var rightToes))
            {
                feet += rightToes - rightFoot;
                feetCount++;
            }
            if (feetCount > 0)
            {
                var flat = feet - up * NpcifyVector3.Dot(feet, up);
                var facing = NpcifyVector3.Dot(cross, flat.Normalized());
                if (Math.Abs(facing) > 0.25)
                {
                    handedness = facing > 0 ? 1.0 : -1.0;
                }
            }
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bone in NpcifyMotionFrames.Bones)
            {
                if (tPose.ContainsKey(bone))
                {
                    present.Add(bone);
                }
            }
            return new NpcifyLiveMotionRig(present, left, up, cross * handedness, handedness, origin, hips);
        }

        /// <summary>A glTF direction in target space.</summary>
        public NpcifyVector3 ToTarget(NpcifyVector3 v) => _left * v.X + _up * v.Y + _forward * v.Z;

        /// <summary>A target-space direction in glTF axes.</summary>
        public NpcifyVector3 ToGltf(NpcifyVector3 v) =>
            new NpcifyVector3(NpcifyVector3.Dot(v, _left), NpcifyVector3.Dot(v, _up), NpcifyVector3.Dot(v, _forward));

        /// <summary>A glTF rotation in target space.</summary>
        public NpcifyQuaternion ToTarget(NpcifyQuaternion q)
        {
            var axis = ToTarget(new NpcifyVector3(q.X, q.Y, q.Z)) * _handedness;
            return new NpcifyQuaternion(axis.X, axis.Y, axis.Z, q.W);
        }

        /// <summary>
        /// The live rotation of a present bone relative to its present parent, in target space. A
        /// missing VRM bone between them gives its rotation to the bone.
        /// </summary>
        public bool TryGetLiveLocalRotation(NpcifyLiveMotionPlayer player, string bone, out NpcifyQuaternion rotation)
        {
            rotation = NpcifyQuaternion.Identity;
            if (player == null || !_present.Contains(bone) || !player.TryGetRotation(bone, out var local))
            {
                return false;
            }
            var stop = ParentOf(bone);
            var chain = local;
            var node = bone == "hips" ? null : NpcifyMotionFrames.Parents[bone];
            while (node != null && node != stop)
            {
                player.TryGetRotation(node, out var missing);
                chain = missing * chain;
                node = NpcifyMotionFrames.Parents.TryGetValue(node, out var next) ? next : null;
            }
            rotation = ToTarget(chain);
            return true;
        }

        /// <summary>
        /// The live hips position in target space: scaled by the target's rest hips height over
        /// the frames' rest hips height, anchored over the T-pose hips or at the generated root.
        /// </summary>
        public NpcifyVector3 GetLiveHipsPosition(NpcifyLiveMotionPlayer player)
        {
            var raw = player.RawHipsPosition;
            var scale = _hipsHeight / Math.Max(1e-6, player.RestHipsHeight);
            var offset = ToTarget(new NpcifyVector3(raw.X * scale, raw.Y * scale, raw.Z * scale));
            if (player.RootMode == NpcifyLiveMotionRootMode.World)
            {
                return _origin + offset;
            }
            var rest = _restHips - _origin;
            var flatRest = rest - _up * NpcifyVector3.Dot(rest, _up);
            return _origin + flatRest + offset;
        }
    }
}
