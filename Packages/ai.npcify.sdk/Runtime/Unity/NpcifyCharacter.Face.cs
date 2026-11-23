#if UNITY_5_3_OR_NEWER
using System.Collections.Generic;
using Npcify.Sdk.Character;
using UnityEngine;

namespace Npcify.Sdk.Unity
{
    /// <summary>The face of NpcifyCharacter: expressions, gaze, blinks and blend shapes.</summary>
    public sealed partial class NpcifyCharacter
    {
        private readonly Dictionary<Mesh, NpcifyBlendShapePlan> _blendShapePlans =
            new Dictionary<Mesh, NpcifyBlendShapePlan>();
        private readonly List<string> _blendShapeNames = new List<string>();
        // The Animator state of the last frame (layer 0), for the blink at a new animation.
        private int _animatorState;
        private bool _animatorStateKnown;
        // The head bone of the last frame: another bone (the next model) is no turn of the head.
        private Transform _trackedHead;

        public void SetEmotion(string expression, float weight = 1f)
        {
            EnsureInitialized();
            _runtime.Expressions.SetEmotion(expression, weight);
        }

        public void ClearEmotion()
        {
            EnsureInitialized();
            _runtime.Expressions.ClearEmotion();
        }

        public void PlayExpressionCue(
            string expression,
            float weight = 1f,
            float durationSeconds = 1.2f,
            float fadeInSeconds = 0.15f,
            float fadeOutSeconds = 0.25f
        )
        {
            EnsureInitialized();
            _runtime.Expressions.PlayCue(expression, weight, durationSeconds, fadeInSeconds, fadeOutSeconds);
        }

        /// <summary>
        /// Tells the face that the body starts the animation <paramref name="animationId"/>, for
        /// example the return to idle that the game plays by itself: people blink when they start
        /// another movement. <see cref="PlayAnimation"/> and a new state of the Animator do this.
        /// </summary>
        public void NoteAnimationStarted(string animationId)
        {
            EnsureInitialized();
            _runtime.NoteAnimationStarted(animationId);
        }

        /// <summary>
        /// Asks for a blink because the character starts a movement. <paramref name="chance"/> is
        /// the part of such movements that bring a blink. Returns whether a blink starts.
        /// </summary>
        public bool RequestBlink(float chance = 1f)
        {
            EnsureInitialized();
            return _runtime.Expressions.RequestBlink(chance);
        }

        public void LookAt(Transform target)
        {
            EnsureInitialized();
            gazeTarget = target;
            _hasLookAtPosition = false;
            if (target == null)
            {
                _runtime.ClearLookAt();
            }
        }

        public void LookAt(Vector3 worldPosition)
        {
            EnsureInitialized();
            gazeTarget = null;
            _lookAtWorldPosition = worldPosition;
            _hasLookAtPosition = true;
            UpdateGazeLayer();
        }

        public void ClearLookAt()
        {
            EnsureInitialized();
            gazeTarget = null;
            _hasLookAtPosition = false;
            _runtime.ClearLookAt();
        }

        // People blink when they turn the head fast or start another movement. This watches the
        // head bone and the Animator after the animation of the frame.
        private void WatchBodyForBlinks()
        {
            if (_runtime == null || !autoBlink)
            {
                _animatorStateKnown = false;
                return;
            }
            var head = GetBone(HumanBodyBones.Head);
            if (head != _trackedHead)
            {
                _trackedHead = head;
                _runtime.Expressions.IdleBlinks.RestartHead();
            }
            if (head != null)
            {
                var rotation = head.rotation;
                _runtime.Expressions.TrackHead(rotation.x, rotation.y, rotation.z, rotation.w);
            }
            if (animatorComponent == null || !animatorComponent.isActiveAndEnabled ||
                animatorComponent.runtimeAnimatorController == null || animatorComponent.layerCount == 0)
            {
                _animatorStateKnown = false;
                return;
            }
            var state = animatorComponent.IsInTransition(0)
                ? animatorComponent.GetNextAnimatorStateInfo(0)
                : animatorComponent.GetCurrentAnimatorStateInfo(0);
            // The first state after a load brings no blink.
            if (_animatorStateKnown && state.fullPathHash != _animatorState)
            {
                var idle = state.shortNameHash == Animator.StringToHash(_runtime.IdleAnimationId);
                _runtime.Expressions.RequestBlink(
                    idle ? NpcifyIdleBlinks.IdleReturnBlinkChance : NpcifyIdleBlinks.ActionBlinkChance);
            }
            _animatorState = state.fullPathHash;
            _animatorStateKnown = true;
        }

        private void UpdateGazeLayer()
        {
            if (gazeTarget == null && !_hasLookAtPosition)
            {
                _runtime.ClearLookAt();
                return;
            }

            var targetWorldPos = gazeTarget != null ? gazeTarget.position : _lookAtWorldPosition;
            var headTransform = GetBone(HumanBodyBones.Head) ?? Root;
            var localDir = headTransform.InverseTransformPoint(targetWorldPos);
            _runtime.LookAtDirection(localDir.x, localDir.y, localDir.z);
        }

        private void ApplyBlendShapesToSkinnedMeshes()
        {
            if (_runtime == null || _skinnedMeshRenderers.Count == 0)
            {
                return;
            }

            for (var rIdx = 0; rIdx < _skinnedMeshRenderers.Count; rIdx++)
            {
                var renderer = _skinnedMeshRenderers[rIdx];
                if (renderer == null || renderer.sharedMesh == null)
                {
                    continue;
                }

                var mesh = renderer.sharedMesh;
                var count = mesh.blendShapeCount;
                if (!_blendShapePlans.TryGetValue(mesh, out var plan) || plan.Count != count)
                {
                    _blendShapeNames.Clear();
                    for (var bIdx = 0; bIdx < count; bIdx++)
                    {
                        _blendShapeNames.Add(mesh.GetBlendShapeName(bIdx));
                    }
                    // With the expressions of the VRM file, each expression plays the shapes
                    // that the file binds to it; without them, the alias names of the shapes.
                    plan = NpcifyBlendShapePlan.Create(_blendShapeNames, NpcifyVrmExpressionSources.Read(renderer));
                    _blendShapePlans[mesh] = plan;
                }
                for (var bIdx = 0; bIdx < count; bIdx++)
                {
                    renderer.SetBlendShapeWeight(bIdx, plan.Weight(bIdx, _runtime.Expressions) * 100f);
                }
            }
        }
    }
}
#endif
