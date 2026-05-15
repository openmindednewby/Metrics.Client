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
| `http_requests_total` | Counter | service, method, endpoint, status_code | Total HTTP requests processed |
| `http_request_duration_seconds` | Histogram | service, method, endpoint, status_code | Request duration in seconds |
| `http_requests_in_flight` | Gauge | service | Currently processing requests |
| `canary_http_requests_total` | Counter | service, method, status_code | HTTP requests tagged as in-cluster E2E canary traffic |

### Canary traffic

`canary_http_requests_total` is a **separate, low-cardinality counter** for
in-cluster E2E canary traffic (the `X-Canary-Run-Id` header + superUser JWT
flow from `Canary.AspNetCore`). It is incremented **only** when the request was
auth-validated as canary (`ICanaryRunContext.IsCanary == true`) — never on mere
header presence.

It is deliberately a separate series rather than a `canary` label on
`http_requests_total` (which would double that metric's series count), and
deliberately omits the `endpoint` label to keep cardinality minimal.

This powers a Grafana "Canary Activity" dashboard and lets SLO dashboards
default-exclude canary noise, e.g.:

```promql
# Real (non-canary) request rate per service
sum by (service) (rate(http_requests_total[5m]))
  - sum by (service) (rate(canary_http_requests_total[5m]))
```

Consuming services pick this up automatically — no wiring change is needed
beyond the existing `UsePrometheusMetrics()` call, provided `UseCanaryAuth()`
is registered after it (the standard pipeline order).

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

## Prometheus Scrape Config

```yaml
scrape_configs:
  - job_name: 'my-service'
    static_configs:
      - targets: ['my-service:8080']
    metrics_path: /metrics
```

## Design Decisions

- **Low cardinality**: Uses route templates (not raw paths) for endpoint labels
- **Health/metrics excluded**: Health check and metrics endpoints are not tracked to avoid noise
- **Lightweight**: No-op overhead when requests hit excluded paths
