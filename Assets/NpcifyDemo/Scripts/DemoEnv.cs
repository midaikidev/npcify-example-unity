using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NpcifyDemo
{
    /// <summary>
    /// Reads demo settings from environment variables and from a <c>.env</c> file.
    /// Environment variables have priority. The first <c>.env</c> file found is used:
    /// the project folder in the Editor, or the folder that contains the built game.
    /// </summary>
    public static class DemoEnv
    {
        public const string ApiBaseUrlKey = "NPCIFY_API_BASE_URL";
        public const string ApiKeyKey = "NPCIFY_API_KEY";
        public const string OnlineCharacterIdKey = "NPCIFY_ONLINE_CHARACTER_ID";
        public const string OfflineCharacterIdKey = "NPCIFY_OFFLINE_CHARACTER_ID";

        public const string DefaultApiBaseUrl = "https://api.npcify.ai";

        private static Dictionary<string, string> _fileValues;

        public static string LoadedFilePath { get; private set; }

        public static string ApiBaseUrl => Get(ApiBaseUrlKey, DefaultApiBaseUrl);

        public static string ApiKey => Get(ApiKeyKey);

        public static bool HasApiKey
        {
            get
            {
                var key = ApiKey;
                return !string.IsNullOrWhiteSpace(key) && !key.StartsWith("<", StringComparison.Ordinal);
            }
        }

        public static string Get(string key, string fallback = "")
        {
            var fromProcess = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(fromProcess))
            {
                return fromProcess.Trim();
            }

            EnsureFileLoaded();
            if (_fileValues.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
            return fallback;
        }

        public static void Reload()
        {
            _fileValues = null;
            LoadedFilePath = null;
        }

        private static void EnsureFileLoaded()
        {
            if (_fileValues != null)
            {
                return;
            }

            _fileValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var candidate in CandidateFiles())
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }
                Parse(File.ReadAllLines(candidate), _fileValues);
                LoadedFilePath = candidate;
                return;
            }
        }

        private static IEnumerable<string> CandidateFiles()
        {
            // Editor: <project>/Assets -> <project>/.env
            // Windows/Linux player: <game>/<name>_Data -> <game>/.env
            // macOS player: <name>.app/Contents -> folder that contains <name>.app
            var dataPath = Application.dataPath;
            yield return Path.GetFullPath(Path.Combine(dataPath, "..", ".env"));
            yield return Path.GetFullPath(Path.Combine(dataPath, "..", "..", ".env"));
            yield return Path.Combine(Application.streamingAssetsPath, ".env");
        }

        internal static void Parse(IEnumerable<string> lines, IDictionary<string, string> target)
        {
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                if (line.StartsWith("export ", StringComparison.Ordinal))
                {
                    line = line.Substring(7).TrimStart();
                }
                var separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }
                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                if (value.Length >= 2 &&
                    ((value[0] == '"' && value[value.Length - 1] == '"') ||
                     (value[0] == '\'' && value[value.Length - 1] == '\'')))
                {
                    value = value.Substring(1, value.Length - 2);
                }
                else
                {
                    var comment = value.IndexOf(" #", StringComparison.Ordinal);
                    if (comment >= 0)
                    {
                        value = value.Substring(0, comment).TrimEnd();
                    }
                }
                target[key] = value;
            }
        }
    }
}
