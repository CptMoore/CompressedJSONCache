using BattleTech;
using BattleTech.UI;
using Harmony;
using HBS.Data;
using HBS.Logging;
using HBS.Util;
using K4os.Compression.LZ4;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using UnityEngine;

namespace CompressedJSONCache
{
    //[HarmonyPatch(typeof(MainMenu), "ReceiveButtonPress")]
    public static class MainMenu_ReceiveButtonPress_Patch
    {
        public static bool Prefix(string button)
        {
            if (button == "Credits")
            {
                Cache.Save();
                Benchmark.PrintBenchmarks();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(File), nameof(File.ReadAllText), typeof(string))]
    public static class File_ReadAllText_Patch
    {
        public static bool Prefix(string path, ref string __result)
        {
            try
            {
                var key = Cache.NormFilePath(path);
                if (Cache.Get(key, out __result))
                {
                    return false;
                }
            }
            catch (Exception e)
            {
                Main.Logger.LogError(e);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(DataLoader), nameof(DataLoader.LoadResource), typeof(string), typeof(Action<string>))]
    public static class DataLoader_LoadResource_string_Patch
    {
        public static bool Prefix(string path, ref Action<string> handler)
        {
            try
            {
                var key = Cache.NormFilePath(path);
                if (Cache.Get(key, out var text))
                {
                    handler(text);
                    return false;
                }
                Main.Logger.Log($"cache miss {key}");
            }
            catch (Exception e)
            {
                Main.Logger.LogError(e);
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(BattleTechResourceLocator), "RefreshTypedEntries")]
    public static class BattleTechResourceLocator_RefreshTypedEntries_Patch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(
            Dictionary<BattleTechResourceType, Dictionary<string, VersionManifestEntry>> ___baseManifest,
            Dictionary<BattleTechResourceType, Dictionary<string, VersionManifestEntry>> ___contentPacksManifest,
            Dictionary<VersionManifestAddendum, Dictionary<BattleTechResourceType, Dictionary<string, VersionManifestEntry>>> ___addendumsManifest)
        {
            try
            {
                var entries = EnumerateEntries(___baseManifest, ___contentPacksManifest, ___addendumsManifest);
                Cache.Build(entries);
            }
            catch (Exception e)
            {
                Main.Logger.LogError("error loading", e);
            }
        }

        private static IEnumerable<VersionManifestEntry> EnumerateEntries(
            Dictionary<BattleTechResourceType, Dictionary<string, VersionManifestEntry>> ___baseManifest,
            Dictionary<BattleTechResourceType, Dictionary<string, VersionManifestEntry>> ___contentPacksManifest,
            Dictionary<VersionManifestAddendum, Dictionary<BattleTechResourceType, Dictionary<string, VersionManifestEntry>>> ___addendumsManifest)
        {
            foreach (var dict in ___baseManifest.Values)
            {
                foreach (var entry in dict.Values)
                {
                    yield return entry;
                }
            }
            
            foreach (var dict in ___contentPacksManifest.Values)
            {
                foreach (var entry in dict.Values)
                {
                    yield return entry;
                }
            }

            foreach (var dict in ___addendumsManifest.Values)
            {
                foreach (var dict2 in dict.Values)
                {
                    foreach (var entry in dict2.Values)
                    {
                        yield return entry;
                    }
                }
            }
        }
    }

    internal static class Cache
    {
        private static Dictionary<string, byte[]> cache = new Dictionary<string, byte[]>();
        internal static void Build(IEnumerable<VersionManifestEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (!entry.IsFileAsset)
                {
                    continue;
                }
                var filePath = entry.FilePath;
                var ext = Path.GetExtension(filePath);
                if (ext == ".csv" || ext == ".json" || ext == ".txt")
                {
                    try
                    {
                        var key = NormFilePath(filePath);
                        Main.Logger.Log($"caching {key}");
                        var text = File.ReadAllText(filePath);
                        Set(key, text);
                    }
                    catch (Exception e)
                    {
                        Main.Logger.LogError(e);
                    }
                }
            }
            Main.Logger.Log("saving cache");
            Save();
            Benchmark.PrintBenchmarks();
        }

