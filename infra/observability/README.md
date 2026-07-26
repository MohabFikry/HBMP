# Observability — Mersal HBMP (Phase 11.3)

End-to-end traces, metrics, PHI-redacted logs, dashboards, SLOs + alerts (NFR §9 OBS, §8 REL).

## What's wired

- **Traces (Tempo).** Every service exports OTLP traces (`OTEL_EXPORTER_OTLP_ENDPOINT →
  tempo:4317`) with correlation IDs (Kong `correlation-id` plugin → services → audit). Gateway
  → service → audit trace propagation for investigation (NFR-080/084).
- **Metrics (Prometheus).** Every service now exposes **`/metrics`** via the OpenTelemetry
  Prometheus exporter — golden signals: `http_server_request_duration_seconds` (latency/traffic/
  errors via ASP.NET Core instrumentation) + `process_*`/runtime (saturation). Scraped by the
  `services` job in `infra/compose/config/prometheus.yml`.
- **Logs (Loki).** JSON logs, **PHI-redacted** (NFR-042/081) — redaction is enforced in the
  logging pipeline; redaction tests live with the audit/logging code.
- **Dashboards-as-code (Grafana).** Provisioned from `infra/compose/config/dashboards/`:
  - `golden-signals.json` — latency p95 (NFR-001 bar), traffic, error ratio, saturation, per `$job`.
  - `business-kpis.json` — approval TAT p95, pending/over-SLA approvals, consume throughput, no-show rate (from reporting-service).
- **Alerts.** `infra/compose/config/rules/slo-and-ops-alerts.yml` (Prometheus) → Alertmanager
  (`alertmanager.yml`): SLO burn-rate, latency/saturation, event-bus backlog, **failed-consume**,
  **approvals-SLA-breach**, **auth-anomaly**, **audit-chain-stalled**. Pages → on-call,
  security-labelled → security channel.

## Metric-emission rollout (fleet-wide)

The Prometheus exporter + runtime instrumentation is enabled in **all 17 application services'**
`Program.cs`:

```csharp
builder.Services.AddOpenTelemetry().ConfigureResource(r => r.AddService("<svc>-service"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddPrometheusExporter());
...
app.MapPrometheusScrapingEndpoint(); // /metrics — in-cluster scrape only (NetworkPolicies restrict access)
```

Business-KPI metrics referenced by `business-kpis.json` (e.g. `approvals_pending_over_sla`,
`orders_consume_committed_total`, `appointments_noshow_rate`) are emitted by the owning services
via named Meters/instruments; the reporting-service projects the aggregate KPIs. Where a service
does not yet publish a given business metric, its panel reads empty until that instrument is
added — the dashboard + alert wiring is in place so it lights up on first emission.

## Fire-a-synthetic-incident (prove routing)

Each alert should be proven to route to on-call by triggering a synthetic incident (e.g. push a
temporary high-5xx canary, or stuff the approvals queue in staging) and confirming the page
lands. Record which alert → which channel in the on-call handbook.

## SLOs

Per critical service: availability + latency SLOs with an error budget; multi-window burn-rate
alerts (`ErrorBudgetBurnFast` + the latency-breach rule) drive paging. Tune thresholds per
service from the perf baseline.
