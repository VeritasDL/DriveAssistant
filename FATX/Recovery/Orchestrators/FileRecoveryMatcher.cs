using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FATX.Recovery.Orchestrators
{
    /// <summary>
    /// Matches JSON file entries to recovered/deleted files on disk.
    /// </summary>
    public class FileRecoveryMatcher
    {
        private readonly string _recoveredPath;
        private readonly string _deletedPath;
        private readonly bool _enableFuzzy;
        private Dictionary<string, string>? _pathCache;

        public FileRecoveryMatcher(string recoveredPath, string deletedPath, bool enableFuzzy = true)
        {
            _recoveredPath = recoveredPath;
            _deletedPath = deletedPath;
            _enableFuzzy = enableFuzzy;
            BuildCache();
        }

        private void BuildCache()
        {
            _pathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Cache recovered files
            if (Directory.Exists(_recoveredPath))
            {
                foreach (var file in Directory.EnumerateFiles(_recoveredPath, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    if (!_pathCache.ContainsKey(fileName))
                        _pathCache[fileName] = file;
                }
            }

            // Cache deleted files
            if (Directory.Exists(_deletedPath))
            {
                foreach (var file in Directory.EnumerateFiles(_deletedPath, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    if (!_pathCache.ContainsKey(fileName))
                        _pathCache[fileName] = file;
                }
            }
        }

        /// <summary>
        /// Attempts to find a file matching the given path.
        /// </summary>
        public string? TryFindFile(string jsonPath)
        {
            if (string.IsNullOrWhiteSpace(jsonPath))
                return null;

            // Try exact path match first
            var exactPath = Path.Combine(_recoveredPath, jsonPath);
            if (File.Exists(exactPath))
                return exactPath;

            // Try in deleted path
            var deletedExact = Path.Combine(_deletedPath, jsonPath);
            if (File.Exists(deletedExact))
                return deletedExact;

            // Try filename-only match
            if (_enableFuzzy)
            {
                var fileName = Path.GetFileName(jsonPath);
                if (_pathCache!.TryGetValue(fileName, out var cachedPath) && File.Exists(cachedPath))
                    return cachedPath;
            }

            return null;
        }
    }
}
