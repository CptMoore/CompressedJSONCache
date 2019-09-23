using Harmony;
using HBS.Logging;
using System;
using System.IO;
using System.Reflection;

namespace CompressedJSONCache
{
    public static class Main
    {
        public static string CacheFilePath = null;
        public static ILog Logger = null;

        public static void Start(string modDirectory)
        {
            try
            {
                var name = nameof(CompressedJSONCache);
                Logger = SetupLogging(modDirectory, name);

                CacheFilePath = Path.Combine(modDirectory, "cache.db");
                AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
                {
                    var resolvingName = new AssemblyName(args.Name);
                    var assemblyPath = Path.Combine(modDirectory, $"{resolvingName.Name}.dll");
                    if (!File.Exists(assemblyPath))
                    {
                        return null;
                    }
                    return Assembly.LoadFrom(assemblyPath);
                };

                var harmony = HarmonyInstance.Create(name);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception e)
            {
                Logger.LogError("error starting", e);
            }
        }

        internal static ILog SetupLogging(string modDirectory, string name)
        {
            HBS.Logging.Logger.SetLoggerLevel(name, LogLevel.Debug);
            var path = Path.Combine(modDirectory, "log.txt");
            var appender = new FileLogAppender(path, FileLogAppender.WriteMode.INSTANT);
            HBS.Logging.Logger.AddAppender(name, appender);
            return HBS.Logging.Logger.GetLogger(name);
        }
    }
}