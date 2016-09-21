﻿#region Copyright
//=======================================================================================
// Microsoft Azure Customer Advisory Team  
//
// This sample is supplemental to the technical guidance published on the community
// blog at http://blogs.msdn.com/b/paolos/. 
// 
// Author: Paolo Salvatori
//=======================================================================================
// Copyright © 2016 Microsoft Corporation. All rights reserved.
// 
// THIS CODE AND INFORMATION IS PROVIDED "AS IS" WITHOUT WARRANTY OF ANY KIND, EITHER 
// EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED WARRANTIES OF 
// MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE. YOU BEAR THE RISK OF USING IT.
//=======================================================================================
#endregion

#region Using Directives

using System;
using System.Diagnostics.Tracing;
using System.Fabric;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Services.Runtime;

#endregion

namespace Microsoft.AzureCat.Samples.EventProcessorHostService
{
    [EventSource(Name = "WriteEventsToSql-EventProcessorHostService")]
    internal sealed class ServiceEventSource : EventSource
    {
        public static readonly ServiceEventSource Current = new ServiceEventSource();

        static ServiceEventSource()
        {
            // A workaround for the problem where ETW activities do not get tracked until Tasks infrastructure is initialized.
            // This problem will be fixed in .NET Framework 4.6.2.
            Task.Run(() => { }).Wait();
        }

        // Instance constructor is private to enforce singleton semantics
        private ServiceEventSource()
        { }

        #region Keywords
        // Event keywords can be used to categorize events. 
        // Each keyword is a bit flag. A single event can be associated with multiple keywords (via EventAttribute.Keywords property).
        // Keywords must be defined as a public class named 'Keywords' inside EventSource that uses them.
        public static class Keywords
        {
            public const EventKeywords Requests = (EventKeywords)0x1L;
            public const EventKeywords ServiceInitialization = (EventKeywords)0x2L;
            public const EventKeywords EventHub = (EventKeywords)0x4L;
        }
        #endregion

        #region Events
        // Define an instance method for each event you want to record and apply an [Event] attribute to it.
        // The method name is the name of the event.
        // Pass any parameters you want to record with the event (only primitive integer types, DateTime, Guid & string are allowed).
        // Each event method implementation should check whether the event source is enabled, and if it is, call WriteEvent() method to raise the event.
        // The number and types of arguments passed to every event method must exactly match what is passed to WriteEvent().
        // Put [NonEvent] attribute on all methods that do not define an event.
        // For more information see https://msdn.microsoft.com/en-us/library/system.diagnostics.tracing.eventsource.aspx

        private const int MessageEventId = 1;
        [Event(MessageEventId, Level = EventLevel.Informational, Message = "{0}")]
        public void Message(string message, [CallerFilePath] string source = "", [CallerMemberName] string method = "")
        {
            if (!IsEnabled())
            {
                return;
            }
            WriteEvent(MessageEventId, $"[{GetClassFromFilePath(source) ?? "UNKNOWN"}::{method ?? "UNKNOWN"}] {message}");
        }

        [NonEvent]
        public void ServiceMessage(StatelessService service, string message, params object[] args)
        {
            if (IsEnabled())
            {
                string finalMessage = string.Format(message, args);
                ServiceMessage(
                    service.Context.ServiceName.ToString(),
                    service.Context.ServiceTypeName,
                    service.Context.InstanceId,
                    service.Context.PartitionId,
                    service.Context.CodePackageActivationContext.ApplicationName,
                    service.Context.CodePackageActivationContext.ApplicationTypeName,
                    FabricRuntime.GetNodeContext().NodeName,
                    finalMessage);
            }
        }

        [NonEvent]
        public void ServiceMessage(StatefulService service, string message, params object[] args)
        {
            if (IsEnabled())
            {
                string finalMessage = string.Format(message, args);
                ServiceMessage(
                    service.Context.ServiceName.ToString(),
                    service.Context.ServiceTypeName,
                    service.Context.ReplicaId,
                    service.Context.PartitionId,
                    service.Context.CodePackageActivationContext.ApplicationName,
                    service.Context.CodePackageActivationContext.ApplicationTypeName,
                    FabricRuntime.GetNodeContext().NodeName,
                    finalMessage);
            }
        }

