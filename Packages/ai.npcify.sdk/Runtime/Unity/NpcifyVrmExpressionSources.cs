#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using Npcify.Sdk.Character;
using UnityEngine;

namespace Npcify.Sdk.Unity
{
    /// <summary>
    /// Readers that give the expressions of the VRM file for one mesh, so that the face plays the
    /// blend shapes that the file binds to each expression (<see cref="NpcifyBlendShapePlan"/>).
    /// The UniVRM 1.0 adapter (assembly Npcify.Sdk.Runtime.UniVrm) registers itself when UniVRM is
    /// in the project. A game with another VRM loader can register its own reader. Without a
    /// reader, or for a model without expressions, the face plays by the alias names of the shapes.
    /// </summary>
    public static class NpcifyVrmExpressionSources
    {
        private static readonly List<Func<SkinnedMeshRenderer, IReadOnlyList<NpcifyVrmExpression>>> Readers =
            new List<Func<SkinnedMeshRenderer, IReadOnlyList<NpcifyVrmExpression>>>();

        public static void Register(Func<SkinnedMeshRenderer, IReadOnlyList<NpcifyVrmExpression>> reader)
        {
            if (reader != null && !Readers.Contains(reader))
            {
                Readers.Add(reader);
            }
        }

        /// <summary>The expressions of the VRM file for the mesh of <paramref name="renderer"/>; null: none.</summary>
        public static IReadOnlyList<NpcifyVrmExpression> Read(SkinnedMeshRenderer renderer)
        {
            if (renderer == null)
            {
                return null;
            }
            foreach (var reader in Readers)
            {
                var expressions = reader(renderer);
                if (expressions != null && expressions.Count > 0)
                {
                    return expressions;
                }
            }
            return null;
        }
    }
}
#endif
