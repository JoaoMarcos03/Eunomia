# ADR-12: Editor SQL

- **Estado:** Aceite (especificação da Fase 0)
- **Origem:** secção 29 da especificação (`docs/spec/specification.md`). Este ficheiro transcreve a linha da tabela, para cada decisão ter a sua ADR (secção 22)
- **Rever quando:** Spike

## Opções

AvaloniaEdit, editor próprio, Scintilla embebido

## Decisão

AvaloniaEdit + serviço de linguagem próprio

## Razão

Maturidade, MIT

## Trade-offs

Compatibilidade com Avalonia 12 por confirmar

## Risco

Fork necessário

## Resultado do spike (1 out 2026)

O AvaloniaEdit 12.0.0 funciona com Avalonia 12.1.3 sem fork: render, input, undo, colorizer alimentado pelo lexer do `Engine.Sql` e completion (5 testes headless). Há uma aresta contornada, `CompletionList.SelectItem` antes de o template estar aplicado. Ver `docs/spike/phase-1-report.md`, secção 4.4. A condição "rever no spike" fica cumprida.

## Requisitos afetados

A ligar aos IDs REQ/FAM/TR (convenção da secção 22) quando começar a fase do roadmap que implementa esta decisão. A secção 29 não faz esse mapeamento.
