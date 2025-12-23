/*---------------------------------------------------------------------------------------------
 * Copyright (c) Unity Technologies.
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Profiling;

namespace Antigravity.Ide.Editor
{
    public interface IGenerator
    {
        bool SyncIfNeeded(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles);
        void Sync();
        bool HasSolutionBeenGenerated();
        string SolutionFile();
        string ProjectDirectory { get; }
        IAssemblyNameProvider AssemblyNameProvider { get; }
        bool IsSupportedFile(string path);
    }

    internal class ProjectGeneration : IGenerator
    {
        public const string MSBuildNamespaceUri = "http://schemas.microsoft.com/developer/msbuild/2003";

        internal static readonly Dictionary<string, ScriptingLanguage> k_ProjectExtensions = new Dictionary<string, ScriptingLanguage>
        {
            { "cs", ScriptingLanguage.CSharp },
            { ".cs", ScriptingLanguage.CSharp },
        };

        internal static readonly Regex InvalidCharactersRegexPattern = new Regex(@"[<>:""|?*]");

        private readonly string m_SolutionProjectEntryTemplate = string.Join(k_WindowsNewline,
            @"Project(""{{{0}}}"") = ""{1}"", ""{2}"", ""{{{3}}}""",
            @"{4}EndProject").Replace("    ", "\t");

        private readonly string m_SolutionProjectConfigurationTemplate = string.Join(k_WindowsNewline,
            @"		{{{0}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
            @"		{{{0}}}.Debug|Any CPU.Build.0 = Debug|Any CPU",
            @"		{{{0}}}.Release|Any CPU.ActiveCfg = Release|Any CPU",
            @"		{{{0}}}.Release|Any CPU.Build.0 = Release|Any CPU").Replace("    ", "\t");

        static readonly string[] k_ReimportSyncExtensions = { ".dll", ".asmdef" };

        internal static bool IsSupportedExtension(string extension)
        {
            extension = extension.TrimStart('.');
            return k_ProjectExtensions.ContainsKey(extension);
        }

        internal static ScriptingLanguage ScriptingLanguageFor(Assembly assembly)
        {
            return ScriptingLanguageFor(assembly.compilerOptions.languageVersion);
        }

        internal static ScriptingLanguage ScriptingLanguageFor(string extension)
        {
            extension = extension.TrimStart('.');
            return k_ProjectExtensions.TryGetValue(extension, out var result)
                ? result
                : ScriptingLanguage.None;
        }

        internal static readonly string k_WindowsNewline = "\r\n";
        internal const string k_ToolsVersion = "ToolsVersion=\"4.0\"";
        internal const string k_ProductVersion = "10.0.20506";
        internal const string k_BaseDirectory = ".";
        internal const string k_TargetLanguageVersion = "latest";

        internal readonly IAssemblyNameProvider m_AssemblyNameProvider;
        internal readonly IFileIO m_FileIOProvider;
        internal readonly IGUIDGenerator m_GUIDGenerator;
        internal readonly string m_ProjectName;

        public string ProjectDirectory { get; }
        public IAssemblyNameProvider AssemblyNameProvider => m_AssemblyNameProvider;

        public ProjectGeneration() : this(Directory.GetParent(Application.dataPath)?.FullName) { }

        public ProjectGeneration(string tempDirectory) : this(tempDirectory, new AssemblyNameProvider(), new FileIOProvider(), new GUIDProvider()) { }

        public ProjectGeneration(string tempDirectory, IAssemblyNameProvider assemblyNameProvider, IFileIO fileIO, IGUIDGenerator guidGenerator)
        {
            ProjectDirectory = tempDirectory.NormalizePathSeparators();
            m_ProjectName = Path.GetFileName(ProjectDirectory);
            m_AssemblyNameProvider = assemblyNameProvider;
            m_FileIOProvider = fileIO;
            m_GUIDGenerator = guidGenerator;
        }

        public bool SyncIfNeeded(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles)
        {
            Profiler.BeginSample("AntigravityEditor.SyncIfNeeded");
            try
            {
                if ((HasFilesBeenModified(affectedFiles, reimportedFiles) ||
                    m_AssemblyNameProvider.ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.Unknown))
                    && ShouldSyncOn(affectedFiles, reimportedFiles))
                {
                    Sync();
                    return true;
                }
            }
            finally
            {
                Profiler.EndSample();
            }
            return false;
        }

        private bool HasFilesBeenModified(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles)
        {
            return affectedFiles.Any(ShouldFileBePartOfSolution) || reimportedFiles.Any(ShouldSyncOnReimportedAsset);
        }

        private static bool ShouldSyncOnReimportedAsset(string asset)
        {
            return k_ReimportSyncExtensions.Contains(new FileInfo(asset).Extension);
        }

        internal virtual bool ShouldSyncOn(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles)
        {
            if (reimportedFiles.Any()) return true;
            return affectedFiles.Any();
        }

        public void Sync()
        {
            var assemblies = m_AssemblyNameProvider.GetAssemblies(ShouldFileBePartOfSolution);
            var allAssetProjectParts = GenerateAllAssetProjectParts();
            var responseFilesData = ParseResponseFileData(assemblies).ToList();

            var newAssemblies = new List<Assembly>();
            foreach (var assembly in assemblies)
            {
                var options = new ScriptCompilerOptions
                {
                    ResponseFiles = assembly.compilerOptions.ResponseFiles.Concat(responseFilesData.SelectMany(x => x.Errors).Distinct()).ToArray(),
                    AllowUnsafeCode = assembly.compilerOptions.AllowUnsafeCode,
                    ApiCompatibilityLevel = assembly.compilerOptions.ApiCompatibilityLevel,
                    languageVersion = assembly.compilerOptions.languageVersion
                };
                var newAssembly = new Assembly(assembly.name, assembly.outputPath, assembly.sourceFiles, assembly.defines, assembly.references, assembly.compiledAssemblyReferences, assembly.flags, options, assembly.rootNamespace);
                newAssemblies.Add(newAssembly);
            }

            if (ShouldGenerateProject())
            {
                Profiler.BeginSample("AntigravityEditor.SyncSolution");
                SyncSolution(newAssemblies);
                Profiler.EndSample();
            }

            foreach (var assembly in newAssemblies)
            {
                Profiler.BeginSample("AntigravityEditor.SyncProject");
                try
                {
                    SyncProject(assembly, allAssetProjectParts, responseFilesData, newAssemblies);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Antigravity] Failed to generate project for {assembly.name}: {e}");
                }
                Profiler.EndSample();
            }

            m_AssemblyNameProvider.ToggleProjectGeneration(ProjectGenerationFlag.None);
        }

        public bool HasSolutionBeenGenerated()
        {
            return m_FileIOProvider.Exists(SolutionFile());
        }

        private bool ShouldGenerateProject()
        {
            if (!m_AssemblyNameProvider.ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.PlayerAssemblies) &&
                !m_AssemblyNameProvider.ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.Unknown))
            {
                return false;
            }
            return true;
        }

        public string SolutionFile()
        {
            return Path.Combine(ProjectDirectory, $"{m_ProjectName}.sln");
        }

        internal string ProjectFile(Assembly assembly)
        {
            var fileBuilder = new StringBuilder(assembly.name);
            if (!m_AssemblyNameProvider.IsInternalizedPackagePath(assembly.outputPath))
            {
                if (m_AssemblyNameProvider.ProjectGenerationFlag.HasFlag(ProjectGenerationFlag.PlayerAssemblies))
                {
                    fileBuilder.Append(".Player");
                }
            }
            fileBuilder.Append(GetProjectExtension());
            return Path.Combine(ProjectDirectory, fileBuilder.ToString());
        }

        internal string ProjectFile(string assemblyName)
        {
            return Path.Combine(ProjectDirectory, $"{assemblyName}{GetProjectExtension()}");
        }

        private void SyncProject(
            Assembly assembly,
            Dictionary<string, string> allAssetsProjectParts,
            IEnumerable<ResponseFileData> responseFilesData,
            List<Assembly> allAssemblies)
        {
            SyncProjectFileIfNotChanged(
                ProjectFile(assembly),
                ProjectText(assembly, allAssetsProjectParts, responseFilesData, allAssemblies)
            );
        }

        private void SyncProjectFileIfNotChanged(string path, string newContents)
        {
            SyncFileIfNotChanged(path, newContents);
        }

        private void SyncSolutionFileIfNotChanged(string path, string newContents)
        {
            newContents = newContents.Replace("\r\n", "\n").Replace("\n", k_WindowsNewline);
            SyncFileIfNotChanged(path, newContents);
        }

        private void SyncFileIfNotChanged(string filename, string newContents)
        {
            try
            {
                if (m_FileIOProvider.Exists(filename) && newContents == m_FileIOProvider.ReadAllText(filename))
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            m_FileIOProvider.WriteAllText(filename, newContents);
        }

        internal string ProjectText(
            Assembly assembly,
            Dictionary<string, string> allAssetsProjectParts,
            IEnumerable<ResponseFileData> responseFilesData,
            List<Assembly> allAssemblies)
        {
            var projectBuilder = new StringBuilder();
            var properties = new ProjectProperties();

            // Setup properties
            properties.ProjectGuid = ProjectGuid(assembly);
            properties.LangVersion = k_TargetLanguageVersion;
            properties.AssemblyName = assembly.name;
            properties.RootNamespace = GetRootNamespace(assembly);
            properties.OutputPath = assembly.outputPath;
            properties.Analyzers = m_AssemblyNameProvider.GetAnalyzers(assembly.name, allAssemblies).ToArray();
            properties.RulesetPath = m_AssemblyNameProvider.GetAnalyzerRulesetPath(assembly.name, allAssemblies);
            properties.AnalyzerConfigPath = m_AssemblyNameProvider.GetAnalyzerConfigPath(assembly.name, allAssemblies);
            properties.AdditionalFilePaths = m_AssemblyNameProvider.GetAdditionalFilePaths(assembly.name, allAssemblies).ToArray();

            // RSP alterable
            foreach (var responseFileData in responseFilesData)
            {
                properties.Defines = properties.Defines.Concat(responseFileData.Defines).ToArray();
                properties.Unsafe |= responseFileData.Unsafe;
            }

            properties.Defines = properties.Defines.Concat(assembly.defines).Distinct().ToArray();
            properties.Unsafe |= assembly.compilerOptions.AllowUnsafeCode;

            // VSTU Flavouring
            properties.FlavoringProjectType = "Local";
            properties.FlavoringBuildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();
            properties.FlavoringUnityVersion = Application.unityVersion;
            properties.FlavoringPackageVersion = "2.0.22";

            // HEADER GENERATION
            GetProjectHeader(properties, out var headerBuilder);
            projectBuilder.Append(headerBuilder);

            // FILES GENERATION
            projectBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);
            var files = assembly.sourceFiles;
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file).ToLower();
                var fullPath = Path.GetFullPath(file);

                // This logic ensures we handle packages vs local assets correctly
                projectBuilder.Append(@"    <Compile Include=""").Append(EscapedRelativePathFor(file, out var packageInfo)).Append(@""" />").Append(k_WindowsNewline);
            }
            // Append assets (if any)
            if (allAssetsProjectParts.TryGetValue(assembly.name, out var assetsProjectPart))
                projectBuilder.Append(assetsProjectPart);
            projectBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);

            // REFERENCES GENERATION
            projectBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);

            // 1. Project References (Cross-Ref support)
            foreach (var referenceName in assembly.references)
            {
                var reference = allAssemblies.FirstOrDefault(a => a.name == referenceName);
                if (reference != null)
                {
                    AppendProjectReference(assembly, reference, projectBuilder);
                }
            }

            // 2. DLL References
            foreach (var compiledRef in assembly.compiledAssemblyReferences)
            {
                var relativePath = FileUtility.MakeRelativeToProjectPath(compiledRef);
                var referenceName = Path.GetFileNameWithoutExtension(compiledRef);
                projectBuilder.Append(@"    <Reference Include=""").Append(referenceName).Append(@""">").Append(k_WindowsNewline);
                projectBuilder.Append(@"        <HintPath>").Append(relativePath).Append(@"</HintPath>").Append(k_WindowsNewline);
                projectBuilder.Append(@"    </Reference>").Append(k_WindowsNewline);
            }
            projectBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);

            // FOOTER GENERATION (CRITICAL FOR VALIDITY)
            GetProjectFooter(projectBuilder);
            return projectBuilder.ToString();
        }

        internal string XmlFilename(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            path = path.Replace(@"%", "%25");
            path = path.Replace(@";", "%3b");
            return XmlEscape(path);
        }

        internal string XmlEscape(string s)
        {
            return System.Security.SecurityElement.Escape(s);
        }

        internal virtual void AppendProjectReference(Assembly assembly, Assembly reference, StringBuilder projectBuilder)
        {
            var referenceName = reference.name;
            var projectReferenceGuid = ProjectGuid(reference);
            projectBuilder.Append(@"    <ProjectReference Include=""").Append(referenceName).Append(GetProjectExtension()).Append(@""">").Append(k_WindowsNewline);
            projectBuilder.Append(@"        <Project>{").Append(projectReferenceGuid).Append(@"}</Project>").Append(k_WindowsNewline);
            projectBuilder.Append(@"        <Name>").Append(referenceName).Append(@"</Name>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    </ProjectReference>").Append(k_WindowsNewline);
        }

        internal virtual string StyleName => "Legacy";

        internal virtual void GetProjectHeader(ProjectProperties properties, out StringBuilder headerBuilder)
        {
            headerBuilder = new StringBuilder();

            // Standard MSBuild Header
            headerBuilder.Append(@"<?xml version=""1.0"" encoding=""utf-8""?>").Append(k_WindowsNewline);
            headerBuilder.Append($@"<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""{MSBuildNamespaceUri}"">").Append(k_WindowsNewline);
            headerBuilder.Append(@"  ").Append(k_WindowsNewline);
            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <LangVersion>").Append(properties.LangVersion).Append(@"</LangVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            GetProjectHeaderConfigurations(properties, headerBuilder);

            // Standard Unity Settings
            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <NoConfig>true</NoConfig>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <NoStdLib>true</NoStdLib>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AddAdditionalExplicitAssemblyReferences>false</AddAdditionalExplicitAssemblyReferences>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ImplicitlyExpandNETStandardFacades>false</ImplicitlyExpandNETStandardFacades>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ImplicitlyExpandDesignTimeFacades>false</ImplicitlyExpandDesignTimeFacades>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            GetProjectHeaderVstuFlavoring(properties, headerBuilder);
            GetProjectHeaderAnalyzers(properties, headerBuilder);
        }

        private Dictionary<string, string> GenerateAllAssetProjectParts()
        {
            // Placeholder for asset handling if needed in future
            return new Dictionary<string, string>();
        }

        private IEnumerable<ResponseFileData> ParseResponseFileData(IEnumerable<Assembly> assemblies)
        {
            foreach (var assembly in assemblies)
            {
                foreach (var responseFile in assembly.compilerOptions.ResponseFiles)
                {
                    yield return m_AssemblyNameProvider.ParseResponseFile(
                        responseFile,
                        ProjectDirectory,
                        m_AssemblyNameProvider.GetSystemReferenceDirectories(assembly.name));
                }
            }
        }

        internal bool ShouldFileBePartOfSolution(string file)
        {
            var extension = Path.GetExtension(file);
            return IsSupportedExtension(extension);
        }

        public bool IsSupportedFile(string path)
        {
            return IsSupportedExtension(Path.GetExtension(path));
        }

        internal void GetProjectHeaderVstuFlavoring(ProjectProperties properties, StringBuilder headerBuilder, bool includeProjectTypeGuids = true)
        {
            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            if (includeProjectTypeGuids)
            {
                headerBuilder.Append(@"    <ProjectTypeGuids>{E097FAD1-6243-4DAD-9C02-E9B9EFC3FFC1};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}</ProjectTypeGuids>").Append(k_WindowsNewline);
            }
            headerBuilder.Append(@"    <UnityProjectGenerator>Package</UnityProjectGenerator>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <UnityProjectGeneratorVersion>").Append(properties.FlavoringPackageVersion).Append(@"</UnityProjectGeneratorVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <UnityProjectGeneratorStyle>").Append(StyleName).Append("</UnityProjectGeneratorStyle>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <UnityProjectType>").Append(properties.FlavoringProjectType).Append(@"</UnityProjectType>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <UnityBuildTarget>").Append(properties.FlavoringBuildTarget).Append(@"</UnityBuildTarget>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <UnityVersion>").Append(properties.FlavoringUnityVersion).Append(@"</UnityVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);
        }

        internal void GetProjectHeaderAnalyzers(ProjectProperties properties, StringBuilder headerBuilder)
        {
            if (!string.IsNullOrEmpty(properties.RulesetPath))
            {
                headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
                headerBuilder.Append(@"    <CodeAnalysisRuleSet>").Append(properties.RulesetPath).Append(@"</CodeAnalysisRuleSet>").Append(k_WindowsNewline);
                headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);
            }
            if (properties.Analyzers.Any())
            {
                headerBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);
                foreach (var analyzer in properties.Analyzers)
                {
                    headerBuilder.Append(@"    <Analyzer Include=""").Append(analyzer).Append(@""" />").Append(k_WindowsNewline);
                }
                headerBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);
            }
            if (!string.IsNullOrEmpty(properties.AnalyzerConfigPath))
            {
                headerBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);
                headerBuilder.Append(@"    <EditorConfigFiles Include=""").Append(properties.AnalyzerConfigPath).Append(@""" />").Append(k_WindowsNewline);
                headerBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);
            }
            if (properties.AdditionalFilePaths.Any())
            {
                headerBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);
                foreach (var additionalFile in properties.AdditionalFilePaths)
                {
                    headerBuilder.Append(@"    <AdditionalFiles Include=""").Append(additionalFile).Append(@""" />").Append(k_WindowsNewline);
                }
                headerBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);
            }
        }

        internal void GetProjectHeaderConfigurations(ProjectProperties properties, StringBuilder headerBuilder)
        {
            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Configuration Condition="" '$(Configuration)' == '' "">Debug</Configuration>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Platform Condition="" '$(Platform)' == '' "">AnyCPU</Platform>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ProductVersion>").Append(k_ProductVersion).Append(@"</ProductVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <SchemaVersion>2.0</SchemaVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <RootNamespace>").Append(properties.RootNamespace).Append(@"</RootNamespace>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ProjectGuid>{").Append(properties.ProjectGuid).Append(@"}</ProjectGuid>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <OutputType>Library</OutputType>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AppDesignerFolder>Properties</AppDesignerFolder>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AssemblyName>").Append(properties.AssemblyName).Append(@"</AssemblyName>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <FileAlignment>512</FileAlignment>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <BaseDirectory>").Append(k_BaseDirectory).Append(@"</BaseDirectory>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            headerBuilder.Append(@"  <PropertyGroup Condition="" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' "">").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <DebugSymbols>true</DebugSymbols>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <DebugType>full</DebugType>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Optimize>false</Optimize>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <OutputPath>").Append(properties.OutputPath).Append(@"</OutputPath>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <DefineConstants>").Append(string.Join(";", properties.Defines.Concat(new[] { "DEBUG", "TRACE" }).Distinct().ToArray())).Append(@"</DefineConstants>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ErrorReport>prompt</ErrorReport>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <WarningLevel>4</WarningLevel>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <NoWarn>0169</NoWarn>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AllowUnsafeBlocks>").Append(properties.Unsafe.ToString().ToLower()).Append(@"</AllowUnsafeBlocks>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            headerBuilder.Append(@"  <PropertyGroup Condition="" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' "">").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <DebugType>pdbonly</DebugType>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Optimize>true</Optimize>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <OutputPath>").Append(properties.OutputPath).Append(@"</OutputPath>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <DefineConstants>").Append(string.Join(";", properties.Defines.Concat(new[] { "TRACE" }).Distinct().ToArray())).Append(@"</DefineConstants>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ErrorReport>prompt</ErrorReport>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <WarningLevel>4</WarningLevel>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <NoWarn>0169</NoWarn>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AllowUnsafeBlocks>").Append(properties.Unsafe.ToString().ToLower()).Append(@"</AllowUnsafeBlocks>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);
        }

        internal virtual void GetProjectFooter(StringBuilder footerBuilder)
        {
            // CRITICAL FIX: The previous version was empty. This imports the C# targets so it actually builds.
            footerBuilder.Append(string.Join(k_WindowsNewline,
                $"  <Import Project=\"{@"$(MSBuildToolsPath)\Microsoft.CSharp.targets".NormalizePathSeparators()}\" />",
                @"  <Target Name=""GenerateTargetFrameworkMonikerAttribute"" />",
                @"</Project>",
                @""));
        }

        internal string EscapedRelativePathFor(string file, out UnityEditor.PackageManager.PackageInfo packageInfo)
        {
            var projectDir = ProjectDirectory.NormalizePathSeparators();
            file = file.NormalizePathSeparators();
            var path = SkipPathPrefix(file, projectDir);

            packageInfo = m_AssemblyNameProvider.FindForAssetPath(path.NormalizeWindowsToUnix());
            if (packageInfo != null)
            {
                var absolutePath = Path.GetFullPath(path.NormalizePathSeparators());
                path = SkipPathPrefix(absolutePath, projectDir);
            }

            return XmlFilename(path);
        }

        internal static string SkipPathPrefix(string path, string prefix)
        {
            if (path.StartsWith($"{prefix}{Path.DirectorySeparatorChar}") && (path.Length > prefix.Length))
                return path.Substring(prefix.Length + 1);
            return path;
        }

        internal static string GetProjectExtension()
        {
            return ".csproj";
        }

        internal string ProjectGuid(string assemblyName)
        {
            return m_GUIDGenerator.ProjectGuid(m_ProjectName, assemblyName);
        }

        internal string ProjectGuid(Assembly assembly)
        {
            return ProjectGuid(m_AssemblyNameProvider.GetAssemblyName(assembly.outputPath, assembly.name));
        }

        private string SolutionGuid(Assembly assembly)
        {
            return m_GUIDGenerator.SolutionGuid(m_ProjectName, ScriptingLanguageFor(assembly));
        }

        private static string GetRootNamespace(Assembly assembly)
        {
#if UNITY_2020_2_OR_NEWER
            return assembly.rootNamespace;
#else
            return EditorSettings.projectGenerationRootNamespace;
#endif
        }

        private static string GetPropertiesText(SolutionProperties[] array)
        {
            if (array == null || array.Length == 0)
            {
                array = new[] {
                    new SolutionProperties() {
                        Name = "SolutionProperties",
                        Type = "preSolution",
                        Entries = new List<KeyValuePair<string,string>>() { new KeyValuePair<string, string> ("HideSolutionNode", "FALSE") }
                    }
                };
            }
            var result = new StringBuilder();

            for (var i = 0; i < array.Length; i++)
            {
                if (i > 0) result.Append(k_WindowsNewline);
                var properties = array[i];
                result.Append($"	GlobalSection({properties.Name}) = {properties.Type}");
                result.Append(k_WindowsNewline);
                foreach (var entry in properties.Entries)
                {
                    result.Append($"		{entry.Key} = {entry.Value}");
                    result.Append(k_WindowsNewline);
                }
                result.Append("	EndGlobalSection");
            }
            return result.ToString();
        }

        private string GetProjectEntriesText(IEnumerable<SolutionProjectEntry> entries)
        {
            var projectEntries = entries.Select(entry => string.Format(
                m_SolutionProjectEntryTemplate,
                entry.ProjectFactoryGuid, entry.Name, entry.FileName, entry.ProjectGuid, entry.Metadata
            ));
            return string.Join(k_WindowsNewline, projectEntries.ToArray());
        }

        private IEnumerable<SolutionProjectEntry> ToProjectEntries(IEnumerable<Assembly> assemblies)
        {
            foreach (var assembly in assemblies)
                yield return new SolutionProjectEntry()
                {
                    ProjectFactoryGuid = SolutionGuid(assembly),
                    Name = assembly.name,
                    FileName = Path.GetFileName(ProjectFile(assembly)),
                    ProjectGuid = ProjectGuid(assembly),
                    Metadata = k_WindowsNewline
                };
        }

        private string GetProjectActiveConfigurations(string projectGuid)
        {
            return string.Format(
                m_SolutionProjectConfigurationTemplate,
                projectGuid);
        }

        private static string GetSolutionText()
        {
            return string.Join(k_WindowsNewline,
            @"",
            @"Microsoft Visual Studio Solution File, Format Version {0}",
            @"# Visual Studio {1}",
            @"{2}",
            @"Global",
            @"	GlobalSection(SolutionConfigurationPlatforms) = preSolution",
            @"		Debug|Any CPU = Debug|Any CPU",
            @"		Release|Any CPU = Release|Any CPU",
            @"	EndGlobalSection",
            @"	GlobalSection(ProjectConfigurationPlatforms) = postSolution",
            @"{3}",
            @"	EndGlobalSection",
            @"{4}",
            @"EndGlobal",
            @"").Replace("    ", "\t");
        }

        private void SyncSolution(IEnumerable<Assembly> assemblies)
        {
            if (InvalidCharactersRegexPattern.IsMatch(ProjectDirectory))
                Debug.LogWarning("Project path contains special characters, which can be an issue when opening Visual Studio");

            var solutionFile = SolutionFile();
            var previousSolution = m_FileIOProvider.Exists(solutionFile) ? SolutionParser.ParseSolutionFile(solutionFile, m_FileIOProvider) : null;
            SyncSolutionFileIfNotChanged(solutionFile, SolutionText(assemblies, previousSolution));
        }

        private string SolutionText(IEnumerable<Assembly> assemblies, Solution previousSolution = null)
        {
            const string fileversion = "12.00";
            const string vsversion = "15";

            var relevantAssemblies = RelevantAssembliesForMode(assemblies);
            var generatedProjects = ToProjectEntries(relevantAssemblies).ToList();

            SolutionProperties[] properties = null;
            var projects = new List<SolutionProjectEntry>();
            projects.AddRange(generatedProjects);

            if (previousSolution != null)
            {
                var externalProjects = previousSolution.Projects
                    .Where(p => p.IsSolutionFolderProjectFactory() || !FileUtility.IsFileInProjectRootDirectory(p.FileName))
                    .Where(p => generatedProjects.All(gp => gp.FileName != p.FileName));

                projects.AddRange(externalProjects);
                properties = previousSolution.Properties;
            }

            string propertiesText = GetPropertiesText(properties);
            string projectEntriesText = GetProjectEntriesText(projects);
            var configurableProjects = projects.Where(p => !p.IsSolutionFolderProjectFactory());
            string projectConfigurationsText = string.Join(k_WindowsNewline, configurableProjects.Select(p => GetProjectActiveConfigurations(p.ProjectGuid)).ToArray());

            return string.Format(GetSolutionText(), fileversion, vsversion, projectEntriesText, projectConfigurationsText, propertiesText);
        }

        private static IEnumerable<Assembly> RelevantAssembliesForMode(IEnumerable<Assembly> assemblies)
        {
            return assemblies.Where(i => ScriptingLanguage.CSharp == ScriptingLanguageFor(i));
        }
    }

    public static class SolutionGuidGenerator
    {
        public static string GuidForProject(string projectName)
        {
            return ComputeGuidHashFor(projectName + "salt");
        }

        public static string GuidForSolution(string projectName, ScriptingLanguage language)
        {
            if (language == ScriptingLanguage.CSharp)
            {
                return "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
            }
            return ComputeGuidHashFor(projectName);
        }

        private static string ComputeGuidHashFor(string input)
        {
            var hash = MD5.Create().ComputeHash(Encoding.Default.GetBytes(input));
            return HashAsGuid(HashToString(hash));
        }

        private static string HashAsGuid(string hash)
        {
            var guid = hash.Substring(0, 8) + "-" + hash.Substring(8, 4) + "-" + hash.Substring(12, 4) + "-" + hash.Substring(16, 4) + "-" + hash.Substring(20, 12);
            return guid.ToUpper();
        }

        private static string HashToString(byte[] bs)
        {
            var sb = new StringBuilder();
            foreach (byte b in bs)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}