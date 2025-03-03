//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Azure.Functions.PowerShellWorker.PowerShell;
using Microsoft.Azure.Functions.PowerShellWorker.Utility;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;
using Xunit;

namespace Microsoft.Azure.Functions.PowerShellWorker.Test
{
    using System.Collections.ObjectModel;
    using System.Management.Automation;
    using Microsoft.Azure.Functions.PowerShellWorker.Durable;
    using Microsoft.Azure.Functions.PowerShellWorker.DurableWorker;
    using Microsoft.Extensions.Logging.Abstractions;
    using Newtonsoft.Json;

    internal class TestUtils
    {
        // Helper method to wait for debugger to attach and set a breakpoint.
        internal static void Break()
        {
            while (!System.Diagnostics.Debugger.IsAttached)
            {
                System.Threading.Thread.Sleep(200);
            }
            System.Diagnostics.Debugger.Break();
        }
    }

    public class PowerShellManagerTests : IDisposable
    {
        private const string TestInputBindingName = "req";
        private const string TestOutputBindingName = "res";
        private const string TestStringData = "Foo";
        private const int TestRetryCount = 0;
        private const int TestMaxRetryCount = 1;
        private const string TestMessage = "TestMessage";
        private readonly static RpcException TestException = new RpcException
        {
            Source = "",
            StackTrace = "",
            Message = TestMessage
        };

        private readonly static string s_funcDirectory;
        private readonly static FunctionLoadRequest s_functionLoadRequest;

        private readonly static ConsoleLogger s_testLogger;
        private readonly static List<ParameterBinding> s_testInputData;

        static PowerShellManagerTests()
        {
            s_funcDirectory = Path.Join(AppDomain.CurrentDomain.BaseDirectory, "TestScripts", "PowerShell");
            s_testLogger = new ConsoleLogger();
            s_testInputData = new List<ParameterBinding>
            {
                new ParameterBinding
                {
                    Name = TestInputBindingName,
                    Data = new TypedData { String = TestStringData }
                }
            };

            var rpcFunctionMetadata = new RpcFunctionMetadata()
            {
                Name = "TestFuncApp",
                Directory = s_funcDirectory,
                Bindings =
                {
                    { TestInputBindingName , new BindingInfo { Direction = BindingInfo.Types.Direction.In, Type = "httpTrigger" } },
                    { TestOutputBindingName, new BindingInfo { Direction = BindingInfo.Types.Direction.Out, Type = "http" } }
                }
            };

            s_functionLoadRequest = new FunctionLoadRequest { FunctionId = "FunctionId", Metadata = rpcFunctionMetadata };
            FunctionLoader.SetupWellKnownPaths(s_functionLoadRequest, managedDependenciesPath: null);
        }

        // Have a single place to get a PowerShellManager for testing.
        // This is to guarantee that the well known paths are setup before calling the constructor of PowerShellManager.
        internal static PowerShellManager NewTestPowerShellManager(ConsoleLogger logger, PowerShell pwsh = null)
        {
            return pwsh != null ? new PowerShellManager(logger, pwsh) : new PowerShellManager(logger, id: 2);
        }

        private static (AzFunctionInfo, PowerShellManager) PrepareFunction(string scriptFile, string entryPoint)
        {
            s_functionLoadRequest.Metadata.ScriptFile = scriptFile;
            s_functionLoadRequest.Metadata.EntryPoint = entryPoint;
            s_functionLoadRequest.Metadata.Directory = Path.GetDirectoryName(scriptFile);

            FunctionLoader.LoadFunction(s_functionLoadRequest);
            var funcInfo = FunctionLoader.GetFunctionInfo(s_functionLoadRequest.FunctionId);
            var psManager = NewTestPowerShellManager(s_testLogger);

            return (funcInfo, psManager);
        }

        public void Dispose()
        {
            FunctionLoader.ClearLoadedFunctions();
            s_testLogger.FullLog.Clear();
        }

        [Fact]
        public void InvokeBasicFunctionWorks()
        {
            string path = Path.Join(s_funcDirectory, "testBasicFunction.ps1");
            var (functionInfo, testManager) = PrepareFunction(path, string.Empty);

            try
            {
                FunctionMetadata.RegisterFunctionMetadata(testManager.InstanceId, functionInfo.OutputBindings);
                Hashtable result = InvokeFunction(testManager, functionInfo);

                // The outputBinding hashtable for the runspace should be cleared after 'InvokeFunction'
                Hashtable outputBindings = FunctionMetadata.GetOutputBindingHashtable(testManager.InstanceId);
                Assert.Empty(outputBindings);

                // A PowerShell function should be created fro the Az function.
                string expectedResult = $"{TestStringData},{functionInfo.DeployedPSFuncName}";
                Assert.Equal(expectedResult, result[TestOutputBindingName]);
            }
            finally
            {
                FunctionMetadata.UnregisterFunctionMetadata(testManager.InstanceId);
            }
        }

