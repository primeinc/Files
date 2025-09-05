# Remote Control / IPC — hardened (final candidate)

This revision hardens the IPC subsystem for Files to address resource, security, and correctness issues:
- Strict JSON-RPC 2.0 validation and shape enforcement (includes IsInvalidRequest).
- Encrypted token storage (DPAPI) with epoch-based rotation that invalidates existing sessions.
- Centralized RpcMethodRegistry used everywhere (transports + adapter).
- WebSocket receive caps, per-method caps, per-client queue caps, lossy coalescing by method and per-client token bucket applied for both requests and notifications.
- Named Pipe per-user ACL and per-session randomized pipe name; length-prefixed framing.
- getMetadata: capped by items and timeout; runs off UI thread and honors client cancellation.
- Selection notifications are capped and include truncated flag.
- UIOperationQueue required to be passed a DispatcherQueue; all UI-affecting operations serialized.

## Merge checklist
- [ ] Settings UI: "Enable Remote Control" (ProtectedTokenStore.SetEnabled), "Rotate Token" (RotateTokenAsync), "Enable Long Paths" toggle and display of current pipe name/port only when enabled.
- [ ] ShellViewModel: wire ExecuteActionById / NavigateToPathNormalized or expose small interface for adapter.
- [ ] Packaging decision: Document Kestrel + URLACL if WS is desired in Store/MSIX; default recommended for Store builds is NamedPipe-only.
- [ ] Tests: WS/pipe oversize, slow-consumer (lossy/coalesce), JSON-RPC conformance, getMetadata timeout & cancellation, token rotation invalidation.
- [ ] Telemetry hooks: auth failures, slow-client disconnects, queue drops.

## JSON-RPC error codes used
- -32700 Parse error
- -32600 Invalid Request
- -32601 Method not found
- -32602 Invalid params
- -32001 Authentication required
- -32002 Invalid token
- -32003 Rate limit exceeded
- -32004 Session expired
- -32000 Internal server error

## Architecture

### Core Components

#### JsonRpcMessage
Strict JSON-RPC 2.0 implementation with helpers for creating responses and validating message shapes. Preserves original ID types and enforces result XOR error semantics.

#### ProtectedTokenStore
DPAPI-backed encrypted token storage in LocalSettings with epoch-based rotation. When tokens are rotated, the epoch increments and invalidates all existing client sessions.

#### ClientContext  
Per-client state management including:
- Token bucket rate limiting (configurable burst and refill rate)
- Lossy message queue with method-based coalescing
- Authentication state and epoch tracking
- Connection lifecycle management

#### RpcMethodRegistry
Centralized registry for RPC method definitions including:
- Authentication requirements
- Notification permissions  
- Per-method payload size limits
- Custom authorization policies

#### Transport Services
- **WebSocketAppCommunicationService**: HTTP listener on loopback with WebSocket upgrade
- **NamedPipeAppCommunicationService**: Per-user ACL with randomized pipe names

#### ShellIpcAdapter
Application logic adapter that:
- Enforces method allowlists and security policies
- Provides path normalization and validation
- Implements resource-bounded operations (metadata with timeouts)
- Serializes UI operations through UIOperationQueue

## Security Features

### Authentication & Authorization
- DPAPI-encrypted token storage
- Per-session token validation with epoch checking
- Method-level authorization policies
- Per-user ACL on named pipes

### Resource Protection
- Configurable message size limits per transport
- Per-client queue size limits with lossy behavior
- Rate limiting with token bucket algorithm
- Operation timeouts and cancellation support

### Attack Mitigation
- Strict JSON-RPC validation prevents malformed requests
- Path normalization rejects device paths and traversal attempts
- Selection notifications capped to prevent resource exhaustion
- Automatic cleanup of inactive/stale connections

## Configuration

All limits are configurable via `IpcConfig`:
```csharp
IpcConfig.WebSocketMaxMessageBytes = 16 * 1024 * 1024; // 16 MB
IpcConfig.NamedPipeMaxMessageBytes = 10 * 1024 * 1024; // 10 MB  
IpcConfig.PerClientQueueCapBytes = 2 * 1024 * 1024;    // 2 MB
IpcConfig.RateLimitPerSecond = 20;
IpcConfig.RateLimitBurst = 60;
IpcConfig.SelectionNotificationCap = 200;
IpcConfig.GetMetadataMaxItems = 500;
IpcConfig.GetMetadataTimeoutSec = 30;
```

## Supported Methods

### Authentication
- `handshake` - Authenticate with token and establish session

### State Query  
- `getState` - Get current navigation state
- `listActions` - Get available actions

### Operations
- `navigate` - Navigate to path (with normalization)
- `executeAction` - Execute registered action by ID
- `getMetadata` - Get file/folder metadata (batched, with timeout)

### Notifications (Broadcast)
- `workingDirectoryChanged` - Current directory changed
- `selectionChanged` - File selection changed (with truncation)
- `ping` - Keepalive heartbeat

## Usage

**DO NOT enable IPC by default** — StartAsync refuses to start unless the user explicitly enables Remote Control via Settings. See merge checklist above.

### Enabling Remote Control
```csharp
// In Settings UI
await ProtectedTokenStore.SetEnabled(true);
var token = await ProtectedTokenStore.GetOrCreateTokenAsync();
```

### Starting Services
```csharp
// Only starts if enabled
await webSocketService.StartAsync();
await namedPipeService.StartAsync();
```

### Token Rotation
```csharp
// Invalidates all existing sessions
var newToken = await ProtectedTokenStore.RotateTokenAsync();
```

## Implementation Status

✅ **Complete**: Core IPC framework, security model, transport services
🔄 **Pending**: Settings UI integration, ShellViewModel method wiring
📋 **TODO**: Comprehensive tests, telemetry integration, Kestrel option