#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using Npcify.Sdk.Motion;
using UnityEngine;

namespace Npcify.Sdk.Unity
{
    /// <summary>
    /// A rig that plays live motion (docs/realtime-motion.md). <see cref="NpcifyCharacter"/>
    /// declares the <c>motion_frames</c> capability only while its target is ready, so a
    /// character without a target keeps its prepared animation clips.
    /// </summary>
    public interface INpcifyLiveMotionTarget
    {
        /// <summary>True when the rig can play live motion now.</summary>
        bool IsReady { get; }

        /// <summary>The player whose pose the target blends over the animation each frame.</summary>
        void Bind(NpcifyLiveMotionPlayer player);
    }

    /// <summary>
    /// Factories that find a live motion target under an avatar root. The UniVRM 1.0 adapter
    /// (assembly Npcify.Sdk.Runtime.UniVrm) registers itself when UniVRM is in the project.
    /// </summary>
    public static class NpcifyLiveMotionTargets
    {
        private static readonly List<Func<GameObject, INpcifyLiveMotionTarget>> Factories =
            new List<Func<GameObject, INpcifyLiveMotionTarget>>();

        public static void Register(Func<GameObject, INpcifyLiveMotionTarget> factory)
        {
            if (factory != null && !Factories.Contains(factory))
            {
                Factories.Add(factory);
            }
        }

        public static INpcifyLiveMotionTarget Create(GameObject avatarRoot)
        {
            if (avatarRoot == null)
            {
                return null;
            }
            foreach (var factory in Factories)
            {
                var target = factory(avatarRoot);
                if (target != null)
                {
                    return target;
                }
            }
            return null;
        }

        public static Vector3 ToUnity(NpcifyVector3 value)
        {
            return new Vector3((float)value.X, (float)value.Y, (float)value.Z);
        }

        public static NpcifyVector3 FromUnity(Vector3 value)
        {
            return new NpcifyVector3(value.x, value.y, value.z);
        }

        public static Quaternion ToUnity(NpcifyQuaternion value)
        {
            return new Quaternion((float)value.X, (float)value.Y, (float)value.Z, (float)value.W);
        }
    }
}
#endif
