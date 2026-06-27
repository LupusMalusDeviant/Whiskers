# Agent / Chat

Rich chat widgets — lets an agent reply embed a **curated** live widget (a metric chart or a status card) by writing a token in its text, which the chat UI renders as a real Blazor component.

## Token protocol

The agent writes one of these tokens inline; everything else stays plain markdown:

| Token | Renders |
|---|---|
| `[[chart:server:<id>:cpu]]` / `:mem` | CPU or memory history chart for a server |
| `[[chart:container:<id>:cpu]]` / `:mem` | CPU or memory history chart for a container |
| `[[status:server:<id>]]` | Status card for a server |
| `[[status:container:<id>]]` | Status card for a container |

Metric aliases: `mem`, `memory`, `ram` → memory; anything else / omitted → CPU. Tokens are case-insensitive.

The grammar is **fixed and closed**: the `<id>` group cannot contain `:` or `]`, and anything that doesn't match a known shape (e.g. `[[bogus]]`, `[[chart:cluster:…]]`) stays plain text. The model can never inject arbitrary components or HTML this way — only this small set is ever rendered.

## Files

| File | Purpose |
|---|---|
| `ChatWidgetParser.cs` | `IChatWidgetParser` + `ChatWidgetParser` — splits a reply into ordered text/widget [`ChatSegment`](../../../Models/Agent/ChatWidget.cs)s, extracting the tokens. Pure + unit-tested; registered interface-first as a singleton. |

## Related

- Model: [`../../../Models/Agent/ChatWidget.cs`](../../../Models/Agent/ChatWidget.cs) (`ChatWidgetSpec`, `ChatSegment`, kinds/targets/metrics)
- Renderer component + chat wiring: [`Agent.razor`](../../../Components/Pages/Agent.razor) (uses the parser + an inline widget component)
- Metric data source: [`../../Metrics/IMetricsQueryService.cs`](../../Metrics/IMetricsQueryService.cs)