        private static readonly BinaryFormatter formatter = new BinaryFormatter();
        internal static void Save()
        {
            using (var fileStream = new FileStream(Main.CacheFilePath, FileMode.Create))
            {
                formatter.Serialize(fileStream, cache);
            }
        }

        internal static readonly Benchmark benchmarkd = new Benchmark("Decompress");
        internal static bool Get(string key, out string text)
        {
            try
            {
                if (cache.TryGetValue(key, out var data))
                {
                    benchmarkd.Prefix();
                    text = Decompress(data);
                    benchmarkd.Postfix();
                    return true;
                }
            }
            catch (Exception e)
            {
                Main.Logger.LogError(e);
            }

            text = null;
            return false;
        }
        
        internal static readonly Benchmark benchmarkc = new Benchmark("Compress");
        internal static void Set(string key, string text)
        {
            try
            {
                benchmarkc.Prefix();
                cache[key] = Compress(text);
                benchmarkc.Postfix();
            }
            catch (Exception e)
            {
                Main.Logger.LogError(e);
            }
        }

        private static readonly string GameDirectoryPath;
        static Cache()
        {
            GameDirectoryPath = Path.GetFullPath(Path.Combine(Path.Combine(Application.streamingAssetsPath, ".."), ".."));
        }
        internal static string NormFilePath(string path)
        {
            return GetRelativePath(path, GameDirectoryPath);
        }
        internal static string GetRelativePath(string path, string rootPath)
        {
            if (!Path.IsPathRooted(path))
                return path;

            rootPath = Path.GetFullPath(rootPath);
            if (rootPath[rootPath.Length-1] != Path.DirectorySeparatorChar)
                rootPath += Path.DirectorySeparatorChar;

            var pathUri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            var rootUri = new Uri(rootPath, UriKind.Absolute);

            if (pathUri.Scheme != rootUri.Scheme)
                return path;

            var relativeUri = rootUri.MakeRelativeUri(pathUri);
            var relativePath = Uri.UnescapeDataString(relativeUri.ToString());

            relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            return relativePath;
        }

        private static byte[] Compress(string text)
        {
            // LZ4
            var textBytes = Encoding.UTF8.GetBytes(text);
            var compressedBufferBytesLength = LZ4Codec.MaximumOutputSize(textBytes.Length);
            var compressedBufferBytes = new byte[compressedBufferBytesLength];

            var compressedLength = LZ4Codec.Encode(textBytes.AsSpan(), compressedBufferBytes.AsSpan(), LZ4Level.L09_HC);

            var textLengthBytes = BitConverter.GetBytes(textBytes.Length);

            // lengthBytes has to be 4!
            var data = new byte[4 + compressedLength];

            Buffer.BlockCopy(textLengthBytes, 0, data, 0, 4);
            Buffer.BlockCopy(compressedBufferBytes, 0, data, 4, compressedLength);

            return data;
        }

        private static string Decompress(byte[] data)
        {
            // LZ4
            var textLength = BitConverter.ToInt32(data, 0);
            var textBytes = new byte[textLength];
            LZ4Codec.Decode(data.AsSpan(4), textBytes.AsSpan());

            var text = Encoding.UTF8.GetString(textBytes);

            return text;
        }
    }

    public class Benchmark
    {
        readonly Stopwatch stopwatch = new Stopwatch();
        int count = 0;

        private string id;
        internal Benchmark(string id)
        {
            this.id = id;
        }

        internal void Prefix()
        {
            ++count;
            stopwatch.Start();
        }
        internal void Postfix()
        {
            stopwatch.Stop();
        }

        private void Print()
        {
            var timeMS = stopwatch.ElapsedMilliseconds;
            var avgMS = count == 0 ? "-" : (timeMS / count).ToString();
            Main.Logger.LogError($"BENCHMARK id={id} time={timeMS}ms count={count} avg={avgMS}");
        }

        public static void PrintBenchmarks()
        {
            Cache.benchmarkc.Print();
            Cache.benchmarkd.Print();
        }
    }
}
