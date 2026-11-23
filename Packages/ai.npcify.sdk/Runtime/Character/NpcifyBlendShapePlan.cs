using System;
using System.Collections.Generic;

namespace Npcify.Sdk.Character
{
    /// <summary>What an expression does to the blinks or to the gaze while it plays (VRM 1.0, override).</summary>
    public enum NpcifyExpressionOverride
    {
        None = 0,
        /// <summary>The others do not play while this expression has a weight.</summary>
        Block = 1,
        /// <summary>The others play less by the weight of this expression.</summary>
        Blend = 2,
    }

    /// <summary>One blend shape that an expression of a VRM file plays.</summary>
    public struct NpcifyExpressionBind
    {
        public int BlendShapeIndex;
        /// <summary>0 to 1.</summary>
        public float Weight;

        public NpcifyExpressionBind(int blendShapeIndex, float weight)
        {
            BlendShapeIndex = blendShapeIndex;
            Weight = weight;
        }
    }

    /// <summary>
    /// One expression of a VRM file (VRMC_vrm.expressions, or a VRM 0 blend shape group) for one
    /// mesh. The host reads it from the file and gives the list to <see cref="NpcifyBlendShapePlan"/>.
    /// </summary>
    public sealed class NpcifyVrmExpression
    {
        /// <summary>
        /// The name of the expression: a VRM 1 preset ("happy", "aa") or a VRM 0 preset ("joy",
        /// "a"). The plan does not play an expression with another name (a custom expression).
        /// </summary>
        public string Name;
        /// <summary>The blend shapes of this mesh that the expression plays.</summary>
        public List<NpcifyExpressionBind> Binds = new List<NpcifyExpressionBind>();
        /// <summary>
        /// The expression has binds on another mesh of the model. An expression with binds on any
        /// mesh plays by its binds only; the alias names do not play it.
        /// </summary>
        public bool BoundOnOtherMesh;
        public bool IsBinary;
        public NpcifyExpressionOverride OverrideBlink;
        public NpcifyExpressionOverride OverrideLookAt;
    }

    /// <summary>
    /// What each blend shape of one mesh plays. A face can have more than one shape for the same
    /// lids: a generated face has <c>blink</c>, <c>blinkLeft</c>, <c>blinkRight</c> and the ARKit
    /// <c>eyeBlinkLeft</c> and <c>eyeBlinkRight</c>; a VRoid face has <c>Fcl_EYE_Close</c> and its
    /// two sides. Each of them closes the lids by itself, so together they close them two or three
    /// times and the lids go through each other. The plan plays one shape for each lid: the
    /// both-eyes shape for the part that both lids share, and one shape of each side for the rest.
    /// A shape with the exact expression name comes first (the shape that the VRM's own expression
    /// binds), then the aliases in the order of the alias table. Every other shape plays as
    /// <see cref="NpcifyFacialMixer.ResolveBlendShapeWeight01"/> gives it.
    ///
    /// A plan with the expressions of the VRM file plays as the Web SDK does: each expression
    /// plays the blend shapes that the file binds to it, with the weights of the file. An
    /// expression that has binds does not play by alias names. An expression without binds (for
    /// example the gaze of a face whose eyes are bones) keeps the alias names. The file's
    /// "is binary" and its overrides of the blinks and of the gaze apply to the bound expressions.
    /// A file that binds the two side blinks but not "blink" plays a blink on the two sides.
    /// </summary>
    public sealed class NpcifyBlendShapePlan
    {
        private enum Role : byte
        {
            Other,
            Both,
            Left,
            Right,
            Muted,
        }

        private struct Bound
        {
            public int Expression;
            public float Weight;
        }

        private struct Rule
        {
            /// <summary>The expression has binds on a mesh of the model.</summary>
            public bool Bound;
            public bool IsBinary;
            public NpcifyExpressionOverride OverrideBlink;
            public NpcifyExpressionOverride OverrideLookAt;
        }

        private const int BlinkIndex = 11;
        private const int BlinkLeftIndex = 12;
        private const int BlinkRightIndex = 13;
        private const int LookUpIndex = 14;

        private static readonly string[] BothNames = { "blink", "fcl_eye_close" };
        private static readonly string[] LeftNames = { "blinkleft", "blink_l", "fcl_eye_close_l", "eyeblinkleft" };
        private static readonly string[] RightNames = { "blinkright", "blink_r", "fcl_eye_close_r", "eyeblinkright" };

