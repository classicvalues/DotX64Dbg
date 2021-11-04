﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Dotx64Dbg
{
    internal class PluginInfo
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Website { get; set; }

        public string[] Dependencies { get; set; }
    }

    internal class Plugin
    {
        public PluginInfo Info;
        public string Path;
        public string ConfigPath;
        public string BuildOutputPath;
        public List<string> SourceFiles;
        public bool RequiresRebuild;
        internal AssemblyLoader Loader;
        internal string AssemblyPath;
        internal object Instance;
        internal Type InstanceType;
    }

    internal partial class Plugins
    {
        string PluginsPath = "dotplugins";
        string AppDataPath;
        string PluginOutputPath;

        FileSystemWatcher PluginWatch;
        NuGetDependencyResolver dependencyResolver;

        List<Plugin> Registered = new();

        private void SetupDirectories()
        {
            PluginsPath = Settings.PluginsPath;
            if (!Path.IsPathFullyQualified(PluginsPath))
            {
                PluginsPath = Path.GetFullPath(Path.Combine(Utils.GetRootPath(), PluginsPath));
            }

            if (!Directory.Exists(PluginsPath))
            {
                try
                {
                    Directory.CreateDirectory(PluginsPath);
                }
                catch (Exception)
                {
                    Console.WriteLine("Unable to create directory for plugins: {0}", PluginsPath);
                }
            }

            Console.WriteLine("DotX64Dbg Plugins Path: {0}", PluginsPath);

            AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotX64Dbg");
            if (!Directory.Exists(AppDataPath))
            {
                Directory.CreateDirectory(AppDataPath);
            }

            PluginOutputPath = Path.Combine(AppDataPath, "Plugins");
            if (!Directory.Exists(PluginOutputPath))
            {
                Directory.CreateDirectory(PluginOutputPath);
            }
        }

        public void Initialize()
        {
            SetupDirectories();

            PluginWatch = new FileSystemWatcher(PluginsPath, "*.*");
            PluginWatch.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes;
            PluginWatch.IncludeSubdirectories = true;
            PluginWatch.EnableRaisingEvents = true;
            PluginWatch.Created += OnPluginCreate;
            PluginWatch.Deleted += OnPluginRemove;
            PluginWatch.Renamed += OnPluginRename;
            PluginWatch.Changed += OnPluginChange;

            dependencyResolver = new();

            RegisterPlugins();
            GenerateProjects();
            StartBuildWorker();
            TriggerRebuild();
        }

        public void Shutdown()
        {
            StopBuildWorker();
        }

        void RegisterPlugins()
        {
            var dirs = new List<string>(Directory.EnumerateDirectories(PluginsPath));
            foreach (var dir in dirs)
            {
                RegisterPlugin(dir);
            }
        }

        void GenerateProjects()
        {
            var binaryPathX86 = Path.Combine(Utils.GetRootPath(), "x86", "plugins");
            var binaryPathX64 = Path.Combine(Utils.GetRootPath(), "x64", "plugins");
            var assemblies = new string[] {
                "Dotx64Dbg.Bindings.dll", "Dotx64Dbg.Managed.dll"
            };

            foreach (var plugin in Registered)
            {
                if (plugin.Info == null)
                    continue;

                var projectFilePath = Path.Combine(plugin.Path, plugin.Info.Name + ".csproj");
                if (File.Exists(projectFilePath))
                    continue;

                Console.WriteLine($"Generating project file for {plugin.Info.Name}");

                var projGen = new ProjectGenerator();
                projGen.ReferencePathX86 = binaryPathX86;
                projGen.ReferencePathX64 = binaryPathX64;
                projGen.References = assemblies;
                projGen.Frameworks = plugin.Info.Dependencies
                    .Where(deps => NuGetDependencyResolver.VersioningHelper.IsValidDotNetFrameworkName(deps))
                    .Select(deps => new NuGet.Frameworks.NuGetFramework(
                            NuGetDependencyResolver.VersioningHelper.GetFrameworkName(deps),
                            new Version(NuGetDependencyResolver.VersioningHelper.GetFrameworkVersion(deps)))
                    ).ToArray();

                projGen.Save(projectFilePath);
            }
        }

        List<string> EnumerateSourceFiles(string path)
        {
            return new List<string>(Directory.EnumerateFiles(path, "*.cs", new EnumerationOptions()
            {
                RecurseSubdirectories = true,
            }));
        }

        PluginInfo GetPluginInfo(string jsonFile)
        {
            try
            {
                var jsonString = Utils.ReadFileContents(jsonFile);
                var pluginInfo = JsonSerializer.Deserialize<PluginInfo>(jsonString);

                return JsonSerializer.Deserialize<PluginInfo>(jsonString);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        void RegisterPlugin(string path)
        {
            var jsonFile = Path.Combine(path, "plugin.json");
            var pluginInfo = GetPluginInfo(jsonFile);
            var pathName = Path.GetFileName(path);

            var plugin = new Plugin()
            {
                Info = pluginInfo,
                Path = path,
                ConfigPath = jsonFile,
                RequiresRebuild = false,
                BuildOutputPath = Path.Combine(PluginOutputPath, pathName),
                SourceFiles = EnumerateSourceFiles(path),
            };

            if (!Directory.Exists(plugin.BuildOutputPath))
            {
                Directory.CreateDirectory(plugin.BuildOutputPath);
            }

            // Delete the old cache.
            var oldFiles = Directory.GetFiles(plugin.BuildOutputPath, "*.*", SearchOption.AllDirectories);
            foreach (var oldFile in oldFiles)
            {
                try
                {
                    if (oldFile.EndsWith(".dll") || oldFile.EndsWith(".pdb"))
                    {
                        File.Delete(oldFile);
                    }
                }
                catch (Exception)
                {
                }
            }

            Registered.Add(plugin);
            Utils.DebugPrintLine($"Registered new plugin: {plugin.Path}");

            if (plugin.Info != null)
            {
                plugin.RequiresRebuild = true;
                TriggerRebuild(50);
            }
        }

        void RemovePlugin(Plugin plugin)
        {
            Utils.DebugPrintLine($"Removing plugin: {plugin.Path}");

            UnloadPlugin(plugin);

            for (var i = 0; i < Registered.Count; ++i)
            {
                if (Registered[i].Path == plugin.Path)
                {
                    Registered.RemoveAt(i);
                    break;
                }
            }
        }

        void LoadPlugin(Plugin plugin)
        {
            var pluginInfo = GetPluginInfo(plugin.ConfigPath);
            if (pluginInfo == null)
            {
                Utils.DebugPrintLine("Unable to load plugin info.");
                return;
            }

            Utils.DebugPrintLine("Plugin meta loaded, activating plugin.");
            plugin.Info = pluginInfo;

            plugin.RequiresRebuild = true;
            TriggerRebuild(50);
        }

        Plugin FindPlugin(string path)
        {
            foreach (var plugin in Registered)
            {
                if (plugin.Path == path)
                {
                    return plugin;
                }
            }
            return null;
        }

        class PluginFileInfo
        {
            public bool PluginRootFolder { get; set; }
            public string PluginName { get; set; }
            public string FilePath { get; set; }
        }

        PluginFileInfo ParseInfo(string path)
        {
            var relativePath = Path.GetRelativePath(PluginsPath, path);
            if (relativePath.Length == 0)
                return null;

            if (relativePath.Contains(Path.DirectorySeparatorChar))
            {
                var pos = relativePath.IndexOf(Path.DirectorySeparatorChar);
                var pluginName = relativePath.Substring(0, pos);

                return new PluginFileInfo()
                {
                    PluginRootFolder = false,
                    PluginName = pluginName,
                    FilePath = relativePath.Substring(pos + 1)
                };
            }
            else
            {
                return new PluginFileInfo()
                {
                    PluginRootFolder = true,
                    PluginName = relativePath,
                    FilePath = null
                };
            }
        }

        void OnPluginCreate(object sender, FileSystemEventArgs e)
        {
            Utils.DebugPrintLine($"[PluginWatch] Plugin File Create: {e.FullPath}");

            var info = ParseInfo(e.FullPath);
            if (info == null)
                return;

            if (info.PluginRootFolder)
            {
                RegisterPlugin(e.FullPath);
            }
            else
            {
                var pluginPath = Path.Combine(PluginsPath, info.PluginName);
                var plugin = FindPlugin(pluginPath);
                if (plugin == null)
                {
                    Utils.DebugPrintLine($"[PluginWatch] Unable to find registered plugin for {info.PluginName}");
                    return;
                }

                // File was created.
                OnPluginFileCreate(plugin, info);
            }

            TriggerRebuild(50);
        }

        void OnPluginFileCreate(Plugin plugin, PluginFileInfo info)
        {
            if (info.FilePath == "plugin.json")
            {
                LoadPlugin(plugin);
            }
            else
            {
                var fullPath = Path.Combine(PluginsPath, info.PluginName, info.FilePath);
                if (!fullPath.EndsWith(".cs"))
                    return;

                if (!plugin.SourceFiles.Contains(fullPath))
                    plugin.SourceFiles.Add(fullPath);

                plugin.RequiresRebuild = true;
                TriggerRebuild(50);
            }
        }

        void OnPluginRemove(object sender, FileSystemEventArgs e)
        {
            Utils.DebugPrintLine($"[PluginWatch] Plugin File Remove: {e.FullPath}");

            var info = ParseInfo(e.FullPath);
            if (info == null)
                return;

            var pluginPath = Path.Combine(PluginsPath, info.PluginName);
            var plugin = FindPlugin(pluginPath);
            if (plugin == null)
            {
                Utils.DebugPrintLine($"[PluginWatch] Unable to find registered plugin for {info.PluginName}");
                return;
            }

            if (info.PluginRootFolder)
            {
                RemovePlugin(plugin);
            }
            else
            {
                OnPluginFileRemove(plugin, info);
            }
        }

        void OnPluginFileRemove(Plugin plugin, PluginFileInfo info)
        {
            var fullPath = Path.Combine(PluginsPath, info.PluginName, info.FilePath);

            if (plugin.ConfigPath == fullPath)
            {
                Utils.DebugPrintLine("plugin.json got removed, unloading plugin");

                plugin.Info = null;
                UnloadPlugin(plugin);
            }
            else
            {
                if (plugin.SourceFiles.Remove(fullPath))
                {
                    if (plugin.SourceFiles.Count == 0)
                    {
                        UnloadPlugin(plugin);
                    }
                    else
                    {
                        plugin.RequiresRebuild = true;
                        TriggerRebuild(50);
                    }
                }
            }
        }

        void OnPluginRename(object sender, RenamedEventArgs e)
        {
            Utils.DebugPrintLine($"[PluginWatch] Plugin File Rename: {e.OldFullPath} -> {e.FullPath}");

            var info = ParseInfo(e.FullPath);
            if (info == null)
                return;

            var pluginPath = Path.Combine(PluginsPath, info.PluginName);
            var plugin = FindPlugin(pluginPath);
            if (plugin == null)
            {
                Utils.DebugPrintLine($"[PluginWatch] Unable to find registered plugin for {info.PluginName}");
                return;
            }

            if (info.PluginRootFolder)
            {
                RemovePlugin(plugin);
                RegisterPlugin(e.FullPath);
            }
            else
            {
                OnPluginFileRename(plugin, info, e.OldFullPath);
            }
        }

        void OnPluginFileRename(Plugin plugin, PluginFileInfo info, string oldFullPath)
        {
            var fullPath = Path.Combine(PluginsPath, info.PluginName, info.FilePath);

            if (plugin.ConfigPath == oldFullPath)
            {
                plugin.Info = null;
                UnloadPlugin(plugin);
            }
            else if (plugin.ConfigPath == fullPath)
            {
                LoadPlugin(plugin);
            }
            else
            {
                //Logging.WriteLine("File rename {0}, {1}", e.OldFullPath, e.FullPath);
                plugin.SourceFiles.Remove(oldFullPath);

                if (!plugin.SourceFiles.Contains(fullPath))
                    plugin.SourceFiles.Add(fullPath);

                plugin.RequiresRebuild = true;
                TriggerRebuild(50);
            }
        }

        void OnPluginChange(object sender, FileSystemEventArgs e)
        {
            Utils.DebugPrintLine($"[PluginWatch] Plugin Change: {e.FullPath}");

            var info = ParseInfo(e.FullPath);
            if (info == null)
                return;

            if (!info.PluginRootFolder)
            {
                var pluginPath = Path.Combine(PluginsPath, info.PluginName);
                var plugin = FindPlugin(pluginPath);
                if (plugin == null)
                {
                    Utils.DebugPrintLine($"[PluginWatch] Unable to find registered plugin for {info.PluginName}");
                    return;
                }

                OnPluginFileChange(plugin, info);
            }
        }

        void OnPluginFileChange(Plugin plugin, PluginFileInfo info)
        {
            var fullPath = Path.Combine(PluginsPath, info.PluginName, info.FilePath);

            if (plugin.ConfigPath == fullPath)
            {
                Utils.DebugPrintLine("Plugin info modified, reloading meta...");
                var requiresRebuild = plugin.Info == null;

                var pluginInfo = GetPluginInfo(fullPath);
                if (pluginInfo == null)
                {
                    Utils.DebugPrintLine("Unable to read plugin meta");
                }

                plugin.Info = pluginInfo;
                if (requiresRebuild && pluginInfo != null)
                {
                    plugin.RequiresRebuild = true;
                    TriggerRebuild(50);
                }
            }
            else
            {
                if (plugin.SourceFiles.Contains(fullPath))
                {
                    plugin.RequiresRebuild = true;
                    TriggerRebuild(50);
                }
            }
        }

        public List<IPlugin> GetPluginInstances()
        {
            // If we are currently rebuilding we have to wait.
            WaitForRebuild();

            return Registered
                .Select(x => x.Instance as IPlugin)
                .Where(x => x != null)
                .ToList();
        }
    }
}
