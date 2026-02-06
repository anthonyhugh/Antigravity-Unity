/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Unity Technologies.
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *  Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;

namespace Antigravity.Ide.Editor
{
    public interface IAssemblyNameProvider
    {
        string[] ProjectSupportedExtensions { get; }
        string ProjectGenerationRootNamespace { get; }
        ProjectGenerationFlag ProjectGenerationFlag { get; }

        string GetAssemblyNameFromScriptPath(string path);
        string GetAssemblyName(string assemblyOutputPath, string assemblyName);
        bool IsInternalizedPackagePath(string path);
        IEnumerable<Assembly> GetAssemblies(Func<string, bool> shouldFileBePartOfSolution);
        IEnumerable<string> GetAllAssetPaths();
        UnityEditor.PackageManager.PackageInfo FindForAssetPath(string assetPath);
        UnityEditor.Compilation.ResponseFileData ParseResponseFile(string responseFilePath, string projectDirectory, string[] systemReferenceDirectories);
        void ToggleProjectGeneration(ProjectGenerationFlag preference);
        IEnumerable<string> GetAnalyzers(string assemblyName, IEnumerable<Assembly> allAssemblies);
        string GetAnalyzerRulesetPath(string assemblyName, IEnumerable<Assembly> allAssemblies);
        string GetAnalyzerConfigPath(string assemblyName, IEnumerable<Assembly> allAssemblies);
        IEnumerable<string> GetAdditionalFilePaths(string assemblyName, IEnumerable<Assembly> allAssemblies);
        string[] GetSystemReferenceDirectories(string assemblyName);
    }

    public class AssemblyNameProvider : IAssemblyNameProvider
    {
        private readonly Dictionary<string, UnityEditor.PackageManager.PackageInfo> m_PackageInfoCache = new Dictionary<string, UnityEditor.PackageManager.PackageInfo>();

        ProjectGenerationFlag m_ProjectGenerationFlag = (ProjectGenerationFlag)EditorPrefs.GetInt(
            "antigravity_project_generation_flag",
            (int)(ProjectGenerationFlag.Local | ProjectGenerationFlag.Embedded));

        public string[] ProjectSupportedExtensions => EditorSettings.projectGenerationUserExtensions;

        public string ProjectGenerationRootNamespace => EditorSettings.projectGenerationRootNamespace;

        public ProjectGenerationFlag ProjectGenerationFlag
        {
            get { return ProjectGenerationFlagImpl; }
            private set { ProjectGenerationFlagImpl = value; }
        }

        internal virtual ProjectGenerationFlag ProjectGenerationFlagImpl
        {
            get => m_ProjectGenerationFlag;
            private set
            {
                EditorPrefs.SetInt("antigravity_project_generation_flag", (int)value);
                m_ProjectGenerationFlag = value;
            }
        }

        public string GetAssemblyNameFromScriptPath(string path)
        {
            return UnityEditor.Compilation.CompilationPipeline.GetAssemblyNameFromScriptPath(path);
        }

        internal static readonly string AssemblyOutput = @"Temp\bin\Debug\".NormalizePathSeparators();
        internal static readonly string ScriptAssemblyOutput = @"Library\ScriptAssemblies\".NormalizePathSeparators();
        internal static readonly string PlayerAssemblyOutput = @"Temp\bin\Debug\Player\".NormalizePathSeparators();

        public IEnumerable<Assembly> GetAssemblies(Func<string, bool> shouldFileBePartOfSolution)
        {
            IEnumerable<Assembly> assemblies = GetAssembliesByType(UnityEditor.Compilation.AssembliesType.Editor, shouldFileBePartOfSolution, AssemblyOutput);

            if (!ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.PlayerAssemblies))
            {
                return assemblies;
            }
            var playerAssemblies = GetAssembliesByType(UnityEditor.Compilation.AssembliesType.Player, shouldFileBePartOfSolution, PlayerAssemblyOutput);
            return assemblies.Concat(playerAssemblies);
        }

