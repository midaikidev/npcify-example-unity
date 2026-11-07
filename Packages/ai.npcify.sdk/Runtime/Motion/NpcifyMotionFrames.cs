using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace Npcify.Sdk.Motion
{
    /// <summary>
    /// One <c>motion_frames</c> stream event of NPCP 1.1 (docs/realtime-motion.md). Frame
    /// <c>StartFrame + i</c> plays at <c>(StartFrame + i) / Fps</c> seconds of the staging clock.
    /// </summary>
    public sealed class NpcifyMotionFramesEvent
    {
        public string TaskId = string.Empty;
        public string StagingId = string.Empty;
        public long Revision;
        public double Fps;
        public long StartFrame;
        public int FrameCount;
        public string[] Bones = Array.Empty<string>();
        public double RestHipsHeight;
        /// <summary>FrameCount frames of (3 + 4 x Bones.Length) values.</summary>
        public float[] Values = Array.Empty<float>();
    }

    /// <summary>The motion prompt of a span of a staging (<c>timeline.motions</c>).</summary>
    public sealed class StagingMotionPrompt
    {
        public double StartTime;
        public double EndTime;
        public string Prompt = string.Empty;
    }

    /// <summary>
    /// The stored frames of a completed staging (<c>timeline.motion</c>): the same float32 bytes as
    /// the events, at <see cref="FramesUrl"/> or in <see cref="FramesData"/>.
    /// </summary>
    public sealed class StagingMotionTrack
    {
        public double Fps;
        public int FrameCount;
        public string[] Bones = Array.Empty<string>();
        public double RestHipsHeight;
        public string FramesUrl;
        public byte[] FramesData;
    }

    /// <summary>
    /// Shared constants and readers of live motion frames: the hips position in meters (Y up,
    /// the body faces +Z), then one rotation (x, y, z, w) per bone in the VRM 1.0 normalized
    /// humanoid space, relative to the VRM parent, every bone at rest in the T-pose.
    /// </summary>
    public static class NpcifyMotionFrames
    {
        /// <summary>The client capability that asks the server for live motion.</summary>
        public const string Capability = "motion_frames";

        public const int MaxFramesPerEvent = 3600;
        public const int MaxBones = 64;
        private const long MaxStartFrame = 24L * 60 * 60 * 240;

        /// <summary>The 22 bones of the server, hips to toes (no fingers, eyes or jaw).</summary>
        public static readonly string[] Bones =
        {
            "hips", "spine", "chest", "upperChest", "neck", "head",
            "leftShoulder", "leftUpperArm", "leftLowerArm", "leftHand",
            "rightShoulder", "rightUpperArm", "rightLowerArm", "rightHand",
            "leftUpperLeg", "leftLowerLeg", "leftFoot", "leftToes",
            "rightUpperLeg", "rightLowerLeg", "rightFoot", "rightToes",
        };

        /// <summary>The VRM parent of each bone, to give a missing bone's rotation to its children.</summary>
        public static readonly IReadOnlyDictionary<string, string> Parents = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["spine"] = "hips",
            ["chest"] = "spine",
            ["upperChest"] = "chest",
            ["neck"] = "upperChest",
            ["head"] = "neck",
            ["leftShoulder"] = "upperChest",
            ["leftUpperArm"] = "leftShoulder",
            ["leftLowerArm"] = "leftUpperArm",
            ["leftHand"] = "leftLowerArm",
            ["rightShoulder"] = "upperChest",
            ["rightUpperArm"] = "rightShoulder",
            ["rightLowerArm"] = "rightUpperArm",
            ["rightHand"] = "rightLowerArm",
            ["leftUpperLeg"] = "hips",
            ["leftLowerLeg"] = "leftUpperLeg",
            ["leftFoot"] = "leftLowerLeg",
            ["leftToes"] = "leftFoot",
            ["rightUpperLeg"] = "hips",
            ["rightLowerLeg"] = "rightUpperLeg",
            ["rightFoot"] = "rightLowerLeg",
            ["rightToes"] = "rightFoot",
        };

        /// <summary>Values of one frame for <paramref name="boneCount"/> bones.</summary>
        public static int FrameWidth(int boneCount)
        {
            return 3 + 4 * boneCount;
        }

        /// <summary>
        /// Reads little-endian float32 frames. Throws when the byte length does not match the
        /// frame count and the bones.
        /// </summary>
        public static float[] Read(byte[] data, int frameCount, int boneCount)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (frameCount < 0 || boneCount <= 0 || boneCount > MaxBones)
            {
                throw new InvalidDataException("Motion frames have an invalid shape.");
            }
            var count = (long)frameCount * FrameWidth(boneCount);
            if (data.LongLength != count * 4)
            {
                throw new InvalidDataException("Motion frames do not match their frame count.");
            }
            var values = new float[count];
            if (BitConverter.IsLittleEndian)
            {
                Buffer.BlockCopy(data, 0, values, 0, data.Length);
                return values;
            }
            var word = new byte[4];
            for (var index = 0; index < values.Length; index++)
            {
                var offset = index * 4;
                word[0] = data[offset + 3];
                word[1] = data[offset + 2];
                word[2] = data[offset + 1];
                word[3] = data[offset];
                values[index] = BitConverter.ToSingle(word, 0);
            }
            return values;
        }

        /// <summary>
        /// Reads a <c>motion_frames</c> stream event. Returns null for any other event or an
        /// invalid one; an invalid event never stops the speech of the staging.
        /// </summary>
        public static NpcifyMotionFramesEvent TryParseEvent(Dictionary<string, object> map)
        {
            if (map == null || !(map.TryGetValue("type", out var type) && type is string typeName && typeName == "motion_frames"))
            {
                return null;
            }
            var taskId = GetString(map, "taskId");
            var stagingId = GetString(map, "stagingId");
            var revision = GetCount(map, "revision", long.MaxValue);
            var startFrame = GetCount(map, "startFrame", MaxStartFrame);
            var frameCount = GetCount(map, "frameCount", MaxFramesPerEvent);
            var fps = GetNumber(map, "fps");
            var restHipsHeight = GetNumber(map, "restHipsHeight");
            var bones = GetBones(map);
            var data = map.TryGetValue("frames", out var frames) ? frames as byte[] : null;
            if (taskId == null || stagingId == null || revision < 0 || startFrame < 0 || frameCount < 0 ||
                !(fps > 0 && fps <= 240) || !(restHipsHeight > 0 && !double.IsInfinity(restHipsHeight)) ||
                bones == null || data == null ||
                data.LongLength != frameCount * (long)FrameWidth(bones.Length) * 4)
            {
                return null;
            }
            return new NpcifyMotionFramesEvent
            {
                TaskId = taskId,
                StagingId = stagingId,
                Revision = revision,
                Fps = fps,
                StartFrame = startFrame,
                FrameCount = (int)frameCount,
                Bones = bones,
                RestHipsHeight = restHipsHeight,
                Values = Read(data, (int)frameCount, bones.Length),
            };
        }

        /// <summary>Reads <c>timeline.motion</c>, or returns null when it is absent or invalid.</summary>
        public static StagingMotionTrack TryParseTrack(Dictionary<string, object> map)
        {
            if (map == null)
            {
                return null;
            }
            var fps = GetNumber(map, "fps");
            var frameCount = GetCount(map, "frameCount", int.MaxValue);
            var restHipsHeight = GetNumber(map, "restHipsHeight");
            var bones = GetBones(map);
            var frames = map.TryGetValue("frames", out var framesValue) ? framesValue as Dictionary<string, object> : null;
            if (!(fps > 0 && fps <= 240) || frameCount < 0 || !(restHipsHeight > 0) || bones == null || frames == null)
            {
                return null;
            }
            var track = new StagingMotionTrack
            {
                Fps = fps,
                FrameCount = (int)frameCount,
                Bones = bones,
                RestHipsHeight = restHipsHeight,
            };
            var kind = GetString(frames, "type");
            if (kind == "bytes" && frames.TryGetValue("data", out var data) && data is byte[] bytes)
            {
                track.FramesData = bytes;
                return track;
            }
            var url = GetString(frames, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }
            track.FramesUrl = url;
            return track;
        }

        /// <summary>Reads <c>timeline.motions</c>; invalid entries are skipped.</summary>
        public static List<StagingMotionPrompt> ParsePrompts(IList list)
        {
            var prompts = new List<StagingMotionPrompt>();
            if (list == null)
            {
                return prompts;
            }
            foreach (var item in list)
            {
                if (item is Dictionary<string, object> entry && GetString(entry, "prompt") is string prompt)
                {
                    prompts.Add(new StagingMotionPrompt
                    {
                        StartTime = GetNumber(entry, "startTime"),
                        EndTime = GetNumber(entry, "endTime"),
                        Prompt = prompt,
                    });
                }
            }
            return prompts;
        }

        private static string GetString(Dictionary<string, object> map, string key)
        {
            return map.TryGetValue(key, out var value) ? value as string : null;
        }

        private static double GetNumber(Dictionary<string, object> map, string key)
        {
            if (!map.TryGetValue(key, out var value))
            {
                return double.NaN;
            }
            if (value is double number)
            {
                return number;
            }
            if (value is long integer)
            {
                return integer;
            }
            return double.NaN;
        }

        private static long GetCount(Dictionary<string, object> map, string key, long max)
        {
            if (!map.TryGetValue(key, out var value))
            {
                return -1;
            }
            if (value is long integer)
            {
                return integer >= 0 && integer <= max ? integer : -1;
            }
            if (value is double number && Math.Floor(number) == number && number >= 0 && number <= max)
            {
                return (long)number;
            }
            return -1;
        }

        private static string[] GetBones(Dictionary<string, object> map)
        {
            if (!map.TryGetValue("bones", out var value) || !(value is IList list) || list.Count == 0 || list.Count > MaxBones)
            {
                return null;
            }
            var bones = new string[list.Count];
            for (var index = 0; index < list.Count; index++)
            {
                if (!(list[index] is string bone))
                {
                    return null;
                }
                bones[index] = bone;
            }
            return bones;
        }
    }
}
