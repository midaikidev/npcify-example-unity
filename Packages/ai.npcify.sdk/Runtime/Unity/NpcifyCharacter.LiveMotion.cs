#if UNITY_5_3_OR_NEWER
using System;
using System.Threading;
using System.Threading.Tasks;
using Npcify.Sdk.Motion;
using UnityEngine;

namespace Npcify.Sdk.Unity
{
    /// <summary>Live motion of NpcifyCharacter (docs/realtime-motion.md).</summary>
    public sealed partial class NpcifyCharacter
    {
        [Header("Live Motion")]
        [Tooltip("Play the generated body motion of each reply when the character has Live motion on. Off: the character keeps its prepared animation clips.")]
        [SerializeField] private bool liveMotion = true;
        [Tooltip("Anchored keeps the character where the game placed it. World applies the generated movement of the hips.")]
        [SerializeField] private NpcifyLiveMotionRootMode liveMotionRootMode = NpcifyLiveMotionRootMode.Anchored;

        private NpcifyLiveMotionOptions _liveMotionOptions = new NpcifyLiveMotionOptions();
        private INpcifyLiveMotionTarget _liveMotionTarget;
        private bool _liveMotionTargetFromGame;

        /// <summary>The live motion frames and the current live pose of this character.</summary>
        public NpcifyLiveMotionPlayer LiveMotion => Runtime.LiveMotion;

        /// <summary>
        /// The rig that plays live motion. NpcifyCharacter finds a UniVRM 1.0 model under the avatar
        /// root; set your own target for another rig, or null to find one again.
        /// </summary>
        public INpcifyLiveMotionTarget LiveMotionTarget
        {
            get => _liveMotionTarget;
            set
            {
                _liveMotionTargetFromGame = value != null;
                _liveMotionTarget = value;
                if (value == null)
                {
                    FindLiveMotionTarget();
                }
                BindLiveMotionTarget();
            }
        }

        /// <summary>True while turns ask for live motion: it is on and the rig can play it.</summary>
        public bool LiveMotionReady => liveMotion && _liveMotionTarget != null && _liveMotionTarget.IsReady;

        /// <summary>
        /// Warms up live motion for the current conversation on the player's intent. Call it when the
        /// player starts to type a reply; push-to-talk calls it itself. It runs once per
        /// conversation, does nothing without live motion, and never delays the reply.
        /// </summary>
        public Task<bool> PrepareLiveMotionAsync(CancellationToken cancellationToken = default)
        {
            EnsureInitialized();
            return _runtime.PrepareLiveMotionAsync(cancellationToken);
        }

        private NpcifyLiveMotionOptions BuildLiveMotionOptions()
        {
            return new NpcifyLiveMotionOptions
            {
                Enabled = liveMotion,
                RootMode = liveMotionRootMode,
                BlendInSeconds = _liveMotionOptions.BlendInSeconds,
                BlendOutSeconds = _liveMotionOptions.BlendOutSeconds,
            };
        }

        private void ApplyLiveMotionOptions(NpcifyLiveMotionOptions options)
        {
            if (options == null)
            {
                return;
            }
            _liveMotionOptions = options;
            liveMotion = options.Enabled;
            liveMotionRootMode = options.RootMode;
        }

        private void FindLiveMotionTarget()
        {
            if (_liveMotionTargetFromGame && _liveMotionTarget != null)
            {
                return;
            }
            _liveMotionTarget = NpcifyLiveMotionTargets.Create(Root.gameObject);
            BindLiveMotionTarget();
        }

        private void BindLiveMotionTarget()
        {
            if (_runtime != null)
            {
                _liveMotionTarget?.Bind(_runtime.LiveMotion);
            }
        }

        private void UpdateLiveMotionTarget()
        {
            _runtime.LiveMotionTargetReady = LiveMotionReady;
        }

        private async void PrepareLiveMotionOnIntent()
        {
            try
            {
                await _runtime.PrepareLiveMotionAsync();
            }
            catch (Exception)
            {
                // A failed warm-up costs the turn nothing.
            }
        }
    }
}
#endif
