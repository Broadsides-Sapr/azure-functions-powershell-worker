//
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Management.Automation.Remoting;
using System.Threading.Tasks;

using Microsoft.Azure.Functions.PowerShellWorker.Messaging;
using Microsoft.Azure.Functions.PowerShellWorker.PowerShell;
using Microsoft.Azure.Functions.PowerShellWorker.Utility;
using Microsoft.Azure.Functions.PowerShellWorker.DependencyManagement;
using Microsoft.Azure.Functions.PowerShellWorker.DurableWorker;
using Microsoft.Azure.WebJobs.Script.Grpc.Messages;

namespace Microsoft.Azure.Functions.PowerShellWorker
{
    using System.Diagnostics;
    using LogLevel = Microsoft.Azure.WebJobs.Script.Grpc.Messages.RpcLog.Types.Level;
    using System.Runtime.InteropServices;
    using Microsoft.Azure.Functions.PowerShellWorker.OpenTelemetry;

    internal class RequestProcessor
    {
        private readonly MessagingStream _msgStream;
        private readonly System.Management.Automation.PowerShell _firstPwshInstance;
        private readonly PowerShellManagerPool _powershellPool;
        private DependencyManager _dependencyManager;
        private string _pwshVersion;

        // Holds the exception if an issue is encountered while processing the function app dependencies.
        private Exception _initTerminatingError;

        // Indicate whether the FunctionApp has been initialized.
        private bool _isFunctionAppInitialized;

        private Dictionary<StreamingMessage.ContentOneofCase, Func<StreamingMessage, StreamingMessage>> _requestHandlers =
            new Dictionary<StreamingMessage.ContentOneofCase, Func<StreamingMessage, StreamingMessage>>();

        internal RequestProcessor(MessagingStream msgStream, System.Management.Automation.PowerShell firstPwshInstance, string pwshVersion)
        {
            _msgStream = msgStream;
            _firstPwshInstance = firstPwshInstance;
            _powershellPool = new PowerShellManagerPool(() => new RpcLogger(msgStream));
            _pwshVersion = pwshVersion;

            // Host sends capabilities/init data to worker
            _requestHandlers.Add(StreamingMessage.ContentOneofCase.WorkerInitRequest, ProcessWorkerInitRequest);

            // Host sends terminate message to worker.
            // Worker terminates if it can, otherwise host terminates after a grace period
            _requestHandlers.Add(StreamingMessage.ContentOneofCase.WorkerTerminate, ProcessWorkerTerminateRequest);

            // Add any worker relevant status to response
            _requestHandlers.Add(StreamingMessage.ContentOneofCase.WorkerStatusRequest, ProcessWorkerStatusRequest);

            // On file change event, host sends notification to worker
            _requestHandlers.Add(StreamingMessage.ContentOneofCase.FileChangeEventRequest, ProcessFileChangeEventRequest);

            // Host sends required metadata to worker to load function
            _requestHandlers.Add(StreamingMessage.ContentOneofCase.FunctionLoadRequest, ProcessFunctionLoadRequest);

            // Host requests a given invocation
            _requestHandlers.Add(StreamingMessage.ContentOneofCase.InvocationRequest, ProcessInvocationRequest);

            // Host sends cancel message to attempt to cancel an invocation. 
            // If an invocation is cancelled, host will receive an invocation response with status cancelled.
            _requestHandlers.Add(StreamingMessage.ContentOneofCase.InvocationCancel, ProcessInvocationCancelRequest);

            _requestHandlers.Add(StreamingMessage.ContentOneofCase.FunctionEnvironmentReloadRequest, ProcessFunctionEnvironmentReloadRequest);
        }

        internal async Task ProcessRequestLoop()
        {
            StreamingMessage request, response;
            while (await _msgStream.MoveNext())
            {
                request = _msgStream.GetCurrentMessage();

                if (_requestHandlers.TryGetValue(request.ContentCase, out Func<StreamingMessage, StreamingMessage> requestFunc))
                {
                    response = requestFunc(request);
                }
                else
                {
                    RpcLogger.WriteSystemLog(LogLevel.Warning, string.Format(PowerShellWorkerStrings.UnsupportedMessage, request.ContentCase));
                    continue;
                }

                if (response != null)
                {
                    _msgStream.Write(response);
                }
            }
        }

        internal StreamingMessage ProcessWorkerInitRequest(StreamingMessage request)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var workerInitRequest = request.WorkerInitRequest;
            Environment.SetEnvironmentVariable("AZUREPS_HOST_ENVIRONMENT", $"AzureFunctions/{workerInitRequest.HostVersion}");
            Environment.SetEnvironmentVariable("POWERSHELL_DISTRIBUTION_CHANNEL", $"Azure-Functions:{workerInitRequest.HostVersion}");

