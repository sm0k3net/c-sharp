﻿using System;
using System.Threading;
using System.Net;
using System.Threading.Tasks;
using System.IO;
using System.Globalization;
#if !NET35 && !NET40 && !NET45 && !NET461 && !NETSTANDARD10
using System.Net.Http;
using System.Net.Http.Headers;
#endif

namespace PubnubApi
{
	#region "Network Status -- code split required"
	internal class ClientNetworkStatus
	{
        private readonly IJsonPluggableLibrary jsonLib;
        private readonly PNConfiguration pubnubConfig;
        private readonly IPubnubUnitTest unit;
        private readonly IPubnubLog pubnubLog;

        private bool networkStatus = true;
#if !NET35 && !NET40 && !NET45 && !NET461 && !NETSTANDARD10
        private static HttpClient httpClient;
#endif

#if !NET35 && !NET40 && !NET45 && !NET461 && !NETSTANDARD10
        public ClientNetworkStatus(PNConfiguration config, IJsonPluggableLibrary jsonPluggableLibrary, IPubnubUnitTest pubnubUnit, IPubnubLog log, HttpClient refHttpClient)
#else
        public ClientNetworkStatus(PNConfiguration config, IJsonPluggableLibrary jsonPluggableLibrary, IPubnubUnitTest pubnubUnit, IPubnubLog log)
#endif
        {
            pubnubConfig = config;
            jsonLib = jsonPluggableLibrary;
            unit = pubnubUnit;
            pubnubLog = log;

#if !NET35 && !NET40 && !NET45 && !NET461 && !NETSTANDARD10
            httpClient = refHttpClient;
            if (httpClient == null)
            {
                HttpClientHandler httpClientHandler = new HttpClientHandler();
                if (httpClientHandler.SupportsProxy && pubnubConfig.Proxy != null)
                {
                    httpClientHandler.Proxy = pubnubConfig.Proxy;
                    httpClientHandler.UseProxy = true;
                }
                PubnubHttpClientHandler pubnubHttpClientHandler = new PubnubHttpClientHandler("PubnubHttpClientHandler", httpClientHandler, pubnubConfig, jsonLib, unit, pubnubLog);

                httpClient = new HttpClient(pubnubHttpClientHandler);
                httpClient.DefaultRequestHeaders.Accept.Clear();
                httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                httpClient.Timeout = TimeSpan.FromSeconds(pubnubConfig.NonSubscribeRequestTimeout);
            }
#endif
        }

		internal bool CheckInternetStatus<T>(bool systemActive, PNOperationType type, PNCallback<T> callback, string[] channels, string[] channelGroups)
		{
            if (unit != null)
            {
                return unit.InternetAvailable;
            }
			else
			{
                Task[] tasks = new Task[1];

                tasks[0] = Task.Factory.StartNew(async() => await CheckClientNetworkAvailability(CallbackClientNetworkStatus, type, callback, channels, channelGroups).ConfigureAwait(false));
                tasks[0].ConfigureAwait(false);
                try
                {
                    Task.WaitAll(tasks);
                }
                catch (AggregateException ae) {
                    foreach (var ie in ae.InnerExceptions)
                    {
                        LoggingMethod.WriteToLog(pubnubLog, string.Format("DateTime {0} AggregateException CheckInternetStatus Error: {1} {2} ", DateTime.Now.ToString(CultureInfo.InvariantCulture), ie.GetType().Name, ie.Message), pubnubConfig.LogVerbosity);
                    }
                }

                return networkStatus;
			}
		}

		private void CallbackClientNetworkStatus(bool status)
		{
			networkStatus = status;
		}

        private static object internetCheckLock = new object();
        private bool isInternetCheckRunning;

        internal bool IsInternetCheckRunning()
        {
            return isInternetCheckRunning;
        }

        private async Task<bool> CheckClientNetworkAvailability<T>(Action<bool> internalCallback, PNOperationType type, PNCallback<T> callback, string[] channels, string[] channelGroups)
		{
            lock (internetCheckLock)
            {
                if (isInternetCheckRunning)
                {
                    LoggingMethod.WriteToLog(pubnubLog, string.Format("DateTime {0} InternetCheckRunning Already running", DateTime.Now.ToString(CultureInfo.InvariantCulture)), pubnubConfig.LogVerbosity);
                    return networkStatus;
                }
            }

			InternetState<T> state = new InternetState<T>();
			state.InternalCallback = internalCallback;
            state.PubnubCallbacck = callback;
            state.ResponseType = type;
            state.Channels = channels;
            state.ChannelGroups = channelGroups;

            networkStatus = await CheckSocketConnect<T>(state).ConfigureAwait(false);
            return networkStatus;
		}

