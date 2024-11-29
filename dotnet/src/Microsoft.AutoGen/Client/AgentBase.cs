// Copyright (c) Microsoft Corporation. All rights reserved.
// AgentBase.cs

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;

namespace Microsoft.AutoGen.Core;

public abstract class AgentBase
{
    public static readonly ActivitySource s_source = new("AutoGen.Agent");
    public AgentId AgentId => _context.AgentId;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcResponse>> _pendingRequests = [];

    private readonly Channel<object> _mailbox = Channel.CreateUnbounded<object>();
    private readonly RuntimeContext _context;
    public RuntimeContext Context => _context;
    public string Route { get; set; } = "base";

    protected internal ILogger<AgentBase> _logger;
    protected readonly EventTypes EventTypes;

    protected AgentBase(
        RuntimeContext context,
        EventTypes eventTypes,
        ILogger<AgentBase>? logger = null)
    {
        _context = context;
        context.AgentInstance = this;
        EventTypes = eventTypes;
        _logger = logger ?? LoggerFactory.Create(builder => { }).CreateLogger<AgentBase>();
        Completion = Start();
    }
    internal Task Completion { get; }

    internal Task Start()
    {
        var didSuppress = false;
        if (!ExecutionContext.IsFlowSuppressed())
        {
            didSuppress = true;
            ExecutionContext.SuppressFlow();
        }

        try
        {
            return Task.Run(RunMessagePump);
        }
        finally
        {
            if (didSuppress)
            {
                ExecutionContext.RestoreFlow();
            }
        }
    }
    public void ReceiveMessage(Message message) => _mailbox.Writer.TryWrite(message);

