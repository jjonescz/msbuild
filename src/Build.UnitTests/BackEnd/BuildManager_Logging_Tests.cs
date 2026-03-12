// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using static Microsoft.Build.UnitTests.ObjectModelHelpers;

#nullable disable

namespace Microsoft.Build.Engine.UnitTests.BackEnd
{
    public class BuildManager_Logging_Tests : IDisposable
    {
        private string _mainProject = @"
<Project>

  <Target Name=`MainTarget`>
    <MSBuild Projects=`{0}` Targets=`ChildTarget` />
  </Target>

</Project>";

        private string _childProjectWithCustomBuildEvent = $@"
<Project>

    <UsingTask TaskName=""CustomBuildEventTask"" AssemblyFile=""{Assembly.GetExecutingAssembly().Location}"" />
    <Target Name=`ChildTarget`>
        <CustomBuildEventTask />
    </Target>

</Project>";


        /// <summary>
        /// The mock logger for testing.
        /// </summary>
        private readonly MockLogger _logger;

        /// <summary>
        /// The standard build manager for each test.
        /// </summary>
        private readonly BuildManager _buildManager;

        /// <summary>
        /// The project collection used.
        /// </summary>
        private readonly ProjectCollection _projectCollection;

        private readonly TestEnvironment _env;

        /// <summary>
        /// SetUp
        /// </summary>
        public BuildManager_Logging_Tests(ITestOutputHelper output)
        {
            // Ensure that any previous tests which may have been using the default BuildManager do not conflict with us.
            BuildManager.DefaultBuildManager.Dispose();

            _logger = new MockLogger(output);
            _buildManager = new BuildManager();
            _projectCollection = new ProjectCollection();

            _env = TestEnvironment.Create(output);
        }

        [DotNetOnlyFact]
        public void Build_WithCustomBuildArgs_ShouldEmitErrorOnNetCore() => Build_WithCustomBuildArgs_ShouldEmitEvent<BuildErrorEventArgs>();

        [WindowsFullFrameworkOnlyFact]
        public void Build_WithCustomBuildArgs_ShouldEmitWarningOnFramework() => Build_WithCustomBuildArgs_ShouldEmitEvent<BuildWarningEventArgs>();

        private void Build_WithCustomBuildArgs_ShouldEmitEvent<T>() where T : LazyFormattedBuildEventArgs
        {
            var testFiles = _env.CreateTestProjectWithFiles(string.Empty, ["main", "child1"], string.Empty);

            ILoggingService service = LoggingService.CreateLoggingService(LoggerMode.Synchronous, 1);
            service.RegisterLogger(_logger);

            _env.SetEnvironmentVariable("MSBUILDNOINPROCNODE", "1");

            _buildManager.BeginBuild(BuildParameters);

            try
            {
                var child1ProjectPath = testFiles.CreatedFiles[1];
                var cleanedUpChildContents = CleanupFileContents(_childProjectWithCustomBuildEvent);
                File.WriteAllText(child1ProjectPath, cleanedUpChildContents);

                var mainProjectPath = testFiles.CreatedFiles[0];
                var cleanedUpMainContents = CleanupFileContents(string.Format(_mainProject, child1ProjectPath));
                File.WriteAllText(mainProjectPath, cleanedUpMainContents);

                var buildRequestData = new BuildRequestData(
                   mainProjectPath,
                   new Dictionary<string, string>(),
                   MSBuildConstants.CurrentToolsVersion,
                   ["MainTarget"],
                   null);

                var submission = _buildManager.PendBuildRequest(buildRequestData);
                var result = submission.Execute();
                var allEvents = _logger.AllBuildEvents;

                allEvents.OfType<T>().ShouldHaveSingleItem();
                allEvents.First(x => x is T).Message.ShouldContain(
                    string.Format(ResourceUtilities.GetResourceString("DeprecatedEventSerialization"),
                    "MyCustomBuildEventArgs"));
            }
            finally
            {
                _buildManager.EndBuild();
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void EvaluationData(bool inMemory, bool multipleRequests)
        {
            _projectCollection.RegisterLogger(_logger);

            _buildManager.BeginBuild(new BuildParameters(_projectCollection)
            {
                Loggers = [_logger],
            });

            try
            {
                var projectContents = """
                    <Project>
                        <PropertyGroup>
                            <A>Hello</A>
                            <B>World</B>
                        </PropertyGroup>
                        <Target Name="MainTarget">
                            <Message Text="$(A), $(B)!" />
                        </Target>
                        <Target Name="SecondTarget">
                            <Message Text="test: $(C)" />
                        </Target>
                    </Project>
                    """;

                ProjectRootElement projectRootElement;
                if (inMemory)
                {
                    using var reader = XmlReader.Create(new StringReader(projectContents));
                    projectRootElement = ProjectRootElement.Create(reader, _projectCollection);
                }
                else
                {
                    var projectPath = _env.CreateFile().Path;
                    File.WriteAllText(path: projectPath, contents: projectContents);
                    projectRootElement = ProjectRootElement.Open(projectPath, _projectCollection);
                }

                var buildRequestData = new BuildRequestData(
                    ProjectInstance.FromProjectRootElement(projectRootElement, new ProjectOptions
                    {
                        ProjectCollection = _projectCollection,
                    }),
                    ["MainTarget"]);

                var result = _buildManager.BuildRequest(buildRequestData);

                Assert.Equal(BuildResultCode.Success, result.OverallResult);

                _logger.AllBuildEvents.OfType<ProjectEvaluationStartedEventArgs>().ShouldHaveSingleItem();

                if (multipleRequests)
                {
                    var buildRequestData2 = new BuildRequestData(
                        ProjectInstance.FromProjectRootElement(projectRootElement, new ProjectOptions
                        {
                            ProjectCollection = _projectCollection,
                            GlobalProperties = new Dictionary<string, string> { { "C", "input" } },
                        }),
                        ["SecondTarget"]);

                    var result2 = _buildManager.BuildRequest(buildRequestData2);

                    Assert.Equal(BuildResultCode.Success, result2.OverallResult);

                    _logger.AllBuildEvents.OfType<ProjectEvaluationStartedEventArgs>().Count().ShouldBe(2);
                }
            }
            finally
            {
                _buildManager.EndBuild();
            }
        }

        private BuildParameters BuildParameters => new BuildParameters(_projectCollection)
        {
            DisableInProcNode = true,
            EnableNodeReuse = false,
            Loggers = new ILogger[] { _logger }
        };

        /// <summary>
        /// TearDown
        /// </summary>
        public void Dispose()
        {
            try
            {
                _buildManager.Dispose();
                _projectCollection.Dispose();
            }
            finally
            {
                _env.Dispose();
            }
        }
    }
}