        private async Task<bool> CheckSocketConnect<T>(object internetState)
		{
            lock (internetCheckLock)
            {
                isInternetCheckRunning = true;
            }
            LoggingMethod.WriteToLog(pubnubLog, string.Format("DateTime {0} CheckSocketConnect Entered", DateTime.Now.ToString(CultureInfo.InvariantCulture)), pubnubConfig.LogVerbosity);

            Action<bool> internalCallback = null;
            PNCallback<T> pubnubCallback = null;
            PNOperationType type = PNOperationType.None;
            string[] channels = null;
            string[] channelGroups = null;

            var t = new TaskCompletionSource<bool>();

            InternetState<T> state = internetState as InternetState<T>;
            if (state != null)
            {
                internalCallback = state.InternalCallback;
                type = state.ResponseType;
                pubnubCallback = state.PubnubCallbacck;
                channels = state.Channels;
                channelGroups = state.ChannelGroups;
            }

            PubnubApi.Interface.IUrlRequestBuilder urlBuilder = new UrlRequestBuilder(pubnubConfig, jsonLib, unit, pubnubLog, null);
            Uri requestUri = urlBuilder.BuildTimeRequest(null);
            try
            {
                bool gotTimeResp = false;
                if (pubnubConfig.UseClassicHttpWebRequest)
                {
                    gotTimeResp = await GetTimeWithClassicHttp<T>(requestUri).ConfigureAwait(false);
                    t.TrySetResult(gotTimeResp);
                }
                else
                {
#if !NET35 && !NET40 && !NET45 && !NET461 && !NETSTANDARD10
                    if (pubnubConfig.UseTaskFactoryAsyncInsteadOfHttpClient)
                    {
                        gotTimeResp = await GetTimeWithTaskFactoryAsync(requestUri);
                    }
                    else
                    {
                        gotTimeResp = await GetTimeWithHttpClient(requestUri);
                    }
                    t.TrySetResult(gotTimeResp);
#else
                    gotTimeResp = await GetTimeWithTaskFactoryAsync(requestUri);
                    t.TrySetResult(gotTimeResp);
#endif
                }
            }
            catch (Exception ex)
            {
                networkStatus = false;
                LoggingMethod.WriteToLog(pubnubLog, string.Format("DateTime {0} CheckSocketConnect (HttpClient Or Task.Factory) Failed {1}", DateTime.Now.ToString(CultureInfo.InvariantCulture), ex.Message), pubnubConfig.LogVerbosity);
                if (!networkStatus)
                {
                    t.TrySetResult(false);
                    isInternetCheckRunning = false;
                    ParseCheckSocketConnectException<T>(ex, type, pubnubCallback, internalCallback);
                }
            }
            finally
            {
                isInternetCheckRunning = false;
            }
            return await t.Task.ConfigureAwait(false);
		}


