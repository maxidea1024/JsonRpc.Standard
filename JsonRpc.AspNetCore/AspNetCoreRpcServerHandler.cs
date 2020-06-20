﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JsonRpc.Messages;
using JsonRpc.Server;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;

namespace JsonRpc.AspNetCore
{

    /// <summary>
    /// A <see cref="JsonRpcServerHandler"/> that transfers the requests from either a middleware
    /// or in the MVC controller.
    /// </summary>
    public class AspNetCoreRpcServerHandler : JsonRpcServerHandler
    {

        private static readonly Encoding UTF8NoBom = new UTF8Encoding(false);

        private Encoding _Encoding = UTF8NoBom;
        private string _ResponseContentType = "application/json";
        private bool _EmitContentCharset = true;
        private string fullContentType;
        private MemoryStream cachedMemoryStream;

        /// <inheritdoc />
        public AspNetCoreRpcServerHandler(IJsonRpcServiceHost serviceHost) : base(serviceHost)
        {
            UpdateFullContentType();
        }

        private void UpdateFullContentType()
        {
            if (ResponseContentType == null)
            {
                fullContentType = null;
                return;
            }
            fullContentType = ResponseContentType;
            if (EmitContentCharset)
                fullContentType += ";charset=" + Encoding.WebName;
        }

        /// <summary>
        /// Encoding of the emitted messages.
        /// </summary>
        public Encoding Encoding
        {
            get => _Encoding;
            set
            {
                _Encoding = value ?? UTF8NoBom;
                UpdateFullContentType();
            }
        }

        /// <summary>
        /// Content-Type header value of the emitted messages.
        /// </summary>
        /// <value>Content-Type header value, or <c>null</c> to suppress Content-Type header.</value>
        public string ResponseContentType
        {
            get => _ResponseContentType;
            set
            {
                _ResponseContentType = value;
                UpdateFullContentType();
            }
        }

        /// <summary>
        /// Whether to follow the <see cref="ResponseContentType"/> with a "charset=xxx" part
        /// when writing messages to the stream. This property has no effect if <see cref="ResponseContentType"/>
        /// is null.
        /// </summary>
        public bool EmitContentCharset
        {
            get => _EmitContentCharset;
            set
            {
                _EmitContentCharset = value;
                UpdateFullContentType();
            }
        }

        /// <summary>
        /// Processes the JSON-RPC request contained in the HTTP request body,
        /// and writes the response to the HTTP response body.
        /// </summary>
        /// <param name="context">The HTTP request context.</param>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <c>null</c>.</exception>
        /// <remarks>
        /// <para>Implementation of this method parses the request message,
        /// then calls <see cref="ProcessRequestAsync(RequestMessage,HttpContext,bool)"/>
        /// to handle the parsed message, and sends back the JSON RPC response.</para>
        /// </remarks>
        public virtual async Task ProcessRequestAsync(HttpContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsGet(context.Request.Method))
            {
                await WriteResponseWithStatusCodeHintAsync(context.Response,
                    new ResponseMessage(MessageId.Empty, new ResponseError(JsonRpcErrorCode.InvalidRequest, "The request method is not allowed.", StatusCodes.Status405MethodNotAllowed)));
                return;
            }
            if (!context.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseWithStatusCodeHintAsync(context.Response,
                    new ResponseMessage(MessageId.Empty, new ResponseError(JsonRpcErrorCode.InvalidRequest, "The request payload type is not supported.", StatusCodes.Status415UnsupportedMediaType)));
                return;
            }
            // {"method":""}        // 13 characters
            if (context.Request.ContentLength < 12)
            {
                await WriteResponseWithStatusCodeHintAsync(context.Response,
                    new ResponseMessage(MessageId.Empty, new ResponseError(JsonRpcErrorCode.InvalidRequest, "The request body is too short.")));
                return;
            }
            RequestMessage message;
            try
            {
                using (var reader = new StreamReader(context.Request.Body))
                    message = (RequestMessage) Message.LoadJson(reader);
            }
            catch (JsonReaderException ex)
            {
                await WriteResponseWithStatusCodeHintAsync(context.Response,
                    new ResponseMessage(MessageId.Empty, new ResponseError(JsonRpcErrorCode.InvalidRequest, ex.Message)));
                return;
            }
            catch (Exception ex)
            {
                await WriteResponseWithStatusCodeHintAsync(context.Response,
                    new ResponseMessage(MessageId.Empty, ResponseError.FromException(ex, false)));
                return;
            }
            context.RequestAborted.ThrowIfCancellationRequested();
            var response = await ProcessRequestAsync(message, context, false);
            await WriteResponseWithStatusCodeHintAsync(context.Response, response);
        }

        private Task WriteResponseWithStatusCodeHintAsync(HttpResponse httpResponse, ResponseMessage response, int statusCodeHint = 0)
        {
            if (statusCodeHint == 0)
                statusCodeHint = response == null ? StatusCodes.Status204NoContent : StatusCodes.Status200OK;
            return WriteResponseAsync(httpResponse, response, GetStatusCodeFromResponse(response, statusCodeHint));
        }

