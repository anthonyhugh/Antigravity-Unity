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

        // FIX: Ensure we support standard C# extensions
        internal static readonly Dictionary<string, ScriptingLanguage> k_ProjectExtensions = new Dictionary<string, ScriptingLanguage>
        {
            { "cs", ScriptingLanguage.CSharp },
            { ".cs", ScriptingLanguage.CSharp },
        };

        internal static readonly Regex InvalidCharactersRegexPattern = new Regex(@"[<>:""|?*]");

        private readonly string m_SolutionProjectEntryTemplate = string.Join("\r\n",
            @"Project(""{{{0}}}"") = ""{1}"", ""{2}"", ""{{{3}}}""",
            @"{4}EndProject").Replace("    ", "\t");

        private readonly string m_SolutionProjectConfigurationTemplate = string.Join("\r\n",
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
            return ScriptingLanguageFor(Path.GetExtension(assembly.sourceFiles[0]));
        }

        internal static ScriptingLanguage ScriptingLanguageFor(string extension)
        {
            extension = extension.TrimStart('.');
            return k_ProjectExtensions.TryGetValue(extension, out var result) ? result : ScriptingLanguage.None;
        }

        internal static readonly string k_WindowsNewline = "\r\n";

        // FIX: Force version 4.0 which is compatible with Unity's internal Mono
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
            ProjectDirectory = tempDirectory.Replace('\\', '/');
            m_ProjectName = Path.GetFileName(ProjectDirectory);
            m_AssemblyNameProvider = assemblyNameProvider;
            m_FileIOProvider = fileIO;
            m_GUIDGenerator = guidGenerator;
        }

        public bool SyncIfNeeded(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles)
        {
            if (ShouldSyncOn(affectedFiles, reimportedFiles))
            {
                Sync();
                return true;
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

            // FIX: Simplified response file parsing to avoid IO errors
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

            SyncSolution(newAssemblies);

            foreach (var assembly in newAssemblies)
            {
                try
                {
                    SyncProject(assembly, allAssetProjectParts, responseFilesData, newAssemblies);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Antigravity] Failed to generate project {assembly.name}: {e}");
                }
            }
        }

        public bool HasSolutionBeenGenerated()
        {
            return m_FileIOProvider.Exists(SolutionFile());
        }

        public string SolutionFile()
        {
            return Path.Combine(ProjectDirectory, $"{m_ProjectName}.sln");
        }

        internal string ProjectFile(Assembly assembly)
        {
            return Path.Combine(ProjectDirectory, $"{assembly.name}.csproj");
        }

        private void SyncProject(Assembly assembly, Dictionary<string, string> allAssetsProjectParts, IEnumerable<ResponseFileData> responseFilesData, List<Assembly> allAssemblies)
        {
            SyncFileIfNotChanged(ProjectFile(assembly), ProjectText(assembly, allAssetsProjectParts, responseFilesData, allAssemblies));
        }

        private void SyncSolutionFileIfNotChanged(string path, string newContents)
        {
            newContents = newContents.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
            SyncFileIfNotChanged(path, newContents);
        }

        private void SyncFileIfNotChanged(string filename, string newContents)
        {
            if (m_FileIOProvider.Exists(filename) && newContents == m_FileIOProvider.ReadAllText(filename)) return;
            m_FileIOProvider.WriteAllText(filename, newContents);
        }

        internal string ProjectText(Assembly assembly, Dictionary<string, string> allAssetsProjectParts, IEnumerable<ResponseFileData> responseFilesData, List<Assembly> allAssemblies)
        {
            var projectBuilder = new StringBuilder();
            var properties = new ProjectProperties
            {
                ProjectGuid = ProjectGuid(assembly),
                LangVersion = k_TargetLanguageVersion,
                AssemblyName = assembly.name,
                RootNamespace = GetRootNamespace(assembly),
                OutputPath = assembly.outputPath,
                Defines = assembly.defines.Concat(responseFilesData.SelectMany(x => x.Defines)).Distinct().ToArray(),
                Unsafe = assembly.compilerOptions.AllowUnsafeCode
            };

            // HEADER GENERATION (Legacy)
            projectBuilder.Append(@"<?xml version=""1.0"" encoding=""utf-8""?>").Append(k_WindowsNewline);
            projectBuilder.Append($@"<Project ToolsVersion=""4.0"" DefaultTargets=""Build"" xmlns=""{MSBuildNamespaceUri}"">").Append(k_WindowsNewline);
            projectBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <LangVersion>").Append(properties.LangVersion).Append(@"</LangVersion>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <Configuration Condition="" '$(Configuration)' == '' "">Debug</Configuration>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <Platform Condition="" '$(Platform)' == '' "">AnyCPU</Platform>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <ProductVersion>10.0.20506</ProductVersion>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <SchemaVersion>2.0</SchemaVersion>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <RootNamespace>").Append(properties.RootNamespace).Append(@"</RootNamespace>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <ProjectGuid>{").Append(properties.ProjectGuid).Append(@"}</ProjectGuid>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <OutputType>Library</OutputType>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <AppDesignerFolder>Properties</AppDesignerFolder>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <AssemblyName>").Append(properties.AssemblyName).Append(@"</AssemblyName>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <FileAlignment>512</FileAlignment>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <BaseDirectory>.</BaseDirectory>").Append(k_WindowsNewline);
            projectBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            // CONFIGURATION BLOCKS
            projectBuilder.Append(@"  <PropertyGroup Condition="" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' "">").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <DebugSymbols>true</DebugSymbols>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <DebugType>full</DebugType>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <Optimize>false</Optimize>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <OutputPath>").Append(properties.OutputPath).Append(@"</OutputPath>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <DefineConstants>").Append(string.Join(";", properties.Defines.Concat(new[] { "DEBUG", "TRACE" }).Distinct())).Append(@"</DefineConstants>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <ErrorReport>prompt</ErrorReport>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <WarningLevel>4</WarningLevel>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <NoWarn>0169</NoWarn>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <AllowUnsafeBlocks>").Append(properties.Unsafe.ToString().ToLower()).Append(@"</AllowUnsafeBlocks>").Append(k_WindowsNewline);
            projectBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            // RELEASE BLOCK (Just in case)
            projectBuilder.Append(@"  <PropertyGroup Condition="" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' "">").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <DebugType>pdbonly</DebugType>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <Optimize>true</Optimize>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <OutputPath>").Append(properties.OutputPath).Append(@"</OutputPath>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <DefineConstants>").Append(string.Join(";", properties.Defines.Concat(new[] { "TRACE" }).Distinct())).Append(@"</DefineConstants>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <ErrorReport>prompt</ErrorReport>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <WarningLevel>4</WarningLevel>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <NoWarn>0169</NoWarn>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    <AllowUnsafeBlocks>").Append(properties.Unsafe.ToString().ToLower()).Append(@"</AllowUnsafeBlocks>").Append(k_WindowsNewline);
            projectBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            // FILES
            projectBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);
            foreach (var file in assembly.sourceFiles)
            {
                projectBuilder.Append(@"    <Compile Include=""").Append(XmlFilename(file)).Append(@""" />").Append(k_WindowsNewline);
            }
            projectBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);

            // REFERENCES
            projectBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);

            // 1. Project References (Other Unity Assemblies)
            foreach (var referenceName in assembly.references)
            {
                var reference = allAssemblies.FirstOrDefault(a => a.name == referenceName);
                if (reference != null)
                {
                    var guid = ProjectGuid(reference);
                    projectBuilder.Append(@"    <ProjectReference Include=""").Append(reference.name).Append(@".csproj"">").Append(k_WindowsNewline);
                    projectBuilder.Append(@"        <Project>{").Append(guid).Append(@"}</Project>").Append(k_WindowsNewline);
                    projectBuilder.Append(@"        <Name>").Append(reference.name).Append(@"</Name>").Append(k_WindowsNewline);
                    projectBuilder.Append(@"    </ProjectReference>").Append(k_WindowsNewline);
                }
            }

            // 2. Compiled References (DLLs) - FORCE ABSOLUTE PATHS
            foreach (var compiledRef in assembly.compiledAssemblyReferences)
            {
                string fullPath = Path.GetFullPath(compiledRef);
                // FIX: Replace backslashes with forward slashes for better XML compatibility
                fullPath = fullPath.Replace('\\', '/');
                string name = Path.GetFileNameWithoutExtension(fullPath);

                projectBuilder.Append(@"    <Reference Include=""").Append(XmlFilename(name)).Append(@""">").Append(k_WindowsNewline);
                projectBuilder.Append(@"        <HintPath>").Append(XmlFilename(fullPath)).Append(@"</HintPath>").Append(k_WindowsNewline);
                projectBuilder.Append(@"    </Reference>").Append(k_WindowsNewline);
            }
            projectBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);

            // FOOTER
            projectBuilder.Append(@"  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />").Append(k_WindowsNewline);
            projectBuilder.Append(@"  <Target Name=""GenerateTargetFrameworkMonikerAttribute"" />").Append(k_WindowsNewline);
            projectBuilder.Append(@"</Project>").Append(k_WindowsNewline);

            return projectBuilder.ToString();
        }

        internal string XmlFilename(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            path = path.Replace(@"%", "%25").Replace(@";", "%3b");
            return System.Security.SecurityElement.Escape(path);
        }

        private Dictionary<string, string> GenerateAllAssetProjectParts() => new Dictionary<string, string>();

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

        internal bool ShouldFileBePartOfSolution(string file) => IsSupportedExtension(Path.GetExtension(file));
        public bool IsSupportedFile(string path) => IsSupportedExtension(Path.GetExtension(path));

        internal string ProjectGuid(string assemblyName) => m_GUIDGenerator.ProjectGuid(m_ProjectName, assemblyName);
        internal string ProjectGuid(Assembly assembly) => ProjectGuid(m_AssemblyNameProvider.GetAssemblyName(assembly.outputPath, assembly.name));
        private string SolutionGuid(Assembly assembly) => m_GUIDGenerator.SolutionGuid(m_ProjectName, ScriptingLanguageFor(assembly));

        private static string GetRootNamespace(Assembly assembly)
        {
#if UNITY_2020_2_OR_NEWER
            return assembly.rootNamespace;
#else
            return EditorSettings.projectGenerationRootNamespace;
#endif
        }

        private void SyncSolution(IEnumerable<Assembly> assemblies)
        {
            var solutionFile = SolutionFile();
            var relevantAssemblies = assemblies.Where(i => ScriptingLanguage.CSharp == ScriptingLanguageFor(i));

            var projectEntries = new StringBuilder();
            var projectConfigurations = new StringBuilder();

            foreach (var assembly in relevantAssemblies)
            {
                var guid = ProjectGuid(assembly);
                var filename = Path.GetFileName(ProjectFile(assembly));

                projectEntries.AppendFormat(m_SolutionProjectEntryTemplate, SolutionGuid(assembly), assembly.name, filename, guid, k_WindowsNewline).Append(k_WindowsNewline);
                projectConfigurations.AppendFormat(m_SolutionProjectConfigurationTemplate, guid).Append(k_WindowsNewline);
            }

            string solutionText = string.Format(string.Join(k_WindowsNewline,
            @"",
            @"Microsoft Visual Studio Solution File, Format Version 12.00",
            @"# Visual Studio 15",
            @"VisualStudioVersion = 15.0.26124.0",
            @"MinimumVisualStudioVersion = 15.0.26124.0",
            @"{0}",
            @"Global",
            @"	GlobalSection(SolutionConfigurationPlatforms) = preSolution",
            @"		Debug|Any CPU = Debug|Any CPU",
            @"		Release|Any CPU = Release|Any CPU",
            @"	EndGlobalSection",
            @"	GlobalSection(ProjectConfigurationPlatforms) = postSolution",
            @"{1}",
            @"	EndGlobalSection",
            @"	GlobalSection(SolutionProperties) = preSolution",
            @"		HideSolutionNode = FALSE",
            @"	EndGlobalSection",
            @"EndGlobal",
            @""), projectEntries.ToString(), projectConfigurations.ToString());

            SyncSolutionFileIfNotChanged(solutionFile, solutionText);
        }
    }

    public static class SolutionGuidGenerator
    {
        public static string GuidForProject(string projectName) => ComputeGuidHashFor(projectName + "salt");
        public static string GuidForSolution(string projectName, ScriptingLanguage language) => "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC";
        private static string ComputeGuidHashFor(string input) => HashAsGuid(HashToString(MD5.Create().ComputeHash(Encoding.Default.GetBytes(input))));
        private static string HashAsGuid(string hash) => (hash.Substring(0, 8) + "-" + hash.Substring(8, 4) + "-" + hash.Substring(12, 4) + "-" + hash.Substring(16, 4) + "-" + hash.Substring(20, 12)).ToUpper();
        private static string HashToString(byte[] bs) { var sb = new StringBuilder(); foreach (byte b in bs) sb.Append(b.ToString("x2")); return sb.ToString(); }
    }
}