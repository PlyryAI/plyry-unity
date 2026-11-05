using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Plyry.Unity
{
    [Serializable]
    internal sealed class PlyryLibraryIndex
    {
        public List<PlyryLibraryIndexEntry> entries = new List<PlyryLibraryIndexEntry>();
    }

    [Serializable]
    internal sealed class PlyryLibraryIndexEntry
    {
        public string targetId;
        public string mediaType;
        public string jobHash;
        public string file;
        public string sceneFile;
        public string mimeType;
        public string sha256;
        public string savedUtc;
    }

    public sealed class PlyryStoredAsset
    {
        public string TargetId { get; internal set; }
        public string MediaType { get; internal set; }
        public string JobHash { get; internal set; }
        public string Path { get; internal set; }
        public string ScenePath { get; internal set; }
        public string MimeType { get; internal set; }
        public string Sha256 { get; internal set; }
    }

    /// <summary>Persists the latest generated file for each target and media kind.</summary>
    public static class PlyryLocalLibrary
    {
        private const string FolderName = "Plyry";
        private const string IndexName = "media-index.json";

        public static PlyryStoredAsset Store(string targetId, string mediaType, string jobHash,
            string extension, string temporaryPath, string mimeType, string sha256)
        {
            if (string.IsNullOrEmpty(temporaryPath) || !File.Exists(temporaryPath))
                throw new FileNotFoundException("The downloaded Plyry file was not found.", temporaryPath);

            var safeTarget = SafeSegment(targetId);
            var safeType = SafeSegment(mediaType);
            var effectiveJobHash = string.IsNullOrEmpty(jobHash) ? Guid.NewGuid().ToString("N") : jobHash;
            var safeHash = SafeSegment(effectiveJobHash);
            var safeExtension = SafeExtension(extension);
            var root = Path.Combine(Application.persistentDataPath, FolderName);
            var directory = Path.Combine(root, safeTarget, safeType);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, safeHash + safeExtension);
            File.Copy(temporaryPath, destination, true);
            File.Delete(temporaryPath);

            var index = ReadIndex(root);
            var record = new PlyryLibraryIndexEntry
            {
                targetId = targetId ?? string.Empty,
                mediaType = mediaType ?? string.Empty,
                jobHash = effectiveJobHash,
                file = MakeRelative(root, destination),
                mimeType = mimeType ?? "application/octet-stream",
                sha256 = sha256 ?? string.Empty,
                savedUtc = DateTime.UtcNow.ToString("o")
            };

            var updated = false;
            for (var i = index.entries.Count - 1; i >= 0; i--)
            {
                var existing = index.entries[i];
                if (existing == null || existing.targetId != record.targetId
                    || !string.Equals(existing.mediaType, record.mediaType, StringComparison.OrdinalIgnoreCase)
                    || existing.jobHash != record.jobHash) continue;
                record.sceneFile = existing.sceneFile;
                index.entries[i] = record;
                updated = true;
                break;
            }
            if (!updated) index.entries.Add(record);
            WriteIndex(root, index);
            PlyryStoredAsset stored;
            if (!TryToStoredAsset(root, record, out stored))
                throw new InvalidDataException("The saved Plyry file could not be resolved inside the local library.");
            return stored;
        }

        public static PlyryStoredAsset FindLatest(string targetId, string mediaType)
        {
            var root = Path.Combine(Application.persistentDataPath, FolderName);
            var index = ReadIndex(root);
            for (var i = index.entries.Count - 1; i >= 0; i--)
            {
                var entry = index.entries[i];
                if (entry != null && entry.targetId == (targetId ?? string.Empty)
                    && string.Equals(entry.mediaType, mediaType ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    PlyryStoredAsset asset;
                    if (TryToStoredAsset(root, entry, out asset) && File.Exists(asset.Path)) return asset;
                }
            }
            return null;
        }

        public static PlyryStoredAsset[] FindLatestForTarget(string targetId)
        {
            var root = Path.Combine(Application.persistentDataPath, FolderName);
            var index = ReadIndex(root);
            var latest = new Dictionary<string, PlyryStoredAsset>(StringComparer.OrdinalIgnoreCase);
            for (var i = index.entries.Count - 1; i >= 0; i--)
            {
                var entry = index.entries[i];
                if (entry == null || entry.targetId != (targetId ?? string.Empty) || string.IsNullOrEmpty(entry.mediaType)
                    || latest.ContainsKey(entry.mediaType)) continue;
                PlyryStoredAsset asset;
                if (TryToStoredAsset(root, entry, out asset) && File.Exists(asset.Path)) latest.Add(entry.mediaType, asset);
            }
            var result = new System.Collections.Generic.List<PlyryStoredAsset>(latest.Values);
            return result.ToArray();
        }

        public static PlyryStoredAsset[] FindAllForTarget(string targetId)
        {
            var root = Path.Combine(Application.persistentDataPath, FolderName);
            var index = ReadIndex(root);
            var result = new System.Collections.Generic.List<PlyryStoredAsset>();
            for (var i = 0; i < index.entries.Count; i++)
            {
                var entry = index.entries[i];
                if (entry == null || entry.targetId != (targetId ?? string.Empty)) continue;
                PlyryStoredAsset asset;
                if (TryToStoredAsset(root, entry, out asset) && File.Exists(asset.Path)) result.Add(asset);
            }
            return result.ToArray();
        }

        public static string StoreScene(string targetId, string mediaType, string jobHash, string temporaryPath)
        {
            if (string.IsNullOrEmpty(temporaryPath) || !File.Exists(temporaryPath))
                throw new FileNotFoundException("The downloaded Plyry scene file was not found.", temporaryPath);
            var root = Path.Combine(Application.persistentDataPath, FolderName);
            var destination = Path.Combine(root, SafeSegment(targetId), SafeSegment(mediaType), SafeSegment(jobHash) + ".scene.json");
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(temporaryPath, destination, true);
            File.Delete(temporaryPath);
            var index = ReadIndex(root);
            for (var i = 0; i < index.entries.Count; i++)
            {
                var entry = index.entries[i];
                if (entry != null && entry.targetId == (targetId ?? string.Empty)
                    && string.Equals(entry.mediaType, mediaType ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && entry.jobHash == (jobHash ?? string.Empty))
                {
                    entry.sceneFile = MakeRelative(root, destination);
                    WriteIndex(root, index);
                    return destination;
                }
            }
            throw new InvalidDataException("The Plyry media index has no matching asset entry for this scene file.");
        }

        private static PlyryLibraryIndex ReadIndex(string root)
        {
            var path = Path.Combine(root, IndexName);
            if (!File.Exists(path)) return new PlyryLibraryIndex();
            try
            {
                var index = JsonUtility.FromJson<PlyryLibraryIndex>(File.ReadAllText(path));
                if (index == null || index.entries == null) return new PlyryLibraryIndex();
                return index;
            }
            catch (Exception)
            {
                return new PlyryLibraryIndex();
            }
        }

        private static void WriteIndex(string root, PlyryLibraryIndex index)
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, IndexName), JsonUtility.ToJson(index, true));
        }

        private static bool TryToStoredAsset(string root, PlyryLibraryIndexEntry entry, out PlyryStoredAsset asset)
        {
            asset = null;
            if (entry == null || string.IsNullOrEmpty(entry.file)) return false;
            try
            {
                var file = ResolveInside(root, entry.file);
                var scene = string.IsNullOrEmpty(entry.sceneFile) ? string.Empty : ResolveInside(root, entry.sceneFile);
                asset = new PlyryStoredAsset
                {
                    TargetId = entry.targetId,
                    MediaType = entry.mediaType,
                    JobHash = entry.jobHash,
                    Path = file,
                    ScenePath = scene,
                    MimeType = entry.mimeType,
                    Sha256 = entry.sha256
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ResolveInside(string root, string relative)
        {
            var path = Path.GetFullPath(Path.Combine(root, relative ?? string.Empty));
            var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Plyry media index contains a path outside the library.");
            return path;
        }

        private static string MakeRelative(string root, string path)
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.Substring(fullRoot.Length).Replace(Path.DirectorySeparatorChar, '/');
        }

        private static string SafeSegment(string value)
        {
            if (string.IsNullOrEmpty(value)) return "default";
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_') chars[i] = '_';
            var safe = new string(chars);
            if (safe == "." || safe == "..") return "default";
            if (IsWindowsReservedName(safe)) return "_" + safe;
            return safe;
        }

        private static bool IsWindowsReservedName(string value)
        {
            var name = value.TrimEnd('.').ToUpperInvariant();
            if (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL") return true;
            if (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)))
                return name[3] >= '1' && name[3] <= '9';
            return false;
        }

        private static string SafeExtension(string value)
        {
            if (string.IsNullOrEmpty(value)) return ".bin";
            var extension = value.StartsWith(".") ? value : "." + value;
            if (extension.Length > 10) return ".bin";
            for (var i = 1; i < extension.Length; i++)
                if (!char.IsLetterOrDigit(extension[i])) return ".bin";
            return extension.ToLowerInvariant();
        }
    }
}

