using System;
using System.Collections.Generic;

namespace Npcify.Sdk.Character
{
    /// <summary>
    /// The expression or animation clips of a staging that played. A clip is known by its ID, or
    /// by its name and start time. In a streamed staging, a revision can move a clip that played:
    /// the beats of a pending speech block take new times when more of the block is aligned. There,
    /// a clip with the same name whose time overlaps a clip that played is that clip.
    /// </summary>
    internal sealed class NpcifyPlayedClips
    {
        private struct Span
        {
            public string Name;
            public double Start;
            public double End;
        }

        private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<Span> _spans = new List<Span>();

        public void Clear()
        {
            _keys.Clear();
            _spans.Clear();
        }

        /// <summary>
        /// True when the clip played. With <paramref name="matchMovedClips"/>, a clip that only
        /// moved also counts; it is then kept with its new time too.
        /// </summary>
        public bool Contains(string id, string name, double start, double end, bool matchMovedClips)
        {
            if (_keys.Contains(Key(id, name, start)))
            {
                return true;
            }
            if (!matchMovedClips)
            {
                return false;
            }
            foreach (var span in _spans)
            {
                if (span.Name == name && span.Start < end && start < span.End)
                {
                    Add(id, name, start, end);
                    return true;
                }
            }
            return false;
        }

        public void Add(string id, string name, double start, double end)
        {
            if (_keys.Add(Key(id, name, start)))
            {
                _spans.Add(new Span { Name = name, Start = start, End = end });
            }
        }

        private static string Key(string id, string name, double start)
        {
            // Not the list index: a revision can add a clip in front of clips that played.
            return !string.IsNullOrEmpty(id) ? id : $"{name}:{start:F3}";
        }
    }
}