            StreamingMessage response = NewStreamingMessageTemplate(
                request.RequestId,
                StreamingMessage.ContentOneofCase.WorkerInitResponse,
                out StatusResult status);

            response.WorkerInitResponse.Capabilities.Add("RpcHttpBodyOnly", "true");
            response.WorkerInitResponse.Capabilities.Add("WorkerStatus", "true");

            if (OpenTelemetryController.IsOpenTelemetryEnvironmentEnabled())
            {
                response.WorkerInitResponse.Capabilities.Add("WorkerOpenTelemetryEnabled", "true");
            }

            // If the environment variable is set, spin up the custom named pipe server.
            // This is typically used for debugging. It will throw a friendly exception if the
            // pipe name is not a valid pipename.
            string pipeName = Environment.GetEnvironmentVariable("PSWorkerCustomPipeName");
            if (!string.IsNullOrEmpty(pipeName))
            {
                RpcLogger.WriteSystemLog(LogLevel.Trace, string.Format(PowerShellWorkerStrings.SpecifiedCustomPipeName, pipeName));
                RemoteSessionNamedPipeServer.CreateCustomNamedPipeServer(pipeName);
            }

            try
            {
                var rpcLogger = new RpcLogger(_msgStream);
                rpcLogger.SetContext(request.RequestId, null);

                response.WorkerInitResponse.WorkerMetadata = GetWorkerMetadata(_pwshVersion);

                rpcLogger.Log(isUserOnlyLog: false, LogLevel.Trace, string.Format(PowerShellWorkerStrings.WorkerInitCompleted, stopwatch.ElapsedMilliseconds));
            }
            catch (Exception e)
            {
                status.Status = StatusResult.Types.Status.Failure;
                status.Exception = e.ToRpcException();
                return response;
            }

            return response;
        }

        internal StreamingMessage ProcessWorkerTerminateRequest(StreamingMessage request)
        {
            return null;
        }

        internal StreamingMessage ProcessWorkerStatusRequest(StreamingMessage request)
        {
            // WorkerStatusResponse type says that it is not used but this will create an empty one anyway to return to the host
            StreamingMessage response = NewStreamingMessageTemplate(
                request.RequestId,
                StreamingMessage.ContentOneofCase.WorkerStatusResponse,
                out StatusResult status);

            return response;
        }

        internal StreamingMessage ProcessFileChangeEventRequest(StreamingMessage request)
        {
            return null;
        }

        /// <summary>
        /// Method to process a FunctionLoadRequest.
        /// FunctionLoadRequest should be processed sequentially. There is no point to process FunctionLoadRequest
        /// concurrently as a FunctionApp doesn't include a lot functions in general. Having this step sequential
        /// will make the Runspace-level initialization easier and more predictable.
        /// </summary>
        internal StreamingMessage ProcessFunctionLoadRequest(StreamingMessage request)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            FunctionLoadRequest functionLoadRequest = request.FunctionLoadRequest;

            StreamingMessage response = NewStreamingMessageTemplate(
                request.RequestId,
                StreamingMessage.ContentOneofCase.FunctionLoadResponse,
                out StatusResult status);
            response.FunctionLoadResponse.FunctionId = functionLoadRequest.FunctionId;

            // The worker may occasionally receive multiple function load requests with
            // the same FunctionId. In order to make function load request idempotent,
            // the worker should ignore the duplicates.
            if (FunctionLoader.IsLoaded(functionLoadRequest.FunctionId))
            {
                // If FunctionLoader considers this function loaded, this means
                // the previous request was successful, so respond accordingly.
                return response;
            }

            // When a functionLoadRequest comes in, we check to see if a dependency download has failed in a previous call
            // or if PowerShell could not be initialized. If this is the case, mark this as a failed request
            // and submit the exception to the Host (runtime).
            if (_initTerminatingError != null)
            {
                status.Status = StatusResult.Types.Status.Failure;
                status.Exception = _initTerminatingError.ToRpcException();
                return response;
            }

