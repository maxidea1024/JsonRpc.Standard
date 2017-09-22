﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using JsonRpc.Standard;
using JsonRpc.Standard.Client;
using JsonRpc.Standard.Contracts;

namespace JsonRpc.DynamicProxy.Client
{
    /// <summary>
    /// Infrastructure. Base class for client proxy implementation.
    /// </summary>
    [Browsable(false)]
    public class JsonRpcProxyBase
    {

        protected JsonRpcProxyBase(JsonRpcClient client, IList<JsonRpcMethod> methodTable, IJsonRpcRequestMarshaler marshaler)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (methodTable == null) throw new ArgumentNullException(nameof(methodTable));
            Client = client;
            MethodTable = methodTable;
            Marshaler = marshaler;
        }

        public JsonRpcClient Client { get; }

        protected IList<JsonRpcMethod> MethodTable { get; }

        protected IJsonRpcRequestMarshaler Marshaler { get; }

        /// <summary>
        /// Infrastructure. Sends the request and wait for the response.
        /// </summary>
        protected TResult Send<TResult>(int methodIndex, IList paramValues)
        {
            return SendAsync<TResult>(methodIndex, paramValues).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Infrastructure. Sends the notification; do not wait for the response.
        /// </summary>
        protected void Send(int methodIndex, IList paramValues)
        {
            var forgetit = SendAsync<object>(methodIndex, paramValues);
        }

        /// <summary>
        /// Infrastructure. Asynchronously sends the request and wait for the response.
        /// </summary>
        /// <typeparam name="TResult">Response type.</typeparam>
        /// <param name="methodIndex">The JSON RPC method index in <see cref="MethodTable"/>.</param>
        /// <param name="paramValues">Parameters, in the order of expected parameter order.</param>
        /// <exception cref="JsonRpcRemoteException">An error has occurred on the remote-side.</exception>
        /// <exception cref="JsonRpcContractException">An error has occurred when generating the request or parsing the response.</exception>
        /// <exception cref="OperationCanceledException">The operation has been cancelled.</exception>
        /// <returns>The response.</returns>
        protected async Task<TResult> SendAsync<TResult>(int methodIndex, IList paramValues)
        {
            var method = MethodTable[methodIndex];
            MarshaledRequestParameters marshaled;
            try
            {
                marshaled = Marshaler.MarshalParameters(method.Parameters, paramValues);
            }
            catch (Exception ex)
            {
                throw new JsonRpcContractException("An exception occured while marshalling the request. " + ex.Message,
                    ex);
            }
            marshaled.CancellationToken.ThrowIfCancellationRequested();
            var request = new RequestMessage(method.MethodName, marshaled.Parameters);
            // Send the request
            if (!method.IsNotification) request.Id = Client.NextRequestId();
            var response = await Client.SendAsync(request, marshaled.CancellationToken).ConfigureAwait(false);
            // For notification, we do not have a response.
            if (response != null)
            {
                if (response.Error != null)
                {
                    throw new JsonRpcRemoteException(response.Error);
                }
                if (method.ReturnParameter.ParameterType != typeof(void))
                {
                    // VSCode will return void for null in `window/showMessageRequest`.
                    // I mean, https://github.com/Microsoft/language-server-protocol/blob/master/protocol.md#window_showMessageRequest
                    // So just don't be picky…
                    //if (response.Result == null)
                    //    throw new JsonRpcContractException(
                    //        $"Expect \"{method.ReturnParameter.ParameterType}\" result, got void.",
                    //        message);
                    try
                    {
                        return (TResult) method.ReturnParameter.Converter.JsonToValue(response.Result, typeof(TResult));
                    }
                    catch (Exception ex)
                    {
                        throw new JsonRpcContractException(
                            "An exception occured while unmarshalling the response. " + ex.Message,
                            request, ex);
                    }
                }
            }
            return default(TResult);
        }
    }

#if DEBUG
    //… So that I can see some IL

    internal interface IContractTest
    {
        object M1(int a, string b, object c, HashSet<int>.Enumerator d);

        int P1 { get; set; }
    }

    internal class JsonRpcProxyTest : JsonRpcProxyBase, IContractTest
    {
        /// <inheritdoc />
        public JsonRpcProxyTest(JsonRpcClient client, JsonRpcMethod[] methodTable) : base(client, methodTable, new NamedRequestMarshaler())
        {
        }

        /// <inheritdoc />
        object IContractTest.M1(int a, string b, object c, HashSet<int>.Enumerator d)
        {
            return Send<object>(1, new object[] {a, b, c, d});
        }

        /// <inheritdoc />
        public int P1 { get; set; }

        void XXX()
        {
            Send<object>(1, null);
        }
    }
#endif
}