        private readonly string[] _names;
        private readonly Role[] _roles;
        // The expression that the alias name of each shape plays; -1: none.
        private readonly int[] _aliasExpressions;
        private readonly bool _hasBoth;
        // The binds of the VRM file for each shape; null without the expressions of a file.
        private Bound[][] _bound;
        private readonly Rule[] _rules = new Rule[NpcifyFacialMixer.ExpressionCount];
        private bool _hasExpressions;
        private bool _hasOverrides;

        private NpcifyBlendShapePlan(string[] names, Role[] roles, bool hasBoth)
        {
            _names = names;
            _roles = roles;
            _hasBoth = hasBoth;
            _aliasExpressions = new int[names.Length];
            for (var index = 0; index < names.Length; index++)
            {
                _aliasExpressions[index] = NpcifyFacialMixer.ExpressionIndex(names[index]);
            }
        }

        /// <summary>The number of blend shapes of the mesh.</summary>
        public int Count => _names.Length;

        /// <summary>The plan of a mesh with the blend shapes <paramref name="names"/>, in their order.</summary>
        public static NpcifyBlendShapePlan Create(IReadOnlyList<string> names)
        {
            var count = names?.Count ?? 0;
            var copy = new string[count];
            var roles = new Role[count];
            for (var index = 0; index < count; index++)
            {
                copy[index] = names[index] ?? string.Empty;
            }
            var both = Choose(copy, BothNames, roles, Role.Both);
            Choose(copy, LeftNames, roles, Role.Left);
            Choose(copy, RightNames, roles, Role.Right);
            return new NpcifyBlendShapePlan(copy, roles, both);
        }

        /// <summary>
        /// The plan of a mesh with the blend shapes <paramref name="names"/> and the expressions
        /// of its VRM file.
        /// </summary>
        public static NpcifyBlendShapePlan Create(IReadOnlyList<string> names, IReadOnlyList<NpcifyVrmExpression> expressions)
        {
            var plan = Create(names);
            if (expressions == null)
            {
                return plan;
            }
            var bound = new List<Bound>[plan._names.Length];
            for (var e = 0; e < expressions.Count; e++)
            {
                var expression = expressions[e];
                // The mixer plays the presets only: a custom expression of the file has no weight.
                var index = expression == null ? -1 : NpcifyFacialMixer.PresetIndex(expression.Name);
                if (index < 0)
                {
                    continue;
                }
                plan._hasExpressions = true;
                var rule = plan._rules[index];
                rule.IsBinary = rule.IsBinary || expression.IsBinary;
                rule.Bound = rule.Bound || expression.BoundOnOtherMesh;
                if (expression.OverrideBlink != NpcifyExpressionOverride.None)
                {
                    rule.OverrideBlink = expression.OverrideBlink;
                }
                if (expression.OverrideLookAt != NpcifyExpressionOverride.None)
                {
                    rule.OverrideLookAt = expression.OverrideLookAt;
                }
                var binds = expression.Binds;
                for (var b = 0; binds != null && b < binds.Count; b++)
                {
                    // A bind on a shape that the mesh does not have still makes the expression a
                    // bound one: the file binds it, so the alias names do not play it.
                    rule.Bound = true;
                    var shape = binds[b].BlendShapeIndex;
                    if (shape >= 0 && shape < bound.Length)
                    {
                        (bound[shape] ?? (bound[shape] = new List<Bound>())).Add(
                            new Bound { Expression = index, Weight = Clamp01(binds[b].Weight) });
                    }
                }
                plan._rules[index] = rule;
            }
            plan._bound = new Bound[bound.Length][];
            for (var index = 0; index < bound.Length; index++)
            {
                plan._bound[index] = bound[index] == null ? Array.Empty<Bound>() : bound[index].ToArray();
            }
            for (var index = 0; index < plan._rules.Length; index++)
            {
                var rule = plan._rules[index];
                var overridesBlink = rule.OverrideBlink != NpcifyExpressionOverride.None && !IsBlink(index);
                var overridesLook = rule.OverrideLookAt != NpcifyExpressionOverride.None && !IsLook(index);
                plan._hasOverrides = plan._hasOverrides || overridesBlink || overridesLook;
            }
            return plan;
        }

