# ADR-14: Schema Visualizer

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/especificacao-fase-0.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Benchmark de 1.000 tabelas

## Opções

Nativo + MSAGL, WebView2 + React Flow, nativo + ELK

## Decisão

Nativo + MSAGL atrás de `IGraphLayout`

## Razão

Coerência de UI e teclado

## Trade-offs

MSAGL parado desde 2021

## Risco

Qualidade de layout

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