    private async Task RunMessagePump()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await foreach (var message in _mailbox.Reader.ReadAllAsync())
        {
            try
            {
                switch (message)
                {
                    case Message msg:
                        await HandleRpcMessage(msg, new CancellationToken()).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected message '{message}'.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing message.");
            }
        }
    }
    protected internal async Task HandleRpcMessage(Message msg, CancellationToken cancellationToken = default)
    {
        switch (msg.MessageCase)
        {
            case Message.MessageOneofCase.CloudEvent:
                {
                    var activity = this.ExtractActivity(msg.CloudEvent.Type, msg.CloudEvent.Metadata);
                    await this.InvokeWithActivityAsync(
                        static (state, ct) => state.Item1.CallHandler(state.CloudEvent, ct),
                        (this, msg.CloudEvent),
                        activity,
                        msg.CloudEvent.Type, cancellationToken).ConfigureAwait(false);
                }
                break;
            case Message.MessageOneofCase.Request:
                {
                    var activity = this.ExtractActivity(msg.Request.Method, msg.Request.Metadata);
                    await this.InvokeWithActivityAsync(
                        static (state, ct) => state.Item1.OnRequestCoreAsync(state.Request, ct),
                        (this, msg.Request),
                        activity,
                        msg.Request.Method, cancellationToken).ConfigureAwait(false);
                }
                break;
            case Message.MessageOneofCase.Response:
                OnResponseCore(msg.Response);
                break;
        }
    }
    public List<string> Subscribe(string topic)
    {
        Message message = new()
        {
            AddSubscriptionRequest = new()
            {
                RequestId = Guid.NewGuid().ToString(),
                Subscription = new Subscription
                {
                    TypeSubscription = new TypeSubscription
                    {
                        TopicType = topic,
                        AgentType = AgentId.Key
                    }
                }
            }
        };
        _context.SendMessageAsync(message).AsTask().Wait();

        return new List<string> { topic };
    }
    public async Task StoreAsync(AgentState state, CancellationToken cancellationToken = default)
    {
        await _context.StoreAsync(state, cancellationToken).ConfigureAwait(false);
        return;
    }
    public async Task<T> ReadAsync<T>(AgentId agentId, CancellationToken cancellationToken = default) where T : IMessage, new()
    {
        var agentstate = await _context.ReadAsync(agentId, cancellationToken).ConfigureAwait(false);
        return agentstate.FromAgentState<T>();
    }
    private void OnResponseCore(RpcResponse response)
    {
        var requestId = response.RequestId;
        TaskCompletionSource<RpcResponse>? completion;
        lock (_lock)
        {
            if (!_pendingRequests.Remove(requestId, out completion))
            {
                throw new InvalidOperationException($"Unknown request id '{requestId}'.");
            }
        }

        completion.SetResult(response);
    }
    private async Task OnRequestCoreAsync(RpcRequest request, CancellationToken cancellationToken = default)
    {
        RpcResponse response;

        try
        {
            response = await HandleRequest(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response = new RpcResponse { Error = ex.Message };
        }
        await _context.SendResponseAsync(request, response, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<RpcResponse> RequestAsync(AgentId target, string method, Dictionary<string, string> parameters)
    {
        var requestId = Guid.NewGuid().ToString();
        var request = new RpcRequest
        {
            Target = target,
            RequestId = requestId,
            Method = method,
            Payload = new Payload
            {
                DataType = "application/json",
                Data = ByteString.CopyFrom(JsonSerializer.Serialize(parameters), Encoding.UTF8),
                DataContentType = "application/json"

            }
        };

        var activity = s_source.StartActivity($"Call '{method}'", ActivityKind.Client, Activity.Current?.Context ?? default);
        activity?.SetTag("peer.service", target.ToString());

        var completion = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Update(request, activity);
        await this.InvokeWithActivityAsync(
            static async (state, ct) =>
            {
                var (self, request, completion) = state;

                lock (self._lock)
                {
                    self._pendingRequests[request.RequestId] = completion;
                }

                await state.Item1._context.SendRequestAsync(state.Item1, state.request, ct).ConfigureAwait(false);

                await completion.Task.ConfigureAwait(false);
            },
            (this, request, completion),
            activity,
            method).ConfigureAwait(false);

        // Return the result from the already-completed task
        return await completion.Task.ConfigureAwait(false);
    }

    public async ValueTask PublishMessageAsync<T>(T message, string? source = null, CancellationToken token = default) where T : IMessage
    {
        var src = string.IsNullOrWhiteSpace(source) ? AgentId.Key : source;
        var evt = message.ToCloudEvent(src);
        await PublishEventAsync(evt, token).ConfigureAwait(false);
    }

    public async ValueTask PublishEventAsync(CloudEvent item, CancellationToken cancellationToken = default)
    {
        var activity = s_source.StartActivity($"PublishEventAsync '{item.Type}'", ActivityKind.Client, Activity.Current?.Context ?? default);
        activity?.SetTag("peer.service", $"{item.Type}/{item.Source}");

        // TODO: fix activity
        _context.Update(item, activity);
        await this.InvokeWithActivityAsync(
            static async (state, ct) =>
            {
                await state.Item1._context.PublishEventAsync(state.item, cancellationToken : ct).ConfigureAwait(false);
            },
            (this, item),
            activity,
            item.Type, cancellationToken).ConfigureAwait(false);
    }

    public Task CallHandler(CloudEvent item, CancellationToken cancellationToken)
    {
        // Only send the event to the handler if the agent type is handling that type
        // foreach of the keys in the EventTypes.EventsMap[] if it contains the item.type
        if (EventTypes.CheckIfTypeHandles(GetType(), item.Type) &&
                 item.Source == AgentId.Key)
        {
            var payload = item.ProtoData.Unpack(EventTypes.TypeRegistry);
            var eventType = EventTypes.GetEventTypeByName(item.Type) ?? throw new InvalidOperationException($"Type not found on event type {item.Type}");
            var convertedPayload = Convert.ChangeType(payload, eventType);
            var genericInterfaceType = typeof(IHandle<>).MakeGenericType(eventType);

            MethodInfo? methodInfo = null;
            try
            {
                // check that our target actually implements this interface, otherwise call the default static
                if (genericInterfaceType.IsInstanceOfType(this))
                {
                    methodInfo = genericInterfaceType.GetMethod("Handle", BindingFlags.Public | BindingFlags.Instance)
                                   ?? throw new InvalidOperationException($"Method not found on type {genericInterfaceType.FullName}");
                    return methodInfo.Invoke(this, [convertedPayload, cancellationToken]) as Task ?? Task.CompletedTask;
                }

                // The error here is we have registered for an event that we do not have code to listen to
                throw new InvalidOperationException($"No handler found for event '{item.Type}'; expecting IHandle<{item.Type}> implementation.");
                
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error invoking method {methodInfo?.Name ?? "Handle"}");
                throw; // TODO: ?
            }
        }

        return Task.CompletedTask;
    }

    public Task<RpcResponse> HandleRequest(RpcRequest request) => Task.FromResult(new RpcResponse { Error = "Not implemented" });

    //TODO: should this be async and cancellable?
    public virtual Task HandleObject(object item)
    {
        // get all Handle<T> methods
        var handleTMethods = GetType().GetMethods().Where(m => m.Name == "Handle" && m.GetParameters().Length == 1).ToList();

        // get the one that matches the type of the item
        var handleTMethod = handleTMethods.FirstOrDefault(m => m.GetParameters()[0].ParameterType == item.GetType());

        // if we found one, invoke it
        if (handleTMethod != null)
        {
            return (Task)handleTMethod.Invoke(this, [item])!;
        }

        // otherwise, complain
        throw new InvalidOperationException($"No handler found for type {item.GetType().FullName}");
    }
    public async ValueTask PublishEventAsync(string topic, IMessage evt, CancellationToken cancellationToken = default)
    {
        await PublishEventAsync(evt.ToCloudEvent(topic), cancellationToken).ConfigureAwait(false);
    }
}