        // For very high-frequency events it might be advantageous to raise events using WriteEventCore API.
        // This results in more efficient parameter handling, but requires explicit allocation of EventData structure and unsafe code.
        // To enable this code path, define UNSAFE conditional compilation symbol and turn on unsafe code support in project properties.
        private const int ServiceMessageEventId = 2;
        [Event(ServiceMessageEventId, Level = EventLevel.Informational, Message = "{7}")]
        private
#if UNSAFE
        unsafe
#endif
        void ServiceMessage(
            string serviceName,
            string serviceTypeName,
            long replicaOrInstanceId,
            Guid partitionId,
            string applicationName,
            string applicationTypeName,
            string nodeName,
            string message)
        {
#if !UNSAFE
            WriteEvent(ServiceMessageEventId, serviceName, serviceTypeName, replicaOrInstanceId, partitionId, applicationName, applicationTypeName, nodeName, message);
#else
            const int numArgs = 8;
            fixed (char* pServiceName = serviceName, pServiceTypeName = serviceTypeName, pApplicationName = applicationName, pApplicationTypeName = applicationTypeName, pNodeName = nodeName, pMessage = message)
            {
                EventData* eventData = stackalloc EventData[numArgs];
                eventData[0] = new EventData { DataPointer = (IntPtr) pServiceName, Size = SizeInBytes(serviceName) };
                eventData[1] = new EventData { DataPointer = (IntPtr) pServiceTypeName, Size = SizeInBytes(serviceTypeName) };
                eventData[2] = new EventData { DataPointer = (IntPtr) (&replicaOrInstanceId), Size = sizeof(long) };
                eventData[3] = new EventData { DataPointer = (IntPtr) (&partitionId), Size = sizeof(Guid) };
                eventData[4] = new EventData { DataPointer = (IntPtr) pApplicationName, Size = SizeInBytes(applicationName) };
                eventData[5] = new EventData { DataPointer = (IntPtr) pApplicationTypeName, Size = SizeInBytes(applicationTypeName) };
                eventData[6] = new EventData { DataPointer = (IntPtr) pNodeName, Size = SizeInBytes(nodeName) };
                eventData[7] = new EventData { DataPointer = (IntPtr) pMessage, Size = SizeInBytes(message) };

                WriteEventCore(ServiceMessageEventId, numArgs, eventData);
            }
#endif
        }

        private const int ServiceTypeRegisteredEventId = 3;
        [Event(ServiceTypeRegisteredEventId, Level = EventLevel.Informational, Message = "Service host process {0} registered service type {1}", Keywords = Keywords.ServiceInitialization)]
        public void ServiceTypeRegistered(int hostProcessId, string serviceType)
        {
            WriteEvent(ServiceTypeRegisteredEventId, hostProcessId, serviceType);
        }

        private const int ServiceHostInitializationFailedEventId = 4;
        [Event(ServiceHostInitializationFailedEventId, Level = EventLevel.Error, Message = "Service host initialization failed", Keywords = Keywords.ServiceInitialization)]
        public void ServiceHostInitializationFailed(string exception)
        {
            WriteEvent(ServiceHostInitializationFailedEventId, exception);
        }

        // A pair of events sharing the same name prefix with a "Start"/"Stop" suffix implicitly marks boundaries of an event tracing activity.
        // These activities can be automatically picked up by debugging and profiling tools, which can compute their execution time, child activities,
        // and other statistics.
        private const int ServiceRequestStartEventId = 5;
        [Event(ServiceRequestStartEventId, Level = EventLevel.Informational, Message = "Service request '{0}' started", Keywords = Keywords.Requests)]
        public void ServiceRequestStart(string requestTypeName)
        {
            WriteEvent(ServiceRequestStartEventId, requestTypeName);
        }

        private const int ServiceRequestStopEventId = 6;
        [Event(ServiceRequestStopEventId, Level = EventLevel.Informational, Message = "Service request '{0}' finished", Keywords = Keywords.Requests)]
        public void ServiceRequestStop(string requestTypeName)
        {
            WriteEvent(ServiceRequestStopEventId, requestTypeName);
        }