        /// <summary>The weight (0 to 1) of the blend shape at <paramref name="index"/>.</summary>
        public float Weight(int index, NpcifyFacialMixer mixer)
        {
            if (mixer == null || index < 0 || index >= _names.Length)
            {
                return 0f;
            }
            if (!_hasExpressions)
            {
                return AliasWeight(index, mixer);
            }
            var weight = 0f;
            var binds = _bound[index];
            for (var b = 0; b < binds.Length; b++)
            {
                weight += BoundWeight(binds[b].Expression, mixer) * binds[b].Weight;
            }
            // The alias name plays only an expression that the file does not bind.
            var role = _roles[index];
            if (role == Role.Other)
            {
                var alias = _aliasExpressions[index];
                if (alias >= 0 && !_rules[alias].Bound)
                {
                    weight += AliasWeight(index, mixer);
                }
            }
            else if (role != Role.Muted &&
                !_rules[BlinkIndex].Bound && !_rules[BlinkLeftIndex].Bound && !_rules[BlinkRightIndex].Bound)
            {
                weight += AliasWeight(index, mixer);
            }
            return Clamp01(weight);
        }

        private float AliasWeight(int index, NpcifyFacialMixer mixer)
        {
            var role = _roles[index];
            if (role == Role.Other)
            {
                return mixer.GetWeightAt(_aliasExpressions[index]);
            }
            if (role == Role.Muted)
            {
                return 0f;
            }
            var blink = mixer.GetWeightAt(BlinkIndex);
            var left = Math.Max(blink, mixer.GetWeightAt(BlinkLeftIndex));
            var right = Math.Max(blink, mixer.GetWeightAt(BlinkRightIndex));
            var shared = _hasBoth ? Math.Min(left, right) : 0f;
            switch (role)
            {
                case Role.Both:
                    return shared;
                case Role.Left:
                    return Clamp01(left - shared);
                default:
                    return Clamp01(right - shared);
            }
        }

        private float BoundWeight(int expression, NpcifyFacialMixer mixer)
        {
            var weight = mixer.GetWeightAt(expression);
            // A file without binds for "blink" plays a blink on the two sides.
            if ((expression == BlinkLeftIndex || expression == BlinkRightIndex) && !_rules[BlinkIndex].Bound)
            {
                weight = Math.Max(weight, mixer.GetWeightAt(BlinkIndex));
            }
            if (_rules[expression].IsBinary)
            {
                weight = weight > 0.5f ? 1f : 0f;
            }
            if (_hasOverrides && weight > 0f)
            {
                if (IsBlink(expression))
                {
                    weight *= OverrideFactor(true, mixer);
                }
                else if (IsLook(expression))
                {
                    weight *= OverrideFactor(false, mixer);
                }
            }
            return weight;
        }

        // VRM 1.0: an expression that overrides the blinks (or the gaze) blocks them while it has
        // a weight, or takes its own weight from them.
        private float OverrideFactor(bool blink, NpcifyFacialMixer mixer)
        {
            var factor = 1f;
            for (var index = 0; index < _rules.Length; index++)
            {
                var rule = _rules[index];
                var mode = blink ? rule.OverrideBlink : rule.OverrideLookAt;
                if (mode == NpcifyExpressionOverride.None || (blink ? IsBlink(index) : IsLook(index)))
                {
                    continue;
                }
                var weight = mixer.GetWeightAt(index);
                if (rule.IsBinary)
                {
                    weight = weight > 0.5f ? 1f : 0f;
                }
                if (mode == NpcifyExpressionOverride.Block)
                {
                    if (weight > 0f)
                    {
                        return 0f;
                    }
                }
                else
                {
                    factor *= 1f - Clamp01(weight);
                }
            }
            return factor;
        }

        private static bool IsBlink(int index)
        {
            return index >= BlinkIndex && index <= BlinkRightIndex;
        }

        private static bool IsLook(int index)
        {
            return index >= LookUpIndex && index < NpcifyFacialMixer.ExpressionCount;
        }

        // Gives the best shape of the names `aliases` the role `role` and mutes the other shapes
        // of those names. Returns whether the mesh has one.
        private static bool Choose(string[] names, string[] aliases, Role[] roles, Role role)
        {
            var best = -1;
            var bestRank = int.MaxValue;
            for (var index = 0; index < names.Length; index++)
            {
                var rank = Array.IndexOf(aliases, names[index].Trim().ToLowerInvariant());
                if (rank < 0)
                {
                    continue;
                }
                roles[index] = Role.Muted;
                if (rank < bestRank)
                {
                    best = index;
                    bestRank = rank;
                }
            }
            if (best < 0)
            {
                return false;
            }
            roles[best] = role;
            return true;
        }

        private static float Clamp01(float value)
        {
            return value < 0f ? 0f : value > 1f ? 1f : value;
        }
    }
}
