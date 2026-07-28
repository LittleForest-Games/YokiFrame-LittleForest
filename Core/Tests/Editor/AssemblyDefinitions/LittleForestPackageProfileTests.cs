using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;

namespace YokiFrame.Tests
{
    /// <summary>
    /// Locks the Little Forest fork profile: YokiFrame is an Editor control-plane
    /// dependency, while every native Kit remains inactive and absent from Player.
    /// </summary>
    public sealed class LittleForestPackageProfileTests
    {
        private const string PackageName = "com.hinatayoki.yokiframe";
        private const string AllowedAutoEntry =
            "Core/Adapters/Unity/Editor/LittleForestControlPlane/"
            + "YokiFrameLittleForestControlPlaneHost.cs";

        private static readonly Regex sEditorOnlyPlatforms = new Regex(
            "\"includePlatforms\"\\s*:\\s*\\[\\s*\"Editor\"\\s*\\]",
            RegexOptions.CultureInvariant);

        private static readonly Regex sAutomaticEntry = new Regex(
            "\\[\\s*(?:(?:UnityEditor|UnityEngine)(?:\\.[A-Za-z_][A-Za-z0-9_]*)*\\.)?"
            + "(?:InitializeOnLoad(?:Method)?|RuntimeInitializeOnLoadMethod"
            + "|DidReloadScripts|OnOpenAsset|PostProcessScene|PostProcessBuild"
            + "|InitializeOnEnterPlayMode)"
            + "(?:Attribute)?\\b",
            RegexOptions.CultureInvariant);

