﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Azure.Core.Serialization;
using Grpc.Core;
using Microsoft.Azure.Functions.Worker.Context.Features;
using Microsoft.Azure.Functions.Worker.Grpc;
using Microsoft.Azure.Functions.Worker.Grpc.Features;
using Microsoft.Azure.Functions.Worker.Grpc.Messages;
using Microsoft.Azure.Functions.Worker.Invocation;
using Microsoft.Azure.Functions.Worker.OutputBindings;
using Microsoft.Azure.Functions.Worker.Rpc;
using Microsoft.Extensions.Options;
using static Microsoft.Azure.Functions.Worker.Grpc.Messages.FunctionRpc;
using MsgType = Microsoft.Azure.Functions.Worker.Grpc.Messages.StreamingMessage.ContentOneofCase;


namespace Microsoft.Azure.Functions.Worker
{
    internal class GrpcWorker : IWorker
    {
        private readonly ChannelReader<StreamingMessage> _outputReader;
        private readonly ChannelWriter<StreamingMessage> _outputWriter;

        private readonly IFunctionsApplication _application;
        private readonly FunctionRpcClient _rpcClient;
        private readonly IInvocationFeaturesFactory _invocationFeaturesFactory;
        private readonly IOutputBindingsInfoProvider _outputBindingsInfoProvider;
        private readonly IMethodInfoLocator _methodInfoLocator;
        private readonly IOptions<GrpcWorkerStartupOptions> _startupOptions;
        private readonly ObjectSerializer _serializer;

        public GrpcWorker(IFunctionsApplication application, FunctionRpcClient rpcClient, GrpcHostChannel outputChannel, IInvocationFeaturesFactory invocationFeaturesFactory,
            IOutputBindingsInfoProvider outputBindingsInfoProvider, IMethodInfoLocator methodInfoLocator, IOptions<GrpcWorkerStartupOptions> startupOptions, IOptions<WorkerOptions> workerOptions)
        {
            if (outputChannel == null)
            {
                throw new ArgumentNullException(nameof(outputChannel));
            }

            _outputReader = outputChannel.Channel.Reader;
            _outputWriter = outputChannel.Channel.Writer;

            _application = application ?? throw new ArgumentNullException(nameof(application));
            _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
            _invocationFeaturesFactory = invocationFeaturesFactory ?? throw new ArgumentNullException(nameof(invocationFeaturesFactory));
            _outputBindingsInfoProvider = outputBindingsInfoProvider ?? throw new ArgumentNullException(nameof(outputBindingsInfoProvider));
            _methodInfoLocator = methodInfoLocator ?? throw new ArgumentNullException(nameof(methodInfoLocator));
            _startupOptions = startupOptions ?? throw new ArgumentNullException(nameof(startupOptions));
            _serializer = workerOptions.Value.Serializer ?? throw new InvalidOperationException(nameof(workerOptions.Value.Serializer));
        }

        public async Task StartAsync(CancellationToken token)
        {
            var eventStream = _rpcClient.EventStream(cancellationToken: token);

            await SendStartStreamMessageAsync(eventStream.RequestStream);

            _ = StartWriterAsync(eventStream.RequestStream);
            _ = StartReaderAsync(eventStream.ResponseStream);
        }

        public Task StopAsync(CancellationToken token)
        {
            return Task.CompletedTask;
        }

        private async Task SendStartStreamMessageAsync(IClientStreamWriter<StreamingMessage> requestStream)
        {
            StartStream str = new StartStream()
            {
                WorkerId = _startupOptions.Value.WorkerId
            };

            StreamingMessage startStream = new StreamingMessage()
            {
                StartStream = str
            };

            await requestStream.WriteAsync(startStream);
        }

        private async Task StartWriterAsync(IClientStreamWriter<StreamingMessage> requestStream)
        {
            await foreach (StreamingMessage rpcWriteMsg in _outputReader.ReadAllAsync())
            {
                await requestStream.WriteAsync(rpcWriteMsg);
            }
        }