        /// <summary>
        /// Asynchronously writes JSON-RPC response to the HTTP response.
        /// </summary>
        /// <param name="httpResponse">HTTP response object.</param>
        /// <param name="response">JSON-RPC response, or <c>null</c> if the response is empty.</param>
        /// <param name="statusCode">the HTTP status code.</param>
        protected async Task WriteResponseAsync(HttpResponse httpResponse, ResponseMessage response, int statusCode)
        {
            if (httpResponse == null) throw new ArgumentNullException(nameof(httpResponse));

            httpResponse.StatusCode = statusCode;
            if (response == null) return;

            if (fullContentType != null)
                httpResponse.ContentType = fullContentType;
            
            // For notification, we don't wait for the task.
            // Buffer content synchronously.
            var ms = Interlocked.Exchange(ref cachedMemoryStream, null) ?? new MemoryStream(1024);
            Debug.Assert(ms.Position == 0);
            using (var writer = new StreamWriter(ms, Encoding, 4096, true))
                response.WriteJson(writer);
            httpResponse.ContentLength = ms.Length;
            
            // Write content asynchronously.
            ms.Position = 0;
#if BCL_FEATURE_ASYNC_DISPOSABLE
            await
#endif
            using (httpResponse.Body)
            {
                await ms.CopyToAsync(httpResponse.Body, 4096);
            }
            ms.Position = 0;
            ms.SetLength(0);
            Interlocked.CompareExchange(ref cachedMemoryStream, ms, null);
        }

        /// <summary>
        /// Gets corresponding HTTP status code from a specific JSON-RPC response.
        /// </summary>
        /// <param name="response">JSON-RPC response, or <c>null</c> if there is no response available (e.g. notifications).</param>
        /// <param name="statusCodeHint">
        /// suggested status code offered by the caller;
        /// usually this is 200 for JSON RPC response and 204 for notification (no response).
        /// </param>
        /// <returns>HTTP status code.</returns>
        /// <remarks>
        /// Caller may hint for 307, 405, or 415 in <paramref name="statusCodeHint"/> for HTTP transport errors defined in
        /// <a href="http://www.simple-is-better.org/json-rpc/transport_http.html#response">JSON-RPC 2.0 specification on HTTP transport</a>,
        /// but implementation can change this behavior by returning a different status code.
        /// </remarks>
        protected virtual int GetStatusCodeFromResponse(ResponseMessage response, int statusCodeHint)
        {
            return statusCodeHint;
        }

        /// <summary>
        /// Processes the specified JSON-RPC request with certain <see cref="HttpContext"/>, and returns the response.
        /// </summary>
        /// <param name="message">The message to be processed.</param>
        /// <param name="context">The HTTP request context.</param>
        /// <returns>The JSON-RPC response, or <c>null</c> if there's no such response.</returns>
        /// <exception cref="ArgumentNullException">Either <paramref name="message"/> or <paramref name="context"/> is <c>null</c>.</exception>
        /// <remarks>
        /// <para>This method will enable <see cref="IAspNetCoreFeature"/> in invoked JSON RPC service handler.</para>
        /// <para>This method will use <see cref="HttpContext.RequestAborted"/> as cancellation token passed into <see cref="RequestContext"/>.</para>
        /// <para>This overload do not wait for the JSON-RPC response if the request is a notification message.</para>
        /// </remarks>
        public Task<ResponseMessage> ProcessRequestAsync(RequestMessage message, HttpContext context)
        {
            return ProcessRequestAsync(message, context, false);
        }

        /// <summary>
        /// Processes the specified JSON-RPC request with certain <see cref="HttpContext"/>, and returns the response.
        /// </summary>
        /// <param name="message">The message to be processed.</param>
        /// <param name="context">The HTTP request context.</param>
        /// <param name="waitForNotification">Whether to wait for the handler for the notification request to finish before completing the task.</param>
        /// <returns>The JSON-RPC response, or <c>null</c> if there's no such response.</returns>
        /// <exception cref="ArgumentNullException">Either <paramref name="message"/> or <paramref name="context"/> is <c>null</c>.</exception>
        /// <remarks>
        /// <para>This method will enable <see cref="IAspNetCoreFeature"/> in invoked JSON RPC service handler.</para>
        /// <para>This method will use <see cref="HttpContext.RequestAborted"/> as cancellation token passed into <see cref="RequestContext"/>.</para>
        /// </remarks>
        public async Task<ResponseMessage> ProcessRequestAsync(RequestMessage message, HttpContext context, bool waitForNotification)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (context == null) throw new ArgumentNullException(nameof(context));
            var features = new AspNetCoreFeatureCollection(DefaultFeatures, context);
            var task = ServiceHost.InvokeAsync(message, features, context.RequestAborted);
            if (waitForNotification || !message.IsNotification) return await task.ConfigureAwait(false);
            return null;
        }
    }

}
