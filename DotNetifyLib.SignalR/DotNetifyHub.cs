﻿/*
Copyright 2016-2020 Dicky Suryadi

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using DotNetify.Security;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DotNetify
{
   /// <summary>
   /// This class is a SignalR hub for communicating with browser clients.
   /// </summary>
   public class DotNetifyHub : Hub
   {
      private readonly IVMControllerFactory _vmControllerFactory;
      private readonly IHubServiceProvider _serviceProvider;
      private readonly IPrincipalAccessor _principalAccessor;
      private readonly IHubPipeline _hubPipeline;
      private DotNetifyHubContext _hubContext;
      private IHubContext<DotNetifyHub> _globalHubContext;
      private HubCallerContext _callerContext;
      private IPrincipal _principal;

      private interface IDotNetifyHubMethod
      {
         void Request_VM();

         void Update_VM();

         void Dispose_VM();

         void Response_VM();
      }

      /// <summary>
      /// Identity principal of the hub connection.
      /// </summary>
      private IPrincipal Principal
      {
         get { return _principal ?? _callerContext?.User; }
         set { _principal = value; }
      }

      /// <summary>
      /// View model controller associated with the current connection.
      /// </summary>
      private VMController VMController
      {
         get
         {
            SetHubPrincipalAccessor();

            var vmController = _vmControllerFactory.GetInstance(Context.ConnectionId);
            vmController.RequestVMFilter = RunRequestingVMFilters;
            vmController.UpdateVMFilter = RunUpdatingVMFilters;
            vmController.ResponseVMFilter = RunRespondingVMFilters;

            if (_serviceProvider is HubServiceProvider)
               (_serviceProvider as HubServiceProvider).ServiceProvider = vmController.ServiceProvider;

            return vmController;
         }
      }

      /// <summary>
      /// Constructor for dependency injection.
      /// </summary>
      /// <param name="vmControllerFactory">Factory of view model controllers.</param>
      /// <param name="serviceProvider">Allows to provide scoped service provider for the view models.</param>
      /// <param name="principalAccessor">Allows to pass the hub principal.</param>
      /// <param name="hubPipeline">Manages middlewares and view model filters.</param>
      /// <param name="globalHubContext">Provides access to hubs.</param>
      public DotNetifyHub(
         IVMControllerFactory vmControllerFactory,
         IHubServiceProvider serviceProvider,
         IPrincipalAccessor principalAccessor,
         IHubPipeline hubPipeline,
         IHubContext<DotNetifyHub> globalHubContext)
      {
         _vmControllerFactory = vmControllerFactory;
         _vmControllerFactory.ResponseDelegate = ResponseVMAsync;
         _serviceProvider = serviceProvider;
         _principalAccessor = principalAccessor;
         _hubPipeline = hubPipeline;
         _globalHubContext = globalHubContext;
      }

      /// <summary>
      /// Handles when a client gets disconnected.
      /// </summary>
      /// <param name="stopCalled">True, if stop was called on the client closing the connection gracefully;
      /// false, if the connection has been lost for longer than the timeout.</param>
      /// <returns></returns>
      public override async Task OnDisconnectedAsync(Exception exception)
      {
         // Access VMController to set the ambient context.
         VMController _ = VMController;

         // Remove the controller on disconnection.
         _vmControllerFactory.Remove(Context.ConnectionId);

         // Allow middlewares to hook to the event.
         await _hubPipeline.RunDisconnectionMiddlewaresAsync(Context);

         await base.OnDisconnectedAsync(exception);
      }

      #region Client Requests

      /// <summary>
      /// This method is called by browser clients to request view model data.
      /// </summary>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vmArg">Optional argument that may contain view model's initialization argument and/or request headers.</param>
      [HubMethodName(nameof(IDotNetifyHubMethod.Request_VM))]
      public async Task RequestVMAsync(string vmId, object vmArg)
      {
         object data = NormalizeType(vmArg);

         try
         {
            _callerContext = Context;
            _hubContext = new DotNetifyHubContext(_callerContext, nameof(IDotNetifyHubMethod.Request_VM), vmId, data, null, Principal);
            await _hubPipeline.RunMiddlewaresAsync(_hubContext, async ctx =>
            {
               Principal = ctx.Principal;
               string groupName = await VMController.OnRequestVMAsync(Context.ConnectionId, ctx.VMId, ctx.Data);

               // A multicast view model may be assigned to a SignalR group. If so, add the connection to the group.
               if (!string.IsNullOrEmpty(groupName))
                  await _globalHubContext.Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            });
         }
         catch (Exception ex)
         {
            var finalEx = await _hubPipeline.RunExceptionMiddlewareAsync(Context, ex);
            if (finalEx is OperationCanceledException == false)
               await ResponseVMAsync(Context.ConnectionId, vmId, SerializeException(finalEx));
         }
      }

      [Obsolete]
      [HubMethodName("Request_VM_Obsolete")]
      public void Request_VM(string vmId, object vmArg) => _ = RequestVMAsync(vmId, vmArg);

      /// <summary>
      /// This method is called by browser clients to update a view model's value.
      /// </summary>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vmData">View model update data, where key is the property path and value is the property's new value.</param>
      [HubMethodName(nameof(IDotNetifyHubMethod.Update_VM))]
      public async Task UpdateVMAsync(string vmId, Dictionary<string, object> vmData)
      {
         var data = vmData?.ToDictionary(x => x.Key, x => NormalizeType(x.Value));

         try
         {
            _callerContext = Context;
            _hubContext = new DotNetifyHubContext(_callerContext, nameof(IDotNetifyHubMethod.Update_VM), vmId, data, null, Principal);
            await _hubPipeline.RunMiddlewaresAsync(_hubContext, async ctx =>
            {
               Principal = ctx.Principal;
               await VMController.OnUpdateVMAsync(ctx.CallerContext.ConnectionId, ctx.VMId, ctx.Data as Dictionary<string, object>);
            });
         }
         catch (Exception ex)
         {
            var finalEx = await _hubPipeline.RunExceptionMiddlewareAsync(Context, ex);
            if (finalEx is OperationCanceledException == false)
               await ResponseVMAsync(Context.ConnectionId, vmId, SerializeException(finalEx));
         }
      }

      [Obsolete]
      [HubMethodName("Update_VM_Obsolete")]
      public void Update_VM(string vmId, Dictionary<string, object> vmData) => _ = UpdateVMAsync(vmId, vmData);

      /// <summary>
      /// This method is called by browser clients to remove its view model as it's no longer used.
      /// </summary>
      /// <param name="vmId">Identifies the view model.  By convention, this should match a view model class name.</param>
      [HubMethodName(nameof(IDotNetifyHubMethod.Dispose_VM))]
      public async Task DisposeVMAsyc(string vmId)
      {
         try
         {
            _callerContext = Context;
            _hubContext = new DotNetifyHubContext(_callerContext, nameof(IDotNetifyHubMethod.Dispose_VM), vmId, null, null, Principal);
            await _hubPipeline.RunMiddlewaresAsync(_hubContext, ctx =>
            {
               Principal = ctx.Principal;
               VMController.OnDisposeVM(Context.ConnectionId, ctx.VMId);
               return Task.CompletedTask;
            });
         }
         catch (Exception ex)
         {
            await _hubPipeline.RunExceptionMiddlewareAsync(Context, ex);
         }
      }

      [Obsolete]
      [HubMethodName("Dispose_VM_Obsolete")]
      public void Dispose_VM(string vmId) => _ = DisposeVMAsyc(vmId);

      #endregion Client Requests

      #region Server Responses

      /// <summary>
      /// This method is called internally to send response back to browser clients.
      /// This is also overloaded to handle SignalR groups for multicast view models.
      /// </summary>
      /// <param name="connectionId">Identifies the browser client making prior request.</param>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vmData">View model data in serialized JSON.</param>
      internal Task ResponseVMAsync(string connectionId, string vmId, string vmData)
      {
         if (connectionId.StartsWith(VMController.MULTICAST))
            HandleMulticastMessage(connectionId, vmId, vmData);
         else
         {
            if (_vmControllerFactory.GetInstance(connectionId) != null) // Touch the factory to push the timeout.
               _globalHubContext.Clients.Client(connectionId).SendAsync(nameof(IDotNetifyHubMethod.Response_VM), new object[] { vmId, vmData });
         }
         return Task.CompletedTask;
      }

      /// <summary>
      /// Handles messages dealing with group multicasting.
      /// </summary>
      /// <param name="messageType">Message type.</param>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="serializedMessage">Serialized message.</param>
      internal void HandleMulticastMessage(string messageType, string vmId, string serializedMessage)
      {
         if (messageType.EndsWith(nameof(VMController.GroupSend)))
         {
            var message = JsonConvert.DeserializeObject<VMController.GroupSend>(serializedMessage);
            var method = nameof(IDotNetifyHubMethod.Response_VM);
            var payload = new object[] { vmId, message.Data };

            if (!string.IsNullOrEmpty(message.GroupName))
            {
               if (message.ExcludedConnectionIds?.Count == 0)
                  _globalHubContext.Clients.Group(message.GroupName).SendAsync(method, payload);
               else
               {
                  var excludedIds = new List<string>(message.ExcludedConnectionIds);
                  _globalHubContext.Clients.GroupExcept(message.GroupName, excludedIds).SendAsync(method, payload);
               }
            }
            else if (message.UserIds?.Count > 0)
            {
               var userIds = new List<string>(message.UserIds);
               _globalHubContext.Clients.Users(userIds).SendAsync(method, payload);
            }
            else if (message.ConnectionIds?.Count > 0)
            {
               foreach (var connectionId in message.ConnectionIds)
                  _globalHubContext.Clients.Client(connectionId).SendAsync(method, payload);
            }

            // Touch the factory to push the timeout.
            foreach (var connectionId in message.ConnectionIds)
               _vmControllerFactory.GetInstance(connectionId);
         }
         else if (messageType.EndsWith(nameof(VMController.GroupRemove)))
         {
            var message = JsonConvert.DeserializeObject<VMController.GroupRemove>(serializedMessage);
            _globalHubContext.Groups.RemoveFromGroupAsync(message.ConnectionId, message.GroupName);
         }
      }

      #endregion Server Responses

      /// <summary>
      /// Normalizes the type of the object argument to JObject when possible.
      /// </summary>
      /// <param name="data">Arbitrary object.</param>
      /// <returns>JObject if the object is convertible; otherwise unchanged.</returns>
      internal static object NormalizeType(object data)
      {
         if (data == null)
            return null;
         else if (data is JsonElement jElement)
         {
            // System.Text.Json protocol.
            var value = JToken.Parse(jElement.GetRawText());
            return value is JValue ? (value as JValue).Value : value;
         }
         else if (data is JObject)
            // Newtonsoft.Json protocol.
            return data as JObject;
         else if (!(data.GetType().IsPrimitive || data is string))
            // MessagePack protocol.
            return JObject.FromObject(data);
         return data;
      }

      /// <summary>
      /// Runs the view model filter.
      /// </summary>
      /// <param name="vmId">Identifies the view model.</param>
      /// <param name="vm">View model instance.</param>
      /// <param name="data">View model data.</param>
      /// <param name="vmAction">Filter action.</param>
      private async Task RunVMFilters(BaseVM vm, object data, VMController.VMActionDelegate vmAction)
      {
         try
         {
            _hubContext.Data = data;
            await _hubPipeline.RunVMFiltersAsync(_hubContext, vm, async ctx =>
            {
               await vmAction(ctx.HubContext.Data);
            });
         }
         catch (TargetInvocationException ex)
         {
            throw ex.InnerException;
         }
      }

      /// <summary>
      /// Runs the filter before the view model is requested.
      /// </summary>
      private Task RunRequestingVMFilters(string vmId, BaseVM vm, object vmArg, VMController.VMActionDelegate vmAction) => RunVMFilters(vm, vmArg, vmAction);

      /// <summary>
      /// Runs the filter before the view model is updated.
      /// </summary>
      private Task RunUpdatingVMFilters(string vmId, BaseVM vm, object vmData, VMController.VMActionDelegate vmAction) => RunVMFilters(vm, vmData, vmAction);

      /// <summary>
      /// Runs the filter before the view model respond to something.
      /// </summary>
      private async Task RunRespondingVMFilters(string vmId, BaseVM vm, object vmData, VMController.VMActionDelegate vmAction)
      {
         try
         {
            _hubContext = new DotNetifyHubContext(_callerContext, nameof(IDotNetifyHubMethod.Response_VM), vmId, vmData, null, Principal);
            await _hubPipeline.RunMiddlewaresAsync(_hubContext, async ctx =>
            {
               Principal = ctx.Principal;
               await RunVMFilters(vm, ctx.Data, vmAction);
            });
         }
         catch (Exception ex)
         {
            var finalEx = await _hubPipeline.RunExceptionMiddlewareAsync(_callerContext, ex);
            if (finalEx is OperationCanceledException == false && _callerContext != null)
               await ResponseVMAsync(_callerContext.ConnectionId, vmId, SerializeException(finalEx));
         }
      }

      /// <summary>
      /// Serializes an exception.
      /// </summary>
      /// <param name="ex">Exception to serialize.</param>
      /// <returns>Serialized exception.</returns>
      internal static string SerializeException(Exception ex) => JsonConvert.SerializeObject(new { ExceptionType = ex.GetType().Name, ex.Message });

      /// <summary>
      /// Sets the hub principal and connection context to the ambient accessor object.
      /// </summary>
      private void SetHubPrincipalAccessor()
      {
         if (_principalAccessor is HubPrincipalAccessor)
         {
            var hubPrincipalAccessor = _principalAccessor as HubPrincipalAccessor;
            hubPrincipalAccessor.Principal = Principal;
            hubPrincipalAccessor.CallerContext = Context;
         }
      }
   }
}