        [Fact]
        public void InvokeFunctionWithSpecialVariableWorks()
        {
            string path = Path.Join(s_funcDirectory, "testBasicFunctionSpecialVariables.ps1");
            var (functionInfo, testManager) = PrepareFunction(path, string.Empty);

            try
            {
                FunctionMetadata.RegisterFunctionMetadata(testManager.InstanceId, functionInfo.OutputBindings);
                Hashtable result = InvokeFunction(testManager, functionInfo);

                // The outputBinding hashtable for the runspace should be cleared after 'InvokeFunction'
                Hashtable outputBindings = FunctionMetadata.GetOutputBindingHashtable(testManager.InstanceId);
                Assert.Empty(outputBindings);

                // A PowerShell function should be created fro the Az function.
                string expectedResult = $"{s_funcDirectory},{path},{functionInfo.DeployedPSFuncName}";
                Assert.Equal(expectedResult, result[TestOutputBindingName]);
            }
            finally
            {
                FunctionMetadata.UnregisterFunctionMetadata(testManager.InstanceId);
            }
        }

        [Fact]
        public void InvokeBasicFunctionWithRequiresWorks()
        {
            string path = Path.Join(s_funcDirectory, "testBasicFunctionWithRequires.ps1");
            var (functionInfo, testManager) = PrepareFunction(path, string.Empty);

            try
            {
                FunctionMetadata.RegisterFunctionMetadata(testManager.InstanceId, functionInfo.OutputBindings);
                Hashtable result = InvokeFunction(testManager, functionInfo);

                // The outputBinding hashtable for the runspace should be cleared after 'InvokeFunction'
                Hashtable outputBindings = FunctionMetadata.GetOutputBindingHashtable(testManager.InstanceId);
                Assert.Empty(outputBindings);

                // When function script has #requires, not PowerShell function will be created for the Az function,
                // and the invocation uses the file path directly.
                string expectedResult = $"{TestStringData},ThreadJob,testBasicFunctionWithRequires.ps1";
                Assert.Equal(expectedResult, result[TestOutputBindingName]);
            }
            finally
            {
                FunctionMetadata.UnregisterFunctionMetadata(testManager.InstanceId);
            }
        }

        [Fact]
        public void InvokeBasicFunctionWithTriggerMetadataAndTraceContextAndRetryContextWorks()
        {
            string path = Path.Join(s_funcDirectory, "testBasicFunctionWithTriggerMetadataAndRetryContext.ps1");
            var (functionInfo, testManager) = PrepareFunction(path, string.Empty);

            Hashtable triggerMetadata = new Hashtable(StringComparer.OrdinalIgnoreCase)
            {
                { TestInputBindingName, TestStringData }
            };

            RetryContext retryContext = new RetryContext(TestRetryCount, TestMaxRetryCount, TestException);

            try
            {
                FunctionMetadata.RegisterFunctionMetadata(testManager.InstanceId, functionInfo.OutputBindings);

                Hashtable result = InvokeFunction(testManager, functionInfo, triggerMetadata, retryContext);

                // The outputBinding hashtable for the runspace should be cleared after 'InvokeFunction'
                Hashtable outputBindings = FunctionMetadata.GetOutputBindingHashtable(testManager.InstanceId);
                Assert.Empty(outputBindings);

                // A PowerShell function should be created fro the Az function.
                string expectedResult = $"{TestStringData},{functionInfo.DeployedPSFuncName}:{TestRetryCount},{TestMaxRetryCount},{TestMessage}";
                Assert.Equal(expectedResult, result[TestOutputBindingName]);
            }
            finally
            {
                FunctionMetadata.UnregisterFunctionMetadata(testManager.InstanceId);
            }
        }

        [Fact]
        public void InvokeFunctionWithEntryPointWorks()
        {
            string path = Path.Join(s_funcDirectory, "testFunctionWithEntryPoint.psm1");
            var (functionInfo, testManager) = PrepareFunction(path, "Run");

            try
            {
                FunctionMetadata.RegisterFunctionMetadata(testManager.InstanceId, functionInfo.OutputBindings);
                Hashtable result = InvokeFunction(testManager, functionInfo);

                // The outputBinding hashtable for the runspace should be cleared after 'InvokeFunction'
                Hashtable outputBindings = FunctionMetadata.GetOutputBindingHashtable(testManager.InstanceId);
                Assert.Empty(outputBindings);

                string expectedResult = $"{TestStringData},Run";
                Assert.Equal(expectedResult, result[TestOutputBindingName]);
            }
            finally
            {
                FunctionMetadata.UnregisterFunctionMetadata(testManager.InstanceId);
            }
        }

