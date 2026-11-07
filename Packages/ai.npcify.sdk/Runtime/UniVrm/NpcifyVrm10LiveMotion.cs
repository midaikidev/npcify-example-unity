#if UNITY_5_3_OR_NEWER && NPCIFY_UNIVRM
using System;
using System.Collections.Generic;
using Npcify.Sdk.Motion;
using UniGLTF.Utils;
using UnityEngine;
using UniVRM10;

namespace Npcify.Sdk.Unity.UniVrm
{
    /// <summary>
    /// Plays live motion on a UniVRM 1.0 model with a control rig. UniVRM's runtime update
    /// copies the control rig (the normalized humanoid) to the bones; this component wraps the
    /// model's <see cref="Vrm10Runtime.VrmAnimation"/>, so that the live pose blends over the
    /// animation (a VRMA, or the Animator's pose on the control rig) in that update, before the
    /// copy, the constraints and the spring bones. It runs before <see cref="Vrm10Instance"/>
    /// (execution order 11000) and wraps an animation that the game sets again. NpcifyCharacter
    /// adds it to a model with a Vrm10Instance.
    /// </summary>
    /// <remarks>
    /// UniVRM keeps the bone world rotations of the first <see cref="Vrm10Instance.Runtime"/>
    /// access as the T-pose. Access it once right after loading, before the model is rotated or
    /// animated. The model needs <c>ControlRigGenerationOption.Generate</c>.
    /// </remarks>
    [DefaultExecutionOrder(10999)]
    [DisallowMultipleComponent]
    [AddComponentMenu("Npcify/Npcify VRM 1.0 Live Motion")]
    public sealed class NpcifyVrm10LiveMotion : MonoBehaviour, INpcifyLiveMotionTarget
    {
        private static readonly Dictionary<string, HumanBodyBones> UnityBones = new Dictionary<string, HumanBodyBones>(StringComparer.Ordinal)
        {
            ["hips"] = HumanBodyBones.Hips,
            ["spine"] = HumanBodyBones.Spine,
            ["chest"] = HumanBodyBones.Chest,
            ["upperChest"] = HumanBodyBones.UpperChest,
            ["neck"] = HumanBodyBones.Neck,
            ["head"] = HumanBodyBones.Head,
            ["leftShoulder"] = HumanBodyBones.LeftShoulder,
            ["leftUpperArm"] = HumanBodyBones.LeftUpperArm,
            ["leftLowerArm"] = HumanBodyBones.LeftLowerArm,
            ["leftHand"] = HumanBodyBones.LeftHand,
            ["rightShoulder"] = HumanBodyBones.RightShoulder,
            ["rightUpperArm"] = HumanBodyBones.RightUpperArm,
            ["rightLowerArm"] = HumanBodyBones.RightLowerArm,
            ["rightHand"] = HumanBodyBones.RightHand,
            ["leftUpperLeg"] = HumanBodyBones.LeftUpperLeg,
            ["leftLowerLeg"] = HumanBodyBones.LeftLowerLeg,
            ["leftFoot"] = HumanBodyBones.LeftFoot,
            ["leftToes"] = HumanBodyBones.LeftToes,
            ["rightUpperLeg"] = HumanBodyBones.RightUpperLeg,
            ["rightLowerLeg"] = HumanBodyBones.RightLowerLeg,
            ["rightFoot"] = HumanBodyBones.RightFoot,
            ["rightToes"] = HumanBodyBones.RightToes,
        };

        private Vrm10Instance _vrm;
        private Vrm10RuntimeControlRig _controlRig;
        private Transform _rigRoot;
        private NpcifyLiveMotionRig _rig;
        private NpcifyLiveMotionPlayer _player;
        private LiveAnimation _animation;
        private bool _warned;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterFactory()
        {
            NpcifyLiveMotionTargets.Register(Create);
        }

        private static INpcifyLiveMotionTarget Create(GameObject avatarRoot)
        {
            var vrm = avatarRoot.GetComponentInParent<Vrm10Instance>();
            if (vrm == null)
            {
                vrm = avatarRoot.GetComponentInChildren<Vrm10Instance>(true);
            }
            if (vrm == null)
            {
                return null;
            }
            var existing = vrm.GetComponent<NpcifyVrm10LiveMotion>();
            return existing != null ? existing : vrm.gameObject.AddComponent<NpcifyVrm10LiveMotion>();
        }

        /// <summary>True after the first update found the control rig and read its T-pose.</summary>
        public bool IsReady => _rig != null && _player != null;

        public void Bind(NpcifyLiveMotionPlayer player)
        {
            _player = player;
        }

        private void Awake()
        {
            _vrm = GetComponent<Vrm10Instance>();
            _animation = new LiveAnimation(this);
        }

        private void Update()
        {
            Install();
        }

        private void LateUpdate()
        {
            Install();
        }

        private void OnDisable()
        {
            // Give the game its animation back; never make the runtime here (the T-pose trap).
            if (_controlRig != null && _vrm != null && ReferenceEquals(_vrm.Runtime.VrmAnimation, _animation))
            {
                _vrm.Runtime.VrmAnimation = _animation.Inner;
            }
        }

        private void Install()
        {
            if (_vrm == null || !_vrm.isActiveAndEnabled)
            {
                return;
            }
            var runtime = _vrm.Runtime;
            if (runtime.ControlRig == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning("[Npcify] Live motion needs a UniVRM control rig (ControlRigGenerationOption.Generate). The character keeps its prepared animation clips.", this);
                }
                return;
            }
            if (_rig == null && !BuildRig(runtime.ControlRig))
            {
                return;
            }
            if (!ReferenceEquals(runtime.VrmAnimation, _animation))
            {
                _animation.Inner = runtime.VrmAnimation;
                runtime.VrmAnimation = _animation;
            }
        }

