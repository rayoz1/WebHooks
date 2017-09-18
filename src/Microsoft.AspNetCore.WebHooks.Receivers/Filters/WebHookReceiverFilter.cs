﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;          // ??? Will we run FxCop on the AspNetCore projects?
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Internal;       // ??? BufferingHelper is pub-Internal.
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebHooks.Properties;
using Microsoft.AspNetCore.WebHooks.Utilities;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Microsoft.AspNetCore.WebHooks.Filters
{
    /// <summary>
    /// Base class for <see cref="IWebHookReceiver"/> implementations that for example verify request signatures.
    /// Subclasses normally also implement <see cref="Mvc.Filters.IResourceFilter"/> or
    /// <see cref="Mvc.Filters.IAsyncResourceFilter"/>. Subclasses should have an
    /// <see cref="Mvc.Filters.IOrderedFilter.Order"/> less than <see cref="WebHookVerifyMethodFilter.Order"/>.
    /// </summary>
    public abstract class WebHookReceiverFilter : IWebHookReceiver
    {
        // Application setting for disabling HTTPS check
        internal const string DisableHttpsCheckKey = "MS_WebHookDisableHttpsCheck";

        // Information about the 'code' URI parameter
        internal const int CodeMinLength = 32;
        internal const int CodeMaxLength = 128;
        internal const string CodeQueryParameter = "code";

        /// <summary>
        /// Instantiates a new <see cref="WebHookReceiverFilter"/> instance.
        /// </summary>
        /// <param name="loggerFactory">
        /// The <see cref="ILoggerFactory"/> used to initialize <see cref="Logger"/>.
        /// </param>
        /// <param name="receiverConfig">The <see cref="IWebHookReceiverConfig"/>.</param>
        protected WebHookReceiverFilter(ILoggerFactory loggerFactory, IWebHookReceiverConfig receiverConfig)
        {
            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }
            if (receiverConfig == null)
            {
                throw new ArgumentNullException(nameof(receiverConfig));
            }

            Logger = loggerFactory.CreateLogger(GetType());
            ReceiverConfig = receiverConfig;
        }

        /// <summary>
        /// Gets the <see cref="Mvc.Filters.IOrderedFilter.Order"/> recommended for all
        /// <see cref="WebHookReceiverFilter"/> instances. The recommended filter sequence is
        /// <list type="number">
        /// <item><description>
        /// Confirm signature e.g. in a subclass of this filter.
        /// </description></item>
        /// <item><description>Short-circuit GET or HEAD requests, if receiver supports either.</description></item>
        /// <item>
        /// <description>Confirm it's a POST request (<see cref="WebHookVerifyMethodFilter"/>).</description>
        /// </item>
        /// <item><description>Confirm body type (<see cref="WebHookVerifyBodyTypeFilter"/>).</description></item>
        /// <item><description>
        /// Short-circuit ping requests, if not done in #2 for this receiver (<see cref="WebHookPingResponseFilter"/>).
        /// </description></item>
        /// </list>
        /// </summary>
        public static int Order => -500;

        /// <inheritdoc />
        public abstract string ReceiverName { get; }

        /// <summary>
        /// Gets the current <see cref="IConfiguration"/> for the application.
        /// </summary>
        protected IConfiguration Configuration => ReceiverConfig.Configuration;

        /// <summary>
        /// Gets an <see cref="ILogger"/> for use in this class and any subclasses.
        /// </summary>
        /// <remarks>
        /// Methods in this class use <see cref="EventId"/>s that should be distinct from (higher than) those used in
        /// subclasses.
        /// </remarks>
        protected ILogger Logger { get; }

        /// <summary>
        /// Gets the <see cref="IWebHookReceiverConfig"/> for WebHook receivers in this application.
        /// </summary>
        protected IWebHookReceiverConfig ReceiverConfig { get; }

        /// <inheritdoc />
        public bool IsApplicable(string receiverName)
        {
            if (receiverName == null)
            {
                throw new ArgumentNullException(nameof(receiverName));
            }

            return string.Equals(ReceiverName, receiverName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Provides a time consistent comparison of two secrets in the form of two byte arrays.
        /// </summary>
        /// <param name="inputA">The first secret to compare.</param>
        /// <param name="inputB">The second secret to compare.</param>
        /// <returns>Returns <c>true</c> if the two secrets are equal; <c>false</c> otherwise.</returns>
        [MethodImpl(MethodImplOptions.NoOptimization)]
        protected internal static bool SecretEqual(byte[] inputA, byte[] inputB)
        {
            if (ReferenceEquals(inputA, inputB))
            {
                return true;
            }

            if (inputA == null || inputB == null || inputA.Length != inputB.Length)
            {
                return false;
            }

            var areSame = true;
            for (var i = 0; i < inputA.Length; i++)
            {
                areSame &= inputA[i] == inputB[i];
            }

            return areSame;
        }

        /// <summary>
        /// Provides a time consistent comparison of two secrets in the form of two strings.
        /// </summary>
        /// <param name="inputA">The first secret to compare.</param>
        /// <param name="inputB">The second secret to compare.</param>
        /// <returns>Returns <c>true</c> if the two secrets are equal; <c>false</c> otherwise.</returns>
        [MethodImpl(MethodImplOptions.NoOptimization)]
        protected internal static bool SecretEqual(string inputA, string inputB)
        {
            if (ReferenceEquals(inputA, inputB))
            {
                return true;
            }

            if (inputA == null || inputB == null || inputA.Length != inputB.Length)
            {
                return false;
            }

            var areSame = true;
            for (var i = 0; i < inputA.Length; i++)
            {
                areSame &= inputA[i] == inputB[i];
            }

            return areSame;
        }

        /// <summary>
        /// Some WebHooks rely on HTTPS for sending WebHook requests in a secure manner. A
        /// <see cref="WebHookReceiverFilter"/> can call this method to ensure that the incoming WebHook request is
        /// using HTTPS. If the request is not using HTTPS an error will be generated and the request will not be
        /// further processed.
        /// </summary>
        /// <remarks>This method does allow local HTTP requests using <c>localhost</c>.</remarks>
        /// <param name="request">The current <see cref="HttpRequest"/>.</param>
        /// <returns>
        /// <c>null</c> in the success case. When a check fails, an <see cref="IActionResult"/> that when executed will
        /// produce a response containing details about the problem.
        /// </returns>
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed by caller.")]
        protected virtual IActionResult EnsureSecureConnection(HttpRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // Check to see if we have been configured to ignore this check
            var disableHttpsCheckValue = Configuration[DisableHttpsCheckKey];
            if (bool.TryParse(disableHttpsCheckValue, out var disableHttpsCheck) && disableHttpsCheck == true)
            {
                return null;
            }

            // Require HTTP unless request is local
            if (!request.IsLocal() && !request.IsHttps)
            {
                Logger.LogError(
                    500,
                    "The WebHook receiver '{ReceiverType}' requires HTTPS in order to be secure. " +
                    "Please register a WebHook URI of type '{SchemeName}'.",
                    GetType().Name,
                    Uri.UriSchemeHttps);

                var message = string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.Receiver_NoHttps,
                    GetType().Name,
                    Uri.UriSchemeHttps);
                var noHttps = WebHookResultUtilities.CreateErrorResult(message);

                return noHttps;
            }

            return null;
        }

        /// <summary>
        /// For WebHooks providers with insufficient security considerations, the receiver can require that the WebHook
        /// URI must be an <c>https</c> URI and contain a 'code' query parameter with a value configured for that
        /// particular <paramref name="id"/>. A sample WebHook URI is
        /// '<c>https://&lt;host&gt;/api/webhooks/incoming/&lt;receiver&gt;?code=83699ec7c1d794c0c780e49a5c72972590571fd8</c>'.
        /// The 'code' parameter must be between 32 and 128 characters long.
        /// </summary>
        /// <param name="request">The current <see cref="HttpRequest"/>.</param>
        /// <param name="id">
        /// A (potentially empty) ID of a particular configuration for this <see cref="WebHookReceiverFilter"/>. This
        /// allows an <see cref="WebHookReceiverFilter"/> to support multiple senders with individual configurations.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> that on completion provides <c>null</c> in the success case. When a check fails,
        /// provides an <see cref="IActionResult"/> that when executed will produce a response containing details about
        /// the problem.
        /// </returns>
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Response is disposed by Web API.")]
        protected virtual async Task<IActionResult> EnsureValidCode(HttpRequest request, string id)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var result = EnsureSecureConnection(request);
            if (result != null)
            {
                return result;
            }

            var code = request.Query[CodeQueryParameter];
            if (StringValues.IsNullOrEmpty(code))
            {
                Logger.LogError(
                    501,
                    "The WebHook verification request must contain a '{ParameterName}' query parameter.",
                    CodeQueryParameter);

                var message = string.Format(CultureInfo.CurrentCulture, Resources.Receiver_NoCode, CodeQueryParameter);
                var noCode = WebHookResultUtilities.CreateErrorResult(message);

                return noCode;
            }

            var secretKey = await GetReceiverConfig(request, ReceiverName, id, CodeMinLength, CodeMaxLength);
            if (!SecretEqual(code, secretKey))
            {
                Logger.LogError(
                    502,
                    "The '{ParameterName}' query parameter provided in the HTTP request did not match the expected value.",
                    CodeQueryParameter);

                var message = string.Format(CultureInfo.CurrentCulture, Resources.Receiver_BadCode, CodeQueryParameter);
                var invalidCode = WebHookResultUtilities.CreateErrorResult(message);

                return invalidCode;
            }

            return null;
        }

        /// <summary>
        /// Ensure we can read the <paramref name="request"/> body without messing up JSON etc. deserialization. Body
        /// will be read at least twice in most receivers.
        /// </summary>
        /// <param name="request">The <see cref="HttpRequest"/> to prepare.</param>
        public async Task PrepareRequestBody(HttpRequest request)
        {
            if (!request.Body.CanSeek)
            {
                BufferingHelper.EnableRewind(request);
                Debug.Assert(request.Body.CanSeek);

                await request.Body.DrainAsync(CancellationToken.None);
            }

            // Always start at the beginning.
            request.Body.Seek(0L, SeekOrigin.Begin);
        }

        /// <summary>
        /// Gets the locally configured WebHook secret key used to validate any signature header provided in a WebHook
        /// request.
        /// </summary>
        /// <param name="request">The current <see cref="HttpRequest"/>.</param>
        /// <param name="configurationName">
        /// The name of the configuration to obtain. Typically this the name of the receiver, e.g. <c>github</c>.
        /// </param>
        /// <param name="id">
        /// A (potentially empty) ID of a particular configuration for this <see cref="WebHookReceiverFilter"/>. This
        /// allows an <see cref="WebHookReceiverFilter"/> to support multiple senders with individual configurations.
        /// </param>
        /// <param name="minLength">The minimum length of the key value.</param>
        /// <param name="maxLength">The maximum length of the key value.</param>
        /// <returns>A <see cref="Task"/> that on completion provides the configured WebHook secret key.</returns>
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed by caller")]
        protected virtual async Task<string> GetReceiverConfig(
            HttpRequest request,
            string configurationName,
            string id,
            int minLength,
            int maxLength)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (configurationName == null)
            {
                throw new ArgumentNullException(nameof(configurationName));
            }

            // Look up configuration for this receiver and instance
            var secret = await ReceiverConfig.GetReceiverConfigAsync(configurationName, id, minLength, maxLength);
            if (secret == null)
            {
                Logger.LogCritical(
                    503,
                    "Could not find a valid configuration for WebHook receiver '{ReceiverName}' and instance '{Id}'. " +
                    "The setting must be set to a value between {MinLength} and {MaxLength} characters long.",
                    configurationName,
                    id,
                    minLength,
                    maxLength);

                var message = string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.Receiver_BadSecret,
                    configurationName,
                    id,
                    minLength,
                    maxLength);
                throw new InvalidOperationException(message);
            }

            return secret;
        }

        /// <summary>
        /// Gets the value of a given HTTP request <paramref name="headerName"/>. If the field is either not present in
        /// the <paramref name="request"/> or has more than one value then an error is generated and returned in
        /// <paramref name="errorResult"/>.
        /// </summary>
        /// <param name="request">The current <see cref="HttpRequest"/>.</param>
        /// <param name="headerName">The name of the HTTP request header to look up.</param>
        /// <param name="errorResult">
        /// Set to <c>null</c> in the success case. When a check fails, an <see cref="IActionResult"/> that when
        /// executed will produce a response containing details about the problem.
        /// </param>
        /// <returns>The signature header; <c>null</c> if this cannot be found.</returns>
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed by caller")]
        protected virtual string GetRequestHeader(
            HttpRequest request,
            string headerName,
            out IActionResult errorResult)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (headerName == null)
            {
                throw new ArgumentNullException(nameof(headerName));
            }

            if (!request.Headers.TryGetValue(headerName, out var headers) || headers.Count != 1)
            {
                var headersCount = headers.Count;
                Logger.LogInformation(
                    504,
                    "Expecting exactly one '{HeaderName}' header field in the WebHook request but found {HeaderCount}. " +
                    "Please ensure that the request contains exactly one '{HeaderName}' header field.",
                    headerName,
                    headersCount);

                var message = string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.Receiver_BadHeader,
                    headerName,
                    headersCount);
                errorResult = WebHookResultUtilities.CreateErrorResult(message);

                return null;
            }

            errorResult = null;

            return headers;
        }

        /// <summary>
        /// Returns a new <see cref="IActionResult"/> that when executed produces a response indicating that a
        /// request had an invalid signature and as a result could not be processed.
        /// </summary>
        /// <param name="request">The current <see cref="HttpRequest"/>.</param>
        /// <param name="signatureHeaderName">The name of the HTTP header with invalid contents.</param>
        /// <returns>
        /// An <see cref="IActionResult"/> that when executed will produce a response with status code 400 "Bad
        /// Request" and containing details about the problem.
        /// </returns>
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope", Justification = "Disposed by caller")]
        protected virtual IActionResult CreateBadSignatureResult(HttpRequest request, string signatureHeaderName)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            Logger.LogError(
                505,
                "The WebHook signature provided by the '{HeaderName}' header field does not match the value expected " +
                "by the '{ReceiverType}' receiver. WebHook request is invalid.",
                signatureHeaderName,
                GetType().Name);

            var message = string.Format(
                CultureInfo.CurrentCulture,
                Resources.Receiver_BadSignature,
                signatureHeaderName,
                GetType().Name);
            var badSignature = WebHookResultUtilities.CreateErrorResult(message);

            return badSignature;
        }
    }
}