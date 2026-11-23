#if UNITY_5_3_OR_NEWER
using Npcify.Sdk.Motion;
using UnityEngine;

namespace Npcify.Sdk.Unity
{
    /// <summary>
    /// Gives the state of one Animator to another: a change of appearance variant changes the
    /// model, and the next model continues the animation of the previous one (the state, its time
    /// and a crossfade in progress). An Animator that becomes active starts again in its default
    /// state; without this the pose jumps at the change.
    /// </summary>
    public static class NpcifyAnimatorHandover
    {
        /// <summary>The state of an active Animator with a controller, or null.</summary>
        public static NpcifyAnimatorState Capture(Animator animator)
        {
            if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null ||
                !animator.isInitialized)
            {
                return null;
            }
            var state = new NpcifyAnimatorState { Speed = animator.speed };
            foreach (var parameter in animator.parameters)
            {
                if (animator.IsParameterControlledByCurve(parameter.nameHash))
                {
                    continue;
                }
                switch (parameter.type)
                {
                    case AnimatorControllerParameterType.Float:
                        state.Parameters.Add(new NpcifyAnimatorParameter
                        {
                            Hash = parameter.nameHash,
                            Kind = NpcifyAnimatorParameterKind.Float,
                            FloatValue = animator.GetFloat(parameter.nameHash),
                        });
                        break;
                    case AnimatorControllerParameterType.Int:
                        state.Parameters.Add(new NpcifyAnimatorParameter
                        {
                            Hash = parameter.nameHash,
                            Kind = NpcifyAnimatorParameterKind.Int,
                            IntValue = animator.GetInteger(parameter.nameHash),
                        });
                        break;
                    case AnimatorControllerParameterType.Bool:
                        state.Parameters.Add(new NpcifyAnimatorParameter
                        {
                            Hash = parameter.nameHash,
                            Kind = NpcifyAnimatorParameterKind.Bool,
                            BoolValue = animator.GetBool(parameter.nameHash),
                        });
                        break;
                }
            }
            for (var layer = 0; layer < animator.layerCount; layer++)
            {
                var current = animator.GetCurrentAnimatorStateInfo(layer);
                var entry = new NpcifyAnimatorLayer
                {
                    Weight = layer == 0 ? 1f : animator.GetLayerWeight(layer),
                    StateHash = current.fullPathHash,
                    StateTime = current.normalizedTime,
                };
                if (animator.IsInTransition(layer))
                {
                    var next = animator.GetNextAnimatorStateInfo(layer);
                    var transition = animator.GetAnimatorTransitionInfo(layer);
                    entry.InTransition = true;
                    entry.NextStateHash = next.fullPathHash;
                    entry.NextStateTime = next.normalizedTime;
                    entry.NextStateLength = next.length;
                    entry.TransitionTime = transition.normalizedTime;
                    entry.TransitionDuration = transition.duration;
                    entry.FixedDuration = transition.durationUnit == DurationUnit.Fixed;
                }
                state.Layers.Add(entry);
            }
            return state;
        }

        /// <summary>
        /// Puts <paramref name="state"/> on an active Animator and writes its pose now. Returns
        /// false when the Animator has none of the states.
        /// </summary>
        public static bool Apply(NpcifyAnimatorState state, Animator animator)
        {
            if (state == null || animator == null || !animator.isActiveAndEnabled ||
                animator.runtimeAnimatorController == null)
            {
                return false;
            }
            return state.ApplyTo(new Target(animator)) > 0;
        }

        private sealed class Target : INpcifyAnimatorTarget
        {
            private readonly Animator _animator;

            public Target(Animator animator)
            {
                _animator = animator;
            }

            public int LayerCount => _animator.layerCount;

            public bool HasParameter(int hash, NpcifyAnimatorParameterKind kind)
            {
                foreach (var parameter in _animator.parameters)
                {
                    if (parameter.nameHash == hash)
                    {
                        return !_animator.IsParameterControlledByCurve(hash) && parameter.type == ToUnity(kind);
                    }
                }
                return false;
            }

            public void SetSpeed(float speed)
            {
                _animator.speed = speed;
            }

            public void SetParameter(NpcifyAnimatorParameter parameter)
            {
                switch (parameter.Kind)
                {
                    case NpcifyAnimatorParameterKind.Float:
                        _animator.SetFloat(parameter.Hash, parameter.FloatValue);
                        break;
                    case NpcifyAnimatorParameterKind.Int:
                        _animator.SetInteger(parameter.Hash, parameter.IntValue);
                        break;
                    case NpcifyAnimatorParameterKind.Bool:
                        _animator.SetBool(parameter.Hash, parameter.BoolValue);
                        break;
                }
            }

            public void SetLayerWeight(int layer, float weight)
            {
                _animator.SetLayerWeight(layer, weight);
            }

            public bool HasState(int layer, int stateHash)
            {
                return _animator.HasState(layer, stateHash);
            }

            public void Play(int layer, int stateHash, float normalizedTime)
            {
                _animator.Play(stateHash, layer, normalizedTime);
            }

            public void CrossFade(int layer, NpcifyAnimatorLayer state)
            {
                if (state.FixedDuration)
                {
                    _animator.CrossFadeInFixedTime(
                        state.NextStateHash,
                        state.TransitionDuration,
                        layer,
                        state.NextStateTime * state.NextStateLength,
                        state.TransitionTime);
                }
                else
                {
                    _animator.CrossFade(
                        state.NextStateHash,
                        state.TransitionDuration,
                        layer,
                        state.NextStateTime,
                        state.TransitionTime);
                }
            }

            public void Evaluate()
            {
                _animator.Update(0f);
            }

            private static AnimatorControllerParameterType ToUnity(NpcifyAnimatorParameterKind kind)
            {
                switch (kind)
                {
                    case NpcifyAnimatorParameterKind.Int:
                        return AnimatorControllerParameterType.Int;
                    case NpcifyAnimatorParameterKind.Bool:
                        return AnimatorControllerParameterType.Bool;
                    default:
                        return AnimatorControllerParameterType.Float;
                }
            }
        }
    }
}
#endif