        private bool BuildRig(Vrm10RuntimeControlRig controlRig)
        {
            var hips = controlRig.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null || hips.parent == null)
            {
                return false;
            }
            var tPose = (ITPoseProvider)controlRig;
            var positions = new Dictionary<string, NpcifyVector3>(StringComparer.Ordinal);
            foreach (var pair in UnityBones)
            {
                var transform = tPose.GetWorldTransform(pair.Value);
                if (transform.HasValue && controlRig.GetBoneTransform(pair.Value) != null)
                {
                    positions[pair.Key] = NpcifyLiveMotionTargets.FromUnity(transform.Value.Translation);
                }
            }
            _rigRoot = hips.parent;
            var origin = NpcifyLiveMotionTargets.FromUnity(_rigRoot.InverseTransformPoint(_vrm.transform.position));
            try
            {
                _rig = NpcifyLiveMotionRig.Create(positions, origin);
            }
            catch (ArgumentException error)
            {
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning("[Npcify] Live motion is off for this model: " + error.Message, this);
                }
                return false;
            }
            if (Quaternion.Angle(_rigRoot.localRotation, Quaternion.identity) > 1f && !_warned)
            {
                _warned = true;
                Debug.LogWarning("[Npcify] UniVRM made the control rig while the model was rotated. Access Vrm10Instance.Runtime before the model gets its rotation.", this);
            }
            _controlRig = controlRig;
            return true;
        }

        /// <summary>The game's animation with the live pose blended over it.</summary>
        private sealed class LiveAnimation : IVrm10Animation, INormalizedPoseProvider, ITPoseProvider
        {
            private static readonly Dictionary<ExpressionKey, Func<float>> NoExpressions = new Dictionary<ExpressionKey, Func<float>>();
            private readonly NpcifyVrm10LiveMotion _owner;
            private readonly Dictionary<HumanBodyBones, Quaternion> _live = new Dictionary<HumanBodyBones, Quaternion>();
            private int _frame = -1;
            private float _weight;
            private Vector3 _liveHips;

            public LiveAnimation(NpcifyVrm10LiveMotion owner)
            {
                _owner = owner;
            }

            public IVrm10Animation Inner;

            public (INormalizedPoseProvider, ITPoseProvider) ControlRig => (this, this);

            public IReadOnlyDictionary<ExpressionKey, Func<float>> ExpressionMap => Inner?.ExpressionMap ?? NoExpressions;

            public LookAtInput? LookAt => Inner?.LookAt;

            public void ShowBoxMan(bool enable)
            {
                Inner?.ShowBoxMan(enable);
            }

            public void SetBoxManMaterial(Material material)
            {
                Inner?.SetBoxManMaterial(material);
            }

            public void Dispose()
            {
                // The inner animation belongs to the game.
            }

            Vector3 INormalizedPoseProvider.GetRawHipsPosition()
            {
                Refresh();
                var basePosition = BaseHips();
                return _weight > 0 ? Vector3.Lerp(basePosition, _liveHips, _weight) : basePosition;
            }

            Quaternion INormalizedPoseProvider.GetNormalizedLocalRotation(HumanBodyBones bone, HumanBodyBones parentBone)
            {
                Refresh();
                Quaternion baseRotation;
                if (Inner != null)
                {
                    baseRotation = Inner.ControlRig.Item1.GetNormalizedLocalRotation(bone, parentBone);
                }
                else
                {
                    var control = _owner._controlRig?.GetBoneTransform(bone);
                    baseRotation = control != null ? control.localRotation : Quaternion.identity;
                }
                return _weight > 0 && _live.TryGetValue(bone, out var live)
                    ? Quaternion.Slerp(baseRotation, live, _weight)
                    : baseRotation;
            }

            EuclideanTransform? ITPoseProvider.GetWorldTransform(HumanBodyBones bone)
            {
                return _owner._controlRig != null ? ((ITPoseProvider)_owner._controlRig).GetWorldTransform(bone) : null;
            }

            private Vector3 BaseHips()
            {
                var controlRig = _owner._controlRig;
                var sinkHips = ((ITPoseProvider)controlRig).GetWorldTransform(HumanBodyBones.Hips).Value.Translation;
                if (Inner != null)
                {
                    var (pose, tPose) = Inner.ControlRig;
                    var sourceHips = tPose.GetWorldTransform(HumanBodyBones.Hips).Value.Translation;
                    var scaling = Mathf.Abs(sourceHips.y) > 1e-6f ? sinkHips.y / sourceHips.y : 1f;
                    return sinkHips + (pose.GetRawHipsPosition() - sourceHips) * scaling;
                }
                var hips = controlRig.GetBoneTransform(HumanBodyBones.Hips);
                return _owner._rigRoot.InverseTransformPoint(hips.position);
            }

            private void Refresh()
            {
                if (_frame == Time.frameCount)
                {
                    return;
                }
                _frame = Time.frameCount;
                var player = _owner._player;
                var rig = _owner._rig;
                _live.Clear();
                _weight = player != null && rig != null && player.HasPose ? (float)player.Weight : 0f;
                if (_weight <= 0)
                {
                    return;
                }
                foreach (var bone in rig.PresentBones)
                {
                    if (rig.TryGetLiveLocalRotation(player, bone, out var rotation))
                    {
                        _live[UnityBones[bone]] = NpcifyLiveMotionTargets.ToUnity(rotation);
                    }
                }
                _liveHips = NpcifyLiveMotionTargets.ToUnity(rig.GetLiveHipsPosition(player));
            }
        }
    }
}
#endif