        private async Task StartReaderAsync(IAsyncStreamReader<StreamingMessage> responseStream)
        {
            while (await responseStream.MoveNext())
            {
                await ProcessRequestAsync(responseStream.Current);
            }
        }

        private Task ProcessRequestAsync(StreamingMessage request)
        {
            // Dispatch and return.
            Task.Run(() => ProcessRequestCoreAsync(request));
            return Task.CompletedTask;
        }

        private async Task ProcessRequestCoreAsync(StreamingMessage request)
        {
            StreamingMessage responseMessage = new StreamingMessage
            {
                RequestId = request.RequestId
            };

            if (request.ContentCase == MsgType.InvocationRequest)
            {
                responseMessage.InvocationResponse = await InvocationRequestHandlerAsync(request.InvocationRequest, _application,
                    _invocationFeaturesFactory, _serializer, _outputBindingsInfoProvider);
            }
            else if (request.ContentCase == MsgType.WorkerInitRequest)
            {
                responseMessage.WorkerInitResponse = WorkerInitRequestHandler(request.WorkerInitRequest);
            }
            else if (request.ContentCase == MsgType.FunctionsMetadataRequest)
            {
                responseMessage.FunctionMetadataResponses = FunctionsMetadataRequestHandler(request.FunctionsMetadataRequest);
            }
            else if (request.ContentCase == MsgType.FunctionLoadRequest)
            {
                responseMessage.FunctionLoadResponse = FunctionLoadRequestHandler(request.FunctionLoadRequest, _application, _methodInfoLocator);
            }
            else if (request.ContentCase == MsgType.FunctionEnvironmentReloadRequest)
            {
                // No-op for now, but return a response.
                responseMessage.FunctionEnvironmentReloadResponse = new FunctionEnvironmentReloadResponse
                {
                    Result = new StatusResult { Status = StatusResult.Types.Status.Success }
                };
            }
            else
            {
                // TODO: Trace failure here.
                return;
            }

            await _outputWriter.WriteAsync(responseMessage);
        }

    internal static async Task<InvocationResponse> InvocationRequestHandlerAsync(InvocationRequest request, IFunctionsApplication application,
            IInvocationFeaturesFactory invocationFeaturesFactory, ObjectSerializer serializer, IOutputBindingsInfoProvider outputBindingsInfoProvider)
        {
            FunctionContext? context = null;
            InvocationResponse response = new()
            {
                InvocationId = request.InvocationId
            };

            try
            {
                var invocation = new GrpcFunctionInvocation(request);

                IInvocationFeatures invocationFeatures = invocationFeaturesFactory.Create();
                invocationFeatures.Set<FunctionInvocation>(invocation);
                invocationFeatures.Set<IExecutionRetryFeature>(invocation);

                context = application.CreateContext(invocationFeatures);
                invocationFeatures.Set<IFunctionBindingsFeature>(new GrpcFunctionBindingsFeature(context, request, outputBindingsInfoProvider));

                await application.InvokeFunctionAsync(context);

                var functionBindings = context.GetBindings();

                foreach (var binding in functionBindings.OutputBindingData)
                {
                    var parameterBinding = new ParameterBinding
                    {
                        Name = binding.Key
                    };

                    if (binding.Value is not null)
                    {
                        parameterBinding.Data = await binding.Value.ToRpcAsync(serializer);
                    }

                    response.OutputData.Add(parameterBinding);
                }

                if (functionBindings.InvocationResult != null)
                {
                    TypedData? returnVal = await functionBindings.InvocationResult.ToRpcAsync(serializer);

                    response.ReturnValue = returnVal;
                }

                response.Result = new StatusResult
                {
                    Status = StatusResult.Types.Status.Success
                };
            }
            catch (Exception ex)
            {
                response.Result = new StatusResult
                {
                    Exception = ex.ToRpcException(),
                    Status = StatusResult.Types.Status.Failure
                };
            }
            finally
            {
                (context as IDisposable)?.Dispose();
            }

            return response;
        }