        private void ParseCheckSocketConnectException<T>(Exception ex, PNOperationType type, PNCallback<T> callback, Action<bool> internalcallback)
		{
            PNStatusCategory errorCategory = PNStatusCategoryHelper.GetPNStatusCategory(ex);
            StatusBuilder statusBuilder = new StatusBuilder(pubnubConfig, jsonLib);
            PNStatus status = statusBuilder.CreateStatusResponse<T>(type, errorCategory, null, (int)System.Net.HttpStatusCode.NotFound, ex);

            if (callback != null)
            {
                callback.OnResponse(default(T), status);
            }

			LoggingMethod.WriteToLog(pubnubLog, string.Format("DateTime {0} ParseCheckSocketConnectException Error. {1}", DateTime.Now.ToString(CultureInfo.InvariantCulture), ex.Message), pubnubConfig.LogVerbosity);
			internalcallback(false);
		}

#if !NET35 && !NET40 && !NET45 && !NET461 && !NETSTANDARD10
        private async Task<bool> GetTimeWithHttpClient(Uri requestUri)
        {
            bool successFlag = false;
            try
            {
                var response = await httpClient.GetAsync(requestUri);
                successFlag = response.IsSuccessStatusCode;
                if (successFlag)
                {
                    LoggingMethod.WriteToLog(pubnubLog, string.Format("DateTime {0} GetTimeWithHttpClient Resp OK", DateTime.Now.ToString(CultureInfo.InvariantCulture)), pubnubConfig.LogVerbosity);
                }
                else
                {
                    LoggingMethod.WriteToLog(pubnubLog, string.Format("DateTime {0} GetTimeWithHttpClient FAILED", DateTime.Now.ToString(CultureInfo.InvariantCulture)), pubnubConfig.LogVerbosity);
                }
            }
            finally
            {
                networkStatus = successFlag;
            }
            return networkStatus;
        }
#endif
        private async Task<bool> GetTimeWithTaskFactoryAsync(Uri requestUri)
        {
            bool successFlag = false;
            try
            {
                HttpWebRequest myRequest = null;
                myRequest = (HttpWebRequest)System.Net.WebRequest.Create(requestUri);
                myRequest.Method = "GET";
                using (HttpWebResponse response = await Task.Factory.FromAsync<HttpWebResponse>(myRequest.BeginGetResponse, asyncPubnubResult => (HttpWebResponse)myRequest.EndGetResponse(asyncPubnubResult), null))
                {
                    if (response != null)
                    {
                        LoggingMethod.WriteToLog(pubnubLog, string.Format("DateTime {0} GetTimeWithTaskFactoryAsync Resp {1}", DateTime.Now.ToString(CultureInfo.InvariantCulture), response.StatusCode.ToString()), pubnubConfig.LogVerbosity);
                        successFlag = response.StatusCode == HttpStatusCode.OK;
                    }
                    else
                    {
                        LoggingMethod.WriteToLog(pubnubLog, string.Format("DateTime {0} GetTimeWithTaskFactoryAsync FAILED.", DateTime.Now.ToString(CultureInfo.InvariantCulture)), pubnubConfig.LogVerbosity);
                    }
                }
            }
            finally
            {
                networkStatus = successFlag;
            }
            return networkStatus;
        }

        private async Task<bool> GetTimeWithClassicHttp<T>(Uri requestUri)
        {
            bool successFlag = true;
            try
            {
                HttpWebRequest myRequest = (HttpWebRequest)System.Net.WebRequest.Create(requestUri);
                myRequest.Method = "GET";

                RequestState<T> pubnubRequestState = new RequestState<T>();
                pubnubRequestState.Request = myRequest;

                IAsyncResult asyncResult = myRequest.BeginGetResponse(new AsyncCallback(
                        (asynchronousResult) => {
                            RequestState<T> asyncRequestState = asynchronousResult.AsyncState as RequestState<T>;
                            HttpWebRequest asyncWebRequest = asyncRequestState.Request as HttpWebRequest;
                            if (asyncWebRequest != null)
                            {
                                HttpWebResponse asyncWebResponse = (HttpWebResponse)asyncWebRequest.EndGetResponse(asynchronousResult);
                                if (asyncWebResponse != null)
                                {
                                    successFlag = asyncWebResponse.StatusCode == HttpStatusCode.OK;
                                }
                                if (asyncRequestState.Response != null)
                                {
#if NET35 || NET40 || NET45 || NET461
                                    pubnubRequestState.Response.Close();
#endif
                                    asyncRequestState.Response = null;
                                    asyncRequestState.Request = null;
                                }
                            }
                        }
                        ), pubnubRequestState);

                Timer webRequestTimer = new Timer(OnPubnubWebRequestTimeout<T>, pubnubRequestState, pubnubConfig.NonSubscribeRequestTimeout * 1000, Timeout.Infinite);
            }
            finally
            {
#if NET35 || NET40
                await Task.Factory.StartNew(() => { });
#else
                await Task.Delay(1);
#endif
                networkStatus = successFlag;
            }
            return networkStatus;
        }

        private void OnPubnubWebRequestTimeout<T>(System.Object requestState)
        {
            RequestState<T> currentState = requestState as RequestState<T>;
            if (currentState != null && currentState.Response == null && currentState.Request != null)
            {
                currentState.Timeout = true;
                try
                {
                    currentState.Request.Abort();
                }
                catch {  /* ignore */ }

                LoggingMethod.WriteToLog(pubnubLog, string.Format("DateTime: {0}, GetTimeWithClassicHttp timedout", DateTime.Now.ToString(CultureInfo.InvariantCulture)), pubnubConfig.LogVerbosity);
            }
        }
    }
    #endregion
}