            // Ideally, the initialization should happen when processing 'WorkerInitRequest'. However, we defer the initialization
            // until the first 'FunctionLoadRequest' which contains the information about whether Managed Dependencies is enabled for the function app,
            // and if it is, we add the Managed Dependencies path to the PSModulePath.
            // Also, we receive a FunctionLoadRequest when a proxy is configured. This is just a no-op on the worker size, so we skip over them.
            if (!_isFunctionAppInitialized && !functionLoadRequest.Metadata.IsProxy)
            {
                try
                {
                    _isFunctionAppInitialized = true;

                    var rpcLogger = new RpcLogger(_msgStream);
                    rpcLogger.SetContext(request.RequestId, null);

                    _dependencyManager = new DependencyManager(request.FunctionLoadRequest.Metadata.Directory, logger: rpcLogger);
                    var managedDependenciesPath = _dependencyManager.Initialize(request, rpcLogger);

                    SetupAppRootPathAndModulePath(functionLoadRequest, managedDependenciesPath);

                    _powershellPool.Initialize(_firstPwshInstance);

                    // Start the download asynchronously if needed.
                    _dependencyManager.StartDependencyInstallationIfNeeded(request, _firstPwshInstance, rpcLogger);

                    rpcLogger.Log(isUserOnlyLog: false, LogLevel.Trace, string.Format(PowerShellWorkerStrings.FirstFunctionLoadCompleted, stopwatch.ElapsedMilliseconds));
                }
                catch (Exception e)
                {
                    // Failure that happens during this step is terminating and we will need to return a failure response to
                    // all subsequent 'FunctionLoadRequest'. Cache the exception so we can reuse it in future calls.
                    _initTerminatingError = e;

                    status.Status = StatusResult.Types.Status.Failure;
                    status.Exception = e.ToRpcException();
                    return response;
                }
            }

            try
            {
                // Load the metadata of the function.
                FunctionLoader.LoadFunction(functionLoadRequest);
            }
            catch (Exception e)
            {
                status.Status = StatusResult.Types.Status.Failure;
                status.Exception = e.ToRpcException();
            }

