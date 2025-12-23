/*---------------------------------------------------------------------------------------------
 * Copyright (c) Unity Technologies.
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.IO;
using System.Text;
using UnityEditor.Compilation;
using UnityEngine;

namespace Antigravity.Ide.Editor
{
    internal class SdkStyleProjectGeneration : ProjectGeneration
    {
        internal override string StyleName => "SDK";

        public SdkStyleProjectGeneration() : base(
            Directory.GetParent(Application.dataPath)?.FullName,
            new AssemblyNameProvider(),
            new FileIOProvider(),
            new GUIDProvider())
        {
        }

        internal static readonly string[] SupportedCapabilities = new string[]
        {
            "Unity",
        };

        internal static readonly string[] UnsupportedCapabilities = new string[]
        {
            "LaunchProfiles",
            "SharedProjectReferences",
            "ReferenceManagerSharedProjects",
            "COMReferences",
            "ReferenceManagerCOM",
        };

        internal override void GetProjectHeader(ProjectProperties properties, out StringBuilder headerBuilder)
        {
            headerBuilder = new StringBuilder();

            headerBuilder.Append(@"<Project Sdk=""Microsoft.NET.Sdk"">").Append(k_WindowsNewline);
            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            headerBuilder.Append($"    <BaseIntermediateOutputPath>{@"Temp\obj\$(Configuration)\$(MSBuildProjectName)".NormalizePathSeparators()}</BaseIntermediateOutputPath>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <IntermediateOutputPath>$(BaseIntermediateOutputPath)</IntermediateOutputPath>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            GetCapabilityBlock(headerBuilder, "Sdk.props", "Include", SupportedCapabilities);

            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <EnableDefaultItems>false</EnableDefaultItems>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <LangVersion>").Append(properties.LangVersion).Append(@"</LangVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Configurations>Debug;Release</Configurations>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Configuration Condition="" '$(Configuration)' == '' "">Debug</Configuration>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Platform Condition="" '$(Platform)' == '' "">AnyCPU</Platform>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <RootNamespace>").Append(properties.RootNamespace).Append(@"</RootNamespace>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <OutputType>Library</OutputType>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AppDesignerFolder>Properties</AppDesignerFolder>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AssemblyName>").Append(properties.AssemblyName).Append(@"</AssemblyName>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ProjectGuid>{").Append(properties.ProjectGuid).Append(@"}</ProjectGuid>").Append(k_WindowsNewline);

            // FIX: Target net471 (NET Framework 4.7.1) to match Unity Editor.
            // Using netstandard2.1 often breaks Editor scripts as they depend on full framework features.
            headerBuilder.Append(@"    <TargetFramework>net471</TargetFramework>").Append(k_WindowsNewline);

            // FIX: Removed DisableImplicitFrameworkReferences or set to false.
            // We NEED standard libraries (System, mscorlib) for anything to load.
            headerBuilder.Append(@"    <DisableImplicitFrameworkReferences>false</DisableImplicitFrameworkReferences>").Append(k_WindowsNewline);

            // Helpful properties for Unity handling
            headerBuilder.Append(@"    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <BaseDirectory>.</BaseDirectory>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            GetProjectHeaderConfigurations(properties, headerBuilder);

            GetProjectHeaderVstuFlavoring(properties, headerBuilder, false);
            GetProjectHeaderAnalyzers(properties, headerBuilder);
        }

        internal override void AppendProjectReference(Assembly assembly, Assembly reference, StringBuilder projectBuilder)
        {
            var referenceName = m_AssemblyNameProvider.GetAssemblyName(assembly.outputPath, reference.name);
            var projectReferenceGuid = ProjectGuid(reference);
            projectBuilder.Append(@"    <ProjectReference Include=""").Append(referenceName).Append(GetProjectExtension()).Append(@""">").Append(k_WindowsNewline);
            projectBuilder.Append(@"        <Project>{").Append(projectReferenceGuid).Append(@"}</Project>").Append(k_WindowsNewline);
            projectBuilder.Append(@"        <Name>").Append(referenceName).Append(@"</Name>").Append(k_WindowsNewline);
            projectBuilder.Append(@"        <Private>false</Private>").Append(k_WindowsNewline); // Prevent copying references locally which can confuse Unity
            projectBuilder.Append(@"    </ProjectReference>").Append(k_WindowsNewline);
        }

        internal override void GetProjectFooter(StringBuilder footerBuilder)
        {
            GetCapabilityBlock(footerBuilder, "Sdk.targets", "Remove", UnsupportedCapabilities);
            footerBuilder.Append("</Project>").Append(k_WindowsNewline);
        }

        internal static void GetCapabilityBlock(StringBuilder footerBuilder, string import, string attribute, string[] capabilities)
        {
            footerBuilder.Append(@"  <ItemGroup>").Append(k_WindowsNewline);
            foreach (var capability in capabilities)
            {
                footerBuilder.Append($@"    <ProjectCapability {attribute}=""{capability}"" />").Append(k_WindowsNewline);
            }
            footerBuilder.Append(@"  </ItemGroup>").Append(k_WindowsNewline);
        }
    }
}