﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using JsonRpc.Standard;
using JsonRpc.Standard.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JsonRpc.Dataflow
{

    /// <summary>
    /// Provides options for <see cref="JsonRpcServiceHost"/>.
    /// </summary>
    [Flags]
    public enum DataflowRpcServiceHostOptions
    {
        /// <summary>
        /// No options.
        /// </summary>
        None = 0,

        /// <summary>
        /// Makes the response sequence consistent with the request order.
        /// </summary>
        ConsistentResponseSequence,

        /// <summary>
        /// Enables request-cancellation with <see cref="DataflowRpcServiceHost.TryCancelRequest"/>.
        /// </summary>
        SupportsRequestCancellation
    }

    /// <summary>
    /// Pumps JSON RPC requests from TPL Dataflow blocks and dispatches them.
    /// </summary>
    public class DataflowRpcServiceHost
    {

        private readonly ILogger logger;

        private readonly FeatureCollection defaultFeatures;

        public DataflowRpcServiceHost(IJsonRpcServiceHost rpcServiceHost) : this(rpcServiceHost, null, null, DataflowRpcServiceHostOptions.None)
        {
        }

        public DataflowRpcServiceHost(IJsonRpcServiceHost rpcServiceHost, DataflowRpcServiceHostOptions options) : this(rpcServiceHost, null, null, options)
        {
        }

        public DataflowRpcServiceHost(IJsonRpcServiceHost rpcServiceHost, IFeatureCollection defaultFeatures) : this(rpcServiceHost, defaultFeatures, null, DataflowRpcServiceHostOptions.None)
        {
        }

        public DataflowRpcServiceHost(IJsonRpcServiceHost rpcServiceHost, IFeatureCollection defaultFeatures, DataflowRpcServiceHostOptions options) : this(rpcServiceHost, defaultFeatures, null, options)
        {
        }

        public DataflowRpcServiceHost(IJsonRpcServiceHost rpcServiceHost, IFeatureCollection defaultFeatures,
            ILoggerFactory loggerFactory, DataflowRpcServiceHostOptions options)
        {
            if (rpcServiceHost == null) throw new ArgumentNullException(nameof(rpcServiceHost));
            RpcServiceHost = rpcServiceHost;
            this.defaultFeatures = new FeatureCollection(defaultFeatures);
            logger = (ILogger) loggerFactory?.CreateLogger<DataflowRpcServiceHost>() ?? NullLogger.Instance;
            Propagator = new TransformBlock<Message, ResponseMessage>(
                (Func<Message, Task<ResponseMessage>>) ReaderAction,
                new ExecutionDataflowBlockOptions
                {
                    EnsureOrdered = (options & DataflowRpcServiceHostOptions.ConsistentResponseSequence) ==
                                    DataflowRpcServiceHostOptions.ConsistentResponseSequence,
                    MaxDegreeOfParallelism = -1 // This will permit more than one message to get into the block.
                });
            // Drain null responses generated by RpcMethodEntryPoint.
            Propagator.LinkTo(DataflowBlock.NullTarget<ResponseMessage>(), m => m == null);

            if ((options & DataflowRpcServiceHostOptions.SupportsRequestCancellation) ==
                DataflowRpcServiceHostOptions.SupportsRequestCancellation)
            {
                requestCtsDict = new Dictionary<MessageId, CancellationTokenSource>();
                this.defaultFeatures.Set<IRequestCancellationFeature>(new RequestCancellationFeature(this));
            }
            else
            {
                requestCtsDict = null;
            }
        }

        public IJsonRpcServiceHost RpcServiceHost { get; }

        protected IPropagatorBlock<Message, ResponseMessage> Propagator { get; }

        /// <summary>
        /// Attaches the host to the specific source block and target block.
        /// </summary>
        /// <param name="source">The source block used to retrieve the requests.</param>
        /// <param name="target">The target block used to emit the responses.</param>
        /// <returns>A <see cref="IDisposable"/> used to disconnect the source and target blocks.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="target"/> is <c>null</c>.</exception>
        public IDisposable Attach(ISourceBlock<Message> source, ITargetBlock<ResponseMessage> target)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            var d1 = source.LinkTo(Propagator, new DataflowLinkOptions {PropagateCompletion = true},
                m => m is RequestMessage);
            var d2 = Propagator.LinkTo(target, new DataflowLinkOptions {PropagateCompletion = true},
                m => m != null);
            return Utility.CombineDisposable(d1, d2);
        }

        // Persists the CTS for all the currently processing, cancellable requests.
        private readonly Dictionary<MessageId, CancellationTokenSource> requestCtsDict;
        
        /// <summary>
        /// Tries to cancel the specified request by request id.
        /// </summary>
        /// <param name="id">Id of the request to cancel.</param>
        /// <exception cref="NotSupportedException"><see cref="DataflowRpcServiceHostOptions.SupportsRequestCancellation"/> is not specified in the constructor, so cancellation is not supported.</exception>
        /// <returns><c>true</c> if the specified request has been cancelled. <c>false</c> if the specified request id has not found.</returns>
        /// <remarks>If cancellation is supported, you may cancel an arbitary request in the <see cref="JsonRpcService"/> via <see cref="IRequestCancellationFeature"/>.</remarks>
        public bool TryCancelRequest(MessageId id)
        {
            if (requestCtsDict == null) throw new NotSupportedException("Request cancellation is not supported.");
            CancellationTokenSource cts;
            lock (requestCtsDict) if (!requestCtsDict.TryGetValue(id, out cts)) return false;
            cts.Cancel();
            return true;
        }

        private async Task<ResponseMessage> ReaderAction(Message message)
        {
            var request = message as RequestMessage;
            if (request == null) return null;
            CancellationTokenSource cts = null;
            var requestId = request.Id; // Defensive copy, in case request has been changed in the pipeline.
            if (requestCtsDict != null && !request.IsNotification)
            {
                cts = new CancellationTokenSource();
                try
                {
                    lock (requestCtsDict) requestCtsDict.Add(requestId, cts);
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogWarning(1001, ex, "Duplicate request id for client detected: Id = {id}",
                        requestId);
                    cts.Dispose();
                    cts = null;
                }
            }
            try
            {
                var pipelineTask =
                    RpcServiceHost.InvokeAsync(request, defaultFeatures, cts?.Token ?? CancellationToken.None);
                // For notification, we just forget it… Don't want to choke the pipeline.
                if (request.IsNotification) return null;
                // We need to wait for an response
                return await pipelineTask.ConfigureAwait(false);
            }
            finally
            {
                if (cts != null)
                {
                    lock (requestCtsDict) requestCtsDict.Remove(requestId);
                    cts.Dispose();
                }
            }
        }

        private class RequestCancellationFeature : IRequestCancellationFeature
        {
            private readonly DataflowRpcServiceHost _Owner;

            public RequestCancellationFeature(DataflowRpcServiceHost owner)
            {
                Debug.Assert(owner != null);
                _Owner = owner;
            }

            /// <inheritdoc />
            public bool TryCancel(MessageId id)
            {
                return _Owner.TryCancelRequest(id);
            }
        }
    }
}