        internal static WorkerInitResponse WorkerInitRequestHandler(WorkerInitRequest request)
        {
            var response = new WorkerInitResponse
            {
                Result = new StatusResult { Status = StatusResult.Types.Status.Success },
                WorkerVersion = WorkerInformation.Instance.WorkerVersion
            };

            response.Capabilities.Add("RpcHttpBodyOnly", bool.TrueString);
            response.Capabilities.Add("RawHttpBodyBytes", bool.TrueString);
            response.Capabilities.Add("RpcHttpTriggerMetadataRemoved", bool.TrueString);
            response.Capabilities.Add("UseNullableValueDictionaryForHttp", bool.TrueString);
            response.Capabilities.Add("TypedDataCollection", bool.TrueString);

            return response;
        }

        // Need to ask about the design of this, how to name things, and how many new classes to create?
        internal static FunctionMetadataResponses FunctionsMetadataRequestHandler(FunctionsMetadataRequest request)
        {
            var directory = request.FunctionAppDirectory;

            var response = new FunctionMetadataResponses
            {
                OverallStatus = StatusResult.Success
            };

            try
            {
                // we need to get a list of items of type "FunctionLoadRequests" which has a field RpcFunctionMetadata
                var functionMetadata = GetFunctionLoadRequests(directory);

                for (int i = 0; i < functionMetadata.Count; i++)
                {
                    response.Results.Add(functionMetadata[i]);
                }
            }
            catch (Exception ex)
            {
                response.OverallStatus = new StatusResult
                {
                    Status = StatusResult.Types.Status.Failure,
                    Exception = ex.ToRpcException()
                };
            }

            return response;
        }

        internal static IReadOnlyList<FunctionLoadRequest> GetFunctionLoadRequests(string directory)
        {
            // The logic for getting functionMetadata already exists in this repo, but it takes in things we don't have and creates more than we need
            // TODO: Discuss w/ team to see how we can refactor or workaround existing classes/methods
            var functionGenerator = new FunctionMetadataGenerator(); // in the fileGenerateFunctionMetadata this takes MSBuilder.
                                                                     
            var functions = functionGenerator.GenerateFunctionMetadata(directory);

            var functionRequests = new List<FunctionLoadRequest>(functions.Count);

            foreach (var metadata in functions)
            {
                FunctionLoadRequest request = new FunctionLoadRequest()
                {
                    FunctionId = metadata.GetFunctionId(), // TODO: this doesn't exist in SdkFunctionMetadata which is used internally in this repo
                    Metadata = new RpcFunctionMetadata()
                    {
                        Name = metadata.Name,
                        Directory = metadata.FunctionDirectory ?? string.Empty,
                        EntryPoint = metadata.EntryPoint ?? string.Empty,
                        ScriptFile = metadata.ScriptFile ?? string.Empty,
                        IsProxy = metadata.IsProxy() // TODO: this also doesn't exist in SdkFunctionMetadata
                    }
                };

                foreach (var binding in metadata.Bindings)
                {
                    BindingInfo bindingInfo = binding.ToBindingInfo();

                    request.Metadata.Bindings.Add(binding.Name, bindingInfo);
                }

                functionRequests.Add(request);
            }
                

            return functionRequests;
        }

        internal static FunctionLoadResponse FunctionLoadRequestHandler(FunctionLoadRequest request, IFunctionsApplication application, IMethodInfoLocator methodInfoLocator)
        {
            var response = new FunctionLoadResponse
            {
                FunctionId = request.FunctionId,
                Result = StatusResult.Success
            };

            if (!request.Metadata.IsProxy)
            {
                try
                {
                    FunctionDefinition definition = request.ToFunctionDefinition(methodInfoLocator);
                    application.LoadFunction(definition);
                }
                catch (Exception ex)
                {
                    response.Result = new StatusResult
                    {
                        Status = StatusResult.Types.Status.Failure,
                        Exception = ex.ToRpcException()
                    };
                }
            }

            return response;
        }
    }
}