        private static IEnumerable<Assembly> GetAssembliesByType(UnityEditor.Compilation.AssembliesType type, Func<string, bool> shouldFileBePartOfSolution, string outputPath)
        {
            foreach (var assembly in UnityEditor.Compilation.CompilationPipeline.GetAssemblies(type))
            {
                if (assembly.sourceFiles.Any(shouldFileBePartOfSolution))
                {
                    var options = new ScriptCompilerOptions
                    {
                        ResponseFiles = assembly.compilerOptions.ResponseFiles,
                        AllowUnsafeCode = assembly.compilerOptions.AllowUnsafeCode,
                        ApiCompatibilityLevel = assembly.compilerOptions.ApiCompatibilityLevel,
                        languageVersion = "latest" // Default or extract if accessible via reflection/property
                    };

                    yield return new Assembly(
                        assembly.name,
                        assembly.name.StartsWith("Unity") ? ScriptAssemblyOutput : outputPath,  // Unity assemblies go to ScriptAssemblies, others go to Temp/bin/Debug
                        assembly.sourceFiles,
                        assembly.defines,
                        assembly.assemblyReferences?.Select(r => r.name).ToArray() ?? new string[0],
                        assembly.compiledAssemblyReferences,
                        assembly.flags,
                        options,
#if UNITY_2020_2_OR_NEWER
                        assembly.rootNamespace
#else
                        ""
#endif
                    );
                }
            }
        }

        public string GetCompileOutputPath(string assemblyName)
        {
            if (assemblyName.EndsWith(".Player", StringComparison.Ordinal))
                return PlayerAssemblyOutput;
            return AssemblyOutput;
        }

        public IEnumerable<string> GetAnalyzers(string assemblyName, IEnumerable<Assembly> allAssemblies)
        {
            return new string[0];
        }

        public string GetAnalyzerRulesetPath(string assemblyName, IEnumerable<Assembly> allAssemblies)
        {
            return string.Empty;
        }

        public string GetAnalyzerConfigPath(string assemblyName, IEnumerable<Assembly> allAssemblies)
        {
            return string.Empty;
        }

        public IEnumerable<string> GetAdditionalFilePaths(string assemblyName, IEnumerable<Assembly> allAssemblies)
        {
            return new string[0];
        }

        public string[] GetSystemReferenceDirectories(string assemblyName)
        {
            return new string[0];
        }

        public IEnumerable<string> GetAllAssetPaths()
        {
            return AssetDatabase.GetAllAssetPaths();
        }

        private static string ResolvePotentialParentPackageAssetPath(string assetPath)
        {
            const string packagesPrefix = "packages/";
            if (!assetPath.StartsWith(packagesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var followupSeparator = assetPath.IndexOf('/', packagesPrefix.Length);
            if (followupSeparator == -1)
            {
                return assetPath.ToLowerInvariant();
            }

            return assetPath.Substring(0, followupSeparator).ToLowerInvariant();
        }

        public UnityEditor.PackageManager.PackageInfo FindForAssetPath(string assetPath)
        {
            var parentPackageAssetPath = ResolvePotentialParentPackageAssetPath(assetPath);
            if (parentPackageAssetPath == null)
            {
                return null;
            }

            if (m_PackageInfoCache.TryGetValue(parentPackageAssetPath, out var cachedPackageInfo))
            {
                return cachedPackageInfo;
            }

            var result = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(parentPackageAssetPath);
            m_PackageInfoCache[parentPackageAssetPath] = result;
            return result;
        }

        public bool IsInternalizedPackagePath(string path)
        {
            if (string.IsNullOrEmpty(path.Trim()))
            {
                return false;
            }
            var packageInfo = FindForAssetPath(path);
            if (packageInfo == null)
            {
                return false;
            }
            var packageSource = packageInfo.source;
            switch (packageSource)
            {
                case PackageSource.Embedded:
                    return !ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.Embedded);
                case PackageSource.Registry:
                    return !ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.Registry);
                case PackageSource.BuiltIn:
                    return !ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.BuiltIn);
                case PackageSource.Unknown:
                    return !ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.Unknown);
                case PackageSource.Local:
                    return !ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.Local);
                case PackageSource.Git:
                    return !ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.Git);
                case PackageSource.LocalTarball:
                    return !ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.LocalTarBall);
            }

            return false;
        }

        public UnityEditor.Compilation.ResponseFileData ParseResponseFile(string responseFilePath, string projectDirectory, string[] systemReferenceDirectories)
        {
            return UnityEditor.Compilation.CompilationPipeline.ParseResponseFile(
              responseFilePath,
              projectDirectory,
              systemReferenceDirectories
            );
        }

        public void ToggleProjectGeneration(ProjectGenerationFlag preference)
        {
            if (ProjectGenerationFlag.HasFlag(preference))
            {
                ProjectGenerationFlag ^= preference;
            }
            else
            {
                ProjectGenerationFlag |= preference;
            }
        }

        internal void ResetPackageInfoCache()
        {
            m_PackageInfoCache.Clear();
        }

        public void ResetProjectGenerationFlag()
        {
            ProjectGenerationFlag = ProjectGenerationFlag.None;
        }

        public string GetAssemblyName(string assemblyOutputPath, string assemblyName)
        {
            if (assemblyOutputPath == PlayerAssemblyOutput)
                return assemblyName + ".Player";

            return assemblyName;
        }
    }
}
