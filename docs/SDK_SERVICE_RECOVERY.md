# Logix Designer SDK Service recovery

The shared service can start the local Windows service **Logix Designer SDK Service** (`LdSdkService`) when it is stopped. MCP, authenticated REST and the desktop SDK workspace use the same implementation. A running SDK service stays running; recovery never stops it or interrupts it deliberately.

## Chat and API access

Discover these extension operations through `studio_extensions_catalog`, then describe and invoke their current generation:

| Operation | Effect | REST route |
| --- | --- | --- |
| `studio_sdk_service_status` | Read Windows state, configuration and retained uncertain request IDs | `GET /api/sdk/service` |
| `studio_sdk_service_ensure_running` | Start a stopped service or observe its existing startup | `POST /api/sdk/service/ensure-running` |

The control operation requires a fresh lowercase 32-character hexadecimal `requestId`, for example:

```json
{"requestId":"82731f529d3d4b22819cf210f7e22c674"}
```

Retain that ID across transport errors. Repeating it returns the original persisted receipt, including an original no-op or refusal. It never becomes a new start if the service later stops. Use a new ID only for a new deliberate request after reviewing the previous result. Status and service control do not open a PLC project or connect to a controller.

## Automatic use and GUI

Before submitting a new SDK job, native project conversion or SDK watch session, Core checks the SDK service and starts it if stopped. SDK signature inspection and CIP reads do not require this preflight. Existing polling sessions are not reconnected, and interrupted SDK jobs or controller writes are never replayed by recovery.

The desktop **SDK** workspace has **SDK service status** and **Start SDK service** buttons. The start button retains its request ID after a lost or uncertain response. Under **Settings → Runtime and limits**, configure:

| Setting | Default | Meaning |
| --- | --- | --- |
| `EnableSdkServiceRecovery` | `true` | Permit starting the fixed stopped service |
| `AutoStartSdkService` | `true` | Run the preflight before new SDK work |
| `SdkServiceStartTimeoutSeconds` | `30` | Readback timeout, 1–120 seconds |

Disable automatic preflight separately to retain manual recovery. Disabling recovery prevents starts; already-running observations remain available. Settings are staged for the next shared-service restart. A maintenance hold blocks new recovery operations before service access; an existing request can still return its retained receipt.

## Windows behavior and receipts

Only the fixed local `LdSdkService` target is supported. Windows Service Control Manager owns its process; it is not an app-owned worker and app shutdown does not stop it. The caller needs service-query access and, for a start, `SERVICE_START`. Access denial and a disabled service produce actionable receipts. The app does not elevate, change service permissions/startup type, resume a paused service or control arbitrary services.

Requests serialize independently of project jobs, so a blocked SDK operation does not block manual recovery. Active and queued manual requests prevent shared-service shutdown. Existing `StartPending` is observed without another start request; other non-running/non-stopped states are refused.

Before sending a start, Core durably writes its intent under the configured state directory's `sdk-service-control/`. A successful result requires fresh `Running` readback. Lost responses, cancellation or timeouts retain uncertainty; unresolved dispatched starts block replacement starts when the service is stopped. Preserve receipts for operator review rather than deleting them to force a retry. History is bounded to 4,096 receipts of at most 16 KiB each; preserve and archive reviewed history during offline maintenance before reaching that limit.

The readback timeout starts after the Windows start call returns. Windows may itself block that call while handling service startup. A reported running service establishes Windows service state; native SDK project/connection readiness requires its own result. See Microsoft's [StartServiceW contract](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-startservicew) and [QueryServiceStatusEx contract](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-queryservicestatusex).

## Validation scope

Service state, serialization, idempotency, permission rejection, cancellation, timeout, identity checks and shutdown fences have synthetic driver tests. Compilation checks native bindings. Actual Windows-service recovery and subsequent native SDK work require separate authorized validation; synthetic tests do not start or stop an installed service.
