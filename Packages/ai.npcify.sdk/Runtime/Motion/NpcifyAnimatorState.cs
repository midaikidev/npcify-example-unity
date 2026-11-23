using System;
using System.Collections.Generic;

namespace Npcify.Sdk.Motion
{
    /// <summary>The kind of an animator parameter.</summary>
    public enum NpcifyAnimatorParameterKind
    {
        Float,
        Int,
        Bool,
    }

    /// <summary>The value of one animator parameter.</summary>
    public struct NpcifyAnimatorParameter
    {
        public int Hash;
        public NpcifyAnimatorParameterKind Kind;
        public float FloatValue;
        public int IntValue;
        public bool BoolValue;
    }

    /// <summary>The state of one animator layer: its state, its time and a crossfade in progress.</summary>
    public struct NpcifyAnimatorLayer
    {
        public float Weight;
        public int StateHash;
        /// <summary>The normalized time of the state (1 = one loop of its clip).</summary>
        public float StateTime;
        public bool InTransition;
        public int NextStateHash;
        public float NextStateTime;
        /// <summary>The length of one loop of the next state, in seconds.</summary>
        public float NextStateLength;
        /// <summary>The progress of the crossfade, 0 to 1.</summary>
        public float TransitionTime;
        /// <summary>The length of the crossfade, in seconds when <see cref="FixedDuration"/>, else normalized.</summary>
        public float TransitionDuration;
        public bool FixedDuration;
    }

    /// <summary>The animator that takes the state: the next model of a change of appearance variant.</summary>
    public interface INpcifyAnimatorTarget
    {
        int LayerCount { get; }
        bool HasParameter(int hash, NpcifyAnimatorParameterKind kind);
        void SetSpeed(float speed);
        void SetParameter(NpcifyAnimatorParameter parameter);
        void SetLayerWeight(int layer, float weight);
        bool HasState(int layer, int stateHash);
        void Play(int layer, int stateHash, float normalizedTime);
        /// <summary>Starts a crossfade from the current state of the layer to the next state.</summary>
        void CrossFade(int layer, NpcifyAnimatorLayer state);
        /// <summary>Applies the calls before this one and writes the pose now, without time passing.</summary>
        void Evaluate();
    }

    /// <summary>
    /// The state of an animator at one moment: its speed, its parameters and the state, time and
    /// crossfade of each layer. A change of appearance variant gives it to the animator of the next
    /// model, so that the change changes the model only (docs/performance-protocol.md).
    /// </summary>
    public sealed class NpcifyAnimatorState
    {
        public float Speed = 1f;
        public readonly List<NpcifyAnimatorParameter> Parameters = new List<NpcifyAnimatorParameter>();
        public readonly List<NpcifyAnimatorLayer> Layers = new List<NpcifyAnimatorLayer>();

        /// <summary>
        /// Puts this state on <paramref name="target"/>: the parameters that it has (triggers are
        /// not kept), the layer weights, the state and time of each layer that has the same state,
        /// and then the crossfades in progress at their progress. Returns the layers that took the
        /// state.
        /// </summary>
        public int ApplyTo(INpcifyAnimatorTarget target)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            target.SetSpeed(Speed);
            foreach (var parameter in Parameters)
            {
                if (target.HasParameter(parameter.Hash, parameter.Kind))
                {
                    target.SetParameter(parameter);
                }
            }
            var count = Math.Min(Layers.Count, target.LayerCount);
            var applied = new bool[count];
            var applies = 0;
            for (var layer = 0; layer < count; layer++)
            {
                var state = Layers[layer];
                if (layer > 0)
                {
                    target.SetLayerWeight(layer, state.Weight);
                }
                if (!target.HasState(layer, state.StateHash))
                {
                    continue;
                }
                target.Play(layer, state.StateHash, state.StateTime);
                applied[layer] = true;
                applies++;
            }
            if (applies == 0)
            {
                return 0;
            }
            // A crossfade starts from the state that plays: the plays must apply first.
            target.Evaluate();
            var fades = false;
            for (var layer = 0; layer < count; layer++)
            {
                var state = Layers[layer];
                if (applied[layer] && state.InTransition && target.HasState(layer, state.NextStateHash))
                {
                    target.CrossFade(layer, state);
                    fades = true;
                }
            }
            if (fades)
            {
                target.Evaluate();
            }
            return applies;
        }
    }
}
