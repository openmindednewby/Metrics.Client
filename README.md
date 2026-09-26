# Metrics.Client

Lightweight Prometheus metrics for ASP.NET Core services. Auto-collects HTTP request duration, request count, and active requests with low-cardinality labels.

## Quick Start

```csharp
using Metrics.Client.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add Prometheus metrics
builder.AddPrometheusMetrics(opts => opts.ServiceName = "MyService");

var app = builder.Build();

// Use Prometheus metrics middleware + /metrics endpoint
app.UsePrometheusMetrics();

await app.RunAsync();
```

## Metrics Collected

| Metric | Type | Labels | Description |
|--------|------|--------|-------------|
| `http_requests_total` | Counter | app, method, http_route, status_code | Total HTTP requests processed |
| `http_request_duration_seconds` | Histogram | app, method, http_route, status_code | Request duration in seconds |
| `http_requests_in_flight` | Gauge | app | Currently processing requests |
| `http_unmatched_requests_total` | Counter | app, method | Requests that matched no route |
| `canary_http_requests_total` | Counter | app, method, status_code | HTTP requests tagged as in-cluster E2E canary traffic |

## ⚠️ Breaking changes in 2.0.0

**1. Labels renamed: `service` → `app`, `endpoint` → `http_route`.**

Under Kubernetes service discovery, Prometheus attaches its own target labels named
`service` (the k8s Service) and `endpoint` (the port name). Ours collided, so
Prometheus silently renamed ours to `exported_service` / `exported_endpoint` — and a
dashboard grouping by the natural-looking `endpoint` was really bucketing everything
into the port number `"8080"`. No error; just a wrong, plausible graph. The new names
cannot collide with Prometheus target labels.

Update queries: `endpoint` (or `exported_endpoint`) → `http_route`;
`exported_service` → `app`. Note that `sum by (service)` keeps working — that
resolves to Prometheus' own target label, which this package does not touch.

**2. Unmatched requests no longer record the requested URL.**

`http_route` was previously the RAW request path whenever a request matched no route,
which made the label unbounded — every 404 URL minted a permanent series at ~14 series
each (counter + 11 histogram buckets + sum + count). It now records the literal
`"unmatched"`, and the rate is preserved separately in `http_unmatched_requests_total`.
Alert on that counter rather than on 404 paths:

```promql
# a client is calling a route that no longer exists
sum by (app) (rate(http_unmatched_requests_total[5m])) > 1
```

**3. prometheus-net's built-in `UseHttpMetrics()` is no longer registered.**

It was registered alongside this package's middleware, so every request was measured
twice — and since both write `http_request_duration_seconds` under incompatible label
schemas (`code,method,endpoint` vs ours), the result was ~2x the series *and* a silent
double-count in any query summing that metric. `http_requests_received_total` and
`http_requests_in_progress` are therefore no longer emitted; use `http_requests_total`
and `http_requests_in_flight`. To keep the built-in, call `app.UseHttpMetrics()`
yourself after `UsePrometheusMetrics()`.

### Canary traffic

`canary_http_requests_total` is a **separate, low-cardinality counter** for
in-cluster E2E canary traffic (the `X-Canary-Run-Id` header + superUser JWT
flow from `Canary.AspNetCore`). It is incremented **only** when the request was
auth-validated as canary (`ICanaryRunContext.IsCanary == true`) — never on mere
header presence.

It is deliberately a separate series rather than a `canary` label on
`http_requests_total` (which would double that metric's series count), and
deliberately omits the `http_route` label to keep cardinality minimal.

This powers a Grafana "Canary Activity" dashboard and lets SLO dashboards
default-exclude canary noise, e.g.:

```promql
# Real (non-canary) request rate per app
sum by (app) (rate(http_requests_total[5m]))
  - sum by (app) (rate(canary_http_requests_total[5m]))
```

Consuming services pick this up automatically — no wiring change is needed
beyond the existing `UsePrometheusMetrics()` call, provided `UseCanaryAuth()`
is registered after it (the standard pipeline order).

**The canary integration is optional.** `Canary.AspNetCore` wiring is *not*
required for this package to work: a service that calls `AddPrometheusMetrics()`
without `AddCanaryAuth()` records all HTTP metrics normally and simply never
emits `canary_http_requests_total`.

> **Fixed in 1.2.1.** In 1.2.0 and earlier this was not true. The middleware
> declared `InvokeAsync(HttpContext, ICanaryRunContext)`, and ASP.NET Core
> resolves such parameters with `GetRequiredService` *before* the method body
> runs — so a service wiring metrics but not canary threw
> `InvalidOperationException` on **every** request, including `/health/live` and
> `/metrics` (the skip logic for those paths sits after the resolution point).
> If you are on ≤ 1.2.0, either upgrade or make sure you also call
> `AddCanaryAuth()`.

## Configuration

Via `appsettings.json`:

```json
{
  "Metrics": {
    "ServiceName": "MyService",
    "Enabled": true,
    "MetricsPath": "/metrics"
  }
}
```

Or programmatically:

```csharp
builder.AddPrometheusMetrics(opts =>
{
    opts.ServiceName = "MyService";
    opts.Enabled = true;
});
```

### Turning metrics off (since 2.2.0)

`Enabled` and `MetricsPath` are honoured (before 2.2.0 they were bound but never read).

```bash
Metrics__Enabled=false          # no middleware, no scrape endpoint, no collectors, no Prometheus needed
Metrics__MetricsPath=/internal/metrics   # serve the scrape endpoint somewhere else
```

Keep both calls (`AddPrometheusMetrics` + `UsePrometheusMetrics`) in `Program.cs`; the switch
is config, not code. `UsePrometheusMetrics` without `AddPrometheusMetrics` keeps the old
defaults (enabled, `/metrics`).

## Prometheus Scrape Config

```yaml
scrape_configs:
  - job_name: 'my-service'
    static_configs:
      - targets: ['my-service:8080']
    metrics_path: /metrics
```

## Design Decisions

- **Bounded cardinality**: `http_route` is ALWAYS a route template or the literal
  `"unmatched"` — never a raw path. This is the invariant the package exists to hold;
  a raw-path fallback is what made the label unbounded before 2.0.0
- **Health/metrics excluded**: Health check and metrics endpoints are not tracked to avoid noise
- **Lightweight**: No-op overhead when requests hit excluded paths
