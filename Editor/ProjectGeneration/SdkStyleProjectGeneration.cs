/*---------------------------------------------------------------------------------------------
 * Copyright (c) Unity Technologies.
 * Copyright (c) Microsoft Corporation. All rights reserved.
 * Licensed under the MIT License. See License.txt in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System;
using System.IO;
using System.Text;
using System.Linq;
using UnityEditor.Compilation;
using UnityEngine;

namespace Antigravity.Ide.Editor
{
    internal class SdkStyleProjectGeneration : ProjectGeneration
    {
        internal override string StyleName => "SDK";

        // Capabilities required for Unity support in VS Code
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

            // 1. Calculate the path to Unity's internal Mono libraries (mscorlib, System, etc.)
            // This fixes the "Developer Pack not found" error by pointing directly to Unity's folders.
            var mscorlibPath = typeof(object).Assembly.Location;
            var frameworkPath = Path.GetDirectoryName(mscorlibPath).NormalizePathSeparators();

            headerBuilder.Append(@"<Project Sdk=""Microsoft.NET.Sdk"">").Append(k_WindowsNewline);
            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            headerBuilder.Append($"    <BaseIntermediateOutputPath>{@"Temp\obj\$(Configuration)\$(MSBuildProjectName)".NormalizePathSeparators()}</BaseIntermediateOutputPath>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <IntermediateOutputPath>$(BaseIntermediateOutputPath)</IntermediateOutputPath>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            headerBuilder.Append(@"  <PropertyGroup>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <TargetFramework>net471</TargetFramework>").Append(k_WindowsNewline);
            
            // MAGIC FIX: Tell the SDK exactly where to find System.dll and mscorlib.dll
            headerBuilder.Append($"    <FrameworkPathOverride>{frameworkPath}</FrameworkPathOverride>").Append(k_WindowsNewline);
            
            headerBuilder.Append(@"    <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <EnableDefaultItems>false</EnableDefaultItems>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>").Append(k_WindowsNewline);
            
            headerBuilder.Append(@"    <LangVersion>").Append(properties.LangVersion).Append(@"</LangVersion>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Configurations>Debug;Release</Configurations>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Configuration Condition="" '$(Configuration)' == '' "">Debug</Configuration>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <Platform Condition="" '$(Platform)' == '' "">AnyCPU</Platform>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <RootNamespace>").Append(properties.RootNamespace).Append(@"</RootNamespace>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <OutputType>Library</OutputType>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AppDesignerFolder>Properties</AppDesignerFolder>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <AssemblyName>").Append(properties.AssemblyName).Append(@"</AssemblyName>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <ProjectGuid>{").Append(properties.ProjectGuid).Append(@"}</ProjectGuid>").Append(k_WindowsNewline);
            headerBuilder.Append(@"    <BaseDirectory>.</BaseDirectory>").Append(k_WindowsNewline);
            headerBuilder.Append(@"  </PropertyGroup>").Append(k_WindowsNewline);

            // Add Capabilities for C# Dev Kit
            GetCapabilityBlock(headerBuilder, "Sdk.props", "Include", SupportedCapabilities);

            GetProjectHeaderConfigurations(properties, headerBuilder);
            GetProjectHeaderVstuFlavoring(properties, headerBuilder, false);
            GetProjectHeaderAnalyzers(properties, headerBuilder);
        }

        internal override void AppendProjectReference(Assembly assembly, Assembly reference, StringBuilder projectBuilder)
        {
            // SDK Style references
            var referenceName = m_AssemblyNameProvider.GetAssemblyName(assembly.outputPath, reference.name);
            var projectReferenceGuid = ProjectGuid(reference);
            projectBuilder.Append(@"    <ProjectReference Include=""").Append(referenceName).Append(GetProjectExtension()).Append(@""">").Append(k_WindowsNewline);
            projectBuilder.Append(@"        <Project>{").Append(projectReferenceGuid).Append(@"}</Project>").Append(k_WindowsNewline);
            projectBuilder.Append(@"        <Name>").Append(referenceName).Append(@"</Name>").Append(k_WindowsNewline);
            projectBuilder.Append(@"        <Private>false</Private>").Append(k_WindowsNewline);
            projectBuilder.Append(@"    </ProjectReference>").Append(k_WindowsNewline);
        }

        internal override void GetProjectFooter(StringBuilder footerBuilder)
        {
            // Clean footer for SDK style - NO standard targets import needed
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
    }
}