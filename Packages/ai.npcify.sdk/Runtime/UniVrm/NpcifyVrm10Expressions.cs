#if UNITY_5_3_OR_NEWER && NPCIFY_UNIVRM
using System.Collections.Generic;
using Npcify.Sdk.Character;
using UnityEngine;
using UniVRM10;
using VrmOverride = UniGLTF.Extensions.VRMC_vrm.ExpressionOverrideType;

namespace Npcify.Sdk.Unity.UniVrm
{
    /// <summary>
    /// Reads the preset expressions of a UniVRM 1.0 model (<see cref="Vrm10Instance"/>) for
    /// <see cref="NpcifyVrmExpressionSources"/>: the blend shapes that the VRM file binds to each
    /// expression, "is binary" and the overrides of the blinks and of the gaze.
    /// </summary>
    internal static class NpcifyVrm10Expressions
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void RegisterReader()
        {
            NpcifyVrmExpressionSources.Register(Read);
        }

        private static IReadOnlyList<NpcifyVrmExpression> Read(SkinnedMeshRenderer renderer)
        {
            // The model can be inactive (an appearance variant that waits): search by hand.
            Vrm10Instance vrm = null;
            for (var current = renderer.transform; current != null && vrm == null; current = current.parent)
            {
                vrm = current.GetComponent<Vrm10Instance>();
            }
            if (vrm == null || vrm.Vrm == null || vrm.Vrm.Expression == null)
            {
                return null;
            }
            var path = RelativePath(vrm.transform, renderer.transform);
            var expressions = new List<NpcifyVrmExpression>();
            foreach (var (preset, clip) in vrm.Vrm.Expression.Clips)
            {
                // The mixer plays the presets only.
                if (clip == null || preset == ExpressionPreset.custom)
                {
                    continue;
                }
                var expression = new NpcifyVrmExpression
                {
                    Name = preset.ToString(),
                    IsBinary = clip.IsBinary,
                    OverrideBlink = Override(clip.OverrideBlink),
                    OverrideLookAt = Override(clip.OverrideLookAt),
                };
                var binds = clip.MorphTargetBindings;
                for (var index = 0; binds != null && index < binds.Length; index++)
                {
                    if (binds[index].RelativePath == path)
                    {
                        expression.Binds.Add(new NpcifyExpressionBind(binds[index].Index, binds[index].Weight));
                    }
                    else
                    {
                        expression.BoundOnOtherMesh = true;
                    }
                }
                expressions.Add(expression);
            }
            return expressions;
        }

        private static NpcifyExpressionOverride Override(VrmOverride value)
        {
            switch (value)
            {
                case VrmOverride.block:
                    return NpcifyExpressionOverride.Block;
                case VrmOverride.blend:
                    return NpcifyExpressionOverride.Blend;
                default:
                    return NpcifyExpressionOverride.None;
            }
        }

        // The path of a transform below the model root, as UniVRM writes it in a bind ("Body/Face").
        private static string RelativePath(Transform root, Transform target)
        {
            var path = string.Empty;
            for (var current = target; current != null && current != root; current = current.parent)
            {
                path = path.Length == 0 ? current.name : current.name + "/" + path;
            }
            return path;
        }
    }
}
#endif
