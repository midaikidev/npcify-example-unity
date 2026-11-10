#if UNITY_5_3_OR_NEWER
using System;
using System.Threading.Tasks;
using Npcify.Sdk.Sight;

namespace Npcify.Sdk.Unity
{
    /// <summary>
    /// Sight of NpcifyCharacter (docs/sight.md): the character sees a game view or a webcam. The
    /// SDK sends a frame at the interval or when the picture changes, on the conversation stream.
    /// </summary>
    public sealed partial class NpcifyCharacter
    {
        /// <summary>An observation of the current conversation: what the vision model saw in a frame.</summary>
        public event Action<NpcifySightObservation> OnSight;

        /// <summary>The sight state changed; the error is the failure that stopped sight or its start, else null.</summary>
        public event Action<NpcifySightState, NpcifySightException> OnSightState;

        /// <summary>
        /// Unavailable until the character info turns sight on (its owner's Sight tab; the info is
        /// read when a conversation starts), and in a baked conversation; then Off, Starting or On.
        /// </summary>
        public NpcifySightState SightState => _runtime != null ? _runtime.SightState : NpcifySightState.Unavailable;

        /// <summary>
        /// Lets the character see (docs/sight.md). Needs a conversation. The source is
        /// options.Camera, options.RenderTexture or options.WebCam (with the scripting define
        /// NPCIFY_SIGHT_WEBCAM); none: the screen. Completes when
        /// frames can go. A failure faults the task with an NpcifySightException and goes to
        /// OnError: SIGHT_DISABLED when sight is unavailable, CONVERSATION_REQUIRED,
        /// SIGHT_ALREADY_STARTED, a code of the source, or the code of a failed join.
        /// </summary>
        public async Task StartSight(NpcifySightOptions options = null)
        {
            EnsureInitialized();
            options = options ?? new NpcifySightOptions();
            try
            {
                await _runtime.StartSight(options, NpcifySightCapture.ForOptions(this, options));
            }
            catch (Exception error)
            {
                OnError?.Invoke(error);
                throw;
            }
        }

        /// <summary>Stops the frames and releases the source. A webcam that the SDK started stops too.</summary>
        public void StopSight()
        {
            _runtime?.StopSight();
        }

        private void BindSight()
        {
            _runtime.OnSight += observation => OnSight?.Invoke(observation);
            _runtime.OnSightState += (state, error) => OnSightState?.Invoke(state, error);
        }

        // A disabled character sends no frames: its coroutines and updates stop.
        private void OnDisable()
        {
            _runtime?.StopSight();
        }

        // No frames while the application is paused or in the background (OnApplicationPause).
        private void PauseSight(bool paused)
        {
            if (_runtime != null)
            {
                _runtime.SightHidden = paused;
            }
        }
    }
}
#endif