            return response;
        }

        /// <summary>
        /// Method to process a InvocationRequest.
        /// This method checks out a worker from the pool, and then starts the actual invocation in a threadpool thread.
        /// </summary>
        internal StreamingMessage ProcessInvocationRequest(StreamingMessage request)
        {
            try
            {
                var stopwatch = new FunctionInvocationPerformanceStopwatch();
                stopwatch.OnStart();

                // Will block if installing dependencies is required
                _dependencyManager.WaitForDependenciesAvailability(
                    () =>
                        {
                            var rpcLogger = new RpcLogger(_msgStream);
                            rpcLogger.SetContext(request.RequestId, request.InvocationRequest?.InvocationId);
                            return rpcLogger;
                        });

                stopwatch.OnCheckpoint(FunctionInvocationPerformanceStopwatch.Checkpoint.DependenciesAvailable);

                AzFunctionInfo functionInfo = FunctionLoader.GetFunctionInfo(request.InvocationRequest.FunctionId);

                PowerShellManager psManager = _powershellPool.CheckoutIdleWorker(
                                                request.RequestId,
                                                request.InvocationRequest?.InvocationId,
                                                functionInfo.FuncName,
                                                functionInfo.OutputBindings);

                stopwatch.OnCheckpoint(FunctionInvocationPerformanceStopwatch.Checkpoint.RunspaceAvailable);

                // When the concurrency upper bound is more than 1, we have to handle the invocation in a worker
                // thread, so multiple invocations can make progress at the same time, even though by time-sharing.
                Task.Run(() => ProcessInvocationRequestImpl(request, functionInfo, psManager, stopwatch));
            }
            catch (Exception e)
            {
                StreamingMessage response = NewStreamingMessageTemplate(
                    request.RequestId,
                    StreamingMessage.ContentOneofCase.InvocationResponse,
                    out StatusResult status);

                response.InvocationResponse.InvocationId = request.InvocationRequest.InvocationId;
                status.Status = StatusResult.Types.Status.Failure;
                status.Exception = e.ToRpcException();

                return response;
            }

            return null;
        }

        /// <summary>
        /// Implementation method to actual invoke the corresponding function.
        /// InvocationRequest messages are processed in parallel when there are multiple PowerShellManager instances in the pool.
        /// </summary>
        private void ProcessInvocationRequestImpl(
            StreamingMessage request,
            AzFunctionInfo functionInfo,
            PowerShellManager psManager,
            FunctionInvocationPerformanceStopwatch stopwatch)
        {
            InvocationRequest invocationRequest = request.InvocationRequest;
            StreamingMessage response = NewStreamingMessageTemplate(
                request.RequestId,
                StreamingMessage.ContentOneofCase.InvocationResponse,
                out StatusResult status);
            response.InvocationResponse.InvocationId = invocationRequest.InvocationId;

            try
            {
                // Invoke the function and return a hashtable of out binding data
                Hashtable results = InvokeFunction(functionInfo, psManager, stopwatch, invocationRequest);

                BindOutputFromResult(response.InvocationResponse, functionInfo, results);
            }
            catch (Exception e)
            {
                status.Status = StatusResult.Types.Status.Failure;
                status.Exception = e.ToRpcException();
            }
            finally
            {
                _powershellPool.ReclaimUsedWorker(psManager);
            }

            _msgStream.Write(response);
        }

        private Hashtable InvokeFunction(
            AzFunctionInfo functionInfo,
            PowerShellManager psManager,
            FunctionInvocationPerformanceStopwatch stopwatch,
            InvocationRequest invocationRequest)
        {
            var triggerMetadata = GetTriggerMetadata(functionInfo, invocationRequest);
            var traceContext = GetTraceContext(functionInfo, invocationRequest);
            var retryContext = GetRetryContext(functionInfo, invocationRequest);
            stopwatch.OnCheckpoint(FunctionInvocationPerformanceStopwatch.Checkpoint.MetadataAndTraceContextReady);

            OpenTelemetryInvocationContext otelContext = new OpenTelemetryInvocationContext(invocationRequest.InvocationId, 
                invocationRequest.TraceContext?.TraceParent, invocationRequest.TraceContext?.TraceState);

            return psManager.InvokeFunction(functionInfo, triggerMetadata, traceContext, retryContext, invocationRequest.InputData, stopwatch, otelContext);
        }

        internal StreamingMessage ProcessInvocationCancelRequest(StreamingMessage request)
        {
            return null;
        }

        internal StreamingMessage ProcessFunctionEnvironmentReloadRequest(StreamingMessage request)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var environmentReloadRequest = request.FunctionEnvironmentReloadRequest;

            var rpcLogger = new RpcLogger(_msgStream);
            rpcLogger.SetContext(request.RequestId, null);

            var functionsEnvironmentReloader = new FunctionsEnvironmentReloader(rpcLogger);
            functionsEnvironmentReloader.ReloadEnvironment(
                environmentReloadRequest.EnvironmentVariables,
                environmentReloadRequest.FunctionAppDirectory);

            rpcLogger.Log(isUserOnlyLog: false, LogLevel.Trace, string.Format(PowerShellWorkerStrings.EnvironmentReloadCompleted, stopwatch.ElapsedMilliseconds));

            StreamingMessage response = NewStreamingMessageTemplate(
                request.RequestId,
                StreamingMessage.ContentOneofCase.FunctionEnvironmentReloadResponse,
                out StatusResult status);

            // This will force the OpenTelemetryController to check the environment variables and module presence again
            OpenTelemetryController.ResetOpenTelemetryModuleStatus();
            if (OpenTelemetryController.IsOpenTelemetryEnvironmentEnabled())
            {
                // Note: The ability to edit worker capabilities is new within the last few months. I am unsure if we need to 
                // report all prior capabilities that were sent at worker init. Going with minimal change for now. 
                response.FunctionEnvironmentReloadResponse.Capabilities.Add("WorkerOpenTelemetryEnabled", "true");
            }

            return response;
        }

        #region Helper_Methods

        /// <summary>
        /// Create an object of 'StreamingMessage' as a template, for further update.
        /// </summary>
        private StreamingMessage NewStreamingMessageTemplate(string requestId, StreamingMessage.ContentOneofCase msgType, out StatusResult status)
        {
            // Assume success. The state of the status object can be changed in the caller.
            status = new StatusResult() { Status = StatusResult.Types.Status.Success };
            var response = new StreamingMessage() { RequestId = requestId };

            switch (msgType)
            {
                case StreamingMessage.ContentOneofCase.WorkerInitResponse:
                    response.WorkerInitResponse = new WorkerInitResponse() { Result = status };
                    break;
                case StreamingMessage.ContentOneofCase.WorkerStatusResponse:
                    response.WorkerStatusResponse = new WorkerStatusResponse();
                    break;
                case StreamingMessage.ContentOneofCase.FunctionLoadResponse:
                    response.FunctionLoadResponse = new FunctionLoadResponse() { Result = status };
                    break;
                case StreamingMessage.ContentOneofCase.InvocationResponse:
                    response.InvocationResponse = new InvocationResponse() { Result = status };
                    break;
                case StreamingMessage.ContentOneofCase.FunctionEnvironmentReloadResponse:
                    response.FunctionEnvironmentReloadResponse = new FunctionEnvironmentReloadResponse() { Result = status };
                    break;
                default:
                    throw new InvalidOperationException("Unreachable code.");
            }

            return response;
        }

        private static Hashtable GetTriggerMetadata(AzFunctionInfo functionInfo, InvocationRequest invocationRequest)
        {
            if (!functionInfo.HasTriggerMetadataParam)
            {
                return null;
            }

            const string InvocationId = "InvocationId";
            const string FunctionDirectory = "FunctionDirectory";
            const string FunctionName = "FunctionName";

            var triggerMetadata = new Hashtable(StringComparer.OrdinalIgnoreCase);
            foreach (var dataItem in invocationRequest.TriggerMetadata)
            {
                // MapField<K, V> is case-sensitive, but powershell is case-insensitive,
                // so for keys differ only in casing, the first wins.
                if (!triggerMetadata.ContainsKey(dataItem.Key))
                {
                    triggerMetadata.Add(dataItem.Key, dataItem.Value.ToObject());
                }
            }

            if (!triggerMetadata.ContainsKey(InvocationId))
            {
                triggerMetadata.Add(InvocationId, invocationRequest.InvocationId);
            }

            if (!triggerMetadata.ContainsKey(FunctionDirectory))
            {
                triggerMetadata.Add(FunctionDirectory, functionInfo.FuncDirectory);
            }

            if (!triggerMetadata.ContainsKey(FunctionName))
            {
                triggerMetadata.Add(FunctionName, functionInfo.FuncName);
            }

            return triggerMetadata;
        }

        private static TraceContext GetTraceContext(AzFunctionInfo functionInfo, InvocationRequest invocationRequest)
        {
            if (!functionInfo.HasTraceContextParam)
            {
                return null;
            }

            return new TraceContext(
                invocationRequest.TraceContext.TraceParent,
                invocationRequest.TraceContext.TraceState,
                invocationRequest.TraceContext.Attributes);
        }

        private static RetryContext GetRetryContext(AzFunctionInfo functionInfo, InvocationRequest invocationRequest)
        {
            if (!functionInfo.HasRetryContextParam)
            {
                return null;
            }

            return new RetryContext(
                invocationRequest.RetryContext.RetryCount,
                invocationRequest.RetryContext.MaxRetryCount,
                invocationRequest.RetryContext.Exception);
        }

        /// <summary>
        /// Set the 'ReturnValue' and 'OutputData' based on the invocation results appropriately.
        /// </summary>
        private static void BindOutputFromResult(InvocationResponse response, AzFunctionInfo functionInfo, IDictionary results)
        {
            if (functionInfo.DurableFunctionInfo.Type != DurableFunctionType.OrchestrationFunction)
            {
                // Set out binding data and return response to be sent back to host
                foreach (var (bindingName, bindingInfo) in functionInfo.OutputBindings)
                {
                    var outValue = results[bindingName];
                    var transformedValue = Utils.TransformOutBindingValueAsNeeded(bindingName, bindingInfo, outValue);
                    var dataToUse = transformedValue.ToTypedData();

                    // if one of the bindings is '$return' we need to set the ReturnValue
                    if (string.Equals(bindingName, AzFunctionInfo.DollarReturn, StringComparison.OrdinalIgnoreCase))
                    {
                        response.ReturnValue = dataToUse;
                        continue;
                    }

                    var paramBinding = new ParameterBinding()
                    {
                        Name = bindingName,
                        Data = dataToUse
                    };

                    response.OutputData.Add(paramBinding);
                }
            }

            if (functionInfo.DurableFunctionInfo.ProvidesForcedDollarReturnValue)
            {
                response.ReturnValue = results[AzFunctionInfo.DollarReturn].ToTypedData(isDurableData: true);
            }
        }

        private void SetupAppRootPathAndModulePath(FunctionLoadRequest functionLoadRequest, string managedDependenciesPath)
        {
            FunctionLoader.SetupWellKnownPaths(functionLoadRequest, managedDependenciesPath);

            if (FunctionLoader.FunctionAppRootPath == null)
            {
                throw new InvalidOperationException(PowerShellWorkerStrings.FunctionAppRootNotResolved);
            }

            _firstPwshInstance.AddCommand("Microsoft.PowerShell.Management\\Set-Content")
                .AddParameter("Path", "env:PSModulePath")
                .AddParameter("Value", FunctionLoader.FunctionModulePath)
                .InvokeAndClearCommands();
        }

        private WorkerMetadata GetWorkerMetadata(string pwshVersion)
        {
            var data = new WorkerMetadata();
            data.WorkerBitness = RuntimeInformation.OSArchitecture.ToString();
            data.WorkerVersion = typeof(Worker).Assembly.GetName().Version.ToString();
            data.RuntimeVersion = pwshVersion;
            data.RuntimeName = "powershell";

            return data;
        }

        #endregion
    }
}
