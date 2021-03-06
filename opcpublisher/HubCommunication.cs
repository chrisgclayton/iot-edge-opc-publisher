﻿
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpcPublisher
{
    using Microsoft.Azure.Devices.Client;
    using Newtonsoft.Json;
    using Opc.Ua;
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using static Diagnostics;
    using static OpcApplicationConfiguration;
    using static OpcPublisher.OpcMonitoredItem;
    using static OpcPublisher.PublisherNodeConfiguration;
    using static OpcPublisher.PublisherTelemetryConfiguration;
    using static Program;

    /// <summary>
    /// Class to handle all IoTHub/EdgeHub communication.
    /// </summary>
    public class HubCommunication
    {
        private class IotCentralMessage
        {
            public string Key { get; set; }
            public string Value { get; set; }

            public IotCentralMessage()
            {
                Key = null;
                Value = null;
            }
        }

        public static long MonitoredItemsQueueCount => _monitoredItemsDataQueue.Count;

        public static long NumberOfEvents { get; private set; }

        public static long MissedSendIntervalCount { get; private set; }

        public static long TooLargeCount { get; private set; }

        public static long SentBytes { get; private set; }

        public static long SentMessages { get; private set; }

        public static DateTime SentLastTime { get; private set; }

        public static long FailedMessages { get; private set; }

        public const uint HubMessageSizeMax = (256 * 1024);

        public static uint HubMessageSize { get; set; } = 262144;

        public static TransportType HubProtocol { get; set; } = TransportType.Mqtt_WebSocket_Only;

        public static int DefaultSendIntervalSeconds { get; set; } = 10;

        public static int MonitoredItemsQueueCapacity { get; set; } = 8192;

        public static long EnqueueCount => _enqueueCount;

        public static long EnqueueFailureCount => _enqueueFailureCount;

        public static bool IsHttp1Transport() => (_transportType == TransportType.Http1);

        public static bool IotCentralMode { get; set; } = false;

        /// <summary>
        /// Ctor for the class.
        /// </summary>
        public HubCommunication(CancellationToken ct)
        {
            _shutdownToken = ct;
        }

        /// <summary>
        /// Initializes edge message broker communication.
        /// </summary>
        public async Task<bool> InitHubCommunicationAsync(ModuleClient edgeHubClient, TransportType transportType)
        {
            // init EdgeHub communication parameters
            _edgeHubClient = edgeHubClient;
            return await InitHubCommunicationAsync(transportType).ConfigureAwait(false);
        }


        /// <summary>
        /// Initializes message broker communication.
        /// </summary>
        public async Task<bool> InitHubCommunicationAsync(DeviceClient iotHubClient, TransportType transportType)
        {
            // init IoTHub communication parameters
            _iotHubClient = iotHubClient;
            return await InitHubCommunicationAsync(transportType).ConfigureAwait(false);
        }

        /// <summary>
        /// Initializes message broker communication.
        /// </summary>
        private async Task<bool> InitHubCommunicationAsync(TransportType transportType)
        {
            try
            {
                // set hub communication parameters
                _transportType = transportType;
                ExponentialBackoff exponentialRetryPolicy = new ExponentialBackoff(int.MaxValue, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(1024), TimeSpan.FromMilliseconds(3));

                // show IoTCentral mode
                Logger.Information($"IoTCentral mode: {IotCentralMode}");

                if (_iotHubClient == null)
                {
                    _edgeHubClient.ProductInfo = "OpcPublisher";
                    _edgeHubClient.SetRetryPolicy(exponentialRetryPolicy);
                    // register connection status change handler
                    _edgeHubClient.SetConnectionStatusChangesHandler(ConnectionStatusChange);

                    // open connection
                    Logger.Debug($"Open EdgeHub communication");
                    await _edgeHubClient.OpenAsync().ConfigureAwait(false);

                    // init twin properties and method callbacks
                    Logger.Debug($"Register desired properties and method callbacks");

                    // register method handlers
                    await _edgeHubClient.SetMethodHandlerAsync("PublishNodes", HandlePublishNodesMethodAsync, _edgeHubClient).ConfigureAwait(false);
                    await _edgeHubClient.SetMethodHandlerAsync("UnpublishNodes", HandleUnpublishNodesMethodAsync, _edgeHubClient).ConfigureAwait(false);
                    await _edgeHubClient.SetMethodHandlerAsync("UnpublishAllNodes", HandleUnpublishAllNodesMethodAsync, _edgeHubClient).ConfigureAwait(false);
                    await _edgeHubClient.SetMethodHandlerAsync("GetConfiguredEndpoints", HandleGetConfiguredEndpointsMethodAsync, _edgeHubClient).ConfigureAwait(false);
                    await _edgeHubClient.SetMethodHandlerAsync("GetConfiguredNodesOnEndpoint", HandleGetConfiguredNodesOnEndpointMethodAsync, _edgeHubClient).ConfigureAwait(false);
                    await _edgeHubClient.SetMethodHandlerAsync("GetDiagnosticInfo", HandleGetDiagnosticInfoMethodAsync, _edgeHubClient).ConfigureAwait(false);
                    await _edgeHubClient.SetMethodHandlerAsync("GetDiagnosticLog", HandleGetDiagnosticLogMethodAsync, _edgeHubClient).ConfigureAwait(false);
                    await _edgeHubClient.SetMethodHandlerAsync("GetDiagnosticStartupLog", HandleGetDiagnosticStartupLogMethodAsync, _edgeHubClient).ConfigureAwait(false);
                    await _edgeHubClient.SetMethodHandlerAsync("ExitApplication", HandleExitApplicationMethodAsync, _edgeHubClient).ConfigureAwait(false);
                    await _edgeHubClient.SetMethodHandlerAsync("GetInfo", HandleGetInfoMethodAsync, _edgeHubClient).ConfigureAwait(false);
                    await _edgeHubClient.SetMethodDefaultHandlerAsync(DefaultMethodHandlerAsync, _edgeHubClient).ConfigureAwait(false);
                }
                else
                {
                    _iotHubClient.ProductInfo = "OpcPublisher";
                    _iotHubClient.SetRetryPolicy(exponentialRetryPolicy);
                    // register connection status change handler
                    _iotHubClient.SetConnectionStatusChangesHandler(ConnectionStatusChange);

                    // open connection
                    Logger.Debug($"Open IoTHub communication");
                    await _iotHubClient.OpenAsync().ConfigureAwait(false);

                    // init twin properties and method callbacks (not supported for HTTP)
                    if (!IsHttp1Transport())
                    {
                        Logger.Debug($"Register desired properties and method callbacks");

                        // register method handlers
                        await _iotHubClient.SetMethodHandlerAsync("PublishNodes", HandlePublishNodesMethodAsync, _iotHubClient).ConfigureAwait(false);
                        await _iotHubClient.SetMethodHandlerAsync("UnpublishNodes", HandleUnpublishNodesMethodAsync, _iotHubClient).ConfigureAwait(false);
                        await _iotHubClient.SetMethodHandlerAsync("UnpublishAllNodes", HandleUnpublishAllNodesMethodAsync, _iotHubClient).ConfigureAwait(false);
                        await _iotHubClient.SetMethodHandlerAsync("GetConfiguredEndpoints", HandleGetConfiguredEndpointsMethodAsync, _iotHubClient).ConfigureAwait(false);
                        await _iotHubClient.SetMethodHandlerAsync("GetConfiguredNodesOnEndpoint", HandleGetConfiguredNodesOnEndpointMethodAsync, _iotHubClient).ConfigureAwait(false);
                        await _iotHubClient.SetMethodHandlerAsync("GetDiagnosticInfo", HandleGetDiagnosticInfoMethodAsync, _iotHubClient).ConfigureAwait(false);
                        await _iotHubClient.SetMethodHandlerAsync("GetDiagnosticLog", HandleGetDiagnosticLogMethodAsync, _iotHubClient).ConfigureAwait(false);
                        await _iotHubClient.SetMethodHandlerAsync("GetDiagnosticStartupLog", HandleGetDiagnosticStartupLogMethodAsync, _iotHubClient).ConfigureAwait(false);
                        await _iotHubClient.SetMethodHandlerAsync("ExitApplication", HandleExitApplicationMethodAsync, _iotHubClient).ConfigureAwait(false);
                        await _iotHubClient.SetMethodHandlerAsync("GetInfo", HandleGetInfoMethodAsync, _iotHubClient).ConfigureAwait(false);
                        await _iotHubClient.SetMethodDefaultHandlerAsync(DefaultMethodHandlerAsync, _iotHubClient).ConfigureAwait(false);
                    }
                }
                Logger.Debug($"Init D2C message processing");
                return await InitMessageProcessingAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failure initializing hub communication processing.");
                return false;
            }
        }

        /// <summary>
        /// Handle connection status change notifications.
        /// </summary>
        static void ConnectionStatusChange(ConnectionStatus status, ConnectionStatusChangeReason reason)
        {
            if (reason == ConnectionStatusChangeReason.Connection_Ok || ShutdownTokenSource.IsCancellationRequested)
            {
                Logger.Information($"Connection status changed to '{status}', reason '{reason}'");
            }
            else
            {
                Logger.Error($"Connection status changed to '{status}', reason '{reason}'");
            }
        }

        /// <summary>
        /// Handle publish node method call.
        /// </summary>
        static async Task<MethodResponse> HandlePublishNodesMethodAsync(MethodRequest methodRequest, object userContext)
        {
            string logPrefix = "HandlePublishNodesMethodAsync:";
            Uri endpointUri = null;
            PublishNodesMethodRequestModel publishNodesMethodData = null;
            HttpStatusCode statusCode = HttpStatusCode.OK;
            HttpStatusCode nodeStatusCode = HttpStatusCode.InternalServerError;
            List<string> statusResponse = new List<string>();
            string statusMessage = string.Empty;
            try
            {
                Logger.Debug($"{logPrefix} called");
                publishNodesMethodData = JsonConvert.DeserializeObject<PublishNodesMethodRequestModel>(methodRequest.DataAsJson);
                endpointUri = new Uri(publishNodesMethodData.EndpointUrl);
            }
            catch (UriFormatException e)
            {
                statusMessage = $"Exception ({e.Message}) while parsing EndpointUrl '{publishNodesMethodData.EndpointUrl}'";
                Logger.Error(e, $"{logPrefix} {statusMessage}");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.NotAcceptable;
            }
            catch (Exception e)
            {
                statusMessage = $"Exception ({e.Message}) while deserializing message payload";
                Logger.Error(e, $"{logPrefix} {statusMessage}");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }

            if (statusCode == HttpStatusCode.OK)
            {
                // find/create a session to the endpoint URL and start monitoring the node.
                try
                {
                    // lock the publishing configuration till we are done
                    await OpcSessionsListSemaphore.WaitAsync().ConfigureAwait(false);

                    if (ShutdownTokenSource.IsCancellationRequested)
                    {
                        statusMessage = $"Publisher is in shutdown";
                        Logger.Warning($"{logPrefix} {statusMessage}");
                        statusResponse.Add(statusMessage);
                        statusCode = HttpStatusCode.Gone;
                    }
                    else
                    {
                        // find the session we need to monitor the node
                        OpcSession opcSession = null;
                        opcSession = OpcSessions.FirstOrDefault(s => s.EndpointUrl.Equals(endpointUri.OriginalString, StringComparison.OrdinalIgnoreCase));

                        // add a new session.
                        if (opcSession == null)
                        {
                            // create new session info.
                            opcSession = new OpcSession(endpointUri.OriginalString, true, OpcSessionCreationTimeout);
                            OpcSessions.Add(opcSession);
                            Logger.Information($"{logPrefix} No matching session found for endpoint '{endpointUri.OriginalString}'. Requested to create a new one.");
                        }

                        // process all nodes
                        foreach (var node in publishNodesMethodData.OpcNodes)
                        {
                            // support legacy format
                            if (string.IsNullOrEmpty(node.Id) && !string.IsNullOrEmpty(node.ExpandedNodeId))
                            {
                                    node.Id = node.ExpandedNodeId;
                            }

                            NodeId nodeId = null;
                            ExpandedNodeId expandedNodeId = null;
                            bool isNodeIdFormat = true;
                            try
                            {
                                if (node.Id.Contains("nsu=", StringComparison.InvariantCulture))
                                {
                                    expandedNodeId = ExpandedNodeId.Parse(node.Id);
                                    isNodeIdFormat = false;
                                }
                                else
                                {
                                    nodeId = NodeId.Parse(node.Id);
                                    isNodeIdFormat = true;
                                }
                            }
                            catch (Exception e)
                            {
                                statusMessage = $"Exception ({e.Message}) while formatting node '{node.Id}'!";
                                Logger.Error(e, $"{logPrefix} {statusMessage}");
                                statusResponse.Add(statusMessage);
                                statusCode = HttpStatusCode.NotAcceptable;
                                continue;
                            }

                            try
                            {
                                if (isNodeIdFormat)
                                {
                                    // add the node info to the subscription with the default publishing interval, execute syncronously
                                    Logger.Debug($"{logPrefix} Request to monitor item with NodeId '{node.Id}' (PublishingInterval: {node.OpcPublishingInterval.ToString() ?? "--"}, SamplingInterval: {node.OpcSamplingInterval.ToString() ?? "--"})");
                                    nodeStatusCode = await opcSession.AddNodeForMonitoringAsync(nodeId, null, node.OpcPublishingInterval, node.OpcSamplingInterval, node.DisplayName, ShutdownTokenSource.Token).ConfigureAwait(false);
                                }
                                else
                                {
                                    // add the node info to the subscription with the default publishing interval, execute syncronously
                                    Logger.Debug($"{logPrefix} Request to monitor item with ExpandedNodeId '{node.Id}' (PublishingInterval: {node.OpcPublishingInterval.ToString() ?? "--"}, SamplingInterval: {node.OpcSamplingInterval.ToString() ?? "--"})");
                                    nodeStatusCode = await opcSession.AddNodeForMonitoringAsync(null, expandedNodeId, node.OpcPublishingInterval, node.OpcSamplingInterval, node.DisplayName, ShutdownTokenSource.Token).ConfigureAwait(false);
                                }

                                // check and store a result message in case of an error
                                switch (nodeStatusCode)
                                {
                                    case HttpStatusCode.OK:
                                        statusMessage = $"'{node.Id}': already monitored";
                                        Logger.Debug($"{logPrefix} {statusMessage}");
                                        statusResponse.Add(statusMessage);
                                        break;

                                    case HttpStatusCode.Accepted:
                                        statusMessage = $"'{node.Id}': added";
                                        Logger.Debug($"{logPrefix} {statusMessage}");
                                        statusResponse.Add(statusMessage);
                                        break;

                                    case HttpStatusCode.Gone:
                                        statusMessage = $"'{node.Id}': session to endpoint does not exist anymore";
                                        Logger.Debug($"{logPrefix} {statusMessage}");
                                        statusResponse.Add(statusMessage);
                                        statusCode = HttpStatusCode.Gone;
                                        break;

                                    case HttpStatusCode.InternalServerError:
                                        statusMessage = $"'{node.Id}': error while trying to configure";
                                        Logger.Debug($"{logPrefix} {statusMessage}");
                                        statusResponse.Add(statusMessage);
                                        statusCode = HttpStatusCode.InternalServerError;
                                        break;
                                }
                            }
                            catch (Exception e)
                            {
                                statusMessage = $"Exception ({e.Message}) while trying to configure publishing node '{node.Id}'";
                                Logger.Error(e, $"{logPrefix} {statusMessage}");
                                statusResponse.Add(statusMessage);
                                statusCode = HttpStatusCode.InternalServerError;
                            }
                        }
                    }
                }
                catch (AggregateException e)
                {
                    foreach (Exception ex in e.InnerExceptions)
                    {
                        Logger.Error(ex, $"{logPrefix} Exception");
                    }
                    statusMessage = $"EndpointUrl: '{publishNodesMethodData.EndpointUrl}': exception ({e.Message}) while trying to publish";
                    Logger.Error(e, $"{logPrefix} {statusMessage}");
                    statusResponse.Add(statusMessage);
                    statusCode = HttpStatusCode.InternalServerError;
                }
                catch (Exception e)
                {
                    statusMessage = $"EndpointUrl: '{publishNodesMethodData.EndpointUrl}': exception ({e.Message}) while trying to publish";
                    Logger.Error(e, $"{logPrefix} {statusMessage}");
                    statusResponse.Add(statusMessage);
                    statusCode = HttpStatusCode.InternalServerError;
                }
                finally
                {
                    OpcSessionsListSemaphore.Release();
                }
            }

            // adjust response size
            AdjustResponse(ref statusResponse);

            // build response
            string resultString = JsonConvert.SerializeObject(statusResponse);
            byte[] result = Encoding.UTF8.GetBytes(resultString);
            if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
            {
                Logger.Error($"{logPrefix} Response size is too long");
                Array.Resize(ref result, result.Length > MAX_RESPONSE_PAYLOAD_LENGTH ? MAX_RESPONSE_PAYLOAD_LENGTH : result.Length);
            }
            MethodResponse methodResponse = new MethodResponse(result, (int)statusCode);
            Logger.Information($"{logPrefix} completed with result {statusCode.ToString()}");
            return methodResponse;
        }

        /// <summary>
        /// Handle unpublish node method call.
        /// </summary>
        static async Task<MethodResponse> HandleUnpublishNodesMethodAsync(MethodRequest methodRequest, object userContext)
        {
            string logPrefix = "HandleUnpublishNodesMethodAsync:";
            NodeId nodeId = null;
            ExpandedNodeId expandedNodeId = null;
            Uri endpointUri = null;
            bool isNodeIdFormat = true;
            UnpublishNodesMethodRequestModel unpublishNodesMethodData = null;
            HttpStatusCode statusCode = HttpStatusCode.OK;
            HttpStatusCode nodeStatusCode = HttpStatusCode.InternalServerError;
            List<string> statusResponse = new List<string>();
            string statusMessage = string.Empty;
            try
            {
                Logger.Debug($"{logPrefix} called");
                unpublishNodesMethodData = JsonConvert.DeserializeObject<UnpublishNodesMethodRequestModel>(methodRequest.DataAsJson);
                endpointUri = new Uri(unpublishNodesMethodData.EndpointUrl);
            }
            catch (UriFormatException e)
            {
                statusMessage = $"Exception ({e.Message}) while parsing EndpointUrl '{unpublishNodesMethodData.EndpointUrl}'";
                Logger.Error(e, $"{logPrefix} {statusMessage}");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }
            catch (Exception e)
            {
                statusMessage = $"Exception ({e.Message}) while deserializing message payload";
                Logger.Error(e, $"{logPrefix} {statusMessage}");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }

            if (statusCode == HttpStatusCode.OK)
            {
                try
                {
                    await OpcSessionsListSemaphore.WaitAsync().ConfigureAwait(false);
                    if (ShutdownTokenSource.IsCancellationRequested)
                    {
                        statusMessage = $"Publisher is in shutdown";
                        Logger.Error($"{logPrefix} {statusMessage}");
                        statusResponse.Add(statusMessage);
                        statusCode = HttpStatusCode.Gone;
                    }
                    else
                    {
                        // find the session we need to monitor the node
                        OpcSession opcSession = null;
                        try
                        {
                            opcSession = OpcSessions.FirstOrDefault(s => s.EndpointUrl.Equals(endpointUri.OriginalString, StringComparison.OrdinalIgnoreCase));
                        }
                        catch
                        {
                            opcSession = null;
                        }

                        if (opcSession == null)
                        {
                            // do nothing if there is no session for this endpoint.
                            statusMessage = $"Session for endpoint '{endpointUri.OriginalString}' not found.";
                            Logger.Error($"{logPrefix} {statusMessage}");
                            statusResponse.Add(statusMessage);
                            statusCode = HttpStatusCode.Gone;
                        }
                        else
                        {
                            foreach (var node in unpublishNodesMethodData.OpcNodes)
                            {
                                // support legacy format
                                if (string.IsNullOrEmpty(node.Id) && !string.IsNullOrEmpty(node.ExpandedNodeId))
                                {
                                    node.Id = node.ExpandedNodeId;
                                }

                                try
                                {
                                    if (node.Id.Contains("nsu=", StringComparison.InvariantCulture))
                                    {
                                        expandedNodeId = ExpandedNodeId.Parse(node.Id);
                                        isNodeIdFormat = false;
                                    }
                                    else
                                    {
                                        nodeId = NodeId.Parse(node.Id);
                                        isNodeIdFormat = true;
                                    }

                                }
                                catch (Exception e)
                                {
                                    statusMessage = $"Exception ({e.Message}) while formatting node '{node.Id}'!";
                                    Logger.Error(e, $"{logPrefix} {statusMessage}");
                                    statusResponse.Add(statusMessage);
                                    statusCode = HttpStatusCode.NotAcceptable;
                                    continue;
                                }

                                try
                                {
                                    if (isNodeIdFormat)
                                    {
                                        // stop monitoring the node, execute synchronously
                                        Logger.Information($"{logPrefix} Request to stop monitoring item with NodeId '{nodeId.ToString()}')");
                                        nodeStatusCode = await opcSession.RequestMonitorItemRemovalAsync(nodeId, null, ShutdownTokenSource.Token).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        // stop monitoring the node, execute synchronously
                                        Logger.Information($"{logPrefix} Request to stop monitoring item with ExpandedNodeId '{expandedNodeId.ToString()}')");
                                        nodeStatusCode = await opcSession.RequestMonitorItemRemovalAsync(null, expandedNodeId, ShutdownTokenSource.Token).ConfigureAwait(false);
                                    }

                                    // check and store a result message in case of an error
                                    switch (nodeStatusCode)
                                    {
                                        case HttpStatusCode.OK:
                                            statusMessage = $"Id '{node.Id}': was not configured";
                                            Logger.Debug($"{logPrefix} {statusMessage}");
                                            statusResponse.Add(statusMessage);
                                            break;

                                        case HttpStatusCode.Accepted:
                                            statusMessage = $"Id '{node.Id}': tagged for removal";
                                            Logger.Debug($"{logPrefix} {statusMessage}");
                                            statusResponse.Add(statusMessage);
                                            break;

                                        case HttpStatusCode.Gone:
                                            statusMessage = $"Id '{node.Id}': session to endpoint does not exist anymore";
                                            Logger.Debug($"{logPrefix} {statusMessage}");
                                            statusResponse.Add(statusMessage);
                                            statusCode = HttpStatusCode.Gone;
                                            break;

                                        case HttpStatusCode.InternalServerError:
                                            statusMessage = $"Id '{node.Id}': error while trying to remove";
                                            Logger.Debug($"{logPrefix} {statusMessage}");
                                            statusResponse.Add(statusMessage);
                                            statusCode = HttpStatusCode.InternalServerError;
                                            break;
                                    }
                                }
                                catch (Exception e)
                                {
                                    statusMessage = $"Exception ({e.Message}) while trying to tag node '{node.Id}' for removal";
                                    Logger.Error(e, $"{logPrefix} {statusMessage}");
                                    statusResponse.Add(statusMessage);
                                    statusCode = HttpStatusCode.InternalServerError;
                                }
                            }
                        }
                    }

                }
                catch (AggregateException e)
                {
                    foreach (Exception ex in e.InnerExceptions)
                    {
                        Logger.Error(ex, $"{logPrefix} Exception");
                    }
                    statusMessage = $"EndpointUrl: '{unpublishNodesMethodData.EndpointUrl}': exception while trying to unpublish";
                    Logger.Error(e, $"{logPrefix} {statusMessage}");
                    statusResponse.Add(statusMessage);
                    statusCode = HttpStatusCode.InternalServerError;
                }
                catch (Exception e)
                {
                    statusMessage = $"EndpointUrl: '{unpublishNodesMethodData.EndpointUrl}': exception ({e.Message}) while trying to unpublish";
                    Logger.Error($"e, {logPrefix} {statusMessage}");
                    statusResponse.Add(statusMessage);
                    statusCode = HttpStatusCode.InternalServerError;
                }
                finally
                {
                    OpcSessionsListSemaphore.Release();
                }
            }

            // adjust response size
            AdjustResponse(ref statusResponse);

            // build response
            string resultString = JsonConvert.SerializeObject(statusResponse);
            byte[] result = Encoding.UTF8.GetBytes(resultString);
            if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
            {
                Logger.Error($"{logPrefix} Response size is too long");
                Array.Resize(ref result, result.Length > MAX_RESPONSE_PAYLOAD_LENGTH ? MAX_RESPONSE_PAYLOAD_LENGTH : result.Length);
            }
            MethodResponse methodResponse = new MethodResponse(result, (int)statusCode);
            Logger.Information($"{logPrefix} completed with result {statusCode.ToString()}");
            return methodResponse;
        }

        /// <summary>
        /// Handle unpublish all nodes method call.
        /// </summary>
        static async Task<MethodResponse> HandleUnpublishAllNodesMethodAsync(MethodRequest methodRequest, object userContext)
        {
            string logPrefix = "HandleUnpublishAllNodesMethodAsync:";
            Uri endpointUri = null;
            UnpublishAllNodesMethodRequestModel unpublishAllNodesMethodData = null;
            HttpStatusCode statusCode = HttpStatusCode.OK;
            List<string> statusResponse = new List<string>();
            string statusMessage = string.Empty;

            try
            {
                Logger.Debug($"{logPrefix} called");
                unpublishAllNodesMethodData = JsonConvert.DeserializeObject<UnpublishAllNodesMethodRequestModel>(methodRequest.DataAsJson);
                if (unpublishAllNodesMethodData != null && unpublishAllNodesMethodData.EndpointUrl != null)
                {
                    endpointUri = new Uri(unpublishAllNodesMethodData.EndpointUrl);
                }
            }
            catch (UriFormatException e)
            {
                statusMessage = $"Exception ({e.Message}) while parsing EndpointUrl '{unpublishAllNodesMethodData.EndpointUrl}'";
                Logger.Error(e, $"{logPrefix} {statusMessage}");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }
            catch (Exception e)
            {
                statusMessage = $"Exception ({e.Message}) while deserializing message payload";
                Logger.Error(e, $"{logPrefix} {statusMessage}");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }

            if (statusCode == HttpStatusCode.OK)
            {
                // schedule to remove all nodes on all sessions
                try
                {
                    await OpcSessionsListSemaphore.WaitAsync().ConfigureAwait(false);
                    if (ShutdownTokenSource.IsCancellationRequested)
                    {
                        statusMessage = $"Publisher is in shutdown";
                        Logger.Error($"{logPrefix} {statusMessage}");
                        statusResponse.Add(statusMessage);
                        statusCode = HttpStatusCode.Gone;
                    }
                    else
                    {
                        // loop through all sessions
                        foreach (var session in OpcSessions)
                        {
                            bool sessionLocked = false;
                            try
                            {
                                // is an endpoint was given, limit unpublish to this endpoint
                                if (endpointUri != null && !endpointUri.OriginalString.Equals(session.EndpointUrl, StringComparison.InvariantCulture))
                                {
                                    continue;
                                }

                                sessionLocked = await session.LockSessionAsync().ConfigureAwait(false);
                                if (!sessionLocked || ShutdownTokenSource.IsCancellationRequested)
                                {
                                    break;
                                }

                                // loop through all subscriptions of a connected session
                                foreach (var subscription in session.OpcSubscriptions)
                                {
                                    // loop through all monitored items
                                    foreach (var monitoredItem in subscription.OpcMonitoredItems)
                                    {
                                        if (monitoredItem.ConfigType == OpcMonitoredItemConfigurationType.NodeId)
                                        {
                                            await session.RequestMonitorItemRemovalAsync(monitoredItem.ConfigNodeId, null, ShutdownTokenSource.Token, false).ConfigureAwait(false);
                                        }
                                        else
                                        {
                                            await session.RequestMonitorItemRemovalAsync(null, monitoredItem.ConfigExpandedNodeId, ShutdownTokenSource.Token, false).ConfigureAwait(false);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                if (sessionLocked)
                                {
                                    session.ReleaseSession();
                                }
                            }
                        }
                        // build response
                        statusMessage = $"All monitored items in all subscriptions{(endpointUri != null ? $" on endpoint '{endpointUri.OriginalString}'" : " ")} tagged for removal";
                        statusResponse.Add(statusMessage);
                        Logger.Information($"{logPrefix} {statusMessage}");
                    }
                }
                catch (Exception e)
                {
                    statusMessage = $"EndpointUrl: '{unpublishAllNodesMethodData.EndpointUrl}': exception ({e.Message}) while trying to unpublish";
                    Logger.Error(e, $"{logPrefix} {statusMessage}");
                    statusResponse.Add(statusMessage);
                    statusCode = HttpStatusCode.InternalServerError;
                }
                finally
                {
                    OpcSessionsListSemaphore.Release();
                }
            }

            // adjust response size to available package size and keep proper json syntax
            byte[] result;
            int maxIndex = statusResponse.Count();
            string resultString = string.Empty;
            while (true)
            {
                resultString = JsonConvert.SerializeObject(statusResponse.GetRange(0, maxIndex));
                result = Encoding.UTF8.GetBytes(resultString);
                if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
                {
                    maxIndex /= 2;
                    continue;
                }
                else
                {
                    break;
                }
            };
            if (maxIndex != statusResponse.Count())
            {
                statusResponse.RemoveRange(maxIndex, statusResponse.Count() - maxIndex);
                statusResponse.Add("Results have been cropped due to package size limitations.");
            }

            // build response
            resultString = JsonConvert.SerializeObject(statusResponse);
            result = Encoding.UTF8.GetBytes(resultString);
            if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
            {
                Logger.Error($"{logPrefix} Response size is too long");
                Array.Resize(ref result, result.Length > MAX_RESPONSE_PAYLOAD_LENGTH ? MAX_RESPONSE_PAYLOAD_LENGTH : result.Length);
            }
            MethodResponse methodResponse = new MethodResponse(result, (int)statusCode);
            Logger.Information($"{logPrefix} completed with result {statusCode.ToString()}");
            return methodResponse;
        }

        /// <summary>
        /// Handle method call to get all endpoints which published nodes.
        /// </summary>
        static async Task<MethodResponse> HandleGetConfiguredEndpointsMethodAsync(MethodRequest methodRequest, object userContext)
        {
            string logPrefix = "HandleGetConfiguredEndpointsMethodAsync:";
            GetConfiguredEndpointsMethodRequestModel getConfiguredEndpointsMethodRequest = null;
            GetConfiguredEndpointsMethodResponseModel getConfiguredEndpointsMethodResponse = new GetConfiguredEndpointsMethodResponseModel();
            uint actualEndpointsCount = 0;
            uint availableEndpointCount = 0;
            uint nodeConfigVersion = 0;
            uint startIndex = 0;
            List<string> endpointUrls = new List<string>();
            HttpStatusCode statusCode = HttpStatusCode.OK;
            List<string> statusResponse = new List<string>();
            string statusMessage = string.Empty;

            try
            {
                Logger.Debug($"{logPrefix} called");
                getConfiguredEndpointsMethodRequest = JsonConvert.DeserializeObject<GetConfiguredEndpointsMethodRequestModel>(methodRequest.DataAsJson);
            }
            catch (Exception e)
            {
                statusMessage = $"Exception ({e.Message}) while deserializing message payload";
                Logger.Error(e, $"{logPrefix} Exception");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }

            if (statusCode == HttpStatusCode.OK)
            {
                // get the list of all endpoints
                endpointUrls = GetPublisherConfigurationFileEntries(null, false, out nodeConfigVersion).Select(e => e.EndpointUrl.OriginalString).ToList();
                uint endpointsCount = (uint)endpointUrls.Count;

                // validate version
                if (getConfiguredEndpointsMethodRequest?.ContinuationToken != null)
                {
                    uint requestedNodeConfigVersion = (uint)(getConfiguredEndpointsMethodRequest.ContinuationToken >> 32);
                    if (nodeConfigVersion != requestedNodeConfigVersion)
                    {
                        statusMessage = $"The node configuration has changed between calls. Requested version: {requestedNodeConfigVersion:X8}, Current version '{nodeConfigVersion:X8}'";
                        Logger.Information($"{logPrefix} {statusMessage}");
                        statusResponse.Add(statusMessage);
                        statusCode = HttpStatusCode.Gone;
                    }
                    startIndex = (uint)(getConfiguredEndpointsMethodRequest.ContinuationToken & 0x0FFFFFFFFL);
                }

                if (statusCode == HttpStatusCode.OK)
                {
                    // set count
                    uint requestedEndpointsCount = endpointsCount - startIndex;
                    availableEndpointCount = endpointsCount - startIndex;
                    actualEndpointsCount = Math.Min(requestedEndpointsCount, availableEndpointCount);

                    // generate response
                    string endpointsString;
                    byte[] endpointsByteArray;
                    while (true)
                    {
                        endpointsString = JsonConvert.SerializeObject(endpointUrls.GetRange((int)startIndex, (int)actualEndpointsCount));
                        endpointsByteArray = Encoding.UTF8.GetBytes(endpointsString);
                        if (endpointsByteArray.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
                        {
                            actualEndpointsCount /= 2;
                            continue;
                        }
                        else
                        {
                            break;
                        }
                    };
                }
            }

            // build response
            byte[] result = null;
            string resultString = null;
            if (statusCode == HttpStatusCode.OK)
            {
                getConfiguredEndpointsMethodResponse.ContinuationToken = null;
                if (actualEndpointsCount < availableEndpointCount)
                {
                    getConfiguredEndpointsMethodResponse.ContinuationToken = ((ulong)nodeConfigVersion << 32) | actualEndpointsCount + startIndex;
                }
                getConfiguredEndpointsMethodResponse.Endpoints.AddRange(endpointUrls.GetRange((int)startIndex, (int)actualEndpointsCount).Select(e => new ConfiguredEndpointModel(e)).ToList());
                resultString = JsonConvert.SerializeObject(getConfiguredEndpointsMethodResponse);
                result = Encoding.UTF8.GetBytes(resultString);
                Logger.Information($"{logPrefix} returning {actualEndpointsCount} endpoint(s) (node config version: {nodeConfigVersion:X8})!");
            }
            else
            {
                resultString = JsonConvert.SerializeObject(statusResponse);
            }

            result = Encoding.UTF8.GetBytes(resultString);
            if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
            {
                Logger.Error($"{logPrefix} Response size is too long");
                Array.Resize(ref result, result.Length > MAX_RESPONSE_PAYLOAD_LENGTH ? MAX_RESPONSE_PAYLOAD_LENGTH : result.Length);
            }
            MethodResponse methodResponse = new MethodResponse(result, (int)statusCode);
            Logger.Information($"{logPrefix} completed with result {statusCode.ToString()}");
            return methodResponse;
        }

        /// <summary>
        /// Handle method call to get list of configured nodes on a specific endpoint.
        /// </summary>
        static async Task<MethodResponse> HandleGetConfiguredNodesOnEndpointMethodAsync(MethodRequest methodRequest, object userContext)
        {
            string logPrefix = "HandleGetConfiguredNodesOnEndpointMethodAsync:";
            Uri endpointUri = null;
            GetConfiguredNodesOnEndpointMethodRequestModel getConfiguredNodesOnEndpointMethodRequest = null;
            uint nodeConfigVersion = 0;
            GetConfiguredNodesOnEndpointMethodResponseModel getConfiguredNodesOnEndpointMethodResponse = new GetConfiguredNodesOnEndpointMethodResponseModel();
            uint actualNodeCount = 0;
            uint availableNodeCount = 0;
            uint requestedNodeCount = 0;
            List<OpcNodeOnEndpointModel> opcNodes = new List<OpcNodeOnEndpointModel>();
            uint startIndex = 0;
            HttpStatusCode statusCode = HttpStatusCode.OK;
            List<string> statusResponse = new List<string>();
            string statusMessage = string.Empty;

            try
            {
                Logger.Debug($"{logPrefix} called");
                getConfiguredNodesOnEndpointMethodRequest = JsonConvert.DeserializeObject<GetConfiguredNodesOnEndpointMethodRequestModel>(methodRequest.DataAsJson);
                endpointUri = new Uri(getConfiguredNodesOnEndpointMethodRequest.EndpointUrl);
            }
            catch (UriFormatException e)
            {
                statusMessage = $"Exception ({e.Message}) while parsing EndpointUrl '{getConfiguredNodesOnEndpointMethodRequest.EndpointUrl}'";
                Logger.Error(e, $"{logPrefix} {statusMessage}");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }
            catch (Exception e)
            {
                statusMessage = $"Exception ({e.Message}) while deserializing message payload";
                Logger.Error(e, $"{logPrefix} Exception");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }

            if (statusCode == HttpStatusCode.OK)
            {
                // get the list of published nodes for the endpoint
                List<PublisherConfigurationFileEntryModel> configFileEntries = GetPublisherConfigurationFileEntries(endpointUri.OriginalString, false, out nodeConfigVersion);

                // return if there are no nodes configured for this endpoint
                if (configFileEntries.Count == 0)
                {
                    statusMessage = $"There are no nodes configured for endpoint '{endpointUri.OriginalString}'";
                    Logger.Information($"{logPrefix} {statusMessage}");
                    statusResponse.Add(statusMessage);
                    statusCode = HttpStatusCode.OK;
                }
                else
                {
                    foreach (var configFileEntry in configFileEntries)
                    {
                        opcNodes.AddRange(configFileEntry.OpcNodes);
                    }
                    uint configuredNodesOnEndpointCount = (uint)opcNodes.Count();

                    // validate version
                    startIndex = 0;
                    if (getConfiguredNodesOnEndpointMethodRequest.ContinuationToken != null)
                    {
                        uint requestedNodeConfigVersion = (uint)(getConfiguredNodesOnEndpointMethodRequest.ContinuationToken >> 32);
                        if (nodeConfigVersion != requestedNodeConfigVersion)
                        {
                            statusMessage = $"The node configuration has changed between calls. Requested version: {requestedNodeConfigVersion:X8}, Current version '{nodeConfigVersion:X8}'!";
                            Logger.Information($"{logPrefix} {statusMessage}");
                            statusResponse.Add(statusMessage);
                            statusCode = HttpStatusCode.Gone;
                        }
                        startIndex = (uint)(getConfiguredNodesOnEndpointMethodRequest.ContinuationToken & 0x0FFFFFFFFL);
                    }

                    if (statusCode == HttpStatusCode.OK)
                    {
                        // set count
                        requestedNodeCount = configuredNodesOnEndpointCount - startIndex;
                        availableNodeCount = configuredNodesOnEndpointCount - startIndex;
                        actualNodeCount = Math.Min(requestedNodeCount, availableNodeCount);

                        // generate response
                        string publishedNodesString;
                        byte[] publishedNodesByteArray;
                        while (true)
                        {
                            publishedNodesString = JsonConvert.SerializeObject(opcNodes.GetRange((int)startIndex, (int)actualNodeCount));
                            publishedNodesByteArray = Encoding.UTF8.GetBytes(publishedNodesString);
                            if (publishedNodesByteArray.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
                            {
                                actualNodeCount /= 2;
                                continue;
                            }
                            else
                            {
                                break;
                            }
                        };
                    }
                }
            }

            // build response
            byte[] result = null;
            string resultString = null;
            if (statusCode == HttpStatusCode.OK)
            {
                getConfiguredNodesOnEndpointMethodResponse.ContinuationToken = null;
                if (actualNodeCount < availableNodeCount)
                {
                    getConfiguredNodesOnEndpointMethodResponse.ContinuationToken = (ulong)nodeConfigVersion << 32 | actualNodeCount + startIndex;
                }
                getConfiguredNodesOnEndpointMethodResponse.OpcNodes.AddRange(opcNodes.GetRange((int)startIndex, (int)actualNodeCount).Select(n => new OpcNodeOnEndpointModel(n.Id)
                {
                    OpcPublishingInterval = n.OpcPublishingInterval,
                    OpcSamplingInterval = n.OpcSamplingInterval,
                    DisplayName = n.DisplayName
                }).ToList());
                getConfiguredNodesOnEndpointMethodResponse.EndpointUrl = endpointUri.OriginalString;
                resultString = JsonConvert.SerializeObject(getConfiguredNodesOnEndpointMethodResponse);
                Logger.Information($"{logPrefix} Success returning {actualNodeCount} node(s) of {availableNodeCount} (start: {startIndex}) (node config version: {nodeConfigVersion:X8})!");
            }
            else
            {
                resultString = JsonConvert.SerializeObject(statusResponse);
            }
            result = Encoding.UTF8.GetBytes(resultString);
            if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
            {
                Logger.Error($"{logPrefix} Response size is too long");
                Array.Resize(ref result, result.Length > MAX_RESPONSE_PAYLOAD_LENGTH ? MAX_RESPONSE_PAYLOAD_LENGTH : result.Length);
            }
            MethodResponse methodResponse = new MethodResponse(result, (int)statusCode);
            Logger.Information($"{logPrefix} completed with result {statusCode.ToString()}");
            return methodResponse;
        }

        /// <summary>
        /// Handle method call to get diagnostic information.
        /// </summary>
        static async Task<MethodResponse> HandleGetDiagnosticInfoMethodAsync(MethodRequest methodRequest, object userContext)
        {
            string logPrefix = "HandleGetDiagnosticInfoMethodAsync:";
            HttpStatusCode statusCode = HttpStatusCode.OK;
            List<string> statusResponse = new List<string>();
            string statusMessage = string.Empty;

            // get the diagnostic info
            DiagnosticInfoMethodResponseModel diagnosticInfo = new DiagnosticInfoMethodResponseModel();
            try
            {
                diagnosticInfo = GetDiagnosticInfo();
            }
            catch (Exception e)
            {
                statusMessage = $"Exception ({e.Message}) while reading diagnostic info";
                Logger.Error(e, $"{logPrefix} Exception");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }

            // build response
            byte[] result = null;
            string resultString = null;
            if (statusCode == HttpStatusCode.OK)
            {
                resultString = JsonConvert.SerializeObject(diagnosticInfo);
            }
            else
            {
                resultString = JsonConvert.SerializeObject(statusResponse);
            }
            result = Encoding.UTF8.GetBytes(resultString);
            if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
            {
                Logger.Error($"{logPrefix} Response size is too long");
                Array.Resize(ref result, result.Length > MAX_RESPONSE_PAYLOAD_LENGTH ? MAX_RESPONSE_PAYLOAD_LENGTH : result.Length);
            }
            MethodResponse methodResponse = new MethodResponse(result, (int)statusCode);
            Logger.Information($"{logPrefix} completed with result {statusCode.ToString()}");
            return methodResponse;
        }

        /// <summary>
        /// Handle method call to get log information.
        /// </summary>
        static async Task<MethodResponse> HandleGetDiagnosticLogMethodAsync(MethodRequest methodRequest, object userContext)
        {
            string logPrefix = "HandleGetDiagnosticLogMethodAsync:";
            HttpStatusCode statusCode = HttpStatusCode.OK;
            List<string> statusResponse = new List<string>();
            string statusMessage = string.Empty;

            // get the diagnostic info
            DiagnosticLogMethodResponseModel diagnosticLogMethodResponseModel = new DiagnosticLogMethodResponseModel();
            try
            {
                diagnosticLogMethodResponseModel = await GetDiagnosticLogAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                statusMessage = $"Exception ({e.Message}) while reading diagnostic log";
                Logger.Error(e, $"{logPrefix} Exception");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }

            // build response
            byte[] result = null;
            string resultString = null;
            if (statusCode == HttpStatusCode.OK)
            {
                resultString = JsonConvert.SerializeObject(diagnosticLogMethodResponseModel);
            }
            else
            {
                resultString = JsonConvert.SerializeObject(statusResponse);
            }
            result = Encoding.UTF8.GetBytes(resultString);
            if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
            {
                Logger.Error($"{logPrefix} Response size is too long");
                Array.Resize(ref result, result.Length > MAX_RESPONSE_PAYLOAD_LENGTH ? MAX_RESPONSE_PAYLOAD_LENGTH : result.Length);
            }
            MethodResponse methodResponse = new MethodResponse(result, (int)statusCode);
            Logger.Information($"{logPrefix} completed with result {statusCode.ToString()}");
            return methodResponse;
        }

        /// <summary>
        /// Handle method call to get log information.
        /// </summary>
        static async Task<MethodResponse> HandleGetDiagnosticStartupLogMethodAsync(MethodRequest methodRequest, object userContext)
        {
            string logPrefix = "HandleGetDiagnosticStartupLogMethodAsync:";
            HttpStatusCode statusCode = HttpStatusCode.OK;
            List<string> statusResponse = new List<string>();
            string statusMessage = string.Empty;

            // get the diagnostic info
            DiagnosticLogMethodResponseModel diagnosticLogMethodResponseModel = new DiagnosticLogMethodResponseModel();
            try
            {
                diagnosticLogMethodResponseModel = await GetDiagnosticStartupLogAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                statusMessage = $"Exception ({e.Message}) while reading diagnostic startup log";
                Logger.Error(e, $"{logPrefix} Exception");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }

            // build response
            byte[] result = null;
            string resultString = null;
            if (statusCode == HttpStatusCode.OK)
            {
                resultString = JsonConvert.SerializeObject(diagnosticLogMethodResponseModel);
            }
            else
            {
                resultString = JsonConvert.SerializeObject(statusResponse);
            }
            result = Encoding.UTF8.GetBytes(resultString);
            if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
            {
                Logger.Error($"{logPrefix} Response size is too long");
                Array.Resize(ref result, result.Length > MAX_RESPONSE_PAYLOAD_LENGTH ? MAX_RESPONSE_PAYLOAD_LENGTH : result.Length);
            }
            MethodResponse methodResponse = new MethodResponse(result, (int)statusCode);
            Logger.Information($"{logPrefix} completed with result {statusCode.ToString()}");
            return methodResponse;
        }

        /// <summary>
        /// Handle method call to get log information.
        /// </summary>
        static async Task<MethodResponse> HandleExitApplicationMethodAsync(MethodRequest methodRequest, object userContext)
        {
            string logPrefix = "HandleExitApplicationMethodAsync:";
            HttpStatusCode statusCode = HttpStatusCode.OK;
            List<string> statusResponse = new List<string>();
            string statusMessage = string.Empty;

            ExitApplicationMethodRequestModel exitApplicationMethodRequest = null;
            try
            {
                exitApplicationMethodRequest = JsonConvert.DeserializeObject<ExitApplicationMethodRequestModel>(methodRequest.DataAsJson);
            }
            catch (Exception e)
            {
                statusMessage = $"Exception ({e.Message}) while deserializing message payload";
                Logger.Error(e, $"{logPrefix} Exception");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }

            if (statusCode == HttpStatusCode.OK)
            {
                // get the parameter
                ExitApplicationMethodRequestModel exitApplication = new ExitApplicationMethodRequestModel();
                try
                {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    Task.Run(async () => await ExitApplicationAsync(exitApplicationMethodRequest.SecondsTillExit).ConfigureAwait(false));
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    statusMessage = $"Module will exit in {exitApplicationMethodRequest.SecondsTillExit} seconds";
                    Logger.Information($"{logPrefix} {statusMessage}");
                    statusResponse.Add(statusMessage);
                }
                catch (Exception e)
                {
                    statusMessage = $"Exception ({e.Message}) while scheduling application exit";
                    Logger.Error(e, $"{logPrefix} Exception");
                    statusResponse.Add(statusMessage);
                    statusCode = HttpStatusCode.InternalServerError;
                }
            }

            // build response
            byte[] result = null;
            string resultString = null;
            resultString = JsonConvert.SerializeObject(statusResponse);
            result = Encoding.UTF8.GetBytes(resultString);
            if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
            {
                Logger.Error($"{logPrefix} Response size is too long");
                Array.Resize(ref result, result.Length > MAX_RESPONSE_PAYLOAD_LENGTH ? MAX_RESPONSE_PAYLOAD_LENGTH : result.Length);
            }
            MethodResponse methodResponse = new MethodResponse(result, (int)statusCode);
            Logger.Information($"{logPrefix} completed with result {statusCode.ToString()}");
            return methodResponse;
        }

        /// <summary>
        /// Handle method call to get application information.
        /// </summary>
        static async Task<MethodResponse> HandleGetInfoMethodAsync(MethodRequest methodRequest, object userContext)
        {
            string logPrefix = "HandleGetInfoMethodAsync:";
            GetInfoMethodResponseModel getInfoMethodResponseModel = new GetInfoMethodResponseModel();
            HttpStatusCode statusCode = HttpStatusCode.OK;
            List<string> statusResponse = new List<string>();
            string statusMessage = string.Empty;

            try
            {
                // get the info
                getInfoMethodResponseModel.VersionMajor = Assembly.GetExecutingAssembly().GetName().Version.Major;
                getInfoMethodResponseModel.VersionMinor = Assembly.GetExecutingAssembly().GetName().Version.Minor;
                getInfoMethodResponseModel.VersionPatch = Assembly.GetExecutingAssembly().GetName().Version.Build;
                getInfoMethodResponseModel.SemanticVersion = (Attribute.GetCustomAttribute(Assembly.GetEntryAssembly(), typeof(AssemblyInformationalVersionAttribute)) as AssemblyInformationalVersionAttribute).InformationalVersion;
                getInfoMethodResponseModel.InformationalVersion = (Attribute.GetCustomAttribute(Assembly.GetEntryAssembly(), typeof(AssemblyInformationalVersionAttribute)) as AssemblyInformationalVersionAttribute).InformationalVersion;
                getInfoMethodResponseModel.OS = RuntimeInformation.OSDescription;
                getInfoMethodResponseModel.OSArchitecture = RuntimeInformation.OSArchitecture;
                getInfoMethodResponseModel.FrameworkDescription = RuntimeInformation.FrameworkDescription;
            }
            catch (Exception e)
            {
                statusMessage = $"Exception ({e.Message}) while retrieving info";
                Logger.Error(e, $"{logPrefix} Exception");
                statusResponse.Add(statusMessage);
                statusCode = HttpStatusCode.InternalServerError;
            }

            // build response
            byte[] result = null;
            string resultString = null;
            if (statusCode == HttpStatusCode.OK)
            {
                resultString = JsonConvert.SerializeObject(getInfoMethodResponseModel);
            }
            else
            {
                resultString = JsonConvert.SerializeObject(statusResponse);
            }
            result = Encoding.UTF8.GetBytes(resultString);
            if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
            {
                Logger.Error($"{logPrefix} Response size is too long");
                Array.Resize(ref result, result.Length > MAX_RESPONSE_PAYLOAD_LENGTH ? MAX_RESPONSE_PAYLOAD_LENGTH : result.Length);
            }
            MethodResponse methodResponse = new MethodResponse(result, (int)statusCode);
            Logger.Information($"{logPrefix} completed with result {statusCode.ToString()}");
            return methodResponse;
        }

        /// </summary>
        /// Method that is called for any unimplemented call. Just returns that info to the caller
        /// </summary>
        private async Task<MethodResponse> DefaultMethodHandlerAsync(MethodRequest methodRequest, object userContext)
        {
            Logger.Information($"Received direct method call for {methodRequest.Name}, which is not implemented");
            string response = $"Method {methodRequest.Name} successfully received, but this method is not implemented";

            string resultString = JsonConvert.SerializeObject(response);
            byte[] result = Encoding.UTF8.GetBytes(resultString);
            MethodResponse methodResponse = new MethodResponse(result, (int)HttpStatusCode.OK);
            return methodResponse;
        }

        /// <summary>
        /// Initializes internal message processing.
        /// </summary>
        public async Task<bool> InitMessageProcessingAsync()
        {
            try
            {
                // show config
                Logger.Information($"Message processing and hub communication configured with a send interval of {DefaultSendIntervalSeconds} sec and a message buffer size of {HubMessageSize} bytes.");

                // create the queue for monitored items
                _monitoredItemsDataQueue = new BlockingCollection<MessageData>(MonitoredItemsQueueCapacity);

                // start up task to send telemetry to IoTHub
                _monitoredItemsProcessorTask = null;

                Logger.Information("Creating task process and batch monitored item data updates...");
                _monitoredItemsProcessorTask = Task.Run(async () => await MonitoredItemsProcessorAsync(_shutdownToken).ConfigureAwait(false), _shutdownToken);
                return true;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failure initializing message processing.");
                return false;
            }
        }

        /// <summary>
        /// Shuts down the IoTHub communication.
        /// </summary>
        public async Task ShutdownAsync()
        {
            // send cancellation token and wait for last IoT Hub message to be sent.
            try
            {
                await _monitoredItemsProcessorTask.ConfigureAwait(false);

                if (_iotHubClient != null)
                {
                    await _iotHubClient.CloseAsync().ConfigureAwait(false);
                    _iotHubClient = null;
                }
                if (_edgeHubClient != null)
                {
                    await _edgeHubClient.CloseAsync().ConfigureAwait(false);
                    _edgeHubClient = null;
                }
                _monitoredItemsDataQueue = null;
                _monitoredItemsProcessorTask = null;
            }
            catch (Exception e)
            {
                Logger.Error(e, "Failure while shutting down hub messaging.");
            }
        }

        /// <summary>
        /// Enqueue a message for sending to IoTHub.
        /// </summary>
        internal static void Enqueue(MessageData json)
        {
            // Try to add the message.
            Interlocked.Increment(ref _enqueueCount);
            if (_monitoredItemsDataQueue.TryAdd(json) == false)
            {
                Interlocked.Increment(ref _enqueueFailureCount);
                if (_enqueueFailureCount % 10000 == 0)
                {
                    Logger.Information($"The internal monitored item message queue is above its capacity of {_monitoredItemsDataQueue.BoundedCapacity}. We have already lost {_enqueueFailureCount} monitored item notifications:(");
                }
            }
        }

        /// <summary>
        /// Creates a JSON message to be sent to IoTHub, based on the telemetry configuration for the endpoint.
        /// </summary>
        private async Task<string> CreateJsonMessageAsync(MessageData messageData)
        {
            try
            {
                // get telemetry configration
                EndpointTelemetryConfiguration telemetryConfiguration = GetEndpointTelemetryConfiguration(messageData.EndpointUrl);

                // currently the pattern processing is done in MonitoredItemNotificationHandler of OpcSession.cs. in case of perf issues
                // it can be also done here, the risk is then to lose messages in the communication queue. if you enable it here, disable it in OpcSession.cs
                // messageData.ApplyPatterns(telemetryConfiguration);

                // build the JSON message
                StringBuilder _jsonStringBuilder = new StringBuilder();
                StringWriter _jsonStringWriter = new StringWriter(_jsonStringBuilder);
                using (JsonWriter _jsonWriter = new JsonTextWriter(_jsonStringWriter))
                {
                    await _jsonWriter.WriteStartObjectAsync().ConfigureAwait(false);
                    string telemetryValue = string.Empty;

                    // process EndpointUrl
                    if ((bool)telemetryConfiguration.EndpointUrl.Publish)
                    {
                        await _jsonWriter.WritePropertyNameAsync(telemetryConfiguration.EndpointUrl.Name).ConfigureAwait(false);
                        await _jsonWriter.WriteValueAsync(messageData.EndpointUrl).ConfigureAwait(false);
                    }

                    // process NodeId
                    if (!string.IsNullOrEmpty(messageData.NodeId))
                    {
                        await _jsonWriter.WritePropertyNameAsync(telemetryConfiguration.NodeId.Name).ConfigureAwait(false);
                        await _jsonWriter.WriteValueAsync(messageData.NodeId).ConfigureAwait(false);
                    }

                    // process MonitoredItem object properties
                    if (!string.IsNullOrEmpty(messageData.ApplicationUri) || !string.IsNullOrEmpty(messageData.DisplayName))
                    {
                        if (!(bool)telemetryConfiguration.MonitoredItem.Flat)
                        {
                            await _jsonWriter.WritePropertyNameAsync("MonitoredItem").ConfigureAwait(false);
                            await _jsonWriter.WriteStartObjectAsync().ConfigureAwait(false);
                        }

                        // process ApplicationUri
                        if (!string.IsNullOrEmpty(messageData.ApplicationUri))
                        {
                            await _jsonWriter.WritePropertyNameAsync(telemetryConfiguration.MonitoredItem.ApplicationUri.Name).ConfigureAwait(false);
                            await _jsonWriter.WriteValueAsync(messageData.ApplicationUri).ConfigureAwait(false);
                        }

                        // process DisplayName
                        if (!string.IsNullOrEmpty(messageData.DisplayName))
                        {
                            await _jsonWriter.WritePropertyNameAsync(telemetryConfiguration.MonitoredItem.DisplayName.Name).ConfigureAwait(false);
                            await _jsonWriter.WriteValueAsync(messageData.DisplayName).ConfigureAwait(false);
                        }

                        if (!(bool)telemetryConfiguration.MonitoredItem.Flat)
                        {
                            await _jsonWriter.WriteEndObjectAsync().ConfigureAwait(false);
                        }
                    }

                    // process Value object properties
                    if (!string.IsNullOrEmpty(messageData.Value) || !string.IsNullOrEmpty(messageData.SourceTimestamp) ||
                       messageData.StatusCode != null || !string.IsNullOrEmpty(messageData.Status))
                    {
                        if (!(bool)telemetryConfiguration.Value.Flat)
                        {
                            await _jsonWriter.WritePropertyNameAsync("Value").ConfigureAwait(false);
                            await _jsonWriter.WriteStartObjectAsync().ConfigureAwait(false);
                        }

                        // process Value
                        if (!string.IsNullOrEmpty(messageData.Value))
                        {
                            await _jsonWriter.WritePropertyNameAsync(telemetryConfiguration.Value.Value.Name).ConfigureAwait(false);
                            if (messageData.PreserveValueQuotes)
                            {
                                await _jsonWriter.WriteValueAsync(messageData.Value).ConfigureAwait(false);
                            }
                            else
                            {
                                await _jsonWriter.WriteRawValueAsync(messageData.Value).ConfigureAwait(false);
                            }
                        }

                        // process SourceTimestamp
                        if (!string.IsNullOrEmpty(messageData.SourceTimestamp))
                        {
                            await _jsonWriter.WritePropertyNameAsync(telemetryConfiguration.Value.SourceTimestamp.Name).ConfigureAwait(false);
                            await _jsonWriter.WriteValueAsync(messageData.SourceTimestamp).ConfigureAwait(false);
                        }

                        // process StatusCode
                        if (messageData.StatusCode != null)
                        {
                            await _jsonWriter.WritePropertyNameAsync(telemetryConfiguration.Value.StatusCode.Name).ConfigureAwait(false);
                            await _jsonWriter.WriteValueAsync(messageData.StatusCode).ConfigureAwait(false);
                        }

                        // process Status
                        if (!string.IsNullOrEmpty(messageData.Status))
                        {
                            await _jsonWriter.WritePropertyNameAsync(telemetryConfiguration.Value.Status.Name).ConfigureAwait(false);
                            await _jsonWriter.WriteValueAsync(messageData.Status).ConfigureAwait(false);
                        }

                        if (!(bool)telemetryConfiguration.Value.Flat)
                        {
                            await _jsonWriter.WriteEndObjectAsync().ConfigureAwait(false);
                        }
                    }
                    await _jsonWriter.WriteEndObjectAsync().ConfigureAwait(false);
                    await _jsonWriter.FlushAsync().ConfigureAwait(false);
                }
                return _jsonStringBuilder.ToString();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Generation of JSON message failed.");
            }
            return string.Empty;
        }

        /// <summary>
        /// Creates a JSON message to be sent to IoTCentral.
        /// </summary>
        private async Task<string> CreateIotCentralJsonMessageAsync(MessageData messageData)
        {
            try
            {
                // build the JSON message for IoTCentral
                StringBuilder _jsonStringBuilder = new StringBuilder();
                StringWriter _jsonStringWriter = new StringWriter(_jsonStringBuilder);
                using (JsonWriter _jsonWriter = new JsonTextWriter(_jsonStringWriter))
                {
                    await _jsonWriter.WriteStartObjectAsync().ConfigureAwait(false);
                    await _jsonWriter.WritePropertyNameAsync(messageData.DisplayName).ConfigureAwait(false);
                    await _jsonWriter.WriteValueAsync(messageData.Value).ConfigureAwait(false);
                    await _jsonWriter.WriteEndObjectAsync().ConfigureAwait(false);
                    await _jsonWriter.FlushAsync().ConfigureAwait(false);
                }
                return _jsonStringBuilder.ToString();
            }
            catch (Exception e)
            {
                Logger.Error(e, "Generation of IoTCentral JSON message failed.");
            }
            return string.Empty;
        }

        /// <summary>
        /// Dequeue monitored item notification messages, batch them for send (if needed) and send them to IoTHub.
        /// </summary>
        protected async Task MonitoredItemsProcessorAsync(CancellationToken ct)
        {
            uint jsonSquareBracketLength = 2;
            Message tempMsg = new Message();
            // the system properties are MessageId (max 128 byte), Sequence number (ulong), ExpiryTime (DateTime) and more. ideally we get that from the client.
            int systemPropertyLength = 128 + sizeof(ulong) + tempMsg.ExpiryTimeUtc.ToString(CultureInfo.InvariantCulture).Length;
            int applicationPropertyLength = Encoding.UTF8.GetByteCount($"iothub-content-type={CONTENT_TYPE_OPCUAJSON}") + Encoding.UTF8.GetByteCount($"iothub-content-encoding={CONTENT_ENCODING_UTF8}");
            // if batching is requested the buffer will have the requested size, otherwise we reserve the max size
            uint hubMessageBufferSize = (HubMessageSize > 0 ? HubMessageSize : HubMessageSizeMax) - (uint)systemPropertyLength - jsonSquareBracketLength - (uint)applicationPropertyLength;
            byte[] hubMessageBuffer = new byte[hubMessageBufferSize];
            MemoryStream hubMessage = new MemoryStream(hubMessageBuffer);
            DateTime nextSendTime = DateTime.UtcNow + TimeSpan.FromSeconds(DefaultSendIntervalSeconds);
            double millisecondsTillNextSend = nextSendTime.Subtract(DateTime.UtcNow).TotalMilliseconds;
            bool singleMessageSend = DefaultSendIntervalSeconds == 0 && HubMessageSize == 0;

            using (hubMessage)
            {
                try
                {
                    string jsonMessage = string.Empty;
                    MessageData messageData = new MessageData();
                    bool needToBufferMessage = false;
                    int jsonMessageSize = 0;

                    hubMessage.Position = 0;
                    hubMessage.SetLength(0);
                    if (!singleMessageSend)
                    {
                        hubMessage.Write(Encoding.UTF8.GetBytes("["), 0, 1);
                    }
                    while (true)
                    {
                        // sanity check the send interval, compute the timeout and get the next monitored item message
                        if (DefaultSendIntervalSeconds > 0)
                        {
                            millisecondsTillNextSend = nextSendTime.Subtract(DateTime.UtcNow).TotalMilliseconds;
                            if (millisecondsTillNextSend < 0)
                            {
                                MissedSendIntervalCount++;
                                // do not wait if we missed the send interval
                                millisecondsTillNextSend = 0;
                            }
                        }
                        else
                        {
                            // if we are in shutdown do not wait, else wait infinite if send interval is not set
                            millisecondsTillNextSend = ct.IsCancellationRequested ? 0 : Timeout.Infinite;
                        }
                        bool gotItem = _monitoredItemsDataQueue.TryTake(out messageData, (int)millisecondsTillNextSend, ct);

                        // the two commandline parameter --ms (message size) and --si (send interval) control when data is sent to IoTHub/EdgeHub
                        // pls see detailed comments on performance and memory consumption at https://github.com/Azure/iot-edge-opc-publisher

                        // check if we got an item or if we hit the timeout or got canceled
                        if (gotItem)
                        {
                            if (IotCentralMode)
                            {
                                // for IoTCentral we send simple key/value pairs. key is the DisplayName, value the value.
                                jsonMessage = await CreateIotCentralJsonMessageAsync(messageData).ConfigureAwait(false);
                            }
                            else
                            {
                                // create a JSON message from the messageData object
                                jsonMessage = await CreateJsonMessageAsync(messageData).ConfigureAwait(false);
                            }

                            NumberOfEvents++;
                            jsonMessageSize = Encoding.UTF8.GetByteCount(jsonMessage.ToString(CultureInfo.InvariantCulture));

                            // sanity check that the user has set a large enough messages size
                            if ((HubMessageSize > 0 && jsonMessageSize > HubMessageSize ) || (HubMessageSize == 0 && jsonMessageSize > hubMessageBufferSize))
                            {
                                Logger.Error($"There is a telemetry message (size: {jsonMessageSize}), which will not fit into an hub message (max size: {hubMessageBufferSize}].");
                                Logger.Error($"Please check your hub message size settings. The telemetry message will be discarded silently. Sorry:(");
                                TooLargeCount++;
                                continue;
                            }

                            // if batching is requested or we need to send at intervals, batch it otherwise send it right away
                            needToBufferMessage = false;
                            if (HubMessageSize > 0 || (HubMessageSize == 0 && DefaultSendIntervalSeconds > 0))
                            {
                                // if there is still space to batch, do it. otherwise send the buffer and flag the message for later buffering
                                if (hubMessage.Position + jsonMessageSize + 1 <= hubMessage.Capacity)
                                {
                                    // add the message and a comma to the buffer
                                    hubMessage.Write(Encoding.UTF8.GetBytes(jsonMessage.ToString(CultureInfo.InvariantCulture)), 0, jsonMessageSize);
                                    hubMessage.Write(Encoding.UTF8.GetBytes(","), 0, 1);
                                    Logger.Debug($"Added new message with size {jsonMessageSize} to hub message (size is now {(hubMessage.Position - 1)}).");
                                    continue;
                                }
                                else
                                {
                                    needToBufferMessage = true;
                                }
                            }
                        }
                        else
                        {
                            // if we got no message, we either reached the interval or we are in shutdown and have processed all messages
                            if (ct.IsCancellationRequested)
                            {
                                Logger.Information($"Cancellation requested.");
                                _monitoredItemsDataQueue.CompleteAdding();
                                _monitoredItemsDataQueue.Dispose();
                                break;
                            }
                        }

                        // the batching is completed or we reached the send interval or got a cancelation request
                        try
                        {
                            Microsoft.Azure.Devices.Client.Message encodedhubMessage = null;

                            // if we reached the send interval, but have nothing to send (only the opening square bracket is there), we continue
                            if (!gotItem && hubMessage.Position == 1)
                            {
                                nextSendTime += TimeSpan.FromSeconds(DefaultSendIntervalSeconds);
                                hubMessage.Position = 0;
                                hubMessage.SetLength(0);
                                if (!singleMessageSend)
                                {
                                    hubMessage.Write(Encoding.UTF8.GetBytes("["), 0, 1);
                                }
                                continue;
                            }

                            // if there is no batching and no send interval configured, we send the JSON message we just got, otherwise we send the buffer
                            if (singleMessageSend)
                            {
                                // create the message without brackets
                                encodedhubMessage = new Message(Encoding.UTF8.GetBytes(jsonMessage.ToString(CultureInfo.InvariantCulture)));
                            }
                            else
                            {
                                // remove the trailing comma and add a closing square bracket
                                hubMessage.SetLength(hubMessage.Length - 1);
                                hubMessage.Write(Encoding.UTF8.GetBytes("]"), 0, 1);
                                encodedhubMessage = new Message(hubMessage.ToArray());
                            }
                            if (_iotHubClient != null || _edgeHubClient != null)
                            {
                                encodedhubMessage.ContentType = CONTENT_TYPE_OPCUAJSON;
                                encodedhubMessage.ContentEncoding = CONTENT_ENCODING_UTF8;

                                nextSendTime += TimeSpan.FromSeconds(DefaultSendIntervalSeconds);
                                try
                                {
                                    SentBytes += encodedhubMessage.GetBytes().Length;
                                    if (_iotHubClient != null)
                                    {
                                        await _iotHubClient.SendEventAsync(encodedhubMessage).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        await _edgeHubClient.SendEventAsync(encodedhubMessage).ConfigureAwait(false);
                                    }
                                    SentMessages++;
                                    SentLastTime = DateTime.UtcNow;
                                    Logger.Debug($"Sending {encodedhubMessage.BodyStream.Length} bytes to hub.");
                                }
                                catch
                                {
                                    FailedMessages++;
                                }

                                // reset the messaage
                                hubMessage.Position = 0;
                                hubMessage.SetLength(0);
                                if (!singleMessageSend)
                                {
                                    hubMessage.Write(Encoding.UTF8.GetBytes("["), 0, 1);
                                }

                                // if we had not yet buffered the last message because there was not enough space, buffer it now
                                if (needToBufferMessage)
                                {
                                    // add the message and a comma to the buffer
                                    hubMessage.Write(Encoding.UTF8.GetBytes(jsonMessage.ToString(CultureInfo.InvariantCulture)), 0, jsonMessageSize);
                                    hubMessage.Write(Encoding.UTF8.GetBytes(","), 0, 1);
                                }
                            }
                            else
                            {
                                Logger.Information("No hub client available. Dropping messages...");
                            }
                        }
                        catch (Exception e)
                        {
                            Logger.Error(e, "Exception while sending message to hub. Dropping message...");
                        }
                    }
                }
                catch (Exception e)
                {
                    if (!(e is OperationCanceledException))
                    {

                        Logger.Error(e, "Error while processing monitored item messages.");
                    }
                }
            }
        }

        /// <summary>
        /// Exit the application.
        /// </summary>
        static async Task ExitApplicationAsync(int secondsTillExit)
        {
            string logPrefix = "ExitApplicationAsync:";

            // sanity check parameter
            if (secondsTillExit <= 0)
            {
                Logger.Information($"{logPrefix} Time to exit adjusted to {secondsTillExit} seconds...");
                secondsTillExit = 5;
            }

            // wait and exit
            while (secondsTillExit > 0)
            {
                Logger.Information($"{logPrefix} Exiting in {secondsTillExit} seconds...");
                secondsTillExit--;
                await Task.Delay(1000).ConfigureAwait(false);
            }

            // exit
            Environment.Exit(2);
        }

        /// <summary>
        /// Adjust the method response to the max payload size.
        /// </summary>
        private static void AdjustResponse(ref List<string> statusResponse)
        {
            byte[] result;
            int maxIndex = statusResponse.Count();
            string resultString = string.Empty;
            while (true)
            {
                resultString = JsonConvert.SerializeObject(statusResponse.GetRange(0, maxIndex));
                result = Encoding.UTF8.GetBytes(resultString);
                if (result.Length > MAX_RESPONSE_PAYLOAD_LENGTH)
                {
                    maxIndex /= 2;
                    continue;
                }
                else
                {
                    break;
                }
            };
            if (maxIndex != statusResponse.Count())
            {
                statusResponse.RemoveRange(maxIndex, statusResponse.Count() - maxIndex);
                statusResponse.Add("Results have been cropped due to package size limitations.");
            }
        }

        private static int MAX_RESPONSE_PAYLOAD_LENGTH = (128 * 1024 - 256);
        private static string CONTENT_TYPE_OPCUAJSON = "application/opcua+uajson";
        private static string CONTENT_ENCODING_UTF8 = "UTF-8";

        private static long _enqueueCount;
        private static long _enqueueFailureCount;
        private static BlockingCollection<MessageData> _monitoredItemsDataQueue;
        private static Task _monitoredItemsProcessorTask;
        private static DeviceClient _iotHubClient;
        private static ModuleClient _edgeHubClient;
        private static TransportType _transportType;
        private static CancellationToken _shutdownToken;
    }
}