        [Fact]
        public void FunctionShouldCleanupVariableTable()
        {
            string path = Path.Join(s_funcDirectory, "testFunctionCleanup.ps1");
            var (functionInfo, testManager) = PrepareFunction(path, string.Empty);

            try
            {
                FunctionMetadata.RegisterFunctionMetadata(testManager.InstanceId, functionInfo.OutputBindings);

                Hashtable result1 = InvokeFunction(testManager, functionInfo);
                Assert.Equal("is not set", result1[TestOutputBindingName]);

                // the value should not change if the variable table is properly cleaned up.
                Hashtable result2 = InvokeFunction(testManager, functionInfo);
                Assert.Equal("is not set", result2[TestOutputBindingName]);
            }
            finally
            {
                FunctionMetadata.UnregisterFunctionMetadata(testManager.InstanceId);
            }
        }

        [Fact]
        public void ModulePathShouldBeSetCorrectly()
        {
            string workerModulePath = Path.Join(AppDomain.CurrentDomain.BaseDirectory, "Modules");
            string funcAppModulePath = Path.Join(FunctionLoader.FunctionAppRootPath, "Modules");
            string expectedPath = $"{funcAppModulePath}{Path.PathSeparator}{workerModulePath}";
            Assert.Equal(expectedPath, Environment.GetEnvironmentVariable("PSModulePath"));
        }

        [Fact]
        public void RegisterAndUnregisterFunctionMetadataShouldWork()
        {
            string path = Path.Join(s_funcDirectory, "testBasicFunction.ps1");
            var (functionInfo, testManager) = PrepareFunction(path, string.Empty);

            FunctionMetadata.RegisterFunctionMetadata(testManager.InstanceId, functionInfo.OutputBindings);
            var outBindingMap = FunctionMetadata.GetOutputBindingInfo(testManager.InstanceId);
            Assert.Single(outBindingMap);
            Assert.Equal(TestOutputBindingName, outBindingMap.First().Key);

            FunctionMetadata.UnregisterFunctionMetadata(testManager.InstanceId);
            outBindingMap = FunctionMetadata.GetOutputBindingInfo(testManager.InstanceId);
            Assert.Null(outBindingMap);
        }

        [Fact]
        public void ProfileShouldWork()
        {
            var profilePath = Path.Join(s_funcDirectory, "ProfileBasic", "profile.ps1");
            var testManager = NewTestPowerShellManager(s_testLogger);

            // Clear log stream
            s_testLogger.FullLog.Clear();
            testManager.InvokeProfile(profilePath);

            var relevantLogs = s_testLogger.FullLog.Where(message => !message.StartsWith("Trace:")).ToList();
            Assert.Single(relevantLogs);
            Assert.Equal("Information: INFORMATION: Hello PROFILE", relevantLogs[0]);
        }

        [Fact]
        public void ProfileExecutionTimeShouldBeLogged()
        {
            var profilePath = Path.Join(s_funcDirectory, "ProfileBasic", "profile.ps1");
            var testManager = NewTestPowerShellManager(s_testLogger);
            
            s_testLogger.FullLog.Clear();
            testManager.InvokeProfile(profilePath);

            Assert.Equal(1, s_testLogger.FullLog.Count(
                                message => message.StartsWith("Trace")
                                           && message.Contains("Profile invocation completed in")));
        }

        [Fact]
        public void ProfileWithTerminatingError()
        {
            var profilePath = Path.Join(s_funcDirectory, "ProfileWithTerminatingError", "profile.ps1");
            var testManager = NewTestPowerShellManager(s_testLogger);

            // Clear log stream
            s_testLogger.FullLog.Clear();

            Assert.Throws<CmdletInvocationException>(() => testManager.InvokeProfile(profilePath));
            var relevantLogs = s_testLogger.FullLog.Where(message => !message.StartsWith("Trace:")).ToList();
            Assert.Single(relevantLogs);
            Assert.Matches("Error: Errors reported while executing profile.ps1. See logs for detailed errors. Profile location: ", relevantLogs[0]);
        }

