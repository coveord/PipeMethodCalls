﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PipeMethodCalls
{
	/// <summary>
	/// Handles incoming method requests over a pipe stream.
	/// </summary>
	/// <typeparam name="THandling">The interface for the method requests.</typeparam>
	internal class RequestHandler<THandling> : IRequestHandler
	{
		private readonly Func<THandling> handlerFactoryFunc;
		private readonly PipeStreamWrapper pipeStreamWrapper;

		/// <summary>
		/// Initializes a new instance of the <see cref="RequestHandler"/> class.
		/// </summary>
		/// <param name="pipeStreamWrapper">The underlying pipe stream wrapper.</param>
		/// <param name="handlerFactoryFunc"></param>
		public RequestHandler(PipeStreamWrapper pipeStreamWrapper, Func<THandling> handlerFactoryFunc)
		{
			this.pipeStreamWrapper = pipeStreamWrapper;
			this.handlerFactoryFunc = handlerFactoryFunc;

			this.pipeStreamWrapper.RequestHandler = this;
		}

		/// <summary>
		/// Handles a request message received from a remote endpoint.
		/// </summary>
		/// <param name="request">The request message.</param>
		public async void HandleRequest(PipeRequest request)
		{
			PipeResponse response = await this.HandleRequestAsync(request).ConfigureAwait(false);
			await this.pipeStreamWrapper.SendResponseAsync(response, CancellationToken.None).ConfigureAwait(false);
		}

		/// <summary>
		/// Handles a request from a remote endpoint.
		/// </summary>
		/// <param name="request">The request.</param>
		/// <returns>The response.</returns>
		private async Task<PipeResponse> HandleRequestAsync(PipeRequest request)
		{
			if (this.handlerFactoryFunc == null)
			{
				return PipeResponse.Failure(request.CallId, $"No handler implementation registered for interface '{typeof(THandling).FullName}' found.");
			}

			THandling handlerInstance = this.handlerFactoryFunc();
			if (handlerInstance == null)
			{
				return PipeResponse.Failure(request.CallId, $"Handler implementation returned null for interface '{typeof(THandling).FullName}'");
			}

			MethodInfo method = handlerInstance.GetType().GetMethod(request.MethodName);
			if (method == null)
			{
				return PipeResponse.Failure(request.CallId, $"Method '{request.MethodName}' not found in interface '{typeof(THandling).FullName}'.");
			}

			ParameterInfo[] paramInfos = method.GetParameters();
			if (paramInfos.Length != request.Parameters.Length)
			{
				return PipeResponse.Failure(request.CallId, "Parameter mismatch.");
			}

			Type[] genericArguments = method.GetGenericArguments();
			if (genericArguments.Length != request.GenericArguments.Length)
			{
				return PipeResponse.Failure(request.CallId, "Generic arguments mismatch.");
			}

			object[] args = new object[paramInfos.Length];
			for (int i = 0; i < args.Length; i++)
			{
				object origValue = request.Parameters[i];
				Type destType = paramInfos[i].ParameterType;
				if (destType.IsGenericParameter)
				{
					destType = request.GenericArguments[destType.GenericParameterPosition];
				}

				if (Utilities.TryConvert(origValue, destType, out object arg))
				{
					args[i] = arg;
				}
				else
				{
					return PipeResponse.Failure(request.CallId, $"Cannot convert value of parameter '{paramInfos[i].Name}' ({origValue}) from {origValue.GetType().Name} to {destType.Name}.");
				}
			}

			try
			{
				if (method.IsGenericMethod)
				{
					method = method.MakeGenericMethod(request.GenericArguments);
				}

				object result = method.Invoke(handlerInstance, args);

				if (result is Task)
				{
					await ((Task)result).ConfigureAwait(false);

					var resultProperty = result.GetType().GetProperty("Result");
					return PipeResponse.Success(request.CallId, resultProperty?.GetValue(result));
				}
				else
				{
					return PipeResponse.Success(request.CallId, result);
				}
			}
			catch (Exception exception)
			{
				return PipeResponse.Failure(request.CallId, exception.ToString());
			}
		}
	}
}
