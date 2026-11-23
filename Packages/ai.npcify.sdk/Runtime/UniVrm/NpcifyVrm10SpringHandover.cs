#if UNITY_5_3_OR_NEWER && NPCIFY_UNIVRM
using System.Collections.Generic;
using UnityEngine;
using UniVRM10;
using UniVRM10.FastSpringBones;

namespace Npcify.Sdk.Unity.UniVrm
{
    /// <summary>
    /// Gives the spring state (hair, skirts, appendages) of one UniVRM 1.0 model to another at a
    /// change of appearance variant. A model that becomes active has the spring state of the moment
    /// when it left, or of its rest pose: its springs swing hard for some frames. Each spring joint
    /// of the next model that has a joint at the same path in the model that left takes the tail
    /// positions of that joint (its bend and its speed). Other joints keep their own state.
    /// Only models of the shared FastSpringBoneService (UniVRM's default) take part.
    /// </summary>
    internal static class NpcifyVrm10SpringHandover
    {
        public static int Copy(Vrm10Instance from, Vrm10Instance to)
        {
            if (from == null || to == null || from == to ||
                !(from.Runtime.SpringBone is Vrm10FastSpringboneRuntime) ||
                !(to.Runtime.SpringBone is Vrm10FastSpringboneRuntime))
            {
                return 0;
            }
            var combined = FastSpringBoneService.Instance.BufferCombiner.Combined;
            if (combined == null || !combined.TransformAccessArray.isCreated)
            {
                return 0;
            }
            var springs = combined.Springs;
            var logics = combined.Logics;
            var transforms = combined.TransformAccessArray;
            var source = new Dictionary<string, int>();
            var targets = new List<KeyValuePair<string, int>>();
            var fromRoot = from.transform;
            var toRoot = to.transform;
            for (var s = 0; s < springs.Length; s++)
            {
                var spring = springs[s];
                var center = spring.centerTransformIndex >= 0
                    ? transforms[spring.transformIndexOffset + spring.centerTransformIndex]
                    : null;
                for (var j = spring.logicSpan.startIndex; j < spring.logicSpan.EndIndex; j++)
                {
                    var head = transforms[spring.transformIndexOffset + logics[j].headTransformIndex];
                    if (head == null)
                    {
                        continue;
                    }
                    if (head.IsChildOf(fromRoot))
                    {
                        source[Key(head, center, fromRoot)] = j;
                    }
                    else if (head.IsChildOf(toRoot))
                    {
                        targets.Add(new KeyValuePair<string, int>(Key(head, center, toRoot), j));
                    }
                }
            }
            var current = combined.CurrentTails;
            var previous = combined.PrevTails;
            var next = combined.NextTails;
            var copied = 0;
            foreach (var target in targets)
            {
                if (source.TryGetValue(target.Key, out var j))
                {
                    current[target.Value] = current[j];
                    previous[target.Value] = previous[j];
                    next[target.Value] = next[j];
                    copied++;
                }
            }
            return copied;
        }

        // The joint and the space of its tails: the same path under each model root.
        private static string Key(Transform joint, Transform center, Transform root)
        {
            return Path(joint, root) + "|" + (center != null ? Path(center, root) : "<world>");
        }

        private static string Path(Transform transform, Transform root)
        {
            var parts = new List<string>();
            for (var t = transform; t != null && t != root; t = t.parent)
            {
                parts.Add(t.name);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
#endif