        [Fact]
        public void ProfileWithNonTerminatingError()
        {
            var profilePath = Path.Join(s_funcDirectory, "ProfileWithNonTerminatingError", "Profile.ps1");
            var testManager = NewTestPowerShellManager(s_testLogger);

            // Clear log stream
            s_testLogger.FullLog.Clear();
            testManager.InvokeProfile(profilePath);

            var relevantLogs = s_testLogger.FullLog.Where(message => !message.StartsWith("Trace:")).ToList();
            Assert.Equal(2, relevantLogs.Count);
            Assert.StartsWith("Error: ERROR: ", relevantLogs[0]);
            Assert.Contains("help me!", relevantLogs[0]);
            Assert.Matches("Error: Errors reported while executing profile.ps1. See logs for detailed errors. Profile location: ", relevantLogs[1]);
        }

        [Fact]
        public void PSManagerCtorRunsProfileByDefault()
        {
            // Clear log stream
            s_testLogger.FullLog.Clear();
            NewTestPowerShellManager(s_testLogger);

            Assert.Single(s_testLogger.FullLog);
            Assert.Equal($"Trace: No 'profile.ps1' is found at the function app root folder: {FunctionLoader.FunctionAppRootPath}.", s_testLogger.FullLog[0]);
        }

        [Fact]
        public void PSManagerCtorDoesNotRunProfileIfDelayInit()
        {
            // Clear log stream
            s_testLogger.FullLog.Clear();
            NewTestPowerShellManager(s_testLogger, Utils.NewPwshInstance());

            Assert.Empty(s_testLogger.FullLog);
        }

        [Fact]
        public void LoggerContextIsSet()
        {
            var dummyBindingInfo = new Dictionary<string, ReadOnlyBindingInfo>();
            var outputBindings = new ReadOnlyDictionary<string, ReadOnlyBindingInfo>(dummyBindingInfo);

            var powerShellManagerPool = new PowerShellManagerPool(() => new ContextValidatingLogger());
            var pwsh = Utils.NewPwshInstance();
            powerShellManagerPool.Initialize(pwsh);

            var worker = powerShellManagerPool.CheckoutIdleWorker("requestId", "invocationId", "FuncName", outputBindings);

            powerShellManagerPool.ReclaimUsedWorker(worker);
        }

        [Theory]
        [InlineData(DurableFunctionType.None, false)]
        [InlineData(DurableFunctionType.OrchestrationFunction, false)]
        [InlineData(DurableFunctionType.ActivityFunction, true)]
        internal void SuppressPipelineTracesForDurableActivityFunctionOnly(DurableFunctionType durableFunctionType, bool shouldSuppressPipelineTraces)
        {
            s_testLogger.FullLog.Clear();

            var path = Path.Join(s_funcDirectory, "testFunctionWithOutput.ps1");
            var (functionInfo, testManager) = PrepareFunction(path, string.Empty);
            functionInfo.DurableFunctionInfo.Type = durableFunctionType;

            try
            {
                FunctionMetadata.RegisterFunctionMetadata(testManager.InstanceId, functionInfo.OutputBindings);

                var result = testManager.InvokeFunction(functionInfo, null, null, null, CreateOrchestratorInputData(), new FunctionInvocationPerformanceStopwatch(), null);

                var relevantLogs = s_testLogger.FullLog.Where(message => message.StartsWith("Information: OUTPUT:")).ToList();
                var expected = shouldSuppressPipelineTraces ? new string[0] : new[] { "Information: OUTPUT: Hello" };
                Assert.Equal(expected, relevantLogs);
            }
            finally
            {
                FunctionMetadata.UnregisterFunctionMetadata(testManager.InstanceId);
            }
        }

        private static List<ParameterBinding> CreateOrchestratorInputData()
        {
            var orchestrationContext = new OrchestrationContext
            {
                History = new[] { new HistoryEvent { EventType = HistoryEventType.OrchestratorStarted } }
            };

            var testInputData = new List<ParameterBinding>
                {
                    new ParameterBinding
                    {
                        Name = TestInputBindingName,
                        Data = new TypedData { String = JsonConvert.SerializeObject(orchestrationContext) }
                    }
                };
            return testInputData;
        }

        private static Hashtable InvokeFunction(
            PowerShellManager powerShellManager,
            AzFunctionInfo functionInfo,
            Hashtable triggerMetadata = null,
            RetryContext retryContext = null)
        {
            return powerShellManager.InvokeFunction(functionInfo, triggerMetadata, null, retryContext, s_testInputData, new FunctionInvocationPerformanceStopwatch(), null);
        }

        private class ContextValidatingLogger : ILogger
        {
            private bool _isContextSet = false;

            public void Log(bool isUserOnlyLog, RpcLog.Types.Level logLevel, string message, Exception exception = null)
            {
                Assert.True(_isContextSet);
            }

            public void SetContext(string requestId, string invocationId)
            {
                _isContextSet = true;
            }

            public void ResetContext()
            {
                _isContextSet = false;
            }
        }
    }
}