        private static readonly Regex sAutomaticCallbackBase = new Regex(
            ":\\s*(?:(?:UnityEditor)\\.)?"
            + "(?:AssetPostprocessor|AssetModificationProcessor)\\b",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// Every production asmdef is Editor-only. Complete Kit source remains in
        /// the fork for coherent upstream synchronization and design reference,
        /// but it cannot become a Player assembly.
        /// </summary>
        [Test]
        public void EveryProductionAssemblyDefinitionIsEditorOnly()
        {
            string packageRoot = GetPackageRoot();
            string[] asmdefPaths = Directory.GetFiles(
                packageRoot,
                "*.asmdef",
                SearchOption.AllDirectories);

            var violations = new List<string>();
            foreach (string asmdefPath in asmdefPaths)
            {
                string relativePath = GetRelativePath(packageRoot, asmdefPath);
                if (IsTestPath(relativePath))
                {
                    continue;
                }

                string asmdef = File.ReadAllText(asmdefPath);
                if (!sEditorOnlyPlatforms.IsMatch(asmdef))
                {
                    violations.Add(relativePath);
                }
            }

            Assert.IsEmpty(
                violations,
                "Little Forest fork 中所有生产程序集都必须只包含 Editor 平台:\n"
                + string.Join("\n", violations));
        }

        /// <summary>
        /// Unity's actual Player compilation graph must contain no source owned by
        /// this package, independent of assembly naming conventions.
        /// </summary>
        [Test]
        public void PlayerCompilationGraphContainsNoYokiFramePackageSources()
        {
            string packageRoot = NormalizePath(GetPackageRoot()).TrimEnd('/') + "/";
            UnityEditor.Compilation.Assembly[] playerAssemblies =
                CompilationPipeline.GetAssemblies(
                    AssembliesType.PlayerWithoutTestAssemblies);

            var violations = new List<string>();
            foreach (UnityEditor.Compilation.Assembly assembly in playerAssemblies)
            {
                string[] sourceFiles = assembly.sourceFiles ?? Array.Empty<string>();
                foreach (string sourceFile in sourceFiles)
                {
                    string normalizedSource = NormalizePath(sourceFile);
                    if (normalizedSource.StartsWith(
                            packageRoot,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string relativeSource =
                            normalizedSource.Substring(packageRoot.Length);
                        if (IsGodotOnlySource(sourceFile, relativeSource))
                        {
                            continue;
                        }

                        violations.Add(
                            assembly.name + ": "
                            + relativeSource);
                    }
                }
            }

            Assert.IsEmpty(
                violations,
                "Player 编译图不得包含任何 YokiFrame package 源码:\n"
                + string.Join("\n", violations));
        }

        /// <summary>
        /// Unity serializes every asset beneath a Resources directory into the
        /// Player even when its owning asmdef is Editor-only. Production package
        /// content must therefore keep this magic directory empty or native-Kit
        /// assets and MonoScript metadata become Player residue.
        /// </summary>
        [Test]
        public void ProductionUnityResourcesDirectoriesContainNoAssets()
        {
            string packageRoot = GetPackageRoot();
            string[] assetPaths = Directory.GetFiles(
                packageRoot,
                "*",
                SearchOption.AllDirectories);

            var violations = new List<string>();
            foreach (string assetPath in assetPaths)
            {
                string relativePath = GetRelativePath(packageRoot, assetPath);
                if (IsTestPath(relativePath)
                    || IsUnityIgnoredPath(relativePath))
                {
                    continue;
                }

                string[] segments = relativePath.Split('/');
                if (segments.Any(segment =>
                        string.Equals(
                            segment,
                            "Resources",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add(relativePath);
                }
            }

            Assert.IsEmpty(
                violations,
                "生产源码不得位于 Unity Resources 特殊目录，否则 MonoScript 会进入 Player:\n"
                + string.Join("\n", violations));
        }

        /// <summary>
        /// The package has exactly one automatic Unity entry point: the dedicated
        /// Little Forest Local IPC Host. Native Kits, FileBridge and Workbench do
        /// not self-register or initialize.
        /// </summary>
        [Test]
        public void OnlyLittleForestControlPlaneHostOwnsAutomaticEntry()
        {
            string packageRoot = GetPackageRoot();
            string[] sourcePaths = Directory.GetFiles(
                packageRoot,
                "*.cs",
                SearchOption.AllDirectories);

            var entries = new List<string>();
            foreach (string sourcePath in sourcePaths)
            {
                string relativePath = GetRelativePath(packageRoot, sourcePath);
                if (IsTestPath(relativePath))
                {
                    continue;
                }

                string source = File.ReadAllText(sourcePath);
                if (sAutomaticEntry.IsMatch(source)
                    || sAutomaticCallbackBase.IsMatch(source))
                {
                    entries.Add(relativePath);
                }
            }

            CollectionAssert.AreEquivalent(
                new[] { AllowedAutoEntry },
                entries,
                "Little Forest fork 只允许自有 Local IPC Host 自动进入 Unity 生命周期。");
        }

        /// <summary>
        /// The sole Host owns the Local IPC v1 path and explicitly refuses a
        /// FileBridge fallback, preventing two command/session truth paths.
        /// </summary>
        [Test]
        public void ControlPlaneHostUsesLocalIpcWithoutFileBridgeFallback()
        {
            string packageRoot = GetPackageRoot();
            string source = File.ReadAllText(
                Path.Combine(
                    packageRoot,
                    AllowedAutoEntry.Replace('/', Path.DirectorySeparatorChar)));

            StringAssert.Contains("Win32NamedPipeHost", source);
            StringAssert.Contains("CommandTransport = \"local-ipc-v1\"", source);
            StringAssert.Contains("FileBridge fallback is disabled", source);
            Assert.IsFalse(
                source.Contains("YokiFrameEditorFileBridgePump"),
                "Little Forest Host 禁止启动或委托给上游 FileBridge pump。");
        }

        /// <summary>
        /// Little Forest owns its control-panel shortcut surface. The retained
        /// upstream Workbench reference may be opened explicitly from its menu,
        /// but it cannot claim Ctrl+E or another global shortcut.
        /// </summary>
        [Test]
        public void UpstreamWorkbenchDoesNotRegisterGlobalShortcut()
        {
            string source = File.ReadAllText(
                Path.Combine(
                    GetPackageRoot(),
                    "Core",
                    "Adapters",
                    "Unity",
                    "Editor",
                    "WorkbenchLauncher",
                    "YokiFrameWorkbenchLauncher.cs"));

            Assert.IsFalse(
                source.Contains("Open %e"),
                "上游 Workbench 禁止占用 Ctrl+E。");
            Assert.IsFalse(
                source.Contains("[Shortcut("),
                "上游 Workbench 禁止注册全局 Shortcut。");
        }

        private static string GetPackageRoot()
        {
            PackageInfo package = PackageInfo
                .GetAllRegisteredPackages()
                .FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.name,
                        PackageName,
                        StringComparison.Ordinal));

            if (package != null
                && !string.IsNullOrWhiteSpace(package.resolvedPath))
            {
                return Path.GetFullPath(package.resolvedPath);
            }

            string upstreamTestLayout = Path.Combine(
                UnityEngine.Application.dataPath,
                "YokiFrame");
            Assert.IsTrue(
                Directory.Exists(upstreamTestLayout),
                "当前验证工程既没有注册 YokiFrame package，也没有上游测试要求的 Assets/YokiFrame 布局。");
            return Path.GetFullPath(upstreamTestLayout);
        }

        private static bool IsTestPath(string relativePath)
        {
            return relativePath.IndexOf(
                       "/Tests/",
                       StringComparison.OrdinalIgnoreCase) >= 0
                   || relativePath.StartsWith(
                       "Tests/",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUnityIgnoredPath(string relativePath)
        {
            return relativePath.Split('/').Any(segment =>
                segment.EndsWith("~", StringComparison.Ordinal));
        }

        private static bool IsGodotOnlySource(
            string sourcePath,
            string relativePath)
        {
            if (relativePath.IndexOf(
                    "/Adapters/Godot/",
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            string source = File.ReadAllText(sourcePath).TrimStart();
            Assert.IsTrue(
                source.StartsWith("#if GODOT", StringComparison.Ordinal),
                "Assets/YokiFrame 上游测试布局中的 Godot Adapter 必须整文件排除 Unity Player: "
                + relativePath);
            return true;
        }

        private static string GetRelativePath(
            string packageRoot,
            string path)
        {
            string normalizedRoot = NormalizePath(packageRoot).TrimEnd('/') + "/";
            string normalizedPath = NormalizePath(path);
            Assert.IsTrue(
                normalizedPath.StartsWith(
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase),
                "路径不属于当前 YokiFrame package: " + normalizedPath);
            return normalizedPath.Substring(normalizedRoot.Length);
        }

        private static string NormalizePath(string path)
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
    }
}
