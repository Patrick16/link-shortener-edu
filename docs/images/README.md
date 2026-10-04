# Screenshots for the root README

The root [`README.md`](../../README.md) references these files. They are retina (2x) captures,
cropped to the relevant area, taken with the stack running and a load test in progress.

| File | What it shows |
|---|---|
| `architecture-map.png` | Sandbox (http://localhost:5174): the whole graph under load |
| `node-panel.png` | The `pgcat` node panel: pooling, read/write splitting, pool sizes, connections |
| `load-testing.png` | A k6 run in progress: endpoint steps, test data, VU / iteration charts |
| `test-report.png` | The report of a finished run (`redirect-api.resolve`, 0 failures) |
| `bottleneck-analysis.png` | Saved-run panel: bottleneck checklist with findings, slowest trace hops, automatic verdict |
| `compare-runs.png` | Comparison of two runs of the same scenario; differing settings highlighted |
| `learn-card.png` | The "Learn" card of RedirectApi: role, code, pitfalls |
| `product-ui.png` | Product frontend (http://localhost:5173): shortened links with click counts |
| `grafana-dashboard.png` | Grafana "ASP.NET Core" dashboard (needs `-Observability Full`) |
| `observability.png` | Aspire Dashboard trace: LinkApi → RabbitMQ → ShortenerService |

When retaking: use the same dark theme, avoid personal data (test accounts only), and prefer a
clean run (no failed requests) for the report shot.