        private const int ServiceRequestFailedEventId = 7;
        [Event(ServiceRequestFailedEventId, Level = EventLevel.Error, Message = "Service request '{0}' failed", Keywords = Keywords.Requests)]
        public void ServiceRequestFailed(string requestTypeName, string exception)
        {
            WriteEvent(ServiceRequestFailedEventId, exception);
        }

        private const int OpenPartitionEventId = 8;
        [Event(OpenPartitionEventId,
               Message = "[{3}::{4}] EventHub=[{0}] ConsumerGroup=[{1}] PartitionId=[{2}]",
               Keywords = Keywords.EventHub,
               Level = EventLevel.Informational)]
        public void OpenPartition(string eventHub,
                                  string consumerGroup,
                                  string partitionId,
                                  [CallerFilePath] string source = "", 
                                  [CallerMemberName] string method = "")
        {
            if (string.IsNullOrWhiteSpace(eventHub) ||
                string.IsNullOrWhiteSpace(consumerGroup) ||
                string.IsNullOrWhiteSpace(partitionId) ||
                !IsEnabled())
            {
                return;
            }
            WriteEvent(OpenPartitionEventId,
                       eventHub, 
                       consumerGroup, 
                       partitionId,
                       GetClassFromFilePath(source) ?? "UNKNOWN", 
                       method ?? "UNKNOWN");
        }

        private const int ClosePartitionEventId = 9;
        [Event(ClosePartitionEventId,
               Message = "[{4}::{5}] EventHub=[{0}] ConsumerGroup=[{1}] PartitionId=[{2}] Reason=[{3}]",
               Keywords = Keywords.EventHub,
               Level = EventLevel.Informational)]
        public void ClosePartition(string eventHub,
                                   string consumerGroup,
                                   string partitionId,
                                   string reason,
                                   [CallerFilePath] string source = "",
                                   [CallerMemberName] string method = "")
        {
            if (string.IsNullOrWhiteSpace(eventHub) ||
                string.IsNullOrWhiteSpace(consumerGroup) ||
                string.IsNullOrWhiteSpace(partitionId) ||
                !IsEnabled())
            {
                return;
            }
            WriteEvent(ClosePartitionEventId, 
                       eventHub, 
                       consumerGroup, 
                       partitionId, 
                       reason,
                       GetClassFromFilePath(source) ?? "UNKNOWN",
                       method ?? "UNKNOWN");
        }

        private const int ProcessEventsEventId = 10;
        [Event(ProcessEventsEventId,
               Message = "[{4}::{5}] EventHub=[{0}] ConsumerGroup=[{1}] PartitionId=[{2}] MessageCount=[{3}]",
               Keywords = Keywords.EventHub,
               Level = EventLevel.Informational)]
        public void ProcessEvents(string eventHub,
                                  string consumerGroup,
                                  string partitionId,
                                  int messageCount,
                                  [CallerFilePath] string source = "",
                                  [CallerMemberName] string method = "")
        {
            if (string.IsNullOrWhiteSpace(eventHub) ||
                string.IsNullOrWhiteSpace(consumerGroup) ||
                string.IsNullOrWhiteSpace(partitionId) ||
                !IsEnabled())
            {
                return;
            }
            WriteEvent(ProcessEventsEventId,
                       eventHub,
                       consumerGroup,
                       partitionId,
                       messageCount,
                       GetClassFromFilePath(source) ?? "UNKNOWN",
                       method ?? "UNKNOWN");
        }
        #endregion

        #region Private Static Methods
        private static string GetClassFromFilePath(string sourceFilePath)
        {
            if (string.IsNullOrWhiteSpace(sourceFilePath))
            {
                return null;
            }
            var file = new FileInfo(sourceFilePath);
            return Path.GetFileNameWithoutExtension(file.Name);
        }

#if UNSAFE
        private int SizeInBytes(string s)
        {
            if (s == null)
            {
                return 0;
            }
            else
            {
                return (s.Length + 1) * sizeof(char);
            }
        }
#endif
        #endregion
    }
}
